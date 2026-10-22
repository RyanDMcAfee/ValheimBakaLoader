using System;
using System.IO;
using System.IO.Compression;

namespace ValheimBakaLoader.Tools.Atlas
{
    /// <summary>
    /// A PNG encoder for the one shape of image the atlas produces: 8-bit pixels
    /// handed over as 0xAARRGGBB ints. System.Drawing did this job until .NET 6
    /// made it Windows-only, and the Linux build needs the maps as much as the
    /// Windows one does, so the format is written by hand here. Only IHDR, IDAT
    /// and IEND go out; every reader understands those and a map carries no
    /// metadata worth keeping.
    /// </summary>
    public static class PngWriter
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        // Most of the atlas output is opaque, and a map with no alpha channel is a
        // quarter smaller on disk and faster to encode, so the writer drops the
        // channel whenever no pixel needs it. The fog overlay keeps it.
        private const byte ColorTypeRgb = 2;
        private const byte ColorTypeRgba = 6;

        private const byte FilterNone = 0;
        private const byte FilterSub = 1;
        private const byte FilterUp = 2;

        // Readers choke on a chunk that is too big for them long before the format's
        // own limit, and a 2048 map compresses to a few megabytes, so IDAT is sliced.
        private const int IdatSliceBytes = 1 << 20;

        private static readonly uint[] CrcTable = BuildCrcTable();

