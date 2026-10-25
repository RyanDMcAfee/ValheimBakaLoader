using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;

namespace ValheimBakaLoader.Forms
{
    /// <summary>
    /// The host side of Make a test copy (issue 20) that is not servers.create itself.
    /// <para>
    /// A test copy is an ordinary realm made by servers.create with testCopyOf set: its own
    /// install seeded from the source's BepInEx, its own save folder, a copy of the source's
    /// world, and Public held off. What lives here is what that needs around it: the mark kept
    /// through a settings save, the mark moved when the source is renamed, the source's BepInEx
    /// folder, and the optional character step, which lists this PC's Valheim characters, Steam
    /// Cloud ones included, and writes a renamed copy of one (Tools/CharacterCopy.cs).
    /// </para>
    /// </summary>
    public partial class BlendWindow
    {
        /// <summary>
        /// The Valheim save folder the characters are read from, for a test to point at a temp
        /// folder. Null means the client's own, which is where the game keeps them.
        /// </summary>
        internal string CharacterSaveRootOverride;

        private string CharacterSaveRoot => string.IsNullOrWhiteSpace(CharacterSaveRootOverride)
            ? AppDataRoot.ValheimSaveRootFull
            : CharacterSaveRootOverride;

        /// <summary>
        /// Steam's own folder the Steam Cloud characters are read from, for a test to point at a
        /// temp tree. Null means the one Steam names on this PC.
        /// </summary>
        internal string CharacterSteamRootOverride;

        /// <summary>
        /// The Steam folder to read Steam Cloud characters under. A test that points the save
        /// folder at a temp folder and says nothing about Steam reads no Steam folder at all, so
        /// it can never list (or count) the characters of the PC it runs on.
        /// </summary>
        private string CharacterSteamRoot => !string.IsNullOrWhiteSpace(CharacterSteamRootOverride)
            ? CharacterSteamRootOverride
            : string.IsNullOrWhiteSpace(CharacterSaveRootOverride) ? CharacterCopy.FindSteamRoot() : null;

        private void RegisterTestCopyRpcs()
        {
            // This PC's characters, for the test copy dialog's drop down: the save folder's and
            // every Steam Cloud folder's, each row saying which ("local" or "cloud"). Read only:
            // nothing under the character folders is opened for writing here.
            RegisterRpc("characters.list", p => Task.Run<object>(() =>
                CharacterCopy.List(CharacterSaveRoot, CharacterSteamRoot)
                    .Select(c => new
                    {
                        folder = c.Folder,
                        source = c.Source,
                        file = c.File,
                        name = c.Name,
                        copyName = c.CopyName,
                        copyFile = c.CopyFile,
                        copyExists = c.CopyExists,
                        problem = c.Problem,
                    })
                    .ToList()));

            // A renamed copy of one character, written as a NEW file: beside it, or into
            // characters_local for a Steam Cloud character (never into Steam's folder). Refuses a
            // copy that is already there rather than writing over it; the refusals are CharacterCopy's.
            RegisterRpc("characters.copy", async p =>
            {
                var folder = p.Value<string>("folder");
                var file = p.Value<string>("file");
                var root = CharacterSaveRoot;
                var steam = CharacterSteamRoot;
                var copy = await Task.Run(() => CharacterCopy.Copy(root, steam, folder, file));
                Logger.Information("Copied character '{0}' as '{1}' into {2}.", copy.SourceName, copy.Name, copy.Path);
                return new
                {
                    // Where the copy was written, always a save folder, and so always local.
                    folder = copy.Folder,
                    source = CharacterCopy.LocalSource,
                    file = copy.File,
                    name = copy.Name,
                    sourceName = copy.SourceName,
                };
            });

            // Merge-back: following a test copy, and taking its mods back (BlendWindow.TestCopyFeed.cs).
            RegisterTestCopyFeedRpcs();
        }

