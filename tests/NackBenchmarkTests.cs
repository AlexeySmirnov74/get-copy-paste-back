using System;
using System.Drawing;
using HorizonClient;

internal static class NackBenchmarkTests
{
    private static int checks;
    private static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    private static int Main()
    {
        try
        {
            WireBounds(); MissingTail(); FreshnessAndLease(); InvalidDirectCompletion(); LateCompletion();
            Console.WriteLine("NACK benchmark checks passed: " + checks + ". Synthetic data/frames only; no camera, audio, clipboard or driver access."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static FileBenchmarkCommand Command(VideoProfile profile, long bytes)
    {
        return new FileBenchmarkCommand { Session = Guid.NewGuid(), Test = Guid.NewGuid(), Profile = profile, FileSize = bytes,
            Seed = 0x3456789abcdef012UL, Phase = FileBenchmarkPhase.Start };
    }
    private static FileBenchmarkReport Report(FileBenchmarkCommand command, long bytes, long elapsed)
    {
        return new FileBenchmarkReport { Session = command.Session, Test = command.Test, Profile = command.Profile, TotalBytes = command.FileSize,
            ReceivedBytes = bytes, ElapsedMilliseconds = elapsed, Status = FileBenchmarkStatus.Progress };
    }
    private static Packet Data(FileBenchmarkCommand command, int index)
    {
        int size = VisualProtocol.GetChunkSize(command.Profile), length = FileBenchmarkProtocol.SegmentLength(command, 0);
        var payload = new byte[Math.Min(size, length - index * size)]; BenchmarkData.Fill(command.Seed, (long)index * size, payload, 0, payload.Length);
        return new Packet { TransferId = FileBenchmarkProtocol.SegmentTransferId(command.Test, 0), Index = index, Count = (length + size - 1) / size,
            TotalLength = length, Data = payload, ChunkSize = size, Profile = command.Profile };
    }
    private static void WireBounds()
    {
        FileBenchmarkCommand command = Command(VideoProfile.Compatible, 32 * 1024 * 1024);
        var report = Report(command, 1, 1000); report.MissingSegment = 0; report.MissingIndices = new[] { 70000, 70002, 131071 };
        byte[] wire = FileBenchmarkProtocol.Report(report); FileBenchmarkReport parsed;
        Check(wire.Length == 98 && FileBenchmarkProtocol.TryReport(wire, command.Session, out parsed) && parsed.MissingIndices[0] == 70000 && parsed.MissingIndices[2] == 131071,
            "Window base retains full32-bit indices above65535 without wrap; deltas stay bounded.");
        for (int offset = 0; offset < wire.Length; offset++) for (int bit = 0; bit < 8; bit++)
        {
            wire[offset] ^= (byte)(1 << bit); Check(!FileBenchmarkProtocol.TryReport(wire, command.Session, out parsed), "CRC rejects every changed NACK header/index bit."); wire[offset] ^= (byte)(1 << bit);
        }
        for (int length = 0; length < wire.Length; length++) { var truncated = new byte[length]; Array.Copy(wire, truncated, length); Check(!FileBenchmarkProtocol.TryReport(truncated, command.Session, out parsed), "Truncated NACK cannot read outside its envelope."); }
        var extra = new byte[wire.Length + 1]; Array.Copy(wire, extra, wire.Length); Check(!FileBenchmarkProtocol.TryReport(extra, command.Session, out parsed), "Trailing bytes are rejected.");
        foreach (int[] invalid in new[] { new[] { -1 }, new[] { 131072 }, new[] { 2, 2 }, new[] { 2, 1 }, new[] { 0, 65536 }, new int[17] })
        { report.MissingIndices = invalid; Reject(delegate { FileBenchmarkProtocol.Report(report); }, "Malformed NACK request cannot serialize."); }
        report.MissingIndices = new[] { 65 }; report.Status = FileBenchmarkStatus.Completed; report.ReceivedBytes = report.TotalBytes;
        Reject(delegate { FileBenchmarkProtocol.Report(report); }, "Terminal completion never carries stale missing data.");
        report.MissingSegment = -1; report.MissingIndices = null;
        Check(FileBenchmarkProtocol.Report(report).Length == 84 && FileBenchmarkProtocol.TryReport(FileBenchmarkProtocol.Report(report), command.Session, out parsed), "Terminal and legacy reports remain exactly84 bytes.");
        command.CompactReports = true; FileBenchmarkCommand decoded;
        wire = FileBenchmarkProtocol.Command(command);
        Check(wire[45] == 1 && FileBenchmarkProtocol.TryCommand(BridgeControl.Single(command.Session, wire), out decoded) && decoded.CompactReports, "Compact report opt-in uses only reserved command bit45/0.");
        FileBenchmarkCommand legacy = FileBenchmarkProtocol.Copy(command); legacy.CompactReports = false;
        Check(FileBenchmarkProtocol.SameTest(command, legacy) && !FileBenchmarkProtocol.Copy(legacy).CompactReports && FileBenchmarkProtocol.Copy(command).CompactReports,
            "Alternating old/new command announcements describe one exact test while Copy preserves negotiation.");
        wire[45] = 2; Finish(wire); Check(!FileBenchmarkProtocol.TryCommand(BridgeControl.Single(command.Session, wire), out decoded), "Unknown command capability bits are rejected even with a valid CRC.");
    }
    private static void MissingTail()
    {
        long now = 0; const int lost = 65;
        var runner = new FileBenchmarkRunner(Guid.NewGuid(), new[] { VideoProfile.Mp4Modem }, 102400, 0x3456789abcdef012UL, 0, delegate { return now; });
        FileBenchmarkCommand command = runner.CurrentCommand; var receiver = new FileBenchmarkReceiver(command);
        runner.Receive(receiver.Report(0, 1920, 1080, 0)); command = runner.CurrentCommand; receiver.Start(0);
        var source = new FileBenchmarkVideoSource(); int count = Data(command, 0).Count;
        Check(count == 95, "Reproduces the real100KiB MP4Modem95-packet transfer.");
        for (int index = 0; index < count; index++)
        {
            using (Bitmap frame = source.Next(command, 30)) Check(source.LastPacketIndex == index, "Baseline source traverses the complete first round.");
            if (index != lost) receiver.Accept(Data(command, index), index * 34);
        }
        Check(receiver.Snapshot(4000).RemainingPackets == 1 && receiver.Snapshot(4000).Missing[0].Index == lost, "Exactly real missing packet65 remains; no useful bytes are fabricated.");
        int baseline = 0;
        while (++baseline < 100)
        {
            using (Bitmap frame = source.Next(command, 30))
                if (source.LastPacketIndex == lost) { Check(VisualProtocol.DecodePackets(frame)[0].Index == lost, "The baseline tail frame really contains the missing packet."); break; }
        }
        now = 10000; FileBenchmarkReport nack = receiver.Report(now, 1920, 1080, count, true);
        FileBenchmarkReport parsed; Check(FileBenchmarkProtocol.TryReport(FileBenchmarkProtocol.Report(nack), command.Session, out parsed), "Receiver missing window survives real wire framing.");
        runner.Receive(parsed); FileBenchmarkCommand retry = runner.CurrentCommand;
        Check(retry.RetransmitIndices.Length == 1 && retry.RetransmitIndices[0] == lost, "Runner publishes only the requested missing packet to its source.");
        var prioritized = new FileBenchmarkVideoSource();
        for (int burst = 0; burst < 3; burst++) using (Bitmap frame = prioritized.Next(retry, 30))
        {
            Check(prioritized.LastWasRetransmission && prioritized.LastPacketIndex == lost && prioritized.LastPacketCount == 1, "Three-frame repair burst targets only the missing block.");
            if (burst == 2) foreach (Packet packet in VisualProtocol.DecodePackets(frame)) receiver.Accept(packet, now + 100);
        }
        Check(receiver.Terminal && receiver.ReceivedBytes == command.FileSize, "Even losing the first two repair frames recovers the complete verified file.");
        using (Bitmap frame = prioritized.Next(retry, 30)) Check(!prioritized.LastWasRetransmission && prioritized.LastPacketIndex == 0, "A finite repair burst resumes the original sweep without resetting its cursor.");
        runner.Receive(receiver.Report(now + 100, 1920, 1080, count + 3));
        Check(runner.Finished && runner.Snapshot()[0].State == ProfileTestState.Passed, "Only the real full-byte completion receipt passes the benchmark.");
        Check(baseline >= 60 && baseline > 3 * 20, "The missing65/95 tail takes at least60 baseline frames versus3 repair frames.");
        Console.WriteLine("Synthetic MP4 packet65/95 repair: " + baseline + " baseline tail frames versus3 targeted frames (two repair-frame losses). This is not a Horizon throughput measurement.");
    }
    private static void FreshnessAndLease()
    {
        long now = 0; var runner = new FileBenchmarkRunner(Guid.NewGuid(), new[] { VideoProfile.Compact640 }, 10240, 7, 0, delegate { return now; });
        FileBenchmarkCommand command = runner.CurrentCommand; var ready = Report(command, 0, 0); ready.Status = FileBenchmarkStatus.Ready; runner.Receive(ready);
        var request = Report(command, 256, 1000); request.MissingSegment = 0; request.MissingIndices = new[] { 7, 9 }; now = 1000; runner.Receive(request);
        long revision = runner.CurrentCommand.RetransmitRevision; Check(revision == 1, "First fresh request gets its own repair revision.");
        request.MissingIndices[0] = 8; Check(runner.CurrentCommand.RetransmitIndices[0] == 7, "Incoming requests cannot mutate the stored repair queue.");
        FileBenchmarkCommand snapshot = runner.CurrentCommand; snapshot.RetransmitIndices[0] = 0;
        Check(runner.CurrentCommand.RetransmitIndices[0] == 7, "Outgoing command snapshots cannot mutate pending indices.");
        request.MissingIndices = new[] { 8 }; runner.Receive(request); Check(runner.CurrentCommand.RetransmitRevision == revision, "Replayed elapsed counters never restart a completed repair burst.");
        foreach (int mutation in new[] { 0, 1, 2, 3, 4 })
        {
            var bad = Report(command, 256, 2000); bad.MissingSegment = 0; bad.MissingIndices = new[] { 8 };
            if (mutation == 0) bad.Session = Guid.NewGuid(); if (mutation == 1) bad.Test = Guid.NewGuid(); if (mutation == 2) bad.Profile = VideoProfile.Dense;
            if (mutation == 3) bad.TotalBytes++; if (mutation == 4) bad.MissingIndices = new[] { 40 };
            runner.Receive(bad); Check(runner.CurrentCommand.RetransmitRevision == revision, "Foreign identity, substituted size or out-of-command index cannot schedule repairs.");
        }
        now += FileBenchmarkRunner.RetransmitLeaseMilliseconds + 1; Check(runner.CurrentCommand.RetransmitIndices == null, "A repair lease expires if current receiver feedback disappears.");
    }
    private static void InvalidDirectCompletion()
    {
        long now = 0; var runner = new FileBenchmarkRunner(Guid.NewGuid(), new[] { VideoProfile.Dense }, 1024, 7, 0, delegate { return now; });
        FileBenchmarkCommand command = runner.CurrentCommand;
        foreach (int mutation in new[] { 0, 1, 2, 3, 4 })
        {
            var report = Report(command, command.FileSize, 1000); report.Status = FileBenchmarkStatus.Completed;
            if (mutation == 0) report.Failure = FileBenchmarkFailure.DataMismatch;
            if (mutation == 1) report.FrameWidth = -1;
            if (mutation == 2) report.Frames = -1;
            if (mutation == 3) report.MissingSegment = 0;
            if (mutation == 4) report.ElapsedMilliseconds = -1;
            runner.Receive(report); Check(!runner.Finished && runner.Snapshot()[0].State != ProfileTestState.Passed, "Direct invalid completions cannot bypass the wire report invariants.");
        }
    }
    private static void LateCompletion()
    {
        long now = 0; var runner = new FileBenchmarkRunner(Guid.NewGuid(), new[] { VideoProfile.Dense, VideoProfile.Compact640 }, 1024, 7, 0, delegate { return now; });
        FileBenchmarkCommand old = runner.CurrentCommand; now = FileBenchmarkRunner.PrepareAnnouncementMilliseconds; runner.Tick();
        now += runner.NoReplyMilliseconds; runner.Tick(); FileBenchmarkCommand current = runner.CurrentCommand;
        Check(runner.Snapshot()[0].State == ProfileTestState.NoReply && runner.ReportCommands().Length == 2, "Recent timeout remains a bounded compact-report validation candidate.");
        FileBenchmarkCommand[] independent = runner.ReportCommands(); independent[0].Seed++;
        Check(runner.ReportCommands()[0].Seed == current.Seed, "Late/current command candidates are independent clones.");
        var complete = Report(old, old.FileSize, 1000); complete.Status = FileBenchmarkStatus.Completed;
        runner.Receive(complete); Check(runner.Snapshot()[0].State == ProfileTestState.Passed && runner.Snapshot()[0].ServerFinishReason == "late_verified_complete" && runner.CurrentCommand.Test == current.Test,
            "A valid delayed completion repairs only its old timeout result without advancing or resetting the current test.");
        runner.Receive(complete); Check(runner.CurrentCommand.Test == current.Test, "Duplicate old completion cannot advance again.");
        now = 0; var expired = new FileBenchmarkRunner(old.Session, new[] { old.Profile }, 1024, 7, 0, delegate { return now; }); old = expired.CurrentCommand;
        now = FileBenchmarkRunner.PrepareAnnouncementMilliseconds; expired.Tick(); now += expired.NoReplyMilliseconds; expired.Tick(); now += FileBenchmarkRunner.LateCompletionGraceMilliseconds + 1;
        complete = Report(old, old.FileSize, 1000); complete.Status = FileBenchmarkStatus.Completed; expired.Receive(complete);
        Check(expired.Snapshot()[0].State == ProfileTestState.NoReply && expired.ReportCommands().Length == 0, "Late validation expires after15seconds and never invents success.");
        now = 0; var cancelled = new FileBenchmarkRunner(Guid.NewGuid(), new[] { old.Profile }, 1024, 7, 0, delegate { return now; }); old = cancelled.CurrentCommand; cancelled.Cancel();
        complete = Report(old, old.FileSize, 1000); complete.Status = FileBenchmarkStatus.Completed; cancelled.Receive(complete);
        Check(cancelled.Snapshot()[0].State == ProfileTestState.Cancelled && cancelled.ReportCommands().Length == 0, "User cancellation cannot be revived by an old completion.");
    }
    private static void Reject(Action operation, string reason) { try { operation(); } catch (ArgumentException) { checks++; return; } throw new Exception(reason); }
    private static void Finish(byte[] wire)
    {
        uint crc = UInt32.MaxValue;
        for (int i = 0; i < wire.Length - 4; i++) { crc ^= wire[i]; for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320U; }
        crc = ~crc; for (int i = 0; i < 4; i++) wire[wire.Length - 4 + i] = (byte)(crc >> (i * 8));
    }
}
