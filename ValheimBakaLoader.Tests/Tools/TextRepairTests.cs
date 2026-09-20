using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// THE RULE BEHIND THE REPAIR, as a table.
    /// <para>
    /// A name is only put back when it can be proved that it was read through the machine's own
    /// code page once: every character has to fit in that page, the bytes underneath have to be
    /// valid UTF-8, and there has to be at least one multi-byte sequence among them. Everything
    /// else comes back untouched, and the honest names below are the half that matters. A
    /// repair that ate a French name to mend a Greek one would be a worse bug than the one it
    /// was written for, because nobody would report it: the name would simply be wrong and look
    /// deliberate.
    /// </para>
    /// <para>
    /// Every table below names the page it is driving, because the names it is built from were
    /// broken on a particular one. The short call reads with the MACHINE's page, so a test built
    /// on 1252 names that would pass here and fail on a Russian or a Japanese host, where
    /// refusing a 1252 shaped name is the correct answer rather than a defect. The one thing
    /// that is still asked of the short call is that it goes through the machine's own page,
    /// which is the last test in the file.
    /// </para>
    /// </summary>
    public class TextRepairTests
    {
        [Fact]
        public void The_reported_greek_name_comes_back()
        {
            Assert.Equal(Mojibake.Greek,
                TextRepair.FixMojibake(Mojibake.GreekBroken, TextRepair.Page(1252)));
        }

        [Fact]
        public void A_cyrillic_name_comes_back()
        {
            Assert.Equal(Mojibake.Cyrillic,
                TextRepair.FixMojibake(Mojibake.CyrillicBroken, TextRepair.Page(1252)));
        }

        /// <summary>
        /// Two of this name's bytes land on characters Windows-1252 leaves undefined. Windows
        /// hands those back as themselves, so the round trip closes; a repair that refused them
        /// would leave Japanese as the one alphabet it could not mend.
        /// </summary>
        [Fact]
        public void A_japanese_name_comes_back_through_the_undefined_bytes()
        {
            Assert.Equal(Mojibake.Japanese,
                TextRepair.FixMojibake(Mojibake.JapaneseBroken, TextRepair.Page(1252)));
        }

        [Fact]
        public void An_honest_french_name_is_left_exactly_as_it_is()
        {
            var name = Mojibake.French;
            Assert.Same(name, Keep(name));
        }

        [Fact]
        public void An_honest_norse_name_with_an_umlaut_is_left_exactly_as_it_is()
        {
            var name = Mojibake.Umlaut;
            Assert.Same(name, Keep(name));
        }

        /// <summary>
        /// A name already in its own alphabet holds letters Windows-1252 cannot spell at all,
        /// so it was never made by reading bytes through it. Running the repair twice must be
        /// the same as running it once, or a second launch would eat what the first one mended.
        /// </summary>
        [Fact]
        public void A_name_outside_windows_1252_is_left_alone_and_the_repair_is_idempotent()
        {
            var greek = Mojibake.Greek;
            var japanese = Mojibake.Japanese;
            Assert.Same(greek, Keep(greek));
            Assert.Same(japanese, Keep(japanese));

            var once = TextRepair.FixMojibake(Mojibake.GreekBroken, TextRepair.Page(1252));
            Assert.Equal(once, TextRepair.FixMojibake(once, TextRepair.Page(1252)));
        }

        [Fact]
        public void Plain_ascii_empty_and_null_come_straight_back()
        {
            Assert.Same("Bjorn", Keep("Bjorn"));
            Assert.Same("", Keep(""));
            Assert.Null(TextRepair.FixMojibake(null, TextRepair.Page(1252)));
        }

        /// <summary>
        /// The one shape that is not mojibake but reads like it: a lone accented letter opens a
        /// multi-byte sequence with nothing behind it, so the bytes are not UTF-8 and the text
        /// is somebody's real name.
        /// </summary>
        [Theory]
        [InlineData(0x00E9)]              // e acute on its own
        [InlineData(0x00FC)]              // u umlaut on its own
        [InlineData(0x00C0)]              // A grave on its own
        public void A_lone_latin_1_letter_is_not_mistaken_for_a_broken_name(int code)
        {
            var name = "Ase" + Mojibake.Text(code) + "n";
            Assert.Same(name, Keep(name));
        }

        /// <summary>
        /// The repair is per string and knows nothing about what the string is for, so a
        /// character name and an account name go through the same door. Whitespace and
        /// punctuation around a broken name survive it.
        /// </summary>
        [Fact]
        public void The_text_around_a_broken_name_survives()
        {
            var said = "Kicked: " + Mojibake.GreekBroken + " (offline)";
            Assert.Equal("Kicked: " + Mojibake.Greek + " (offline)",
                TextRepair.FixMojibake(said, TextRepair.Page(1252)));
        }

        /// <summary>
        /// The same instance back, which is what "untouched" has to mean: a repair that
        /// rebuilt every string it was given would hide a broken rule behind an equal value.
        /// Windows-1252 by name, because that is the page the honest names above are honest
        /// ON: a machine reading them through another page is a different question.
        /// </summary>
        private static string Keep(string text) => TextRepair.FixMojibake(text, TextRepair.Page(1252));

        // ---- the page that did the damage is the host's, not ours -------------------

        /// <summary>
        /// The pages themselves, which are not in the framework until something registers them.
        /// If this came back null the repair would quietly stop working for every host outside
        /// the Western code page, and no other test here would notice.
        /// </summary>
        [Theory]
        [InlineData(1252)]
        [InlineData(1251)]
        [InlineData(932)]
        public void The_code_pages_are_there_to_be_asked_for(int codePage)
        {
            Assert.NotNull(TextRepair.Page(codePage));
        }

        /// <summary>
        /// The same name, broken on a Russian install rather than a Western one. Every character
        /// of it is different, which is the whole reason the page cannot be a constant: those
        /// hosts are exactly the ones the language packs were written for.
        /// </summary>
        [Fact]
        public void A_name_broken_on_a_russian_machine_comes_back_on_that_machine()
        {
            Assert.Equal(Mojibake.Cyrillic,
                TextRepair.FixMojibake(Mojibake.CyrillicBroken1251, TextRepair.Page(1251)));
        }

        /// <summary>
        /// And the same text on the wrong page is left alone rather than mangled a second time:
        /// it holds letters Windows-1252 cannot spell, so 1252 never wrote it.
        /// </summary>
        [Fact]
        public void A_name_broken_on_a_russian_machine_is_left_alone_on_a_western_one()
        {
            var broken = Mojibake.CyrillicBroken1251;
            Assert.Same(broken, TextRepair.FixMojibake(broken, TextRepair.Page(1252)));
        }

        /// <summary>
        /// THE ONE THAT MUST NOT BE GUESSED. A name broken on a Japanese install passes every
        /// test an undoing can run and still comes back as somebody else's name, because 932
        /// reads two bytes at a time and the byte it could not place was replaced on the way in.
        /// The repair refuses that whole class of page rather than the cases it can spot, because
        /// nothing in the stored text tells the two apart.
        /// </summary>
        [Fact]
        public void A_name_broken_on_a_japanese_machine_is_left_alone_rather_than_guessed_at()
        {
            var broken = Mojibake.KatakanaBroken932;
            var answer = TextRepair.FixMojibake(broken, TextRepair.Page(932));

            Assert.Same(broken, answer);
            Assert.NotEqual(Mojibake.KatakanaWrongUndo, answer);
        }

        /// <summary>
        /// An honest Cyrillic name on the machine of a host who writes in Cyrillic. Its own 1251
        /// bytes are not UTF-8, so there is nothing to put back and nothing is touched.
        /// </summary>
        [Fact]
        public void An_honest_cyrillic_name_on_a_russian_machine_is_left_exactly_as_it_is()
        {
            var name = Mojibake.Cyrillic;
            Assert.Same(name, TextRepair.FixMojibake(name, TextRepair.Page(1251)));
        }

        /// <summary>
        /// The accident, pinned rather than hidden. Two letters, and their 1251 bytes happen to
        /// spell a third letter in UTF-8, so this honest name is put back as a different one. It
        /// takes a name of exactly that shape to happen at all, the platform id rather than the
        /// name is what every lookup in the app matches on, and the alternative is leaving every
        /// genuinely broken name broken. If this ever fails because the rule grew tighter, that
        /// is an improvement and the expectation moves.
        /// </summary>
        [Fact]
        public void The_one_honest_name_the_rule_cannot_tell_from_a_broken_one()
        {
            Assert.Equal(Mojibake.CyrillicAccidentUndone,
                TextRepair.FixMojibake(Mojibake.CyrillicAccident, TextRepair.Page(1251)));
        }

        // ---- the other direction, which works on every page --------------------------

        /// <summary>
        /// Running the damage FORWARDS is exact everywhere, multi-byte pages included, because it
        /// repeats what happened instead of undoing it. It is how a stored name that can never be
        /// repaired is still known for the player standing in front of you.
        /// </summary>
        [Fact]
        public void The_damage_can_always_be_repeated_even_where_it_cannot_be_undone()
        {
            Assert.Equal(Mojibake.GreekBroken,
                TextRepair.Damage(Mojibake.Greek, TextRepair.Page(1252)));
            Assert.Equal(Mojibake.CyrillicBroken1251,
                TextRepair.Damage(Mojibake.Cyrillic, TextRepair.Page(1251)));
            Assert.Equal(Mojibake.KatakanaBroken932,
                TextRepair.Damage(Mojibake.Katakana, TextRepair.Page(932)));

            Assert.True(TextRepair.IsDamagedSpellingOf(
                Mojibake.KatakanaBroken932, Mojibake.Katakana, TextRepair.Page(932)));
            Assert.True(TextRepair.IsDamagedSpellingOf(
                Mojibake.GreekBroken, Mojibake.Greek, TextRepair.Page(1252)));
        }

        /// <summary>
        /// And it says no to everything else: a name that is already right, a different name, and
        /// plain ASCII, which no code page can damage and which must never read as damaged.
        /// </summary>
        [Fact]
        public void A_name_is_not_a_damaged_spelling_of_itself_or_of_anybody_else()
        {
            var page = TextRepair.Page(1252);

            Assert.False(TextRepair.IsDamagedSpellingOf(Mojibake.Greek, Mojibake.Greek, page));
            Assert.False(TextRepair.IsDamagedSpellingOf(Mojibake.GreekBroken, Mojibake.Cyrillic, page));
            Assert.False(TextRepair.IsDamagedSpellingOf("Bjorn", "Bjorn", page));
            Assert.False(TextRepair.IsDamagedSpellingOf("Bjorn", "Bjarne", page));
            Assert.False(TextRepair.IsDamagedSpellingOf(null, Mojibake.Greek, page));
            Assert.False(TextRepair.IsDamagedSpellingOf(Mojibake.GreekBroken, null, page));
        }

        /// <summary>
        /// The one argument call is the machine's own page, which is what every caller in the app
        /// uses. Said out loud so the default cannot quietly become something else.
        /// </summary>
        [Fact]
        public void The_machine_page_is_what_the_short_call_uses()
        {
            Assert.NotNull(TextRepair.AnsiPage);
            Assert.Equal(
                TextRepair.FixMojibake(Mojibake.GreekBroken, TextRepair.AnsiPage),
                TextRepair.FixMojibake(Mojibake.GreekBroken));
        }
    }
}