        /// <summary>
        /// The test copy mark on disk put back onto a profile the page sent, with Public held off
        /// for a test copy. See <see cref="ServerPreferences.KeepTestCopyFrom"/>.
        /// </summary>
        private ServerPreferences KeepTestCopyOf(ServerPreferences prefs)
        {
            if (prefs == null) return null;

            ServerPreferences stored = null;
            try
            {
                stored = ServerPrefsProvider.LoadPreferences(
                    string.IsNullOrWhiteSpace(prefs.ProfileName) ? ActiveProfileName : prefs.ProfileName);
            }
            catch { /* an unreadable profile leaves the payload as it came */ }

            // A first save under a name nothing has yet is a new realm, and a copy still pointing
            // at that name lost its source earlier.
            if (stored == null && !string.IsNullOrWhiteSpace(prefs.ProfileName))
                MarkTestCopiesSourceGone(prefs.ProfileName);

            return prefs.KeepTestCopyFrom(stored);
        }

        /// <summary>
        /// Every test copy of <paramref name="oldName"/> made to name <paramref name="newName"/>
        /// instead, after the source was renamed.
        /// </summary>
        private void MoveTestCopiesOf(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return;

            // A copy whose source was deleted keeps naming the old one: the realm being renamed
            // only took that name later and never made it.
            foreach (var copy in ServerPrefsProvider.LoadPreferences()
                         .Where(x => !x.TestSourceGone
                                     && string.Equals(x.TestCopyOf, oldName, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                copy.TestCopyOf = newName;
                ServerPrefsProvider.SavePreferences(copy);
                Logger.Information("Test copy '{0}' now names its source '{1}' (was '{2}').", copy.ProfileName, newName, oldName);
            }

            // And the other way round: a source that takes its mods from the renamed realm, a
            // test copy, follows it to its new name. The follow is only ever set on a copy that
            // still has its source, so this is the same copy, not a realm that took the name.
            foreach (var source in ServerPrefsProvider.LoadPreferences()
                         .Where(x => string.Equals(x.FollowTestCopy?.Trim(), oldName, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                source.FollowTestCopy = newName;
                ServerPrefsProvider.SavePreferences(source);
                Logger.Information("'{0}' now takes its mods from the test copy '{1}' (was '{2}').", source.ProfileName, newName, oldName);
            }
        }

        /// <summary>
        /// Every test copy of <paramref name="sourceName"/> marked as having lost its source, after
        /// that realm was deleted, or before a new realm is made under a name such a copy still
        /// names. The copies stay standing; the mark only keeps a later realm with the same name
        /// from quietly becoming their source. A realm following a test copy of that name lets go
        /// of it for the same reason (a name that goes, or is given anew, lets go of everything
        /// naming it).
        /// </summary>
        private void MarkTestCopiesSourceGone(string sourceName)
        {
            if (string.IsNullOrWhiteSpace(sourceName)) return;

            foreach (var copy in ServerPrefsProvider.LoadPreferences()
                         .Where(x => !x.TestSourceGone
                                     && !string.Equals(x.ProfileName, sourceName, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(x.TestCopyOf, sourceName, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                copy.TestSourceGone = true;
                ServerPrefsProvider.SavePreferences(copy);
                Logger.Information("Test copy '{0}' lost its source '{1}'.", copy.ProfileName, sourceName);
            }

            // A realm that took its mods from a test copy of that name lets go of it: the copy
            // is gone, or the name now belongs to a new realm that is no test copy of anything.
            // Its Gale profile id, if it has one, is left as it was.
            foreach (var source in ServerPrefsProvider.LoadPreferences()
                         .Where(x => !string.Equals(x.ProfileName, sourceName, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(x.FollowTestCopy?.Trim(), sourceName, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                source.ForgetTestCopyFollow();
                ServerPrefsProvider.SavePreferences(source);
                GaleMemos.TryRemove(source.ProfileName ?? "", out _);
                GaleConfigMemos.TryRemove(source.ProfileName ?? "", out _);
                Logger.Information("'{0}' no longer takes its mods from the test copy '{1}', which is gone.", source.ProfileName, sourceName);
            }
        }

        /// <summary>
        /// One realm's BepInEx folder, beside its own server exe, whichever realm is on screen.
        /// A test copy is seeded from its source's, and the source is not always the realm the
        /// window is standing on.
        /// </summary>
        private string GetBepInExDirFor(string profileName)
        {
            try
            {
                var exe = GetServerExePathFor(profileName);
                var dir = string.IsNullOrWhiteSpace(exe) ? null : Path.GetDirectoryName(exe);
                return string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, "BepInEx");
            }
            catch { return null; }
        }
    }
}
