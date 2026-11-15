using System;
using System.Collections.Generic;
using System.Drawing;
using HorizonClient;

internal static class FileBenchmarkRunnerTests
{
    private static int checks;
    public static int Main()
    {
        try
        {
            HandshakeAndMatching(); CompletionAndStaleReplies(); NoReadyAndTail(); NoReplyAllowsFullSourcePass(); DataFlowsWithoutReverseAudio(); ImplicitReadyAndLateReady(); NoDataAndStall(); MaximumAndUnlimited(); CancelAndCopies();
            SourceCoverageAndRotation(); SourceMultipleSegments(); CompatibleRepetition(); FullReceiverIntegration(); HundredMiBSegments(); InvalidConfiguration();
            StrictFormatPreflight(); StrictFormatMismatchAndTimeout(); StrictFormatChangesAndLateReplies();
            Console.WriteLine("File benchmark runner checks passed ({0} assertions; injected clocks, synthetic bitmaps and bytes only).", checks); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void HandshakeAndMatching()
    {
        var f = new Fixture(new[] { VideoProfile.Compact640 }, 10240, 0);
        FileBenchmarkCommand command = f.Run.CurrentCommand;
        Check(command.Phase == FileBenchmarkPhase.Prepare && f.Run.Snapshot()[0].State == ProfileTestState.Preparing, "A bounded preparation window announces the test before video transmission");
        foreach (int mismatch in new[] { 0, 1, 2, 3 })
        {
            FileBenchmarkReport ready = Report(command, FileBenchmarkStatus.Ready, 0, 0);
            if (mismatch == 0) ready.Session = Guid.NewGuid(); if (mismatch == 1) ready.Test = Guid.NewGuid();
            if (mismatch == 2) ready.Profile = VideoProfile.Fast; if (mismatch == 3) ready.TotalBytes++;
            f.Run.Receive(ready); Check(f.Run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare, "Session, test, profile and full file size all bind Ready");
        }
        f.Ready(); Check(f.Run.CurrentCommand.Phase == FileBenchmarkPhase.Start && f.Run.Snapshot()[0].State == ProfileTestState.Receiving, "Matching Ready starts transfer");
        f.Clock += 1000; f.Run.Receive(Report(command, FileBenchmarkStatus.Progress, 1024, 1000));
        Check(f.Run.Snapshot()[0].ReceivedBytes == 1024, "Only verified unique receive progress is recorded");
        f.Run.Receive(Report(command, FileBenchmarkStatus.Progress, 512, 2000));
        Check(f.Run.Snapshot()[0].ReceivedBytes == 1024, "Older progress cannot reduce covered bytes");
        f.Run.Receive(Report(command, FileBenchmarkStatus.Progress, 1024, 500));
        Check(f.Run.Snapshot()[0].ElapsedMilliseconds == 1000, "Replayed equal-byte progress cannot shorten elapsed time or inflate throughput");
        f.Run.Receive(Report(command, FileBenchmarkStatus.Completed, 2048, 2000));
        Check(!f.Run.Finished && f.Run.Snapshot()[0].ReceivedBytes == 1024 && f.Run.Snapshot()[0].State == ProfileTestState.Receiving, "Partial Completed is ignored completely");
    }

    private static void CompletionAndStaleReplies()
    {
        var f = new Fixture(new[] { VideoProfile.Compatible, VideoProfile.Compact640 }, 10240, 0); f.Ready();
        FileBenchmarkCommand old = f.Run.CurrentCommand; f.Clock += 12345;
        FileBenchmarkReport complete = Report(old, FileBenchmarkStatus.Completed, old.FileSize, 12345); complete.FrameWidth = 320; complete.FrameHeight = 240; complete.Frames = 31;
        f.Run.Receive(complete);
        Check(f.Run.Snapshot()[0].State == ProfileTestState.Passed && f.Run.Snapshot()[0].ReceivedBytes == old.FileSize, "Only acknowledgement of every logical file byte passes a profile");
        Check(f.Run.Snapshot()[0].Width == 320 && f.Run.Snapshot()[0].Height == 240 && f.Run.Snapshot()[0].Frames == 31, "Actual delivered geometry and frame count survive completion");
        Check(f.Run.CurrentIndex == 1 && f.Run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare && f.Run.CurrentCommand.Test != old.Test, "Next profile gets its own bounded preparation window and test identity");
        f.Run.Receive(complete); f.Run.Receive(Report(old, FileBenchmarkStatus.Ready, 0, 0));
        Check(f.Run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare && f.Run.Snapshot()[1].ReceivedBytes == 0, "Late acknowledgements from the previous profile are ignored");
        f.Ready(); FileBenchmarkCommand next = f.Run.CurrentCommand; f.Clock += 1500;
        f.Run.Receive(Report(next, FileBenchmarkStatus.Completed, next.FileSize, 1500));
        Check(f.Run.Finished && f.Run.CurrentCommand.Phase == FileBenchmarkPhase.Cancel, "Completed sweep sends cancellation during its final tail");
        Check(f.Run.Snapshot()[1].MegabitsPerSecond == next.FileSize * 8.0 / 1500 / 1000, "Goodput uses complete verified file bytes and the reported elapsed duration");
    }

    private static void NoReadyAndTail()
    {
        var f = new Fixture(new[] { VideoProfile.Compatible, VideoProfile.Dense }, 10240, 0);
        f.Clock += FileBenchmarkRunner.PrepareAnnouncementMilliseconds - 1; f.Run.Tick();
        Check(f.Run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare, "Preparation announcements keep their full bounded interval");
        f.Clock++; f.Run.Tick();
        Check(f.Run.CurrentCommand.Phase == FileBenchmarkPhase.Start && !f.Run.Finished, "Video starts after preparation even with no reverse-audio Ready");
        f.Clock += FileBenchmarkRunner.ReadyTimeoutMilliseconds - 1; f.Run.Tick();
        Check(f.Run.CurrentIndex == 0 && !f.Run.Finished, "A profile transmits data for the full reply-observation interval");
        f.Clock++; f.Run.Tick();
        Check(!f.Run.Finished && f.Run.Snapshot()[0].State == ProfileTestState.NoReply && f.Run.Snapshot()[0].ServerFinishReason == "receiver_reply_unavailable" &&
            f.Run.Snapshot()[1].State == ProfileTestState.Preparing, "Missing audio is NoReply, and the next profile still gets tested");
        f.Clock += FileBenchmarkRunner.PrepareAnnouncementMilliseconds; f.Run.Tick();
        Check(f.Run.CurrentCommand.Phase == FileBenchmarkPhase.Start, "The second profile also starts video without reverse audio");
        f.Clock += FileBenchmarkRunner.ReadyTimeoutMilliseconds; f.Run.Tick();
        Check(f.Run.Finished && f.Run.Snapshot()[1].State == ProfileTestState.NoReply, "Every no-audio profile completes with an honest unverified result");
        Check(!f.Run.TailComplete, "Cancellation tail starts at terminal state");
        f.Clock += 2999; Check(!f.Run.TailComplete, "Cancellation tail lasts the full three seconds"); f.Clock++; Check(f.Run.TailComplete, "Tail completes after three seconds");
        long elapsed = f.Run.TotalMilliseconds; f.Clock += 5000; Check(f.Run.TotalMilliseconds == elapsed, "Final elapsed duration excludes the cancellation tail");
    }

    private static void DataFlowsWithoutReverseAudio()
    {
        var f = new Fixture(new[] { VideoProfile.Compatible }, 1024, 0);
        FileBenchmarkCommand prepared = f.Run.CurrentCommand;
        var receiver = new FileBenchmarkReceiver(prepared);
        var source = new FileBenchmarkVideoSource();
        f.Clock += FileBenchmarkRunner.PrepareAnnouncementMilliseconds;
        FileBenchmarkCommand transmitting = f.Run.CurrentCommand;
        Check(transmitting.Phase == FileBenchmarkPhase.Start && transmitting.Test == prepared.Test, "Automatic Start preserves the announced transfer identity");
        receiver.Start(f.Clock);
        int decodedPackets = 0;
        for (int frame = 0; frame < 30 && !receiver.Terminal; frame++)
        {
            using (Bitmap bitmap = source.Next(transmitting, 30))
            {
                f.Clock += 33;
                foreach (Packet packet in VisualProtocol.DecodePackets(bitmap)) { decodedPackets++; receiver.Accept(packet, f.Clock); }
            }
            // Deliberately withhold every Ready/Progress/Completed audio report.
            f.Run.Tick();
        }
        Check(decodedPackets >= 4 && receiver.Status == FileBenchmarkStatus.Completed && receiver.ReceivedBytes == 1024,
            "Without any audio, actual generated video packets still transfer and verify the complete synthetic file");
        Check(!f.Run.Finished && f.Run.Snapshot()[0].ReceivedBytes == 0 && f.Run.Snapshot()[0].State == ProfileTestState.Receiving,
            "Video transmission alone never fabricates a remote confirmation or reported useful bytes");
        f.Run.Receive(receiver.Report(f.Clock, 1280, 720, decodedPackets));
        Check(f.Run.Finished && f.Run.Snapshot()[0].State == ProfileTestState.Passed && f.Run.Snapshot()[0].ReceivedBytes == 1024,
            "A real matching Completed report is sufficient even when all prior Ready/Progress reports were lost");
    }

    private static void NoReplyAllowsFullSourcePass()
    {
        var full = new Fixture(new[] { VideoProfile.Compatible }, 102400, 0);
        Check(full.Run.NoReplyMilliseconds >= 110000 && full.Run.NoReplyMilliseconds <= 120000,
            "100KiB Compatible allows its full 400-block source pass plus control/pacing/audio margin");
        full.Clock += FileBenchmarkRunner.PrepareAnnouncementMilliseconds; full.Run.Tick();
        full.Clock += 20000; full.Run.Tick();
        Check(!full.Run.Finished && full.Run.Snapshot()[0].State == ProfileTestState.Receiving && full.Run.Snapshot()[0].ReceivedBytes == 0,
            "A silent reverse-audio channel must not cut off a large-file source after only twenty seconds");
        full.Clock += full.Run.NoReplyMilliseconds - 20001; full.Run.Tick();
        Check(!full.Run.Finished, "Unconfirmed large-file transfer keeps its complete estimated pass budget");
        full.Clock++; full.Run.Tick();
        Check(full.Run.Finished && full.Run.Snapshot()[0].State == ProfileTestState.NoReply,
            "One full pass without audio remains unconfirmed and never fabricates successful delivery");

        var small = new Fixture(new[] { VideoProfile.Compatible }, 1024, 0);
        Check(small.Run.NoReplyMilliseconds == 35000, "Small files allow the fifteen-second remote no-data deadline plus reverse-audio delivery grace");
        var limited = new Fixture(new[] { VideoProfile.Compatible }, 102400, 30000);
        Check(limited.Run.NoReplyMilliseconds == 45000, "An explicit maximum bounds no-reply transmission with fifteen-second acknowledgement grace");
        limited.Clock += FileBenchmarkRunner.PrepareAnnouncementMilliseconds; limited.Run.Tick();
        limited.Clock += 44999; limited.Run.Tick(); Check(!limited.Run.Finished, "Configured maximum does not expire before its acknowledgement grace");
        limited.Clock++; limited.Run.Tick();
        Check(limited.Run.Finished && limited.Run.Snapshot()[0].State == ProfileTestState.NoReply, "Configured maximum still ends an unconfirmed large-file profile");
    }

    private static void ImplicitReadyAndLateReady()
    {
        var implicitReady = new Fixture(new[] { VideoProfile.Compatible }, 10240, 0);
        FileBenchmarkCommand command = implicitReady.Run.CurrentCommand;
        implicitReady.Run.Receive(Report(command, FileBenchmarkStatus.Completed, 256, 100));
        Check(implicitReady.Run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare, "An incomplete Completed cannot trigger implicit readiness");
        var foreign = Report(command, FileBenchmarkStatus.Progress, 256, 100); foreign.Test = Guid.NewGuid();
        implicitReady.Run.Receive(foreign);
        Check(implicitReady.Run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare, "A foreign Progress cannot trigger implicit readiness");
        implicitReady.Run.Receive(Report(command, FileBenchmarkStatus.Progress, 256, 100));
        Check(implicitReady.Run.CurrentCommand.Phase == FileBenchmarkPhase.Start && implicitReady.Run.Snapshot()[0].ReceivedBytes == 256,
            "A matching verified Progress report replaces a lost Ready without losing useful-byte counters");

        var late = new Fixture(new[] { VideoProfile.Compatible }, 10240, 0);
        late.Clock += FileBenchmarkRunner.PrepareAnnouncementMilliseconds; command = late.Run.CurrentCommand;
        late.Clock += 19000; late.Run.Receive(Report(command, FileBenchmarkStatus.Ready, 0, 0));
        late.Clock += FileBenchmarkRunner.ReportSilenceGraceMilliseconds - 1; late.Run.Tick();
        Check(!late.Run.Finished, "The first late Ready gives the actual receiver a full no-data interval");
        late.Run.Receive(Report(command, FileBenchmarkStatus.Ready, 0, 0)); late.Clock++; late.Run.Tick();
        Check(late.Run.Finished && late.Run.Snapshot()[0].State == ProfileTestState.NoData,
            "Repeated Ready never keeps a no-data transfer alive indefinitely; confirmed readiness distinguishes NoData from NoReply");
    }

    private static void NoDataAndStall()
    {
        var empty = new Fixture(new[] { VideoProfile.Compact320 }, 10240, 0); empty.Ready();
        empty.Clock += FileBenchmarkRunner.ReportSilenceGraceMilliseconds - 1; empty.Run.Tick(); Check(!empty.Run.Finished, "NoData watchdog includes report delivery grace");
        empty.Clock++; empty.Run.Tick(); Check(empty.Run.Finished && empty.Run.Snapshot()[0].State == ProfileTestState.NoData, "Ready without delivered bytes is classified as NoData");
        var partial = new Fixture(new[] { VideoProfile.Compact640, VideoProfile.Compatible }, 10240, 0); partial.Ready();
        partial.Clock += 1000; FileBenchmarkCommand command = partial.Run.CurrentCommand;
        partial.Run.Receive(Report(command, FileBenchmarkStatus.Progress, 256, 1000));
        partial.Clock += 19000; partial.Run.Receive(Report(command, FileBenchmarkStatus.Progress, 256, 20000)); partial.Run.Tick();
        Check(partial.Run.Snapshot()[0].State == ProfileTestState.Receiving, "Fresh receiver heartbeat keeps a retry loop alive without adding unique bytes");
        partial.Clock += 1000; partial.Run.Tick();
        Check(partial.Run.Snapshot()[0].State == ProfileTestState.Receiving, "Unique-byte inactivity alone cannot end an active retry round");
        partial.Clock += FileBenchmarkRunner.ReportSilenceGraceMilliseconds - 1000; partial.Run.Tick();
        Check(partial.Run.Snapshot()[0].State == ProfileTestState.Partial && partial.Run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare, "Loss of receiver heartbeats is Partial and the next mode gets its own Ready handshake");
    }

    private static void MaximumAndUnlimited()
    {
        var limited = new Fixture(new[] { VideoProfile.Compact640 }, 10240, 5000); limited.Ready();
        FileBenchmarkCommand command = limited.Run.CurrentCommand;
        for (int i = 1; i <= 4; i++) { limited.Clock += 2000; limited.Run.Receive(Report(command, FileBenchmarkStatus.Progress, i * 256, i * 2000)); limited.Run.Tick(); }
        Check(!limited.Run.Finished, "Progress remains active before maximum time plus acknowledgement grace");
        limited.Clock += 12000; limited.Run.Tick(); Check(limited.Run.Finished && limited.Run.Snapshot()[0].State == ProfileTestState.Partial, "Maximum run bounds a profile despite continued earlier progress");
        var unlimited = new Fixture(new[] { VideoProfile.Compact640 }, 10240, 0); unlimited.Ready(); command = unlimited.Run.CurrentCommand;
        for (int i = 1; i <= 30; i++) { unlimited.Clock += 5000; unlimited.Run.Receive(Report(command, FileBenchmarkStatus.Progress, i * 256, i * 5000)); unlimited.Run.Tick(); }
        Check(!unlimited.Run.Finished && unlimited.Run.CurrentCommand.MaximumRunMilliseconds == 0, "Unlimited has no hidden total-time deadline while unique bytes keep arriving");
        unlimited.Clock += FileBenchmarkRunner.ReportSilenceGraceMilliseconds; unlimited.Run.Tick(); Check(unlimited.Run.Finished && unlimited.Run.Snapshot()[0].State == ProfileTestState.Partial, "Unlimited still detects a genuinely stalled transfer");
    }

    private static void CancelAndCopies()
    {
        var f = new Fixture(new[] { VideoProfile.Compatible, VideoProfile.Dense }, 10240, 0); f.Ready();
        FileBenchmarkCommand command = f.Run.CurrentCommand; command.Test = Guid.Empty;
        ProfileTestResult[] snapshot = f.Run.Snapshot(); snapshot[0].State = ProfileTestState.Passed;
        Check(f.Run.CurrentCommand.Test != Guid.Empty && f.Run.Snapshot()[0].State == ProfileTestState.Receiving, "Snapshots and commands do not expose mutable internal state");
        f.Run.Cancel(); f.Run.Cancel(); Check(f.Run.Finished && f.Run.Snapshot()[0].State == ProfileTestState.Cancelled && f.Run.Snapshot()[1].State == ProfileTestState.Cancelled, "Cancellation is idempotent and covers remaining profiles");
        f.Clock += 3000; Check(f.Run.TailComplete, "Manual cancellation has the same complete three-second tail");
    }

    private static void SourceCoverageAndRotation()
    {
        foreach (int droppedParity in new[] { 0, 1 })
        {
        FileBenchmarkCommand command = Command(VideoProfile.Compact640, 10240); var source = new FileBenchmarkVideoSource(); var seen = new HashSet<int>(); var assembler = new TransferAssembler(); byte[] complete = null;
        for (int frameIndex = 0; frameIndex < 48; frameIndex++)
        using (Bitmap frame = source.Next(command, 30))
        {
            Packet[] packets = VisualProtocol.DecodePackets(frame);
            Check(packets.Length >= 1 && packets.Length <= 7, "Compact source emits only its bounded per-frame batch");
            foreach (Packet packet in packets)
            {
                Check(packet.Profile == command.Profile && packet.Count == 40 && packet.TotalLength == 10240 && packet.ChunkSize == 256, "Source metadata matches the segment exactly");
                int segment; Check(FileBenchmarkProtocol.TrySegmentIndex(command.Test, packet.TransferId, out segment) && segment == 0, "Source packets belong to their reserved segment identity");
                Check(BenchmarkData.Verify(command.Seed, (long)packet.Index * 256, packet.Data), "Every generated packet matches its exact deterministic file offset");
                // Regular frame dropping must be recovered by rotating later rounds.
                if ((frameIndex & 1) != droppedParity) { seen.Add(packet.Index); byte[] result = assembler.Add(packet); if (result != null) complete = result; }
            }
        }
        Check(seen.Count == 40 && complete != null && BenchmarkData.Verify(command.Seed, 0, complete), "All bytes recover across cyclic offset wrap despite dropping every other frame");
        command.Test = Guid.NewGuid(); command.Seed++;
        using (Bitmap frame = source.Next(command, 30)) Check(VisualProtocol.DecodePackets(frame)[0].Index == 0, "A fresh test starts at the first packet with fresh deterministic content");
        command.Phase = FileBenchmarkPhase.Prepare;
        Throws<InvalidOperationException>(delegate { source.Next(command, 30); }, "Source refuses to send before Ready/Start");
        }
    }

    private static void CompatibleRepetition()
    {
        FileBenchmarkCommand command = Command(VideoProfile.Compatible, 10240); var source = new FileBenchmarkVideoSource();
        for (int i = 0; i < 12; i++) using (Bitmap frame = source.Next(command, 30))
        {
            Packet[] packets = VisualProtocol.DecodePackets(frame);
            Check(packets.Length == 1 && packets[0].Index == i / 6, "Compatible repeats the same block for six 30 FPS frames, matching normal transfer");
            Check(BenchmarkData.Verify(command.Seed, (long)packets[0].Index * 256, packets[0].Data), "Repeated Compatible frames retain correct synthetic bytes");
        }
    }

    private static void SourceMultipleSegments()
    {
        FileBenchmarkCommand command = Command(VideoProfile.Compact640, 10240); command.SegmentSize = 4096;
        var source = new FileBenchmarkVideoSource(); var receiver = new FileBenchmarkReceiver(command); receiver.Start(0);
        var seenSegments = new HashSet<int>();
        for (int i = 0; i < 120 && !receiver.Terminal; i++) using (Bitmap frame = source.Next(command, 30))
        {
            if ((i & 1) == 0) continue;
            foreach (Packet packet in VisualProtocol.DecodePackets(frame))
            {
                int segment; Check(FileBenchmarkProtocol.TrySegmentIndex(command.Test, packet.TransferId, out segment), "Segmented source uses reserved segment identities");
                Check(packet.TotalLength == FileBenchmarkProtocol.SegmentLength(command, segment), "Short final segment advertises its actual length");
                Check(BenchmarkData.Verify(command.Seed, (long)segment * command.SegmentSize + packet.Index * 256L, packet.Data), "Segmented source preserves global file offsets");
                seenSegments.Add(segment); receiver.Accept(packet, i * 33L);
            }
        }
        Check(seenSegments.Count == 3 && receiver.Status == FileBenchmarkStatus.Completed && receiver.ReceivedBytes == 10240, "Different segment sizes and offset wrap reconstruct the complete logical file under frame loss");
    }

    private static void FullReceiverIntegration()
    {
        var f = new Fixture(new[] { VideoProfile.Compact640 }, 10240, 0);
        var receiver = new FileBenchmarkReceiver(f.Run.CurrentCommand); var source = new FileBenchmarkVideoSource();
        f.Run.Receive(receiver.Report(f.Clock, 320, 240, 0)); FileBenchmarkCommand command = f.Run.CurrentCommand;
        receiver.Start(f.Clock);
        int frames = 0;
        while (!receiver.Terminal && frames < 100)
        {
            using (Bitmap frame = source.Next(command, 30))
            {
                f.Clock += 33; frames++;
                if ((frames & 1) != 0) foreach (Packet packet in VisualProtocol.DecodePackets(frame)) receiver.Accept(packet, f.Clock);
            }
            f.Run.Receive(receiver.Report(f.Clock, 320, 240, frames));
        }
        Check(receiver.Status == FileBenchmarkStatus.Completed && receiver.ReceivedBytes == 10240, "Receiver verifies a complete file under periodic frame loss: status=" + receiver.Status + ", failure=" + receiver.Failure + ", bytes=" + receiver.ReceivedBytes + ", frames=" + frames);
        Check(f.Run.Finished && f.Run.Snapshot()[0].State == ProfileTestState.Passed && f.Run.Snapshot()[0].ReceivedBytes == 10240, "Only the receiver's full verified completion passes the runner");
    }

    private static void HundredMiBSegments()
    {
        const long bytes = 100L * 1024 * 1024; var f = new Fixture(new[] { VideoProfile.Compatible }, bytes, 0); FileBenchmarkCommand command = f.Run.CurrentCommand;
        Check(FileBenchmarkProtocol.SegmentCount(command) == 4, "A 100 MiB file uses four bounded transfers");
        long sum = 0;
        for (int i = 0; i < 4; i++) { int length = FileBenchmarkProtocol.SegmentLength(command, i); Check(length <= VisualProtocol.MaxTransferBytes && length == (i < 3 ? 32 * 1024 * 1024 : 4 * 1024 * 1024), "Every segment stays within the existing transfer limit"); sum += length; }
        Check(sum == bytes, "All segment lengths exactly cover 100 MiB without allocating that file");
        f.Ready(); f.Clock += 10000; f.Run.Receive(Report(f.Run.CurrentCommand, FileBenchmarkStatus.Completed, bytes, 10000));
        Check(f.Run.Finished && f.Run.Snapshot()[0].State == ProfileTestState.Passed && f.Run.Snapshot()[0].ReceivedBytes == bytes, "Runner requires and preserves completion of the entire 100 MiB logical file");
    }

    private static void InvalidConfiguration()
    {
        Throws<ArgumentException>(delegate { new FileBenchmarkRunner(Guid.Empty, new[] { VideoProfile.Compatible }, 1024, 1, 0); }, "Session required");
        Throws<ArgumentException>(delegate { new FileBenchmarkRunner(Guid.NewGuid(), new VideoProfile[0], 1024, 1, 0); }, "Profiles required");
        Throws<ArgumentException>(delegate { new FileBenchmarkRunner(Guid.NewGuid(), new[] { (VideoProfile)99 }, 1024, 1, 0); }, "Known profile required");
        Throws<ArgumentException>(delegate { new FileBenchmarkRunner(Guid.NewGuid(), new[] { VideoProfile.Compatible }, 1023, 1, 0); }, "Minimum file size enforced");
        Throws<ArgumentException>(delegate { new FileBenchmarkRunner(Guid.NewGuid(), new[] { VideoProfile.Compatible }, 100L * 1024 * 1024 + 1, 1, 0); }, "Maximum file size enforced");
        Throws<ArgumentException>(delegate { new FileBenchmarkRunner(Guid.NewGuid(), new[] { VideoProfile.Compatible }, 1024, 1, 1); }, "Maximum runtime must fit the wire protocol's minimum");
        Throws<ArgumentException>(delegate { new FileBenchmarkRunner(Guid.NewGuid(), new[] { VideoProfile.Compatible }, 1024, 1, Int64.MaxValue); }, "Unbounded overflowing runtime is rejected");
        foreach (int[] size in new[] { new[] { 0, 720 }, new[] { 1280, 0 }, new[] { 0, 0 }, new[] { -1, 720 }, new[] { 7681, 720 }, new[] { 1280, 4321 } })
            Throws<ArgumentException>(delegate { new FileBenchmarkRunner(Guid.NewGuid(), new[] { VideoProfile.Fast }, 1024, 1, 0, size[0], size[1]); }, "Strict format requires a positive supported geometry");
    }

    private static void StrictFormatPreflight()
    {
        var f = new Fixture(new[] { VideoProfile.Fast, VideoProfile.Dense }, 10240, 0, 1920, 1080);
        var source = new FileBenchmarkVideoSource(); FileBenchmarkCommand first = f.Run.CurrentCommand;
        f.Clock += FileBenchmarkRunner.PrepareAnnouncementMilliseconds * 2; f.Run.Tick();
        Check(f.Run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare && !f.Run.Finished, "Strict preflight never starts on the optimistic announcement timer");
        Throws<InvalidOperationException>(delegate { source.Next(f.Run.CurrentCommand, 30); }, "Strict preflight cannot generate any test-file frame before Ready");
        foreach (int mismatch in new[] { 0, 1, 2, 3 })
        {
            FileBenchmarkReport foreign = Geometry(Report(first, FileBenchmarkStatus.Ready, 0, 0), 1280, 720);
            if (mismatch == 0) foreign.Session = Guid.NewGuid(); if (mismatch == 1) foreign.Test = Guid.NewGuid();
            if (mismatch == 2) foreign.Profile = VideoProfile.Dense; if (mismatch == 3) foreign.TotalBytes++;
            f.Run.Receive(foreign);
            Check(!f.Run.Finished && f.Run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare, "Foreign Ready geometry cannot start or cancel the strict run");
        }
        foreach (int[] dimensions in new[] { new[] { 0, 0 }, new[] { 1920, 0 }, new[] { 0, 1080 } })
        {
            f.Run.Receive(Geometry(Report(first, FileBenchmarkStatus.Ready, 0, 0), dimensions[0], dimensions[1]));
            Check(f.Run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare && f.Run.ActualWidth == 0, "Unknown or partial geometry cannot satisfy preflight");
        }
        f.Run.Receive(Geometry(Report(first, FileBenchmarkStatus.Progress, 256, 100), 1920, 1080));
        f.Run.Receive(Geometry(Report(first, FileBenchmarkStatus.Completed, first.FileSize, 100), 1920, 1080));
        Check(f.Run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare && f.Run.Snapshot()[0].ReceivedBytes == 0,
            "Even a matching full Completed cannot replace the mandatory strict Ready");
        FileBenchmarkReport parsed;
        Check(FileBenchmarkProtocol.TryReport(FileBenchmarkProtocol.Report(Geometry(Report(first, FileBenchmarkStatus.Ready, 0, 0), 1920, 1080)), first.Session, out parsed),
            "Strict Ready uses the same validated CRC report path");
        f.Run.Receive(parsed);
        Check(f.Run.CurrentCommand.Phase == FileBenchmarkPhase.Start && f.Run.ActualWidth == 1920 && f.Run.ActualHeight == 1080 &&
            f.Run.ExpectedWidth == 1920 && f.Run.ExpectedHeight == 1080 && f.Run.FormatCheckFailureReason == null,
            "Only exact positive actual capture geometry starts the test");
        using (Bitmap frame = source.Next(f.Run.CurrentCommand, 30)) Check(VisualProtocol.DecodePackets(frame).Length > 0, "Validated strict Ready unlocks actual synthetic file data");
        f.Clock += 1000;
        f.Run.Receive(Geometry(Report(first, FileBenchmarkStatus.Completed, first.FileSize, 1000), 1920, 1080));
        FileBenchmarkCommand second = f.Run.CurrentCommand;
        f.Clock += FileBenchmarkRunner.PrepareAnnouncementMilliseconds * 2; f.Run.Tick();
        f.Run.Receive(parsed);
        Check(f.Run.Snapshot()[0].State == ProfileTestState.Passed && second.Test != first.Test && f.Run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare,
            "Every profile requires its own fresh Ready and ignores the previous test's confirmation");
        f.Run.Receive(Geometry(Report(second, FileBenchmarkStatus.Ready, 0, 0), 1920, 1080));
        Check(f.Run.CurrentCommand.Phase == FileBenchmarkPhase.Start, "A fresh exact Ready unlocks the next profile");
    }

    private static void StrictFormatMismatchAndTimeout()
    {
        var mismatch = new Fixture(new[] { VideoProfile.Fast, VideoProfile.Dense }, 102400, 0, 1920, 1080);
        FileBenchmarkCommand command = mismatch.Run.CurrentCommand;
        mismatch.Run.Receive(Geometry(Report(command, FileBenchmarkStatus.Ready, 0, 0), 1280, 720));
        Check(mismatch.Run.Finished && mismatch.Run.CurrentCommand.Phase == FileBenchmarkPhase.Cancel && mismatch.Run.FormatCheckFailureReason == "capture_format_mismatch" &&
            mismatch.Run.ExpectedWidth == 1920 && mismatch.Run.ExpectedHeight == 1080 && mismatch.Run.ActualWidth == 1280 && mismatch.Run.ActualHeight == 720,
            "A positive mismatched Ready cancels the entire run and exposes expected/actual geometry");
        foreach (ProfileTestResult result in mismatch.Run.Snapshot())
            Check(result.State == ProfileTestState.Cancelled && result.ReceivedBytes == 0 && result.ServerFinishReason == "capture_format_mismatch",
                "No profile sends or claims file bytes after failed preflight");
        Check(mismatch.Run.Snapshot()[0].ReceiverConfirmed && mismatch.Run.Snapshot()[0].Width == 1280 && mismatch.Run.Snapshot()[0].Height == 720,
            "The cancellation preserves the actual geometry supplied by the confirmed receiver");
        var source = new FileBenchmarkVideoSource();
        Throws<InvalidOperationException>(delegate { source.Next(mismatch.Run.CurrentCommand, 30); }, "Rejected geometry cannot generate test-file data");
        mismatch.Run.Receive(Geometry(Report(command, FileBenchmarkStatus.Completed, command.FileSize, 1000), 1920, 1080));
        Check(mismatch.Run.Snapshot()[0].State == ProfileTestState.Cancelled, "Late full completion cannot undo a strict format cancellation");

        var timeout = new Fixture(new[] { VideoProfile.Fast, VideoProfile.Dense }, 102400, 0, 1920, 1080);
        FileBenchmarkCommand waiting = timeout.Run.CurrentCommand;
        timeout.Clock += FileBenchmarkRunner.ReadyTimeoutMilliseconds - 1;
        Check(timeout.Run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare && !timeout.Run.Finished, "Strict preflight allows its complete thirty-five-second confirmation window");
        timeout.Clock++; timeout.Run.Tick();
        Check(timeout.Run.Finished && timeout.Run.FormatCheckFailureReason == "capture_format_unconfirmed" && timeout.Run.ActualWidth == 0 && timeout.Run.ActualHeight == 0,
            "No Ready cancels the whole run with a distinct unconfirmed-format reason");
        foreach (ProfileTestResult result in timeout.Run.Snapshot()) Check(result.State == ProfileTestState.Cancelled && result.ReceivedBytes == 0, "A preflight timeout sends zero test-file bytes for all profiles");
        timeout.Run.Receive(Geometry(Report(waiting, FileBenchmarkStatus.Ready, 0, 0), 1920, 1080));
        Check(timeout.Run.CurrentCommand.Phase == FileBenchmarkPhase.Cancel, "Ready arriving after a preflight deadline cannot restart the canceled run");
        var late = new Fixture(new[] { VideoProfile.Fast }, 10240, 0, 1920, 1080);
        FileBenchmarkCommand lateCommand = late.Run.CurrentCommand; late.Clock += FileBenchmarkRunner.ReadyTimeoutMilliseconds;
        late.Run.Receive(Geometry(Report(lateCommand, FileBenchmarkStatus.Ready, 0, 0), 1920, 1080));
        Check(late.Run.Finished && late.Run.FormatCheckFailureReason == "capture_format_unconfirmed", "Receive enforces the preparation deadline even before the next timer tick");
    }

    private static void StrictFormatChangesAndLateReplies()
    {
        var f = new Fixture(new[] { VideoProfile.Fast, VideoProfile.Dense }, 10240, 0, 1280, 720);
        FileBenchmarkCommand command = f.Run.CurrentCommand;
        f.Run.Receive(Geometry(Report(command, FileBenchmarkStatus.Ready, 0, 0), 1280, 720));
        f.Run.Receive(Geometry(Report(command, FileBenchmarkStatus.Progress, 256, 100), 1280, 720));
        f.Run.Receive(Geometry(Report(command, FileBenchmarkStatus.Ready, 0, 0), 320, 240));
        f.Run.Receive(Geometry(Report(command, FileBenchmarkStatus.Progress, 512, 99), 320, 240));
        f.Run.Receive(Geometry(Report(command, FileBenchmarkStatus.Completed, command.FileSize, 99), 320, 240));
        f.Run.Receive(Geometry(Report(command, FileBenchmarkStatus.Progress, 512, 150), 0, 0));
        FileBenchmarkReport foreign = Geometry(Report(command, FileBenchmarkStatus.Progress, 512, 200), 320, 240); foreign.Test = Guid.NewGuid(); f.Run.Receive(foreign);
        Check(!f.Run.Finished && f.Run.Snapshot()[0].ReceivedBytes == 256 && f.Run.ActualWidth == 1280 && f.Run.ActualHeight == 720,
            "Repeated Ready, stale geometry, zero geometry and foreign progress cannot abort or update current capture state");
        f.Run.Receive(Geometry(Report(command, FileBenchmarkStatus.Progress, 512, 200), 320, 240));
        Check(f.Run.Finished && f.Run.FormatCheckFailureReason == "capture_format_mismatch" && f.Run.ActualWidth == 320 && f.Run.ActualHeight == 240 &&
            f.Run.Snapshot()[0].ReceivedBytes == 256 && f.Run.Snapshot()[1].State == ProfileTestState.Cancelled,
            "A fresh newer matching progress report aborts the whole run when capture geometry changes");
        var completed = new Fixture(new[] { VideoProfile.Fast }, 10240, 0, 1280, 720); command = completed.Run.CurrentCommand;
        completed.Run.Receive(Geometry(Report(command, FileBenchmarkStatus.Ready, 0, 0), 1280, 720));
        completed.Run.Receive(Geometry(Report(command, FileBenchmarkStatus.Completed, command.FileSize, 100), 320, 240));
        Check(completed.Run.Finished && completed.Run.Snapshot()[0].State == ProfileTestState.Cancelled, "A newer wrong-format Completed cannot pass a strict test");
        var late = new Fixture(new[] { VideoProfile.Fast, VideoProfile.Dense }, 10240, 0, 1280, 720); command = late.Run.CurrentCommand;
        late.Run.Receive(Geometry(Report(command, FileBenchmarkStatus.Ready, 0, 0), 1280, 720));
        late.Clock += FileBenchmarkRunner.ReportSilenceGraceMilliseconds; late.Run.Tick();
        FileBenchmarkCommand next = late.Run.CurrentCommand;
        late.Run.Receive(Geometry(Report(command, FileBenchmarkStatus.Completed, command.FileSize, 1000), 320, 240));
        Check(late.Run.Snapshot()[0].State == ProfileTestState.NoData && !late.Run.Finished && late.Run.CurrentCommand.Test == next.Test,
            "A mismatched late completion neither passes the old result nor aborts the new profile");
        late.Run.Receive(Geometry(Report(command, FileBenchmarkStatus.Completed, command.FileSize, 1000), 1280, 720));
        Check(late.Run.Snapshot()[0].State == ProfileTestState.Passed && late.Run.CurrentCommand.Test == next.Test && late.Run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare,
            "A correctly formatted late full completion still repairs an eligible old result without starting the next test");
    }

    private static FileBenchmarkReport Geometry(FileBenchmarkReport report, int width, int height) { report.FrameWidth = width; report.FrameHeight = height; return report; }

    private static FileBenchmarkCommand Command(VideoProfile profile, long size) { return new FileBenchmarkCommand { Session = Guid.NewGuid(), Test = Guid.NewGuid(), Profile = profile, FileSize = size, Seed = 0x0123456789abcdefUL, Phase = FileBenchmarkPhase.Start }; }
    private static FileBenchmarkReport Report(FileBenchmarkCommand command, FileBenchmarkStatus status, long bytes, long elapsed) { return new FileBenchmarkReport { Session = command.Session, Test = command.Test, Profile = command.Profile, TotalBytes = command.FileSize, ReceivedBytes = bytes, ElapsedMilliseconds = elapsed, Status = status }; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static void Throws<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { checks++; return; } throw new Exception(message); }
    private sealed class Fixture
    {
        internal long Clock = 1000; internal readonly FileBenchmarkRunner Run;
        internal Fixture(VideoProfile[] profiles, long bytes, long maximum) { Run = new FileBenchmarkRunner(Guid.NewGuid(), profiles, bytes, 0x0123456789abcdefUL, maximum, delegate { return Clock; }); }
        internal Fixture(VideoProfile[] profiles, long bytes, long maximum, int width, int height) { Run = new FileBenchmarkRunner(Guid.NewGuid(), profiles, bytes, 0x0123456789abcdefUL, maximum, delegate { return Clock; }, width, height); }
        internal void Ready() { Run.Receive(Report(Run.CurrentCommand, FileBenchmarkStatus.Ready, 0, 0)); }
    }
}
