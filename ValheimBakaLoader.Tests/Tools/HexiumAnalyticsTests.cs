using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The journal entry an install from the second site leaves behind. It is the same
    /// modin/modup entry every other install writes, with one extra key saying which
    /// site the files came from, so a later read of the journal can tell them apart
    /// without guessing from a version number.
    /// </summary>
    public class HexiumAnalyticsTests
    {
        [Fact]
        public void An_install_from_the_second_site_records_which_site_it_was()
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(new AnalyticsEvent
            {
                TimeUtc = DateTime.UtcNow,
                Kind = "modin",
                Server = "Final Sunset",
                Mod = "DrakeMods-LockSmith",
                ToVersion = "0.3.6",
                Source = "hexium",
            }));

            Assert.Equal("modin", json.Value<string>("k"));
            Assert.Equal("DrakeMods-LockSmith", json.Value<string>("m"));
            Assert.Equal("0.3.6", json.Value<string>("v"));
            Assert.Equal("hexium", json.Value<string>("src"));
        }

        [Fact]
        public void An_ordinary_install_writes_exactly_what_it_always_did()
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(new AnalyticsEvent
            {
                TimeUtc = DateTime.UtcNow,
                Kind = "modin",
                Server = "Final Sunset",
                Mod = "JereKuusela-WorldEditCommands",
                ToVersion = "1.66.0",
            }));

            // No key at all rather than a null one, so nothing already written changes shape.
            Assert.False(json.ContainsKey("src"));
        }

        [Fact]
        public void The_install_call_records_the_site_and_tells_an_update_from_a_first_install()
        {
            var handler = Between(BridgeSource(), "RegisterRpc(\"mods.installFromHexium\"", "// --- Capabilities");

            Assert.Contains("Source = Tools.ModSourceMarkerFile.HexiumSource", handler);
            // A folder that was already there is an update, and one that was not is an install.
            Assert.Contains("Kind = result.Replaced ? \"modup\" : \"modin\"", handler);
            // And an update says what it replaced, read before anything was overwritten.
            Assert.Contains("FromVersion = result.Replaced ? previousVersion : null", handler);
        }

        private static string Between(string source, string from, string to)
        {
            var start = source.IndexOf(from, StringComparison.Ordinal);
            if (start < 0) return "";
            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            return end < 0 ? source.Substring(start) : source.Substring(start, end - start);
        }

        private static string BridgeSource() => AppSourceTree.Files()["BlendWindow.Bridge.cs"];
    }
}
