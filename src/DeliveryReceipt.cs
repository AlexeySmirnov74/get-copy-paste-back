using System;
using System.Text;
using System.Security.Cryptography;

namespace HorizonClient
{
    // A receipt is sent only after the decoded video payload has been published
    // successfully to the remote Windows clipboard. It never contains user data.
    internal sealed class DeliveryReceipt
    {
        internal Guid Session, Transfer;
        internal int EncodedBytes;
        internal const int Length = 52;
        internal const int FastTokenLength = 16, FastWireLength = FastTokenLength + 4;
        private const int MaximumEncodedBytes = 32 * 1024 * 1024;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HCBDLV01");
        private static readonly byte[] FastDomain = Encoding.ASCII.GetBytes("HorizonClient:delivery-receipt:v1\0");

        // The compact receipt carries no user bytes. The random session and
        // transfer identities bind its 128-bit digest to one exact publication.
        // It is a correlation token, not authentication against a hostile peer.
        internal static byte[] FastToken(Guid session, Guid transfer, int encodedBytes)
        {
            if (session == Guid.Empty || transfer == Guid.Empty || encodedBytes < 1 || encodedBytes > MaximumEncodedBytes)
                throw new ArgumentException("Invalid fast video delivery receipt.");
            byte[] input = new byte[FastDomain.Length + 36];
            Buffer.BlockCopy(FastDomain, 0, input, 0, FastDomain.Length);
            Buffer.BlockCopy(session.ToByteArray(), 0, input, FastDomain.Length, 16);
            Buffer.BlockCopy(transfer.ToByteArray(), 0, input, FastDomain.Length + 16, 16);
            Put(input, FastDomain.Length + 32, (uint)encodedBytes);
            byte[] token = new byte[FastTokenLength];
            using (SHA256 hash = SHA256.Create()) Buffer.BlockCopy(hash.ComputeHash(input), 0, token, 0, token.Length);
            return token;
        }

        internal static byte[] EncodeFast(Guid session, Guid transfer, int encodedBytes)
        {
            byte[] wire = new byte[FastWireLength];
            Buffer.BlockCopy(FastToken(session, transfer, encodedBytes), 0, wire, 0, FastTokenLength);
            Put(wire, FastTokenLength, Crc32(wire, FastTokenLength));
            return wire;
        }

        internal static bool TryDecodeFast(byte[] wire, out byte[] token)
        {
            token = null;
            if (wire == null || wire.Length != FastWireLength || Get(wire, FastTokenLength) != Crc32(wire, FastTokenLength)) return false;
            token = new byte[FastTokenLength]; Buffer.BlockCopy(wire, 0, token, 0, token.Length);
            return true;
        }

        internal static bool SameToken(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != FastTokenLength || second.Length != FastTokenLength) return false;
            int difference = 0;
            for (int i = 0; i < FastTokenLength; i++) difference |= first[i] ^ second[i];
            return difference == 0;
        }

        internal static byte[] Encode(Guid session, Guid transfer, int encodedBytes)
        {
            if (session == Guid.Empty || transfer == Guid.Empty || encodedBytes < 1 || encodedBytes > MaximumEncodedBytes)
                throw new ArgumentException("Invalid video delivery receipt.");
            byte[] bytes = new byte[Length];
            Buffer.BlockCopy(Magic, 0, bytes, 0, Magic.Length);
            Buffer.BlockCopy(session.ToByteArray(), 0, bytes, 8, 16);
            Buffer.BlockCopy(transfer.ToByteArray(), 0, bytes, 24, 16);
            Put(bytes, 40, (uint)encodedBytes);
            Put(bytes, 48, Crc32(bytes, 48));
            return bytes;
        }

        internal static bool TryDecode(byte[] bytes, Guid expectedSession, out DeliveryReceipt receipt)
        {
            receipt = null;
            if (bytes == null || bytes.Length != Length || expectedSession == Guid.Empty) return false;
            for (int i = 0; i < Magic.Length; i++) if (bytes[i] != Magic[i]) return false;
            if (Get(bytes, 44) != 0 || Get(bytes, 48) != Crc32(bytes, 48)) return false;
            byte[] id = new byte[16];
            Buffer.BlockCopy(bytes, 8, id, 0, 16); Guid session = new Guid(id);
            Buffer.BlockCopy(bytes, 24, id, 0, 16); Guid transfer = new Guid(id);
            uint encodedBytes = Get(bytes, 40);
            if (session != expectedSession || transfer == Guid.Empty || encodedBytes < 1 || encodedBytes > MaximumEncodedBytes) return false;
            receipt = new DeliveryReceipt { Session = session, Transfer = transfer, EncodedBytes = (int)encodedBytes };
            return true;
        }

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
