using System;

namespace ValheimBakaLoader.Tools
{
    /// <summary>What the unattended window should do about BepInEx on this restart.</summary>
    public enum BepInExUnattendedAction
    {
        /// <summary>Nothing to do: not looked after, nothing newer, or nothing to update.</summary>
        Skip,

        /// <summary>Write it now, while every server on this install is down.</summary>
        Apply,

        /// <summary>There is a newer pack, and another server on this install is up.</summary>
        Defer,
    }

    /// <summary>
    /// Whether the restart window that applies mod updates should also move BepInEx.
    /// <para>
    /// This is a rule rather than a branch inside the hook because it decides something the
    /// host never sees happen: the window runs while the server is down and nobody is
    /// watching, and the wrong answer either writes a shared loader out from under a world
    /// that is still up, or leaves an install a version behind with no sign of it. Both are
    /// worth a table.
    /// </para>
    /// </summary>
    public static class BepInExUnattended
    {
        /// <summary>
        /// The decision.
        /// </summary>
        /// <param name="maintained">The BepInExMaintained preference.</param>
        /// <param name="installed">Whether BepInEx is installed in the base install at all.</param>
        /// <param name="installedVersion">The pack version the install note names, or null.</param>
        /// <param name="latestVersion">The newest pack version the site offers, or null when unknown.</param>
        /// <param name="otherServersRunning">Whether any OTHER server on this base install is up.</param>
        public static BepInExUnattendedAction Decide(
            bool maintained,
            bool installed,
            string installedVersion,
            string latestVersion,
            bool otherServersRunning)
        {
            if (!maintained) return BepInExUnattendedAction.Skip;

            // Not installed at all is the launch path's business, not this one's: a server
            // about to come up with no loader is handled before it starts, where the failure
            // can be reported against the start that needed it.
            if (!installed) return BepInExUnattendedAction.Skip;

            // The site did not answer. An unknown newest version is never a reason to write.
            if (string.IsNullOrWhiteSpace(latestVersion)) return BepInExUnattendedAction.Skip;

            // No note means BakaLoader did not put this here. First adoption: the install is
            // taken over at the next window, which is what D11.7c calls for.
            var newer = string.IsNullOrWhiteSpace(installedVersion)
                || SemVer.Compare(latestVersion, installedVersion) > 0;

            if (!newer) return BepInExUnattendedAction.Skip;

            return otherServersRunning ? BepInExUnattendedAction.Defer : BepInExUnattendedAction.Apply;
        }
    }
}
