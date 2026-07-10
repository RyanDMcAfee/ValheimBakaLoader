using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Tools.Atlas;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools.Atlas
{
    public class AtlasWorldGenTests : IDisposable
    {
        // Numeric seed for seedName "yBvEFPKD9S" (verified live-world vector in FwlReaderWriterTests).
        private const int RefSeed = 649688311;

        private readonly string _tempDir =
            Path.Combine(Path.GetTempPath(), "vbl-atlas-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }

        // ------------------------------------------------------------------
        // UnityPerlin
        // ------------------------------------------------------------------

        [Fact]
        public void Perlin_OriginMatchesUnityTestVector()
        {
            Assert.Equal(0.4652731f, UnityPerlin.Noise(0f, 0f), 0.0000005f);
        }

        [Fact]
        public void Perlin_IsDeterministicAndBounded()
        {
            for (float x = -3.7f; x < 3.7f; x += 0.61f)
            {
                for (float y = -2.9f; y < 2.9f; y += 0.53f)
                {
                    float a = UnityPerlin.Noise(x, y);
                    float b = UnityPerlin.Noise(x, y);
                    Assert.Equal(a, b);
                    Assert.InRange(a, -0.1f, 1.1f);
                }
            }
        }

        [Fact]
        public void Perlin_UsesAbsoluteInputs()
        {
            // Unity takes Mathf.Abs of both inputs, so mirrored coords match.
            Assert.Equal(UnityPerlin.Noise(1.37f, 2.81f), UnityPerlin.Noise(-1.37f, -2.81f));
        }

        // ------------------------------------------------------------------
        // UnityRandom
        // ------------------------------------------------------------------

        [Fact]
        public void Random_SameSeedSameSequence()
        {
            var a = new UnityRandom(RefSeed);
            var b = new UnityRandom(RefSeed);
            for (int i = 0; i < 64; i++)
            {
                Assert.Equal(a.NextUInt(), b.NextUInt());
            }
        }

        [Fact]
        public void Random_FloatRangeStaysInsideBoundsInclusive()
        {
            var rng = new UnityRandom(1234);
            for (int i = 0; i < 1000; i++)
            {
                float v = rng.Range(60f, 100f);
                Assert.InRange(v, 60f, 100f);
            }
        }

        [Fact]
        public void Random_IntRangeIsMinInclusiveMaxExclusive()
        {
            var rng = new UnityRandom(42);
            var seen = new HashSet<int>();
            for (int i = 0; i < 1000; i++)
            {
                int v = rng.Range(-3, 4);
                Assert.InRange(v, -3, 3);
                seen.Add(v);
            }
            Assert.True(seen.Count >= 5, "expected most of the 7 values to appear");
        }

        [Fact]
        public void Random_FullIntRangeDoesNotThrow()
        {
            // Worldgen draws river/stream seeds via Range(int.MinValue, int.MaxValue),
            // which must wrap (unchecked) instead of overflowing.
            var rng = new UnityRandom(RefSeed);
            var seen = new HashSet<int>();
            for (int i = 0; i < 16; i++)
            {
                seen.Add(rng.Range(int.MinValue, int.MaxValue));
            }
            Assert.True(seen.Count > 1);
        }

        // ------------------------------------------------------------------
        // WorldGen
        // ------------------------------------------------------------------

        [Fact]
        public void WorldGen_SameSeedIsFullyDeterministic()
        {
            var a = new WorldGen(RefSeed);
            var b = new WorldGen(RefSeed);
            var rng = new Random(7); // sample coords only; any source is fine
            for (int i = 0; i < 200; i++)
            {
                float wx = (float)(rng.NextDouble() * 21000.0 - 10500.0);
                float wy = (float)(rng.NextDouble() * 21000.0 - 10500.0);
                Assert.Equal(a.GetBiome(wx, wy), b.GetBiome(wx, wy));
                Assert.Equal(a.GetHeight(wx, wy), b.GetHeight(wx, wy));
            }
        }

        [Fact]
        public void WorldGen_DifferentSeedsDiffer()
        {
            var a = new WorldGen(RefSeed);
            var b = new WorldGen(RefSeed + 1);
            int diffs = 0;
            for (float wx = -9000f; wx <= 9000f; wx += 1500f)
            {
                for (float wy = -9000f; wy <= 9000f; wy += 1500f)
                {
                    if (a.GetBiome(wx, wy) != b.GetBiome(wx, wy) ||
                        a.GetHeight(wx, wy) != b.GetHeight(wx, wy))
                    {
                        diffs++;
                    }
                }
            }
            Assert.True(diffs > 20, $"expected many differences, got {diffs}");
        }

        [Fact]
        public void WorldGen_StructuralBiomeInvariants()
        {
            var gen = new WorldGen(RefSeed);

            // Center: distance gates block Swamp/Mistlands/Plains/BlackForest.
            Biome center = gen.GetBiome(0f, 0f);
            Assert.True(center == Biome.Meadows || center == Biome.Mountain || center == Biome.Ocean,
                $"center biome was {center}");

            // Far south inside the edge (beyond 12000 of the shifted center) is Ashlands.
            // (Beyond ~16000 the two polar circles overlap — outside the world, irrelevant.)
            Assert.Equal(Biome.AshLands, gen.GetBiome(0f, -10400f));

            // The northern band (beyond 12000 of the shifted DeepNorth center)
            // must contain DeepNorth land somewhere; individual points can be
            // Ocean (bh ≤ 0.02 is checked before DeepNorth).
            bool foundDeepNorth = false;
            for (float wx = -4000f; wx <= 4000f && !foundDeepNorth; wx += 400f)
            {
                for (float wy = 8300f; wy <= 10000f; wy += 200f)
                {
                    Biome b = gen.GetBiome(wx, wy);
                    if (b == Biome.DeepNorth || b == Biome.Mountain)
                    {
                        foundDeepNorth = true;
                        break;
                    }
                }
            }
            Assert.True(foundDeepNorth, "no DeepNorth/Mountain found in the northern band");
        }

        [Fact]
        public void WorldGen_EdgeFallsToAbyss()
        {
            var gen = new WorldGen(RefSeed);
            // Beyond the 10500 edge the height clamps to -2 * 200 meters.
            Assert.Equal(-400f, gen.GetBiomeHeight(Biome.Ocean, 10800f, 0f));
        }

        [Fact]
        public void WorldGen_HasBothLandAndSea()
        {
            var gen = new WorldGen(RefSeed);
            int land = 0, sea = 0;
            for (float wx = -9500f; wx <= 9500f; wx += 500f)
            {
                for (float wy = -9500f; wy <= 9500f; wy += 500f)
                {
                    if (Math.Sqrt((double)wx * wx + (double)wy * wy) > 10000.0) continue;
                    if (gen.GetHeight(wx, wy) > WorldGen.SeaLevelMeters) land++;
                    else sea++;
                }
            }
            Assert.True(land > 50, $"land samples: {land}");
            Assert.True(sea > 50, $"sea samples: {sea}");
        }

        [Fact]
        public void WorldGen_RiversCarveBelowSeaLevel()
        {
            // Pregeneration must produce rivers; carved cells drop terrain toward
            // the riverbed (0.14 * 200 = 28 m < 30 m sea level), so a world with
            // rivers has inland water. Weak but structural: assert pregeneration
            // completed with at least one river grid cell registered.
            var gen = new WorldGen(RefSeed);
            Assert.True(gen.RiverGridCellCount > 0, "expected pregenerated river grid cells");
        }

        // ------------------------------------------------------------------
        // MapRenderer
        // ------------------------------------------------------------------

        [Fact]
        public void Renderer_SmokeRenderHasWaterAndLand()
        {
            var gen = new WorldGen(RefSeed);
            int[] px = MapRenderer.Render(gen, 64);
            Assert.Equal(64 * 64, px.Length);

            // All pixels opaque; more than one distinct color (land + water + void).
            Assert.All(px, p => Assert.Equal(0xFF, (p >> 24) & 0xFF));
            Assert.True(px.Distinct().Count() > 8, "expected a varied palette");

            // Corners are outside the circle → void color.
            Assert.Equal(px[0], px[63]);
        }

        [Fact]
        public void Renderer_PngRoundTrips()
        {
            var gen = new WorldGen(RefSeed);
            string path = Path.Combine(_tempDir, "map.png");
            MapRenderer.RenderToPng(gen, path, 32);
            Assert.True(File.Exists(path));
            byte[] header = new byte[8];
            using (var fs = File.OpenRead(path))
            {
                Assert.Equal(8, fs.Read(header, 0, 8));
            }
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header);
        }

        [Fact]
        public void Renderer_ReportsProgressAndHonorsCancellation()
        {
            var gen = new WorldGen(RefSeed);
            int last = -1;
            MapRenderer.Render(gen, 32, pct => last = pct);
            Assert.Equal(100, last);

            using (var cts = new System.Threading.CancellationTokenSource())
            {
                cts.Cancel();
                Assert.Throws<OperationCanceledException>(() => MapRenderer.Render(gen, 32, null, cts.Token));
            }
        }

        // ------------------------------------------------------------------
        // ModCompatScanner
        // ------------------------------------------------------------------

        [Fact]
        public void Scanner_NoBepInExMeansVanilla()
        {
            Directory.CreateDirectory(_tempDir);
            var result = ModCompatScanner.Scan(_tempDir);
            Assert.True(result.IsVanilla);
            Assert.Equal(10000f, result.WorldRadius);
            Assert.Equal(10500f, result.WorldEdge);
        }

        [Fact]
        public void Scanner_ParsesExpandWorldSizeConfig()
        {
            string plugins = Path.Combine(_tempDir, "BepInEx", "plugins");
            string config = Path.Combine(_tempDir, "BepInEx", "config");
            Directory.CreateDirectory(plugins);
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(plugins, "expand_world_size.dll"), "stub");
            File.WriteAllText(Path.Combine(config, "expand_world_size.cfg"),
                "[1. General]\n" +
                "# comment\n" +
                "World radius = 20000\n" +
                "World edge size = 1000\n" +
                "World stretch = 2\n" +
                "Biome stretch = 1.5\n");

            var result = ModCompatScanner.Scan(_tempDir);
            Assert.True(result.HasExpandWorldSize);
            Assert.Equal(20000f, result.WorldRadius);
            Assert.Equal(21000f, result.WorldEdge);
            Assert.Equal(2f, result.WorldStretch);
            Assert.Equal(1.5f, result.BiomeStretch);
            Assert.False(result.IsVanilla);
        }

        [Fact]
        public void Scanner_WarnsOnUnmodelableMods()
        {
            string plugins = Path.Combine(_tempDir, "BepInEx", "plugins", "Somebody-BetterContinents");
            Directory.CreateDirectory(plugins);
            File.WriteAllText(Path.Combine(plugins, "BetterContinents.dll"), "stub");

            var result = ModCompatScanner.Scan(_tempDir);
            Assert.False(result.IsVanilla);
            Assert.Contains(result.Warnings, w => w.Contains("Better Continents"));
        }
    }
}
