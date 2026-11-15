using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HorizonClient
{
    // Independent 4 KiB packets keep a damaged region from discarding a whole
    // high-resolution frame. All packet lengths and metadata remain bounded.
    public static class DenseVideoProtocol
    {
        public const int PacketBytes = 4096;
        private const int HeaderBytes = 40;
        private const uint Magic = 0x32435648; // HVC2
        private static readonly uint[] CrcTable = MakeCrcTable();
        private static readonly int[] PixelPatterns = MakePixelPatterns();
        private static readonly double[] RecoveryPhases = { -.5, 0, 0, -.5, .5, 0, 0, .5, -.5, -.5, -.5, .5, .5, -.5, .5, .5 };

        public static bool Supports(VideoProfile profile)
        {
            return profile >= VideoProfile.HighDensity720 && profile <= VideoProfile.Compact640;
        }

        public static int GetChunkSize(VideoProfile profile) { CheckProfile(profile); return profile == VideoProfile.Compact320 || profile == VideoProfile.Compact640 ? 256 : PacketBytes; }
        private static int GetSlotBytes(VideoProfile profile) { return HeaderBytes + GetChunkSize(profile) + 4; }

        public static int GetWidth(VideoProfile profile)
        {
            CheckProfile(profile);
            if (profile == VideoProfile.Compact320) return 320;
            if (profile == VideoProfile.Compact640) return 640;
            return profile == VideoProfile.HighDensity720 ? 1280
                : profile == VideoProfile.HighDensity1080 || profile == VideoProfile.HighDefinitionRobust ? 1920 : 3840;
        }

        public static int GetHeight(VideoProfile profile) { return GetWidth(profile) * (profile == VideoProfile.Compact320 || profile == VideoProfile.Compact640 ? 3 : 9) / (profile == VideoProfile.Compact320 || profile == VideoProfile.Compact640 ? 4 : 16); }

        public static int GetCellSize(VideoProfile profile)
        {
            CheckProfile(profile);
            if (profile == VideoProfile.Ultra4K) return 2;
            if (profile == VideoProfile.HighDefinitionRobust || profile == VideoProfile.Ultra4KRobust || profile == VideoProfile.Compact320 || profile == VideoProfile.Compact640) return 4;
            return 1;
        }

        public static int GetPacketsPerFrame(VideoProfile profile)
        {
            int cell = GetCellSize(profile);
            return ((GetWidth(profile) / cell - 4) * (GetHeight(profile) / cell - 4) / 8) / GetSlotBytes(profile);
        }

        public static int GetFramePayloadBytes(VideoProfile profile) { return GetPacketsPerFrame(profile) * GetChunkSize(profile); }

        public static Bitmap EncodeFrame(Guid transferId, int index, int count,
            int totalLength, byte[] chunk, VideoProfile profile)
        {
            if (!VisualProtocol.ValidMetadata(index, count, totalLength, chunk, profile, GetChunkSize(profile)))
                throw new ArgumentException("Invalid tiled video packet metadata.");
            byte[] slots = new byte[GetSlotBytes(profile)];
            WritePacket(slots, 0, transferId, index, count, totalLength, chunk, 0, chunk.Length, profile);
            return Render(slots, profile);
        }

        public static Bitmap EncodePacketFrame(Guid transferId, int firstPacketIndex,
            byte[] payload, VideoProfile profile)
        {
            CheckProfile(profile);
            if (payload == null || payload.Length > VisualProtocol.MaxTransferBytes) throw new ArgumentException("Invalid video payload.", "payload");
            int packetBytes = GetChunkSize(profile), slotBytes = GetSlotBytes(profile);
            int count = Math.Max(1, (payload.Length + packetBytes - 1) / packetBytes);
            if (firstPacketIndex < 0 || firstPacketIndex >= count) throw new ArgumentOutOfRangeException("firstPacketIndex");
            int packets = Math.Min(count - firstPacketIndex, GetPacketsPerFrame(profile));
            byte[] slots = new byte[packets * slotBytes];
            for (int i = 0; i < packets; i++)
            {
                int index = firstPacketIndex + i, offset = index * packetBytes;
                WritePacket(slots, i * slotBytes, transferId, index, count, payload.Length,
                    payload, offset, Math.Min(packetBytes, payload.Length - offset), profile);
            }
            return Render(slots, profile);
        }

        private static void WritePacket(byte[] slots, int slot, Guid transferId, int index,
            int count, int totalLength, byte[] payload, int offset, int length, VideoProfile profile)
        {
            Put(slots, slot, Magic); slots[slot + 4] = 2; slots[slot + 5] = (byte)profile;
            Put(slots, slot + 8, (uint)length);
            Buffer.BlockCopy(transferId.ToByteArray(), 0, slots, slot + 12, 16);
            Put(slots, slot + 28, (uint)index); Put(slots, slot + 32, (uint)count); Put(slots, slot + 36, (uint)totalLength);
            Buffer.BlockCopy(payload, offset, slots, slot + HeaderBytes, length);
            Put(slots, slot + HeaderBytes + length, Crc32(slots, slot, HeaderBytes + length));
        }

        private static Bitmap Render(byte[] slots, VideoProfile profile)
        {
            int width = GetWidth(profile), height = GetHeight(profile), cell = GetCellSize(profile);
            if (cell == 1) return RenderSinglePixel(slots, width, height);
            int columns = width / cell, rows = height / cell, dataColumns = columns - 4;
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte[] row = new byte[Math.Abs(locked.Stride)];
                for (int cy = 0; cy < rows; cy++)
                {
                    for (int cx = 0; cx < columns; cx++)
                    {
                        bool white;
                        if (cx < 2 || cx >= columns - 2 || cy < 2 || cy >= rows - 2)
                            white = cx != 0 && cx != columns - 1 && cy != 0 && cy != rows - 1 && ((cx + cy) & 1) == 0;
                        else
                        {
                            int bit = (cy - 2) * dataColumns + cx - 2;
                            white = bit < slots.Length * 8 ? (slots[bit >> 3] & (128 >> (bit & 7))) != 0 : ((cx + cy) & 1) == 0;
                        }
                        byte value = white ? (byte)255 : (byte)0;
                        int begin = cx * cell * 3, end = begin + cell * 3;
                        for (int pixel = begin; pixel < end; pixel++) row[pixel] = value;
                    }
                    for (int dy = 0; dy < cell; dy++) Marshal.Copy(row, 0, IntPtr.Add(locked.Scan0, (cy * cell + dy) * locked.Stride), row.Length);
                }
            }
            catch { bitmap.UnlockBits(locked); bitmap.Dispose(); throw; }
            bitmap.UnlockBits(locked);
            return bitmap;
        }

        private static Bitmap RenderSinglePixel(byte[] slots, int width, int height)
        {
            // Opaque RGB32 lets one integer store fill a monochrome pixel. Decode
            // and camera JPEG encoding still see exactly the same RGB values.
            const int black = unchecked((int)0xff000000), white = -1;
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int columns = width - 4, bits = slots.Length * 8;
                int[] row = new int[width];
                for (int y = 0; y < height; y++)
                {
                    row[0] = row[width - 1] = black;
                    if (y < 2 || y >= height - 2)
                    {
                        for (int x = 1; x < width - 1; x++) row[x] = y == 0 || y == height - 1 || ((x + y) & 1) != 0 ? black : white;
                    }
                    else
                    {
                        row[1] = ((y + 1) & 1) == 0 ? white : black;
                        row[width - 2] = ((width - 2 + y) & 1) == 0 ? white : black;
                        int bit = (y - 2) * columns, remaining = Math.Max(0, Math.Min(columns, bits - bit)), x = 2;
                        int byteIndex = bit >> 3, shift = bit & 7;
                        while (remaining >= 8)
                        {
                            int value = shift == 0 ? slots[byteIndex] : ((slots[byteIndex] << shift) | (slots[byteIndex + 1] >> (8 - shift))) & 255;
                            int pattern = value << 3;
                            row[x] = PixelPatterns[pattern]; row[x + 1] = PixelPatterns[pattern + 1];
                            row[x + 2] = PixelPatterns[pattern + 2]; row[x + 3] = PixelPatterns[pattern + 3];
                            row[x + 4] = PixelPatterns[pattern + 4]; row[x + 5] = PixelPatterns[pattern + 5];
                            row[x + 6] = PixelPatterns[pattern + 6]; row[x + 7] = PixelPatterns[pattern + 7];
                            x += 8; bit += 8; byteIndex++; remaining -= 8;
                        }
                        while (remaining-- > 0) { row[x++] = (slots[bit >> 3] & (128 >> (bit & 7))) != 0 ? white : black; bit++; }
                        for (; x < width - 2; x++) row[x] = ((x + y) & 1) == 0 ? white : black;
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(locked.Scan0, y * locked.Stride), row.Length);
                }
            }
            catch { bitmap.UnlockBits(locked); bitmap.Dispose(); throw; }
            bitmap.UnlockBits(locked); return bitmap;
        }

        private static int[] MakePixelPatterns()
        {
            int[] patterns = new int[256 * 8];
            for (int value = 0; value < 256; value++) for (int bit = 0; bit < 8; bit++) patterns[(value << 3) + bit] = (value & (128 >> bit)) != 0 ? -1 : unchecked((int)0xff000000);
            return patterns;
        }

        public static Packet[] DecodePackets(Bitmap bitmap)
        { return DecodePacketsCore(bitmap, null, null); }

        internal static Packet[] DecodePackets(Bitmap bitmap, VideoProfile profile)
        {
            if (!Supports(profile)) return new Packet[0];
            return DecodePacketsCore(bitmap, profile, null);
        }

        internal static Packet[] DecodePacketsExcept(Bitmap bitmap, VideoProfile profile)
        { return DecodePacketsCore(bitmap, null, profile); }

        private static Packet[] DecodePacketsCore(Bitmap bitmap, VideoProfile? onlyProfile, VideoProfile? excludedProfile)
        {
            if (bitmap == null || bitmap.Width < 128 || bitmap.Height < 96) return new Packet[0];
            using (var pixels = new PixelReader(bitmap))
            {
                int remainingPhaseAttempts = 8;
                // Most frames negotiate an exact native mode. Test those first.
                for (int pass = 0; pass < 2; pass++)
                foreach (VideoProfile profile in new[] { VideoProfile.HighDensity720, VideoProfile.HighDensity1080, VideoProfile.Ultra4K, VideoProfile.Maximum4K, VideoProfile.HighDefinitionRobust, VideoProfile.Ultra4KRobust, VideoProfile.Compact320, VideoProfile.Compact640 })
                {
                    if (onlyProfile.HasValue && profile != onlyProfile.Value) continue;
                    if (excludedProfile.HasValue && profile == excludedProfile.Value) continue;
                    bool exact = bitmap.Width == GetWidth(profile) && bitmap.Height == GetHeight(profile);
                    if ((pass == 0) != exact) continue;
                    var geometry = new Geometry(bitmap.Width, bitmap.Height, profile);
                    // Below one captured pixel per logical cell the information is lost.
                    if (geometry.ScaleX * GetCellSize(profile) < 0.98 || geometry.ScaleY * GetCellSize(profile) < 0.98)
                    { VideoDecoderDiagnostics.Count("dense", profile, "cellBelowOneCapturedPixel", 1); continue; }
                    for (int orientation = 0; orientation < 4; orientation++)
                    {
                        bool crcFailure;
                        List<Packet> result = ReadFrame(pixels, geometry, profile, (orientation & 1) != 0, (orientation & 2) != 0, out crcFailure);
                        // Fractional downscaling can place an integer sample near
                        // a blurred cell edge. Only a valid header and metadata
                        // followed by a failed CRC justify this bounded phase
                        // recovery. Compact two-pixel capture cells can also
                        // lose a magic-header bit: another CRC-valid tile plus
                        // a missing tile permits the same bounded search. Idle
                        // frames stay on the original path; every result keeps CRC.
                        bool compactHeaderLoss = profile == VideoProfile.Compact640 && result.Count > 0 &&
                            result.Count < Math.Min(GetPacketsPerFrame(profile), result[0].Count);
                        if ((crcFailure || compactHeaderLoss) && (profile == VideoProfile.HighDefinitionRobust || profile == VideoProfile.Compact640) && geometry.ScaleX < 1 && geometry.ScaleY < 1 &&
                            geometry.ScaleX * GetCellSize(profile) >= 2 && geometry.ScaleY * GetCellSize(profile) >= 2)
                        {
                            for (int phase = 0; phase < RecoveryPhases.Length && remainingPhaseAttempts > 0; phase += 2)
                            {
                                remainingPhaseAttempts--;
                                VideoDecoderDiagnostics.Count("dense", profile, "phaseAttempted", 1);
                                var shifted = new Geometry(bitmap.Width, bitmap.Height, profile, RecoveryPhases[phase], RecoveryPhases[phase + 1]);
                                bool ignored;
                                List<Packet> recovered = ReadFrame(pixels, shifted, profile, (orientation & 1) != 0, (orientation & 2) != 0, out ignored);
                                foreach (Packet packet in recovered)
                                {
                                    if (result.Count >= GetPacketsPerFrame(profile)) break;
                                    bool duplicate = false;
                                    foreach (Packet previous in result) if (packet.TransferId == previous.TransferId && packet.Index == previous.Index) { duplicate = true; break; }
                                    if (!duplicate) { result.Add(packet); VideoDecoderDiagnostics.Count("dense", profile, "phaseRecovered", 1); }
                                }
                                if (result.Count == GetPacketsPerFrame(profile)) break;
                            }
                        }
                        if (result.Count != 0) return result.ToArray();
                    }
                }
            }
            return new Packet[0];
        }

        private static List<Packet> ReadFrame(PixelReader pixels, Geometry geometry,
            VideoProfile profile, bool reverseX, bool reverseY, out bool crcFailure)
        {
            var result = new List<Packet>();
            int slots = GetPacketsPerFrame(profile), slotBytes = GetSlotBytes(profile), packetBytes = GetChunkSize(profile);
            int detected = 0, invalidMetadata = 0, invalidCrc = 0;
            for (int slot = 0; slot < slots; slot++)
            {
                int firstBit = slot * slotBytes * 8;
                byte[] header = ReadBytes(pixels, geometry, firstBit, HeaderBytes, reverseX, reverseY);
                if (Get(header, 0) != Magic || header[4] != 2 || header[5] != (byte)profile || header[6] != 0 || header[7] != 0) continue;
                detected++;
                uint rawLength = Get(header, 8);
                if (rawLength > packetBytes) { invalidMetadata++; continue; }
                int length = (int)rawLength, index = (int)Get(header, 28), count = (int)Get(header, 32), total = (int)Get(header, 36);
                if (!VisualProtocol.ValidMetadata(index, count, total, length, profile)) { invalidMetadata++; continue; }
                byte[] block = ReadBytes(pixels, geometry, firstBit, HeaderBytes + length + 4, reverseX, reverseY);
                if (Crc32(block, 0, HeaderBytes + length) != Get(block, HeaderBytes + length)) { invalidCrc++; continue; }
                byte[] id = new byte[16], data = new byte[length];
                Buffer.BlockCopy(block, 12, id, 0, id.Length); Buffer.BlockCopy(block, HeaderBytes, data, 0, data.Length);
                result.Add(new Packet { TransferId = new Guid(id), Index = index, Count = count, TotalLength = total, Data = data, ChunkSize = packetBytes, Profile = profile });
            }
            VideoDecoderDiagnostics.Count("dense", profile, "headerNotDetected", slots - detected);
            VideoDecoderDiagnostics.Count("dense", profile, "headerDetected", detected);
            VideoDecoderDiagnostics.Count("dense", profile, "metadataRejected", invalidMetadata);
            VideoDecoderDiagnostics.Count("dense", profile, "crcRejected", invalidCrc);
            VideoDecoderDiagnostics.Count("dense", profile, "accepted", result.Count);
            crcFailure = invalidCrc != 0;
            return result;
        }

        private static byte[] ReadBytes(PixelReader pixels, Geometry geometry, int firstBit,
            int length, bool reverseX, bool reverseY)
        {
            byte[] bytes = new byte[length];
            int x = firstBit % geometry.Columns, y = firstBit / geometry.Columns;
            for (int i = 0; i < length; i++)
            {
                if (geometry.ExactSinglePixel && x + 8 <= geometry.Columns)
                {
                    int sampleX = reverseX ? geometry.Columns + 1 - x : x + 2;
                    int sampleY = reverseY ? geometry.Rows + 1 - y : y + 2;
                    bytes[i] = (byte)pixels.ReadByte(sampleX, sampleY, reverseX ? -3 : 3);
                    x += 8; if (x == geometry.Columns) { x = 0; y++; }
                    continue;
                }
                int value = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    int sx = reverseX ? geometry.Columns - 1 - x : x, sy = reverseY ? geometry.Rows - 1 - y : y;
                    // Pixel centers avoid interpolation and a second full-sized bitmap.
                    int sum = pixels.Sample(geometry.X[sx], geometry.Y[sy]);
                    if (sum >= 128 * 3) value |= 128 >> bit;
                    if (++x == geometry.Columns) { x = 0; y++; }
                }
                bytes[i] = (byte)value;
            }
            return bytes;
        }

        private sealed class Geometry
        {
            internal readonly int Columns, Rows;
            internal readonly int[] X, Y;
            internal readonly double ScaleX, ScaleY;
            internal readonly bool ExactSinglePixel;
            internal Geometry(int width, int height, VideoProfile profile, double phaseX = 0, double phaseY = 0)
            {
                int nativeWidth = GetWidth(profile), nativeHeight = GetHeight(profile), cell = GetCellSize(profile);
                ExactSinglePixel = cell == 1 && width == nativeWidth && height == nativeHeight;
                double cropWidth = width, cropHeight = height, left = 0, top = 0;
                if ((long)width * nativeHeight > (long)height * nativeWidth) { cropWidth = (double)height * nativeWidth / nativeHeight; left = (width - cropWidth) / 2; }
                else { cropHeight = (double)width * nativeHeight / nativeWidth; top = (height - cropHeight) / 2; }
                ScaleX = cropWidth / nativeWidth; ScaleY = cropHeight / nativeHeight;
                Columns = nativeWidth / cell - 4; Rows = nativeHeight / cell - 4;
                X = new int[Columns]; Y = new int[Rows];
                for (int i = 0; i < Columns; i++) X[i] = Math.Max(0, Math.Min(width - 1, (int)(left + (i + 2.5) * cell * ScaleX + phaseX)));
                for (int i = 0; i < Rows; i++) Y[i] = Math.Max(0, Math.Min(height - 1, (int)(top + (i + 2.5) * cell * ScaleY + phaseY)));
            }
        }

        private sealed class PixelReader : IDisposable
        {
            private readonly Bitmap bitmap;
            private readonly BitmapData locked;
            private readonly byte[] row;
            private int rowIndex = -1;
            internal PixelReader(Bitmap bitmap)
            {
                this.bitmap = bitmap;
                locked = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                row = new byte[Math.Abs(locked.Stride)];
            }
            internal int Sample(int x, int y)
            {
                if (rowIndex != y) { Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), row, 0, row.Length); rowIndex = y; }
                int offset = x * 3; return row[offset] + row[offset + 1] + row[offset + 2];
            }
            internal int ReadByte(int x, int y, int step)
            {
                if (rowIndex != y) { Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), row, 0, row.Length); rowIndex = y; }
                int offset = x * 3;
                return (White(row, offset) & 128) | (White(row, offset + step) & 64)
                    | (White(row, offset + step * 2) & 32) | (White(row, offset + step * 3) & 16)
                    | (White(row, offset + step * 4) & 8) | (White(row, offset + step * 5) & 4)
                    | (White(row, offset + step * 6) & 2) | (White(row, offset + step * 7) & 1);
            }
            private static int White(byte[] pixels, int offset) { return (383 - (pixels[offset] + pixels[offset + 1] + pixels[offset + 2])) >> 31; }
            public void Dispose() { bitmap.UnlockBits(locked); }
        }

        private static void CheckProfile(VideoProfile profile) { if (!Supports(profile)) throw new ArgumentOutOfRangeException("profile"); }
        private static void Put(byte[] bytes, int offset, uint value) { for (int i = 0; i < 4; i++) bytes[offset + i] = (byte)(value >> (i * 8)); }
        private static uint Get(byte[] bytes, int offset) { return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24); }
        private static uint Crc32(byte[] bytes, int offset, int length)
        {
            uint crc = 0xffffffff; for (int i = 0; i < length; i++) crc = CrcTable[(crc ^ bytes[offset + i]) & 255] ^ (crc >> 8); return crc ^ 0xffffffff;
        }
        private static uint[] MakeCrcTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < table.Length; i++) { uint value = i; for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1; table[i] = value; }
            return table;
        }
    }
}
