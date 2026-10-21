using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Tools
{
    /// <summary>One entry of a manifest's dependencies, read: who, and the lowest version asked for.</summary>
    public sealed class DependencyRequirement
    {
        public string FullName { get; init; }

        /// <summary>Null when the entry carried no version.</summary>
        public string MinVersion { get; init; }
    }

    /// <summary>
    /// Works out what a mod needs that a realm cannot give it. Pure: it reads the list it is
    /// handed and the mods it is handed, and the only thing it ever asks of the outside world is
    /// the callback a caller passes in for a dependency that is not on disk.
    /// <para>
    /// Why this exists: a mod was installed by link without the core library it is built on.
    /// BepInEx wrote "Could not load ... missing dependencies" into its own log and the mod
    /// simply never loaded, and nothing in BakaLoader said a word, because nothing in it ever
    /// read a manifest's dependencies forwards. The only walk there was went the other way
    /// (who depends on this mod, for a removal).
    /// </para>
    /// <para>
    /// The rules. A dependency matches an installed mod by its "Owner-Name", the way the plugin
    /// folder is named. One that is there but older than the version asked for is reported as
    /// older, not missing, because what the host does about it is an update. One that is there
    /// and switched off (every .dll renamed .dll.old) is reported as off, because the answer
    /// is Switch on, not a download. A copy with no version on it (a hand copy with no
    /// manifest) is taken as good enough: nobody can say it is too old. The BepInEx pack every
    /// mod lists is counted as there whenever the caller says BepInEx is installed or BakaLoader
    /// looks after it, because the pack never sits in the plugins folder a match would look in.
    /// </para>
    /// </summary>
    public static class ModDependencies
    {
        public const string Missing = "missing";
        public const string Older = "older";
        public const string Off = "off";

        /// <summary>
        /// The most dependencies a single walk will ask the site about. A real chain is two or
        /// three deep; this only stops a listing that is wrong somewhere from turning one
        /// install into a hundred requests.
        /// </summary>
        public const int DefaultFetchBudget = 24;

        /// <summary>
        /// Reads "Owner-Name-1.2.3". A namespace and a package name carry no hyphen, so the last
        /// hyphen splits the version off, and only when what follows it reads as a version: an
        /// entry with no version at all ("Owner-Name") keeps its whole text as the name. An
        /// entry with no hyphen in what is left is not a package name and is refused.
        /// </summary>
        public static bool TryParse(string entry, out DependencyRequirement requirement)
        {
            requirement = null;
            if (string.IsNullOrWhiteSpace(entry)) return false;

            var trimmed = entry.Trim();
            string name = trimmed, version = null;
            var last = trimmed.LastIndexOf('-');
            if (last > 0 && last < trimmed.Length - 1)
            {
                var tail = trimmed.Substring(last + 1);
                if (char.IsDigit(tail[0]) && SemVer.IsReadable(tail))
                {
                    name = trimmed.Substring(0, last);
                    version = tail;
                }
            }

            var hyphen = name.IndexOf('-');
            if (hyphen <= 0 || hyphen >= name.Length - 1) return false;

            requirement = new DependencyRequirement { FullName = name, MinVersion = version };
            return true;
        }

        /// <summary>
        /// Every readable entry, one per package. Two entries for the same package keep the
        /// higher minimum, since both of them have to be met. In the order first seen; never null.
        /// </summary>
        public static IReadOnlyList<DependencyRequirement> Parse(IEnumerable<string> declared)
        {
            var order = new List<string>();
            var byName = new Dictionary<string, DependencyRequirement>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in declared ?? Enumerable.Empty<string>())
            {
                if (!TryParse(entry, out var r)) continue;
                if (byName.TryGetValue(r.FullName, out var held))
                {
                    if (Higher(r.MinVersion, held.MinVersion))
                        byName[r.FullName] = new DependencyRequirement { FullName = held.FullName, MinVersion = r.MinVersion };
                    continue;
                }
                byName[r.FullName] = r;
                order.Add(r.FullName);
            }
            return order.Select(n => byName[n]).ToList();
        }

        /// <summary>True for the BepInEx pack itself, which no mod ever finds in the plugins folder.</summary>
        public static bool IsBepInExPack(string fullName) =>
            string.Equals(fullName?.Trim(), BepInExService.DefaultPackage, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// One level only, from what is on disk: what this list asks for that the installed mods
        /// do not give. The Mods table reads this on every scan, so it asks no site and walks no
        /// further than the list itself. A dependency's own needs are that dependency's row.
        /// </summary>
        public static IReadOnlyList<DependencyNeed> Check(
            IEnumerable<string> declared,
            IEnumerable<InstalledMod> installed,
            bool bepInExSatisfied,
            string requiredBy = null)
        {
            var mods = IndexInstalled(installed);
            var result = new List<DependencyNeed>();
            foreach (var r in Parse(declared))
            {
                if (string.Equals(r.FullName, requiredBy, StringComparison.OrdinalIgnoreCase)) continue;
                var need = Judge(r, mods, bepInExSatisfied, requiredBy);
                if (need != null) result.Add(need);
            }
            return result;
        }

        /// <summary>
        /// The whole chain, for a mod that has just gone in: what it asks for, what those ask for,
        /// and so on, with a visited set so a cycle or a shared core is walked once. A dependency
        /// that is on disk is followed through its own manifest; one that is not is followed
        /// through what <paramref name="fetchLatestDependencies"/> says its newest release needs,
        /// since the newest release is what an install of it would fetch. A fetch that fails or
        /// answers nothing ends that branch and never the walk. Ordered as found, nearest first;
        /// never null; never throws.
        /// </summary>
        public static async Task<IReadOnlyList<DependencyNeed>> ResolveAsync(
            string rootFullName,
            IEnumerable<string> declared,
            IEnumerable<InstalledMod> installed,
            bool bepInExSatisfied,
            Func<string, Task<IEnumerable<string>>> fetchLatestDependencies,
            int fetchBudget = DefaultFetchBudget)
        {
            var root = rootFullName?.Trim();
            var mods = IndexInstalled(installed);
            var wanted = new Dictionary<string, DependencyRequirement>(StringComparer.OrdinalIgnoreCase);
            var askedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(root)) visited.Add(root);

            var queue = new Queue<(string By, IEnumerable<string> List)>();
            queue.Enqueue((root, declared));
            var fetches = 0;

            while (queue.Count > 0)
            {
                var (by, list) = queue.Dequeue();
                foreach (var r in Parse(list))
                {
                    if (IsBepInExPack(r.FullName) && bepInExSatisfied) continue;
                    if (string.Equals(r.FullName, root, StringComparison.OrdinalIgnoreCase)) continue;

                    if (wanted.TryGetValue(r.FullName, out var held))
                    {
                        // Asked for again further down the chain, maybe for a newer build.
                        if (Higher(r.MinVersion, held.MinVersion))
                            wanted[r.FullName] = new DependencyRequirement { FullName = held.FullName, MinVersion = r.MinVersion };
                        continue;
                    }

                    wanted[r.FullName] = r;
                    askedBy[r.FullName] = by;
                    order.Add(r.FullName);

                    if (!visited.Add(r.FullName)) continue;

                    if (mods.TryGetValue(r.FullName, out var onDisk))
                    {
                        queue.Enqueue((onDisk.FullName, onDisk.DeclaredDependencies));
                    }
                    else if (fetchLatestDependencies != null && fetches < fetchBudget && !IsBepInExPack(r.FullName))
                    {
                        fetches++;
                        List<string> next = null;
                        try { next = (await fetchLatestDependencies(r.FullName))?.ToList(); }
                        catch { next = null; }
                        if (next != null) queue.Enqueue((r.FullName, next));
                    }
                }
            }

            var result = new List<DependencyNeed>();
            foreach (var name in order)
            {
                var need = Judge(wanted[name], mods, bepInExSatisfied, askedBy[name]);
                if (need != null) result.Add(need);
            }
            return result;
        }

        /// <summary>What one requirement comes to against the mods on disk, or null when it is met.</summary>
        private static DependencyNeed Judge(DependencyRequirement r, IReadOnlyDictionary<string, InstalledMod> mods,
            bool bepInExSatisfied, string requiredBy)
        {
            if (IsBepInExPack(r.FullName) && bepInExSatisfied) return null;

            if (!mods.TryGetValue(r.FullName, out var mod))
                return new DependencyNeed { FullName = r.FullName, MinVersion = r.MinVersion, State = Missing, RequiredBy = requiredBy };

            if (mod.Disabled)
                return new DependencyNeed
                {
                    FullName = mod.FullName, MinVersion = r.MinVersion, State = Off,
                    InstalledVersion = mod.InstalledVersion, RequiredBy = requiredBy,
                };

            // Only a version that was actually read can be too old. "unknown" is what a folder
            // with no manifest gets, and nobody can say that one falls short.
            if (r.MinVersion != null && SemVer.IsReadable(mod.InstalledVersion)
                && SemVer.Compare(mod.InstalledVersion, r.MinVersion) < 0)
                return new DependencyNeed
                {
                    FullName = mod.FullName, MinVersion = r.MinVersion, State = Older,
                    InstalledVersion = mod.InstalledVersion, RequiredBy = requiredBy,
                };

            return null;
        }

        /// <summary>
        /// The installed mods by "Owner-Name". A mod with a plugins folder and a patcher folder
        /// is one row already; should two rows share a name anyway, the one with a plugins
        /// folder is the one kept.
        /// </summary>
        private static IReadOnlyDictionary<string, InstalledMod> IndexInstalled(IEnumerable<InstalledMod> installed)
        {
            var map = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in installed ?? Enumerable.Empty<InstalledMod>())
            {
                if (mod == null || string.IsNullOrWhiteSpace(mod.Author) || string.IsNullOrWhiteSpace(mod.ModName)) continue;
                if (map.TryGetValue(mod.FullName, out var held) && !(held.IsPatcher && !mod.IsPatcher)) continue;
                map[mod.FullName] = mod;
            }
            return map;
        }

        private static bool Higher(string candidate, string held) =>
            candidate != null && (held == null || SemVer.Compare(candidate, held) > 0);
    }
}
