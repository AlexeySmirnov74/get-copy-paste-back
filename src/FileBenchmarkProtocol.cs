using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace HorizonClient
{
    internal enum FileBenchmarkPhase { Prepare = 0, Start = 1, Cancel = 2 }
    internal enum FileBenchmarkStatus { Ready = 0, Progress = 1, Completed = 2, Cancelled = 3, InvalidData = 4, TimedOut = 5 }
    internal enum FileBenchmarkFailure { None = 0, InvalidMetadata = 1, DataMismatch = 2, NoData = 3, Stalled = 4, MaximumRun = 5 }

    internal sealed class FileBenchmarkCommand
    {
        internal Guid Session, Test;
        internal VideoProfile Profile;
        internal long FileSize;
        internal int SegmentSize = VisualProtocol.MaxTransferBytes;
        internal ulong Seed;
        internal int InactivityMilliseconds = 15000;
        internal long MaximumRunMilliseconds;
        internal FileBenchmarkPhase Phase;
        internal bool CompactReports;
        // Local sender scheduling metadata, never part of the video command.
        internal int RetransmitSegment = -1;
        internal int[] RetransmitIndices;
        internal long RetransmitRevision;
    }

    internal sealed class FileBenchmarkReport
    {
        internal Guid Session, Test;
        internal VideoProfile Profile;
        internal FileBenchmarkStatus Status;
        internal FileBenchmarkFailure Failure;
        internal long ReceivedBytes, TotalBytes, ElapsedMilliseconds;
        internal int FrameWidth, FrameHeight, Frames;
        internal int MissingSegment = -1;
        internal int[] MissingIndices;
    }

    internal sealed class FileBenchmarkMissingPacket
    {
        internal int Segment, Index, Length;
        internal long FileOffset;
    }

    internal sealed class FileBenchmarkReceiverSnapshot
    {
        internal Guid Session, Test;
        internal VideoProfile Profile;
        internal FileBenchmarkStatus Status;
        internal FileBenchmarkFailure Failure;
        internal long FileBytes, ReceivedBytes, TotalPackets, UniquePackets, RemainingPackets;
        internal long PacketAttempts, ValidPackets, DuplicatePackets, IgnoredPackets, InvalidMetadataPackets, DataMismatchPackets;
        internal long ElapsedMilliseconds, ActivityAgeMilliseconds, ProgressAgeMilliseconds;
        internal bool Started;
        internal FileBenchmarkMissingPacket[] Missing;
    }

    // Separate, bounded envelopes prevent synthetic file bytes and diagnostics
    // from entering the normal clipboard protocol. A logical100MiB test is
    // segmented into ordinary transfers under the existing32MiB global limit.
    internal static class FileBenchmarkProtocol
    {
        internal const long MaximumFileBytes = 100L * 1024 * 1024;
        internal const int MaximumMissingPackets = 16;
        private const int Length = 84;
        private static readonly byte[] CommandMagic = Encoding.ASCII.GetBytes("HCBFTS03");
        private static readonly byte[] ReportMagic = Encoding.ASCII.GetBytes("HCBFTR03");
        private static readonly byte[] MissingReportMagic = Encoding.ASCII.GetBytes("HCBFTR04");

        internal static byte[] Command(FileBenchmarkCommand command)
        {
            Validate(command);
            var bytes = new byte[Length]; Buffer.BlockCopy(CommandMagic, 0, bytes, 0, 8);
            PutGuid(bytes, 8, command.Session); PutGuid(bytes, 24, command.Test);
            Put32(bytes, 40, (uint)command.Profile); bytes[44] = (byte)command.Phase;
            bytes[45] = command.CompactReports ? (byte)1 : (byte)0;
            Put64(bytes, 48, (ulong)command.FileSize); Put32(bytes, 56, (uint)command.SegmentSize);
            Put32(bytes, 60, (uint)command.InactivityMilliseconds); Put64(bytes, 64, command.Seed);
            Put64(bytes, 72, (ulong)command.MaximumRunMilliseconds); Finish(bytes); return bytes;
        }

        internal static bool TryCommand(Packet packet, out FileBenchmarkCommand command)
        {
            command = null;
            if (packet == null || packet.Count != 1 || packet.Index != 0 || packet.TotalLength != Length || !Envelope(packet.Data, CommandMagic)) return false;
            byte[] bytes = packet.Data;
            if (bytes[45] > 1 || bytes[46] != 0 || bytes[47] != 0) return false;
            var result = new FileBenchmarkCommand {
                Session = GetGuid(bytes, 8), Test = GetGuid(bytes, 24), Profile = (VideoProfile)Get32(bytes, 40),
                Phase = (FileBenchmarkPhase)bytes[44], FileSize = (long)Get64(bytes, 48), SegmentSize = (int)Get32(bytes, 56),
                InactivityMilliseconds = (int)Get32(bytes, 60), Seed = Get64(bytes, 64), MaximumRunMilliseconds = (long)Get64(bytes, 72), CompactReports = bytes[45] == 1
            };
            try { Validate(result); } catch (ArgumentException) { return false; }
            if (result.Session != packet.TransferId) return false;
            command = result; return true;
        }

        internal static byte[] Report(FileBenchmarkReport report)
        {
            if (!ValidReport(report)) throw new ArgumentException("Invalid file benchmark report.");
            int missing = report.MissingIndices == null ? 0 : report.MissingIndices.Length;
            var bytes = new byte[missing == 0 ? Length : 92 + missing * 2]; Buffer.BlockCopy(missing == 0 ? ReportMagic : MissingReportMagic, 0, bytes, 0, 8);
            PutGuid(bytes, 8, report.Session); PutGuid(bytes, 24, report.Test);
            bytes[40] = (byte)report.Profile; bytes[41] = (byte)report.Status; bytes[42] = (byte)report.Failure;
            Put64(bytes, 44, (ulong)report.ReceivedBytes); Put64(bytes, 52, (ulong)report.TotalBytes); Put64(bytes, 60, (ulong)report.ElapsedMilliseconds);
            Put32(bytes, 68, (uint)report.FrameWidth); Put32(bytes, 72, (uint)report.FrameHeight); Put32(bytes, 76, (uint)report.Frames);
            if (missing != 0)
            {
                Put16(bytes, 80, report.MissingSegment); bytes[82] = (byte)missing;
                int first = report.MissingIndices[0]; Put32(bytes, 84, (uint)first);
                for (int i = 0; i < missing; i++) Put16(bytes, 88 + i * 2, report.MissingIndices[i] - first);
            }
            Finish(bytes); return bytes;
        }

        internal static bool TryReport(byte[] bytes, Guid session, out FileBenchmarkReport report)
        {
            report = null;
            bool missing = Envelope(bytes, MissingReportMagic, true);
            if ((!missing && !Envelope(bytes, ReportMagic)) || session == Guid.Empty || GetGuid(bytes, 8) != session || bytes[43] != 0) return false;
            var result = new FileBenchmarkReport {
                Session = session, Test = GetGuid(bytes, 24), Profile = (VideoProfile)bytes[40],
                Status = (FileBenchmarkStatus)bytes[41], Failure = (FileBenchmarkFailure)bytes[42],
                ReceivedBytes = (long)Get64(bytes, 44), TotalBytes = (long)Get64(bytes, 52), ElapsedMilliseconds = (long)Get64(bytes, 60),
                FrameWidth = (int)Get32(bytes, 68), FrameHeight = (int)Get32(bytes, 72), Frames = (int)Get32(bytes, 76)
            };
            if (missing)
            {
                int count = bytes[82];
                if (bytes[83] != 0 || count < 1 || count > MaximumMissingPackets || bytes.Length != 92 + count * 2) return false;
                uint first = Get32(bytes, 84);
                if (first > Int32.MaxValue) return false;
                result.MissingSegment = Get16(bytes, 80); result.MissingIndices = new int[count];
                for (int i = 0; i < count; i++)
                {
                    long index = (long)first + Get16(bytes, 88 + i * 2);
                    if (index > Int32.MaxValue) return false;
                    result.MissingIndices[i] = (int)index;
                }
                if (Get16(bytes, 88) != 0) return false;
            }
            if (!ValidReport(result)) return false;
            report = result; return true;
        }

        internal static Guid SegmentTransferId(Guid test, int segment)
        {
            if (test == Guid.Empty || segment < 0 || segment >= 4096) throw new ArgumentException("Invalid benchmark segment identity.");
            return BridgeControl.BenchmarkEpoch(test, segment);
        }

        internal static bool TrySegmentIndex(Guid test, Guid transfer, out int segment)
        {
            segment = -1;
            if (test == Guid.Empty) return false;
            byte[] candidate = transfer.ToByteArray(), expected = test.ToByteArray();
            if ((candidate[15] & 128) == 0) return false;
            for (int i = 0; i < 12; i++) if (candidate[i] != expected[i]) return false;
            uint value = Get32(candidate, 12) & 0x7fffffffU;
            if (value >= 4096) return false;
            segment = (int)value; return true;
        }

        internal static int SegmentCount(FileBenchmarkCommand command)
        {
            Validate(command); return (int)((command.FileSize + command.SegmentSize - 1) / command.SegmentSize);
        }

        internal static int SegmentLength(FileBenchmarkCommand command, int segment)
        {
            int count = SegmentCount(command);
            if (segment < 0 || segment >= count) throw new ArgumentOutOfRangeException("segment");
            return (int)Math.Min(command.SegmentSize, command.FileSize - (long)segment * command.SegmentSize);
        }

        internal static FileBenchmarkCommand Copy(FileBenchmarkCommand command)
        {
            Validate(command);
            return new FileBenchmarkCommand { Session = command.Session, Test = command.Test, Profile = command.Profile, FileSize = command.FileSize,
                SegmentSize = command.SegmentSize, Seed = command.Seed, InactivityMilliseconds = command.InactivityMilliseconds,
                MaximumRunMilliseconds = command.MaximumRunMilliseconds, Phase = command.Phase, CompactReports = command.CompactReports };
        }

        internal static bool ValidMissing(FileBenchmarkCommand command, int segment, int[] indices)
        {
            if (command == null || indices == null || indices.Length < 1 || indices.Length > MaximumMissingPackets ||
                segment < 0 || segment >= SegmentCount(command)) return false;
            int size = VisualProtocol.GetChunkSize(command.Profile), count = (SegmentLength(command, segment) + size - 1) / size;
            for (int i = 0; i < indices.Length; i++)
                if (indices[i] < 0 || indices[i] >= count || i != 0 && indices[i] <= indices[i - 1]) return false;
            return true;
        }

        internal static bool SameTest(FileBenchmarkCommand left, FileBenchmarkCommand right)
        {
            return left != null && right != null && left.Session == right.Session && left.Test == right.Test && left.Profile == right.Profile && left.FileSize == right.FileSize &&
                left.SegmentSize == right.SegmentSize && left.Seed == right.Seed && left.InactivityMilliseconds == right.InactivityMilliseconds && left.MaximumRunMilliseconds == right.MaximumRunMilliseconds;
        }

        internal static void Validate(FileBenchmarkCommand command)
        {
            if (command == null || command.Session == Guid.Empty || command.Test == Guid.Empty || command.Test == command.Session || !VisualProtocol.IsDefinedProfile(command.Profile) ||
                command.FileSize < 1 || command.FileSize > MaximumFileBytes || command.SegmentSize < 1 || command.SegmentSize > VisualProtocol.MaxTransferBytes ||
                (command.FileSize + command.SegmentSize - 1) / command.SegmentSize > 4096 || command.InactivityMilliseconds < 1000 || command.InactivityMilliseconds > 600000 ||
                command.MaximumRunMilliseconds < 0 || command.MaximumRunMilliseconds > 7L * 24 * 60 * 60 * 1000 || (command.MaximumRunMilliseconds != 0 && command.MaximumRunMilliseconds < 1000) ||
                (int)command.Phase < 0 || (int)command.Phase > 2)
                throw new ArgumentException("Invalid file benchmark command.");
        }

        internal static bool ValidReport(FileBenchmarkReport report)
        {
            if (report == null || report.Session == Guid.Empty || report.Test == Guid.Empty || report.Test == report.Session || !VisualProtocol.IsDefinedProfile(report.Profile) ||
                (int)report.Status < 0 || (int)report.Status > 5 || (int)report.Failure < 0 || (int)report.Failure > 5 ||
                report.TotalBytes < 1 || report.TotalBytes > MaximumFileBytes || report.ReceivedBytes < 0 || report.ReceivedBytes > report.TotalBytes ||
                report.ElapsedMilliseconds < 0 || report.FrameWidth < 0 || report.FrameWidth > 7680 || report.FrameHeight < 0 || report.FrameHeight > 4320 || report.Frames < 0) return false;
            if (report.Status == FileBenchmarkStatus.Ready && (report.ReceivedBytes != 0 || report.ElapsedMilliseconds != 0)) return false;
            if (report.Status == FileBenchmarkStatus.Completed && report.ReceivedBytes != report.TotalBytes) return false;
            if ((report.Status == FileBenchmarkStatus.Ready || report.Status == FileBenchmarkStatus.Progress || report.Status == FileBenchmarkStatus.Completed || report.Status == FileBenchmarkStatus.Cancelled) && report.Failure != FileBenchmarkFailure.None) return false;
            if (report.Status == FileBenchmarkStatus.InvalidData && report.Failure != FileBenchmarkFailure.InvalidMetadata && report.Failure != FileBenchmarkFailure.DataMismatch) return false;
            if (report.Status == FileBenchmarkStatus.TimedOut && report.Failure != FileBenchmarkFailure.NoData && report.Failure != FileBenchmarkFailure.Stalled && report.Failure != FileBenchmarkFailure.MaximumRun) return false;
            int missing = report.MissingIndices == null ? 0 : report.MissingIndices.Length;
            if (missing == 0) return report.MissingSegment == -1;
            if (missing > MaximumMissingPackets || report.MissingSegment < 0 || report.MissingSegment >= 4096 ||
                report.Status != FileBenchmarkStatus.Progress || report.ReceivedBytes < 1 || report.ReceivedBytes >= report.TotalBytes) return false;
            int maximumPackets = (VisualProtocol.MaxTransferBytes + VisualProtocol.GetChunkSize(report.Profile) - 1) / VisualProtocol.GetChunkSize(report.Profile);
            for (int i = 0; i < missing; i++)
                if (report.MissingIndices[i] < 0 || report.MissingIndices[i] >= maximumPackets ||
                    i != 0 && (report.MissingIndices[i] <= report.MissingIndices[i - 1] || (long)report.MissingIndices[i] - report.MissingIndices[0] > UInt16.MaxValue)) return false;
            return true;
        }

        private static bool Envelope(byte[] bytes, byte[] magic, bool missing = false)
        {
            if (bytes == null || (missing ? bytes.Length < 94 || bytes.Length > 92 + MaximumMissingPackets * 2 : bytes.Length != Length)) return false;
            for (int i = 0; i < magic.Length; i++) if (bytes[i] != magic[i]) return false;
            return Get32(bytes, bytes.Length - 4) == Crc32(bytes, bytes.Length - 4);
        }
        private static void Finish(byte[] bytes) { Put32(bytes, bytes.Length - 4, Crc32(bytes, bytes.Length - 4)); }
        private static uint Crc32(byte[] bytes, int count)
        {
            uint crc = UInt32.MaxValue;
            for (int i = 0; i < count; i++) { crc ^= bytes[i]; for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1; }
            return crc ^ UInt32.MaxValue;
        }
        private static Guid GetGuid(byte[] bytes, int offset) { var id = new byte[16]; Buffer.BlockCopy(bytes, offset, id, 0, 16); return new Guid(id); }
        private static int Get16(byte[] bytes, int offset) { return bytes[offset] | bytes[offset + 1] << 8; }
        private static void Put16(byte[] bytes, int offset, int value) { bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8); }
        private static void PutGuid(byte[] bytes, int offset, Guid id) { Buffer.BlockCopy(id.ToByteArray(), 0, bytes, offset, 16); }
        private static uint Get32(byte[] bytes, int offset) { return (uint)bytes[offset] | (uint)bytes[offset + 1] << 8 | (uint)bytes[offset + 2] << 16 | (uint)bytes[offset + 3] << 24; }
        private static void Put32(byte[] bytes, int offset, uint value) { for (int i = 0; i < 4; i++) bytes[offset + i] = (byte)(value >> (8 * i)); }
        private static ulong Get64(byte[] bytes, int offset) { return Get32(bytes, offset) | (ulong)Get32(bytes, offset + 4) << 32; }
        private static void Put64(byte[] bytes, int offset, ulong value) { Put32(bytes, offset, (uint)value); Put32(bytes, offset + 4, (uint)(value >> 32)); }
    }

    // No I/O: validates every byte at its logical file offset and keeps only
    // packet-presence bits. Completion therefore proves full file coverage,
    // including out-of-order segments, without retaining or publishing the file.
    internal sealed class FileBenchmarkReceiver
    {
        internal readonly FileBenchmarkCommand Command;
        private readonly BitArray[] segments;
        private readonly int[] highestAcceptedIndex;
        private readonly long[] firstSegmentPacketAt, lastSegmentProgressAt;
        private readonly bool[] segmentWrapObserved;
        private int highestAcceptedSegment = -1;
        private long remainingPackets, totalPackets, startedAt, lastActivityAt, lastProgressAt, finishedAt = -1;
        private long packetAttempts, validPackets, duplicatePackets, ignoredPackets, invalidMetadataPackets, dataMismatchPackets;
        private long lastActivityLog = -1, lastProgressLog = -1;
        internal long ReceivedBytes { get; private set; }
        internal bool Started { get; private set; }
        internal FileBenchmarkStatus Status { get; private set; }
        internal FileBenchmarkFailure Failure { get; private set; }
        internal bool Terminal { get { return Status >= FileBenchmarkStatus.Completed; } }

        internal FileBenchmarkReceiver(FileBenchmarkCommand command)
        {
            Command = FileBenchmarkProtocol.Copy(command); segments = new BitArray[FileBenchmarkProtocol.SegmentCount(Command)];
            highestAcceptedIndex = new int[segments.Length]; firstSegmentPacketAt = new long[segments.Length];
            lastSegmentProgressAt = new long[segments.Length]; segmentWrapObserved = new bool[segments.Length];
            int chunkSize = VisualProtocol.GetChunkSize(command.Profile);
            for (int i = 0; i < segments.Length; i++)
            {
                int count = (FileBenchmarkProtocol.SegmentLength(command, i) + chunkSize - 1) / chunkSize;
                segments[i] = new BitArray(count); remainingPackets += count;
                highestAcceptedIndex[i] = -1; firstSegmentPacketAt[i] = lastSegmentProgressAt[i] = -1;
            }
            totalPackets = remainingPackets;
            Status = FileBenchmarkStatus.Ready;
        }

        internal void Start(long now)
        {
            if (Started || Terminal) return;
            Started = true; startedAt = lastActivityAt = lastProgressAt = now; Status = FileBenchmarkStatus.Progress;
            Log("file.receiver.start", now);
        }

        internal bool Accept(Packet packet, long now)
        {
            if (!Started || Terminal || packet == null) return false;
            packetAttempts++;
            int segment;
            if (!FileBenchmarkProtocol.TrySegmentIndex(Command.Test, packet.TransferId, out segment) || segment >= segments.Length)
            {
                ignoredPackets++;
                if (ignoredPackets <= 3 && DiagnosticLog.IsEnabled) DiagnosticLog.Write("file.receiver.foreign_packet", "test", Command.Test, "transfer", packet.TransferId, "ignored_packets", ignoredPackets);
                return false;
            }
            int segmentLength = FileBenchmarkProtocol.SegmentLength(Command, segment), chunkSize = VisualProtocol.GetChunkSize(Command.Profile);
            if (packet.Profile != Command.Profile || packet.TotalLength != segmentLength ||
                !VisualProtocol.ValidMetadata(packet.Index, packet.Count, packet.TotalLength, packet.Data, packet.Profile, packet.ChunkSize))
            {
                invalidMetadataPackets++;
                if (DiagnosticLog.IsEnabled) DiagnosticLog.Write("file.receiver.invalid_metadata", "test", Command.Test, "segment", segment, "index", packet.Index, "count", packet.Count, "total_length", packet.TotalLength,
                    "profile", (int)packet.Profile, "declared_chunk", packet.ChunkSize, "packet_bytes", packet.Data == null ? -1 : packet.Data.Length, "expected_segment_bytes", segmentLength, "expected_chunk", chunkSize);
                Fail(FileBenchmarkStatus.InvalidData, FileBenchmarkFailure.InvalidMetadata, now); return false;
            }
            long fileOffset = (long)segment * Command.SegmentSize + (long)packet.Index * chunkSize;
            if (!BenchmarkData.Verify(Command.Seed, fileOffset, packet.Data))
            {
                dataMismatchPackets++;
                if (DiagnosticLog.IsEnabled) DiagnosticLog.Write("file.receiver.data_mismatch", "test", Command.Test, "segment", segment, "index", packet.Index, "file_offset", fileOffset, "packet_bytes", packet.Data.Length);
                Fail(FileBenchmarkStatus.InvalidData, FileBenchmarkFailure.DataMismatch, now); return false;
            }
            validPackets++; lastActivityAt = now;
            if (firstSegmentPacketAt[segment] < 0) firstSegmentPacketAt[segment] = now;
            if (segment > highestAcceptedSegment) highestAcceptedSegment = segment;
            if (segments[segment][packet.Index])
            {
                // A repeated camera frame alone does not prove a new sweep.
                // Require both a backwards duplicate and enough observation
                // time for a complete segment pass before releasing its tail.
                if (packet.Index < highestAcceptedIndex[segment] && now >= firstSegmentPacketAt[segment] &&
                    now - firstSegmentPacketAt[segment] >= SegmentPassMilliseconds(segment)) segmentWrapObserved[segment] = true;
                duplicatePackets++;
                if (duplicatePackets <= 3 || lastActivityLog < 0 || now - lastActivityLog >= 8000)
                {
                    lastActivityLog = now; Log("file.receiver.duplicate", now, segment, packet.Index);
                }
                return false;
            }
            segments[segment][packet.Index] = true; remainingPackets--; ReceivedBytes += packet.Data.Length; lastProgressAt = now;
            highestAcceptedIndex[segment] = Math.Max(highestAcceptedIndex[segment], packet.Index); lastSegmentProgressAt[segment] = now;
            if (remainingPackets == 0 && ReceivedBytes == Command.FileSize)
            {
                Status = FileBenchmarkStatus.Completed; finishedAt = now; Log("file.receiver.completed", now, segment, packet.Index);
            }
            else if (totalPackets - remainingPackets <= 8 || lastProgressLog < 0 || now - lastProgressLog >= 2000 || ((totalPackets - remainingPackets) & 255) == 0)
            {
                lastProgressLog = now; Log("file.receiver.progress", now, segment, packet.Index);
            }
            return true;
        }

        internal void Tick(long now)
        {
            if (!Started || Terminal) return;
            if (Command.MaximumRunMilliseconds > 0 && Elapsed(now) >= Command.MaximumRunMilliseconds)
                Fail(FileBenchmarkStatus.TimedOut, FileBenchmarkFailure.MaximumRun, now);
            else if (Math.Max(0, now - lastActivityAt) >= Command.InactivityMilliseconds)
                Fail(FileBenchmarkStatus.TimedOut, ReceivedBytes == 0 ? FileBenchmarkFailure.NoData : FileBenchmarkFailure.Stalled, now);
        }

        internal void Cancel(long now) { if (!Terminal) Fail(FileBenchmarkStatus.Cancelled, FileBenchmarkFailure.None, now); }
        private void Fail(FileBenchmarkStatus status, FileBenchmarkFailure failure, long now)
        {
            Status = status; Failure = failure; finishedAt = now; Log("file.receiver.terminal", now);
        }
        internal long Elapsed(long now) { return Started ? Math.Max(0, (finishedAt < 0 ? now : finishedAt) - startedAt) : 0; }
        internal FileBenchmarkReport Report(long now, int width, int height, int frames)
        { return Report(now, width, height, frames, false); }

        internal FileBenchmarkReport Report(long now, int width, int height, int frames, bool includeMissing)
        {
            var report = new FileBenchmarkReport { Session = Command.Session, Test = Command.Test, Profile = Command.Profile, Status = Status, Failure = Failure,
                ReceivedBytes = ReceivedBytes, TotalBytes = Command.FileSize, ElapsedMilliseconds = Elapsed(now), FrameWidth = width, FrameHeight = height, Frames = frames };
            if (includeMissing && Status == FileBenchmarkStatus.Progress && ReceivedBytes > 0 && remainingPackets > 0)
                AddMissingRequest(report, now);
            return report;
        }

        private long SegmentPassMilliseconds(int segment)
        {
            int slots = VisualProtocol.GetPacketsPerFrame(Command.Profile);
            long frames = ((long)segments[segment].Length + slots - 1) / slots;
            double fps = Command.Profile == VideoProfile.Compatible ? 5.0 : 30.0;
            return Math.Max(2000, (long)Math.Ceiling(frames * 1000.0 / fps * 31.0 / 27.0 * 1.5));
        }

        private void AddMissingRequest(FileBenchmarkReport report, long now)
        {
            for (int segment = 0; segment < segments.Length; segment++)
            {
                long first = firstSegmentPacketAt[segment], progress = lastSegmentProgressAt[segment];
                long quiet = Math.Min(2000, Command.InactivityMilliseconds / 2);
                bool quietPass = first >= 0 && now >= first && now - first >= SegmentPassMilliseconds(segment) &&
                    progress >= 0 && now >= progress && now - progress >= quiet;
                // The source traverses segments in order. A later segment or
                // observed second pass proves that the earlier tail was sent.
                // Otherwise request only holes below this segment's high-water
                // mark; future packets must keep their normal first-pass slots.
                int limit = segment < highestAcceptedSegment || segmentWrapObserved[segment] || quietPass ?
                    segments[segment].Length : Math.Max(0, highestAcceptedIndex[segment]);
                List<int> indices = null; int firstIndex = -1;
                for (int index = 0; index < limit; index++) if (!segments[segment][index])
                {
                    if (indices == null) { indices = new List<int>(FileBenchmarkProtocol.MaximumMissingPackets); firstIndex = index; }
                    if ((long)index - firstIndex > UInt16.MaxValue) break;
                    indices.Add(index);
                    if (indices.Count == FileBenchmarkProtocol.MaximumMissingPackets) break;
                }
                if (indices == null) continue;
                report.MissingSegment = segment; report.MissingIndices = indices.ToArray(); return;
            }
        }

        // A bounded, independent copy for diagnostic logs. Missing offsets and
        // counters disclose no clipboard contents or generated payload bytes.
        internal FileBenchmarkReceiverSnapshot Snapshot(long now, int maximumMissing = 16)
        {
            if (maximumMissing < 0 || maximumMissing > 128) throw new ArgumentOutOfRangeException("maximumMissing");
            var missing = new List<FileBenchmarkMissingPacket>(Math.Min(maximumMissing, (int)Math.Min(remainingPackets, 128)));
            int chunk = VisualProtocol.GetChunkSize(Command.Profile);
            for (int segment = 0; segment < segments.Length && missing.Count < maximumMissing; segment++)
            {
                int length = FileBenchmarkProtocol.SegmentLength(Command, segment);
                for (int index = 0; index < segments[segment].Length && missing.Count < maximumMissing; index++) if (!segments[segment][index])
                    missing.Add(new FileBenchmarkMissingPacket { Segment = segment, Index = index, FileOffset = (long)segment * Command.SegmentSize + (long)index * chunk, Length = Math.Min(chunk, length - index * chunk) });
            }
            long at = finishedAt >= 0 ? finishedAt : now;
            return new FileBenchmarkReceiverSnapshot { Session = Command.Session, Test = Command.Test, Profile = Command.Profile, Status = Status, Failure = Failure,
                FileBytes = Command.FileSize, ReceivedBytes = ReceivedBytes, TotalPackets = totalPackets, UniquePackets = totalPackets - remainingPackets, RemainingPackets = remainingPackets,
                PacketAttempts = packetAttempts, ValidPackets = validPackets, DuplicatePackets = duplicatePackets, IgnoredPackets = ignoredPackets,
                InvalidMetadataPackets = invalidMetadataPackets, DataMismatchPackets = dataMismatchPackets, Started = Started, ElapsedMilliseconds = Elapsed(now),
                ActivityAgeMilliseconds = Started ? Math.Max(0, at - lastActivityAt) : 0, ProgressAgeMilliseconds = Started ? Math.Max(0, at - lastProgressAt) : 0, Missing = missing.ToArray() };
        }

        private void Log(string eventName, long now, int segment = -1, int index = -1)
        {
            if (!DiagnosticLog.IsEnabled) return;
            FileBenchmarkReceiverSnapshot snapshot = Snapshot(now);
            var missing = new StringBuilder();
            foreach (FileBenchmarkMissingPacket packet in snapshot.Missing)
            {
                if (missing.Length != 0) missing.Append(';');
                missing.Append(packet.Segment).Append(':').Append(packet.Index).Append('@').Append(packet.FileOffset).Append('+').Append(packet.Length);
            }
            DiagnosticLog.Write(eventName, "session", snapshot.Session, "test", snapshot.Test, "profile", (int)snapshot.Profile,
                "status", snapshot.Status, "failure", snapshot.Failure, "segment", segment, "index", index,
                "received_bytes", snapshot.ReceivedBytes, "file_bytes", snapshot.FileBytes, "total_packets", snapshot.TotalPackets, "unique_packets", snapshot.UniquePackets, "remaining_packets", snapshot.RemainingPackets,
                "packet_attempts", snapshot.PacketAttempts, "valid_packets", snapshot.ValidPackets, "duplicate_packets", snapshot.DuplicatePackets, "ignored_packets", snapshot.IgnoredPackets,
                "invalid_metadata_packets", snapshot.InvalidMetadataPackets, "data_mismatch_packets", snapshot.DataMismatchPackets,
                "elapsed_ms", snapshot.ElapsedMilliseconds, "activity_age_ms", snapshot.ActivityAgeMilliseconds, "progress_age_ms", snapshot.ProgressAgeMilliseconds,
                "missing_sample", missing.ToString(), "missing_sample_count", snapshot.Missing.Length);
        }
    }
}
