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

        // ---- the id the server knows a player by ----

        /// <summary>
        /// The game reads a kick target as a platform user id first and only compares NAMES
        /// when that finds nobody. On a Steam only server it looks the peer up by the id alone,
        /// because a Steam socket answers its host name as the bare number; on a crossplay
        /// server it looks it up by the whole "Platform_id". The one spelling below satisfies
        /// both, which is why it is the one the app sends.
        /// </summary>
        [Theory]
        [InlineData(PlayerPlatforms.Steam, "76561198000000001", "Steam_76561198000000001")]
        [InlineData(PlayerPlatforms.Xbox, "2535000000000000", "Xbox_2535000000000000")]
        [InlineData(PlayerPlatforms.PlayFab, "BakaXplay_2498_3c72cce4", "PlayFab_BakaXplay_2498_3c72cce4")]
        [InlineData("  Steam  ", "  76561198000000001  ", "Steam_76561198000000001")]
        public void AHostIdIsThePlatformAndTheIdJoined(string platform, string playerId, string expected)
        {
            Assert.Equal(expected, PlayerPlatforms.HostId(platform, playerId));
        }

        /// <summary>
        /// Null rather than half an id: the caller has the player's name to fall back to, and a
        /// kick built out of a blank half would be a command aimed at nobody. Whitespace inside
        /// either half is refused for the same reason, since the command is one line.
        /// </summary>
        [Theory]
        [InlineData(null, "76561198000000001")]
        [InlineData("Steam", null)]
        [InlineData("", "76561198000000001")]
        [InlineData("Steam", "   ")]
        [InlineData("Steam", "7656 1198")]
        [InlineData("Play Station", "1")]
        public void AnIdThatCannotBeSentAsOneLineIsNotBuilt(string platform, string playerId)
        {
            Assert.Null(PlayerPlatforms.HostId(platform, playerId));
        }
    }
}
