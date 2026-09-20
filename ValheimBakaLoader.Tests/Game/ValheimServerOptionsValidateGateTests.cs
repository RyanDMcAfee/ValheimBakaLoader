using System;
using System.Linq;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// <see cref="ValheimServerOptions.Validate"/> is documented as a gate and is called by
    /// nothing at all.
    /// <para>
    /// It was written as "the single gate every start path goes through" and the wiring never
    /// landed, so every rule inside it has been unreachable for as long as it has existed. The
    /// comment said otherwise, which is worse than no gate: a rule that reads as enforced and is
    /// not is a rule the next person builds on.
    /// </para>
    /// <para>
    /// 1.2.1 does NOT wire it in, on purpose. Several of these rules would start refusing
    /// launches that work today, and a host whose profile has booted for a year would meet a
    /// hard refusal at the Start button on the strength of a rule nobody has ever seen enforced.
    /// So the comment is made true instead, and this names every check that is unreachable, so
    /// the fact is visible in the suite rather than resting on a comment that could drift again.
    /// </para>
    /// </summary>
    public class ValheimServerOptionsValidateGateTests
    {
        /// <summary>
        /// Every rule inside Validate, by the sentence a host would read if it ever ran. None of
        /// them can be reached from any start path. When one of these is wired up in 1.2.2, its
        /// line comes out of here and a test that DRIVES it goes in.
        /// </summary>
        public static readonly string[] UnreachableChecks =
        {
            "Give the server a name.",
            "Give the world a name.",
            "The server name and the world name must differ",
            "Community (public) servers require a password.",
            "The password needs at least 5 characters.",
            "The password may not contain the server name",
            "The password may not contain the world name",
            "The game port must be between 1 and 65535.",
            "The RCON port must be between 1 and 65535.",
            "The RCON port must differ from the game port",
            "The empty-server restart delay needs to be at least 1 minute.",
            "The scheduled restart interval needs to be at least 1 hour.",
            "The save interval must be greater than zero.",
            "The short backup interval must be greater than zero.",
            "The long backup interval must be greater than zero.",
            "The save interval cannot exceed either backup interval.",
            "The short backup interval cannot exceed the long one.",
            "is not a world preset",
            "World modifiers cannot be combined with a world preset.",
            "is not a world modifier",
            "is not a valid setting for the",
            "is not a world key",
            "The '-logFile' server argument is not supported.",
        };

        /// <summary>
        /// The fact itself: nothing in the app calls it. The search is over the whole app source
        /// rather than over one file, because a gate that only checked the file it lives in
        /// would be answered by a call from anywhere else.
        /// </summary>
        [Fact]
        public void Nothing_in_the_app_calls_the_gate()
        {
            // The declaration itself is not a call, and it is the one place "void Validate()"
            // is written, so it is the one shape excluded.
            var callers = AppSourceTree.Files()
                .Where(file => Regex.IsMatch(file.Value, @"(?<![A-Za-z0-9_])(?<!void )Validate\(\s*\)"))
                .Select(file => file.Key)
                .ToList();

            Assert.True(callers.Count == 0,
                "Validate() is called from: " + string.Join(", ", callers)
                + ". If that is deliberate, this test and the comment on Validate both need rewriting.");
        }

        /// <summary>Every rule named above really is in there, so the list cannot rot quietly.</summary>
        [Theory]
        [MemberData(nameof(EveryUnreachableCheck))]
        public void Every_named_check_is_really_in_the_gate(string sentence)
            => Assert.Contains(sentence, AppSourceTree.Files()["ValheimServerOptions.cs"], StringComparison.Ordinal);

        public static TheoryData<string> EveryUnreachableCheck()
        {
            var data = new TheoryData<string>();
            foreach (var check in UnreachableChecks) data.Add(check);
            return data;
        }

        /// <summary>
        /// And the other way round: a rule ADDED to the gate later has to be named here too, or
        /// the list stops being the whole truth about what is unreachable. Counted by the
        /// Require calls, which is one per rule.
        /// </summary>
        [Fact]
        public void The_list_covers_every_rule_the_gate_holds()
        {
            var source = AppSourceTree.Files()["ValheimServerOptions.cs"];
            // Call sites only: the one "void Require(" is the helper they all go through.
            var rules = Regex.Matches(source, @"(?<!void )Require\(").Count;

            Assert.True(rules == UnreachableChecks.Length,
                $"the gate holds {rules} rule(s) and this test names {UnreachableChecks.Length}. "
                + "A rule added to Validate has to be named here, or wired up and driven by a test of its own.");
        }

        /// <summary>
        /// The comment is the thing that was false. It has to say so in as many words, because
        /// the next person to read it is the one who would otherwise trust it.
        /// </summary>
        [Fact]
        public void The_comment_says_that_nothing_calls_it()
        {
            var source = AppSourceTree.Files()["ValheimServerOptions.cs"];

            Assert.Contains("NOTHING CALLS IT.", source, StringComparison.Ordinal);
            Assert.DoesNotContain("The single gate every start path goes through", source, StringComparison.Ordinal);
            Assert.Contains("1.2.2:", source, StringComparison.Ordinal);
        }

        /// <summary>
        /// While the gate is unreachable, the bridge's own two parsers are the whole of what
        /// guards a world's generation settings, so they have to be reachable and they have to
        /// refuse. Driven rather than asserted about.
        /// </summary>
        [Fact]
        public void The_reachable_guard_on_keys_is_the_one_in_the_bridge()
        {
            // it rejects a name that is not a switch ...
            Assert.Throws<ValheimBakaLoader.Tools.HostFacingException>(
                () => ValheimBakaLoader.Forms.BlendWindow.ParseWorldKeys(
                    Newtonsoft.Json.Linq.JArray.FromObject(new[] { "nocraftcost" })));

            // ... and rejects nothing the pass-through has to keep, because a stored key never
            // travels through it: the merge carries it.
            var kept = ValheimBakaLoader.Forms.BlendWindow.MergeWorldKeys(
                new[] { "carryweightrate 150" },
                ValheimBakaLoader.Forms.BlendWindow.ParseWorldKeys(
                    Newtonsoft.Json.Linq.JArray.FromObject(new[] { "nobuildcost" })));

            Assert.Contains("carryweightrate 150", kept);
        }
    }
}
