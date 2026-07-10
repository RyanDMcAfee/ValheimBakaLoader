using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ValheimBakaLoader.Tools.Atlas
{
    /// <summary>
    /// Renders a top-down biome/terrain map from a WorldGen instance.
    /// Row 0 is north (+wy). Pixels span the playable disc plus the edge
    /// (world coords -10500..+10500). Rendering is row-parallel; WorldGen
    /// is read-only after construction so no locking is needed.
    /// </summary>
    public static class MapRenderer
    {
        private const float Extent = WorldGen.WorldEdge; // 10500

        // BakaLoader Atlas palette (ARGB) — our own colors, not the game's.
        private const int ColMeadows = unchecked((int)0xFF5E8A3D);
        private const int ColBlackForest = unchecked((int)0xFF34502F);
        private const int ColSwamp = unchecked((int)0xFF5E5440);
        private const int ColMountain = unchecked((int)0xFFDADEE2);
        private const int ColPlains = unchecked((int)0xFFBDA95F);
        private const int ColMistlands = unchecked((int)0xFF69607A);
        private const int ColAshlands = unchecked((int)0xFF80352A);
        private const int ColDeepNorth = unchecked((int)0xFFC6D2DA);
        private const int ColShallow = unchecked((int)0xFF3A6E93);
        private const int ColDeep = unchecked((int)0xFF0E2A4A);
        private const int ColSand = unchecked((int)0xFFC9B183);
        private const int ColVoid = unchecked((int)0xFF080C12);

        /// <summary>
        /// Renders the world into a 32bpp ARGB pixel buffer (row-major,
        /// sizePx * sizePx). progress receives 0..100.
        /// </summary>
        public static int[] Render(WorldGen gen, int sizePx = 2048, Action<int> progress = null, CancellationToken ct = default)
        {
            return Render(gen, null, sizePx, progress, ct);
        }

        /// <summary>
        /// Render with mod adaptation: when Expand World Size is detected the
        /// map spans the modded radius and terrain/biome sampling is divided
        /// by the configured stretch factors (map-scale approximation of the
        /// mod's coordinate remapping).
        /// </summary>
        public static int[] Render(WorldGen gen, ModCompatResult compat, int sizePx = 2048, Action<int> progress = null, CancellationToken ct = default)
        {
            if (gen == null) throw new ArgumentNullException(nameof(gen));
            if (sizePx < 16 || sizePx > 8192) throw new ArgumentOutOfRangeException(nameof(sizePx));

            float extent = compat != null && compat.HasExpandWorldSize ? compat.WorldEdge : Extent;
            float worldStretch = compat != null && compat.WorldStretch > 0f ? compat.WorldStretch : 1f;
            float biomeStretch = compat != null && compat.BiomeStretch > 0f ? compat.BiomeStretch : 1f;

            int[] pixels = new int[sizePx * sizePx];
            float step = extent * 2f / sizePx;
            int rowsDone = 0;
            int lastReported = -1;

            var opts = new ParallelOptions { CancellationToken = ct };
            Parallel.For(0, sizePx, opts, py =>
            {
                float wy = extent - (py + 0.5f) * step; // row 0 = north
                int baseIdx = py * sizePx;
                for (int px = 0; px < sizePx; px++)
                {
                    float wx = -extent + (px + 0.5f) * step;
                    pixels[baseIdx + px] = SamplePixel(gen, wx, wy, extent, worldStretch, biomeStretch);
                }

                int done = Interlocked.Increment(ref rowsDone);
                if (progress != null)
                {
                    int pct = done * 100 / sizePx;
                    if (pct != Volatile.Read(ref lastReported))
                    {
                        Volatile.Write(ref lastReported, pct);
                        progress(pct);
                    }
                }
            });

            return pixels;
        }

        /// <summary>Renders and writes a PNG to <paramref name="path"/>.</summary>
        public static void RenderToPng(WorldGen gen, string path, int sizePx = 2048, Action<int> progress = null, CancellationToken ct = default, ModCompatResult compat = null)
        {
            int[] pixels = Render(gen, compat, sizePx, progress, ct);
            ct.ThrowIfCancellationRequested();
            SavePng(pixels, sizePx, path);
        }

        /// <summary>Writes an ARGB pixel buffer as a PNG via LockBits.</summary>
        public static void SavePng(int[] pixels, int sizePx, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using (var bmp = new Bitmap(sizePx, sizePx, PixelFormat.Format32bppArgb))
            {
                var rect = new Rectangle(0, 0, sizePx, sizePx);
                BitmapData data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    // Stride can exceed width*4; copy row by row when it does.
                    if (data.Stride == sizePx * 4)
                    {
                        Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                    }
                    else
                    {
                        for (int y = 0; y < sizePx; y++)
                        {
                            Marshal.Copy(pixels, y * sizePx, IntPtr.Add(data.Scan0, y * data.Stride), sizePx);
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }

        private static int SamplePixel(WorldGen gen, float wx, float wy, float extent, float worldStretch, float biomeStretch)
        {
            double distSq = (double)wx * wx + (double)wy * wy;
            if (distSq > (double)extent * extent)
            {
                return ColVoid;
            }

            // Stretch factors divide the sampled coordinate (EWS semantics):
            // biome stretch applies on top of world stretch for biome lookup.
            float sx = wx / worldStretch;
            float sy = wy / worldStretch;
            Biome biome = gen.GetBiome(sx / biomeStretch, sy / biomeStretch);
            float h = gen.GetBiomeHeight(biome, sx, sy);

            if (h < WorldGen.SeaLevelMeters)
            {
                // Water: depth-shaded from shallow to deep over ~60 m.
                float depth = WorldGen.SeaLevelMeters - h;
                float t = depth / 60f;
                if (t < 0f) t = 0f;
                if (t > 1f) t = 1f;
                return LerpColor(ColShallow, ColDeep, t);
            }

            int col = BiomeColor(biome);

            // Shoreline sand band just above sea level (skip icy/ashen coasts).
            if (h < 32f && biome != Biome.Mountain && biome != Biome.DeepNorth && biome != Biome.AshLands)
            {
                float t = (32f - h) / 2f;
                col = LerpColor(col, ColSand, t * 0.8f);
            }

            // Subtle altitude lightening so relief reads at map scale.
            float alt = (h - WorldGen.SeaLevelMeters) / 170f;
            if (alt > 1f) alt = 1f;
            if (alt > 0f)
            {
                col = LerpColor(col, unchecked((int)0xFFFFFFFF), alt * 0.22f);
            }

            return col;
        }

        private static int BiomeColor(Biome biome)
        {
            switch (biome)
            {
                case Biome.Meadows: return ColMeadows;
                case Biome.BlackForest: return ColBlackForest;
                case Biome.Swamp: return ColSwamp;
                case Biome.Mountain: return ColMountain;
                case Biome.Plains: return ColPlains;
                case Biome.Mistlands: return ColMistlands;
                case Biome.AshLands: return ColAshlands;
                case Biome.DeepNorth: return ColDeepNorth;
                case Biome.Ocean: return ColDeep;
                default: return ColVoid;
            }
        }

        private static int LerpColor(int a, int b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            int ar = (a >> 16) & 0xFF, ag = (a >> 8) & 0xFF, ab = a & 0xFF;
            int br = (b >> 16) & 0xFF, bg = (b >> 8) & 0xFF, bb = b & 0xFF;
            int r = ar + (int)((br - ar) * t);
            int g = ag + (int)((bg - ag) * t);
            int bl = ab + (int)((bb - ab) * t);
            return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | bl;
        }
    }
}
