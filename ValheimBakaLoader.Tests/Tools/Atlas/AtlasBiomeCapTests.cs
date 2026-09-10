using System;
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

        /// <summary>
        /// A second and third seed for the checks that are meant to hold whatever
        /// the world is, so a single lucky seed cannot carry them.
        /// </summary>
        private static readonly int[] OtherSeeds = { 1580707604, 1 };

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
            int ash = 0, sampled = 0, aboveSeaLevel = 0, belowSeaLevel = 0;
            for (int y = -10000; y <= -6000; y += 100)
            for (int x = -10000; x <= 10000; x += 100)
            {
                if (x * (long)x + y * (long)y > 10000L * 10000L) continue;
                if (!WorldGen.IsAshlands(x, y)) continue;
                var b = gen.GetBiome(x, y);
                if (b == Biome.Ocean) continue;
                sampled++;
                if (b == Biome.AshLands) ash++;

                // The height the map is actually drawn from: GetBiomeHeight with
                // preGeneration left at false, the same call MapRenderer makes.
                if (gen.GetBiomeHeight(b, x, y) >= WorldGen.SeaLevelMeters) aboveSeaLevel++;
                else belowSeaLevel++;
            }

            Assert.True(sampled > 500, $"expected a real southern cap, sampled only {sampled} points");
            Assert.Equal(sampled, ash);

            // GetBiome hands back AshLands for the whole ring whatever the ground
            // does, so the count above only proves the classifier ran. What makes
            // the cap render as a continent is the height: the real formula puts
            // roughly a quarter to a third of the ring above sea level (773 of
            // 2689 sampled points on this seed), and the rest is the ocean around
            // it. Both sides have to be there or the map is drawing a solid slab
            // or an empty sea.
            Assert.True(aboveSeaLevel > 400,
                $"the Ashlands cap should render land, only {aboveSeaLevel} of {sampled} points are above sea level");
            Assert.True(belowSeaLevel > 400,
                $"the Ashlands cap should be surrounded by ocean, only {belowSeaLevel} of {sampled} points are below sea level");
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

            // The point of the test, and what its name has always claimed: the
            // rendered height is not either pregeneration height.
            Assert.NotEqual(final, pre);
            Assert.NotEqual(final, preRiver);

            // One point could differ by luck, so the whole cap is swept. Every
            // Deep North sample must differ, and the two formulas must be far
            // apart over a good part of it rather than a rounding step: 1085 of
            // 1876 sampled points move by more than 5 m on this seed.
            int sampled = 0, differ = 0, moreThanFiveMetres = 0;
            for (int y = 6000; y <= 10000; y += 100)
            for (int x = -10000; x <= 10000; x += 100)
            {
                if (x * (long)x + y * (long)y > 10000L * 10000L) continue;
                if (gen.GetBiome(x, y) != Biome.DeepNorth) continue;
                sampled++;
                float f = gen.GetBiomeHeight(Biome.DeepNorth, x, y);
                float p = gen.GetBiomeHeight(Biome.DeepNorth, x, y, preGeneration: true);
                if (f != p) differ++;
                if (Math.Abs(f - p) > 5f) moreThanFiveMetres++;
            }

            Assert.True(sampled > 500, $"expected a real northern cap, sampled only {sampled} points");
            Assert.Equal(sampled, differ);
            Assert.True(moreThanFiveMetres > sampled / 4,
                $"only {moreThanFiveMetres} of {sampled} Deep North points move by more than 5 m");
        }

        // ------------------------------------------------------------------
        // Ashlands: pregeneration versus the real terrain
        // ------------------------------------------------------------------

        /// <summary>
        /// Distance from the ring the real Ashlands terrain is built around.
        /// GetAshlandsHeight measures Length(x, y + yOffset - yOffset * 0.3)
        /// against AshlandsMinDistance + WorldAngle * 100 and fades the land
        /// out over 1000 m either side of it (decomp_new :151346-151351), so
        /// this is the one number that says whether a point is on the Ashlands
        /// landmass or in the water around it. yOffset is -4000, so the ring is
        /// centred 2800 m south of the origin: at x = 0 the angle term is
        /// sin(atan2(0, y) * 20) * 100, which is zero for a southern point, and
        /// the ring sits at y = -9200.
        /// </summary>
        private static double BandOffset(float x, float y)
        {
            double angle = (double)WorldGen.WorldAngle(x, y) * 100.0;
            double shiftedY = (double)y + (-4000.0) - (-4000.0) * 0.3;
            return Math.Sqrt((double)x * x + shiftedY * shiftedY) - (12000.0 + angle);
        }

        [Fact]
        public void AshlandsHeight_PregenerateAndFinalPathsDiffer()
        {
            // Ashlands got the same treatment as the Deep North: a pregeneration
            // formula for laying out rivers and a completely different one for
            // the ground (decomp_new GetBiomeHeight :151179-151184). If the
            // AshLands case ever stops branching on preGeneration the map goes
            // back to drawing a coastline no player will find.
            var gen = new WorldGen(RefSeed);

            // (0, -9200) is the centre of the ring the real formula builds its
            // landmass on, so it is where the two formulas have the most to
            // disagree about.
            Assert.NotEqual(gen.GetBiomeHeight(Biome.AshLands, 0f, -9200f),
                            gen.GetBiomeHeight(Biome.AshLands, 0f, -9200f, preGeneration: true));

            int sampled = 0, moreThanFiveMetres = 0;
            for (int y = -10000; y <= -6000; y += 100)
            for (int x = -10000; x <= 10000; x += 100)
            {
                if (x * (long)x + y * (long)y > 10000L * 10000L) continue;
                if (gen.GetBiome(x, y) != Biome.AshLands) continue;
                sampled++;
                float f = gen.GetBiomeHeight(Biome.AshLands, x, y);
                float p = gen.GetBiomeHeight(Biome.AshLands, x, y, preGeneration: true);
                if (Math.Abs(f - p) > 5f) moreThanFiveMetres++;
            }

            Assert.True(sampled > 500, $"expected a real southern cap, sampled only {sampled} points");

            // 2444 of 2689 on this seed. A stray handful can agree where the
            // gap multiplier pins both to zero, so this is not "all of them",
            // but nine in ten is far outside anything two versions of the same
            // formula could produce.
            Assert.True(moreThanFiveMetres > sampled * 9 / 10,
                $"only {moreThanFiveMetres} of {sampled} Ashlands points move by more than 5 m, "
                + "which is what happens when the AshLands case stops branching on preGeneration");
        }

        [Fact]
        public void AshlandsHeight_FinalFormulaRedrawsTheCoastline()
        {
            // The two formulas do not just texture the ground differently, they
            // put the water line somewhere else, which is the whole reason this
            // had to be ported rather than approximated. Measured on the
            // reference seed: 773 of 2689 sampled points are land under the real
            // formula against 1913 under the pregeneration one.
            var gen = new WorldGen(RefSeed);
            int sampled = 0, finalLand = 0, pregenLand = 0, disagree = 0;
            for (int y = -10000; y <= -6000; y += 100)
            for (int x = -10000; x <= 10000; x += 100)
            {
                if (x * (long)x + y * (long)y > 10000L * 10000L) continue;
                if (gen.GetBiome(x, y) != Biome.AshLands) continue;
                sampled++;
                bool f = gen.GetBiomeHeight(Biome.AshLands, x, y) >= WorldGen.SeaLevelMeters;
                bool p = gen.GetBiomeHeight(Biome.AshLands, x, y, preGeneration: true) >= WorldGen.SeaLevelMeters;
                if (f) finalLand++;
                if (p) pregenLand++;
                if (f != p) disagree++;
            }

            Assert.True(sampled > 500, $"expected a real southern cap, sampled only {sampled} points");
            Assert.True(finalLand > 400, $"the real formula should still leave a continent, got {finalLand} land points");
            Assert.True(finalLand < pregenLand * 3 / 5,
                $"the real Ashlands is far smaller than the pregeneration one, got {finalLand} against {pregenLand}");
            Assert.True(disagree > sampled / 3,
                $"only {disagree} of {sampled} points changed side of the water line");
        }

        [Fact]
        public void AshlandsHeight_LandFollowsTheRingAndTheOuterEdgeIsOcean()
        {
            // Two predictions read straight off GetAshlandsHeight, both of which
            // hold for any seed because the terms that drive them do not use the
            // world offsets:
            //
            //   * Past 10150 m the height is lerped towards -1 over 600 m
            //     (decomp_new :151356-151357). By 10400 m that lerp keeps at
            //     most 0.624 of a height whose own ceiling is 0.25 * baseHeight
            //     + 0.6125, and baseHeight is already being pulled to -0.2 out
            //     there, so nothing in the annulus can reach the 0.15 that sea
            //     level sits at. The Ashlands must end in water before the world
            //     edge, which the pregeneration formula never guaranteed.
            //
            //   * More than 1000 m off the ring the band term bottoms out at the
            //     0.1 floor of its smoothstep, which caps the terrain
            //     contribution at 0.075 and leaves the height at roughly
            //     0.25 * baseHeight + 0.19 before the simplex scale, below the
            //     water line for any ground the far south actually has. So the
            //     landmass follows the ring and there is no Ashlands island
            //     stranded away from it.
            foreach (int seed in new[] { RefSeed, OtherSeeds[0], OtherSeeds[1] })
            {
                var gen = new WorldGen(seed);

                int outerSampled = 0, outerLand = 0;
                for (int y = -10500; y <= 0; y += 25)
                for (int x = -10500; x <= 10500; x += 25)
                {
                    double r = Math.Sqrt((double)x * x + (double)y * y);
                    if (r < 10400 || r > 10500) continue;
                    if (!WorldGen.IsAshlands(x, y)) continue;
                    outerSampled++;
                    if (gen.GetBiomeHeight(Biome.AshLands, x, y) >= WorldGen.SeaLevelMeters) outerLand++;
                }

                Assert.True(outerSampled > 1000, $"seed {seed}: sampled only {outerSampled} points in the outer annulus");
                Assert.Equal(0, outerLand);

                int coreSampled = 0, coreLand = 0, offRingSampled = 0, offRingLand = 0;
                for (int y = -10000; y <= -6000; y += 50)
                for (int x = -10000; x <= 10000; x += 50)
                {
                    if (x * (long)x + y * (long)y > 10000L * 10000L) continue;
                    if (!WorldGen.IsAshlands(x, y)) continue;
                    bool land = gen.GetBiomeHeight(Biome.AshLands, x, y) >= WorldGen.SeaLevelMeters;
                    double offset = Math.Abs(BandOffset(x, y));
                    if (offset < 250)
                    {
                        coreSampled++;
                        if (land) coreLand++;
                    }
                    else if (offset > 900)
                    {
                        offRingSampled++;
                        if (land) offRingLand++;
                    }
                }

                Assert.True(coreSampled > 500, $"seed {seed}: sampled only {coreSampled} points on the ring");
                Assert.True(offRingSampled > 500, $"seed {seed}: sampled only {offRingSampled} points off the ring");

                // Measured 34.5% to 51.1% on the ring across seven seeds
                // including int.MaxValue-scale ones, and 0 of 1282 off it.
                Assert.True(coreLand > coreSampled / 4,
                    $"seed {seed}: only {coreLand} of {coreSampled} points on the ring are land");
                Assert.Equal(0, offRingLand);
            }
        }
    }
}
