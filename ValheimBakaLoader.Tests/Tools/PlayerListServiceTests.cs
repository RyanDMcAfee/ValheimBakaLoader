using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
        // A PlayFab id and a plain name are values the server compares as written, so they have
        // to survive untouched.
        [InlineData("PlayFab_BakaXplay_2498_3c72cce4")]
        [InlineData("SomePlayerName")]
        public void NormalizeForms_KeepsNonSteamIdsVerbatim(string id)
        {
            Assert.Equal(new[] { id }, PlayerListService.NormalizeForms(id));
        }

        [Theory]
        // The display form a 1.0 server prints, and the raw id it was made from. Splatform's
        // multiplier is odd, so the multiplication has an exact inverse and the raw id is
        // recovered rather than lost. An operator who copies an id out of a 1.0 kick line must
        // end up with the same pair of lines as one who has the raw id.
        [InlineData("X_7534670547182701657", "Xbox_2535412345678901")]
        [InlineData("X_11400714819323198485", "Xbox_1")]
        [InlineData("S_11400714819323198485", "PlayStation_1")]
        [InlineData("N_11400714819323198485", "Nintendo_1")]
        [InlineData("A_11400714819323198485", "GameCenter_1")]
        public void NormalizeForms_ConsoleDisplayFormGivesTheSamePairAsItsRawId(
            string displayForm, string rawId)
        {
            Assert.Equal(new[] { rawId, displayForm }, PlayerListService.NormalizeForms(displayForm));

            // Both spellings have to agree, or the same player is two different entries.
            Assert.Equal(
                PlayerListService.NormalizeForms(rawId),
                PlayerListService.NormalizeForms(displayForm));
        }

        [Fact]
        public void NormalizeForms_KeepsAConsoleDisplayFormWithANonNumericIdVerbatim()
        {
            const string id = "X_not-a-number";

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
        public void AddToList_StoresBothLinesForAConsoleDisplayForm()
        {
            const string displayForm = "X_11400714819323198485";

            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Banned, displayForm));

            var lines = File.ReadAllLines(Path.Combine(SaveFolder, "bannedlist.txt"));
            Assert.Equal(new[] { "Xbox_1", displayForm }, lines);
        }

        [Fact]
        public void AddToList_KeepsAPlayFabIdAsASingleLine()
        {
            const string id = "PlayFab_BakaXplay_2498_3c72cce4";

            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Banned, id));

            var lines = File.ReadAllLines(Path.Combine(SaveFolder, "bannedlist.txt"));
            Assert.Equal(new[] { id }, lines);
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

            // The lazy upgrade gave each display line its raw twin on the way in, and only the
            // Xbox player's two lines went. The PlayStation player is a different id and keeps
            // both of theirs.
            Assert.Equal(new[] { "S_11400714819323198485", "PlayStation_1" }, File.ReadAllLines(path));
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
        public void UpgradeLegacyEntries_CompletesAConsoleDisplayLineAndLeavesAPlayFabIdAlone()
        {
            WriteAdminList("X_11400714819323198485", "PlayFab_BakaXplay_2498_3c72cce4");

            // The display line gains the raw twin a pre-1.0 server looks up, the same courtesy a
            // V_ Steam line gets. The PlayFab id is compared as written on either version, so it
            // has no twin to gain.
            Assert.Equal(1, Service.UpgradeLegacyEntries(SaveFolder, PlayerListType.Admin));
            Assert.Equal(
                new[] { "X_11400714819323198485", "PlayFab_BakaXplay_2498_3c72cce4", "Xbox_1" },
                AdminLines());

            Assert.Equal(0, Service.UpgradeLegacyEntries(SaveFolder, PlayerListType.Admin));
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

        // --- One value, one line ----------------------------------------------------------

        [Theory]
        // A line break in the middle would arrive at the server as two separate entries.
        [InlineData(SteamId + "\n" + SteamId)]
        [InlineData(SteamId + "\r\n// junk")]
        [InlineData(SteamId + "\rsomething")]
        // A value the server files under comments can never be an entry, so it must not be one.
        [InlineData("//" + SteamId)]
        [InlineData("// " + SteamId)]
        public void NormalizeForms_RefusesAValueThatCannotBeStoredAsOneEntry(string id)
        {
            Assert.Empty(PlayerListService.NormalizeForms(id));
        }

        /// <summary>
        /// A leading # is not a comment to the game: SyncedList.Load skips only // lines, so a
        /// #-prefixed line in permittedlist.txt is a live entry that switches the whitelist on and
        /// locks everyone else out. Refusing the value outright left that line unmatched and
        /// undeletable from the interface. It is only the WRITE that BakaLoader declines.
        /// </summary>
        [Fact]
        public void AHashPrefixedLine_IsReadAndRemovableButNeverWritten()
        {
            const string hashed = "#" + SteamId;

            Assert.Equal(new[] { hashed }, PlayerListService.NormalizeForms(hashed));

            WriteAdminList(hashed, OtherId, OtherIdV);

            // The server sees it, so the interface has to agree that it is there.
            Assert.True(ServerModel.Matches(AdminLines(), hashed));
            Assert.True(Service.IsListed(SaveFolder, PlayerListType.Admin, hashed));

            // And clicking remove has to clear it rather than reporting a change that never was.
            Assert.True(Service.RemoveFromList(SaveFolder, PlayerListType.Admin, hashed));
            Assert.Equal(new[] { OtherId, OtherIdV }, AdminLines());

            // BakaLoader still never authors one: # is the comment marker every other tool that
            // edits these files uses, and it is never part of a platform id.
            Assert.False(Service.AddToList(SaveFolder, PlayerListType.Admin, hashed));
            Assert.Equal(new[] { OtherId, OtherIdV }, AdminLines());
        }

        [Fact]
        public void NormalizeForms_TrimsALineBreakOffTheEndsRatherThanRefusing()
        {
            // Padding at the ends, a stray break included, is the caller being untidy rather than
            // a value that cannot be stored, so it is cleaned up instead of thrown away.
            Assert.Equal(new[] { SteamId, SteamIdV }, PlayerListService.NormalizeForms("\r\n" + SteamId + "\n"));
        }

        [Fact]
        public void AddToList_RefusesACraftedIdThatWouldWriteItselfTwice()
        {
            WriteAdminList(OtherId, OtherIdV);

            // Two ids wearing one id's clothes: written as given this lands as two lines, and the
            // caller is told a single player was added.
            Assert.False(Service.AddToList(SaveFolder, PlayerListType.Admin, SteamId + "\n" + SteamId));

            Assert.Equal(new[] { OtherId, OtherIdV }, AdminLines());
            Assert.Empty(AdminLines().Where(line => line == SteamId));
        }

        [Fact]
        public void AddToList_RefusesAnIdTheServerWouldReadAsAComment()
        {
            Assert.False(Service.AddToList(SaveFolder, PlayerListType.Admin, "//" + SteamIdV));
            Assert.False(Service.AddToList(SaveFolder, PlayerListType.Admin, "#" + SteamIdV));

            Assert.False(File.Exists(AdminPath));
        }

        [Fact]
        public void AddToList_RefusesAnIdCarryingALineBreakOnEveryList()
        {
            var crafted = "Xbox_2535412345678901\nXbox_2535412345678902";

            Assert.False(Service.AddToList(SaveFolder, PlayerListType.Banned, crafted));
            Assert.False(Service.AddToList(SaveFolder, PlayerListType.Permitted, crafted));

            Assert.Empty(Directory.GetFiles(SaveFolder));
        }

        // --- The upgrade is only remembered when it ran -----------------------------------

        [Fact]
        public void EnsureUpgraded_TriesAgainAfterAPassThatCouldNotRun()
        {
            WriteAdminList(SteamId);

            using (new FileStream(AdminPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // Nothing can read the file, so the upgrade cannot run and the answer is unknown.
                Assert.Null(Service.IsListed(SaveFolder, PlayerListType.Admin, SteamId));
            }

            // The file is free again, so the pass that never happened has to happen now. Marking
            // the file done on a failed pass would leave this admin without their 1.0 line for the
            // rest of the run, which on a 1.0 server means they are quietly not an admin.
            Assert.True(Service.IsListed(SaveFolder, PlayerListType.Admin, SteamId));
            Assert.Equal(new[] { SteamId, SteamIdV }, AdminLines());
        }

        [Fact]
        public void UpgradeLegacyEntries_ReportsNothingAddedWhenItCouldNotRun()
        {
            WriteAdminList(SteamId);

            using (new FileStream(AdminPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Equal(0, Service.UpgradeLegacyEntries(SaveFolder, PlayerListType.Admin));
            }

            Assert.Equal(1, Service.UpgradeLegacyEntries(SaveFolder, PlayerListType.Admin));
        }

        // --- File shape, continued --------------------------------------------------------

        [Fact]
        public void Writes_FollowTheLineEndingMostOfTheFileUsesNotTheFirstOne()
        {
            // One stray Unix break at the top of an otherwise Windows file. Following the first
            // break would rewrite every line in the file to LF over that one line.
            File.WriteAllText(AdminPath, "// admins\n" + OtherId + "\r\n" + OtherIdV + "\r\n");

            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Admin, SteamId));

            var text = File.ReadAllText(AdminPath);

            Assert.Equal(
                "// admins\r\n" + OtherId + "\r\n" + OtherIdV + "\r\n" + SteamId + "\r\n" + SteamIdV + "\r\n",
                text);

            // And nothing mixed: no bare LF is left once the Windows breaks are taken out.
            Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty));
        }

        [Fact]
        public void Writes_KeepAMostlyUnixFileOnUnixLineEndings()
        {
            File.WriteAllText(AdminPath, "// admins\r\n" + OtherId + "\n" + OtherIdV + "\n");

            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Admin, SteamId));

            Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(AdminPath));
            Assert.Equal(
                "// admins\n" + OtherId + "\n" + OtherIdV + "\n" + SteamId + "\n" + SteamIdV + "\n",
                File.ReadAllText(AdminPath));
        }

        [Fact]
        public void Writes_LeaveAByteOrderMarkOffAFileThatCameWithOne()
        {
            // The game's writer emits no mark and its reader strips one, so a marked file is not
            // broken for the game. Ours comes back the way the game would have written it.
            File.WriteAllText(AdminPath, OtherId + "\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Admin, SteamId));

            var bytes = File.ReadAllBytes(AdminPath);

            Assert.NotEqual(0xEF, bytes[0]);

            // The lazy upgrade gives the existing bare id its 1.0 twin on the way through.
            Assert.Equal(
                OtherId + "\r\n" + OtherIdV + "\r\n" + SteamId + "\r\n" + SteamIdV + "\r\n",
                File.ReadAllText(AdminPath));
        }

        // --- What each server version actually matches ------------------------------------

        /// <summary>
        /// ZNet.ListContainsId over SyncedList, as each server version runs it, transcribed from
        /// the decompiled assemblies. SyncedList.Load drops an empty line, files a line starting
        /// with <c>//</c> under comments and keeps every other line exactly as written, and
        /// Contains is a whole string compare over what is left.
        /// </summary>
        private static class ServerModel
        {
            private static List<string> Entries(IEnumerable<string> fileLines) =>
                fileLines
                    .Where(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
                    .ToList();

            /// <summary>
            /// Valheim 1.0. The Steam spelling and the bare number are tried first, and then the
            /// display filtered spelling overwrites that answer whenever filtering changed the id.
            /// That overwrite is why a 1.0 server only ever matches a Steam player on a
            /// V_&lt;steamid64&gt; line.
            /// </summary>
            public static bool Matches(IEnumerable<string> fileLines, string idString)
            {
                var entries = Entries(fileLines);
                var (platform, userId) = Parse(idString);
                var raw = platform + "_" + userId;

                var found = platform == "Steam"
                    ? entries.Contains(raw) || entries.Contains(userId)
                    : entries.Contains(raw);

                var filtered = ServerLookupForm(platform, userId);
                if (filtered != raw) found = entries.Contains(filtered);

                return found;
            }

            /// <summary>Valheim 0.9 and earlier, which had no display filter at all.</summary>
            public static bool MatchesPre10(IEnumerable<string> fileLines, string idString)
            {
                var entries = Entries(fileLines);
                var (platform, userId) = Parse(idString);
                var raw = platform + "_" + userId;

                return platform == "Steam"
                    ? entries.Contains(raw) || entries.Contains(userId)
                    : entries.Contains(raw);
            }

            /// <summary>
            /// PlatformUserID.TryParse, which splits on the first underscore and maps a one letter
            /// display prefix back to its platform, falling back to Steam for an id with no prefix.
            /// </summary>
            private static (string Platform, string UserId) Parse(string idString)
            {
                var underscore = idString.IndexOf('_');
                if (underscore <= 0 || underscore == idString.Length - 1) return ("Steam", idString);

                var prefix = idString.Substring(0, underscore);
                var userId = idString.Substring(underscore + 1);

                var platform = prefix switch
                {
                    "V" => "Steam",
                    "X" => "Xbox",
                    "S" => "PlayStation",
                    "N" => "Nintendo",
                    "A" => "GameCenter",
                    _ => prefix,
                };

                return (platform, userId);
            }
        }

        [Theory]
        [InlineData(SteamId)]
        [InlineData(SteamIdV)]
        [InlineData("Steam_" + SteamId)]
        public void StoredSteamForms_AreMatchedByBothServerVersions(string idAsTheOperatorTypesIt)
        {
            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Admin, idAsTheOperatorTypesIt));

            var lines = AdminLines();

            // Both spellings are on disk, so the one line each server version looks up is there.
            Assert.Contains(SteamId, lines);
            Assert.Contains(SteamIdV, lines);

            Assert.True(ServerModel.Matches(lines, SteamId));
            Assert.True(ServerModel.Matches(lines, SteamIdV));
            Assert.True(ServerModel.MatchesPre10(lines, SteamId));
            Assert.True(ServerModel.MatchesPre10(lines, "Steam_" + SteamId));
        }

        [Fact]
        public void ALegacyFileIsDeadOnA10ServerUntilTheUpgradeRuns()
        {
            WriteAdminList("// admins", SteamId);

            // The whole reason this service exists: the bare number still works on 0.9 and stopped
            // working on 1.0, so this operator lost their admin the day they updated.
            Assert.False(ServerModel.Matches(AdminLines(), SteamId));
            Assert.True(ServerModel.MatchesPre10(AdminLines(), SteamId));

            Assert.Equal(1, Service.UpgradeLegacyEntries(SaveFolder, PlayerListType.Admin));

            Assert.True(ServerModel.Matches(AdminLines(), SteamId));
            Assert.True(ServerModel.MatchesPre10(AdminLines(), SteamId));
        }

        [Theory]
        [InlineData("Xbox_2535412345678901")]
        [InlineData("PlayStation_4802345678901234")]
        [InlineData("Nintendo_1234567890123456")]
        [InlineData("GameCenter_9876543210987654")]
        public void StoredConsoleForms_AreMatchedByBothServerVersions(string rawId)
        {
            var path = Path.Combine(SaveFolder, "bannedlist.txt");

            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Banned, rawId));

            var lines = File.ReadAllLines(path);

            Assert.Equal(2, lines.Length);
            Assert.True(ServerModel.Matches(lines, rawId));
            Assert.True(ServerModel.MatchesPre10(lines, rawId));
        }

        [Fact]
        public void AnAlreadyFilteredConsoleForm_IsMatchedByBothServerVersions()
        {
            // X_11400714819323198485 is what 1.0 displays for Xbox user 1, so that is the line the
            // server compares when that player connects. It is stored as given, and so is the raw
            // spelling a pre-1.0 server compares: the display form is not one way after all, since
            // Splatform's multiplier is odd and the raw id divides back out of it exactly.
            const string displayForm = "X_11400714819323198485";
            var path = Path.Combine(SaveFolder, "bannedlist.txt");

            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Banned, displayForm));
            Assert.Equal(new[] { "Xbox_1", displayForm }, File.ReadAllLines(path));

            Assert.True(ServerModel.Matches(File.ReadAllLines(path), "Xbox_1"));

            // Which is what keeps the ban working if the profile is ever rolled back to a build
            // from before 1.0. Stored one way round it silently stopped applying there.
            Assert.True(ServerModel.MatchesPre10(File.ReadAllLines(path), "Xbox_1"));
        }

        [Fact]
        public void APlayFabId_IsMatchedVerbatimByBothServerVersions()
        {
            const string id = "PlayFab_BakaXplay_2498_3c72cce4";
            var path = Path.Combine(SaveFolder, "bannedlist.txt");

            Assert.True(Service.AddToList(SaveFolder, PlayerListType.Banned, id));
            Assert.Equal(new[] { id }, File.ReadAllLines(path));

            // The display filter only rewrites an id it can read as a number, so this one is
            // compared exactly as written on either server.
            Assert.True(ServerModel.Matches(File.ReadAllLines(path), id));
            Assert.True(ServerModel.MatchesPre10(File.ReadAllLines(path), id));
        }

        [Fact]
        public void ACommentedOutForm_IsInvisibleToTheServerAndIsNeverWritten()
        {
            WriteAdminList("//" + SteamIdV);

            Assert.False(ServerModel.Matches(AdminLines(), SteamId));
            Assert.False(Service.IsListed(SaveFolder, PlayerListType.Admin, SteamId));

            // Which is exactly why a value that reads as a comment is refused rather than written
            // and then reported as a player who was added.
            Assert.False(Service.AddToList(SaveFolder, PlayerListType.Admin, "//" + SteamIdV));
            Assert.Equal(new[] { "//" + SteamIdV }, AdminLines());
        }
    }
}
