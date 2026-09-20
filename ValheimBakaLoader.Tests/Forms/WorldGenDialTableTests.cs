using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The one table of dial key sets, held against the fine print the page draws from its own
    /// copy of it.
    /// <para>
    /// There are two readers of "what keys does Combat on Hard write": the C# importer, which
    /// uses them to read a world's own settings back out of its header, and the page, which
    /// prints them under each dial so a host can match a BakaLoader world against the game's own
    /// world-modifier menu. Two lists of the same facts drift, and the drift is invisible: the
    /// page would go on printing the old set while the importer quietly stopped recognising a
    /// world. So the page's copy is parsed here and held against
    /// <see cref="WorldGen.DialKeys"/>, cell by cell.
    /// </para>
    /// </summary>
    public class WorldGenDialTableTests
    {
        /// <summary>
        /// The page's table, read out of app.js: dial, then value, then the keys its "effects"
        /// line names. "no keys set" is Normal, which writes nothing and is not in the C# table.
        /// </summary>
        private static Dictionary<string, Dictionary<string, string[]>> PageTable()
        {
            var js = AppSourceTree.Web("app.js");

            var start = js.IndexOf("const WORLDGEN_HELP={", StringComparison.Ordinal);
            Assert.True(start > 0, "the page's world-generation table is gone");
            var end = js.IndexOf("\n};", start, StringComparison.Ordinal);
            Assert.True(end > start, "the page's world-generation table does not end");
            var block = js.Substring(start, end - start);

            var table = new Dictionary<string, Dictionary<string, string[]>>(StringComparer.Ordinal);
            string dial = null;
            string value = null;

            foreach (var line in block.Split('\n'))
            {
                var dialAt = Regex.Match(line, @"^\s{2}([a-z]+):\{sel:");
                if (dialAt.Success)
                {
                    dial = dialAt.Groups[1].Value;
                    value = null;
                    table[dial] = new Dictionary<string, string[]>(StringComparer.Ordinal);
                    continue;
                }

                var valueAt = Regex.Match(line, @"^\s*\{v:""([a-z]*)""");
                if (valueAt.Success && dial != null)
                {
                    value = valueAt.Groups[1].Value;
                    continue;
                }

                var effectsAt = Regex.Match(line, @"^\s*effects:""([^""]*)""");
                if (!effectsAt.Success || dial == null || value == null) continue;

                var effects = effectsAt.Groups[1].Value;
                table[dial][value] = effects == "no keys set"
                    ? Array.Empty<string>()
                    : effects.Split(',').Select(part => part.Trim()).ToArray();
                value = null;
            }

            Assert.NotEmpty(table);
            return table;
        }

        [Fact]
        public void The_page_names_the_same_five_dials_the_table_does()
        {
            var page = PageTable();

            Assert.Equal(
                WorldGen.DialKeys.Keys.OrderBy(k => k, StringComparer.Ordinal),
                page.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }

        /// <summary>
        /// The gate itself: every option on the page prints exactly the keys the C# table holds
        /// for it, and Normal prints "no keys set" because the table deliberately holds none.
        /// </summary>
        [Fact]
        public void Every_option_the_page_prints_matches_the_table_key_for_key()
        {
            var page = PageTable();
            var wrong = new List<string>();

            foreach (var (dial, options) in page)
            {
                if (!WorldGen.DialKeys.TryGetValue(dial, out var table))
                {
                    wrong.Add(dial + ": the page draws a dial the table does not hold");
                    continue;
                }

                foreach (var (value, keys) in options)
                {
                    // "" is Normal: the game default, which writes no keys at all.
                    if (value.Length == 0)
                    {
                        if (keys.Length != 0) wrong.Add(dial + " Normal prints keys, and Normal writes none");
                        continue;
                    }

                    if (!table.TryGetValue(value, out var expected))
                    {
                        wrong.Add($"{dial}={value}: the page draws an option the table does not hold");
                        continue;
                    }

                    if (!expected.SequenceEqual(keys, StringComparer.Ordinal))
                    {
                        wrong.Add($"{dial}={value}: the page prints [{string.Join(", ", keys)}] "
                            + $"and the table holds [{string.Join(", ", expected)}]");
                    }
                }

                foreach (var value in table.Keys.Where(v => !options.ContainsKey(v)))
                    wrong.Add($"{dial}={value}: the table holds an option the page never prints");
            }

            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        /// <summary>
        /// The table and the vocabulary the command line is built from have to name the same
        /// values, or a dial could be read back off a header and then refused on its way out.
        /// </summary>
        [Fact]
        public void Every_value_in_the_table_is_a_value_the_game_accepts()
        {
            foreach (var (dial, options) in WorldGen.DialKeys)
            {
                Assert.True(WorldGen.Modifiers.ContainsKey(dial), dial + " is not a world modifier");

                foreach (var value in options.Keys)
                {
                    Assert.True(WorldGen.Modifiers[dial].Contains(value),
                        $"'{value}' is not a value the game accepts for {dial}");
                }

                foreach (var value in WorldGen.Modifiers[dial])
                {
                    Assert.True(options.ContainsKey(value),
                        $"{dial}={value} is a value the game accepts and the table holds no keys for it");
                }
            }
        }

        /// <summary>
        /// A key is spelled the way the game writes it into a header: lower case, and a value
        /// key as one string with a single space in it. Anything else would be read back wrong
        /// and would reach the command line wrong.
        /// </summary>
        [Fact]
        public void Every_key_in_the_table_is_spelled_the_way_the_game_writes_it()
        {
            foreach (var (dial, options) in WorldGen.DialKeys)
            {
                foreach (var (value, keys) in options)
                {
                    Assert.NotEmpty(keys);

                    foreach (var key in keys)
                    {
                        Assert.True(Regex.IsMatch(key, "^[a-z]+( [0-9]+)?$"),
                            $"{dial}={value} holds '{key}', which is not how the game spells a key");
                    }
                }
            }
        }

        /// <summary>
        /// No dial may write a key that is also one of the five switches. The reading takes the
        /// dials first and the switches from what is left, so an overlap would make one of them
        /// invisible.
        /// </summary>
        [Fact]
        public void No_dial_writes_a_key_that_is_also_a_switch()
        {
            foreach (var (dial, options) in WorldGen.DialKeys)
            {
                foreach (var (value, keys) in options)
                {
                    foreach (var key in keys)
                    {
                        Assert.False(WorldGen.IsSwitch(key),
                            $"{dial}={value} writes '{key}', which is also a switch");
                    }
                }
            }
        }
    }
}
