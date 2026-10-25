using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Forms
{
    /// <summary>
    /// Merge-back (1.3.0): a realm takes back, from one of its own test copies, the mods and
    /// config files the host proved there. The world never travels.
    /// <para>
    /// The realm follows the copy the way it would follow a Gale profile: testcopy.follow sets
    /// FollowTestCopy on the SOURCE realm, and from then on the Gale box, gale.check, the dialog,
    /// gale.configs and the condition bar row read the copy's folders on this PC through
    /// <see cref="TestCopyGaleFeed"/> in place of Gale's service (<see cref="GaleFeedFor"/>).
    /// The comparison, the configs apply and the dialog are Gale's own and unchanged.
    /// </para>
    /// <para>
    /// The one thing Gale's road cannot do is the mods themselves: a Gale install downloads a
    /// pinned build from Thunderstore, and a mod on a test copy may be one nobody published.
    /// testcopy.pullMods copies the named mod folders from the copy into the source instead.
    /// It lives here, not in the Gale files, because it replaces a folder: the source's own
    /// folder of that mod is moved aside into BepInEx/.bakaloader-mod-backups first (the same
    /// place an install backs up to), never thrown away, and a mod that was not named is never
    /// touched. A mod switched off on the copy arrives switched off, because the switch is the
    /// files' names. Held to the realm on screen and to a stopped server, the same holds the
    /// mod add has.
    /// </para>
    /// </summary>
    public partial class BlendWindow
    {
        /// <summary>Where the source's own folder of a mod is moved before the copy's goes in, under its BepInEx folder.</summary>
        internal const string TestCopyModBackupFolder = ".bakaloader-mod-backups";

        private void RegisterTestCopyFeedRpcs()
        {
            // Follow one of this realm's test copies, or stop (copy null or blank). Answers with
            // the Gale box's state, the one gale.get gives. A name that is not a test copy of
            // this realm is refused before anything is stored.
            RegisterRpc("testcopy.follow", p => Task.FromResult(TestCopyFollow(GaleProfileOf(p), p?.Value<string>("copy"))));

            // Copies the named mods from the followed test copy into this realm.
            RegisterRpc("testcopy.pullMods", p => Task.Run(() => TestCopyPullMods(GaleProfileOf(p), p?["mods"])));
        }

        /// <summary>
        /// What the Gale RPCs read for a realm: the test copy it follows, read off this PC, or
        /// Gale's service. A copy that cannot feed it (gone, no longer its copy, on the same
        /// install) is a feed that answers every read with the sentence saying so.
        /// </summary>
        private IGaleSyncClient GaleFeedFor(string profile, ServerPreferences prefs)
        {
            if (prefs == null || !prefs.FollowsTestCopy()) return GaleClient;
            var copy = ResolveTestCopyFeed(prefs);
            return copy.Refusal != null
                ? TestCopyGaleFeed.Refused(copy.Name, copy.Refusal)
                : new TestCopyGaleFeed(copy.Name, copy.PluginsDir, copy.ConfigDir, ModScanner);
        }

        /// <summary>One followed test copy, as checked: its name, its folders, or why it cannot feed the realm.</summary>
        private readonly record struct TestCopyFeedSource(string Name, string PluginsDir, string ConfigDir, string Refusal);

        /// <summary>
        /// The test copy a realm follows, checked the way a follow is set: still a realm, still a
        /// test copy of this realm with its source not lost, and on an install of its own (a copy
        /// that shares this realm's plugins folder has nothing to give back, and reading it would
        /// compare the folder with itself). The folders are asked for only once the copy is known
        /// to be a realm, because an unknown name would answer with the app's base install.
        /// </summary>
        private TestCopyFeedSource ResolveTestCopyFeed(ServerPreferences source)
        {
            var name = source?.FollowTestCopy?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return new TestCopyFeedSource(name, null, null, TestCopyGaleFeed.GoneSentence);

            var copy = FindRealm(name);
            if (copy == null) return new TestCopyFeedSource(name, null, null, TestCopyGaleFeed.GoneSentence);
            if (!IsTestCopyOf(copy, source))
                return new TestCopyFeedSource(copy.ProfileName, null, null, TestCopyGaleFeed.NotACopySentence);

            string pluginsDir, configDir, sourcePlugins;
            try
            {
                pluginsDir = GetPluginsDirectoryFor(copy.ProfileName);
                configDir = GetConfigDirectoryFor(copy.ProfileName);
                sourcePlugins = GetPluginsDirectoryFor(source.ProfileName);
            }
            catch (Exception)
            {
                return new TestCopyFeedSource(copy.ProfileName, null, null, TestCopyGaleFeed.UnreadableSentence);
            }

            if (string.IsNullOrWhiteSpace(pluginsDir))
                return new TestCopyFeedSource(copy.ProfileName, null, null, TestCopyGaleFeed.UnreadableSentence);
            if (SameFolder(pluginsDir, sourcePlugins))
                return new TestCopyFeedSource(copy.ProfileName, null, null, TestCopyGaleFeed.SharesInstallSentence);

            return new TestCopyFeedSource(copy.ProfileName, pluginsDir, configDir, null);
        }

        /// <summary>A saved realm by its profile name in any case, the way a test copy names its source, or null.</summary>
        private ServerPreferences FindRealm(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                return ServerPrefsProvider.LoadPreferences()
                    .FirstOrDefault(x => string.Equals(x.ProfileName?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e)
            {
                AppLogger?.Debug("Could not read the realms to find {0}: {1}", name, e.Message);
                return null;
            }
        }

        /// <summary>True when <paramref name="copy"/> is a test copy of <paramref name="source"/> that has not lost it.</summary>
        private static bool IsTestCopyOf(ServerPreferences copy, ServerPreferences source) =>
            copy != null && source != null
            && !copy.TestSourceGone
            && !string.Equals(copy.ProfileName, source.ProfileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(copy.TestCopyOf?.Trim(), source.ProfileName?.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// testcopy.follow. Sets or clears FollowTestCopy on <paramref name="profile"/> and answers
        /// with its Gale state. Following another copy, or none, starts the copy's approval and
        /// unticks afresh; a Gale profile id the realm has is kept, and is what it follows again
        /// once the copy is let go of.
        /// </summary>
        internal object TestCopyFollow(string profile, string copyName)
        {
            var prefs = LoadGalePrefs(profile)
                ?? throw new HostFacingException("testcopy.follow.noSuchRealm",
                    $"There is no server named '{profile}'.", ("profile", profile));

            copyName = copyName?.Trim();
            string follow = null;
            if (!string.IsNullOrEmpty(copyName))
            {
                var copy = FindRealm(copyName);
                if (!IsTestCopyOf(copy, prefs))
                    throw new HostFacingException("testcopy.follow.notACopy",
                        $"'{copyName}' is not a test copy of '{prefs.ProfileName}', so it cannot hand anything back to it.",
                        ("copy", copyName), ("profile", prefs.ProfileName));
                follow = copy.ProfileName;
            }

            if (!string.Equals(prefs.FollowTestCopy?.Trim(), follow, StringComparison.Ordinal))
            {
                prefs.ForgetTestCopyFollow();
                prefs.FollowTestCopy = follow;
                ServerPrefsProvider.SavePreferences(prefs);
                GaleMemos.TryRemove(profile, out _);
                GaleConfigMemos.TryRemove(profile, out _);
                Logger.Information(follow == null
                    ? "'{0}' no longer takes its mods from a test copy."
                    : "'{0}' now takes its mods and configs from its test copy '{1}'.", prefs.ProfileName, follow);
            }

            return GaleState(profile);
        }

        /// <summary>
        /// testcopy.pullMods. Copies each named mod's folders (plugins, and patchers when it has
        /// them) from the followed test copy into <paramref name="profile"/>, replacing that
        /// realm's own folders of the same mod, which are moved aside first. Every check is made
        /// before anything is written; a mod that fails part way stops the walk there and puts
        /// back what that one mod moved.
        /// </summary>
        internal object TestCopyPullMods(string profile, JToken mods)
        {
            object Refused(string reason, string error, Dictionary<string, object> errorParams = null) => new
            {
                ok = false, pulled = Array.Empty<string>(), backup = (string)null,
                Reason = reason, Error = error, ErrorParams = errorParams, state = GaleState(profile),
            };

            if (!string.Equals(profile, ActiveProfileName, StringComparison.OrdinalIgnoreCase))
                return Refused("realmChanged", "The server on screen changed, so nothing was copied.");
            if (_modUpdateInProgress)
                return Refused("busy", "A mod update is already in progress. Try again in a moment.");

            var prefs = LoadGalePrefs(profile);
            if (prefs == null || !prefs.FollowsTestCopy())
                return Refused("notFollowing", "This server does not follow a test copy, so nothing was copied.");

            var copy = ResolveTestCopyFeed(prefs);
            var copyParams = new Dictionary<string, object> { ["copy"] = copy.Name };
            if (copy.Refusal == TestCopyGaleFeed.GoneSentence)
                return Refused("copyGone", $"The test copy '{copy.Name}' is not there any more, so nothing was copied.", copyParams);
            if (copy.Refusal == TestCopyGaleFeed.NotACopySentence)
                return Refused("notACopy", $"'{copy.Name}' is not a test copy of this server any more, so nothing was copied.", copyParams);
            if (copy.Refusal == TestCopyGaleFeed.SharesInstallSentence)
                return Refused("sharesInstall", $"The test copy '{copy.Name}' runs on this server's own install, so there is nothing to copy back.", copyParams);
            if (copy.Refusal != null)
                return Refused("copyUnreadable", $"The mods of the test copy '{copy.Name}' could not be read, so nothing was copied.", copyParams);

            var pluginsDir = GetPluginsDirectoryFor(profile);
            if (string.IsNullOrWhiteSpace(pluginsDir))
                return Refused("noServerPath", "BepInEx plugins folder not found. Set a valid server .exe path first.");
            var sep = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            var bepinexDir = Path.GetDirectoryName(Path.GetFullPath(pluginsDir).TrimEnd(sep));
            var patchersDir = Path.Combine(bepinexDir!, "patchers");

            var holding = RunningSessionName(new[] { pluginsDir, patchersDir });
            if (holding != null)
                return Refused("serverRunning", $"Stop the server '{holding}' first. Nothing was copied.",
                    new Dictionary<string, object> { ["name"] = holding });

            var names = ((mods as JArray)?.OfType<JValue>() ?? Enumerable.Empty<JValue>())
                .Where(v => v.Type == JTokenType.String)
                .Select(v => ((string)v)?.Trim())
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<InstalledMod> onCopy, here;
            try
            {
                onCopy = ModScanner.ScanPlugins(copy.PluginsDir) ?? new List<InstalledMod>();
                here = Directory.Exists(pluginsDir) ? ModScanner.ScanPlugins(pluginsDir) ?? new List<InstalledMod>() : new List<InstalledMod>();
            }
            catch (Exception e)
            {
                AppLogger?.Debug("Could not read the mods for a pull from {0}: {1}", copy.Name, e.Message);
                return Refused("copyUnreadable", $"The mods of the test copy '{copy.Name}' could not be read, so nothing was copied.", copyParams);
            }

            // Every name is found on the copy before anything moves, so a wrong one costs nothing.
            var picked = new List<InstalledMod>();
            foreach (var name in names)
            {
                var found = onCopy.FirstOrDefault(m => m != null && string.Equals(TestCopyGaleFeed.NameOf(m), name, StringComparison.OrdinalIgnoreCase));
                if (found == null)
                    return Refused("notInCopy", $"{name} is not on the test copy '{copy.Name}', so nothing was copied.",
                        new Dictionary<string, object> { ["mod"] = name, ["copy"] = copy.Name });
                picked.Add(found);
            }

            var pulled = new List<string>();
            if (picked.Count == 0)
                return new { ok = true, pulled, backup = (string)null, Reason = (string)null, Error = (string)null, ErrorParams = (object)null, state = GaleState(profile) };

            string backups = null;
            _modUpdateInProgress = true;
            try
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                foreach (var mod in picked)
                {
                    var name = TestCopyGaleFeed.NameOf(mod);
                    var mine = here.FirstOrDefault(m => m != null && string.Equals(TestCopyGaleFeed.NameOf(m), name, StringComparison.OrdinalIgnoreCase));
                    try
                    {
                        var leaf = PullOneMod(mod, mine, bepinexDir, stamp);
                        backups = $"BepInEx/{TestCopyModBackupFolder}";
                        pulled.Add(name);
                        AppLogger?.Information("{Line:l}",
                            $"[Test copy] Copied {name} from the test copy {copy.Name} into {profile}. What {profile} had of it is in BepInEx/{TestCopyModBackupFolder}/{leaf}/{stamp}.");
                    }
                    catch (Exception e)
                    {
                        AppLogger?.Information("{Line:l}",
                            $"[Test copy] Could not copy {name} from the test copy {copy.Name}: {e.Message} Stopped there, with {pulled.Count} copied before it.");
                        return new
                        {
                            ok = false, pulled, backup = backups,
                            Reason = "copyFailed", Error = $"Could not copy {name}: {e.Message}",
                            ErrorParams = new Dictionary<string, object> { ["mod"] = name, ["done"] = pulled.Count, ["detail"] = e.Message },
                            state = GaleStateAfterPull(profile, prefs),
                        };
                    }
                }
            }
            finally
            {
                _modUpdateInProgress = false;
            }

            return new
            {
                ok = true, pulled, backup = backups,
                Reason = (string)null, Error = (string)null, ErrorParams = (object)null,
                state = GaleStateAfterPull(profile, prefs),
            };
        }

        /// <summary>The Gale state with the realm's mods read off the disk again, so the counts the box shows follow the pull at once.</summary>
        private object GaleStateAfterPull(string profile, ServerPreferences prefs)
        {
            if (GaleMemos.TryGetValue(profile ?? "", out var memo) && memo?.Result?.Ok == true
                && string.Equals(memo.ProfileId, prefs?.GaleFollowKey(), StringComparison.Ordinal))
                RereadGaleInstalled(profile, memo);
            return GaleState(profile);
        }

        /// <summary>
        /// One mod across: the copy's folders are copied into a staging folder beside the realm's
        /// plugins first, then the realm's own folders of that mod are moved aside into the
        /// backups, then the staged folders are moved into place. A failure before the last step
        /// leaves the realm as it was; one during it puts back what was moved aside. Answers the
        /// mod's folder name.
        /// </summary>
        private string PullOneMod(InstalledMod fromCopy, InstalledMod mine, string bepinexDir, string stamp)
        {
            var sep = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            var leaf = Path.GetFileName((fromCopy.PluginDirectory ?? fromCopy.PatcherDirectory ?? "").TrimEnd(sep));
            if (!GaleProfileDiff.IsLocalName(leaf)) throw new IOException("the mod's folder name cannot be used here.");

            var pluginsDir = Path.Combine(bepinexDir, "plugins");
            var patchersDir = Path.Combine(bepinexDir, "patchers");
            if (BepInExService.IsReparsePoint(pluginsDir)) throw new IOException("this server's plugins folder is a link to another install.");

            var touchesPatchers = fromCopy.PatcherDirectory != null || mine?.PatcherDirectory != null
                || Directory.Exists(Path.Combine(patchersDir, leaf));
            if (touchesPatchers)
            {
                InstallIsolationService.EnsureOwnPatchersFolder(bepinexDir, AppLogger);
                if (BepInExService.IsReparsePoint(patchersDir)) throw new IOException("this server's patchers folder is a link to another install.");
            }

            var staging = Path.Combine(bepinexDir, ".bakaloader-pull-" + Guid.NewGuid().ToString("N"));
            var moved = new List<(string From, string To)>();
            var placed = new List<string>();
            try
            {
                string stagedPlugin = null, stagedPatcher = null;
                if (fromCopy.PluginDirectory != null) CopyModTree(fromCopy.PluginDirectory, stagedPlugin = Path.Combine(staging, "plugins"));
                if (fromCopy.PatcherDirectory != null) CopyModTree(fromCopy.PatcherDirectory, stagedPatcher = Path.Combine(staging, "patchers"));

                var backupBase = Path.Combine(bepinexDir, TestCopyModBackupFolder, leaf, stamp);
                for (var n = 2; Directory.Exists(backupBase); n++) backupBase = Path.Combine(bepinexDir, TestCopyModBackupFolder, leaf, stamp + "-" + n);

                // What this realm has of the mod, wherever the scan found it, and anything already
                // at the paths the copy's folders are about to take.
                var aside = new[]
                    {
                        (mine?.PluginDirectory, backupBase),
                        (Path.Combine(pluginsDir, leaf), backupBase),
                        (mine?.PatcherDirectory, backupBase + "-patchers"),
                        (Path.Combine(patchersDir, leaf), backupBase + "-patchers"),
                    }
                    .Where(a => a.Item1 != null && (Directory.Exists(a.Item1) || File.Exists(a.Item1)))
                    .GroupBy(a => Path.GetFullPath(a.Item1).TrimEnd(sep), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
                foreach (var (from, to) in aside)
                {
                    var target = to;
                    for (var n = 2; Directory.Exists(target) || File.Exists(target); n++) target = to + "-" + n;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(from)) File.Move(from, target);
                    else Directory.Move(from, target);
                    moved.Add((from, target));
                }

                if (stagedPlugin != null)
                {
                    Directory.CreateDirectory(pluginsDir);
                    var into = Path.Combine(pluginsDir, leaf);
                    Directory.Move(stagedPlugin, into);
                    placed.Add(into);
                }
                if (stagedPatcher != null)
                {
                    Directory.CreateDirectory(patchersDir);
                    var into = Path.Combine(patchersDir, leaf);
                    Directory.Move(stagedPatcher, into);
                    placed.Add(into);
                }
            }
            catch
            {
                // Back as it was: what went in comes out to the staging folder, and what was
                // moved aside goes back to where it was.
                foreach (var into in placed)
                {
                    try { Directory.Move(into, Path.Combine(staging, "undone-" + Guid.NewGuid().ToString("N"))); }
                    catch (Exception) { /* left in place; the backup still holds the old one */ }
                }
                foreach (var (from, to) in Enumerable.Reverse(moved))
                {
                    try
                    {
                        if (File.Exists(to) && !File.Exists(from)) File.Move(to, from);
                        else if (Directory.Exists(to) && !Directory.Exists(from)) Directory.Move(to, from);
                    }
                    catch (Exception) { /* the backup still holds it, and the log names the folder */ }
                }
                throw;
            }
            finally
            {
                // The staging folder is this pull's own, made above under a fresh name.
                try { if (Directory.Exists(staging)) DeleteOwnStaging(staging); }
                catch (Exception) { /* a leftover staging folder BepInEx never reads */ }
            }

            return leaf;
        }

        /// <summary>A mod folder copied file by file. A link anywhere in it refuses the copy: the files behind a link are another folder's.</summary>
        private static void CopyModTree(string from, string to)
        {
            var source = new DirectoryInfo(from);
            if ((source.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("the mod's folder on the test copy is a link.");
            Directory.CreateDirectory(to);
            foreach (var entry in source.GetFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"{entry.Name} in the mod's folder on the test copy is a link.");
                if (entry is DirectoryInfo sub) CopyModTree(sub.FullName, Path.Combine(to, sub.Name));
                else File.Copy(entry.FullName, Path.Combine(to, entry.Name), overwrite: false);
            }
        }

        /// <summary>Clears the staging folder this pull made. It never holds a link: CopyModTree refuses them.</summary>
        private static void DeleteOwnStaging(string staging)
        {
            var dir = new DirectoryInfo(staging);
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) { Directory.Delete(staging); return; }
            foreach (var file in dir.GetFiles()) { file.Attributes = FileAttributes.Normal; file.Delete(); }
            foreach (var sub in dir.GetDirectories()) DeleteOwnStaging(sub.FullName);
            Directory.Delete(staging);
        }
    }
}
