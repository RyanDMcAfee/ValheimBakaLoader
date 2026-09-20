using System;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The names this defect was reported with, in both spellings: the one the player chose
    /// and the one BakaLoader stored while it read the server's output with the machine's code
    /// page instead of as UTF-8.
    /// <para>
    /// Every one of them is written as character NUMBERS rather than as letters, so these test
    /// files stay plain ASCII on disk and cannot themselves be changed by an editor that saves
    /// in the wrong encoding. A test for a text bug that is written in the text the bug eats is
    /// a test nobody can trust.
    /// </para>
    /// <para>
    /// The broken spellings are not guesses: each is the good name's UTF-8 bytes read back
    /// through a code page, which is the whole of what went wrong. WHICH page is the host's
    /// own, so a name below that was broken on another machine says which page in its own
    /// name: 1252 on a Western install, 1251 on a Russian one, 932 on a Japanese one. A name
    /// with no page in its name was broken on 1252, the machine this was reported from.
    /// </para>
    /// </summary>
    internal static class Mojibake
    {
        /// <summary>A string from its character numbers.</summary>
        public static string Text(params int[] codes)
            => new string(Array.ConvertAll(codes, c => (char)c));

        /// <summary>Greek capital omega, a space, greek capital sigma: the reported name.</summary>
        public static string Greek => Text(0x03A9, 0x0020, 0x03A3);

        /// <summary>The four Latin-1 characters that name was stored as.</summary>
        public static string GreekBroken => Text(0x00CE, 0x00A9, 0x0020, 0x00CE, 0x00A3);

        /// <summary>A Cyrillic name (Drakon).</summary>
        public static string Cyrillic => Text(0x0414, 0x0440, 0x0430, 0x043A, 0x043E, 0x043D);

        public static string CyrillicBroken => Text(
            0x00D0, 0x201D, 0x00D1, 0x20AC, 0x00D0, 0x00B0,
            0x00D0, 0x00BA, 0x00D0, 0x00BE, 0x00D0, 0x00BD);

        /// <summary>A Japanese name in katakana (Baka).</summary>
        public static string Japanese => Text(0x30D0, 0x30AB);

        /// <summary>
        /// Its broken spelling, which runs through two of the five bytes Windows-1252 leaves
        /// undefined. Windows maps those to themselves and so does the repair, or a Japanese
        /// name would be the one alphabet that could not come back.
        /// </summary>
        public static string JapaneseBroken => Text(0x00E3, 0x0192, 0x0090, 0x00E3, 0x201A, 0x00AB);

        /// <summary>An honest French name, which was never broken and must never be touched.</summary>
        public static string French => Text(0x0046, 0x0061, 0x0075, 0x0072, 0x00E9);

        /// <summary>An honest Norse name with an umlaut, same rule.</summary>
        public static string Umlaut => Text(
            0x004A, 0x00F6, 0x0072, 0x006D, 0x0075, 0x006E, 0x0067, 0x0061, 0x006E, 0x0064, 0x0072);

        // ---- the other machines, because the page that did the damage is the host's ----

        /// <summary>
        /// The same Cyrillic name as a RUSSIAN install stored it, through code page 1251. Not one
        /// character of it is what 1252 made of that name, so a repair that assumed 1252 refuses
        /// this outright. That is why the page is a parameter and not a constant.
        /// </summary>
        public static string CyrillicBroken1251 => Text(
            0x0420, 0x201D, 0x0421, 0x0402, 0x0420, 0x00B0,
            0x0420, 0x0454, 0x0420, 0x0455, 0x0420, 0x0405);

        /// <summary>A Japanese name in katakana (Sato).</summary>
        public static string Katakana => Text(0x30B5, 0x30C8);

        /// <summary>
        /// What a JAPANESE install stored it as, through code page 932. This one cannot be put
        /// back: 932 reads two bytes at a time, the last byte of the name landed on no character
        /// it has and became a replacement, and that replacement is written back out as two
        /// different bytes. Every check an undoing can run still passes, which is the trap.
        /// </summary>
        public static string KatakanaBroken932 => Text(0x7E67, 0xFF75, 0x7E5D, 0x30FB);

        /// <summary>
        /// The name a repair that trusted those checks would have produced: the first letter
        /// right, the second one a different letter, and a stray E on the end. It is a name, it
        /// is not this player's, and nobody would ever report it as a bug.
        /// </summary>
        public static string KatakanaWrongUndo => Text(0x30B5, 0x30C1, 0x0045);

        /// <summary>
        /// The one shape the rule genuinely cannot tell apart: an honest two letter Cyrillic name
        /// whose own 1251 bytes (D0 B8) happen to be valid UTF-8 for another Cyrillic letter. A
        /// Russian host who calls themselves this is the one person this gets wrong, and the test
        /// beside it pins that rather than pretending it cannot happen.
        /// </summary>
        public static string CyrillicAccident => Text(0x0420, 0x0451);

        /// <summary>What the repair makes of that name on a 1251 machine.</summary>
        public static string CyrillicAccidentUndone => Text(0x0438);
    }
}
