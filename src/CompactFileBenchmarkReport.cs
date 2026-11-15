using System;
using System.Security.Cryptography;
using System.Text;

namespace HorizonClient
{
    // Correlation, not authentication: the current random command supplies all
    // identity/size fields, while the audio carries only its96-bit digest and
    // bounded control counters. No clipboard or file contents are included.
    internal static class CompactFileBenchmarkReport
    {
        internal const int BaseWireLength = 36, MaximumWireLength = BaseWireLength + 3 * 16;
        private static readonly byte[] Domain = Encoding.ASCII.GetBytes("HorizonClient:compact-file-report:v1\0");

        internal static byte[] Encode(FileBenchmarkCommand command, FileBenchmarkReport report)
        {
            FileBenchmarkProtocol.Validate(command);
            if (report == null || report.Session != command.Session || report.Test != command.Test || report.Profile != command.Profile || report.TotalBytes != command.FileSize)
                throw new ArgumentException("The compact report must belong to the exact benchmark command.");
            FileBenchmarkProtocol.Report(report); // Reuse the canonical metadata checks.
            if (report.ElapsedMilliseconds > UInt32.MaxValue) throw new ArgumentOutOfRangeException("report");
            int[] missing = report.MissingIndices;
            int count = missing == null ? 0 : missing.Length;
            if (!ValidMissing(command, report.Status, report.MissingSegment, missing)) throw new ArgumentException("Invalid compact missing-packet window.");
            byte[] wire = new byte[BaseWireLength + count * 3];
            wire[0] = (byte)(0xa0 | count);
            Buffer.BlockCopy(Token(command), 0, wire, 1, 12);
            wire[13] = (byte)((int)report.Status | ((int)report.Failure << 3));
            Put(wire, 14, (uint)report.ReceivedBytes); Put(wire, 18, (uint)report.ElapsedMilliseconds);
            Put16(wire, 22, report.FrameWidth); Put16(wire, 24, report.FrameHeight); Put(wire, 26, (uint)report.Frames);
            Put16(wire, 30, count == 0 ? 65535 : report.MissingSegment);
            for (int i = 0; i < count; i++) { int at = 32 + i * 3; wire[at] = (byte)missing[i]; wire[at + 1] = (byte)(missing[i] >> 8); wire[at + 2] = (byte)(missing[i] >> 16); }
            Put(wire, wire.Length - 4, Crc32(wire, wire.Length - 4));
            return wire;
        }

        internal static bool TryDecode(byte[] wire, FileBenchmarkCommand command, out FileBenchmarkReport report)
        {
            report = null;
            if (!ValidWire(wire) || command == null) return false;
            try { FileBenchmarkProtocol.Validate(command); } catch (ArgumentException) { return false; }
            byte[] token = Token(command); int different = 0;
            for (int i = 0; i < token.Length; i++) different |= token[i] ^ wire[i + 1];
            if (different != 0 || (wire[13] & 0xc0) != 0 || Get(wire, 26) > Int32.MaxValue) return false;
            int count = wire[0] & 31;
            var result = new FileBenchmarkReport { Session = command.Session, Test = command.Test, Profile = command.Profile,
                Status = (FileBenchmarkStatus)(wire[13] & 7), Failure = (FileBenchmarkFailure)((wire[13] >> 3) & 7),
                ReceivedBytes = Get(wire, 14), TotalBytes = command.FileSize, ElapsedMilliseconds = Get(wire, 18),
                FrameWidth = Get16(wire, 22), FrameHeight = Get16(wire, 24), Frames = (int)Get(wire, 26),
                MissingSegment = count == 0 ? -1 : Get16(wire, 30), MissingIndices = count == 0 ? null : new int[count] };
            if (count == 0 && Get16(wire, 30) != 65535) return false;
            for (int i = 0; i < count; i++) { int at = 32 + i * 3; result.MissingIndices[i] = wire[at] | wire[at + 1] << 8 | wire[at + 2] << 16; }
            if (!ValidMissing(command, result.Status, result.MissingSegment, result.MissingIndices)) return false;
            try { FileBenchmarkProtocol.Report(result); } catch (ArgumentException) { return false; }
            report = result; return true;
        }

        private static bool ValidMissing(FileBenchmarkCommand command, FileBenchmarkStatus status, int segment, int[] indices)
        {
            int count = indices == null ? 0 : indices.Length;
            if (count == 0) return segment == -1;
            if (count > FileBenchmarkProtocol.MaximumMissingPackets || status != FileBenchmarkStatus.Progress || segment < 0 || segment >= FileBenchmarkProtocol.SegmentCount(command)) return false;
            int length = FileBenchmarkProtocol.SegmentLength(command, segment), chunk = VisualProtocol.GetChunkSize(command.Profile);
            int packets = (length + chunk - 1) / chunk;
            for (int i = 0; i < count; i++) if (indices[i] < 0 || indices[i] >= packets || indices[i] >= 1 << 24 || (i > 0 && indices[i] <= indices[i - 1])) return false;
            return true;
        }

        internal static int WireLength(byte header)
        {
            int count = header & 31;
            return (header & 0xe0) == 0xa0 && count <= 16 ? BaseWireLength + count * 3 : 0;
        }
        internal static bool ValidWire(byte[] wire)
        { return wire != null && wire.Length >= BaseWireLength && wire.Length <= MaximumWireLength && WireLength(wire[0]) == wire.Length && Get(wire, wire.Length - 4) == Crc32(wire, wire.Length - 4); }

        private static byte[] Token(FileBenchmarkCommand command)
        {
            FileBenchmarkCommand normalized = FileBenchmarkProtocol.Copy(command);
            normalized.Phase = FileBenchmarkPhase.Prepare; normalized.CompactReports = false;
            byte[] canonical = FileBenchmarkProtocol.Command(normalized), input = new byte[Domain.Length + canonical.Length];
            Buffer.BlockCopy(Domain, 0, input, 0, Domain.Length); Buffer.BlockCopy(canonical, 0, input, Domain.Length, canonical.Length);
            byte[] result = new byte[12];
            using (SHA256 hash = SHA256.Create()) Buffer.BlockCopy(hash.ComputeHash(input), 0, result, 0, result.Length);
            return result;
        }
        private static uint Get(byte[] bytes, int at) { return (uint)(bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16 | bytes[at + 3] << 24); }
        private static int Get16(byte[] bytes, int at) { return bytes[at] | bytes[at + 1] << 8; }
        private static void Put(byte[] bytes, int at, uint value) { for (int i = 0; i < 4; i++) bytes[at + i] = (byte)(value >> (i * 8)); }
        private static void Put16(byte[] bytes, int at, int value) { bytes[at] = (byte)value; bytes[at + 1] = (byte)(value >> 8); }
        private static uint Crc32(byte[] bytes, int length)
        {
            uint crc = UInt32.MaxValue;
            for (int i = 0; i < length; i++) { crc ^= bytes[i]; for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320U; }
            return ~crc;
        }
    }
}
