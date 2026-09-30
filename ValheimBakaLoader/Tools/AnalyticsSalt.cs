using System;
using System.Security.Cryptography;
using ValheimBakaLoader.Game;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The random value this install keys its anonymous per-realm counts with.
    /// <para>
    /// WHY IT IS NOT THE DEVICE HASH. The per-realm key used to be a SHA-256 over the
    /// install's anonymous id and the realm name. That id is the device hash, and the device
    /// hash travels in the SAME beat as the counts: anybody holding a beat holds the salt, so
    /// a realm name could be recovered by hashing a wordlist against it. A one-way key whose
    /// salt is published beside it is not a one-way key.
    /// </para>
    /// <para>
    /// So the salt is a random value made once per install and kept in userprefs.json, and it
    /// is never sent anywhere. The key is still stable per install, which is all the beat
    /// needs it to be: it exists to tell two realms on one machine apart between beats, and
    /// nothing outside this machine ever has to reproduce it.
    /// </para>
    /// </summary>
    public static class AnalyticsSalt
    {
        /// <summary>How many hex characters a salt is: 16 random bytes.</summary>
        public const int HexLength = 32;

        /// <summary>A fresh salt. Cryptographic randomness, because guessable is the whole bug.</summary>
        public static string New()
        {
            var bytes = new byte[HexLength / 2];
            using var random = RandomNumberGenerator.Create();
            random.GetBytes(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        /// <summary>True when a stored value is a salt this build wrote.</summary>
        public static bool Looks(string value)
        {
            if (value == null || value.Length != HexLength) return false;
            foreach (var c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        /// <summary>
        /// This install's salt, made and written down the first time it is asked for.
        /// <para>
        /// Get-or-create under the one gate every other writer of userprefs.json takes, and
        /// the change function returns false when there is nothing to write, so asking for it
        /// again costs no disk write. A document hand-edited to something that is not a salt is
        /// replaced rather than used, because a short or empty salt is the bug back again.
        /// </para>
        /// </summary>
        public static string Ensure(IUserPreferencesProvider preferences)
        {
            if (preferences == null) return New();

            string held = null;
            preferences.Mutate(prefs =>
            {
                if (Looks(prefs.AnalyticsSalt))
                {
                    held = prefs.AnalyticsSalt;
                    return false;
                }

                held = prefs.AnalyticsSalt = New();
                return true;
            });

            return held ?? New();
        }
    }
}
