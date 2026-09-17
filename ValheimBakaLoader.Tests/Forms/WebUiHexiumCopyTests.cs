using System;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// What the interface says about the second mod site, and where it is allowed to say
    /// it. Turning the switch on is the whole of the agreement, so the switch has to carry
    /// the whole of what it means; and a host who never turns it on should not read the
    /// site's name anywhere except on the switch itself.
    /// <para>
    /// These are gates on the interface's own source, because what they guard against is a
    /// line somebody trims later.
    /// </para>
    /// </summary>
    public class WebUiHexiumCopyTests
    {
        private static string AppJs() => AppSourceTree.Web("app.js");

        private static string Between(string source, string from, string to)
        {
            var start = source.IndexOf(from, StringComparison.Ordinal);
            if (start < 0) return "";
            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            return end < 0 ? source.Substring(start) : source.Substring(start, end - start);
        }

        // --- The switch says the whole of what it means ---

        [Fact]
        public void The_switch_text_names_every_thing_the_host_is_agreeing_to()
        {
            var help = Between(AppJs(), "const HEXIUM_HELP=", "function renderHexiumCopy");

            foreach (var (thing, phrase) in new[]
            {
                ("that it is a second mod site", "second mod site"),
                ("that nobody is named as running it", "not named on the site"),
                ("that its accounts are Discord sign-ins", "Discord sign-ins"),
                ("that an author there cannot be tied to the same name on Thunderstore", "same person as the author of the same name on Thunderstore"),
                ("how often the machine will contact the site", "about four times an hour"),
                ("how long the site keeps request logs", "ninety days"),
            })
            {
                Assert.True(help.Contains(phrase, StringComparison.Ordinal),
                    "the switch text no longer says " + thing);
            }
        }

        [Fact]
        public void The_switch_text_and_its_label_both_go_through_the_wording_pass()
        {
            var render = Between(AppJs(), "function renderHexiumCopy", "\n/*");

            Assert.Contains("TT(HEXIUM_SWITCH_LABEL)", render);
            Assert.Contains("TT(HEXIUM_HELP)", render);
        }

        // --- Nothing acts on the second site without being asked ---

        [Fact]
        public void The_dialog_asks_in_the_words_the_host_has_to_agree_to()
        {
            var dialog = Between(AppJs(), "function hexiumConsentModal", "async function hexiumInstallFlow");

            // It has to say whose it is not, and that nobody can vouch for who made it.
            Assert.Contains("not Thunderstore", dialog);
            Assert.Contains("cannot tell you who published it", dialog);
            // It has to say the folder will be replaced when there is one.
            Assert.Contains("will be replaced", dialog);
            // And the button has to be an acceptance rather than an OK.
            Assert.Contains("TT(\"I accept the risk, install\")", dialog);
        }

        [Fact]
        public void Turning_the_dialog_down_is_always_on_offer()
        {
            // confirmModal draws the second button for every dialog in the app, this one
            // included, and it reads as a refusal rather than as nothing.
            Assert.Contains("id=\"mCancel\">${esc(TT(\"Cancel\"))}", AppJs());
        }

        [Fact]
        public void The_install_call_carries_the_token_the_dialog_handed_back()
        {
            var install = Between(AppJs(), "async function doInstallFromHexium", "/* The way back.");

            Assert.Contains("mods.installFromHexium", install);
            Assert.Contains("token:pay.Token", install);
        }

        // --- A host who never turned it on reads the name in one place only ---

        [Fact]
        public void A_row_with_nothing_on_the_second_site_offers_no_menu_item_naming_it()
        {
            var menu = Between(AppJs(), "function modRowItems(mod)", "/* Update a single mod");

            // Left out, not greyed out. A disabled entry still reads its label out loud.
            Assert.Contains("if(onHexium)", menu);
            Assert.DoesNotContain("disabled:!onHexium", menu);

            // The two swap offers are each behind their own condition as well.
            Assert.Contains("if(mod.hexiumNewer&&mod.hexiumLatest)", menu);
            Assert.Contains("if(mod.thunderstoreNewer&&mod.LatestVersion)", menu);
        }

        [Fact]
        public void The_mark_on_the_latest_cell_only_appears_when_there_is_something_to_say()
        {
            var mark = Between(AppJs(), "function modLatestMark(m)", "/* The transient status");

            Assert.Contains("if(m.hexiumNewer&&m.hexiumLatest)", mark);
            Assert.Contains("if(m.thunderstoreNewer&&m.LatestVersion)", mark);
            Assert.Contains("return \"\";", mark);
        }

        [Fact]
        public void The_note_in_the_add_dialog_only_appears_with_the_switch_on()
        {
            var add = Between(AppJs(), "function addModFlow()", "async function doAddMod");

            Assert.Contains("const hexNote=S.hexium", add);
        }

        [Fact]
        public void The_refusal_when_the_switch_is_off_says_where_the_switch_is()
        {
            var refusal = Between(AppJs(), "function hexiumFailToast", "function hexiumConsentModal");

            Assert.Contains("r.Reason===\"sourceOff\"", refusal);
            Assert.Contains("TT(\"Turn on Also check Hexium in Upkeep to install from Hexium.\")", refusal);
        }

        // --- The switch is a real setting, saved with the rest of them ---

        [Fact]
        public void The_switch_is_read_back_and_written_with_every_other_setting()
        {
            var js = AppJs();

            Assert.Contains("setT(\"tUseHexium\",up.UseHexiumSource)", js);
            Assert.Contains("UseHexiumSource:T(\"tUseHexium\")", js);
            Assert.Contains("$(\"#tUseHexium\")?.addEventListener(\"click\"", js);
        }

        [Fact]
        public void The_switch_and_its_explanation_are_both_in_the_upkeep_card()
        {
            var html = AppSourceTree.Web("index.html");
            var upkeep = Between(html, "id=\"upkeepBody\"", "<!-- RECENT LOG");

            Assert.Contains("id=\"tUseHexium\"", upkeep);
            Assert.Contains("id=\"hexiumSwitchLabel\"", upkeep);
            Assert.Contains("id=\"hexiumHelp\"", upkeep);
        }
    }
}
