using System;
using System.Collections.Generic;
using System.IO;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Which realm, if any, has open a folder that a removal is about to delete from.
    /// <para>
    /// WHY THIS EXISTS. The refusal used to be "is ANY server up", and a realm is not an
    /// install. Realms are given their own isolated install by default, and an isolated
    /// install COPIES its plugins folder, so the plugins of a stopped realm sit somewhere the
    /// running one has never opened and Windows has no objection to deleting them. The old
    /// rule refused anyway, named the realm the host was not looking at, and left a host who
    /// keeps one world up all evening unable to remove a mod from any other realm for as long
    /// as it stayed up. It also disagreed with the page, which asked only about the realm on
    /// screen, so the confirm offered a button the host side then refused.
    /// </para>
    /// <para>
    /// The other half is why this takes a LIST of folders rather than one. An isolated install
    /// copies <c>plugins</c> but JUNCTIONS <c>patchers</c> to the base install, so that one is
    /// genuinely shared between realms: a mod that carries a patcher part, removed from a
    /// stopped realm, deletes the very folder a running realm has loaded. Whatever the removal
    /// will touch is what is asked about here, and a junction is followed before the two are
    /// compared so a shared folder reached by two names is still one folder.
    /// </para>
    /// </summary>
    public static class ModRemovalGate
    {
        /// <summary>One live realm as this gate reads it.</summary>
        public readonly struct Session
        {
            public Session(string profileName, bool running, params string[] folders)
                : this(profileName, running, (IReadOnlyList<string>)folders) { }

            public Session(string profileName, bool running, IReadOnlyList<string> folders)
            {
                ProfileName = profileName;
                Running = running;
                Folders = folders ?? Array.Empty<string>();
            }

            /// <summary>The realm's own name, which is what a refusal says out loud.</summary>
            public string ProfileName { get; }

            /// <summary>
            /// Anything but Stopped. A server that is starting has already loaded its plugins,
            /// and one that is stopping has not let go of them yet.
            /// </summary>
            public bool Running { get; }

            /// <summary>
            /// The folders this session has open: its own plugins folder and its patchers
            /// folder, the second of which may be a junction it shares with other realms.
            /// </summary>
            public IReadOnlyList<string> Folders { get; }
        }

        /// <summary>
        /// The name of the realm holding one of <paramref name="touched"/> open, or null when
        /// nothing is and the removal can go ahead.
        /// <para>
        /// Unknown is treated as held. When the folders a removal will touch cannot be worked
        /// out, or a running session will not say which folders it opened, nothing here can
        /// prove the two are different installs, and the safe answer to that is the one that
        /// refuses: a half removed folder is worse than a refusal the host can read.
        /// </para>
        /// </summary>
        public static string BlockedBy(IEnumerable<Session> sessions, IEnumerable<string> touched)
        {
            if (sessions == null) return null;

            var wanted = Resolve(touched);

            foreach (var session in sessions)
            {
                if (!session.Running) continue;
                if (wanted.Count == 0) return session.ProfileName;

                var theirs = Resolve(session.Folders);
                if (theirs.Count == 0) return session.ProfileName;

                foreach (var folder in theirs)
                {
                    if (wanted.Contains(folder)) return session.ProfileName;
                }
            }

            return null;
        }

        /// <summary>
        /// Every folder in the list as one comparable name: absolute, with no trailing
        /// separator and no relative step left in it, and with a directory junction followed to
        /// what it really points at. Windows paths do not care about case, and neither does
        /// this. Anything blank is dropped rather than turned into a name that matches nothing.
        /// </summary>
        private static HashSet<string> Resolve(IEnumerable<string> folders)
        {
            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (folders == null) return resolved;

            foreach (var folder in folders)
            {
                var one = ResolveOne(folder);
                if (!string.IsNullOrWhiteSpace(one)) resolved.Add(one);
            }

            return resolved;
        }

        private static string ResolveOne(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;

            string full;
            try
            {
                full = Path.GetFullPath(folder)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return folder.Trim();
            }

            try
            {
                // A junction is another name for a folder that is already in this list under its
                // own name, and an isolated realm's patchers folder is exactly that. Nothing here
                // is created or written: the folder is only asked where it points.
                var target = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                {
                    return target.FullName
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            }
            catch
            {
                // Not a link, gone, or a path Windows will not answer for: the name stands.
            }

            return full;
        }
    }
}
