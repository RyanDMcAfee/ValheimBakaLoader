using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Covers the Valheim 1.0 id forms in adminlist / bannedlist / permittedlist. From 1.0 the
    /// server only matches a Steam player when the line reads V_&lt;steamid64&gt;, while older
    /// servers only match the bare number, so the service keeps both lines and upgrades files
    /// that were written before 1.0.
    /// </summary>
    public class PlayerListServiceTests : BaseTest, IDisposable
    {
        // Not a real account, just a well formed steamid64.
        private const string SteamId = "76561198012345678";
        private const string SteamIdV = "V_76561198012345678";

        private const string OtherId = "76561198087654321";
        private const string OtherIdV = "V_76561198087654321";

        private readonly PlayerListService Service;

        private readonly string SaveFolder =
            Path.Combine(Path.GetTempPath(), "vbl-playerlist-tests-" + Guid.NewGuid().ToString("N"));

        public PlayerListServiceTests()
        {
            Service = GetService<PlayerListService>();
            Directory.CreateDirectory(SaveFolder);
        }

        public void Dispose()
        {
            try { Directory.Delete(SaveFolder, recursive: true); } catch { /* best effort */ }
        }

        private string AdminPath => Path.Combine(SaveFolder, "adminlist.txt");

        private void WriteAdminList(params string[] lines) => File.WriteAllLines(AdminPath, lines);

        private string[] AdminLines() =>
            File.Exists(AdminPath) ? File.ReadAllLines(AdminPath) : Array.Empty<string>();

        // --- NormalizeForms ---------------------------------------------------------------

        [Fact]
        public void NormalizeForms_BareSteamIdGivesBothForms()
        {
            Assert.Equal(new[] { SteamId, SteamIdV }, PlayerListService.NormalizeForms(SteamId));
        }

        [Fact]
        public void NormalizeForms_DisplayFormGivesTheSamePair()
        {
            Assert.Equal(new[] { SteamId, SteamIdV }, PlayerListService.NormalizeForms(SteamIdV));
        }

        [Fact]
        public void NormalizeForms_LegacySteamPrefixGivesTheSamePair()
        {
            Assert.Equal(new[] { SteamId, SteamIdV }, PlayerListService.NormalizeForms("Steam_" + SteamId));
        }

        [Fact]
        public void NormalizeForms_TreatsAMiscasedPrefixAsItsOwnEntry()
        {
            // The server matches list lines with an ordinal, case sensitive compare, so a
            // lower case prefix is a different string to it and must be to us as well.
            Assert.Equal(new[] { "steam_" + SteamId }, PlayerListService.NormalizeForms("steam_" + SteamId));
            Assert.Equal(new[] { "v_" + SteamId }, PlayerListService.NormalizeForms("v_" + SteamId));
        }

        [Fact]
        public void NormalizeForms_TrimsSurroundingWhitespace()
        {
            Assert.Equal(new[] { SteamId, SteamIdV }, PlayerListService.NormalizeForms("  " + SteamId + "\t"));
        }

        [Theory]
        // An already filtered console id, a PlayFab id and a plain name are all values the
        // server compares as written, so they have to survive untouched.
        [InlineData("X_11400714819323198485")]
        [InlineData("S_11400714819323198485")]
        [InlineData("N_11400714819323198485")]
        [InlineData("A_11400714819323198485")]
        [InlineData("PlayFab_BakaXplay_2498_3c72cce4")]
        [InlineData("SomePlayerName")]
        public void NormalizeForms_KeepsNonSteamIdsVerbatim(string id)
        {
            Assert.Equal(new[] { id }, PlayerListService.NormalizeForms(id));
        }

        [Theory]
        // The raw log spelling plus the line 1.0 actually looks up, which is the display prefix
        // and the user id multiplied by Splatform's constant.
        [InlineData("Xbox_2535412345678901", "X_")]
        [InlineData("PlayStation_4802345678901234", "S_")]
        [InlineData("Nintendo_1234567890123456", "N_")]
        [InlineData("GameCenter_9876543210987654", "A_")]
        public void NormalizeForms_ConsoleIdGivesTheRawLineAndTheFilteredLine(string id, string prefix)
        {
            var forms = PlayerListService.NormalizeForms(id);

            Assert.Equal(2, forms.Count);
            Assert.Equal(id, forms[0]);
            Assert.StartsWith(prefix, forms[1]);
            Assert.NotEqual(id, forms[1]);
        }

        [Fact]
        public void NormalizeForms_KeepsAConsoleIdWithANonNumericUserIdVerbatim()
        {
            // The server only rewrites a user id it can read as a number, so neither do we.
            const string id = "Xbox_not-a-number";

            Assert.Equal(new[] { id }, PlayerListService.NormalizeForms(id));
        }

        [Theory]
        [InlineData("Steam", SteamId)]
        [InlineData("Xbox", "2535412345678901")]
        [InlineData("PlayStation", "4802345678901234")]
        [InlineData("Nintendo", "1234567890123456")]
        [InlineData("GameCenter", "9876543210987654")]
        [InlineData("PlayFab", "BakaXplay_2498_3c72cce4")]
        public void NormalizeForms_StoresTheLineTheServerLooksUp(string platform, string userId)
        {
            var forms = PlayerListService.NormalizeForms(platform + "_" + userId);

            Assert.Contains(ServerLookupForm(platform, userId), forms);
        }

        /// <summary>
        /// A transcription of Splatform.FilterPlatformUserID and PlatformUserID.ToString from the
        /// Valheim 1.0 assemblies: the platform maps to a one letter display prefix, and the user
        /// id is multiplied by a fixed constant for the four console platforms but not for Steam.
        /// The server runs the id it is asked about through this before it compares, so one of
        /// the lines this service stores has to come out equal to the result.
        /// </summary>
        private static string ServerLookupForm(string platform, string userId)
        {
            var displayPrefixes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "Steam", "V" },
                { "Xbox", "X" },
                { "PlayStation", "S" },
                { "Nintendo", "N" },
                { "GameCenter", "A" },
            };

            var numberFiltered = new HashSet<string>(StringComparer.Ordinal)
            {
                "Nintendo", "PlayStation", "Xbox", "GameCenter",
            };

            // A user id that is not a number is passed through with its platform spelling.
            if (!ulong.TryParse(userId, out var value)) return platform + "_" + userId;

            var prefix = displayPrefixes.TryGetValue(platform, out var mapped) ? mapped : platform;
            var id = numberFiltered.Contains(platform)
                ? unchecked(value * 11400714819323198485UL)
                : value;

            return prefix + "_" + id.ToString(CultureInfo.InvariantCulture);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void NormalizeForms_ReturnsNothingForAnEmptyId(string id)
        {
            Assert.Empty(PlayerListService.NormalizeForms(id));
        }

        // --- AddToList --------------------------------------------------------------------

        [Fact]
        public void AddToList_WritesBothFormsForASteamId()
        {
            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Admin, SteamId));

            Assert.Equal(new[] { SteamId, SteamIdV }, AdminLines());
        }

        [Fact]
        public void AddToList_DoesNotDuplicateAnAlreadyCompleteEntry()
        {
            Service.AddToList(SaveFolder, PlayerListType.Admin, SteamId);

            Assert.False(Service.AddToList(SaveFolder, PlayerListType.Admin, SteamId));
            Assert.False(Service.AddToList(SaveFolder, PlayerListType.Admin, SteamIdV));
            Assert.Equal(new[] { SteamId, SteamIdV }, AdminLines());
        }

        [Fact]
        public void AddToList_KeepsCommentsAndOrderAndAppendsNewLines()
        {
            WriteAdminList("// server owner", OtherId, OtherIdV, "");

            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Admin, SteamId));

            Assert.Equal(
                new[] { "// server owner", OtherId, OtherIdV, "", SteamId, SteamIdV },
                AdminLines());
        }

        [Fact]
        public void AddToList_StoresAConsoleIdVerbatimAsASingleLine()
        {
            const string consoleId = "X_11400714819323198485";

            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Banned, consoleId));

            var lines = File.ReadAllLines(Path.Combine(SaveFolder, "bannedlist.txt"));
            Assert.Equal(new[] { consoleId }, lines);
        }

        [Fact]
        public void AddToList_AppendsCleanlyWhenTheFileHasNoTrailingNewLine()
        {
            File.WriteAllText(AdminPath, OtherId + Environment.NewLine + OtherIdV);

            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Admin, SteamId));

            Assert.Equal(new[] { OtherId, OtherIdV, SteamId, SteamIdV }, AdminLines());
        }

        // --- IsListed ---------------------------------------------------------------------

        [Fact]
        public void IsListed_MatchesAFileThatOnlyHasTheBareForm()
        {
            WriteAdminList(SteamId);

            Assert.True(Service.IsListed(SaveFolder, PlayerListType.Admin, SteamId));
            Assert.True(Service.IsListed(SaveFolder, PlayerListType.Admin, SteamIdV));
        }

        [Fact]
        public void IsListed_MatchesAFileThatOnlyHasTheDisplayForm()
        {
            WriteAdminList(SteamIdV);

            Assert.True(Service.IsListed(SaveFolder, PlayerListType.Admin, SteamId));
            Assert.True(Service.IsListed(SaveFolder, PlayerListType.Admin, SteamIdV));
        }

        [Fact]
        public void IsListed_MatchesAFileThatHasBothForms()
        {
            WriteAdminList("// admins", SteamId, SteamIdV);

            Assert.True(Service.IsListed(SaveFolder, PlayerListType.Admin, SteamId));
        }

        [Fact]
        public void IsListed_IgnoresCommentedOutEntriesAndUnknownIds()
        {
            WriteAdminList("// " + SteamId, OtherId, OtherIdV);

            Assert.False(Service.IsListed(SaveFolder, PlayerListType.Admin, SteamId));
            Assert.True(Service.IsListed(SaveFolder, PlayerListType.Admin, OtherId));
        }

        [Fact]
        public void IsListed_IsFalseWhenTheFileDoesNotExist()
        {
            Assert.False(Service.IsListed(SaveFolder, PlayerListType.Permitted, SteamId));
        }

        [Fact]
        public void IsListed_UpgradesALegacyFileOnFirstTouch()
        {
            WriteAdminList("// admins", SteamId);

            Assert.True(Service.IsListed(SaveFolder, PlayerListType.Admin, SteamId));

            Assert.Equal(new[] { "// admins", SteamId, SteamIdV }, AdminLines());
        }

        // --- What the server can actually see ---------------------------------------------

        [Fact]
        public void Presence_UsesTheRawLineTheWayTheServerDoes()
        {
            // SyncedList.Load keeps the line exactly as written and the lookup is a whole string
            // compare, so an annotated line and a padded line are not the id at all to the
            // server. Reading them leniently is what made the whole 1.0 fix a no-op on the most
            // ordinary file an operator writes.
            WriteAdminList(SteamIdV + " // owner", OtherIdV + " ");

            // The annotated line has nothing the server would recognise, so it gains no twin.
            // The padded line reads as a Steam id once the padding is off, so it does.
            Assert.Equal(2, Service.UpgradeLegacyEntries(SaveFolder, PlayerListType.Admin));
            Assert.Equal(
                new[] { SteamIdV + " // owner", OtherIdV + " ", OtherId, OtherIdV },
                AdminLines());

            // The annotated owner is not an admin on the server, so the menu must not say so.
            Assert.False(Service.IsListed(SaveFolder, PlayerListType.Admin, SteamId));
            Assert.True(Service.IsListed(SaveFolder, PlayerListType.Admin, OtherId));

            // Promoting them appends the exact forms rather than deciding they are already there.
            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Admin, SteamId));
            Assert.Equal(
                new[] { SteamIdV + " // owner", OtherIdV + " ", OtherId, OtherIdV, SteamId, SteamIdV },
                AdminLines());
            Assert.True(Service.IsListed(SaveFolder, PlayerListType.Admin, SteamId));
        }

        [Fact]
        public void Presence_TreatsAConsoleIdAsListedOnlyOnTheLineTheServerLooksUp()
        {
            const string rawId = "Xbox_2535412345678901";
            var path = Path.Combine(SaveFolder, "bannedlist.txt");

            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Banned, rawId));

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Equal(rawId, lines[0]);
            Assert.Equal(ServerLookupForm("Xbox", "2535412345678901"), lines[1]);

            Assert.True(Service.IsListed(SaveFolder, PlayerListType.Banned, rawId));
            Assert.True(Service.RemoveFromList(SaveFolder, PlayerListType.Banned, rawId));
            Assert.Empty(File.ReadAllLines(path));
        }

        [Fact]
        public void Writes_AreAtomicAndLeaveNoTempFileBehind()
        {
            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Admin, SteamId));

            Assert.Equal(new[] { AdminPath }, Directory.GetFiles(SaveFolder));
        }

        // --- RemoveFromList ---------------------------------------------------------------

        [Fact]
        public void RemoveFromList_RemovesBothFormsAndKeepsEverythingElse()
        {
            WriteAdminList("// admins", SteamId, OtherId, SteamIdV, OtherIdV, "");

            Assert.True(Service.RemoveFromList(SaveFolder, PlayerListType.Admin, SteamId));

            Assert.Equal(new[] { "// admins", OtherId, OtherIdV, "" }, AdminLines());
        }

        [Fact]
        public void RemoveFromList_AlsoClearsALegacySteamPrefixedLine()
        {
            WriteAdminList("Steam_" + SteamId, OtherId, OtherIdV);

            Assert.True(Service.RemoveFromList(SaveFolder, PlayerListType.Admin, SteamId));

            // The lazy upgrade adds the pair for the Steam_ line first, then the removal has to
            // take all three spellings away and leave the other admin alone.
            Assert.Equal(new[] { OtherId, OtherIdV }, AdminLines());
            Assert.False(Service.IsListed(SaveFolder, PlayerListType.Admin, SteamId));
        }

        [Fact]
        public void RemoveFromList_LeavesTheFileAloneWhenTheIdIsNotThere()
        {
            WriteAdminList(OtherId, OtherIdV);

            Assert.False(Service.RemoveFromList(SaveFolder, PlayerListType.Admin, SteamId));
            Assert.Equal(new[] { OtherId, OtherIdV }, AdminLines());
        }

        [Fact]
        public void RemoveFromList_RemovesOnlyTheMatchingConsoleId()
        {
            var path = Path.Combine(SaveFolder, "bannedlist.txt");
            File.WriteAllLines(path, new[] { "X_11400714819323198485", "S_11400714819323198485" });

            Assert.True(Service.RemoveFromList(SaveFolder, PlayerListType.Banned, "X_11400714819323198485"));
            Assert.Equal(new[] { "S_11400714819323198485" }, File.ReadAllLines(path));
        }

        // --- Upgrade ----------------------------------------------------------------------

        [Fact]
        public void UpgradeLegacyEntries_AddsTheMissingTwinOnceAndIsIdempotent()
        {
            WriteAdminList("// admins", SteamId, "", OtherId);

            Assert.Equal(2, Service.UpgradeLegacyEntries(SaveFolder, PlayerListType.Admin));
            Assert.Equal(
                new[] { "// admins", SteamId, "", OtherId, SteamIdV, OtherIdV },
                AdminLines());

            Assert.Equal(0, Service.UpgradeLegacyEntries(SaveFolder, PlayerListType.Admin));
            Assert.Equal(
                new[] { "// admins", SteamId, "", OtherId, SteamIdV, OtherIdV },
                AdminLines());
        }

        [Fact]
        public void UpgradeLegacyEntries_CompletesALegacySteamPrefixedLine()
        {
            WriteAdminList("Steam_" + SteamId);

            Assert.Equal(2, Service.UpgradeLegacyEntries(SaveFolder, PlayerListType.Admin));
            Assert.Equal(new[] { "Steam_" + SteamId, SteamId, SteamIdV }, AdminLines());
        }

        [Fact]
        public void UpgradeLegacyEntries_LeavesConsoleIdsAlone()
        {
            WriteAdminList("X_11400714819323198485", "PlayFab_BakaXplay_2498_3c72cce4");

            Assert.Equal(0, Service.UpgradeLegacyEntries(SaveFolder, PlayerListType.Admin));
            Assert.Equal(
                new[] { "X_11400714819323198485", "PlayFab_BakaXplay_2498_3c72cce4" },
                AdminLines());
        }

        [Fact]
        public void UpgradeLegacyEntries_IsSafeWhenTheFileIsMissing()
        {
            Assert.Equal(0, Service.UpgradeLegacyEntries(SaveFolder, PlayerListType.Permitted));
            Assert.False(File.Exists(Path.Combine(SaveFolder, "permittedlist.txt")));
        }

        [Fact]
        public void UpgradeAll_CoversAllThreeLists()
        {
            WriteAdminList(SteamId);
            File.WriteAllLines(Path.Combine(SaveFolder, "bannedlist.txt"), new[] { OtherId });
            File.WriteAllLines(Path.Combine(SaveFolder, "permittedlist.txt"), new[] { SteamIdV });

            Assert.Equal(3, Service.UpgradeAll(SaveFolder));

            Assert.Contains(SteamIdV, AdminLines());
            Assert.Contains(OtherIdV, File.ReadAllLines(Path.Combine(SaveFolder, "bannedlist.txt")));
            Assert.Contains(SteamId, File.ReadAllLines(Path.Combine(SaveFolder, "permittedlist.txt")));
        }

        // --- File shape -------------------------------------------------------------------

        [Fact]
        public void Writes_KeepTheLineEndingsAndEncodingTheFileAlreadyUses()
        {
            File.WriteAllText(AdminPath, OtherId + "\n" + OtherIdV + "\n");

            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Admin, SteamId));

            var bytes = File.ReadAllBytes(AdminPath);
            Assert.DoesNotContain((byte)'\r', bytes);

            // No byte order mark: a mark on the first line would stop the server matching it.
            Assert.NotEqual(0xEF, bytes[0]);

            var text = File.ReadAllText(AdminPath);
            Assert.Equal(OtherId + "\n" + OtherIdV + "\n" + SteamId + "\n" + SteamIdV + "\n", text);
        }

        [Fact]
        public void Writes_UseWindowsLineEndingsForANewFile()
        {
            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Admin, SteamId));

            Assert.Equal(SteamId + "\r\n" + SteamIdV + "\r\n", File.ReadAllText(AdminPath));
        }

        [Fact]
        public void Comparison_IsCaseSensitiveLikeTheServer()
        {
            // The server compares list lines with an ordinal, case sensitive match, so a
            // lower case v_ line is dead weight and must not read as listed.
            WriteAdminList("v_" + SteamId);

            Assert.False(Service.IsListed(SaveFolder, PlayerListType.Admin, SteamId));
            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Admin, SteamId));
            Assert.Contains(SteamIdV, AdminLines());
        }
    }
}
