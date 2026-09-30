using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using UncheatPlan = BakaLoaderUncheatPlan.UncheatPlan;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The client companion's sweep: one loop, and it is the one the suite drives.
    /// <para>
    /// WHY THIS EXISTS. <c>UncheatPlan.ClearAll</c> was tested here and had no caller anywhere:
    /// the plugin hand-rolled the same walk over the game's own <c>ItemDrop.ItemData</c>, with
    /// its own two counters. Two loops, one of them tested. A rule anybody changed in the plan
    /// would have gone on passing while the shipped sweep went on doing whatever it did, which
    /// is the one way a sweep can be green everywhere and clear nothing on a player's machine.
    /// </para>
    /// </summary>
    public class UncheatShippedCodeTests
    {
        /// <summary>An item shaped like the game's, to drive the generic sweep the plugin uses.</summary>
        private sealed class FakeItem
        {
            public FakeItem(string name, bool cheated) { Name = name; Cheated = cheated; }

            public string Name { get; }

            public bool Cheated { get; set; }
        }

        private static int Sweep(IEnumerable<FakeItem> items, out int looked)
            => UncheatPlan.ClearAll(items, i => i.Cheated, i => i.Cheated = false, out looked);

        [Fact]
        public void The_sweep_clears_the_marked_ones_and_counts_everything_it_looked_at()
        {
            var items = new List<FakeItem>
            {
                new("SwordIron", true),
                new("Wood", false),
                null,
                new("Coins", true),
                new("Feather", false),
            };

            int looked;
            var cleared = Sweep(items, out looked);

            Assert.Equal(2, cleared);
            Assert.Equal(4, looked);                        // the null is not an item
            Assert.All(items.Where(i => i != null), i => Assert.False(i.Cheated));
        }

        /// <summary>
        /// "Looked at 41 and cleared none" and "there was nothing to look at" are different
        /// facts, and the one line the plugin writes tells them apart off these two numbers.
        /// </summary>
        [Fact]
        public void An_empty_inventory_and_a_clean_one_are_different_answers()
        {
            int lookedEmpty;
            Assert.Equal(0, Sweep(new List<FakeItem>(), out lookedEmpty));
            Assert.Equal(0, lookedEmpty);

            int lookedClean;
            Assert.Equal(0, Sweep(new List<FakeItem> { new("Wood", false) }, out lookedClean));
            Assert.Equal(1, lookedClean);

            Assert.NotEqual(UncheatPlan.Reply(0, 0, 1), UncheatPlan.Reply(0, 1, 1));
        }

        [Fact]
        public void Nothing_to_sweep_is_not_a_throw()
        {
            int looked;
            Assert.Equal(0, UncheatPlan.ClearAll<FakeItem>(null, i => i.Cheated, i => i.Cheated = false, out looked));
            Assert.Equal(0, looked);
        }

        /// <summary>
        /// And the PLUGIN really goes through it. A source gate, because the half that names a
        /// Valheim type is behind a VALHEIM_PLUGIN fence and is never compiled into the suite:
        /// what it holds is that the shipped loop is the tested loop.
        /// </summary>
        [Fact]
        public void The_plugin_calls_the_plan_rather_than_walking_the_items_itself()
        {
            var plugin = AppSourceTree.Read(
                "ValheimBakaLoader", "Resources", "Uncheat", "BakaLoaderUncheat.cs");

            Assert.Contains("UncheatPlan.ClearAll(", plugin, StringComparison.Ordinal);
            Assert.Contains("item => item.m_cheated, item => item.m_cheated = false, out looked",
                plugin, StringComparison.Ordinal);

            // And the loop it used to carry is gone, counters and all.
            Assert.DoesNotContain("item.m_cheated = false;", plugin, StringComparison.Ordinal);
            Assert.DoesNotContain("looked++;", plugin, StringComparison.Ordinal);
        }
    }
}
