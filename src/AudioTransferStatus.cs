using System;
using System.Security.Cryptography;
using System.Text;

namespace HorizonClient
{
    internal enum AudioTransferPhase { Sending = 1, PassComplete = 2, Cancelled = 3 }
    internal sealed class AudioTransferStatus
    {
        internal Guid Session, Transfer;
        internal uint Sequence;
        internal int PassIndex;
        internal long SentBytes, TotalBytes;
        internal AudioTransferPhase Phase;
    }
    // Progress is not delivery: only the existing published clipboard receipt
    // acknowledges completion. Pass/sequence distinguish progress from replay.
    internal static class AudioTransferStatusProtocol
    {
        internal const int WireBytes = 68;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HCATST01");
        internal static byte[] Encode(AudioTransferStatus value)
        {
            if (!Valid(value)) throw new ArgumentException("Invalid audio transfer status.");
            byte[] bytes = new byte[WireBytes]; Buffer.BlockCopy(Magic, 0, bytes, 0, 8);
            Buffer.BlockCopy(value.Session.ToByteArray(), 0, bytes, 8, 16); Buffer.BlockCopy(value.Transfer.ToByteArray(), 0, bytes, 24, 16);
            Buffer.BlockCopy(BitConverter.GetBytes(value.Sequence), 0, bytes, 40, 4); Buffer.BlockCopy(BitConverter.GetBytes(value.SentBytes), 0, bytes, 44, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(value.TotalBytes), 0, bytes, 52, 8); bytes[60] = (byte)value.Phase; bytes[61] = (byte)value.PassIndex;
            Buffer.BlockCopy(BitConverter.GetBytes(Crc(bytes)), 0, bytes, 64, 4); return bytes;
        }
        internal static bool TryDecode(byte[] bytes, Guid outerTransfer, out AudioTransferStatus value)
        {
            value = null; if (bytes == null || bytes.Length != WireBytes || bytes[62] != 0 || bytes[63] != 0) return false;
            for (int i = 0; i < 8; i++) if (bytes[i] != Magic[i]) return false;
            if (BitConverter.ToUInt32(bytes, 64) != Crc(bytes)) return false;
            byte[] session = new byte[16], transfer = new byte[16]; Buffer.BlockCopy(bytes, 8, session, 0, 16); Buffer.BlockCopy(bytes, 24, transfer, 0, 16);
            var result = new AudioTransferStatus { Session = new Guid(session), Transfer = new Guid(transfer), Sequence = BitConverter.ToUInt32(bytes, 40),
                SentBytes = BitConverter.ToInt64(bytes, 44), TotalBytes = BitConverter.ToInt64(bytes, 52), Phase = (AudioTransferPhase)bytes[60], PassIndex = bytes[61] };
            if (!Valid(result) || TransferId(result) != outerTransfer) return false; value = result; return true;
        }
        internal static Guid TransferId(AudioTransferStatus value)
        {
            byte[] input = new byte[40]; Buffer.BlockCopy(Magic, 0, input, 0, 4); Buffer.BlockCopy(value.Session.ToByteArray(), 0, input, 4, 16);
            Buffer.BlockCopy(value.Transfer.ToByteArray(), 0, input, 20, 16); Buffer.BlockCopy(BitConverter.GetBytes(value.Sequence), 0, input, 36, 4);
            using (var hash = SHA256.Create()) { byte[] digest = hash.ComputeHash(input), result = new byte[16]; Buffer.BlockCopy(digest, 0, result, 0, 16); return new Guid(result); }
        }
        private static bool Valid(AudioTransferStatus v)
        { return v != null && v.Session != Guid.Empty && v.Transfer != Guid.Empty && v.Sequence != 0 && v.PassIndex >= 0 && v.PassIndex <= 10 && v.TotalBytes > 0 && v.TotalBytes <= 33554432 && v.SentBytes >= 0 && v.SentBytes <= v.TotalBytes && v.Phase >= AudioTransferPhase.Sending && v.Phase <= AudioTransferPhase.Cancelled && (v.Phase != AudioTransferPhase.PassComplete || v.SentBytes == v.TotalBytes); }
        private static uint Crc(byte[] bytes) { uint crc = UInt32.MaxValue; for (int i = 0; i < 64; i++) { crc ^= bytes[i]; for (int b = 0; b < 8; b++) crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1; } return ~crc; }
    }
}
