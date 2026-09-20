using System;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Which published pack an UNATTENDED write is allowed to put in.
    /// <para>
    /// A host pressing the button has read the version and chosen it. A restart window has
    /// nobody at the keyboard, so the pack it writes has to have earned its way in on its
    /// own: not a version the site has taken down, not a pre-release, not a package the
    /// listing marks deprecated, old enough that somebody else has already found a bad
    /// upload, and with a size to check what came down against. None of these hold the manual
    /// button back.
    /// </para>
    /// <para>
    /// Every answer is a reason from <see cref="BepInExSkipReason"/> rather than a boolean,
    /// because a window that writes nothing and says nothing is exactly the silence section H
    /// is about: the row has to be able to name which of these five it was.
    /// </para>
    /// </summary>
    public static class BepInExPackPolicy
    {
        /// <summary>
        /// How long a pack has to have been listed before a window will write it.
        /// <para>
        /// Three days is the span in which a bad upload of this particular package gets found:
        /// it is the loader every mod on the machine runs under, so a broken one is noticed by
        /// the whole community within hours and pulled. Waiting means the discovery is somebody
        /// else's rather than this host's, and it costs nothing but a restart window: the next
        /// one takes the pack that by then has a track record.
        /// </para>
        /// </summary>
        public static readonly TimeSpan Soak = TimeSpan.FromHours(72);

        /// <summary>
        /// When a pack listed at this moment becomes eligible for an unattended write, in UTC,
        /// or null when the listing carried no date to count from. This is what the row shows.
        /// </summary>
        public static DateTime? EligibleAt(DateTime? listedUtc)
            => listedUtc == null ? null : Utc(listedUtc.Value).Add(Soak);

        /// <summary>
        /// The reason an unattended write must not put this pack in, or null when it may.
        /// Always null for a manual write: every one of these is a judgement the host is
        /// allowed to make and the window is not.
        /// </summary>
        /// <param name="unattended">Whether this is a restart window or a start rather than a press.</param>
        /// <param name="version">The pack version the listing offers.</param>
        /// <param name="deprecated">Whether the listing marks the package deprecated.</param>
        /// <param name="listedUtc">When that version was published, or null when unknown.</param>
        /// <param name="fileSize">
        /// What the listing says the archive weighs, from either endpoint. Null means there is
        /// nothing to check the download against, and an unattended write does not unpack an
        /// archive nothing vouched for over a folder a server runs from.
        /// </param>
        /// <param name="nowUtc">The moment to judge the soak against.</param>
        /// <param name="pulled">
        /// Whether the listing says this version is no longer one the site serves
        /// (<c>is_active</c> false). Somebody took it down, and the ordinary reason for taking
        /// a loader pack down is that it broke something: a window never writes one. Only a
        /// listing that actually said no counts, the same way deprecation does.
        /// </param>
        public static string RefusalFor(
            bool unattended, string version, bool deprecated,
            DateTime? listedUtc, long? fileSize, DateTime nowUtc,
            bool pulled = false)
        {
            if (!unattended) return null;

            // Asked before everything else, because it is the only one of these that says the
            // pack should not be on this machine at all rather than not yet or not unwatched.
            if (pulled) return BepInExSkipReason.Pulled;

            // A pre-release is published to be tried, by somebody who chose to try it. There
            // is no falling back to the last stable one here: the client keeps only the newest
            // version of each package, so the older one is not a thing this can name. The
            // window leaves the install alone and the row says a pre-release is out.
            if (SemVer.IsPreRelease(version)) return BepInExSkipReason.PreRelease;

            if (deprecated) return BepInExSkipReason.Deprecated;

            // An unknown date is not a reason to hold back. The soak is a rule about a pack
            // that IS known to be new, and refusing every pack whose listing happened not to
            // carry a date would stop the window working at all on the day Thunderstore
            // changes a field name. The size check below is the guard that has no such hole.
            if (StillSoaking(listedUtc, nowUtc)) return BepInExSkipReason.Soak;

            if (fileSize == null || fileSize.Value <= 0) return BepInExSkipReason.Unverified;

            return null;
        }

        /// <summary>
        /// True when a pack has not been listed long enough for a window to write it. A
        /// listing with no date has not been shown to be young and is not held back.
        /// </summary>
        public static bool StillSoaking(DateTime? listedUtc, DateTime nowUtc)
        {
            var eligible = EligibleAt(listedUtc);
            return eligible != null && Utc(nowUtc) < eligible.Value;
        }

        /// <summary>
        /// A moment as UTC. A date parsed out of JSON comes back in this machine's own zone
        /// unless it was written with an offset, and comparing one of those against
        /// <see cref="DateTime.UtcNow"/> is out by however far from Greenwich the host lives,
        /// which on the far side of the world is half the soak.
        /// </summary>
        private static DateTime Utc(DateTime moment) => moment.Kind switch
        {
            DateTimeKind.Utc => moment,
            DateTimeKind.Local => moment.ToUniversalTime(),
            _ => DateTime.SpecifyKind(moment, DateTimeKind.Utc),
        };
    }
}
