using ValheimBakaLoader.Tools.Atlas;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools.Atlas
{
    /// <summary>
    /// Guards the two outer biome caps of the Atlas against regressions in the worldgen port.
    /// Valheim 1.0 stopped promoting high Deep North ground to Mountain, so the whole northern
    /// cap must classify as DeepNorth (or water); the Ashlands cap did not change in 1.0 and must
    /// keep producing AshLands land. Sampled on a 100 m grid for the live reference seed.
    /// </summary>
    public class AtlasBiomeCapTests
    {
        private const int RefSeed = 649688311;

        [Fact]
        public void DeepNorthRegion_NeverClassifiesAsMountain()
        {
            var gen = new WorldGen(RefSeed);
            int deepNorth = 0, mountain = 0, land = 0;
            for (int y = 6000; y <= 10000; y += 100)
            for (int x = -10000; x <= 10000; x += 100)
            {
                if (x * (long)x + y * (long)y > 10000L * 10000L) continue;
                if (!WorldGen.IsDeepnorth(x, y)) continue;
                var b = gen.GetBiome(x, y);
                if (b == Biome.Ocean) continue;
                land++;
                if (b == Biome.DeepNorth) deepNorth++;
                if (b == Biome.Mountain) mountain++;
            }

            Assert.True(land > 500, $"expected a real northern cap, sampled only {land} land points");
            Assert.Equal(0, mountain);
            Assert.Equal(land, deepNorth);
        }

        [Fact]
        public void AshlandsRegion_ProducesAshlandsLand()
        {
            var gen = new WorldGen(RefSeed);
            int ash = 0, land = 0;
            for (int y = -10000; y <= -6000; y += 100)
            for (int x = -10000; x <= 10000; x += 100)
            {
                if (x * (long)x + y * (long)y > 10000L * 10000L) continue;
                if (!WorldGen.IsAshlands(x, y)) continue;
                var b = gen.GetBiome(x, y);
                if (b == Biome.Ocean) continue;
                land++;
                if (b == Biome.AshLands) ash++;
            }

            Assert.True(land > 500, $"expected a real southern cap, sampled only {land} land points");
            Assert.Equal(land, ash);
        }

        [Fact]
        public void DeepNorthHeight_PregenerateAndFinalPathsDiffer()
        {
            // 1.0 split the Deep North height into a pregeneration formula (the old one, plus 0.1
            // when not river-pregenerating) and a new final formula. If both paths ever collapse
            // into one again the streams and the rendered terrain silently revert to the old look.
            var gen = new WorldGen(RefSeed);
            float pre = gen.GetPregenerationHeight(0f, 9500f, riverPreGen: false);
            float preRiver = gen.GetPregenerationHeight(0f, 9500f, riverPreGen: true);
            float final = gen.GetHeight(0f, 9500f);
            Assert.NotEqual(pre, preRiver);
            Assert.True(final > 0f, "the northern cap at (0, 9500) should be land");
        }
    }
}
