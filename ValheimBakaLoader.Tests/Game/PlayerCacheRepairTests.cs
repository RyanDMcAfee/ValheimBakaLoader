using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Data;
using Xunit;
using Xunit.Abstractions;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The half of the encoding fix that reads: a players cache written while the app read the
    /// server's output with the machine's code page is full of broken names, and reading
    /// correctly from now on mends nothing that is already on disk. The roster, the row menu
    /// and every action that names a player read from this file.
    /// <para>
    /// The fixture is a real players-cache.json carrying the reported name in both spellings,
    /// an honest French name that must survive untouched, and the same player filed twice under
    /// two keys, which is how a person who was seen before the fix and again after it ends up
    /// as two rows.
    /// </para>
    /// <para>
    /// The fixture was broken on Windows-1252, so every test that drives the repair over it
    /// names that page. Left to the machine's own, this whole file would pass here and fail on
    /// a Russian or a Japanese host, where refusing a 1252 shaped name is the correct answer.
    /// The one exception is the end to end test at the bottom, which goes through the real
    /// load and so reads with whatever page this machine has; it says why and stops when that
    /// page is not the one the fixture was made on.
    /// </para>
    /// </summary>
    public class PlayerCacheRepairTests : BaseTest
    {
        private readonly ITestOutputHelper Output;

        public PlayerCacheRepairTests(ITestOutputHelper output) => Output = output;

        private static string FixturePath =>
            Path.Combine(Directory.GetCurrentDirectory(), "Resources", "mojibake", "players-cache.json");

        private static Dictionary<string, PlayerInfo> Fixture()
        {
            var file = JsonConvert.DeserializeObject<KeyedDataFile<PlayerInfo>>(File.ReadAllText(FixturePath));
            Assert.NotNull(file);
            Assert.Equal(4, file.Data.Count);
            return file.Data;
        }

        [Fact]
        public void The_fixture_really_does_hold_the_broken_name()
        {
            // A fixture that was fixed up by an editor on its way into the repository would
            // make every test below pass while proving nothing at all.
            var loaded = Fixture();
            Assert.Equal(Mojibake.GreekBroken, loaded["Steam:76561198000000001"].PlayerName);
            Assert.Equal(Mojibake.CyrillicBroken, loaded["Steam:76561198000000003"].PlayerName);
            Assert.Equal(Mojibake.French, loaded["Steam:76561198000000002"].PlayerName);
        }

        [Fact]
        public void Every_stored_name_is_put_back_and_an_honest_one_is_left_alone()
        {
            var repaired = PlayerDataRepository.RepairNames(Fixture(), out var names, out _, TextRepair.Page(1252));

            var greek = repaired["Steam:76561198000000001"];
            Assert.Equal(Mojibake.Greek, greek.PlayerName);
            Assert.Equal(Mojibake.Greek, greek.LastStatusCharacter);

            var french = repaired["Steam:76561198000000002"];
            Assert.Equal(Mojibake.French, french.PlayerName);
            Assert.Equal(Mojibake.French, french.LastStatusCharacter);
            Assert.Equal(Mojibake.French, Assert.Single(french.Characters).CharacterName);

            Assert.True(names > 0, "nothing was reported as repaired");
        }

        /// <summary>
        /// The same character under both spellings inside one record is one character. Left as
        /// two, the row menu offers the same viking twice and one of the two reaches nobody.
        /// The confident pairing survives the fold, because the record did know which platform
        /// id that character belongs to.
        /// </summary>
        [Fact]
        public void Two_spellings_of_one_character_collapse_into_one()
        {
            var repaired = PlayerDataRepository.RepairNames(Fixture(), out _, out _, TextRepair.Page(1252));

            var character = Assert.Single(repaired["Steam:76561198000000001"].Characters);
            Assert.Equal(Mojibake.Greek, character.CharacterName);
            Assert.True(character.MatchConfident);
        }

        /// <summary>
        /// Two records, one platform id: the repaired one folds into the one that was already
        /// correct. What the record that saw the player LAST knows wins, and what only the
        /// other one knows is kept rather than thrown away.
        /// </summary>
        [Fact]
        public void A_player_filed_twice_under_one_platform_id_becomes_one_record()
        {
            var repaired = PlayerDataRepository.RepairNames(Fixture(), out _, out var merged, TextRepair.Page(1252));

            Assert.Equal(1, merged);
            Assert.Equal(3, repaired.Count);
            Assert.False(repaired.ContainsKey("legacy-76561198000000003"));

            var one = repaired["Steam:76561198000000003"];
            Assert.Equal(Mojibake.Cyrillic, one.PlayerName);
            Assert.Equal("1454938750", one.PlayerNumericId);
            Assert.Equal("Final Sunset", one.ServerKey);
            Assert.Equal(Mojibake.Cyrillic, Assert.Single(one.Characters).CharacterName);
        }

        /// <summary>
        /// Running it a second time must change nothing, because it runs at every launch.
        /// </summary>
        [Fact]
        public void A_repaired_cache_is_left_alone_on_the_next_launch()
        {
            var once = PlayerDataRepository.RepairNames(Fixture(), out _, out _, TextRepair.Page(1252));
            var twice = PlayerDataRepository.RepairNames(once, out var names, out var merged, TextRepair.Page(1252));

            Assert.Equal(0, names);
            Assert.Equal(0, merged);
            Assert.Equal(once.Count, twice.Count);
        }

        [Fact]
        public void A_missing_or_empty_cache_is_not_a_failure()
        {
            Assert.Empty(PlayerDataRepository.RepairNames(null, out _, out _, TextRepair.Page(1252)));
            Assert.Empty(PlayerDataRepository.RepairNames(new Dictionary<string, PlayerInfo>(), out _, out _, TextRepair.Page(1252)));
        }

        /// <summary>
        /// And the whole way through, from a file to the collection the app queries: the repair
        /// has to be wired into the load, not merely available to it.
        /// <para>
        /// The one test here that names no page, because the load is the app's own and reads
        /// with the machine's. That ties it to a Western machine and nothing can be done about
        /// it without stopping it from being the end to end test: the fixture's names were
        /// broken on 1252, and a host on 1251 or 932 is RIGHT to leave them exactly as they
        /// are. So it says so and stops there rather than failing for a correct refusal. The
        /// wiring itself is still covered everywhere the page is named.
        /// </para>
        /// </summary>
        [Fact]
        public async Task The_repository_repairs_the_file_as_it_loads_it()
        {
            if (TextRepair.AnsiPage.CodePage != 1252)
            {
                Output.WriteLine(
                    "Not run: this machine reads in code page " + TextRepair.AnsiPage.CodePage +
                    ", and the fixture's names were broken on 1252. Leaving them alone is the " +
                    "correct answer on this machine, so there is nothing here to prove.");
                return;
            }

            await MockDataFileProvider.SaveAsync("players-cache.json", new KeyedDataFile<PlayerInfo>(Fixture()));

            var repository = GetService<IPlayerDataRepository>();
            await repository.LoadAsync();

            var players = repository.Data.ToList();
            Assert.Equal(3, players.Count);
            Assert.Contains(players, p => p.PlayerName == Mojibake.Greek);
            Assert.Contains(players, p => p.PlayerName == Mojibake.Cyrillic);
            Assert.Contains(players, p => p.PlayerName == Mojibake.French);
            Assert.DoesNotContain(players, p => p.PlayerName == Mojibake.GreekBroken);

            // The name the roster and every row action reach for is the repaired one, and it
            // is findable by it: this is the lookup a kick by name used to fail.
            var found = repository.FindPlayersByQuery(new PlayerDataQuery { CharacterName = Mojibake.Greek });
            Assert.Single(found);
        }
    }
}
