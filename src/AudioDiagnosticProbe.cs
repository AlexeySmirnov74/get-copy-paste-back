using System;
using System.Text;

namespace HorizonClient
{
    // Explicit audio test metadata; contains no bridge session or clipboard data.
    internal sealed class AudioDiagnosticProbe
    {
        internal Guid Id;
        internal AudioModemKind Modem;
        internal long SentUtcTicks;
        internal const int Length = 40;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HCBTST01");

        internal static byte[] Encode(Guid id, AudioModemKind modem, long sentUtcTicks)
        {
            if (id == Guid.Empty || !ValidModem(modem) || sentUtcTicks < 1 || sentUtcTicks > DateTime.MaxValue.Ticks)
                throw new ArgumentException("Invalid audio diagnostic probe.");
            byte[] bytes = new byte[Length]; Buffer.BlockCopy(Magic, 0, bytes, 0, Magic.Length);
            Buffer.BlockCopy(id.ToByteArray(), 0, bytes, 8, 16);
            Put(bytes, 24, (uint)modem);
            for (int i = 0; i < 8; i++) bytes[28 + i] = (byte)((ulong)sentUtcTicks >> (i * 8));
            Put(bytes, 36, Crc32(bytes, 36));
            return bytes;
        }

        internal static bool TryDecode(byte[] bytes, out AudioDiagnosticProbe probe)
        {
            probe = null;
            if (bytes == null || bytes.Length != Length) return false;
            for (int i = 0; i < Magic.Length; i++) if (bytes[i] != Magic[i]) return false;
            if (Get(bytes, 36) != Crc32(bytes, 36)) return false;
            byte[] idBytes = new byte[16]; Buffer.BlockCopy(bytes, 8, idBytes, 0, idBytes.Length); Guid id = new Guid(idBytes);
            AudioModemKind modem = (AudioModemKind)Get(bytes, 24);
            ulong ticks = 0; for (int i = 0; i < 8; i++) ticks |= (ulong)bytes[28 + i] << (i * 8);
            if (id == Guid.Empty || !ValidModem(modem) || ticks < 1 || ticks > (ulong)DateTime.MaxValue.Ticks) return false;
            probe = new AudioDiagnosticProbe { Id = id, Modem = modem, SentUtcTicks = (long)ticks }; return true;
        }

        private static bool ValidModem(AudioModemKind modem)
        { return modem == AudioModemKind.Fsk || modem == AudioModemKind.Quiet || modem == AudioModemKind.Aicodix; }
        private static uint Get(byte[] bytes, int offset)
        { return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24); }
        private static void Put(byte[] bytes, int offset, uint value)
        { for (int i = 0; i < 4; i++) bytes[offset + i] = (byte)(value >> (i * 8)); }
        private static uint Crc32(byte[] bytes, int count)
        {
            uint crc = UInt32.MaxValue;
            for (int i = 0; i < count; i++)
            {
                crc ^= bytes[i];
                for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320U;
            }
            return ~crc;
        }
    }
}
