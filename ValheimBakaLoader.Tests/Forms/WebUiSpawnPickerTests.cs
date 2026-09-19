using System;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The spawn picker keeps what the host put into it across a language switch.
    /// <para>
    /// A dialog is written into the page once and never drawn again, so the language
    /// switch rebuilds it by calling the rebuilder its opener handed to modalOpen. The
    /// picker's opener used to hand in a fresh <c>()=&gt;openSpawnModal(p)</c>, which
    /// starts the whole function over: the search text, the item that had been picked
    /// and the amount all lived in the closure being replaced, so a switch reopened a
    /// dialog aimed at the right viking and holding nothing else. Finding an item in a
    /// list of every prefab in the game is the expensive half of this dialog.
    /// </para>
    /// <para>
    /// The browser probe is what proves the behaviour; this is the source gate that
    /// keeps the shape from being written back the way it was. It reads app.js, because
    /// what it guards against is a line somebody adds later.
    /// </para>
    /// </summary>
    public class WebUiSpawnPickerTests
    {
        private static string AppJs() => AppSourceTree.Web("app.js");

        /// <summary>The whole of openSpawnModal, from its declaration to the next one.</summary>
        private static string Picker()
        {
            var source = AppJs();
            var start = source.IndexOf("function openSpawnModal(", StringComparison.Ordinal);
            Assert.True(start >= 0, "app.js no longer declares openSpawnModal");
            var end = source.IndexOf("\n/* ---------- WORLD (profile config)", start, StringComparison.Ordinal);
            Assert.True(end > start, "openSpawnModal is no longer followed by the World hall");
            return source.Substring(start, end - start);
        }

        /// <summary>Where the rebuilder begins. Everything the switch must not lose is above it.</summary>
        private static int RebuilderAt(string picker)
        {
            var at = picker.IndexOf("const again=()=>{", StringComparison.Ordinal);
            Assert.True(at > 0, "openSpawnModal has no rebuilder to hand to modalOpen");
            return at;
        }

        [Fact]
        public void The_picker_hands_modalOpen_its_own_rebuilder_and_not_a_fresh_opener()
        {
            var picker = Picker();

            // The shape that lost everything: the rebuilder called the opener again from
            // the top, so every let in the opener was built afresh.
            Assert.DoesNotContain("()=>openSpawnModal(p)", picker, StringComparison.Ordinal);

            // modalOpen is handed the rebuilder by name, and the rebuilder is what runs.
            Assert.Contains("      again);", picker, StringComparison.Ordinal);
            Assert.Contains("return again();", picker, StringComparison.Ordinal);
        }

        [Fact]
        public void The_search_text_and_the_picked_item_live_above_the_rebuilder()
        {
            var picker = Picker();
            var rebuilder = RebuilderAt(picker);
            var held = picker.Substring(0, rebuilder);

            // The one line that holds the lot, and it is OUTSIDE the part being replaced.
            Assert.Contains("let query=\"\", picked=null", held, StringComparison.Ordinal);

            // Nothing may re-declare them inside the rebuilder, which would shadow the
            // state held out here and lose it again while every other line still reads
            // as though it had been kept.
            var rebuilt = picker.Substring(rebuilder);
            Assert.DoesNotContain("let query", rebuilt, StringComparison.Ordinal);
            Assert.DoesNotContain("let picked", rebuilt, StringComparison.Ordinal);
            Assert.DoesNotContain("let sel", rebuilt, StringComparison.Ordinal);
        }

        [Fact]
        public void The_rebuilt_dialog_puts_the_boxes_back_and_marks_the_row_again()
        {
            var picker = Picker();
            var rebuilt = picker.Substring(RebuilderAt(picker));

            // The boxes, put back from the held state rather than left at the markup's.
            Assert.Contains("q.value=query;", rebuilt, StringComparison.Ordinal);
            Assert.Contains("amt.value=amountText;", rebuilt, StringComparison.Ordinal);
            Assert.Contains("lq.value=gradeText;", rebuilt, StringComparison.Ordinal);

            // The list is fetched again on every rebuild, so the row the picked item sits
            // on has to be found and marked again once the answer lands.
            Assert.Contains("markPicked();", rebuilt, StringComparison.Ordinal);
            Assert.Contains("res.findIndex(it=>it&&it.PrefabName===picked.PrefabName)",
                rebuilt, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_box_writes_its_own_keystroke_back_into_the_held_state()
        {
            var picker = Picker();

            // A box that is only ever read on the press keeps nothing: the value has to
            // travel out to the held state as it is typed, the way promptModal does it.
            Assert.Contains("q.addEventListener(\"input\",()=>{query=q.value;", picker, StringComparison.Ordinal);
            Assert.Contains("amt.addEventListener(\"input\",()=>{amountText=amt.value;});", picker, StringComparison.Ordinal);
            Assert.Contains("lq.addEventListener(\"input\",()=>{gradeText=lq.value;});", picker, StringComparison.Ordinal);

            // And the search asks for the held text, not for whatever is in the box that
            // this particular build of the dialog happens to own.
            Assert.Contains("rpc(\"items.search\",{query:query.trim(),limit:100})", picker, StringComparison.Ordinal);
            Assert.DoesNotContain("query:q.value.trim()", picker, StringComparison.Ordinal);
        }

        [Fact]
        public void The_spawn_press_acts_on_the_held_item()
        {
            var picker = Picker();

            Assert.Contains("if(!picked) return;", picker, StringComparison.Ordinal);
            Assert.Contains("const item=picked;", picker, StringComparison.Ordinal);
        }

        /// <summary>
        /// The comment above MODAL_REDRAW used to name this dialog as the one that loses
        /// something real. It does not any more, and a note that describes a defect which
        /// has been fixed is worse than no note: the next reader believes it.
        /// </summary>
        [Fact]
        public void The_note_above_the_rebuilder_no_longer_says_the_picker_loses_its_search()
        {
            var source = AppJs();
            var start = source.IndexOf("/* What would build the open dialog AGAIN", StringComparison.Ordinal);
            Assert.True(start >= 0, "app.js no longer explains what a rebuilder is for");
            var end = source.IndexOf("let MODAL_REDRAW=null;", start, StringComparison.Ordinal);
            Assert.True(end > start, "the note is no longer attached to MODAL_REDRAW");
            var note = source.Substring(start, end - start);

            Assert.Contains("used to be the one that lost something real", note, StringComparison.Ordinal);
            Assert.DoesNotContain("it reopens with the player it", note, StringComparison.Ordinal);
        }
    }
}
