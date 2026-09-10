using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Valheim 1.0 dropped its fixed platform list: the platform is a plain
    /// string, and the game prints a one-letter form of it in places (V, X, S,
    /// N, A). Nothing may be dropped for being unfamiliar.
    /// </summary>
    public class PlayerPlatformsTests
    {
        [Theory]
        [InlineData("Steam", PlayerPlatforms.Steam)]
        [InlineData("steam", PlayerPlatforms.Steam)]
        [InlineData("V", PlayerPlatforms.Steam)]
        [InlineData("v", PlayerPlatforms.Steam)]
        [InlineData("Xbox", PlayerPlatforms.Xbox)]
        [InlineData("X", PlayerPlatforms.Xbox)]
        [InlineData("PlayStation", PlayerPlatforms.PlayStation)]
        [InlineData("psn", PlayerPlatforms.PlayStation)]
        [InlineData("S", PlayerPlatforms.PlayStation)]
        [InlineData("Nintendo", PlayerPlatforms.Nintendo)]
        [InlineData("switch", PlayerPlatforms.Nintendo)]
        [InlineData("N", PlayerPlatforms.Nintendo)]
        [InlineData("GameCenter", PlayerPlatforms.GameCenter)]
        [InlineData("A", PlayerPlatforms.GameCenter)]
        [InlineData("PlayFab", PlayerPlatforms.PlayFab)]
        [InlineData("playfab", PlayerPlatforms.PlayFab)]
        public void KnownTokensAreNormalized(string token, string expected)
        {
            Assert.True(PlayerPlatforms.TryGetValidPlatform(token, out var platform));
            Assert.Equal(expected, platform);
        }

        [Theory]
        [InlineData("SomethingNew")]
        [InlineData("epic")]
        [InlineData("Q")]
        public void UnknownTokensAreKeptAsWritten(string token)
        {
            Assert.True(PlayerPlatforms.TryGetValidPlatform(token, out var platform));
            Assert.Equal(token, platform);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void OnlyAnEmptyTokenIsRejected(string token)
        {
            Assert.False(PlayerPlatforms.TryGetValidPlatform(token, out var platform));
            Assert.Null(platform);
        }

        [Fact]
        public void SurroundingWhitespaceIsTrimmed()
        {
            Assert.True(PlayerPlatforms.TryGetValidPlatform("  Xbox  ", out var platform));
            Assert.Equal(PlayerPlatforms.Xbox, platform);
        }

        [Theory]
        [InlineData(PlayerPlatforms.Steam, "Steam")]
        [InlineData("V", "Steam")]
        [InlineData(PlayerPlatforms.Xbox, "Xbox")]
        [InlineData(PlayerPlatforms.PlayStation, "PlayStation")]
        [InlineData(PlayerPlatforms.Nintendo, "Nintendo Switch")]
        [InlineData(PlayerPlatforms.GameCenter, "Apple Game Center")]
        [InlineData(PlayerPlatforms.PlayFab, "Crossplay")]
        [InlineData("SomethingNew", "SomethingNew")]
        [InlineData("", "")]
        public void DisplayNamesAreReadable(string platform, string expected)
        {
            Assert.Equal(expected, PlayerPlatforms.DisplayName(platform));
        }
    }
}
