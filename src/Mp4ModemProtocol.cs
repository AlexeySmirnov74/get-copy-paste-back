// MP4Modem V2 basis/FEC port and Horizon packet adapter.
// Derived from grmchn/mp4modem b3f2d2d1fdbba17104ba473170f7111100e10aea,
// Apache-2.0. See third_party/mp4modem for the original sources and notices.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace HorizonClient
{
    // Uses the upstream 16x16 four-basis waveform, pilots, triplicated compact
    // tags, RS(255,191), and column interleave. Flag 0x02 identifies our
    // independent Horizon packets; MP4 containers/Payload Bundles are not used.
    public static class Mp4ModemProtocol
    {
        public const int Width = 1280;
        public const int Height = 720;
        public const int ChunkSize = 1082;
        internal const int FrameDataBytes = 1656;
        internal const int DataBytesPerFrame = FrameDataBytes - 4;
        internal const int FecPayloadSize = 187;
        internal const int FecBlockCount = 6;
        internal const int RsDataSize = 191;
        internal const int RsBlockSize = 255;
        internal const int ParitySymbols = 64;
        internal const int AdapterFlag = 2;
        private const int PacketHeaderSize = 36;
        private const uint PacketMagic = 0x4348344d; // M4HC, little-endian
        private const int TileSize = 16;
        private const int Columns = Width / TileSize;
        private const int BlockCount = Columns * (Height / TileSize);
        private const int TagBytes = 16;
        private const int HeaderBlocksPerCopy = TagBytes * 2;
        private const int PilotBlockCount = 192;
        internal static readonly int[] HeaderIndices;
        internal static readonly int[] PilotIndices;
        internal static readonly int[] DataIndices;
        private static readonly uint[] CrcTable = MakeCrcTable();
        private static readonly object DiagnosticGate = new object();
        private static readonly Dictionary<string, long> DiagnosticFailures = new Dictionary<string, long>();
        private static long diagnosticLast, diagnosticFrames, diagnosticAccepted;
        private static double diagnosticMilliseconds, diagnosticMaxMilliseconds;

        private sealed class DecodeAttemptInfo
        {
            internal string Stage = "basisHeader", ExceptionType;
            internal double PilotSeparation = -1;
            internal long SymbolId = -1;
            internal bool OuterCrcValid;
        }

        internal sealed class CompactTag
        {
            internal int Epoch, Phase, Flags;
            internal uint SymbolId;
        }

        internal sealed class BasisFrame
        {
            internal CompactTag Tag;
            internal byte[] Data;
            internal double MinimumPilotSeparation;
        }

        static Mp4ModemProtocol()
        {
            HeaderIndices = new int[HeaderBlocksPerCopy * 3];
            var reserved = new bool[BlockCount];
            int[] starts = { 0, BlockCount / 2 - HeaderBlocksPerCopy / 2, BlockCount - HeaderBlocksPerCopy };
            for (int copy = 0; copy < starts.Length; copy++)
                for (int index = 0; index < HeaderBlocksPerCopy; index++)
                {
                    int block = starts[copy] + index;
                    HeaderIndices[copy * HeaderBlocksPerCopy + index] = block;
                    reserved[block] = true;
                }
            var eligible = new List<int>();
            for (int block = 0; block < BlockCount; block++) if (!reserved[block]) eligible.Add(block);
            PilotIndices = new int[PilotBlockCount];
            for (int index = 0; index < PilotBlockCount; index++)
            {
                int block = eligible[(int)Math.Floor((index + .5) * eligible.Count / PilotBlockCount)];
                PilotIndices[index] = block; reserved[block] = true;
            }
            var data = new List<int>();
            for (int block = 0; block < BlockCount; block++) if (!reserved[block]) data.Add(block);
            DataIndices = data.ToArray();
            if (DataIndices.Length != FrameDataBytes * 2) throw new InvalidOperationException("Unexpected MP4Modem block layout.");
        }

        public static bool Supports(VideoProfile profile) { return (int)profile == 11; }

        public static Bitmap EncodeFrame(Packet packet)
        {
            if (packet == null || !ValidMetadata(packet.Index, packet.Count, packet.TotalLength, packet.Data))
                throw new ArgumentException("Invalid MP4Modem packet metadata.", "packet");
            var wire = new byte[FecBlockCount * FecPayloadSize];
            WriteLe32(wire, 0, PacketMagic); wire[4] = 1; wire[5] = 11;
            wire[6] = (byte)packet.Data.Length; wire[7] = (byte)(packet.Data.Length >> 8);
            Buffer.BlockCopy(packet.TransferId.ToByteArray(), 0, wire, 8, 16);
            WriteLe32(wire, 24, (uint)packet.Index); WriteLe32(wire, 28, (uint)packet.Count); WriteLe32(wire, 32, (uint)packet.TotalLength);
            Buffer.BlockCopy(packet.Data, 0, wire, PacketHeaderSize, packet.Data.Length);
            WriteLe32(wire, PacketHeaderSize + packet.Data.Length, Crc32(wire, 0, PacketHeaderSize + packet.Data.Length));
            byte[] coded = EncodeFec(wire);
            var data = new byte[FrameDataBytes];
            Buffer.BlockCopy(coded, 0, data, 0, coded.Length);
            WriteBe32(data, DataBytesPerFrame, Crc32(data, 0, DataBytesPerFrame));
            return LuminanceToBitmap(EncodeBasisFrame(new CompactTag { Flags = AdapterFlag, SymbolId = (uint)packet.Index }, data));
        }

        public static Bitmap EncodeFrame(Guid transferId, int index, int count, int totalLength, byte[] chunk)
        {
            return EncodeFrame(new Packet { TransferId = transferId, Index = index, Count = count, TotalLength = totalLength, Data = chunk, ChunkSize = ChunkSize, Profile = (VideoProfile)11 });
        }

        public static Packet DecodeFrame(Bitmap frame)
        {
            if (frame == null || frame.Width < 320 || frame.Height < 180) return null;
            bool diagnostics = DiagnosticLog.IsEnabled;
            long began = diagnostics ? Stopwatch.GetTimestamp() : 0;
            DecodeAttemptInfo cropped = diagnostics ? new DecodeAttemptInfo() : null;
            DecodeAttemptInfo stretched = null;
            Packet packet = DecodeFrameAttempt(frame, true, cropped);
            // Camera redirection may stretch16:9 video to a4:3 negotiated mode.
            // Try that explicit geometry only after centered letterboxing fails.
            if (packet == null && (long)frame.Width * 9 != (long)frame.Height * 16)
            {
                stretched = diagnostics ? new DecodeAttemptInfo() : null;
                packet = DecodeFrameAttempt(frame, false, stretched);
            }
            if (diagnostics) RecordDecode(frame, packet, cropped, stretched,
                (Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency);
            return packet;
        }

        private static Packet DecodeFrameAttempt(Bitmap frame, bool centeredCrop, DecodeAttemptInfo diagnostic)
        {
            try
            {
                BasisFrame decoded = DecodeBasisFrame(BitmapToLuminance(frame, centeredCrop));
                if (diagnostic != null)
                {
                    diagnostic.Stage = "adapterTag"; diagnostic.PilotSeparation = decoded.MinimumPilotSeparation;
                    diagnostic.SymbolId = decoded.Tag.SymbolId;
                    diagnostic.OuterCrcValid = ReadBe32(decoded.Data, DataBytesPerFrame) == Crc32(decoded.Data, 0, DataBytesPerFrame);
                }
                if (decoded.Tag.Flags != AdapterFlag || decoded.Tag.Epoch != 0 || decoded.Tag.Phase != 0) return null;
                if (diagnostic != null) diagnostic.Stage = "pilotSeparation";
                if (decoded.MinimumPilotSeparation < 4) return null;
                // The outer CRC is diagnostic. RS repairs damaged symbols even
                // when a lossy camera frame has invalidated this frame CRC.
                var coded = new byte[FecBlockCount * RsBlockSize];
                Buffer.BlockCopy(decoded.Data, 0, coded, 0, coded.Length);
                if (diagnostic != null) diagnostic.Stage = "fec";
                byte[] wire = DecodeFec(coded, FecBlockCount * FecPayloadSize, null);
                if (diagnostic != null) diagnostic.Stage = "packetHeader";
                if (ReadLe32(wire, 0) != PacketMagic || wire[4] != 1 || wire[5] != 11) return null;
                int length = wire[6] | wire[7] << 8;
                uint index = ReadLe32(wire, 24), count = ReadLe32(wire, 28), total = ReadLe32(wire, 32);
                if (diagnostic != null) diagnostic.Stage = "packetMetadata";
                if (length > ChunkSize || index > Int32.MaxValue || count > Int32.MaxValue || total > Int32.MaxValue || decoded.Tag.SymbolId != index) return null;
                if (diagnostic != null) diagnostic.Stage = "packetCrc";
                if (ReadLe32(wire, PacketHeaderSize + length) != Crc32(wire, 0, PacketHeaderSize + length)) return null;
                var chunk = new byte[length]; Buffer.BlockCopy(wire, PacketHeaderSize, chunk, 0, length);
                if (diagnostic != null) diagnostic.Stage = "packetMetadata";
                if (!ValidMetadata((int)index, (int)count, (int)total, chunk)) return null;
                var guid = new byte[16]; Buffer.BlockCopy(wire, 8, guid, 0, 16);
                if (diagnostic != null) diagnostic.Stage = "accepted";
                return new Packet { TransferId = new Guid(guid), Index = (int)index, Count = (int)count, TotalLength = (int)total, Data = chunk, ChunkSize = ChunkSize, Profile = (VideoProfile)11 };
            }
            catch (ArgumentException error) { if (diagnostic != null) diagnostic.ExceptionType = error.GetType().Name; return null; }
            catch (InvalidOperationException error) { if (diagnostic != null) diagnostic.ExceptionType = error.GetType().Name; return null; }
            catch (InvalidDataException error) { if (diagnostic != null) diagnostic.ExceptionType = error.GetType().Name; return null; }
        }

        private static void RecordDecode(Bitmap frame, Packet packet, DecodeAttemptInfo cropped, DecodeAttemptInfo stretched, double elapsed)
        {
            lock (DiagnosticGate)
            {
                diagnosticFrames++; if (packet != null) diagnosticAccepted++;
                diagnosticMilliseconds += elapsed; diagnosticMaxMilliseconds = Math.Max(diagnosticMaxMilliseconds, elapsed);
                foreach (DecodeAttemptInfo attempt in new DecodeAttemptInfo[] { cropped, stretched })
                {
                    if (attempt == null || attempt.Stage == "accepted") continue;
                    string reason = attempt.Stage + (attempt.ExceptionType == null ? "" : ":" + attempt.ExceptionType);
                    long count; DiagnosticFailures.TryGetValue(reason, out count); DiagnosticFailures[reason] = count + 1;
                }
                long time = Stopwatch.GetTimestamp();
                if (diagnosticLast != 0 && (time - diagnosticLast) * 1000.0 / Stopwatch.Frequency < 1000) return;
                var failures = new Dictionary<string, long>(DiagnosticFailures);
                DecodeAttemptInfo last = stretched ?? cropped;
                DiagnosticLog.Write("video.mp4.decode.summary", "width", frame.Width, "height", frame.Height,
                    "frames", diagnosticFrames, "acceptedFrames", diagnosticAccepted, "failedAttemptsByStage", failures,
                    "decodeMeanMs", diagnosticMilliseconds / diagnosticFrames, "decodeMaxMs", diagnosticMaxMilliseconds,
                    "lastCropStage", cropped.Stage, "lastStretchStage", stretched == null ? "notAttempted" : stretched.Stage,
                    "lastPilotSeparation", last.PilotSeparation, "lastSymbolId", last.SymbolId, "lastOuterCrcValid", last.OuterCrcValid,
                    "lastPacketIndex", packet == null ? -1 : packet.Index, "lastPacketCount", packet == null ? 0 : packet.Count,
                    "note", "No header can also mean another profile or an idle/control frame; match against server.video.mode.");
                diagnosticLast = time; diagnosticFrames = diagnosticAccepted = 0;
                diagnosticMilliseconds = diagnosticMaxMilliseconds = 0; DiagnosticFailures.Clear();
            }
        }

        private static bool ValidMetadata(int index, int count, int total, byte[] data)
        {
            if (data == null || total < 0 || total > VisualProtocol.MaxTransferBytes || index < 0 || count < 1 || index >= count) return false;
            int expectedCount = Math.Max(1, (total + ChunkSize - 1) / ChunkSize);
            return count == expectedCount && data.Length == Math.Min(ChunkSize, total - index * ChunkSize);
        }

        internal static byte[] PackTag(CompactTag tag)
        {
            if (tag == null || tag.Epoch < 0 || tag.Epoch > UInt16.MaxValue || tag.Phase < 0 || tag.Phase > 255 || tag.Flags < 0 || tag.Flags > 255)
                throw new ArgumentException("Invalid MP4Modem tag.");
            var data = new byte[TagBytes]; data[0] = 0x4d; data[1] = 0x32; data[2] = 2; data[3] = 0x81;
            data[4] = (byte)(tag.Epoch >> 8); data[5] = (byte)tag.Epoch; data[6] = (byte)tag.Phase; data[7] = (byte)tag.Flags;
            WriteBe32(data, 8, tag.SymbolId); WriteBe32(data, 12, Crc32(data, 0, 12)); return data;
        }

        private static CompactTag UnpackTag(byte[] data)
        {
            bool magic = (data[0] == 0x4d && data[1] == 0x32) || (data[0] == 0x46 && data[1] == 0x32);
            if (!magic || data[2] != 2 || data[3] != 0x81 || ReadBe32(data, 12) != Crc32(data, 0, 12))
                throw new ArgumentException("Invalid MP4Modem compact tag.");
            return new CompactTag { Epoch = data[4] << 8 | data[5], Phase = data[6], Flags = data[7], SymbolId = ReadBe32(data, 8) };
        }

        internal static byte[] EncodeBasisFrame(CompactTag tag, byte[] data)
        {
            if (data == null || data.Length > FrameDataBytes) throw new ArgumentException("Invalid MP4Modem basis payload.");
            var padded = new byte[FrameDataBytes]; Buffer.BlockCopy(data, 0, padded, 0, data.Length);
            var nibbles = new byte[BlockCount]; byte[] packedTag = PackTag(tag);
            for (int copy = 0; copy < 3; copy++)
                for (int index = 0; index < HeaderBlocksPerCopy; index++) nibbles[HeaderIndices[copy * HeaderBlocksPerCopy + index]] = (byte)GetNibble(packedTag, index);
            for (int index = 0; index < PilotIndices.Length; index++) nibbles[PilotIndices[index]] = (byte)(index % 16);
            for (int index = 0; index < DataIndices.Length; index++) nibbles[DataIndices[index]] = (byte)GetNibble(padded, index);
            var frame = new byte[Width * Height * 3 / 2];
            for (int block = 0; block < BlockCount; block++)
            {
                int value = nibbles[block], s0 = ((value >> 3) & 1) * 2 - 1, s1 = ((value >> 2) & 1) * 2 - 1;
                int s2 = ((value >> 1) & 1) * 2 - 1, s3 = (value & 1) * 2 - 1;
                int blockRow = block / Columns, blockColumn = block % Columns;
                for (int y = 0; y < TileSize; y++)
                {
                    int v = y < 8 ? -1 : 1, offset = (blockRow * TileSize + y) * Width + blockColumn * TileSize;
                    for (int x = 0; x < TileSize; x++)
                    {
                        int h = x < 8 ? -1 : 1;
                        frame[offset + x] = (byte)Math.Max(16, Math.Min(240, 128 + s0 * 40 + s1 * 24 * h + s2 * 24 * v + s3 * 24 * h * v));
                    }
                }
            }
            for (int i = Width * Height; i < frame.Length; i++) frame[i] = 128;
            return frame;
        }

        internal static BasisFrame DecodeBasisFrame(byte[] frame)
        {
            if (frame == null || frame.Length != Width * Height * 3 / 2) throw new ArgumentException("Invalid MP4Modem I420 frame.");
            var observations = new double[BlockCount * 4];
            for (int block = 0; block < BlockCount; block++)
            {
                int blockRow = block / Columns, blockColumn = block % Columns;
                int s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                for (int y = 0; y < TileSize; y++)
                {
                    int v = y < 8 ? -1 : 1, offset = (blockRow * TileSize + y) * Width + blockColumn * TileSize;
                    for (int x = 0; x < TileSize; x++)
                    {
                        int h = x < 8 ? -1 : 1, luminance = frame[offset + x];
                        s0 += luminance - 128; s1 += luminance * h; s2 += luminance * v; s3 += luminance * h * v;
                    }
                }
                observations[block * 4] = s0 / 256.0; observations[block * 4 + 1] = s1 / 256.0;
                observations[block * 4 + 2] = s2 / 256.0; observations[block * 4 + 3] = s3 / 256.0;
            }
            var thresholds = new double[4]; double minimumSeparation = Double.MaxValue;
            for (int basis = 0; basis < 4; basis++)
            {
                double low = 0, high = 0; int lowCount = 0, highCount = 0;
                for (int index = 0; index < PilotIndices.Length; index++)
                {
                    double observed = observations[PilotIndices[index] * 4 + basis];
                    if ((((index % 16) >> (3 - basis)) & 1) == 0) { low += observed; lowCount++; }
                    else { high += observed; highCount++; }
                }
                low /= lowCount; high /= highCount; thresholds[basis] = (low + high) / 2;
                minimumSeparation = Math.Min(minimumSeparation, Math.Abs(high - low));
            }
            var nibbles = new byte[BlockCount];
            for (int block = 0; block < BlockCount; block++)
                for (int basis = 0; basis < 4; basis++) nibbles[block] = (byte)((nibbles[block] << 1) | (observations[block * 4 + basis] >= thresholds[basis] ? 1 : 0));
            var votedTag = new byte[TagBytes];
            for (int bit = 0; bit < TagBytes * 8; bit++)
            {
                int votes = 0;
                for (int copy = 0; copy < 3; copy++) votes += (nibbles[HeaderIndices[copy * HeaderBlocksPerCopy + bit / 4]] >> (3 - bit % 4)) & 1;
                if (votes >= 2) votedTag[bit / 8] |= (byte)(1 << (7 - bit % 8));
            }
            var payload = new byte[FrameDataBytes];
            for (int index = 0; index < DataIndices.Length; index++) SetNibble(payload, index, nibbles[DataIndices[index]]);
            return new BasisFrame { Tag = UnpackTag(votedTag), Data = payload, MinimumPilotSeparation = minimumSeparation };
        }

        internal static byte[] EncodeFec(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            int blocks = Math.Max(1, (data.Length + FecPayloadSize - 1) / FecPayloadSize);
            var codewords = new byte[blocks][];
            for (int block = 0; block < blocks; block++)
            {
                var protectedData = new byte[RsDataSize];
                int length = Math.Min(FecPayloadSize, data.Length - block * FecPayloadSize);
                Buffer.BlockCopy(data, block * FecPayloadSize, protectedData, 0, length);
                WriteBe32(protectedData, FecPayloadSize, Crc32(protectedData, 0, FecPayloadSize));
                codewords[block] = Mp4ModemReedSolomon.Encode(protectedData, ParitySymbols);
            }
            var encoded = new byte[blocks * RsBlockSize]; int offset = 0;
            for (int column = 0; column < RsBlockSize; column++)
                for (int block = 0; block < blocks; block++) encoded[offset++] = codewords[block][column];
            return encoded;
        }

        internal static byte[] DecodeFec(byte[] data, int originalSize, ISet<int> erasures)
        {
            if (data == null || data.Length == 0 || data.Length % RsBlockSize != 0 || originalSize < 0 || originalSize > data.Length / RsBlockSize * FecPayloadSize)
                throw new ArgumentException("Invalid MP4Modem FEC stream.");
            int blocks = data.Length / RsBlockSize; var result = new byte[originalSize];
            for (int block = 0; block < blocks; block++)
            {
                var codeword = new byte[RsBlockSize]; var positions = new List<int>();
                for (int column = 0; column < RsBlockSize; column++)
                {
                    int index = column * blocks + block; codeword[column] = data[index];
                    if (erasures != null && erasures.Contains(index)) positions.Add(column);
                }
                byte[] decoded = Mp4ModemReedSolomon.Decode(codeword, ParitySymbols, positions.ToArray());
                if (ReadBe32(decoded, FecPayloadSize) != Crc32(decoded, 0, FecPayloadSize)) throw new ArgumentException("MP4Modem FEC block CRC mismatch.");
                int length = Math.Min(FecPayloadSize, originalSize - block * FecPayloadSize);
                if (length > 0) Buffer.BlockCopy(decoded, 0, result, block * FecPayloadSize, length);
            }
            return result;
        }

        internal static Bitmap LuminanceToBitmap(byte[] frame)
        {
            var bitmap = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
            BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[Width * 3];
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++) { byte value = frame[y * Width + x]; row[x * 3] = value; row[x * 3 + 1] = value; row[x * 3 + 2] = value; }
                    Marshal.Copy(row, 0, IntPtr.Add(locked.Scan0, y * locked.Stride), row.Length);
                }
            }
            finally { bitmap.UnlockBits(locked); }
            return bitmap;
        }

        private static byte[] BitmapToLuminance(Bitmap frame, bool centeredCrop)
        {
            using (var normalized = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(normalized))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = frame.Width < 640 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
                    graphics.PixelOffsetMode = PixelOffsetMode.Half;
                    double wantedRatio = (double)Width / Height, ratio = (double)frame.Width / frame.Height;
                    float cropWidth = centeredCrop && ratio > wantedRatio ? (float)(frame.Height * wantedRatio) : frame.Width;
                    float cropHeight = centeredCrop && ratio < wantedRatio ? (float)(frame.Width / wantedRatio) : frame.Height;
                    graphics.DrawImage(frame, new RectangleF(0, 0, Width, Height), new RectangleF((frame.Width - cropWidth) / 2, (frame.Height - cropHeight) / 2, cropWidth, cropHeight), GraphicsUnit.Pixel);
                }
                var result = new byte[Width * Height * 3 / 2];
                BitmapData locked = normalized.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var row = new byte[Width * 3];
                    for (int y = 0; y < Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), row, 0, row.Length);
                        for (int x = 0; x < Width; x++) result[y * Width + x] = (byte)((row[x * 3 + 2] * 77 + row[x * 3 + 1] * 150 + row[x * 3] * 29 + 128) >> 8);
                    }
                }
                finally { normalized.UnlockBits(locked); }
                // Chroma is neutral and ignored by the demodulator.
                for (int i = Width * Height; i < result.Length; i++) result[i] = 128;
                return result;
            }
        }

        internal static uint Crc32(byte[] bytes, int offset, int count)
        {
            uint crc = UInt32.MaxValue;
            for (int i = 0; i < count; i++) crc = CrcTable[(crc ^ bytes[offset + i]) & 255] ^ (crc >> 8);
            return crc ^ UInt32.MaxValue;
        }
        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++) { uint value = i; for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1; table[i] = value; }
            return table;
        }
        private static int GetNibble(byte[] data, int index) { return (index & 1) == 0 ? data[index / 2] >> 4 : data[index / 2] & 15; }
        private static void SetNibble(byte[] data, int index, int value) { if ((index & 1) == 0) data[index / 2] = (byte)(value << 4); else data[index / 2] |= (byte)(value & 15); }
        private static uint ReadLe32(byte[] data, int offset) { return (uint)data[offset] | (uint)data[offset + 1] << 8 | (uint)data[offset + 2] << 16 | (uint)data[offset + 3] << 24; }
        private static void WriteLe32(byte[] data, int offset, uint value) { for (int i = 0; i < 4; i++) data[offset + i] = (byte)(value >> (i * 8)); }
        private static uint ReadBe32(byte[] data, int offset) { return (uint)data[offset] << 24 | (uint)data[offset + 1] << 16 | (uint)data[offset + 2] << 8 | data[offset + 3]; }
        private static void WriteBe32(byte[] data, int offset, uint value) { for (int i = 0; i < 4; i++) data[offset + i] = (byte)(value >> ((3 - i) * 8)); }
    }
}
