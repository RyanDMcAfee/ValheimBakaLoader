using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using BakaLoaderClearDrops;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// ClearDropsPlan is the half of baka_cleardrops that touches no game type, compiled into
    /// BakaLoader itself through the ordinary source glob the same way KillAllPlan is, so the
    /// argument parsing, the keep rule and the reply text are held to account on every build with
    /// no dedicated server installed.
    /// <para>
    /// The other half, ClearDropsSweep, can only be exercised against a running server. The
    /// reply strings pinned here are the ones scripts/ui/cleardrops_reply_selftest.js feeds the
    /// window's reader, so the two sides of that line are checked against the same words.
    /// </para>
    /// </summary>
    public class ClearDropsPlanTests
    {
        private static string[] Tokens(string line) =>
            line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        // ------------------------------------------------------------------
        //  Arguments
        // ------------------------------------------------------------------

        /// <summary>The bare command keeps gear and keeps 32 metres around every player.</summary>
        [Fact]
        public void BareCommandKeepsGearAndThirtyTwoMetres()
        {
            foreach (var tokens in new[] { null, new string[0], Tokens("baka_cleardrops") })
            {
                var request = ClearDropsPlan.Parse(tokens);
                Assert.True(request.IsValid);
                Assert.True(request.KeepGear);
                Assert.Equal(32f, request.Radius);
                Assert.True(request.KeepNearPlayers);
            }
        }

        [Theory]
        [InlineData("baka_cleardrops keepgear=0", false, 32f)]
        [InlineData("baka_cleardrops keepgear=1", true, 32f)]
        [InlineData("baka_cleardrops KEEPGEAR=false", false, 32f)]
        [InlineData("baka_cleardrops keepgear=On", true, 32f)]
        [InlineData("baka_cleardrops radius=50", true, 50f)]
        [InlineData("baka_cleardrops radius=12.5", true, 12.5f)]
        [InlineData("baka_cleardrops radius=0", true, 0f)]
        [InlineData("baka_cleardrops radius=1000", true, 1000f)]
        [InlineData("baka_cleardrops keepgear=0 radius=0", false, 0f)]
        [InlineData("baka_cleardrops radius=64 keepgear=0", false, 64f)]
        public void KeyValueArgumentsInAnyOrder(string line, bool keepGear, float radius)
        {
            var request = ClearDropsPlan.Parse(Tokens(line));

            Assert.True(request.IsValid, request.Error);
            Assert.Equal(keepGear, request.KeepGear);
            Assert.Equal(radius, request.Radius);
            Assert.Equal(radius > 0f, request.KeepNearPlayers);
        }

        /// <summary>
        /// A command that deletes things must never run on a guess at what the host meant. A word
        /// it does not know, a key with no value and a key given twice are all usage errors.
        /// </summary>
        [Theory]
        [InlineData("baka_cleardrops everything")]
        [InlineData("baka_cleardrops keepgear")]
        [InlineData("baka_cleardrops =1")]
        [InlineData("baka_cleardrops near=Mithi")]
        [InlineData("baka_cleardrops keepgear=1 keepgear=0")]
        [InlineData("baka_cleardrops radius=10 radius=20")]
        public void AnythingUnknownIsAUsageError(string line)
        {
            var request = ClearDropsPlan.Parse(Tokens(line));

            Assert.False(request.IsValid);
            Assert.Equal(ClearDropsPlan.Usage, request.Error);
        }

        [Theory]
        [InlineData("baka_cleardrops radius=-1")]
        [InlineData("baka_cleardrops radius=1001")]
        [InlineData("baka_cleardrops radius=wide")]
        [InlineData("baka_cleardrops radius=")]
        [InlineData("baka_cleardrops radius=NaN")]
        [InlineData("baka_cleardrops radius=Infinity")]
        [InlineData("baka_cleardrops radius=12,5")]
        public void ABadRadiusIsRefusedWithItsOwnSentence(string line)
        {
            var request = ClearDropsPlan.Parse(Tokens(line));

            Assert.False(request.IsValid);
            Assert.Equal(ClearDropsPlan.BadRadius, request.Error);
            Assert.StartsWith("Error:", request.Error);
        }

        [Theory]
        [InlineData("baka_cleardrops keepgear=2")]
        [InlineData("baka_cleardrops keepgear=yes")]
        [InlineData("baka_cleardrops keepgear=")]
        public void ABadKeepGearIsRefusedWithItsOwnSentence(string line)
        {
            var request = ClearDropsPlan.Parse(Tokens(line));

            Assert.False(request.IsValid);
            Assert.Equal(ClearDropsPlan.BadKeepGear, request.Error);
            Assert.StartsWith("Error:", request.Error);
        }

        /// <summary>
        /// The radius is read the same way on every machine. A server whose Windows is set to a
        /// language that writes a decimal comma must still read 12.5 as twelve and a half.
        /// </summary>
        [Fact]
        public void TheRadiusIgnoresTheMachinesCulture()
        {
            var before = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                var request = ClearDropsPlan.Parse(Tokens("baka_cleardrops radius=12.5"));
                Assert.True(request.IsValid);
                Assert.Equal(12.5f, request.Radius);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = before;
            }
        }

        // ------------------------------------------------------------------
        //  What counts as gear
        // ------------------------------------------------------------------

        private static ClearDropsItemKind Kind(string name) =>
            (ClearDropsItemKind)Enum.Parse(typeof(ClearDropsItemKind), name);

        [Theory]
        [InlineData("OneHandedWeapon")]
        [InlineData("TwoHandedWeapon")]
        [InlineData("TwoHandedWeaponLeft")]
        [InlineData("Bow")]
        [InlineData("AttachAtgeir")]
        [InlineData("Shield")]
        [InlineData("Helmet")]
        [InlineData("Chest")]
        [InlineData("Legs")]
        [InlineData("Hands")]
        [InlineData("Shoulder")]
        [InlineData("Utility")]
        [InlineData("Trinket")]
        [InlineData("Tool")]
        [InlineData("Torch")]
        public void WeaponsArmourToolsShieldsAndUtilityAreGear(string kind)
        {
            // By name, because the enum is internal and an xunit theory parameter has to be public.
            Assert.True(ClearDropsPlan.IsGear(Kind(kind), 1, false));
        }

        [Theory]
        [InlineData("Material")]
        [InlineData("Consumable")]
        [InlineData("Ammo")]
        [InlineData("AmmoNonEquipable")]
        [InlineData("Trophy")]
        [InlineData("Misc")]
        [InlineData("Customization")]
        [InlineData("Fish")]
        [InlineData("None")]
        public void MaterialsFoodAndTrophiesAreNotGear(string kind)
        {
            Assert.False(ClearDropsPlan.IsGear(Kind(kind), 1, false));
        }

        /// <summary>
        /// Anything upgraded past its first level, or with a crafter's name on it, is somebody's,
        /// whatever its type. A level two resin does not exist, but a modded stackable that takes
        /// quality does, and a crafted item is always somebody's work.
        /// </summary>
        [Fact]
        public void QualityAboveOneOrACrafterMakesAnythingGear()
        {
            Assert.True(ClearDropsPlan.IsGear(ClearDropsItemKind.Material, 2, false));
            Assert.True(ClearDropsPlan.IsGear(ClearDropsItemKind.Consumable, 1, true));
            Assert.False(ClearDropsPlan.IsGear(ClearDropsItemKind.Material, 1, false));
        }

        /// <summary>
        /// A type the game grew and this plugin has not met is kept. A modded weapon deleted by a
        /// sweep is gone; an unfamiliar thing left lying is one somebody can still pick up.
        /// </summary>
        [Fact]
        public void AnItemTypeThisPluginHasNotMetIsKeptAsGear()
        {
            Assert.True(ClearDropsPlan.IsGear(ClearDropsItemKind.Unknown, 1, false));
        }

        /// <summary>
        /// Every value of the mirror has a side of the rule on purpose. The names are the game's
        /// own (Attach_Atgeir aside, which loses its underscore), so a review can read the list
        /// against ItemDrop.ItemData.ItemType line by line.
        /// </summary>
        [Fact]
        public void TheMirrorCarriesEveryTypeTheGameHasToday()
        {
            var names = Enum.GetNames(typeof(ClearDropsItemKind));
            foreach (var game in new[]
                     {
                         "None", "Material", "Consumable", "OneHandedWeapon", "Bow", "Shield", "Helmet",
                         "Chest", "Ammo", "Customization", "Legs", "Hands", "Trophy", "TwoHandedWeapon",
                         "Torch", "Misc", "Shoulder", "Utility", "Tool", "AttachAtgeir", "Fish",
                         "TwoHandedWeaponLeft", "AmmoNonEquipable", "Trinket",
                     })
                Assert.Contains(game, names);
        }

        // ------------------------------------------------------------------
        //  The decision
        // ------------------------------------------------------------------

        private static ClearDropsRequest Request(bool keepGear = true, float radius = 32f) =>
            new ClearDropsRequest { KeepGear = keepGear, Radius = radius };

        private const float Far = float.PositiveInfinity;

        /// <summary>
        /// The owner's acceptance test, in the plan: one gear item kept, one far stack removed and
        /// one stack beside the player kept.
        /// </summary>
        [Fact]
        public void TheHandPlacedDropTest()
        {
            var request = Request();

            Assert.Equal(ClearDropsDecision.KeepGear,
                ClearDropsPlan.Decide(request, true, ClearDropsItemKind.OneHandedWeapon, 1, false, Far));
            Assert.Equal(ClearDropsDecision.Remove,
                ClearDropsPlan.Decide(request, true, ClearDropsItemKind.Material, 1, false, 200f * 200f));
            Assert.Equal(ClearDropsDecision.KeepNearPlayer,
                ClearDropsPlan.Decide(request, true, ClearDropsItemKind.Material, 1, false, 3f * 3f));
        }

        [Fact]
        public void NobodyOnlineMeansNothingIsKeptForNearness()
        {
            Assert.Equal(ClearDropsDecision.Remove,
                ClearDropsPlan.Decide(Request(), true, ClearDropsItemKind.Material, 1, false, Far));
        }

        [Fact]
        public void TheRadiusIsInclusiveAtItsEdge()
        {
            Assert.Equal(ClearDropsDecision.KeepNearPlayer,
                ClearDropsPlan.Decide(Request(radius: 10f), true, ClearDropsItemKind.Material, 1, false, 100f));
            Assert.Equal(ClearDropsDecision.Remove,
                ClearDropsPlan.Decide(Request(radius: 10f), true, ClearDropsItemKind.Material, 1, false, 100.01f));
        }

        [Fact]
        public void RadiusZeroClearsRightUpToThePlayersFeet()
        {
            Assert.Equal(ClearDropsDecision.Remove,
                ClearDropsPlan.Decide(Request(radius: 0f), true, ClearDropsItemKind.Material, 1, false, 0f));
        }

        [Fact]
        public void KeepGearOffClearsGearToo()
        {
            Assert.Equal(ClearDropsDecision.Remove,
                ClearDropsPlan.Decide(Request(keepGear: false), true, ClearDropsItemKind.OneHandedWeapon, 3, true, Far));

            // and gear lying beside a player is still kept for its nearness
            Assert.Equal(ClearDropsDecision.KeepNearPlayer,
                ClearDropsPlan.Decide(Request(keepGear: false), true, ClearDropsItemKind.OneHandedWeapon, 3, true, 1f));
        }

        /// <summary>Gear is counted as gear even when it is also near somebody: gear comes first.</summary>
        [Fact]
        public void GearIsCountedBeforeNearness()
        {
            Assert.Equal(ClearDropsDecision.KeepGear,
                ClearDropsPlan.Decide(Request(), true, ClearDropsItemKind.Helmet, 1, false, 1f));
        }

        /// <summary>
        /// An item whose record will not come apart is kept, whatever the switches say. Nobody
        /// can say they saw what it was, so nobody asked for it to go.
        /// </summary>
        [Fact]
        public void AnUnreadableItemIsKeptWhateverTheSwitchesSay()
        {
            foreach (var request in new[] { Request(), Request(false, 0f), Request(true, 0f), Request(false, 32f) })
                Assert.Equal(ClearDropsDecision.KeepUnreadable,
                    ClearDropsPlan.Decide(request, false, ClearDropsItemKind.Material, 1, false, Far));
        }

        [Fact]
        public void ANaNDistanceIsNeverInsideTheRadius()
        {
            Assert.False(ClearDropsPlan.WithinRadius(float.NaN, 32f));
        }

        // ------------------------------------------------------------------
        //  The reply
        // ------------------------------------------------------------------

        /// <summary>
        /// The line the window reads, word for word. scripts/ui/cleardrops_reply_selftest.js
        /// carries the same strings into the page's reader.
        /// </summary>
        [Fact]
        public void TheReplyLineIsTheOneTheWindowReads()
        {
            Assert.Equal(
                "cleardrops: removed 412 \u00B7 kept 9 gear \u00B7 kept 31 near players \u00B7 scanned 184220 objects",
                ClearDropsPlan.Reply(412, 9, 31, 184220, 0));

            Assert.Equal(
                "cleardrops: removed 0 \u00B7 kept 0 gear \u00B7 kept 0 near players \u00B7 scanned 1 object",
                ClearDropsPlan.Reply(0, 0, 0, 1, 0));
        }

        [Fact]
        public void UnreadableItemsAreSaidOnlyWhenThereAreAny()
        {
            Assert.DoesNotContain("unreadable", ClearDropsPlan.Reply(1, 2, 3, 4, 0));
            Assert.EndsWith(" \u00B7 kept 2 unreadable", ClearDropsPlan.Reply(1, 2, 3, 4, 2));
        }

        [Fact]
        public void StartedBusyAndStoppedAllOpenWithTheVerb()
        {
            Assert.Equal(
                "cleardrops: started on 5120 ground items out of 184220 objects. The result line follows in the server log when the sweep finishes.",
                ClearDropsPlan.Started(5120, 184220));
            Assert.Equal(
                "cleardrops: already running, 1 ground item still to go. Wait for its result line before starting another.",
                ClearDropsPlan.AlreadyRunning(1));
            Assert.Equal(
                "cleardrops: stopped early: NullReferenceException. Up to that point: removed 3 \u00B7 kept 1 gear \u00B7 kept 0 near players \u00B7 scanned 90 objects.",
                ClearDropsPlan.StoppedEarly(3, 1, 0, 90, 0, "NullReferenceException"));
            Assert.Contains("no reason given", ClearDropsPlan.StoppedEarly(0, 0, 0, 0, 0, ""));
        }

        /// <summary>Every refusal opens with "Error:" or "Usage:", which is how the window tells them apart.</summary>
        [Fact]
        public void EveryRefusalOpensTheWayTheWindowExpects()
        {
            Assert.StartsWith("Usage: baka_cleardrops", ClearDropsPlan.Usage);
            foreach (var line in new[] { ClearDropsPlan.BadRadius, ClearDropsPlan.BadKeepGear, ClearDropsPlan.NotReady, ClearDropsPlan.NoIndex })
                Assert.StartsWith("Error:", line);
        }

        /// <summary>No long dash anywhere a host can read it.</summary>
        [Fact]
        public void NoLineCarriesALongDash()
        {
            foreach (var line in new[]
                     {
                         ClearDropsPlan.Usage, ClearDropsPlan.BadRadius, ClearDropsPlan.BadKeepGear,
                         ClearDropsPlan.NotReady, ClearDropsPlan.NoIndex, ClearDropsPlan.Reply(1, 2, 3, 4, 5),
                         ClearDropsPlan.Started(1, 2), ClearDropsPlan.AlreadyRunning(2),
                         ClearDropsPlan.StoppedEarly(1, 2, 3, 4, 5, "x"),
                     })
            {
                Assert.DoesNotContain("\u2013", line);
                Assert.DoesNotContain("\u2014", line);
            }
        }

        // ------------------------------------------------------------------
        //  The source
        // ------------------------------------------------------------------

        private static string RepoRoot([CallerFilePath] string here = "")
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(here) ?? ".");
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ValheimBakaLoader.sln")))
                dir = dir.Parent;

            Assert.True(dir != null, "could not find ValheimBakaLoader.sln above " + here);
            return dir.FullName;
        }

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

        /// <summary>
        /// The plan must stay pure, because the app project compiles it for real. One using of
        /// UnityEngine here and BakaLoader itself stops building.
        /// </summary>
        [Fact]
        public void ThePlanNamesNoGameType()
        {
            var src = Read("ValheimBakaLoader", "Resources", "Commander", "BakaClearDropsPlan.cs");
            var code = string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            Assert.DoesNotContain("#if", code);
            foreach (var forbidden in new[]
                     {
                         "using UnityEngine", "using BepInEx", "using HarmonyLib",
                         "ItemDrop", "ZDOVars", "ZDOMan", "ZNet", "GameObject", "Vector3",
                     })
                Assert.False(code.Contains(forbidden), "BakaClearDropsPlan.cs names " + forbidden);
        }

        /// <summary>The sweep names the game, so it has to stay behind the fence the app never defines.</summary>
        [Fact]
        public void TheSweepIsFencedOffFromTheApp()
        {
            var src = Read("ValheimBakaLoader", "Resources", "Commander", "BakaClearDropsSweep.cs");
            Assert.StartsWith("#if VALHEIM_PLUGIN", src);
            Assert.EndsWith("#endif", src.TrimEnd());
        }

        /// <summary>
        /// Commander is built from both halves and answers the verb, and it says so in the
        /// version it announces: a rewritten plugin under the old number is one nobody replaces.
        /// </summary>
        [Fact]
        public void CommanderIsBuiltFromBothHalvesAndAnswersTheVerb()
        {
            var script = Read("ValheimBakaLoader", "Resources", "build-plugins.ps1");
            var entry = script.IndexOf("Dir = \"Commander\"", StringComparison.Ordinal);
            var next = script.IndexOf("Dir = \"", entry + 8, StringComparison.Ordinal);
            Assert.True(entry >= 0 && next > entry, "build-plugins.ps1 no longer builds Commander");
            var block = script.Substring(entry, next - entry);
            Assert.Contains("\"BakaClearDropsPlan.cs\"", block);
            Assert.Contains("\"BakaClearDropsSweep.cs\"", block);

            var commander = Read("ValheimBakaLoader", "Resources", "Commander", "BakaLoaderCommander.cs");
            Assert.Contains("case \"baka_cleardrops\": return CmdClearDrops(tokens);", commander);
            Assert.Contains("ClearDropsSweep.Pump();", commander);

            var version = System.Text.RegularExpressions.Regex.Match(commander, "PluginVersion\\s*=\\s*\"([0-9.]+)\"");
            Assert.True(version.Success);
            Assert.True(new Version(version.Groups[1].Value) >= new Version("1.9.0"),
                "Commander is published as " + version.Groups[1].Value + ", below the 1.9.0 baka_cleardrops shipped in");
        }
    }
}
