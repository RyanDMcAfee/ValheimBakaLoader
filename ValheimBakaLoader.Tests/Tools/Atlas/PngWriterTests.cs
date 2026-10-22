using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ValheimBakaLoader.Tools.Atlas;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools.Atlas
{
    /// <summary>
    /// The PNG writer replaced System.Drawing so the atlas can run on Linux, which
    /// means nothing else in the process checks the bytes it produces. These tests
    /// read the file back the way a decoder would: chunk by chunk, every CRC
    /// recomputed, IDAT inflated and un-filtered, and the pixels compared to what
    /// went in. On Windows the same bytes also go through GDI+, so the suite proves
    /// the file is a PNG that the rest of the world opens.
    /// </summary>
    public class PngWriterTests : IDisposable
    {
        private readonly string _tempDir =
            Path.Combine(Path.GetTempPath(), "vbl-png-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }

        private static readonly int[] Opaque3x2 =
        {
            unchecked((int)0xFF5E8A3D), unchecked((int)0xFF34502F), unchecked((int)0xFF0E2A4A),
            unchecked((int)0xFFFFFFFF), unchecked((int)0xFF000000), unchecked((int)0xFF80352A),
        };

        private static readonly int[] Alpha3x2 =
        {
            unchecked((int)0xCC06070A), 0x00000000, unchecked((int)0x80FF0000),
            unchecked((int)0xFF00FF00), 0x01000000, unchecked((int)0x7F123456),
        };

        public static IEnumerable<object[]> Images()
        {
            yield return new object[] { "1x1 opaque", new[] { unchecked((int)0xFF5E8A3D) }, 1, 1, false };
            yield return new object[] { "1x1 alpha", new[] { unchecked((int)0xCC06070A) }, 1, 1, true };
            yield return new object[] { "3x2 opaque", Opaque3x2, 3, 2, false };
            yield return new object[] { "3x2 alpha", Alpha3x2, 3, 2, true };
        }

        // ------------------------------------------------------------------
        // Chunk layout
        // ------------------------------------------------------------------

        [Theory]
        [MemberData(nameof(Images))]
        public void Layout_IsSignatureIhdrIdatIend(string name, int[] argb, int w, int h, bool hasAlpha)
        {
            byte[] png = PngWriter.Encode(argb, w, h);
            var chunks = ParseChunks(png);

            Assert.True(chunks.Count >= 3, name + ": expected at least IHDR, IDAT, IEND");
            Assert.Equal("IHDR", chunks[0].Type);
            Assert.Equal("IEND", chunks[chunks.Count - 1].Type);
            Assert.All(chunks.Skip(1).Take(chunks.Count - 2), c => Assert.Equal("IDAT", c.Type));
            Assert.Empty(chunks[chunks.Count - 1].Data);

            byte[] ihdr = chunks[0].Data;
            Assert.Equal(13, ihdr.Length);
            Assert.Equal(w, ReadBigEndian(ihdr, 0));
            Assert.Equal(h, ReadBigEndian(ihdr, 4));
            Assert.Equal(8, ihdr[8]);
            Assert.Equal(hasAlpha ? 6 : 2, ihdr[9]);
            Assert.Equal(0, ihdr[10]);
            Assert.Equal(0, ihdr[11]);
            Assert.Equal(0, ihdr[12]);
        }

        [Theory]
        [MemberData(nameof(Images))]
        public void Layout_EveryCrcMatches(string name, int[] argb, int w, int h, bool hasAlpha)
        {
            byte[] png = PngWriter.Encode(argb, w, h);
            var chunks = ParseChunks(png);
            foreach (var c in chunks)
            {
                Assert.True(c.StoredCrc == c.ComputedCrc, name + ": CRC mismatch on " + c.Type);
            }

            // A CRC that matches a corrupted IHDR proves nothing, so the one byte the
            // writer decides for itself is checked under the same CRC.
            Assert.Equal(hasAlpha ? 6 : 2, chunks[0].Data[9]);
        }

        [Fact]
        public void Layout_FileEndsAfterIend()
        {
            byte[] png = PngWriter.Encode(Opaque3x2, 3, 2);
            var chunks = ParseChunks(png);
            var last = chunks[chunks.Count - 1];
            Assert.Equal(png.Length, last.EndOffset);
        }

        // ------------------------------------------------------------------
        // Image data
        // ------------------------------------------------------------------

        [Theory]
        [MemberData(nameof(Images))]
        public void Pixels_RoundTripThroughInflateAndUnfilter(string name, int[] argb, int w, int h, bool hasAlpha)
        {
            byte[] png = PngWriter.Encode(argb, w, h);
            int[] back = Decode(png, out int dw, out int dh);
            Assert.Equal(w, dw);
            Assert.Equal(h, dh);
            Assert.True(argb.SequenceEqual(back), name + ": pixels changed on the way through the file");
            Assert.Equal(hasAlpha, back.Any(p => (p >> 24) != -1));
        }

        [Fact]
        public void Pixels_OnlyNoneSubUpFiltersAreUsed()
        {
            // The decoder below only knows the three filters the writer claims to
            // emit, so a new filter type would show up here before it could silently
            // corrupt a map.
            var rnd = new Random(1234);
            int[] px = new int[64 * 48];
            for (int i = 0; i < px.Length; i++) px[i] = rnd.Next() | (rnd.Next(2) == 0 ? unchecked((int)0xFF000000) : 0);
            byte[] png = PngWriter.Encode(px, 64, 48);

            byte[] raw = Inflate(png);
            int bpp = 4;
            int stride = 64 * bpp + 1;
            for (int y = 0; y < 48; y++)
            {
                Assert.InRange(raw[y * stride], 0, 2);
            }
        }

        [Fact]
        public void Pixels_GradientPicksAFilterThatShrinksTheFile()
        {
            // A smooth ramp is the case Sub and Up exist for; if the heuristic ever
            // stops choosing them, the maps on disk grow and this is the alarm.
            const int n = 128;
            int[] ramp = new int[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    ramp[y * n + x] = unchecked((int)0xFF000000) | ((x * 2) << 16) | ((y * 2) << 8) | ((x + y) & 0xFF);

            byte[] png = PngWriter.Encode(ramp, n, n);
            byte[] raw = Inflate(png);
            int stride = n * 3 + 1;
            int filtered = 0;
            for (int y = 0; y < n; y++) if (raw[y * stride] != 0) filtered++;
            Assert.True(filtered > n / 2, "expected most rows of a gradient to use Sub or Up");

            int idatBytes = ParseChunks(png).Where(c => c.Type == "IDAT").Sum(c => c.Data.Length);
            Assert.True(idatBytes < n * n * 3 / 4, "a gradient should compress well below its raw size");
        }

        [Fact]
        public void Pixels_OpaqueImageDropsAlphaChannel()
        {
            byte[] png = PngWriter.Encode(Opaque3x2, 3, 2);
            byte[] raw = Inflate(png);
            Assert.Equal(2 * (3 * 3 + 1), raw.Length);
        }

        [Fact]
        public void Pixels_AnyTranslucentPixelKeepsAlphaChannel()
        {
            int[] px = (int[])Opaque3x2.Clone();
            px[4] = unchecked((int)0xFE000000);
            byte[] png = PngWriter.Encode(px, 3, 2);
            byte[] raw = Inflate(png);
            Assert.Equal(2 * (3 * 4 + 1), raw.Length);
            Assert.True(px.SequenceEqual(Decode(png, out _, out _)));
        }

        // ------------------------------------------------------------------
        // Files on disk
        // ------------------------------------------------------------------

        [Fact]
        public void File_WritesSameBytesAsStreamAndLeavesNoTempBehind()
        {
            string path = Path.Combine(_tempDir, "nested", "map.png");
            PngWriter.Write(path, Alpha3x2, 3, 2);
            Assert.True(File.Exists(path));
            Assert.True(File.ReadAllBytes(path).SequenceEqual(PngWriter.Encode(Alpha3x2, 3, 2)));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)));
        }

        [Fact]
        public void File_ReplacesAnExistingFile()
        {
            string path = Path.Combine(_tempDir, "map.png");
            Directory.CreateDirectory(_tempDir);
            File.WriteAllText(path, "not a png");
            PngWriter.Write(path, Opaque3x2, 3, 2);
            Assert.True(Opaque3x2.SequenceEqual(Decode(File.ReadAllBytes(path), out _, out _)));
        }

        [Fact]
        public void File_RejectsMismatchedBuffer()
        {
            Assert.Throws<ArgumentException>(() => PngWriter.Encode(new int[5], 3, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => PngWriter.Encode(new int[0], 0, 1));
            Assert.Throws<ArgumentNullException>(() => PngWriter.Encode(null, 1, 1));
        }

        [Fact]
        public void File_MapRendererSavePngGoesThroughTheWriter()
        {
            string path = Path.Combine(_tempDir, "renderer.png");
            int[] square = Alpha3x2.Take(4).ToArray();
            MapRenderer.SavePng(square, 2, path);
            int[] back = Decode(File.ReadAllBytes(path), out int w, out int h);
            Assert.Equal(2, w);
            Assert.Equal(2, h);
            Assert.True(square.SequenceEqual(back));
        }

        // ------------------------------------------------------------------
        // GDI+ round trip (Windows only)
        // ------------------------------------------------------------------

        [Theory]
        [MemberData(nameof(Images))]
        public void Windows_BitmapReadsTheSamePixels(string name, int[] argb, int w, int h, bool hasAlpha)
        {
            if (!OperatingSystem.IsWindows()) return;

            string path = Path.Combine(_tempDir, name.Replace(' ', '_') + ".png");
            PngWriter.Write(path, argb, w, h);
            using (var bmp = new System.Drawing.Bitmap(path))
            {
                Assert.Equal(w, bmp.Width);
                Assert.Equal(h, bmp.Height);
                // GDI+ keeps a 24-bit PNG as 24bppRgb, so the format it picks says
                // whether the writer dropped the alpha channel when it should have.
                Assert.Equal(hasAlpha, System.Drawing.Image.IsAlphaPixelFormat(bmp.PixelFormat));
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        Assert.True(argb[y * w + x] == bmp.GetPixel(x, y).ToArgb(),
                            name + ": pixel (" + x + "," + y + ") differs after GDI+ decode");
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // A small decoder, enough for what the writer emits
        // ------------------------------------------------------------------

        private sealed class Chunk
        {
            public string Type;
            public byte[] Data;
            public uint StoredCrc;
            public uint ComputedCrc;
            public int EndOffset;
        }

        private static List<Chunk> ParseChunks(byte[] png)
        {
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8).ToArray());
            var list = new List<Chunk>();
            int pos = 8;
            while (pos < png.Length)
            {
                int len = ReadBigEndian(png, pos);
                string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
                var data = new byte[len];
                Array.Copy(png, pos + 8, data, 0, len);
                uint stored = (uint)ReadBigEndian(png, pos + 8 + len);
                uint computed = Crc32(png, pos + 4, 4 + len);
                pos += 12 + len;
                list.Add(new Chunk { Type = type, Data = data, StoredCrc = stored, ComputedCrc = computed, EndOffset = pos });
                if (type == "IEND") break;
            }
            return list;
        }

        private static byte[] Inflate(byte[] png)
        {
            var chunks = ParseChunks(png);
            using (var joined = new MemoryStream())
            {
                foreach (var c in chunks.Where(c => c.Type == "IDAT")) joined.Write(c.Data, 0, c.Data.Length);
                joined.Position = 0;
                using (var z = new ZLibStream(joined, CompressionMode.Decompress))
                using (var outp = new MemoryStream())
                {
                    z.CopyTo(outp);
                    return outp.ToArray();
                }
            }
        }

        private static int[] Decode(byte[] png, out int width, out int height)
        {
            var chunks = ParseChunks(png);
            byte[] ihdr = chunks[0].Data;
            width = ReadBigEndian(ihdr, 0);
            height = ReadBigEndian(ihdr, 4);
            int bpp = ihdr[9] == 6 ? 4 : 3;
            byte[] raw = Inflate(png);
            int stride = width * bpp;
            Assert.Equal(height * (stride + 1), raw.Length);

            var pixels = new int[width * height];
            var prev = new byte[stride];
            var cur = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                int at = y * (stride + 1);
                byte filter = raw[at];
                for (int i = 0; i < stride; i++)
                {
                    int f = raw[at + 1 + i];
                    int left = i >= bpp ? cur[i - bpp] : 0;
                    switch (filter)
                    {
                        case 0: cur[i] = (byte)f; break;
                        case 1: cur[i] = (byte)(f + left); break;
                        case 2: cur[i] = (byte)(f + prev[i]); break;
                        default: throw new InvalidDataException("filter " + filter + " is not one the writer emits");
                    }
                }
                for (int x = 0; x < width; x++)
                {
                    int o = x * bpp;
                    int a = bpp == 4 ? cur[o + 3] : 0xFF;
                    pixels[y * width + x] = (a << 24) | (cur[o] << 16) | (cur[o + 1] << 8) | cur[o + 2];
                }
                var swap = prev; prev = cur; cur = swap;
            }
            return pixels;
        }

        private static int ReadBigEndian(byte[] b, int at)
        {
            return (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
        }

        // An independent CRC so the test does not trust the writer's table.
        private static uint Crc32(byte[] buf, int offset, int count)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= buf[i];
                for (int k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
            }
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
