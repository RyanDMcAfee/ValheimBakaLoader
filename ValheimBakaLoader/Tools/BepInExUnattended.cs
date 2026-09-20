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
        /// <param name="consentEffective">
        /// Whether the host has ANSWERED the question and the answer was yes, which is
        /// <see cref="BepInExConsent.Effective"/> and never the preference on its own. An
        /// unanswered default is not a yes, and nothing unattended happens on one.
        /// </param>
        /// <param name="installed">Whether BepInEx is installed in the base install at all.</param>
        /// <param name="installedVersion">The pack version the install note names, or null.</param>
        /// <param name="latestVersion">The newest pack version the site offers, or null when unknown.</param>
        /// <param name="otherServersRunning">Whether any OTHER server on this base install is up.</param>
        /// <param name="drivenElsewhere">
        /// Whether this install's doorstop config points at another tool's profile folder.
        /// Writing there swaps a host's whole mod set, so the window never does it.
        /// </param>
        /// <param name="foreignCore">Whether the BepInEx here is not a 5.x one.</param>
        /// <param name="drifted">
        /// Whether BakaLoader gave up ownership because somebody else wrote over the files its
        /// note recorded. Taking the install back unattended is how two tools end up
        /// overwriting each other forever.
        /// </param>
        /// <param name="unrecognised">
        /// Whether there is a core here BakaLoader does not recognise. Nothing is written over
        /// one of those without being asked.
        /// </param>
        /// <param name="filesMissing">
        /// Whether files the note lists are gone from disk, which an antivirus taking
        /// winhttp.dll is the ordinary cause of. That is an install to repair, and the repair
        /// is the write, so it happens even when the version has not moved and even when the
        /// file that went makes the install read as not installed at all.
        /// </param>
        public static BepInExUnattendedAction Decide(
            bool consentEffective,
            bool installed,
            string installedVersion,
            string latestVersion,
            bool otherServersRunning,
            bool drivenElsewhere = false,
            bool foreignCore = false,
            bool drifted = false,
            bool unrecognised = false,
            bool filesMissing = false)
        {
            if (!consentEffective) return BepInExUnattendedAction.Skip;

            // Four ways an install says "this is not yours to write to". None of them is a
            // failure and none of them is silent: the row carries the reason and the manual
            // button can still be pressed after the host has read it.
            if (drivenElsewhere || foreignCore || drifted || unrecognised)
                return BepInExUnattendedAction.Skip;

            // Not installed at all is the launch path's business, not this one's: a server
            // about to come up with no loader is handled before it starts, where the failure
            // can be reported against the start that needed it.
            //
            // An install whose note lists a file that has GONE is the exception, and it is the
            // ordinary antivirus case: winhttp.dll is half of what "installed" means, so losing
            // that one file makes a whole install answer false here. Sending it to the launch
            // path instead would mean the repair only ever happened on a start, and a host
            // whose world is already up would sit on a loader that loads nothing until they
            // next stopped it.
            if (!installed && !filesMissing) return BepInExUnattendedAction.Skip;

            // The site did not answer. An unknown newest version is never a reason to write.
            if (string.IsNullOrWhiteSpace(latestVersion)) return BepInExUnattendedAction.Skip;

            // No note means BakaLoader did not put this here. First adoption: the install is
            // taken over at the next window, which is what D11.7c calls for.
            var newer = string.IsNullOrWhiteSpace(installedVersion)
                || SemVer.Compare(latestVersion, installedVersion) > 0;

            if (!newer && !filesMissing) return BepInExUnattendedAction.Skip;

            return otherServersRunning ? BepInExUnattendedAction.Defer : BepInExUnattendedAction.Apply;
        }
    }
}