        /// <summary>Encodes the image into a fresh byte array.</summary>
        public static byte[] Encode(int[] argb, int width, int height)
        {
            using (var ms = new MemoryStream())
            {
                Write(ms, argb, width, height);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Writes the image to <paramref name="path"/>. The bytes go to a temp
        /// file beside the target first and are moved into place at the end, so a
        /// crash or a cancelled render leaves no half-written map under the real
        /// name for the page to pick up as a finished one.
        /// </summary>
        public static void Write(string path, int[] argb, int width, int height)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string temp = full + ".part-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    Write(fs, argb, width, height);
                    fs.Flush(true);
                }
                File.Move(temp, full, overwrite: true);
            }
            finally
            {
                // The move either took the temp file away or never happened; either
                // way nothing of ours should be left lying beside the map.
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            }
        }

        /// <summary>Writes the image to an open stream, which is left open.</summary>
        public static void Write(Stream output, int[] argb, int width, int height)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (argb == null) throw new ArgumentNullException(nameof(argb));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if ((long)width * height != argb.Length)
            {
                throw new ArgumentException("pixel buffer does not match width * height", nameof(argb));
            }

            bool opaque = AllOpaque(argb);
            int bpp = opaque ? 3 : 4;
            byte[] idat = CompressImageData(argb, width, height, bpp);

            output.Write(Signature, 0, Signature.Length);

            byte[] ihdr = new byte[13];
            WriteBigEndian(ihdr, 0, (uint)width);
            WriteBigEndian(ihdr, 4, (uint)height);
            ihdr[8] = 8;                                  // bit depth
            ihdr[9] = opaque ? ColorTypeRgb : ColorTypeRgba;
            ihdr[10] = 0;                                 // deflate
            ihdr[11] = 0;                                 // adaptive filtering
            ihdr[12] = 0;                                 // no interlace
            WriteChunk(output, "IHDR", ihdr, 0, ihdr.Length);

            for (int off = 0; off < idat.Length; off += IdatSliceBytes)
            {
                int len = Math.Min(IdatSliceBytes, idat.Length - off);
                WriteChunk(output, "IDAT", idat, off, len);
            }

            WriteChunk(output, "IEND", Array.Empty<byte>(), 0, 0);
            output.Flush();
        }

        private static bool AllOpaque(int[] argb)
        {
            for (int i = 0; i < argb.Length; i++)
            {
                if ((argb[i] >> 24) != -1) return false;
            }
            return true;
        }

        private static byte[] CompressImageData(int[] argb, int width, int height, int bpp)
        {
            int stride = width * bpp;
            byte[] current = new byte[stride];
            byte[] previous = new byte[stride];
            byte[] filtered = new byte[stride + 1];

            using (var compressed = new MemoryStream())
            {
                using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                {
                    for (int y = 0; y < height; y++)
                    {
                        UnpackRow(argb, y * width, width, bpp, current);
                        FilterRow(current, y == 0 ? null : previous, bpp, filtered);
                        zlib.Write(filtered, 0, filtered.Length);

                        byte[] swap = previous;
                        previous = current;
                        current = swap;
                    }
                }
                return compressed.ToArray();
            }
        }

        private static void UnpackRow(int[] argb, int start, int width, int bpp, byte[] row)
        {
            int o = 0;
            for (int x = 0; x < width; x++)
            {
                int p = argb[start + x];
                row[o++] = (byte)(p >> 16);
                row[o++] = (byte)(p >> 8);
                row[o++] = (byte)p;
                if (bpp == 4) row[o++] = (byte)(p >> 24);
            }
        }

        /// <summary>
        /// Picks between None, Sub and Up per row by the usual trick of summing the
        /// filtered bytes as signed values: the row whose residuals sit closest to
        /// zero deflates best. Biome maps are flat areas with soft gradients, where
        /// Sub and Up pay for themselves; Average and Paeth would cost another two
        /// passes per row for little more on this kind of picture.
        /// </summary>
        private static void FilterRow(byte[] row, byte[] prev, int bpp, byte[] outRow)
        {
            int n = row.Length;
            long scoreNone = 0, scoreSub = 0, scoreUp = long.MaxValue;
            for (int i = 0; i < n; i++)
            {
                scoreNone += Residual(row[i]);
                int left = i >= bpp ? row[i - bpp] : 0;
                scoreSub += Residual(row[i] - left);
            }
            if (prev != null)
            {
                scoreUp = 0;
                for (int i = 0; i < n; i++)
                {
                    scoreUp += Residual(row[i] - prev[i]);
                }
            }

            byte type = FilterNone;
            long best = scoreNone;
            if (scoreSub < best) { best = scoreSub; type = FilterSub; }
            if (scoreUp < best) { type = FilterUp; }

            outRow[0] = type;
            switch (type)
            {
                case FilterSub:
                    for (int i = 0; i < n; i++)
                    {
                        int left = i >= bpp ? row[i - bpp] : 0;
                        outRow[i + 1] = (byte)(row[i] - left);
                    }
                    break;
                case FilterUp:
                    for (int i = 0; i < n; i++)
                    {
                        outRow[i + 1] = (byte)(row[i] - prev[i]);
                    }
                    break;
                default:
                    Buffer.BlockCopy(row, 0, outRow, 1, n);
                    break;
            }
        }

        /// <summary>
        /// The size of a filtered byte read as a signed value. Math.Abs on an sbyte
        /// gives back an sbyte and throws on -128, which a noisy row hits within
        /// seconds, so the widening to int happens before the abs.
        /// </summary>
        private static int Residual(int value)
        {
            int signed = (sbyte)(byte)value;
            return signed < 0 ? -signed : signed;
        }

        private static void WriteChunk(Stream output, string type, byte[] data, int offset, int count)
        {
            byte[] head = new byte[8];
            WriteBigEndian(head, 0, (uint)count);
            for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
            output.Write(head, 0, 8);
            if (count > 0) output.Write(data, offset, count);

            // The CRC runs over the type and the data but not the length, per spec.
            uint crc = UpdateCrc(0xFFFFFFFFu, head, 4, 4);
            crc = UpdateCrc(crc, data, offset, count) ^ 0xFFFFFFFFu;
            byte[] tail = new byte[4];
            WriteBigEndian(tail, 0, crc);
            output.Write(tail, 0, 4);
        }

        private static void WriteBigEndian(byte[] buf, int at, uint value)
        {
            buf[at] = (byte)(value >> 24);
            buf[at + 1] = (byte)(value >> 16);
            buf[at + 2] = (byte)(value >> 8);
            buf[at + 3] = (byte)value;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }

        private static uint UpdateCrc(uint crc, byte[] buf, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
            {
                crc = CrcTable[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
            }
            return crc;
        }
    }
}
