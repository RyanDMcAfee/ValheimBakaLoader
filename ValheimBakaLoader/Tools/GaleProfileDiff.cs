using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Tools
{
    /// <summary>Where one mod stands between a Gale profile and the server.</summary>
    public enum GaleDiffKind
    {
        /// <summary>In the profile, ticked there, not on the server.</summary>
        Add,

        /// <summary>In the profile, ticked there, on the server at a different version (higher or lower).</summary>
        Update,

        /// <summary>On the server at the profile's version. Nothing to do.</summary>
        Same,

        /// <summary>Would be an add or an update, but the host said no to it earlier.</summary>
        Unticked,

        /// <summary>The profile has it switched off, so it is skipped whatever the server has.</summary>
        Disabled,

        /// <summary>On the server only. Listed for information and never removed.</summary>
        NotInProfile,

        /// <summary>
        /// The modding framework itself (the BepInEx pack, or HookGenPatcher where the server
        /// keeps it among the patchers). Every Valheim profile carries the pack, but the server
        /// keeps it beside BepInEx, not as a mod, and the BepInEx row owns it. Listed for
        /// information, never offered for install and never counted as work.
        /// </summary>
        Framework,
    }

    /// <summary>One row of the comparison.</summary>
    public sealed class GaleDiffRow
    {
        /// <summary>"Namespace-Name" as the profile spells it, or the server's folder name for a server-only mod.</summary>
        public string Name { get; init; }

        public GaleDiffKind Kind { get; init; }

        /// <summary>The version the profile pins, or null for a server-only mod.</summary>
        public string ProfileVersion { get; init; }

        /// <summary>The version on the server, or null when the mod is not installed.</summary>
        public string ServerVersion { get; init; }

        /// <summary>True when the server has this mod at all.</summary>
        public bool Installed => ServerVersion != null;

        /// <summary>
        /// For an <see cref="GaleDiffKind.Unticked"/> row, what it would have been had the host
        /// not said no: <see cref="GaleDiffKind.Add"/> or <see cref="GaleDiffKind.Update"/>.
        /// The approval dialog lists unticked rows too, with the tick off, and needs to know
        /// which they are. Null on every other row.
        /// </summary>
        public GaleDiffKind? UntickedFrom { get; init; }

        /// <summary>
        /// True for an update that takes the server to an OLDER version than it has. The
        /// server follows the profile, so it is still an update, but the dialog can say so.
        /// </summary>
        public bool IsDowngrade { get; init; }
    }

    /// <summary>The whole comparison, rows grouped by kind, and what the profile carried that could not be read.</summary>
    public sealed class GaleProfileDiffResult
    {
        public IReadOnlyList<GaleDiffRow> Rows { get; init; } = Array.Empty<GaleDiffRow>();

        /// <summary>The profile's updatedAt this comparison was made against.</summary>
        public string UpdatedAt { get; init; }

        /// <summary>
        /// How many manifest lines were skipped: no readable name, a name that is not
        /// "Namespace-Name", no readable version, no readable tick, or a second line for a
        /// name already read.
        /// </summary>
        public int Skipped { get; init; }

        public IEnumerable<GaleDiffRow> Of(GaleDiffKind kind) => Rows.Where(r => r.Kind == kind);

        public int AddCount => Of(GaleDiffKind.Add).Count();

        public int UpdateCount => Of(GaleDiffKind.Update).Count();

        /// <summary>True when there is something to install: at least one add or update the host has not unticked.</summary>
        public bool HasWork => AddCount + UpdateCount > 0;
    }

    /// <summary>
    /// The comparison between a Gale profile's manifest and the mods a server has installed.
    /// Pure: no disk, no network, no clock. The installed side is what
    /// <see cref="ModScanner"/> reports for each folder (its "Namespace-Name" and the
    /// manifest.json version), the same reading the Mods hall already makes.
    /// <para>
    /// Names compare without regard to case, because Thunderstore treats them that way and a
    /// folder made by hand may not match the case the profile uses. Versions compare by
    /// <see cref="SemVer"/>, so "1.2" on the server and 1.2.0 in the profile are the same.
    /// A server version nobody can read ("unknown", what a folder with no manifest.json gets)
    /// is never the same as a readable one, so that row is an update: the profile pins a
    /// version and the server cannot show it has it.
    /// </para>
    /// <para>
    /// A manifest line the reader could not make sense of is skipped and counted, never
    /// guessed at, a server-only mod is listed and never removed, and the framework is listed
    /// and left to the BepInEx row. Nothing in this class
    /// decides to remove anything; it has no row kind that means removal.
    /// </para>
    /// </summary>
    public static class GaleProfileDiff
    {
        // Thunderstore spells a namespace and a name in letters, digits and underscores, and
        // joins them with the one dash.
        private static readonly Regex FullName = new(@"^[A-Za-z0-9_]+-[A-Za-z0-9_]+\z", RegexOptions.CultureInvariant);

        /// <summary>The status line when the profile is for another game.</summary>
        public const string OtherGameSentence = "mods.gale.check.other_game";

        /// <summary>The longest game name put in the status line; a longer one is cut.</summary>
        internal const int MaxGameLength = 64;

        /// <summary>
        /// True when a profile's community is Valheim, in any case. A blank or missing one
        /// counts as Valheim, because a profile made before Gale wrote the field has none.
        /// </summary>
        public static bool IsValheim(string community) =>
            string.IsNullOrWhiteSpace(community)
            || string.Equals(community.Trim(), "valheim", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The check's answer as the page gets it. A readable profile for another game is turned
        /// into a refusal with <see cref="OtherGameSentence"/>, so it is never compared and never
        /// raises the row: its mods are not Valheim mods and installing them would break the
        /// server. Every other result is handed back as it came.
        /// </summary>
        public static GaleCheckResult RefuseOtherGame(GaleCheckResult result)
        {
            if (result?.Ok != true || IsValheim(result.Meta?.Community)) return result;

            var game = result.Meta.Community.Trim();
            if (game.Length > MaxGameLength) game = game.Substring(0, MaxGameLength);
            return new GaleCheckResult
            {
                Outcome = GaleCheckOutcome.OtherGame,
                SentenceId = OtherGameSentence,
                StatusCode = result.StatusCode,
                Game = game,
            };
        }

        /// <summary>True when a name is "Namespace-Name" as Thunderstore spells one.</summary>
        public static bool IsFullName(string name) => name != null && FullName.IsMatch(name);

        /// <summary>
        /// True when a profile line is the framework rather than a mod. The mod scan never
        /// lists the BepInEx pack (<see cref="ModScanner.IsPluginsFrameworkFolder"/>), so it
        /// would read as an add on every check, and installing it as a mod would put it in the
        /// plugins folder, which the BepInEx row then moves out again. HookGenPatcher is the
        /// same when the scan has no copy of it: the server may keep it among the patchers,
        /// which the scan skips (<see cref="ModScanner.IsProtectedPatcher"/>). When the scan
        /// does list it, a host installed it into plugins as an ordinary mod, and it compares
        /// like one.
        /// </summary>
        public static bool IsFramework(string name, string serverVersion) =>
            ModScanner.IsPluginsFrameworkFolder(name)
            || (serverVersion == null && ModScanner.IsProtectedPatcher(name));

        /// <summary>The comparison against the mods a <see cref="ModScanner"/> scan returned.</summary>
        public static GaleProfileDiffResult Compare(
            GaleProfileMeta meta, IEnumerable<InstalledMod> installed, IEnumerable<string> unticked)
        {
            // A mod the host switched off (mods.disable) is still installed at its version: its
            // manifest is read like any other, so it compares as Same and the follow never
            // reinstalls it, which would also be the follow quietly overruling the host.
            var pairs = (installed ?? Enumerable.Empty<InstalledMod>())
                .Where(m => m != null)
                .Select(m => (Name: string.IsNullOrWhiteSpace(m.Author) ? m.ModName : m.FullName, Version: m.InstalledVersion));
            return Compare(meta, pairs, unticked);
        }

        /// <summary>
        /// The comparison itself, with the server's mods as name and version pairs.
        /// </summary>
        public static GaleProfileDiffResult Compare(
            GaleProfileMeta meta,
            IEnumerable<(string Name, string Version)> installed,
            IEnumerable<string> unticked)
        {
            var server = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var serverOrder = new List<string>();
            foreach (var (name, version) in installed ?? Enumerable.Empty<(string, string)>())
            {
                var n = name?.Trim();
                if (string.IsNullOrEmpty(n) || server.ContainsKey(n)) continue;
                // A folder with no version at all still has a version, it just cannot be read.
                server[n] = string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim();
                serverOrder.Add(n);
            }

            var saidNo = new HashSet<string>(
                (unticked ?? Enumerable.Empty<string>()).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var rows = new List<GaleDiffRow>();
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skipped = 0;

            foreach (var mod in meta?.Mods ?? Array.Empty<GaleProfileMod>())
            {
                var name = mod?.Name?.Trim();
                if (!IsFullName(name))
                {
                    skipped++;
                    continue;
                }

                if (!named.Add(name))
                {
                    // The same mod twice in one manifest. The first line stands.
                    skipped++;
                    continue;
                }

                if (mod.Enabled == null || mod.Version == null || !SemVer.IsReadable(mod.Version))
                {
                    // The name is real, so a server copy of it is not "not in profile"; but there
                    // is no version to pin or no tick to follow, so nothing is offered for it.
                    skipped++;
                    continue;
                }

                server.TryGetValue(name, out var serverVersion);

                if (IsFramework(name, serverVersion))
                {
                    rows.Add(new GaleDiffRow
                    {
                        Name = name, Kind = GaleDiffKind.Framework, ProfileVersion = mod.Version, ServerVersion = serverVersion,
                    });
                    continue;
                }

                if (mod.Enabled == false)
                {
                    rows.Add(new GaleDiffRow
                    {
                        Name = name, Kind = GaleDiffKind.Disabled, ProfileVersion = mod.Version, ServerVersion = serverVersion,
                    });
                    continue;
                }

                GaleDiffKind kind;
                var downgrade = false;
                if (serverVersion == null)
                {
                    kind = GaleDiffKind.Add;
                }
                else if (SemVer.IsReadable(serverVersion) && SemVer.Compare(mod.Version, serverVersion) == 0)
                {
                    kind = GaleDiffKind.Same;
                }
                else
                {
                    kind = GaleDiffKind.Update;
                    downgrade = SemVer.IsReadable(serverVersion) && SemVer.Compare(mod.Version, serverVersion) < 0;
                }

                if (kind != GaleDiffKind.Same && saidNo.Contains(name))
                {
                    rows.Add(new GaleDiffRow
                    {
                        Name = name, Kind = GaleDiffKind.Unticked, UntickedFrom = kind,
                        ProfileVersion = mod.Version, ServerVersion = serverVersion, IsDowngrade = downgrade,
                    });
                    continue;
                }

                rows.Add(new GaleDiffRow
                {
                    Name = name, Kind = kind, ProfileVersion = mod.Version, ServerVersion = serverVersion, IsDowngrade = downgrade,
                });
            }

            foreach (var name in serverOrder)
            {
                if (named.Contains(name)) continue;
                rows.Add(new GaleDiffRow { Name = name, Kind = GaleDiffKind.NotInProfile, ServerVersion = server[name] });
            }

            return new GaleProfileDiffResult { Rows = rows, UpdatedAt = meta?.UpdatedAt, Skipped = skipped };
        }

        /// <summary>
        /// Whether the condition bar gets its "Gale profile changed" row: only when there is
        /// something to install, and only when the profile has moved since the host last
        /// approved or dismissed it. A profile that changed in a way that leaves this server
        /// with nothing to do (a mod it already has, a mod the host unticked) raises no row,
        /// and a dismissal holds until the profile's updatedAt moves again.
        /// </summary>
        public static bool IsDue(GaleProfileDiffResult diff, string lastUpdatedAt)
        {
            if (diff == null || !diff.HasWork) return false;
            return !string.Equals(diff.UpdatedAt?.Trim(), lastUpdatedAt?.Trim(), StringComparison.Ordinal);
        }
    }
}
