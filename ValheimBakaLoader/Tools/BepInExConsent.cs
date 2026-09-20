namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Whether the host has agreed to BakaLoader looking after BepInEx.
    /// <para>
    /// There are THREE states, not two, and the switch alone only carries two of them. The
    /// preference defaults to on, so a host upgrading from 1.1.x counts as "maintained" from
    /// the first second of the first launch, before anybody has been asked anything; a profile
    /// that auto-starts never reaches the question at all. Reading the switch on its own
    /// therefore says yes on behalf of somebody who has not spoken.
    /// </para>
    /// <para>
    /// So the two preferences are read together and only here: <c>BepInExMaintenanceAsked</c>
    /// says whether there is an answer, and <c>BepInExMaintained</c> says what the answer was.
    /// Unanswered is its own state: nothing unattended happens in it, and the Hearth and the
    /// first-start dialog are what turn it into one of the other two. Both preferences keep
    /// their names, their types and their defaults, so a file written by 1.1.x still reads.
    /// </para>
    /// </summary>
    public static class BepInExConsent
    {
        /// <summary>
        /// True only when the host has ANSWERED and the answer was yes. Every reader of the
        /// switch that is about to write, or about to tell the host that BakaLoader is looking
        /// after the loader, asks this rather than the preference.
        /// </summary>
        public static bool Effective(bool maintained, bool asked) => asked && maintained;

        /// <summary>
        /// True while the question is still open: nobody has answered, so neither the yes
        /// path nor the no path is the truth yet. This is what the standing Hearth row is
        /// raised on, and what keeps an unattended write from happening on a default.
        /// </summary>
        public static bool Unanswered(bool asked) => !asked;

        /// <summary>
        /// True when the host answered and the answer was no. Nothing is ever written after
        /// this, by any path.
        /// </summary>
        public static bool Declined(bool maintained, bool asked) => asked && !maintained;
    }
}
