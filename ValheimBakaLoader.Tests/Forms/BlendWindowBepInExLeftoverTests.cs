using System;
using System.Collections.Generic;
using System.Text.Json;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The three 1.2.0 leftovers on the loader row, from the host's side of the bridge: a fact
    /// that is sent and never drawn, a failure that is worded for nobody, and the fields on a
    /// write result that no reader reads.
    /// <para>
    /// The drawing is the page's half. These say what the page has to draw it FROM, and that
    /// the host side really sends it, because a row that cannot be worded for want of a field
    /// is the shape all three of these take.
    /// </para>
    /// </summary>
    public class BlendWindowBepInExLeftoverTests
    {
        private static string Bridge() => AppSourceTree.Files()["BlendWindow.Bridge.cs"];

        private static Dictionary<string, JsonElement> Catalog()
        {
            using var document = JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty("keys").EnumerateObject())
                map[entry.Name] = entry.Value.Clone();
            return map;
        }

        /// <summary>
        /// A write that stopped between the old core going into the backup and the new one
        /// coming in leaves the install with no note, so the row reads it as Outside: somebody
        /// else's BepInEx. The fact that says why is on the answer already, and the row needs
        /// nothing further from the host to word it.
        /// </summary>
        [Fact]
        public void The_row_is_told_when_a_write_could_not_finish_its_note()
        {
            var bridge = Bridge();

            Assert.Contains("interruptedWrite = status.InterruptedWrite,", bridge, StringComparison.Ordinal);
            // beside the two facts that make it read as Outside, which is what has to be explained
            Assert.Contains("maintainedByBakaLoader = status.MaintainedByBakaLoader,", bridge, StringComparison.Ordinal);
            Assert.Contains("newestBackup = status.NewestBackup,", bridge, StringComparison.Ordinal);
        }

        /// <summary>
        /// An unattended repair whose own pack the site no longer serves gets a reason of its
        /// own, and it is NOT the one the recorder swallows. Both versions ride with it, so the
        /// row can say what is here and offer the pack that is served.
        /// </summary>
        [Fact]
        public void A_repair_whose_pack_is_gone_reaches_the_row_with_both_versions()
        {
            var bridge = Bridge();
            var at = bridge.IndexOf("private void RecordUnattendedBepInExFailure(", StringComparison.Ordinal);
            Assert.True(at > 0, "the unattended failure recorder is gone");

            var body = bridge.Substring(at, Math.Min(1800, bridge.Length - at));

            // the ordinary bad minute on the internet is still swallowed on purpose
            Assert.Contains("if (string.Equals(id, \"bepinex.offline\", StringComparison.Ordinal)) return;",
                body, StringComparison.Ordinal);

            // and the repair failure is not that id, so it survives and carries its versions
            Assert.Contains("Version = Value(\"offered\"),", body, StringComparison.Ordinal);
            Assert.Contains("InstalledVersion = Value(\"noted\"),", body, StringComparison.Ordinal);

            // which the row reads off the same two fields it already draws for a refusal
            Assert.Contains("version = last.Version,", bridge, StringComparison.Ordinal);
            Assert.Contains("installedVersion = last.InstalledVersion,", bridge, StringComparison.Ordinal);
            Assert.Contains("leftAsItWas = last.Outcome != \"written\"", bridge, StringComparison.Ordinal);
        }

        /// <summary>
        /// The reason names itself and the page holds a sentence for it, so a host reads words
        /// rather than an id or the machine's own message.
        /// </summary>
        [Fact]
        public void The_repair_reason_is_paired_with_a_sentence_that_offers_the_way_out()
        {
            var js = AppSourceTree.Web("app.js");
            var catalog = Catalog();

            Assert.Contains("named:\"bepinex.repairPackGone\"", js, StringComparison.Ordinal);
            Assert.True(catalog.ContainsKey("bepinex.reason.repair_pack_gone"),
                "the catalog has no bepinex.reason.repair_pack_gone");

            var lore = catalog["bepinex.reason.repair_pack_gone"].GetProperty("lore").GetString();
            Assert.Contains("{noted}", lore, StringComparison.Ordinal);
            Assert.Contains("Update", lore, StringComparison.Ordinal);

            // BOTH versions are in the sentence and both are declared. The page passes `offered`
            // and two documents said the row named the pack that is served; until 1.2.1 nothing
            // in the sentence used it, so the row named only the pack that is gone.
            Assert.Contains("{offered}", lore, StringComparison.Ordinal);
            var declared = catalog["bepinex.reason.repair_pack_gone"].GetProperty("params");
            Assert.True(declared.TryGetProperty("noted", out _), "{noted} is not declared");
            Assert.True(declared.TryGetProperty("offered", out _), "{offered} is not declared");
        }

        /// <summary>
        /// THE OFFER IS ONE THE CODE CAN KEEP. That sentence ends by telling a host to Update,
        /// and an Update over an install with files missing is recomputed on the host side as
        /// the same repair: the same pack that is gone, the same 404, the same reason. So the
        /// press carries the host's answer, and the row draws the button that carries it.
        /// </summary>
        [Fact]
        public void The_update_that_sentence_offers_carries_the_answer_that_makes_it_work()
        {
            var js = AppSourceTree.Web("app.js");
            var bridge = Bridge();

            // the row offers it, and only in the state where repair cannot finish
            Assert.Contains(
                "if(state===\"incomplete\") return bepInExRepairPackGone(b)?[\"repair\",\"update\"]:[\"repair\"];",
                js, StringComparison.Ordinal);

            // both presses carry it
            Assert.Contains(
                "()=>bepInExUpdateFlow(bepInExRepairPackGone(S.bepinex)?{takeCurrentPack:true}:null));",
                js, StringComparison.Ordinal);
            Assert.Contains("bepInExUpdateFlow(gone?{takeCurrentPack:true}:null);", js, StringComparison.Ordinal);

            // it survives the confirm a write over somebody else's install still asks
            Assert.Contains(
                "bepInExWrite(method,Object.assign(bepInExWriteFlags(b),extra||{}),after));", js, StringComparison.Ordinal);

            // and the host side reads it and stops calling the write a repair
            Assert.Contains("TakeCurrentPack = call?.Value<bool?>(\"takeCurrentPack\") == true,",
                bridge, StringComparison.Ordinal);
            Assert.Contains("&& !options.TakeCurrentPack",
                AppSourceTree.Files()["BepInExService.cs"], StringComparison.Ordinal);
        }

        /// <summary>
        /// And the three fields are READ. previousPackVersion was guarded on differing from
        /// previousVersion, which no producer can make true: every one of them sends the pair as
        /// the same string or sends this one as null, so the line could never print. The reader
        /// was also the only one of the three sitting past a branch that returns, and that
        /// branch is the adoption, which is the write the field was added for.
        /// </summary>
        [Fact]
        public void The_pack_version_a_note_recorded_can_actually_be_printed()
        {
            var js = AppSourceTree.Web("app.js");

            Assert.DoesNotContain("res.previousPackVersion!==res.previousVersion", js, StringComparison.Ordinal);
            Assert.Contains("if(res.previousPackVersion)", js, StringComparison.Ordinal);

            // and the adoption ending reports as well, rather than returning in front of it
            var at = js.IndexOf("if(res&&res.adopted&&res.nothingChanged){", StringComparison.Ordinal);
            Assert.True(at > 0, "the adoption ending is gone");
            var body = js.Substring(at, Math.Min(700, js.Length - at));
            var reported = body.IndexOf("bepInExReportReach(res);", StringComparison.Ordinal);
            var returned = body.IndexOf("return;", StringComparison.Ordinal);
            Assert.True(reported > 0, "the adoption ending still reports nothing");
            Assert.True(reported < returned, "the report sits after the return, so it never runs");
        }

        /// <summary>
        /// The three fields a write result carries that had no reader. They stay on the answer,
        /// because taking a field off a reply is not additive and a page in the wild may be
        /// reading them; what changed is that the row now has something to say with them. A
        /// field the host sends and nothing reads is a story told to nobody, and naming them
        /// here is what stops a fourth one being added the same way.
        /// </summary>
        [Theory]
        [InlineData("previousPackVersion = result.PreviousPackVersion,")]
        [InlineData("isolatedInstallsLinked = result.IsolatedInstallsLinked,")]
        [InlineData("profileLoaderFilesRefreshed = result.ProfileLoaderFilesRefreshed,")]
        public void The_write_result_still_carries_the_fields_a_row_can_now_word(string field)
            => Assert.Contains(field, Bridge(), StringComparison.Ordinal);
    }
}
