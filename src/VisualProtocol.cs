using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HorizonClient
{
    public enum VideoProfile { Compatible = 0, Fast = 1, Dense = 2,
        HighDensity720 = 3, HighDensity1080 = 4, Ultra4K = 5, Maximum4K = 6,
        HighDefinitionRobust = 7, Ultra4KRobust = 8, Compact320 = 9, Compact640 = 10, Mp4Modem = 11,
        Color720 = 12, Color720Dense = 13, Color1080 = 14, Color1080Dense = 15 }

    public sealed class Packet
    {
        public Guid TransferId;
        public int Index;
        public int Count;
        public int TotalLength;
        public byte[] Data;
        public int ChunkSize;
        public VideoProfile Profile;
    }

    // The original grid profiles use monochrome cells; optional color profiles
    // have separate framing, calibration and CRC in ColorVideoProtocol.
    // CRC rejects damaged frames; repeated transmissions recover dropped data.
    public static class VisualProtocol
    {
        public const int ChunkSize = 256;
        public const int MaxTransferBytes = 32 * 1024 * 1024;
        public const int FrameWidth = 640;
        public const int FrameHeight = 480;
        private const int HeaderSize = 36;
        private const uint Magic = 0x31435648; // HVC1
        private static readonly uint[] CrcTable = MakeCrcTable();

        public static Bitmap EncodeFrame(Guid transferId, int index, int count,
            int totalLength, byte[] chunk)
        {
            return EncodeFrame(transferId, index, count, totalLength, chunk, VideoProfile.Compatible);
        }

        public static int GetChunkSize(VideoProfile profile)
        {
            if (ColorVideoProtocol.Supports(profile)) return ColorVideoProtocol.GetChunkSize(profile);
            if (Mp4ModemProtocol.Supports(profile)) return Mp4ModemProtocol.ChunkSize;
            if (DenseVideoProtocol.Supports(profile)) return DenseVideoProtocol.GetChunkSize(profile);
            switch (profile)
            {
                case VideoProfile.Compatible: return ChunkSize;
                case VideoProfile.Fast: return 1536;
                case VideoProfile.Dense: return 6144;
                default: throw new ArgumentOutOfRangeException("profile");
            }
        }

        public static int GetWidth(VideoProfile profile)
        {
            if (ColorVideoProtocol.Supports(profile)) return ColorVideoProtocol.GetWidth(profile);
            if (Mp4ModemProtocol.Supports(profile)) return Mp4ModemProtocol.Width;
            if (DenseVideoProtocol.Supports(profile)) return DenseVideoProtocol.GetWidth(profile);
            GetChunkSize(profile);
            return profile == VideoProfile.Compatible ? FrameWidth : 1280;
        }

        public static int GetHeight(VideoProfile profile)
        {
            if (ColorVideoProtocol.Supports(profile)) return ColorVideoProtocol.GetHeight(profile);
            if (Mp4ModemProtocol.Supports(profile)) return Mp4ModemProtocol.Height;
            if (DenseVideoProtocol.Supports(profile)) return DenseVideoProtocol.GetHeight(profile);
            GetChunkSize(profile);
            return profile == VideoProfile.Compatible ? FrameHeight : 720;
        }

        private static int GetCellSize(VideoProfile profile)
        {
            return profile == VideoProfile.Compatible ? 10 : profile == VideoProfile.Fast ? 8 : 4;
        }

        public static Bitmap EncodeFrame(Guid transferId, int index, int count,
            int totalLength, byte[] chunk, VideoProfile profile)
        {
            if (ColorVideoProtocol.Supports(profile)) return ColorVideoProtocol.EncodeFrame(transferId, index, count, totalLength, chunk, profile);
            if (Mp4ModemProtocol.Supports(profile)) return Mp4ModemProtocol.EncodeFrame(transferId, index, count, totalLength, chunk);
            if (DenseVideoProtocol.Supports(profile)) return DenseVideoProtocol.EncodeFrame(transferId, index, count, totalLength, chunk, profile);
            if (chunk == null || !ValidMetadata(index, count, totalLength, chunk.Length, profile))
                throw new ArgumentException("Invalid visual packet metadata or chunk length.");

            byte[] bytes = new byte[HeaderSize + chunk.Length + 4];
            WriteUInt32(bytes, 0, Magic);
            bytes[4] = 1;
            bytes[5] = (byte)profile;
            bytes[6] = (byte)chunk.Length;
            bytes[7] = (byte)(chunk.Length >> 8);
            Buffer.BlockCopy(transferId.ToByteArray(), 0, bytes, 8, 16);
            WriteUInt32(bytes, 24, (uint)index);
            WriteUInt32(bytes, 28, (uint)count);
            WriteUInt32(bytes, 32, (uint)totalLength);
            Buffer.BlockCopy(chunk, 0, bytes, HeaderSize, chunk.Length);
            WriteUInt32(bytes, HeaderSize + chunk.Length,
                Crc32(bytes, HeaderSize + chunk.Length));

            int width = GetWidth(profile);
            int height = GetHeight(profile);
            int cellSize = GetCellSize(profile);
            int columns = width / cellSize;
            int rows = height / cellSize;
            int dataColumns = columns - 4;
            Bitmap frame = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            BitmapData locked = frame.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte[] pixels = new byte[locked.Stride * height];
                for (int cy = 0; cy < rows; cy++)
                {
                    for (int cx = 0; cx < columns; cx++)
                    {
                        bool white;
                        if (cx < 2 || cx >= columns - 2 || cy < 2 || cy >= rows - 2)
                        {
                            // Timing border and a quiet outer edge.
                            white = cx != 0 && cx != columns - 1 && cy != 0 && cy != rows - 1
                                && ((cx + cy) & 1) == 0;
                        }
                        else
                        {
                            int bit = (cy - 2) * dataColumns + cx - 2;
                            white = bit < bytes.Length * 8
                                ? (bytes[bit / 8] & (1 << (7 - bit % 8))) != 0
                                : ((cx + cy) & 1) == 0;
                        }
                        byte value = white ? (byte)255 : (byte)0;
                        for (int dy = 0; dy < cellSize; dy++)
                        {
                            int offset = (cy * cellSize + dy) * locked.Stride + cx * cellSize * 3;
                            for (int dx = 0; dx < cellSize * 3; dx++)
                                pixels[offset + dx] = value;
                        }
                    }
                }
                Marshal.Copy(pixels, 0, locked.Scan0, pixels.Length);
            }
            finally
            {
                frame.UnlockBits(locked);
            }
            return frame;
        }

        public static Packet DecodeFrame(Bitmap frame)
        {
            Packet[] packets = DecodePackets(frame);
            return packets.Length == 0 ? null : packets[0];
        }

        public static Packet[] DecodePackets(Bitmap frame)
        {
            if (frame == null) return new Packet[0];
            try
            { return DecodeAllProfiles(frame, null, false); }
            finally { VideoDecoderDiagnostics.Flush(frame.Width, frame.Height); }
        }

        private static Packet[] DecodeAllProfiles(Bitmap frame, VideoProfile? excludedProfile, bool controlAlreadyChecked)
        {
            if (!controlAlreadyChecked && excludedProfile != VideoProfile.Compatible)
            {
                // Idle/hello/camera-format/ACK frames use the existing robust
                // CRC-checked control geometry. Recognize those before probing
                // every historical tiled profile and the MP4 modem.
                Packet control = DecodeLegacyFrame(frame, VideoProfile.Compatible);
                if (control != null) return new[] { control };
                controlAlreadyChecked = true;
            }
            Packet color = ColorVideoProtocol.DecodeFrame(frame, null, excludedProfile);
            if (color != null) return new[] { color };
            Packet[] dense = excludedProfile.HasValue ? DenseVideoProtocol.DecodePacketsExcept(frame, excludedProfile.Value) : DenseVideoProtocol.DecodePackets(frame);
            if (dense.Length != 0) return dense;
            Packet legacy = DecodeLegacyFrame(frame, null, excludedProfile, controlAlreadyChecked);
            if (legacy != null) return new Packet[] { legacy };
            if (excludedProfile == VideoProfile.Mp4Modem) return new Packet[0];
            Packet modem = Mp4ModemProtocol.DecodeFrame(frame);
            return modem == null ? new Packet[0] : new Packet[] { modem };
        }

        // A verified announcement/data packet supplies a hint only. Control
        // frames remain decodable, and a profile change always gets full search.
        // No packet bypasses its original framing, metadata or CRC checks.
        public static Packet[] DecodePackets(Bitmap frame, VideoProfile? preferredProfile)
        {
            if (frame == null) return new Packet[0];
            if (!preferredProfile.HasValue || !IsDefinedProfile(preferredProfile.Value)) return DecodePackets(frame);
            try
            {
                Packet control = DecodeLegacyFrame(frame, VideoProfile.Compatible);
                if (control != null) return new[] { control };
                VideoProfile profile = preferredProfile.Value;
                if (ColorVideoProtocol.Supports(profile))
                {
                    Packet color = ColorVideoProtocol.DecodeFrame(frame, profile);
                    if (color != null) return new[] { color };
                }
                else if (DenseVideoProtocol.Supports(profile))
                {
                    Packet[] dense = DenseVideoProtocol.DecodePackets(frame, profile);
                    if (dense.Length != 0) return dense;
                }
                else if (profile != VideoProfile.Compatible)
                {
                    Packet packet = profile == VideoProfile.Mp4Modem ? Mp4ModemProtocol.DecodeFrame(frame) : DecodeLegacyFrame(frame, profile);
                    if (packet != null) return new[] { packet };
                }
                // The preferred candidate and control already failed on this
                // immutable frame. Search every other profile once; retrying
                // the same damaged tiles cannot recover different bytes.
                return DecodeAllProfiles(frame, profile, true);
            }
            finally { VideoDecoderDiagnostics.Flush(frame.Width, frame.Height); }
        }

        public static bool IsDefinedProfile(VideoProfile profile) { return (int)profile >= 0 && (int)profile <= (int)VideoProfile.Color1080Dense; }

        public static VideoProfile[] GetAllProfiles() { return (VideoProfile[])Enum.GetValues(typeof(VideoProfile)); }

        // Wire IDs stay stable. The server exposes the 4K entries only when
        // its active camera is UHD; default Full HD keeps the smaller catalog.
        public static bool IsOfferedProfile(VideoProfile profile)
        {
            return IsDefinedProfile(profile) && profile != VideoProfile.Compatible &&
                profile != VideoProfile.Compact320 &&
                profile != VideoProfile.HighDensity720 && profile != VideoProfile.HighDensity1080 &&
                GetWidth(profile) <= 3840 && GetHeight(profile) <= 2160;
        }

        public static VideoProfile[] GetProfiles()
        {
            List<VideoProfile> offered = new List<VideoProfile>();
            foreach (VideoProfile profile in GetAllProfiles()) if (IsOfferedProfile(profile)) offered.Add(profile);
            return offered.ToArray();
        }

        public static int GetPacketsPerFrame(VideoProfile profile)
        {
            GetChunkSize(profile);
            return DenseVideoProtocol.Supports(profile) ? DenseVideoProtocol.GetPacketsPerFrame(profile) : 1;
        }

        public static int GetFramePayloadBytes(VideoProfile profile) { return GetPacketsPerFrame(profile) * GetChunkSize(profile); }

        public static Bitmap EncodePacketFrame(Guid transferId, int firstPacketIndex, byte[] payload, VideoProfile profile)
        {
            if (DenseVideoProtocol.Supports(profile)) return DenseVideoProtocol.EncodePacketFrame(transferId, firstPacketIndex, payload, profile);
            if (payload == null || payload.Length > MaxTransferBytes) throw new ArgumentException("Invalid video payload.", "payload");
            int size = GetChunkSize(profile), count = Math.Max(1, (payload.Length + size - 1) / size);
            if (firstPacketIndex < 0 || firstPacketIndex >= count) throw new ArgumentOutOfRangeException("firstPacketIndex");
            int length = Math.Min(size, payload.Length - firstPacketIndex * size);
            byte[] chunk = new byte[length]; Buffer.BlockCopy(payload, firstPacketIndex * size, chunk, 0, length);
            return EncodeFrame(transferId, firstPacketIndex, count, payload.Length, chunk, profile);
        }

        private static Packet DecodeLegacyFrame(Bitmap frame, VideoProfile? onlyProfile = null,
            VideoProfile? excludedProfile = null, bool controlAlreadyChecked = false)
        {
            if (frame == null || frame.Width < 128 || frame.Height < 96)
                return null;

            SampledFrame compatible = null;
            SampledFrame wide = null;
            bool wideFirst = (long)frame.Width * 9 >= (long)frame.Height * 14;
            for (int candidate = 0; candidate < 3; candidate++)
            {
                VideoProfile profile = wideFirst
                    ? (candidate == 0 ? VideoProfile.Fast : candidate == 1 ? VideoProfile.Dense : VideoProfile.Compatible)
                    : (VideoProfile)candidate;
                if (onlyProfile.HasValue && profile != onlyProfile.Value) continue;
                if (excludedProfile.HasValue && profile == excludedProfile.Value || controlAlreadyChecked && profile == VideoProfile.Compatible) continue;
                RectangleF crop = CenteredCrop(frame.Width, frame.Height, GetWidth(profile), GetHeight(profile));
                if ((profile == VideoProfile.Dense && (crop.Width < 960 || crop.Height < 540)) ||
                    (profile == VideoProfile.Fast && (crop.Width < 640 || crop.Height < 360)))
                { VideoDecoderDiagnostics.Count("legacy", profile, "resolutionTooLow", 1); continue; }
                SampledFrame pixels;
                if (profile == VideoProfile.Compatible)
                {
                    if (compatible == null) compatible = new SampledFrame(frame, crop, FrameWidth, FrameHeight);
                    pixels = compatible;
                }
                else
                {
                    if (wide == null) wide = new SampledFrame(frame, crop, 1280, 720);
                    pixels = wide;
                }
                for (int orientation = 0; orientation < 4; orientation++)
                {
                    Packet packet = ReadPacket(pixels, profile, (orientation & 1) != 0, (orientation & 2) != 0);
                    if (packet != null) return packet;
                }
            }
            return null;
        }

        private static RectangleF CenteredCrop(int width, int height, int targetWidth, int targetHeight)
        {
            if ((long)width * targetHeight > (long)height * targetWidth)
            {
                float cropWidth = (float)height * targetWidth / targetHeight;
                return new RectangleF((width - cropWidth) / 2, 0, cropWidth, height);
            }
            float cropHeight = (float)width * targetHeight / targetWidth;
            return new RectangleF(0, (height - cropHeight) / 2, width, cropHeight);
        }

        private sealed class SampledFrame
        {
            public readonly byte[] Pixels;
            public readonly int Stride;

            public SampledFrame(Bitmap source, RectangleF crop, int width, int height)
            {
                using (Bitmap normalized = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics graphics = Graphics.FromImage(normalized))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.DrawImage(source, new Rectangle(0, 0, width, height),
                            crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel);
                    }
                    BitmapData locked = normalized.LockBits(new Rectangle(0, 0, width, height),
                        ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    Stride = locked.Stride;
                    Pixels = new byte[Stride * height];
                    try { Marshal.Copy(locked.Scan0, Pixels, 0, Pixels.Length); }
                    finally { normalized.UnlockBits(locked); }
                }
            }
        }

        private static Packet ReadPacket(SampledFrame pixels, VideoProfile profile, bool reverseX, bool reverseY)
        {
            // Read only the fixed header until magic/profile/length are valid.
            byte[] header = ReadBytes(pixels, profile, HeaderSize, reverseX, reverseY);
            if (ReadUInt32(header, 0) != Magic || header[4] != 1 || header[5] != (byte)profile)
            { VideoDecoderDiagnostics.Count("legacy", profile, "headerNotDetected", 1); return null; }

            VideoDecoderDiagnostics.Count("legacy", profile, "headerDetected", 1);

            int length = header[6] | (header[7] << 8);
            int index = (int)ReadUInt32(header, 24);
            int count = (int)ReadUInt32(header, 28);
            int total = (int)ReadUInt32(header, 32);
            if (!ValidMetadata(index, count, total, length, profile))
            { VideoDecoderDiagnostics.Count("legacy", profile, "metadataRejected", 1); return null; }

            byte[] bytes = ReadBytes(pixels, profile, HeaderSize + length + 4, reverseX, reverseY);
            if (Crc32(bytes, HeaderSize + length) != ReadUInt32(bytes, HeaderSize + length))
            { VideoDecoderDiagnostics.Count("legacy", profile, "crcRejected", 1); return null; }

            VideoDecoderDiagnostics.Count("legacy", profile, "accepted", 1);

            byte[] id = new byte[16];
            Buffer.BlockCopy(bytes, 8, id, 0, id.Length);
            byte[] data = new byte[length];
            Buffer.BlockCopy(bytes, HeaderSize, data, 0, length);
            return new Packet { TransferId = new Guid(id), Index = index, Count = count,
                TotalLength = total, Data = data, Profile = profile, ChunkSize = GetChunkSize(profile) };
        }

        private static byte[] ReadBytes(SampledFrame pixels, VideoProfile profile, int length,
            bool reverseX, bool reverseY)
        {
            int cellSize = GetCellSize(profile);
            int columns = GetWidth(profile) / cellSize;
            int rows = GetHeight(profile) / cellSize;
            int sampleLow = (int)(cellSize * 0.35);
            int sampleMiddle = cellSize / 2;
            int sampleHigh = (int)(cellSize * 0.65);
            int[] samplePositions = new int[] { sampleLow, sampleMiddle, sampleHigh };
            byte[] bytes = new byte[length];
            for (int bit = 0; bit < length * 8; bit++)
            {
                int x = 2 + bit % (columns - 4);
                int y = 2 + bit / (columns - 4);
                if (reverseX) x = columns - 1 - x;
                if (reverseY) y = rows - 1 - y;
                int sum = 0;
                for (int sy = 0; sy < 3; sy++)
                {
                    int rowOffset = (y * cellSize + samplePositions[sy]) * pixels.Stride;
                    for (int sx = 0; sx < 3; sx++)
                    {
                        int offset = rowOffset + (x * cellSize + samplePositions[sx]) * 3;
                        sum += pixels.Pixels[offset] + pixels.Pixels[offset + 1] + pixels.Pixels[offset + 2];
                    }
                }
                if (sum >= 128 * 27) bytes[bit / 8] |= (byte)(1 << (7 - bit % 8));
            }
            return bytes;
        }

        internal static bool ValidMetadata(int index, int count, int total, byte[] data)
        {
            return data != null && ValidMetadata(index, count, total, data.Length, VideoProfile.Compatible);
        }

        internal static bool ValidMetadata(int index, int count, int total, byte[] data, VideoProfile profile,
            int declaredChunkSize)
        {
            if (!IsDefinedProfile(profile) || data == null) return false;
            return (declaredChunkSize == 0 || declaredChunkSize == GetChunkSize(profile))
                && ValidMetadata(index, count, total, data.Length, profile);
        }

        internal static bool ValidMetadata(int index, int count, int total, int length, VideoProfile profile)
        {
            if (!IsDefinedProfile(profile)) return false;
            int chunkSize = GetChunkSize(profile);
            if (total < 0 || total > MaxTransferBytes || count < 1
                || count > (MaxTransferBytes + chunkSize - 1) / chunkSize
                || index < 0 || index >= count || length < 0 || length > chunkSize)
                return false;
            int expectedCount = Math.Max(1, (total + chunkSize - 1) / chunkSize);
            return count == expectedCount && length == Math.Min(chunkSize, total - index * chunkSize);
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            for (int i = 0; i < 4; i++) bytes[offset + i] = (byte)(value >> (8 * i));
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] | bytes[offset + 1] << 8
                | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
        }

        private static uint Crc32(byte[] bytes, int length)
        {
            uint crc = 0xffffffff;
            for (int i = 0; i < length; i++) crc = CrcTable[(crc ^ bytes[i]) & 255] ^ (crc >> 8);
            return crc ^ 0xffffffff;
        }

        private static uint[] MakeCrcTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
                table[i] = value;
            }
            return table;
        }
    }

    public sealed class TransferAssembler
    {
        private readonly TimeSpan timeout;
        private readonly HashSet<Guid> completed = new HashSet<Guid>();
        private readonly Queue<Guid> completedOrder = new Queue<Guid>();
        private byte[] buffer;
        private bool[] received;
        private int totalLength;
        private DateTime lastProgressUtc;
        private VideoProfile currentProfile;
        private int currentChunkSize;

        public TransferAssembler() : this(TimeSpan.FromMinutes(30)) { }

        public TransferAssembler(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("timeout");
            this.timeout = timeout;
        }

        public int ReceivedCount { get; private set; }
        public int TotalCount { get; private set; }
        public Guid TransferId { get; private set; }
        public Guid CurrentTransferId { get { return TransferId; } }

        internal bool HasCompleted(Guid transferId) { return completed.Contains(transferId); }

        public byte[] Add(Packet packet)
        {
            if (packet == null || !VisualProtocol.ValidMetadata(packet.Index, packet.Count,
                packet.TotalLength, packet.Data, packet.Profile, packet.ChunkSize))
                return null;

            if (buffer != null && DateTime.UtcNow - lastProgressUtc >= timeout)
                ClearCurrent();
            if (completed.Contains(packet.TransferId)) return null;

            if (buffer == null || TransferId != packet.TransferId)
            {
                ClearCurrent();
                TransferId = packet.TransferId;
                TotalCount = packet.Count;
                totalLength = packet.TotalLength;
                currentProfile = packet.Profile;
                currentChunkSize = VisualProtocol.GetChunkSize(packet.Profile);
                buffer = new byte[totalLength];
                received = new bool[TotalCount];
                lastProgressUtc = DateTime.UtcNow;
            }
            else if (TotalCount != packet.Count || totalLength != packet.TotalLength || currentProfile != packet.Profile)
            {
                return null;
            }

            int offset = packet.Index * currentChunkSize;
            if (received[packet.Index])
            {
                // Conflicting duplicates cannot replace already accepted bytes.
                for (int i = 0; i < packet.Data.Length; i++)
                    if (buffer[offset + i] != packet.Data[i]) return null;
                return null;
            }
            Buffer.BlockCopy(packet.Data, 0, buffer, offset, packet.Data.Length);
            received[packet.Index] = true;
            ReceivedCount++;
            lastProgressUtc = DateTime.UtcNow;
            if (ReceivedCount != TotalCount) return null;

            byte[] result = buffer;
            buffer = null;
            received = null;
            completed.Add(TransferId);
            completedOrder.Enqueue(TransferId);
            if (completedOrder.Count > 256) completed.Remove(completedOrder.Dequeue());
            return result;
        }

        public void Reset()
        {
            ClearCurrent();
            completed.Clear();
            completedOrder.Clear();
        }

        private void ClearCurrent()
        {
            buffer = null;
            received = null;
            totalLength = 0;
            currentChunkSize = 0;
            currentProfile = VideoProfile.Compatible;
            ReceivedCount = 0;
            TotalCount = 0;
            TransferId = Guid.Empty;
            lastProgressUtc = DateTime.MinValue;
        }
    }
}
