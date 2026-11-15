using System;
using HorizonClient;

internal static class ReceiverNackEligibilityTests
{
    private static int checks;
    private static int Main()
    {
        try
        {
            EarlyPacketsDescribeHolesOnly(); LostTailAfterWrap(); LostTailAfterQuietPass(); LaterSegmentProvesEarlierTail(); LargeSegmentedFile();
            Console.WriteLine("Receiver NACK eligibility checks passed: " + checks + ". Synthetic bytes/clocks only; no hardware, files or clipboard access."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static FileBenchmarkCommand Command(long bytes, VideoProfile profile)
    {
        return new FileBenchmarkCommand { Session = Guid.NewGuid(), Test = Guid.NewGuid(), Profile = profile, FileSize = bytes,
            Seed = 781, InactivityMilliseconds = 15000, Phase = FileBenchmarkPhase.Start, CompactReports = true };
    }
    private static Packet Data(FileBenchmarkCommand command, int segment, int index)
    {
        int chunk = VisualProtocol.GetChunkSize(command.Profile), length = FileBenchmarkProtocol.SegmentLength(command, segment);
        var bytes = new byte[Math.Min(chunk, length - index * chunk)];
        BenchmarkData.Fill(command.Seed, (long)segment * command.SegmentSize + (long)index * chunk, bytes, 0, bytes.Length);
        return new Packet { TransferId = FileBenchmarkProtocol.SegmentTransferId(command.Test, segment), Index = index,
            Count = (length + chunk - 1) / chunk, TotalLength = length, Data = bytes, ChunkSize = chunk, Profile = command.Profile };
    }
    private static void Missing(FileBenchmarkReceiver receiver, long now, int segment, params int[] expected)
    {
        FileBenchmarkReport report = receiver.Report(now, 1920, 1080, 1, true);
        Check(report.MissingSegment == segment, "Only an eligible segment is requested.");
        int count = report.MissingIndices == null ? 0 : report.MissingIndices.Length;
        Check(count == expected.Length, "The NACK contains only proven holes, not future first-pass blocks.");
        for (int i = 0; i < count; i++) Check(report.MissingIndices[i] == expected[i], "The exact expected hole is requested.");
        FileBenchmarkReport parsed; Check(FileBenchmarkProtocol.TryReport(FileBenchmarkProtocol.Report(report), report.Session, out parsed), "Filtered reports retain their bounded CRC wire format.");
    }
    private static void EarlyPacketsDescribeHolesOnly()
    {
        FileBenchmarkCommand command = Command(102400, VideoProfile.Compact320); var receiver = new FileBenchmarkReceiver(command); receiver.Start(0);
        receiver.Accept(Data(command, 0, 0), 61); Missing(receiver, 61, -1);
        Check(receiver.Snapshot(61, 1).Missing[0].Index == 1, "Diagnostic snapshots still disclose all absent packets independently of retransmission eligibility.");
        receiver.Accept(Data(command, 0, 0), 120); Missing(receiver, 120, -1);
        receiver.Accept(Data(command, 0, 5), 200); Missing(receiver, 201, 0, 1, 2, 3, 4);
        receiver.Accept(Data(command, 0, 0), 202); Missing(receiver, 203, 0, 1, 2, 3, 4);
        Check(receiver.ReceivedBytes == 512, "Neither duplicates nor NACK metadata add useful bytes.");
    }
    private static void LostTailAfterWrap()
    {
        FileBenchmarkCommand command = Command(10 * 1082, VideoProfile.Mp4Modem); var receiver = new FileBenchmarkReceiver(command); receiver.Start(0);
        for (int index = 0; index < 9; index++) receiver.Accept(Data(command, 0, index), index * 100);
        Missing(receiver, 900, -1); receiver.Accept(Data(command, 0, 0), 1000); Missing(receiver, 1001, -1);
        receiver.Accept(Data(command, 0, 0), 2100); Missing(receiver, 2101, 0, 9);
        Check(receiver.Accept(Data(command, 0, 9), 2200) && receiver.Status == FileBenchmarkStatus.Completed && receiver.ReceivedBytes == command.FileSize,
            "A missing last packet becomes eligible after a conservative observed wrap and still requires actual byte verification.");
    }
    private static void LostTailAfterQuietPass()
    {
        FileBenchmarkCommand command = Command(10 * 1082, VideoProfile.Mp4Modem); var receiver = new FileBenchmarkReceiver(command); receiver.Start(0);
        for (int index = 0; index < 9; index++) receiver.Accept(Data(command, 0, index), 100 + index * 100);
        Missing(receiver, 2000, -1); Missing(receiver, 2900, 0, 9);
        Check(receiver.Status == FileBenchmarkStatus.Progress, "The fallback tail request never fabricates completion or changes receiver timeout state.");
        command = Command(102400, VideoProfile.Compatible); receiver = new FileBenchmarkReceiver(command); receiver.Start(0);
        receiver.Accept(Data(command, 0, 0), 1); Missing(receiver, 10000, -1);
        Check(receiver.Report(140000, 640, 480, 1, true).MissingIndices.Length == 16,
            "A long compatible first pass is not prematurely replaced by repairs, but elapsed quiet fallback eventually releases its lost tail.");
    }
    private static void LaterSegmentProvesEarlierTail()
    {
        FileBenchmarkCommand command = Command(3 * 1024, VideoProfile.Compact320); command.SegmentSize = 1024;
        var receiver = new FileBenchmarkReceiver(command); receiver.Start(0);
        receiver.Accept(Data(command, 0, 0), 10); receiver.Accept(Data(command, 0, 2), 20); Missing(receiver, 21, 0, 1);
        receiver.Accept(Data(command, 1, 0), 30); Missing(receiver, 31, 0, 1, 3);
        receiver.Accept(Data(command, 0, 1), 40); receiver.Accept(Data(command, 0, 3), 50); Missing(receiver, 51, -1);
        receiver.Accept(Data(command, 1, 2), 60); Missing(receiver, 61, 1, 1);
        Check(receiver.ReceivedBytes == 6 * 256, "Out-of-order segment repairs retain exact global file offsets and useful-byte accounting.");
    }
    private static void LargeSegmentedFile()
    {
        FileBenchmarkCommand command = Command(100L * 1024 * 1024, VideoProfile.Compact320);
        long before = GC.GetTotalMemory(true); var receiver = new FileBenchmarkReceiver(command); long retained = GC.GetTotalMemory(true) - before;
        Check(retained < 4L * 1024 * 1024, "A100MiB test retains bounded coverage/state rather than a copy of the logical file.");
        receiver.Start(0); receiver.Accept(Data(command, 0, 0), 1); receiver.Accept(Data(command, 0, 5), 2); Missing(receiver, 3, 0, 1, 2, 3, 4);
        receiver.Accept(Data(command, 1, 0), 4); FileBenchmarkReport report = receiver.Report(5, 1920, 1080, 3, true);
        Check(report.MissingSegment == 0 && report.MissingIndices.Length == 16 && report.MissingIndices[15] == 17,
            "Segmented100MiB coverage emits at most16 earlier missing indices and never floods future segments.");
        Check(receiver.Snapshot(5, 0).TotalPackets == 409600, "The bounded receiver still accounts for every packet across all100MiB.");
        GC.KeepAlive(receiver);
    }
    private static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
}
