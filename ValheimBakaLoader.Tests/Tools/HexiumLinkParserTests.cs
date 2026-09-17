using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The link shapes the "Add a mod" field accepts for the second site, and every
    /// shape it turns down. Owners on Hexium carry hyphens and dots, which the
    /// Thunderstore parser refuses, so this is a separate reader rather than a widened
    /// one: a hexium.gg address must never be read as a Thunderstore address or the
    /// other way round.
    /// </summary>
    public class HexiumLinkParserTests
    {
        private static HexiumModReference Parse(string url)
        {
            Assert.True(HexiumUrlParser.TryParse(url, out var reference, out var error), error);
            return reference;
        }

        [Theory]
        [InlineData("https://valheim.hexium.gg/mods/DrakeMods/LockSmith", "DrakeMods", "LockSmith", null)]
        [InlineData("https://valheim.hexium.gg/mods/DrakeMods/LockSmith/", "DrakeMods", "LockSmith", null)]
        [InlineData("  https://valheim.hexium.gg/mods/DrakeMods/LockSmith  ", "DrakeMods", "LockSmith", null)]
        [InlineData("https://valheim.hexium.gg/mods/DrakeMods/LockSmith?from=discord", "DrakeMods", "LockSmith", null)]
        [InlineData("https://valheim.hexium.gg/mods/DrakeMods/LockSmith#readme", "DrakeMods", "LockSmith", null)]
        [InlineData("http://valheim.hexium.gg/mods/DrakeMods/LockSmith", "DrakeMods", "LockSmith", null)]
        [InlineData("https://hexium.gg/mods/DrakeMods/LockSmith", "DrakeMods", "LockSmith", null)]
        [InlineData("https://VALHEIM.HEXIUM.GG/mods/DrakeMods/LockSmith", "DrakeMods", "LockSmith", null)]
        // A version in the address pins it.
        [InlineData("https://valheim.hexium.gg/mods/Orjat/SlavesIncorporated/1.0.666", "Orjat", "SlavesIncorporated", "1.0.666")]
        [InlineData("https://valheim.hexium.gg/mods/Orjat/SlavesIncorporated/v/1.0.666", "Orjat", "SlavesIncorporated", "1.0.666")]
        // Pre-release versions exist on this site, unlike Thunderstore.
        [InlineData("https://valheim.hexium.gg/mods/ArgusMagnus/ServersideQoL_AutoStore/2.0.13-beta.1",
            "ArgusMagnus", "ServersideQoL_AutoStore", "2.0.13-beta.1")]
        // Owners carry hyphens and dots.
        [InlineData("https://valheim.hexium.gg/mods/bruceirons-team/Oarsmen", "bruceirons-team", "Oarsmen", null)]
        [InlineData("https://valheim.hexium.gg/mods/Kurios.ZeuS/JavaHeim", "Kurios.ZeuS", "JavaHeim", null)]
        [InlineData("https://valheim.hexium.gg/mods/leazi-mods/leazi_tweaks", "leazi-mods", "leazi_tweaks", null)]
        public void Reads_the_page_shapes(string url, string owner, string name, string version)
        {
            var reference = Parse(url);

            Assert.Equal(owner, reference.Owner);
            Assert.Equal(name, reference.Name);
            Assert.Equal(version, reference.Version);
            Assert.Equal(owner + "-" + name, reference.FolderName);
        }

        [Theory]
        [InlineData("https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/")]
        [InlineData("https://valheim.thunderstore.io/api/v1/package/")]
        [InlineData("https://nothexium.gg/mods/Owner/Mod")]
        [InlineData("https://hexium.gg.example.com/mods/Owner/Mod")]
        [InlineData("https://example.com/mods/Owner/Mod?ref=hexium.gg")]
        [InlineData("ror2mm://v1/install/hexium.gg/Owner/Mod/1.0.0/")]
        [InlineData("file:///C:/mods/Owner/Mod")]
        [InlineData("not a link at all")]
        [InlineData("")]
        [InlineData(null)]
        public void Turns_down_everything_that_is_not_a_hexium_page(string url)
        {
            Assert.False(HexiumUrlParser.TryParse(url, out var reference, out var error));
            Assert.Null(reference);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void A_file_address_on_the_cdn_is_turned_down_with_a_reason()
        {
            // These addresses are opaque numbers with no owner or name in them, so there
            // is nothing to look a package up by and nothing to show the host.
            Assert.False(HexiumUrlParser.TryParse("https://cdn.hexium.gg/upload/1255/0.3.6.zip",
                out _, out var error));
            Assert.Contains("mod page", error);
        }

        [Fact]
        public void The_host_is_recognised_before_either_reader_judges_the_shape()
        {
            // This is what routes a pasted address to the right reader.
            Assert.True(HexiumUrlParser.LooksLikeHexiumLink("https://valheim.hexium.gg/mods/A/B"));
            Assert.True(HexiumUrlParser.LooksLikeHexiumLink("https://cdn.hexium.gg/upload/1/2.zip"));
            Assert.False(HexiumUrlParser.LooksLikeHexiumLink("https://thunderstore.io/c/valheim/p/A/B/"));
            Assert.False(HexiumUrlParser.LooksLikeHexiumLink("https://hexium.gg.example.com/mods/A/B"));
            Assert.False(HexiumUrlParser.LooksLikeHexiumLink("ror2mm://v1/install/thunderstore.io/A/B/1.0.0/"));
            Assert.False(HexiumUrlParser.LooksLikeHexiumLink(null));
        }

        [Theory]
        [InlineData("..", "Mod", null)]
        [InlineData("...", "Mod", null)]
        [InlineData("Own er", "Mod", null)]
        [InlineData("Owner", "Mo d", null)]
        [InlineData("Owner", "Mod-Name", null)]   // a name with a hyphen would split the folder wrong
        [InlineData("Owner", "Mod.Name", null)]
        public void Refuses_segments_that_are_not_plain(string owner, string name, string version)
        {
            var url = "https://valheim.hexium.gg/mods/" + owner + "/" + name + (version != null ? "/" + version : "");
            Assert.False(HexiumUrlParser.TryParse(url, out _, out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void A_version_that_is_not_a_version_is_refused()
        {
            Assert.False(HexiumUrlParser.TryParse("https://valheim.hexium.gg/mods/Owner/Mod/v/latest",
                out _, out var error));
            Assert.Contains("version", error);
        }

        // --- The page address the app builds for itself ---

        [Fact]
        public void The_page_address_is_built_from_checked_segments()
        {
            Assert.Equal("https://valheim.hexium.gg/mods/DrakeMods/LockSmith",
                HexiumUrlParser.PageUrl("DrakeMods", "LockSmith"));
            Assert.Equal("https://valheim.hexium.gg/mods/bruceirons-team/Oarsmen",
                HexiumUrlParser.PageUrl("bruceirons-team", "Oarsmen"));
            Assert.Equal("https://valheim.hexium.gg/mods/Kurios.ZeuS/JavaHeim",
                HexiumUrlParser.PageUrl("Kurios.ZeuS", "JavaHeim"));
        }

        [Theory]
        [InlineData(null, "Mod")]
        [InlineData("Owner", null)]
        [InlineData("", "Mod")]
        [InlineData("..", "Mod")]
        [InlineData("Owner/../..", "Mod")]
        [InlineData("Owner", "Mod/../..")]
        [InlineData("https://evil.example.com", "Mod")]
        [InlineData("Owner", "Mod?x=1")]
        public void An_address_is_never_built_from_anything_but_a_plain_pair(string owner, string name)
        {
            Assert.Null(HexiumUrlParser.PageUrl(owner, name));
        }

        // --- The rule every download address is held to ---

        [Theory]
        [InlineData("https://cdn.hexium.gg/upload/1255/0.3.6.zip", true)]
        [InlineData("https://hexium.gg/upload/1255/0.3.6.zip", true)]
        [InlineData("https://valheim.hexium.gg/files/x.zip", true)]
        [InlineData("http://cdn.hexium.gg/upload/1255/0.3.6.zip", false)]   // plain http is refused
        [InlineData("https://cdn.nothexium.gg/upload/1/2.zip", false)]
        [InlineData("https://hexium.gg.example.com/upload/1/2.zip", false)]
        [InlineData("https://thunderstore.io/package/download/a/b/1.0.0/", false)]
        [InlineData("file:///C:/temp/x.zip", false)]
        [InlineData("not a url", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Only_an_https_address_on_hexium_is_ever_fetched(string url, bool expected)
        {
            Assert.Equal(expected, HexiumUrlParser.IsDownloadAddress(url, out var uri));
            Assert.Equal(expected, uri != null);
        }
    }
}
