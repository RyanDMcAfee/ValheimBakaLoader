using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Covers every link shape the "Add from Thunderstore" field accepts,
    /// including the eight user-supplied BepInExPack examples.
    /// </summary>
    public class ThunderstoreUrlParserTests
    {
        // --- The 8 canonical examples (all must parse) ---

        [Theory]
        // New-style community page, no version -> latest
        [InlineData("https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/",
            "denikson", "BepInExPack_Valheim", null)]
        // Old-style page, no version -> latest
        [InlineData("https://old.thunderstore.io/package/bbepis/BepInExPack/",
            "bbepis", "BepInExPack", null)]
        // Mod-manager protocol link, pinned version
        [InlineData("ror2mm://v1/install/thunderstore.io/denikson/BepInExPack_Valheim/5.4.2333/",
            "denikson", "BepInExPack_Valheim", "5.4.2333")]
        // New-style versions page -> latest
        [InlineData("https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/versions",
            "denikson", "BepInExPack_Valheim", null)]
        // New-style direct download, pinned version
        [InlineData("https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/5.4.2330/",
            "denikson", "BepInExPack_Valheim", "5.4.2330")]
        // Old-style direct download, pinned version
        [InlineData("https://old.thunderstore.io/package/download/bbepis/BepInExPack/5.4.2115/",
            "bbepis", "BepInExPack", "5.4.2115")]
        // Mod-manager protocol link for the old-namespace package
        [InlineData("ror2mm://v1/install/thunderstore.io/bbepis/BepInExPack/5.4.2118/",
            "bbepis", "BepInExPack", "5.4.2118")]
        // Old-style versions page (trailing slash) -> latest
        [InlineData("https://old.thunderstore.io/package/bbepis/BepInExPack/versions/",
            "bbepis", "BepInExPack", null)]
        public void Parses_all_supplied_example_links(string url, string owner, string name, string version)
        {
            Assert.True(ThunderstoreUrlParser.TryParse(url, out var reference, out var error), error);
            Assert.Equal(owner, reference.Owner);
            Assert.Equal(name, reference.Name);
            Assert.Equal(version, reference.Version);
        }

        // --- Additional tolerated shapes ---

        [Theory]
        // New-style pinned version page (/v/{version}/)
        [InlineData("https://thunderstore.io/c/valheim/p/JereKuusela/Server_devcommands/v/1.94.0/",
            "JereKuusela", "Server_devcommands", "1.94.0")]
        // Old-style pinned version page (/package/{owner}/{name}/{version}/)
        [InlineData("https://old.thunderstore.io/package/bbepis/BepInExPack/5.4.2100/",
            "bbepis", "BepInExPack", "5.4.2100")]
        // No trailing slash
        [InlineData("https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim",
            "denikson", "BepInExPack_Valheim", null)]
        // Query string + fragment stripped
        [InlineData("https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/?utm_source=x#readme",
            "denikson", "BepInExPack_Valheim", null)]
        // Surrounding whitespace trimmed
        [InlineData("  https://old.thunderstore.io/package/bbepis/BepInExPack/  ",
            "bbepis", "BepInExPack", null)]
        // http (not https)
        [InlineData("http://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/",
            "denikson", "BepInExPack_Valheim", null)]
        // Community-subdomain host
        [InlineData("https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/",
            "denikson", "BepInExPack_Valheim", null)]
        public void Parses_tolerated_variants(string url, string owner, string name, string version)
        {
            Assert.True(ThunderstoreUrlParser.TryParse(url, out var reference, out var error), error);
            Assert.Equal(owner, reference.Owner);
            Assert.Equal(name, reference.Name);
            Assert.Equal(version, reference.Version);
        }

        [Fact]
        public void FolderName_is_owner_dash_name()
        {
            ThunderstoreUrlParser.TryParse(
                "https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/", out var reference, out _);
            Assert.Equal("denikson-BepInExPack_Valheim", reference.FolderName);
        }

        // --- Rejections ---

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not a link at all")]
        [InlineData("https://example.com/c/valheim/p/denikson/BepInExPack_Valheim/")] // wrong host
        [InlineData("https://thunderstore.io/")]                                      // no package path
        [InlineData("https://thunderstore.io/c/valheim/")]                            // community only
        [InlineData("https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/")] // download w/o version
        [InlineData("ftp://thunderstore.io/package/bbepis/BepInExPack/")]             // bad scheme
        [InlineData("ror2mm://v1/install/example.com/denikson/BepInExPack_Valheim/5.4.2333/")] // bad protocol host
        [InlineData("https://thunderstore.io/c/valheim/p/bad owner/Mod/")]            // invalid owner chars
        public void Rejects_invalid_links_with_an_error(string url)
        {
            Assert.False(ThunderstoreUrlParser.TryParse(url, out var reference, out var error));
            Assert.Null(reference);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }
}
