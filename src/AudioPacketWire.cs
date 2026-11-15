using System;

namespace HorizonClient
{
    // Native modems carry several existing 256-byte packets in one OFDM frame.
    // Keeping the transfer layout unchanged permits switching modems mid-transfer.
    internal static class AudioPacketWire
    {
        internal const int HeaderBytes = 36;
        internal const int OverheadBytes = HeaderBytes + 4;
        internal const int MaximumWireBytes = 65536;
        private const uint Magic = 0x32414348; // HCA2
        private static readonly uint[] CrcTable = MakeCrcTable();

        internal static byte[] Encode(AudioModemKind kind, Guid transfer, int firstIndex, byte[] bytes, int maximumWireBytes, out int packetCount)
        {
            if ((kind != AudioModemKind.Quiet && kind != AudioModemKind.Aicodix) || transfer == Guid.Empty || bytes == null ||
                bytes.Length < 1 || bytes.Length > VisualProtocol.MaxTransferBytes || maximumWireBytes < OverheadBytes + AudioModem.MaxPayloadBytes || maximumWireBytes > MaximumWireBytes)
                throw new ArgumentException("Invalid native audio batch.");
            int totalPackets = (bytes.Length + AudioModem.MaxPayloadBytes - 1) / AudioModem.MaxPayloadBytes;
            if (firstIndex < 0 || firstIndex >= totalPackets) throw new ArgumentOutOfRangeException("firstIndex");
            int slots = (maximumWireBytes - OverheadBytes) / AudioModem.MaxPayloadBytes;
            int offset = firstIndex * AudioModem.MaxPayloadBytes;
            int length = Math.Min(bytes.Length - offset, slots * AudioModem.MaxPayloadBytes);
            packetCount = (length + AudioModem.MaxPayloadBytes - 1) / AudioModem.MaxPayloadBytes;
            byte[] wire = new byte[OverheadBytes + length];
            Put(wire, 0, Magic); wire[4] = 2; wire[5] = (byte)kind; wire[6] = (byte)length; wire[7] = (byte)(length >> 8);
            Buffer.BlockCopy(transfer.ToByteArray(), 0, wire, 8, 16);
            Put(wire, 24, (uint)firstIndex); Put(wire, 28, (uint)totalPackets); Put(wire, 32, (uint)bytes.Length);
            Buffer.BlockCopy(bytes, offset, wire, HeaderBytes, length);
            Put(wire, HeaderBytes + length, Crc(wire, HeaderBytes + length));
            return wire;
        }

        internal static Packet[] Decode(AudioModemKind kind, byte[] wire)
        {
            if ((kind != AudioModemKind.Quiet && kind != AudioModemKind.Aicodix) || wire == null || wire.Length < OverheadBytes || wire.Length > MaximumWireBytes ||
                Get(wire, 0) != Magic || wire[4] != 2 || wire[5] != (byte)kind) return null;
            int length = wire[6] | wire[7] << 8;
            if (length < 1 || wire.Length != length + OverheadBytes || Crc(wire, HeaderBytes + length) != Get(wire, HeaderBytes + length)) return null;
            uint first = Get(wire, 24), count = Get(wire, 28), total = Get(wire, 32);
            if (total < 1 || total > VisualProtocol.MaxTransferBytes || count != (total + AudioModem.MaxPayloadBytes - 1) / AudioModem.MaxPayloadBytes || first >= count) return null;
            int offset = (int)first * AudioModem.MaxPayloadBytes;
            if (length > (int)total - offset || (offset + length != total && length % AudioModem.MaxPayloadBytes != 0)) return null;
            byte[] identity = new byte[16]; Buffer.BlockCopy(wire, 8, identity, 0, identity.Length); Guid id = new Guid(identity);
            if (id == Guid.Empty) return null;
            int packets = (length + AudioModem.MaxPayloadBytes - 1) / AudioModem.MaxPayloadBytes;
            var result = new Packet[packets];
            for (int index = 0; index < packets; index++)
            {
                int size = Math.Min(AudioModem.MaxPayloadBytes, length - index * AudioModem.MaxPayloadBytes);
                byte[] data = new byte[size]; Buffer.BlockCopy(wire, HeaderBytes + index * AudioModem.MaxPayloadBytes, data, 0, size);
                result[index] = new Packet { TransferId = id, Index = (int)first + index, Count = (int)count, TotalLength = (int)total,
                    Profile = VideoProfile.Compatible, ChunkSize = AudioModem.MaxPayloadBytes, Data = data };
            }
            return result;
        }

        private static void Put(byte[] bytes, int offset, uint value) { for (int i = 0; i < 4; i++) bytes[offset + i] = (byte)(value >> (8 * i)); }
        private static uint Get(byte[] bytes, int offset) { return (uint)bytes[offset] | (uint)bytes[offset + 1] << 8 | (uint)bytes[offset + 2] << 16 | (uint)bytes[offset + 3] << 24; }
        private static uint Crc(byte[] bytes, int count) { uint value = UInt32.MaxValue; for (int i = 0; i < count; i++) value = CrcTable[(value ^ bytes[i]) & 255] ^ value >> 8; return ~value; }
        private static uint[] MakeCrcTable() { var result = new uint[256]; for (uint i = 0; i < 256; i++) { uint value = i; for (int j = 0; j < 8; j++) value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320U; result[i] = value; } return result; }
    }
}
