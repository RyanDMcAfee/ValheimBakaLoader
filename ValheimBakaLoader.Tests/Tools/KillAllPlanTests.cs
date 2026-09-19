using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using BakaLoaderKillAll;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The companion plugins compile against the game's own assemblies, so nothing in this
    /// solution can call into them. KillAllPlan is the deliberate exception: it is the half of
    /// baka_killall that touches no game type at all, it compiles into BakaLoader itself
    /// through the ordinary source glob, and so the argument parsing, the spare rule and the
    /// reply text can be held to account on every build, on any machine, with no dedicated
    /// server installed.
    /// <para>
    /// The other half, KillAllSweep, can only be exercised against a running server. What can
    /// be pinned about it lives in CompanionPluginSourceTests, and the stopped-window walk is
    /// in Resources\Commander\CLOSED-TEST-CHECKLIST.md.
    /// </para>
    /// </summary>
    public class KillAllPlanTests
    {
        private static string[] Tokens(string line) =>
            line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        // ------------------------------------------------------------------
        //  Arguments
        // ------------------------------------------------------------------

        [Fact]
        public void BareCommandMeansEveryHostile()
        {
            var request = KillAllPlan.Parse(Tokens("baka_killall"));

            Assert.True(request.IsValid);
            Assert.Equal(KillAllScope.Everything, request.Scope);
        }

        /// <summary>
        /// Commander hands over a line it split itself and the game's Terminal hands over one
        /// it split on every space including the doubled ones. Neither may turn into a usage
        /// error, and a caller with nothing at all must still get a runnable request rather
        /// than a null reference.
        /// </summary>
        [Fact]
        public void NoArgumentsAtAllIsStillTheWholeWorld()
        {
            foreach (var tokens in new[] { null, new string[0], new[] { "baka_killall" } })
            {
                var request = KillAllPlan.Parse(tokens);
                Assert.True(request.IsValid);
                Assert.Equal(KillAllScope.Everything, request.Scope);
            }
        }

        [Fact]
        public void OneWordMeansOnePrefab()
        {
            var request = KillAllPlan.Parse(Tokens("baka_killall Eikthyr"));

            Assert.True(request.IsValid);
            Assert.Equal(KillAllScope.OnePrefab, request.Scope);
            Assert.Equal("Eikthyr", request.PrefabName);
        }

        /// <summary>A prefab name has no spaces, so a second word is a typo, not a guess to make.</summary>
        [Fact]
        public void TwoWordsWhereAPrefabBelongsIsAUsageError()
        {
            var request = KillAllPlan.Parse(Tokens("baka_killall Eikthyr please"));

            Assert.False(request.IsValid);
            Assert.Equal(KillAllPlan.Usage, request.Error);
        }

        [Theory]
        [InlineData("baka_killall near Mithi 50", "Mithi", 50f)]
        [InlineData("baka_killall NEAR Mithi 50", "Mithi", 50f)]
        [InlineData("baka_killall near Mithi 12.5", "Mithi", 12.5f)]
        public void NearTakesAPlayerAndARadius(string line, string player, float radius)
        {
            var request = KillAllPlan.Parse(Tokens(line));

            Assert.True(request.IsValid);
            Assert.Equal(KillAllScope.NearPlayer, request.Scope);
            Assert.Equal(player, request.PlayerName);
            Assert.Equal(radius, request.Radius);
        }

        /// <summary>
        /// Valheim names hold spaces, so the radius is taken off the end and everything
        /// between the keyword and it is the name. Taking the name off the front instead
        /// would have aimed a sweep at "Old" and left "Man Hrafn 50" unread.
        /// </summary>
        [Fact]
        public void APlayerNameMayHoldSpaces()
        {
            var request = KillAllPlan.Parse(Tokens("baka_killall near Old Man Hrafn 40"));

            Assert.True(request.IsValid);
            Assert.Equal("Old Man Hrafn", request.PlayerName);
            Assert.Equal(40f, request.Radius);
        }

        [Theory]
        [InlineData("baka_killall near")]
        [InlineData("baka_killall near Mithi")]
        public void NearWithoutBothHalvesIsAUsageError(string line)
        {
            var request = KillAllPlan.Parse(Tokens(line));

            Assert.False(request.IsValid);
            Assert.Equal(KillAllPlan.Usage, request.Error);
        }

        [Theory]
        [InlineData("baka_killall near Mithi soon")]
        [InlineData("baka_killall near Mithi 0")]
        [InlineData("baka_killall near Mithi -20")]
        public void ARadiusThatIsNotAPositiveNumberIsRefused(string line)
        {
            var request = KillAllPlan.Parse(Tokens(line));

            Assert.False(request.IsValid);
            Assert.Equal(KillAllPlan.BadRadius, request.Error);
        }

        /// <summary>
        /// The radius comes off a wire, not off a keyboard in this machine's language. Parsing
        /// it with the running culture would read "12.5" as 125 on a German host and clear ten
        /// times the ground the host asked for.
        /// </summary>
        [Fact]
        public void TheRadiusIsReadTheSameWayInEveryLanguage()
        {
            var was = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

                var request = KillAllPlan.Parse(Tokens("baka_killall near Mithi 12.5"));

                Assert.True(request.IsValid);
                Assert.Equal(12.5f, request.Radius);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = was;
            }
        }

        // ------------------------------------------------------------------
        //  The spare rule
        // ------------------------------------------------------------------

        /// <summary>The factions a sweep must never touch, whatever else is true of the creature.</summary>
        private static readonly KillAllFaction[] Spared =
        {
            KillAllFaction.Players,
            KillAllFaction.AnimalsVeg,
            KillAllFaction.Dverger,
            KillAllFaction.PlayerSpawned,
            KillAllFaction.TrainingDummy,
            KillAllFaction.Unknown,
        };

        /// <summary>The factions a sweep is for.</summary>
        private static readonly KillAllFaction[] Hostile =
        {
            KillAllFaction.ForestMonsters,
            KillAllFaction.Undead,
            KillAllFaction.Demon,
            KillAllFaction.MountainMonsters,
            KillAllFaction.SeaMonsters,
            KillAllFaction.PlainsMonsters,
            KillAllFaction.Boss,
            KillAllFaction.MistlandsMonsters,
            KillAllFaction.DeepNorth,
        };

        [Fact]
        public void TheFriendlyFactionsAreSpared()
        {
            foreach (var faction in Spared)
                Assert.True(KillAllPlan.ShouldSpare(false, false, faction),
                    faction + " is a faction a sweep must never touch");
        }

        [Fact]
        public void TheHostileFactionsAreKillable()
        {
            foreach (var faction in Hostile)
                Assert.False(KillAllPlan.ShouldSpare(false, false, faction),
                    faction + " is what baka_killall is for");
        }

        /// <summary>
        /// A boss is a hostile like any other. It is summoned on purpose and it is the one
        /// thing a host most often needs cleared, so it must not drift into the spared list on
        /// the argument that it is special.
        /// </summary>
        [Fact]
        public void ABossIsKillable()
        {
            Assert.False(KillAllPlan.ShouldSpare(false, false, KillAllFaction.Boss));
        }

        /// <summary>
        /// The rule table above has to cover the whole mirror, or a faction added to it later
        /// gets whatever the switch happens to fall through to and nobody finds out.
        /// </summary>
        [Fact]
        public void EveryFactionInTheMirrorHasARow()
        {
            var covered = new HashSet<KillAllFaction>(Spared.Concat(Hostile));

            foreach (KillAllFaction faction in Enum.GetValues(typeof(KillAllFaction)))
                Assert.True(covered.Contains(faction),
                    faction + " is in KillAllFaction but neither table in this file says what " +
                    "a sweep should do with it");
        }

        /// <summary>
        /// A tamed wolf is still a ForestMonsters wolf: its faction never changes when a
        /// player feeds it. Reading the faction alone is what once cost an operator every
        /// tamed wolf, boar and lox on the server.
        /// </summary>
        [Fact]
        public void ATamedCreatureIsSparedWhateverItsFactionIs()
        {
            foreach (var faction in Hostile)
                Assert.True(KillAllPlan.ShouldSpare(false, true, faction),
                    "a tamed " + faction + " is somebody's pet");
        }

        [Fact]
        public void APlayerIsSparedWhateverElseIsTrue()
        {
            foreach (var faction in Hostile)
                Assert.True(KillAllPlan.ShouldSpare(true, false, faction));
        }

        /// <summary>
        /// A faction the game grows and this plugin has not met is spared rather than killed.
        /// A hostile that survives is something the host can see and report; a companion the
        /// sweep deleted is gone from the save with nobody told.
        /// </summary>
        [Fact]
        public void AFactionThisPluginHasNotMetIsLeftStanding()
        {
            Assert.True(KillAllPlan.ShouldSpare(false, false, KillAllFaction.Unknown));
        }

        // ------------------------------------------------------------------
        //  The reply
        // ------------------------------------------------------------------

        /// <summary>
        /// The first words are the contract. Anything reading these replies matches on them,
        /// and the reply carried "KillAll complete" before the sweep was rewritten.
        /// </summary>
        [Fact]
        public void TheReplyStillOpensWithKillAllComplete()
        {
            Assert.StartsWith("KillAll complete",
                KillAllPlan.Reply(0, 0, 0, KillAllNote.None, "", 0), StringComparison.Ordinal);
        }

        [Fact]
        public void TheReplyCarriesAllThreeCounts()
        {
            Assert.Equal(
                "KillAll complete: 3 hostiles slain, 1 out of reach, 12 spared (players, pets & allies)",
                KillAllPlan.Reply(3, 1, 12, KillAllNote.None, "", 0));
        }

        /// <summary>
        /// Out of reach is its own number. Folding it into the kills is the lie that hid the
        /// bug: the command reported a tidy count while the world was full of monsters.
        /// </summary>
        [Fact]
        public void OutOfReachIsReportedSeparatelyFromTheKills()
        {
            var reply = KillAllPlan.Reply(0, 7, 0, KillAllNote.None, "", 0);

            Assert.Contains("0 hostiles slain", reply);
            Assert.Contains("7 out of reach", reply);
        }

        [Fact]
        public void OneOfSomethingReadsAsOne()
        {
            Assert.Contains("1 hostile slain", KillAllPlan.Reply(1, 0, 0, KillAllNote.None, "", 0));
        }

        [Fact]
        public void ANameThisGameDoesNotHaveIsSaidPlainly()
        {
            var reply = KillAllPlan.Reply(0, 0, 0, KillAllNote.NoSuchPrefab, "Eikthyyr", 0);

            Assert.StartsWith("KillAll complete", reply, StringComparison.Ordinal);
            Assert.Contains("There is no creature called 'Eikthyyr' in this game.", reply);
        }

        [Fact]
        public void ANamedCreatureThatIsNowhereInTheWorldIsSaidPlainly()
        {
            var reply = KillAllPlan.Reply(0, 0, 0, KillAllNote.NoneInWorld, "Eikthyr", 0);

            Assert.Contains("No Eikthyr is in the world right now.", reply);
        }

        [Fact]
        public void ANamedCreatureThatIsNeverATargetIsSaidPlainly()
        {
            var reply = KillAllPlan.Reply(0, 0, 0, KillAllNote.PrefabIsNotHostile, "Deer", 0);

            Assert.Contains("Deer is not a hostile creature", reply);
        }

        [Fact]
        public void AnEmptyRadiusIsSaidPlainly()
        {
            var reply = KillAllPlan.Reply(0, 0, 4, KillAllNote.NoneInRange, "Mithi", 0);

            Assert.Contains("Nothing hostile was standing inside that radius around Mithi.", reply);
        }

        [Fact]
        public void AnOrdinaryRunCarriesNoExtraSentence()
        {
            Assert.DoesNotContain(".", KillAllPlan.Reply(2, 0, 3, KillAllNote.None, "", 0));
        }

        [Theory]
        [InlineData(1, "One of them belongs to a faction this KillAll does not know yet, so it was left standing.")]
        [InlineData(3, "3 of them belong to a faction this KillAll does not know yet, so they were left standing.")]
        public void AnUnknownFactionIsReportedRatherThanSwallowed(int count, string expected)
        {
            Assert.Contains(expected, KillAllPlan.Reply(0, 0, count, KillAllNote.None, "", count));
        }

        [Fact]
        public void NothingIsSaidAboutUnknownFactionsWhenThereWereNone()
        {
            Assert.DoesNotContain("does not know yet", KillAllPlan.Reply(5, 0, 2, KillAllNote.None, "", 0));
        }

        /// <summary>
        /// Every sentence this file can produce is copy a host reads, so it answers to the
        /// same two rules as the rest of the product: no dash characters standing in for
        /// punctuation, and no " - " doing the work a comma or a full stop should do.
        /// </summary>
        [Fact]
        public void EverySentenceObeysTheCopyRules()
        {
            var sentences = new List<string>
            {
                KillAllPlan.Usage,
                KillAllPlan.BadRadius,
                KillAllPlan.UnknownFactionSentence(1),
                KillAllPlan.UnknownFactionSentence(4),
                KillAllPlan.Counts(1, 1, 1),
                KillAllPlan.Started(1),
                KillAllPlan.Started(9),
                KillAllPlan.AlreadyRunning(1),
                KillAllPlan.AlreadyRunning(9),
                KillAllPlan.StoppedEarly(1, 1, 1, "the index went away"),
                KillAllPlan.StoppedEarly(0, 0, 0, null),
            };

            foreach (KillAllNote note in Enum.GetValues(typeof(KillAllNote)))
            {
                sentences.Add(KillAllPlan.NoteSentence(note, "Eikthyr"));
                sentences.Add(KillAllPlan.Reply(1, 1, 1, note, "Eikthyr", 2));
            }

            foreach (var sentence in sentences)
            {
                Assert.DoesNotContain(" - ", sentence);

                // Figure, en, em and horizontal bar, written as code points so this file
                // does not itself carry the characters it is here to keep out.
                foreach (var dash in new[] { 0x2012, 0x2013, 0x2014, 0x2015 })
                    Assert.DoesNotContain(((char)dash).ToString(), sentence);
            }
        }

        /// <summary>
        /// A note with no subject must not print an empty quote or a stray double space. The
        /// radius form can reach here with a peer whose name never arrived.
        /// </summary>
        [Fact]
        public void ANoteWithNoSubjectStillReadsAsASentence()
        {
            foreach (KillAllNote note in Enum.GetValues(typeof(KillAllNote)))
            {
                var sentence = KillAllPlan.NoteSentence(note, null);

                Assert.DoesNotContain("''", sentence);
                Assert.DoesNotContain("  ", sentence);
                if (note != KillAllNote.None) Assert.EndsWith(".", sentence, StringComparison.Ordinal);
            }
        }

        // ------------------------------------------------------------------
        //  The lines a sweep sends before it has finished
        // ------------------------------------------------------------------

        /// <summary>
        /// THE 4500ms BUG. Commander gives up waiting at CommandTimeoutMs and answers
        /// "Error: command timed out (server main thread busy)" while Update() carries
        /// straight on and finishes the sweep, so the one outcome a long sweep could report
        /// was that it had failed, while every hostile on the server died. The sweep answers
        /// the moment it knows what it is about to walk now, and the result line follows in
        /// the server log. These two lines must never be mistaken for one another: only the
        /// finished one opens with "KillAll complete".
        /// </summary>
        [Fact]
        public void AStartedSweepSaysSoAndSaysHowMuchIsInFrontOfIt()
        {
            var line = KillAllPlan.Started(412);

            Assert.StartsWith("KillAll started: 412 candidates", line, StringComparison.Ordinal);
            Assert.DoesNotContain("KillAll complete", line);
        }

        [Fact]
        public void OneCandidateReadsAsOne()
        {
            Assert.StartsWith("KillAll started: 1 candidate.", KillAllPlan.Started(1), StringComparison.Ordinal);
            Assert.DoesNotContain("2 candidate.", KillAllPlan.Started(2));
        }

        /// <summary>
        /// A second sweep would walk the snapshot the first one is holding, double every count
        /// and strike half the world twice, so it is refused and says how far the first has
        /// left to go rather than silently doing nothing.
        /// </summary>
        [Theory]
        [InlineData(1, "KillAll is already running: 1 candidate still to go.")]
        [InlineData(412, "KillAll is already running: 412 candidates still to go.")]
        public void ASecondSweepIsRefusedAndSaysHowFarTheFirstHasToGo(int remaining, string expected)
        {
            var line = KillAllPlan.AlreadyRunning(remaining);

            Assert.StartsWith(expected, line, StringComparison.Ordinal);
            Assert.DoesNotContain("KillAll complete", line);
        }

        /// <summary>
        /// A sweep that threw part way through did real work before it fell over, and its
        /// counts are the only record of what died. They are reported rather than swallowed,
        /// and the line does not open with "KillAll complete", because nothing completed.
        /// </summary>
        [Fact]
        public void ASweepThatFellOverReportsWhatItManagedAndDoesNotClaimToHaveFinished()
        {
            var line = KillAllPlan.StoppedEarly(3, 1, 12, "the world's object index went away");

            Assert.StartsWith("KillAll stopped early: the world's object index went away.",
                line, StringComparison.Ordinal);
            Assert.Contains("3 hostiles slain, 1 out of reach, 12 spared (players, pets & allies)", line);
            Assert.DoesNotContain("KillAll complete", line);
        }

        /// <summary>An exception with no message must not leave a colon staring at a full stop.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ASweepThatFellOverWithNothingToSayStillReadsAsASentence(string fault)
        {
            var line = KillAllPlan.StoppedEarly(0, 0, 0, fault);

            Assert.DoesNotContain(": .", line);
            Assert.DoesNotContain("  ", line);
            Assert.EndsWith(".", line, StringComparison.Ordinal);
        }

        /// <summary>
        /// The three counts are written once and used by both lines. Spelling them twice is how
        /// the two drift, and the drift a host notices first reads "1 hostiles slain".
        /// </summary>
        [Fact]
        public void BothLinesCountTheSameWay()
        {
            var counts = KillAllPlan.Counts(1, 0, 5);

            Assert.Equal("1 hostile slain, 0 out of reach, 5 spared (players, pets & allies)", counts);
            Assert.Contains(counts, KillAllPlan.Reply(1, 0, 5, KillAllNote.None, "", 0));
            Assert.Contains(counts, KillAllPlan.StoppedEarly(1, 0, 5, "something gave way"));
        }
    }
}
