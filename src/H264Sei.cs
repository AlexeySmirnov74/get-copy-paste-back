using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HorizonClient
{
    public enum H264NalFormat { AnnexB, Avcc, SingleNal }

    public sealed class H264SeiMessage
    {
        public Guid Uuid { get; private set; }
        public byte[] Payload { get; private set; }
        internal H264SeiMessage(Guid uuid, byte[] payload) { Uuid = uuid; Payload = payload; }
    }

    // Parses the original encoded bytes only. Decoded RGB pixels contain no SEI.
    // Samples must contain complete NAL units; malformed/truncated NALs are ignored.
    public static class H264Sei
    {
        public const int MaximumSampleBytes = 16 * 1024 * 1024;
        public const int MaximumUserDataBytes = 1024 * 1024;
        public static readonly Guid ProbeUuid = new Guid("6b82d43f-4d4f-4a2a-9967-0d620761c001");
        public const string ProbeMarker = "HORIZON_SEI_PROBE_V1";
        private static readonly byte[] MarkerBytes = Encoding.ASCII.GetBytes(ProbeMarker);

        public static H264SeiMessage[] ParseSample(byte[] sample, H264NalFormat format, int avccLengthSize = 4)
        {
            if (sample == null) throw new ArgumentNullException("sample");
            if (sample.Length > MaximumSampleBytes) throw new ArgumentException("H.264 sample exceeds the bounded parser limit.", "sample");
            if (format != H264NalFormat.AnnexB && format != H264NalFormat.Avcc && format != H264NalFormat.SingleNal) throw new ArgumentOutOfRangeException("format");
            if (format == H264NalFormat.Avcc && avccLengthSize != 1 && avccLengthSize != 2 && avccLengthSize != 4) throw new ArgumentOutOfRangeException("avccLengthSize");
            var result = new List<H264SeiMessage>();
            if (format == H264NalFormat.SingleNal) ParseNal(sample, 0, sample.Length, result);
            else if (format == H264NalFormat.Avcc)
            {
                int position = 0;
                // First validate every length, so a truncated sample cannot be
                // reported as a successfully received complete access unit.
                while (position < sample.Length)
                {
                    if (sample.Length - position < avccLengthSize) return new H264SeiMessage[0];
                    uint size = 0; for (int i = 0; i < avccLengthSize; i++) size = (size << 8) | sample[position++];
                    if (size == 0 || size > (uint)(sample.Length - position)) return new H264SeiMessage[0];
                    ParseNal(sample, position, (int)size, result); position += (int)size;
                }
            }
            else
            {
                int prefix;
                int start = FindStartCode(sample, 0, out prefix);
                if (start < 0) return new H264SeiMessage[0];
                for (int i = 0; i < start; i++) if (sample[i] != 0) return new H264SeiMessage[0];
                while (start >= 0)
                {
                    int nal = start + prefix, nextPrefix;
                    int next = FindStartCode(sample, nal, out nextPrefix);
                    int end = next < 0 ? sample.Length : next;
                    while (end > nal && sample[end - 1] == 0) end--; // Annex B trailing_zero_8bits.
                    ParseNal(sample, nal, end - nal, result);
                    start = next; prefix = nextPrefix;
                }
            }
            return result.ToArray();
        }

        public static bool IsProbe(H264SeiMessage message)
        {
            if (message == null || message.Uuid != ProbeUuid || message.Payload.Length < MarkerBytes.Length) return false;
            for (int i = 0; i < MarkerBytes.Length; i++) if (message.Payload[i] != MarkerBytes[i]) return false;
            return message.Payload.Length == MarkerBytes.Length || message.Payload[MarkerBytes.Length] == 0 || message.Payload[MarkerBytes.Length] == (byte)':';
        }

        public static byte[] CreateUserDataNal(Guid uuid, byte[] payload, H264NalFormat format, int avccLengthSize = 4)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            if (payload.Length > MaximumUserDataBytes) throw new ArgumentException("SEI user data exceeds the bounded parser limit.", "payload");
            if (format != H264NalFormat.AnnexB && format != H264NalFormat.Avcc && format != H264NalFormat.SingleNal) throw new ArgumentOutOfRangeException("format");
            if (format == H264NalFormat.Avcc && avccLengthSize != 1 && avccLengthSize != 2 && avccLengthSize != 4) throw new ArgumentOutOfRangeException("avccLengthSize");
            byte[] rbsp;
            using (var output = new MemoryStream())
            {
                WriteExtended(output, 5); WriteExtended(output, payload.Length + 16);
                byte[] wireUuid = ToWireUuid(uuid); output.Write(wireUuid, 0, wireUuid.Length); output.Write(payload, 0, payload.Length); output.WriteByte(0x80);
                rbsp = output.ToArray();
            }
            byte[] nal;
            using (var output = new MemoryStream())
            {
                output.WriteByte(6); int zeros = 0;
                foreach (byte value in rbsp)
                {
                    if (zeros == 2 && value <= 3) { output.WriteByte(3); zeros = 0; }
                    output.WriteByte(value); zeros = value == 0 ? zeros + 1 : 0;
                }
                nal = output.ToArray();
            }
            if (format == H264NalFormat.SingleNal) return nal;
            int prefix = format == H264NalFormat.AnnexB ? 4 : avccLengthSize;
            if (format == H264NalFormat.Avcc && avccLengthSize < 4 && nal.Length >= (1 << (avccLengthSize * 8))) throw new ArgumentException("NAL does not fit the selected AVC length prefix.", "avccLengthSize");
            byte[] framed = new byte[prefix + nal.Length];
            if (format == H264NalFormat.AnnexB) framed[3] = 1;
            else for (int i = 0; i < prefix; i++) framed[prefix - 1 - i] = (byte)(nal.Length >> (i * 8));
            Buffer.BlockCopy(nal, 0, framed, prefix, nal.Length); return framed;
        }

        private static void ParseNal(byte[] source, int offset, int length, List<H264SeiMessage> output)
        {
            if (length < 2 || (source[offset] & 0x80) != 0 || (source[offset] & 31) != 6) return;
            byte[] rbsp = Unescape(source, offset + 1, length - 1);
            if (rbsp == null) return;
            var pending = new List<H264SeiMessage>(); int position = 0;
            while (position < rbsp.Length)
            {
                if (position == rbsp.Length - 1 && rbsp[position] == 0x80) { output.AddRange(pending); return; }
                int type, size;
                if (!ReadExtended(rbsp, ref position, out type) || !ReadExtended(rbsp, ref position, out size) || size > rbsp.Length - position) return;
                if (type == 5)
                {
                    if (size < 16 || size - 16 > MaximumUserDataBytes) return;
                    byte[] uuid = new byte[16], payload = new byte[size - 16];
                    Buffer.BlockCopy(rbsp, position, uuid, 0, uuid.Length); Buffer.BlockCopy(rbsp, position + 16, payload, 0, payload.Length);
                    pending.Add(new H264SeiMessage(FromWireUuid(uuid), payload));
                }
                position += size;
            }
        }

        private static byte[] Unescape(byte[] source, int offset, int length)
        {
            byte[] output = new byte[length]; int count = 0, zeros = 0;
            for (int i = 0; i < length; i++)
            {
                byte value = source[offset + i];
                if (zeros == 2)
                {
                    if (value == 3) { if (i + 1 >= length || source[offset + i + 1] > 3) return null; zeros = 0; continue; }
                    if (value <= 2) return null;
                }
                output[count++] = value; zeros = value == 0 ? zeros + 1 : 0;
            }
            if (count != output.Length) Array.Resize(ref output, count); return output;
        }

        private static int FindStartCode(byte[] bytes, int start, out int length)
        {
            for (int i = start; i + 2 < bytes.Length; i++) if (bytes[i] == 0 && bytes[i + 1] == 0)
            {
                if (bytes[i + 2] == 1) { length = 3; return i; }
                if (i + 3 < bytes.Length && bytes[i + 2] == 0 && bytes[i + 3] == 1) { length = 4; return i; }
            }
            length = 0; return -1;
        }
        private static bool ReadExtended(byte[] bytes, ref int position, out int value)
        {
            value = 0;
            while (position < bytes.Length) { int part = bytes[position++]; if (value > MaximumSampleBytes - part) return false; value += part; if (part != 255) return true; }
            return false;
        }
        private static void WriteExtended(Stream output, int value) { while (value >= 255) { output.WriteByte(255); value -= 255; } output.WriteByte((byte)value); }
        private static byte[] ToWireUuid(Guid uuid)
        {
            byte[] bytes = uuid.ToByteArray(); Swap(bytes, 0, 3); Swap(bytes, 1, 2); Swap(bytes, 4, 5); Swap(bytes, 6, 7); return bytes;
        }
        private static Guid FromWireUuid(byte[] bytes) { Swap(bytes, 0, 3); Swap(bytes, 1, 2); Swap(bytes, 4, 5); Swap(bytes, 6, 7); return new Guid(bytes); }
        private static void Swap(byte[] bytes, int a, int b) { byte value = bytes[a]; bytes[a] = bytes[b]; bytes[b] = value; }
    }
}
