using System;
using System.Drawing;
using HorizonClient;

internal static class FileBenchmarkRetransmissionTests
{
    private static int checks;
    public static int Main()
    {
        try
        {
            LostPacketRecoveredOnNextLongRound(); RealInactivityAndMaximumStillExpire(); SnapshotAndSourceMetadata();
            Console.WriteLine("File benchmark retransmission checks passed ({0} assertions; synthetic clocks/packets only; 102144/102400 regression recovered).", checks); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void LostPacketRecoveredOnNextLongRound()
    {
        long now = 0;
        var runner = new FileBenchmarkRunner(Guid.NewGuid(), new[] { VideoProfile.Compatible }, 102400, 0x0123456789abcdefUL, 0, delegate { return now; });
        FileBenchmarkCommand command = runner.CurrentCommand; var receiver = new FileBenchmarkReceiver(command);
        Send(runner, receiver.Report(now, 320, 240, 0)); receiver.Start(now);
        const int missingIndex = 237;
        for (int index = 0; index < 400; index++)
        {
            now = index * 250L;
            if (index != missingIndex) Check(receiver.Accept(Data(command, index), now), "First round credits each delivered packet once");
            receiver.Tick(now);
            if ((index & 31) == 0) Send(runner, receiver.Report(now, 320, 240, index * 6));
            runner.Tick();
        }
        Check(receiver.ReceivedBytes == 102144 && !receiver.Terminal, "Reproduces the observed 102144/102400 state with one missing block");
        FileBenchmarkReceiverSnapshot first = receiver.Snapshot(now);
        Check(first.UniquePackets == 399 && first.RemainingPackets == 1 && first.Missing.Length == 1, "Diagnostics identify exactly one missing packet");
        Check(first.Missing[0].Segment == 0 && first.Missing[0].Index == missingIndex && first.Missing[0].FileOffset == 60672 && first.Missing[0].Length == 256, "Missing packet diagnostics report the exact index, offset and length without payload bytes");
        for (int index = 0; index <= missingIndex; index++)
        {
            now = 100000 + index * 250L;
            bool added = receiver.Accept(Data(command, index), now);
            Check(added == (index == missingIndex), "Second round credits only the previously missing packet");
            receiver.Tick(now);
            if ((index & 31) == 0 || receiver.Terminal) Send(runner, receiver.Report(now, 320, 240, (400 + index) * 6));
            runner.Tick();
            if (index == 120)
            {
                FileBenchmarkReceiverSnapshot retry = receiver.Snapshot(now);
                Check(!receiver.Terminal && !runner.Finished && retry.ProgressAgeMilliseconds > 30000 && retry.ActivityAgeMilliseconds == 0, "Thirty seconds of valid duplicates keep both endpoints active while unique progress is unchanged");
                Check(retry.DuplicatePackets == 121 && retry.ReceivedBytes == 102144, "Duplicates are counted diagnostically without inflating useful bytes");
            }
        }
        Check(receiver.Status == FileBenchmarkStatus.Completed && receiver.ReceivedBytes == 102400, "The next round recovers the missing packet after almost a minute without new bytes");
        Check(runner.Finished && runner.Snapshot()[0].State == ProfileTestState.Passed && runner.Snapshot()[0].ReceivedBytes == 102400, "Only exact verified completion marks the server profile as passed");
        FileBenchmarkReceiverSnapshot final = receiver.Snapshot(now + 10000);
        Check(final.UniquePackets == 400 && final.DuplicatePackets == 237 && final.ValidPackets == 637 && final.RemainingPackets == 0 && final.Missing.Length == 0, "Final diagnostic counters distinguish every unique and repeated packet");
        Check(final.ElapsedMilliseconds == 159250 && runner.Snapshot()[0].ElapsedMilliseconds == 159250, "Successful goodput includes all retransmission time");
    }

    private static void RealInactivityAndMaximumStillExpire()
    {
        FileBenchmarkCommand command = Command(); var receiver = new FileBenchmarkReceiver(command); receiver.Start(0);
        FileBenchmarkCommand foreign = FileBenchmarkProtocol.Copy(command); foreign.Test = Guid.NewGuid();
        receiver.Accept(Data(foreign, 0), 14999); receiver.Tick(15000);
        Check(receiver.Status == FileBenchmarkStatus.TimedOut && receiver.Failure == FileBenchmarkFailure.NoData, "Foreign packets cannot conceal a true no-data timeout");
        Check(receiver.Snapshot(15000).IgnoredPackets == 1, "Foreign traffic is reported separately");
        receiver = new FileBenchmarkReceiver(command); receiver.Start(0); receiver.Accept(Data(command, 0), 100); receiver.Accept(Data(command, 0), 30000); receiver.Tick(44999);
        Check(!receiver.Terminal, "A recent valid retry keeps a partial file active"); receiver.Tick(45000);
        Check(receiver.Failure == FileBenchmarkFailure.Stalled, "Fifteen seconds without any valid retry still times out a partial file");
        command.MaximumRunMilliseconds = 30000; receiver = new FileBenchmarkReceiver(command); receiver.Start(0);
        for (int time = 1000; time <= 30000; time += 1000) { receiver.Accept(Data(command, 0), time); receiver.Tick(time); }
        Check(receiver.Status == FileBenchmarkStatus.TimedOut && receiver.Failure == FileBenchmarkFailure.MaximumRun, "An explicit maximum deadline wins even while valid retries continue");
        long now = 0; var runner = new FileBenchmarkRunner(command.Session, new[] { command.Profile }, command.FileSize, command.Seed, 30000, delegate { return now; });
        var start = runner.CurrentCommand; runner.Receive(Report(start, FileBenchmarkStatus.Ready, 0, 0));
        for (int time = 5000; time <= 30000; time += 5000) { now = time; runner.Receive(Report(start, FileBenchmarkStatus.Progress, 256, time)); runner.Tick(); }
        Check(!runner.Finished, "Fresh receiver heartbeats avoid premature unique-progress timeout"); now = 30000 + FileBenchmarkRunner.MaximumRunGraceMilliseconds; runner.Tick();
        Check(runner.Finished && runner.Snapshot()[0].Failure == FileBenchmarkFailure.MaximumRun, "Server maximum deadline remains independent of heartbeat activity");
        now = 0; runner = new FileBenchmarkRunner(command.Session, new[] { command.Profile }, command.FileSize, command.Seed, 0, delegate { return now; }); start = runner.CurrentCommand;
        runner.Receive(Report(start, FileBenchmarkStatus.Ready, 0, 0)); now = 1000; runner.Receive(Report(start, FileBenchmarkStatus.Progress, 256, 1000));
        now = 19000; runner.Receive(Report(start, FileBenchmarkStatus.Progress, 256, 1000)); now = 1000 + FileBenchmarkRunner.ReportSilenceGraceMilliseconds; runner.Tick();
        Check(runner.Finished && runner.Snapshot()[0].ServerFinishReason == "receiver_report_timeout", "Replayed stale reports cannot keep a disconnected receiver alive");
    }

    private static void SnapshotAndSourceMetadata()
    {
        FileBenchmarkCommand command = Command(); var receiver = new FileBenchmarkReceiver(command);
        FileBenchmarkReceiverSnapshot snapshot = receiver.Snapshot(0, 2);
        Check(snapshot.TotalPackets == 40 && snapshot.Missing.Length == 2 && snapshot.Missing[1].Index == 1, "Missing-packet snapshots are bounded");
        snapshot.Missing[0].Index = 999;
        Check(receiver.Snapshot(0, 1).Missing[0].Index == 0, "Diagnostic snapshots cannot mutate receiver coverage");
        Reject(delegate { receiver.Snapshot(0, 129); });
        var source = new FileBenchmarkVideoSource(); command.Phase = FileBenchmarkPhase.Start; command.Profile = VideoProfile.Compact640;
        int rounds = 0;
        for (int i = 0; i < 16; i++) using (Bitmap frame = source.Next(command, 30))
        {
            Packet[] packets = VisualProtocol.DecodePackets(frame);
            Check(source.LastTest == command.Test && source.LastSegment == 0 && source.LastPacketIndex == packets[0].Index && source.LastPacketCount == packets.Length && source.SegmentPacketCount == 40, "Source diagnostics describe the generated frame before its indices advance");
            Check(source.LastRound >= rounds, "Source round counters advance monotonically"); rounds = source.LastRound;
        }
        Check(rounds >= 1, "Source diagnostic round number covers repeated cycles");
    }

    private static FileBenchmarkCommand Command() { return new FileBenchmarkCommand { Session = Guid.NewGuid(), Test = Guid.NewGuid(), Profile = VideoProfile.Compatible, FileSize = 10240, Seed = 0x0123456789abcdefUL }; }
    private static Packet Data(FileBenchmarkCommand command, int index)
    {
        int chunk = VisualProtocol.GetChunkSize(command.Profile), length = Math.Min(chunk, (int)command.FileSize - index * chunk); byte[] bytes = new byte[length];
        BenchmarkData.Fill(command.Seed, (long)index * chunk, bytes, 0, bytes.Length);
        return new Packet { TransferId = FileBenchmarkProtocol.SegmentTransferId(command.Test, 0), Index = index, Count = ((int)command.FileSize + chunk - 1) / chunk, TotalLength = (int)command.FileSize, Data = bytes, ChunkSize = chunk, Profile = command.Profile };
    }
    private static FileBenchmarkReport Report(FileBenchmarkCommand command, FileBenchmarkStatus status, long bytes, long elapsed) { return new FileBenchmarkReport { Session = command.Session, Test = command.Test, Profile = command.Profile, Status = status, TotalBytes = command.FileSize, ReceivedBytes = bytes, ElapsedMilliseconds = elapsed }; }
    private static void Send(FileBenchmarkRunner runner, FileBenchmarkReport report) { FileBenchmarkReport parsed; Check(FileBenchmarkProtocol.TryReport(FileBenchmarkProtocol.Report(report), report.Session, out parsed), "Receiver reports preserve wire integrity"); runner.Receive(parsed); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { checks++; return; } throw new Exception("Unbounded diagnostic sample was accepted."); }
}
