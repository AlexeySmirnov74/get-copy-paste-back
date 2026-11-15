using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HorizonClient
{
    // Four-color profiles: two bits per cell. Wide in-frame pilot bands
    // calibrate the four RGB centroids; integrity is always decided by CRC32.
    // The black/red/yellow/blue constellation keeps a luminance/chroma spread
    // instead of trying to recover data from one fragile color component.
    public static class ColorVideoProtocol
    {
        public const int Width = 1280, Height = 720, ChunkSize = 3072;
        private const int HeaderBytes = 36;
        private const uint Magic = 0x34435648; // HVC4, distinct from monochrome.
        private static readonly int[] Palette = { 0x000000, 0xff0000, 0xffff00, 0x0000ff };
        private static readonly uint[] CrcTable = MakeCrcTable();
        private static readonly double[] Phases = { 0, 0, -.5, 0, .5, 0, 0, -.5, 0, .5, -.5, -.5, .5, -.5, -.5, .5, .5, .5 };

        public static bool Supports(VideoProfile profile) { return (int)profile >= 12 && (int)profile <= 15; }
        public static int GetWidth(VideoProfile profile) { CheckProfile(profile); return (int)profile < 14 ? 1280 : 1920; }
        public static int GetHeight(VideoProfile profile) { return GetWidth(profile) * 9 / 16; }
        public static int GetCellSize(VideoProfile profile) { CheckProfile(profile); return ((int)profile & 1) == 0 ? 8 : 4; }
        public static int GetChunkSize(VideoProfile profile)
        {
            CheckProfile(profile);
            switch ((int)profile) { case 12: return 3072; case 13: return 12288; case 14: return 6144; default: return 24576; }
        }

        public static Bitmap EncodeFrame(Guid transferId, int index, int count, int totalLength, byte[] chunk)
        { return EncodeFrame(transferId, index, count, totalLength, chunk, (VideoProfile)12); }

        public static Bitmap EncodeFrame(Guid transferId, int index, int count, int totalLength, byte[] chunk, VideoProfile profile)
        {
            CheckProfile(profile);
            if (chunk == null || !VisualProtocol.ValidMetadata(index, count, totalLength, chunk, profile, GetChunkSize(profile)))
                throw new ArgumentException("Invalid four-color packet metadata or chunk length.");
            byte[] wire = new byte[HeaderBytes + chunk.Length + 4];
            Put(wire, 0, Magic); wire[4] = 1; wire[5] = (byte)profile;
            wire[6] = (byte)chunk.Length; wire[7] = (byte)(chunk.Length >> 8);
            Buffer.BlockCopy(transferId.ToByteArray(), 0, wire, 8, 16);
            Put(wire, 24, (uint)index); Put(wire, 28, (uint)count); Put(wire, 32, (uint)totalLength);
            Buffer.BlockCopy(chunk, 0, wire, HeaderBytes, chunk.Length);
            Put(wire, HeaderBytes + chunk.Length, Crc32(wire, HeaderBytes + chunk.Length));
            int width = GetWidth(profile), height = GetHeight(profile), cell = GetCellSize(profile), columns = width / cell, rows = height / cell;
            Bitmap frame = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            BitmapData locked = frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[Math.Abs(locked.Stride)];
                for (int cy = 0; cy < rows; cy++)
                {
                    for (int cx = 0; cx < columns; cx++)
                    {
                        int color;
                        if (cx == 0 || cx == columns - 1 || cy == 0 || cy == rows - 1) color = 0;
                        else if (cx == 1 || cx == columns - 2 || cy == 1 || cy == rows - 2) color = ((cx + cy) & 1) == 0 ? Palette[2] : Palette[0];
                        else if (cy == 2) color = Palette[(cx - 2) * 4 / (columns - 4)];
                        else
                        {
                            int symbol = (cy - 3) * (columns - 4) + cx - 2;
                            int value = symbol < wire.Length * 4 ? (wire[symbol >> 2] >> (6 - 2 * (symbol & 3))) & 3 : (cx + cy) & 3;
                            color = Palette[value];
                        }
                        int start = cx * cell * 3;
                        for (int dx = 0; dx < cell; dx++)
                        {
                            row[start + dx * 3] = (byte)color; row[start + dx * 3 + 1] = (byte)(color >> 8); row[start + dx * 3 + 2] = (byte)(color >> 16);
                        }
                    }
                    for (int dy = 0; dy < cell; dy++) Marshal.Copy(row, 0, IntPtr.Add(locked.Scan0, (cy * cell + dy) * locked.Stride), row.Length);
                }
            }
            catch { frame.UnlockBits(locked); frame.Dispose(); throw; }
            frame.UnlockBits(locked); return frame;
        }

        public static Packet DecodeFrame(Bitmap frame, VideoProfile? onlyProfile = null, VideoProfile? excludedProfile = null)
        {
            if (frame == null || frame.Width < 320 || frame.Height < 180 || frame.Width > 4096 || frame.Height > 4096 ||
                (long)frame.Width * frame.Height > 12 * 1024 * 1024 || (onlyProfile.HasValue && !Supports(onlyProfile.Value))) return null;
            using (var pixels = new PixelReader(frame))
            {
                int[] candidates = frame.Width >= 1600 ? new[] { 14, 15, 12, 13 } : new[] { 12, 13, 14, 15 };
                foreach (int id in candidates)
                {
                    VideoProfile profile = (VideoProfile)id;
                    if ((onlyProfile.HasValue && onlyProfile.Value != profile) || (excludedProfile.HasValue && excludedProfile.Value == profile)) continue;
                    var original = new Geometry(frame.Width, frame.Height, profile, 0, 0);
                    // Fewer than two captured pixels per cell cannot preserve
                    // four chromatic symbols reliably; leave those frames alone.
                    if (original.CellPixels < 2) { VideoDecoderDiagnostics.Count("color4", profile, "resolutionTooLow", 1); continue; }
                    int calibrationRejected = 0, headerNotDetected = 0, headerDetected = 0, metadataRejected = 0, crcRejected = 0;
                    int attempts = original.CellPixels < GetCellSize(profile) ? Phases.Length : 2;
                    for (int phase = 0; phase < attempts; phase += 2)
                    {
                        Geometry geometry = phase == 0 ? original : new Geometry(frame.Width, frame.Height, profile, Phases[phase], Phases[phase + 1]);
                        for (int orientation = 0; orientation < 4; orientation++)
                        {
                            bool reverseX = (orientation & 1) != 0, reverseY = (orientation & 2) != 0;
                            Vector[] pilots = Calibrate(pixels, geometry, reverseX, reverseY);
                            if (pilots == null) { calibrationRejected++; continue; }
                            byte[] header = ReadBytes(pixels, geometry, pilots, HeaderBytes, reverseX, reverseY);
                            if (Get(header, 0) != Magic || header[4] != 1 || header[5] != id) { headerNotDetected++; continue; }
                            headerDetected++;
                            int length = header[6] | header[7] << 8, index = (int)Get(header, 24), count = (int)Get(header, 28), total = (int)Get(header, 32);
                            if (!VisualProtocol.ValidMetadata(index, count, total, length, profile)) { metadataRejected++; continue; }
                            byte[] wire = ReadBytes(pixels, geometry, pilots, HeaderBytes + length + 4, reverseX, reverseY);
                            if (Crc32(wire, HeaderBytes + length) != Get(wire, HeaderBytes + length)) { crcRejected++; continue; }
                            byte[] transfer = new byte[16], data = new byte[length];
                            Buffer.BlockCopy(wire, 8, transfer, 0, 16); Buffer.BlockCopy(wire, HeaderBytes, data, 0, length);
                            CountStages(profile, calibrationRejected, headerNotDetected, headerDetected, metadataRejected, crcRejected);
                            VideoDecoderDiagnostics.Count("color4", profile, "accepted", 1);
                            return new Packet { TransferId = new Guid(transfer), Index = index, Count = count, TotalLength = total, Data = data,
                                Profile = profile, ChunkSize = GetChunkSize(profile) };
                        }
                    }
                    CountStages(profile, calibrationRejected, headerNotDetected, headerDetected, metadataRejected, crcRejected);
                }
            }
            return null;
        }

        private static void CountStages(VideoProfile profile, int calibrationRejected, int headerNotDetected, int headerDetected, int metadataRejected, int crcRejected)
        {
            VideoDecoderDiagnostics.Count("color4", profile, "calibrationRejected", calibrationRejected);
            VideoDecoderDiagnostics.Count("color4", profile, "headerNotDetected", headerNotDetected);
            VideoDecoderDiagnostics.Count("color4", profile, "headerDetected", headerDetected);
            VideoDecoderDiagnostics.Count("color4", profile, "metadataRejected", metadataRejected);
            VideoDecoderDiagnostics.Count("color4", profile, "crcRejected", crcRejected);
        }

        private static Vector[] Calibrate(PixelReader pixels, Geometry geometry, bool reverseX, bool reverseY)
        {
            var colors = new Vector[4]; int band = (geometry.Columns - 4) / 4;
            for (int color = 0; color < 4; color++)
            {
                Vector sum = new Vector();
                for (int sample = 0; sample < 7; sample++)
                {
                    Vector value = Sample(pixels, geometry, 2 + color * band + (2 * sample + 1) * band / 14, 2, reverseX, reverseY);
                    sum.R += value.R; sum.G += value.G; sum.B += value.B;
                }
                colors[color] = new Vector { R = sum.R / 7, G = sum.G / 7, B = sum.B / 7 };
            }
            // Nearest-centroid decoding alone can accidentally accept grayscale
            // pilots. Require surviving chroma, color order and pair separation.
            if (colors[1].R - colors[1].G < 40 || colors[1].R - colors[1].B < 40 ||
                colors[2].R - colors[2].B < 40 || colors[2].G - colors[2].B < 40 ||
                colors[3].B - colors[3].R < 40 || colors[3].B - colors[3].G < 40) return null;
            for (int first = 0; first < 4; first++) for (int second = first + 1; second < 4; second++)
                if (Distance(colors[first], colors[second]) < 2500) return null;
            return colors;
        }

        private static byte[] ReadBytes(PixelReader pixels, Geometry geometry, Vector[] palette, int length, bool reverseX, bool reverseY)
        {
            var bytes = new byte[length]; int columns = geometry.Columns - 4;
            for (int symbol = 0; symbol < length * 4; symbol++)
            {
                Vector color = Sample(pixels, geometry, 2 + symbol % columns, 3 + symbol / columns, reverseX, reverseY);
                int nearest = 0; double minimum = Distance(color, palette[0]);
                for (int candidate = 1; candidate < 4; candidate++)
                {
                    double distance = Distance(color, palette[candidate]);
                    if (distance < minimum) { nearest = candidate; minimum = distance; }
                }
                bytes[symbol >> 2] |= (byte)(nearest << (6 - 2 * (symbol & 3)));
            }
            return bytes;
        }

        private static Vector Sample(PixelReader pixels, Geometry geometry, int cx, int cy, bool reverseX, bool reverseY)
        {
            if (reverseX) cx = geometry.Columns - 1 - cx;
            if (reverseY) cy = geometry.Rows - 1 - cy;
            Vector value = new Vector();
            for (int y = 0; y < 3; y++)
            {
                byte[] row = pixels.Row(geometry.Y[cy * 3 + y]);
                for (int x = 0; x < 3; x++)
                {
                    int offset = geometry.X[cx * 3 + x] * 3;
                    value.B += row[offset]; value.G += row[offset + 1]; value.R += row[offset + 2];
                }
            }
            value.R /= 9; value.G /= 9; value.B /= 9; return value;
        }

        private struct Vector { internal double R, G, B; }
        private static double Distance(Vector first, Vector second)
        { double r = first.R - second.R, g = first.G - second.G, b = first.B - second.B; return r * r + g * g + b * b; }

        private sealed class Geometry
        {
            internal readonly int Columns, Rows;
            internal readonly int[] X, Y;
            internal readonly double CellPixels;
            internal Geometry(int width, int height, VideoProfile profile, double phaseX, double phaseY)
            {
                int nativeWidth = GetWidth(profile), nativeHeight = GetHeight(profile), cell = GetCellSize(profile);
                double cropWidth = width, cropHeight = height, left = 0, top = 0;
                if ((long)width * nativeHeight > (long)height * nativeWidth) { cropWidth = (double)height * nativeWidth / nativeHeight; left = (width - cropWidth) / 2; }
                else { cropHeight = (double)width * nativeHeight / nativeWidth; top = (height - cropHeight) / 2; }
                double scale = cropWidth / nativeWidth;
                CellPixels = cell * scale; Columns = nativeWidth / cell; Rows = nativeHeight / cell;
                X = new int[Columns * 3]; Y = new int[Rows * 3];
                for (int cx = 0; cx < Columns; cx++) for (int sample = 0; sample < 3; sample++)
                    X[cx * 3 + sample] = Clamp((int)(left + (cx + .5 + (sample - 1) * .16) * cell * scale + phaseX), width);
                for (int cy = 0; cy < Rows; cy++) for (int sample = 0; sample < 3; sample++)
                    Y[cy * 3 + sample] = Clamp((int)(top + (cy + .5 + (sample - 1) * .16) * cell * scale + phaseY), height);
            }
            private static int Clamp(int value, int bound) { return Math.Max(0, Math.Min(bound - 1, value)); }
        }

        private sealed class PixelReader : IDisposable
        {
            private readonly byte[][] rows;
            private readonly int stride;
            private readonly Bitmap source;
            private readonly BitmapData locked;
            internal PixelReader(Bitmap frame)
            {
                source = frame;
                locked = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    stride = Math.Abs(locked.Stride); rows = new byte[frame.Height][];
                }
                catch { frame.UnlockBits(locked); throw; }
            }
            internal byte[] Row(int y)
            {
                // Ordinary monochrome/idle frames fail pilot calibration after
                // touching only a few rows. Never copy the full RGB frame just
                // to discover that this experiment is not the active codec.
                byte[] row = rows[y];
                if (row == null)
                {
                    row = new byte[stride]; Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), row, 0, row.Length); rows[y] = row;
                }
                return row;
            }
            public void Dispose() { source.UnlockBits(locked); }
        }

        private static void CheckProfile(VideoProfile profile) { if (!Supports(profile)) throw new ArgumentOutOfRangeException("profile"); }
        private static void Put(byte[] bytes, int offset, uint value) { for (int i = 0; i < 4; i++) bytes[offset + i] = (byte)(value >> (8 * i)); }
        private static uint Get(byte[] bytes, int offset) { return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24); }
        private static uint Crc32(byte[] bytes, int length)
        {
            uint crc = 0xffffffff;
            for (int index = 0; index < length; index++) crc = CrcTable[(crc ^ bytes[index]) & 255] ^ (crc >> 8);
            return crc ^ 0xffffffff;
        }
        private static uint[] MakeCrcTable()
        {
            var result = new uint[256];
            for (uint index = 0; index < result.Length; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
                result[index] = value;
            }
            return result;
        }
    }
}
