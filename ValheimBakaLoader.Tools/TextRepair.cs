using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Text that was read once in the wrong encoding, and what can honestly be done about it.
    /// <para>
    /// The server writes its console output as UTF-8. Until BakaLoader 1.2.0 the app did not say
    /// how to read it, and .NET then decodes a redirected stream with the machine's own ANSI code
    /// page, so a name outside the first 128 letters arrived as one character per BYTE: a player
    /// named with two Greek letters was learned, logged, stored and kicked as four characters
    /// nobody answers to. The read is UTF-8 now, but the names already written into
    /// players-cache.json and into the statistics journal are still the broken ones, and no
    /// amount of correct reading from here on mends a file.
    /// </para>
    /// <para>
    /// WHICH page did the damage is the host's, not ours: 1252 on a Western install, 1251 on a
    /// Russian one, 932 on a Japanese one, 936 or 950 on a Chinese one. Those are exactly the
    /// hosts the language packs are for, so the page is a parameter here and
    /// <see cref="AnsiPage"/> is only the default.
    /// </para>
    /// <para>
    /// Two directions, and they are not equally trustworthy.
    /// <list type="bullet">
    /// <item><see cref="FixMojibake(string)"/> goes BACKWARDS, from the stored text to what was
    /// written, and it is only allowed where the answer is provable. See its own note.</item>
    /// <item><see cref="Damage"/> goes FORWARDS, from a name we now read correctly to what this
    /// machine WOULD have made of it. That direction never has to guess, because it repeats the
    /// damage instead of undoing it: on a single-byte page it is exact, and on a multi-byte one it
    /// is exact whenever the name's last byte was not read together with the character that
    /// followed it on the line. It is how a stored name that cannot be repaired is still
    /// recognised as the same person: see <see cref="IsDamagedSpellingOf(string, string)"/>.
    /// When it misses, the cost is a second row under the same platform id, never two people
    /// folded into one.</item>
    /// </list>
    /// </para>
    /// <para>Pure and side effect free: no file, no clock, no culture beyond the machine page.</para>
    /// </summary>
    public static class TextRepair
    {
        /// <summary>
        /// UTF-8 that THROWS on bytes that are not UTF-8 rather than quietly handing back
        /// replacement characters. The throw is the test: a silent replacement would call every
        /// honest name repairable and turn it into a row of question marks.
        /// </summary>
        private static readonly UTF8Encoding StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>Both readings of one code page, built once and kept.</summary>
        private static readonly ConcurrentDictionary<int, Pages> Resolved = new();

        static TextRepair()
        {
            // 1251, 932, 936 and the rest of them are not in the framework on .NET; this is the
            // one call that puts them there, and it is made before anything here asks for one.
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
            catch { /* already registered, or a runtime without them: the fallbacks below cover it */ }

            AnsiPage = ResolveAnsiPage();
        }

        /// <summary>
        /// The code page this machine would have done the damage in: its ANSI page, falling back
        /// to Windows-1252 and then to Latin-1 when that page cannot be resolved. Never null.
        /// </summary>
        public static Encoding AnsiPage { get; }

        /// <summary>
        /// The text as it was written, when it can be PROVED that this machine's own ANSI page
        /// read it once; otherwise the text exactly as it arrived.
        /// </summary>
        public static string FixMojibake(string text) => FixMojibake(text, AnsiPage);

        /// <summary>
        /// The text as it was written, when it can be proved that <paramref name="page"/> read it
        /// once; otherwise the text exactly as it arrived, same instance and all. Null and empty
        /// come straight back.
        /// <para>
        /// Three things have to hold, and every one of them is a refusal:
        /// </para>
        /// <list type="number">
        /// <item>the page is SINGLE BYTE. A multi-byte page (932, 936, 950) eats bytes on the way
        /// in: the Japanese damage of two katakana loses the last byte of a UTF-8 sequence into a
        /// replacement character, and re-encoding that character gives a DIFFERENT byte that is
        /// still valid UTF-8 and still decodes back to the same stored text. Every test an
        /// undoing can run passes, and the answer is a different name. There is no way to tell
        /// that apart from the case where it worked, so this does not try: on those pages a name
        /// is left exactly as stored, and <see cref="Damage"/> is how it is still matched.</item>
        /// <item>writing the stored text back out in that page works at all, with no best-fit
        /// substitution (a letter the page cannot spell was never made by reading a byte through
        /// it), and the bytes that come out are strictly valid UTF-8 carrying at least one
        /// multi-byte sequence. Pure ASCII cannot be mojibake and is never touched.</item>
        /// <item>reading those same bytes back through the page gives the stored text again,
        /// character for character. That is what makes it lossless rather than likely.</item>
        /// </list>
        /// <para>
        /// An honest name that the page CAN spell and whose bytes happen to be valid UTF-8 is
        /// indistinguishable from mojibake, and is the one thing this gets wrong. It needs a
        /// name of one capital Cyrillic letter followed by exactly the right lowercase one on a
        /// 1251 machine, or the same shape in another alphabet, so it is rare, but it is real and
        /// the tests name a case. Nothing else in the file is touched by it: the platform id, not
        /// the name, is what every lookup in the app matches on.
        /// </para>
        /// </summary>
        public static string FixMojibake(string text, Encoding page)
        {
            if (string.IsNullOrEmpty(text) || page == null) return text;

            var pages = PagesFor(page.CodePage);

            // Rule 1: never guess on a page that loses bytes on the way in.
            if (pages == null || !pages.Lenient.IsSingleByte) return text;

            byte[] bytes;
            try
            {
                bytes = pages.Strict.GetBytes(text);
            }
            catch (EncoderFallbackException)
            {
                // A letter this page cannot spell, so this page never wrote it.
                return text;
            }

            if (!HasHighByte(bytes)) return text;

            string repaired;
            try
            {
                repaired = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // Not UTF-8 underneath, so nothing was broken in the first place.
                return text;
            }

            // Rule 3: the undoing has to be reversible, or it is a guess wearing a proof.
            string reread;
            try
            {
                reread = pages.Lenient.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return text;
            }

            return string.Equals(reread, text, StringComparison.Ordinal) ? repaired : text;
        }

        /// <summary>
        /// What this machine WOULD have stored for a name it now reads correctly: the name's own
        /// UTF-8 bytes, read through the ANSI page the way the old code read the server's output,
        /// replacement characters and all. It repeats the damage rather than undoing it, over the
        /// name's own bytes: exact on a single-byte page, and on a multi-byte page exact unless
        /// the name ended part way through a character of that page, where the old read took the
        /// next byte of the line with it.
        /// </summary>
        public static string Damage(string text, Encoding page)
        {
            if (string.IsNullOrEmpty(text) || page == null) return text;

            var pages = PagesFor(page.CodePage);
            if (pages == null) return text;

            try
            {
                return pages.Lenient.GetString(Encoding.UTF8.GetBytes(text));
            }
            catch (Exception)
            {
                return text;
            }
        }

        /// <summary>
        /// True when <paramref name="stored"/> is what this machine would have made of
        /// <paramref name="fresh"/> back when it read the server's output in its own code page.
        /// <para>
        /// This is the half that works everywhere. A Japanese host's stored names cannot be
        /// repaired, but the moment that player is seen again the app has their real name, and
        /// running the old damage forward over it says whether the row already on file is the
        /// same character. That is what keeps a roster from growing a second entry for one
        /// person, and it needs nothing from the file but the text.
        /// </para>
        /// <para>A name that is plain ASCII is never damaged, so it is never a damaged spelling
        /// of anything, and the two are simply compared.</para>
        /// </summary>
        public static bool IsDamagedSpellingOf(string stored, string fresh)
            => IsDamagedSpellingOf(stored, fresh, AnsiPage);

        /// <inheritdoc cref="IsDamagedSpellingOf(string, string)"/>
        public static bool IsDamagedSpellingOf(string stored, string fresh, Encoding page)
        {
            if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(fresh)) return false;
            if (string.Equals(stored, fresh, StringComparison.Ordinal)) return false;
            if (IsAscii(fresh)) return false;

            return string.Equals(Damage(fresh, page), stored, StringComparison.Ordinal);
        }

        /// <summary>
        /// The code page by number, as this class reads it, or null when the runtime does not
        /// have it. Exists so a caller (a test, above all) names a page without having to know
        /// that the provider has to be registered first.
        /// </summary>
        public static Encoding Page(int codePage) => PagesFor(codePage)?.Lenient;

        /// <summary>True when every character is plain ASCII, which no code page can damage.</summary>
        public static bool IsAscii(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;

            foreach (var c in text)
            {
                if (c > 0x7F) return false;
            }

            return true;
        }

        private static bool HasHighByte(byte[] bytes)
        {
            foreach (var b in bytes)
            {
                if (b >= 0x80) return true;
            }

            return false;
        }

        private static Pages PagesFor(int codePage)
        {
            if (codePage <= 0) return null;

            return Resolved.GetOrAdd(codePage, cp =>
            {
                try
                {
                    return new Pages(
                        Encoding.GetEncoding(cp,
                            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
                        Encoding.GetEncoding(cp));
                }
                catch (Exception)
                {
                    return null;
                }
            });
        }

        /// <summary>
        /// The machine's ANSI page, which is the one .NET falls back to for a redirected stream
        /// with no console behind it, and so the one the damage was done in. The system's own
        /// page is asked for first and the running culture's is the answer when that cannot be
        /// asked at all. Windows-1252 is the next fallback because it is what most of the world's
        /// installs use, and Latin-1 is the last because it is in the framework itself and cannot
        /// fail: it agrees with 1252 everywhere except 0x80 to 0x9F, so it still mends the common
        /// case and refuses the rest rather than inventing an answer.
        /// </summary>
        private static Encoding ResolveAnsiPage()
        {
            // Windows itself first: the page that decoded the server's output is the
            // SYSTEM's one (the language for non-Unicode programs), not the one belonging
            // to whatever culture this process formats its numbers in. They are the same
            // on an ordinary install and differ on a machine whose region format was
            // changed, which is exactly the kind of machine this was written for.
            return Page(SystemAnsiCodePage())
                ?? Page(CultureInfo.CurrentCulture.TextInfo.ANSICodePage)
                ?? Page(1252)
                ?? Encoding.Latin1;
        }

        /// <summary>
        /// The system ANSI code page, or 0 when it cannot be asked for, which is every
        /// platform that is not Windows and anything that refuses the call. Never throws: a
        /// page that cannot be read is one more fallback, not a failure to start.
        /// </summary>
        private static int SystemAnsiCodePage()
        {
            try
            {
                return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? GetACP() : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern int GetACP();

        private sealed class Pages
        {
            public Pages(Encoding strict, Encoding lenient)
            {
                Strict = strict;
                Lenient = lenient;
            }

            /// <summary>Throws rather than substituting, so a repair is proved or refused.</summary>
            public Encoding Strict { get; }

            /// <summary>Substitutes exactly as the old read did, so the damage can be repeated.</summary>
            public Encoding Lenient { get; }
        }
    }
}
