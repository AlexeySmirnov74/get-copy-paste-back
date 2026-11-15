// Logical files are generated and verified in bounded synthetic chunks.
// No filesystem, camera, microphone, audio playback, clipboard or GUI is used.
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using HorizonClient;

internal static class FileBenchmarkTests
{
    private static int checks;
    [STAThread]
    private static int Main()
    {
        try
        {
            Envelopes(); SegmentsAndProfiles(); Full100MiBFile(); ReceiverFailuresAndTiming();
            ClientHandshakeAndReports(); ClientTimeouts(); ClientReplayAndDispatch(); ClientNormalHelloRecovery();
            ClientContinuesAfterLostReady(); ClientStartWithoutPrepare(); ClientEarlyQueueBoundsAndCancel();
            Console.WriteLine("File benchmark tests passed: " + checks + " assertions (84-byte CRC envelopes, all profiles, exact segmented100MiB coverage, duplicates, corruption, ready/start handshake, immediate progress, bounded repeat, timeout/cancel/session races). No devices, disk or clipboard.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Envelopes()
    {
        FileBenchmarkCommand expected = Command(100L * 1024 * 1024, VideoProfile.Mp4Modem);
        expected.Seed = UInt64.MaxValue; expected.MaximumRunMilliseconds = 180000;
        byte[] bytes = FileBenchmarkProtocol.Command(expected);
        Check(bytes.Length == 84 && bytes.Length <= 256, "Command fits robust single-packet video");
        FileBenchmarkCommand actual;
        Check(FileBenchmarkProtocol.TryCommand(BridgeControl.Single(expected.Session, bytes), out actual) && FileBenchmarkProtocol.SameTest(expected, actual), "Command roundtrip preserves full100MiB size, unsigned seed and settings");
        using (var frame = VisualProtocol.EncodeFrame(expected.Session, 0, 1, bytes.Length, bytes))
            Check(FileBenchmarkProtocol.TryCommand(VisualProtocol.DecodeFrame(frame), out actual), "Command survives the normal compatible visual protocol");
        bytes[60] ^= 1;
        Check(!FileBenchmarkProtocol.TryCommand(BridgeControl.Single(expected.Session, bytes), out actual), "CRC-damaged command rejected");
        Check(!FileBenchmarkProtocol.TryCommand(BridgeControl.Single(Guid.NewGuid(), FileBenchmarkProtocol.Command(expected)), out actual), "Command packet transfer ID binds the session");
        var report = new FileBenchmarkReport { Session = expected.Session, Test = expected.Test, Profile = expected.Profile, Status = FileBenchmarkStatus.Progress,
            ReceivedBytes = 33554433, TotalBytes = expected.FileSize, ElapsedMilliseconds = 25L * 60 * 60 * 1000, FrameWidth = 7680, FrameHeight = 4320, Frames = 2700000 };
        bytes = FileBenchmarkProtocol.Report(report); FileBenchmarkReport decoded;
        Check(bytes.Length == 84 && bytes.Length <= 96, "Geometry and long-duration report fit one short audio packet");
        Check(FileBenchmarkProtocol.TryReport(bytes, expected.Session, out decoded) && decoded.ReceivedBytes == report.ReceivedBytes && decoded.ElapsedMilliseconds == report.ElapsedMilliseconds && decoded.FrameWidth == 7680, "Reports preserve long counters and geometry");
        Check(!FileBenchmarkProtocol.TryReport(bytes, Guid.NewGuid(), out decoded), "Report session isolation");
        bytes[44] ^= 1;
        Check(!FileBenchmarkProtocol.TryReport(bytes, expected.Session, out decoded), "CRC-damaged report rejected");
        report.Status = FileBenchmarkStatus.Completed;
        Reject(delegate { FileBenchmarkProtocol.Report(report); }, "Partial data cannot serialize as completion");
        expected.FileSize = FileBenchmarkProtocol.MaximumFileBytes + 1;
        Reject(delegate { FileBenchmarkProtocol.Command(expected); }, "Logical file above100MiB rejected");
        expected.FileSize = 100L * 1024 * 1024; expected.SegmentSize = VisualProtocol.MaxTransferBytes + 1;
        Reject(delegate { FileBenchmarkProtocol.Command(expected); }, "The global32MiB segment limit is unchanged");
        expected.SegmentSize = 1;
        Reject(delegate { FileBenchmarkProtocol.Command(expected); }, "Unbounded segment-count allocations rejected");
    }

    private static void SegmentsAndProfiles()
    {
        // Wire compatibility covers retired sender choices as well.
        foreach (VideoProfile profile in VisualProtocol.GetAllProfiles())
        {
            FileBenchmarkCommand command = Command(3 * 1024 + 19, profile); command.SegmentSize = 1024;
            var receiver = new FileBenchmarkReceiver(command); receiver.Start(1000);
            int count = FileBenchmarkProtocol.SegmentCount(command);
            Check(count == 4 && FileBenchmarkProtocol.SegmentLength(command, 3) == 19, "Exact final segment for " + profile);
            for (int segment = count - 1; segment >= 0; segment--)
            {
                Guid id = FileBenchmarkProtocol.SegmentTransferId(command.Test, segment); int decoded;
                Check(FileBenchmarkProtocol.TrySegmentIndex(command.Test, id, out decoded) && decoded == segment, "Reserved segment ID roundtrip");
                Check(!FileBenchmarkProtocol.TrySegmentIndex(Guid.NewGuid(), id, out decoded), "Foreign test prefix rejected");
                int packets = PacketCount(command, segment);
                for (int index = packets - 1; index >= 0; index--)
                {
                    Packet packet = Data(command, segment, index);
                    Check(receiver.Accept(packet, 1100), "Out-of-order valid packet accepted for " + profile);
                    Check(!receiver.Accept(packet, 1101), "Duplicate packets never add bytes");
                }
            }
            Check(receiver.Status == FileBenchmarkStatus.Completed && receiver.ReceivedBytes == command.FileSize, "All expected segment bits prove completion for " + profile);
            Check(receiver.Report(500000, 640, 360, 9).ElapsedMilliseconds == 100, "Repeated completion reports freeze elapsed time");
        }
    }

    private static void Full100MiBFile()
    {
        FileBenchmarkCommand command = Command(100L * 1024 * 1024, VideoProfile.Maximum4K);
        var receiver = new FileBenchmarkReceiver(command); receiver.Start(100);
        Packet missing = null; long now = 100;
        for (int segment = FileBenchmarkProtocol.SegmentCount(command) - 1; segment >= 0; segment--)
            for (int index = PacketCount(command, segment) - 1; index >= 0; index--)
            {
                Packet packet = Data(command, segment, index);
                if (segment == 0 && index == 0) { missing = packet; continue; }
                Check(receiver.Accept(packet, ++now), "100MiB logical-file packet verifies at its absolute offset");
            }
        Check(receiver.Status == FileBenchmarkStatus.Progress && receiver.ReceivedBytes == command.FileSize - missing.Data.Length, "One missing packet prevents full-file completion across all four segments");
        Check(receiver.Accept(missing, ++now) && receiver.Status == FileBenchmarkStatus.Completed && receiver.ReceivedBytes == command.FileSize, "Final missing packet proves exact100MiB complete coverage");
        Check(VisualProtocol.MaxTransferBytes == 32 * 1024 * 1024, "Clipboard and packet size limits were not raised for the test");
    }

    private static void ReceiverFailuresAndTiming()
    {
        FileBenchmarkCommand command = Command(4096, VideoProfile.Compatible);
        var receiver = new FileBenchmarkReceiver(command);
        receiver.Tick(1000000); Check(receiver.Status == FileBenchmarkStatus.Ready, "No timeout clock before Start");
        Check(!receiver.Accept(Data(command, 0, 0), 1000000) && receiver.ReceivedBytes == 0, "Data before Start is consumed but cannot count");
        receiver.Start(1000000); receiver.Start(1000100); receiver.Tick(1014999);
        Check(!receiver.Terminal, "Repeated Start does not prematurely change the no-data clock");
        receiver.Tick(1015000);
        Check(receiver.Status == FileBenchmarkStatus.TimedOut && receiver.Failure == FileBenchmarkFailure.NoData, "No data times out after15 seconds from first Start");
        Check(receiver.Report(2000000, 0, 0, 0).ElapsedMilliseconds == 15000, "No-data terminal elapsed remains frozen");
        receiver = new FileBenchmarkReceiver(command); receiver.Start(0); Packet first = Data(command, 0, 0); receiver.Accept(first, 100);
        receiver.Accept(first, 15000); receiver.Tick(15100);
        Check(!receiver.Terminal && receiver.ReceivedBytes == first.Data.Length, "Valid repeated traffic keeps a retransmission round alive without increasing unique bytes");
        FileBenchmarkReceiverSnapshot repeated = receiver.Snapshot(15100);
        Check(repeated.DuplicatePackets == 1 && repeated.UniquePackets == 1 && repeated.ActivityAgeMilliseconds == 100 && repeated.ProgressAgeMilliseconds == 15000, "Receiver diagnostics distinguish valid activity from unique progress");
        receiver.Tick(30000);
        Check(receiver.Failure == FileBenchmarkFailure.Stalled, "A partial file still times out after valid packet activity actually stops");
        command.MaximumRunMilliseconds = 1000; receiver = new FileBenchmarkReceiver(command); receiver.Start(0); receiver.Accept(first, 500); receiver.Tick(1000);
        Check(receiver.Failure == FileBenchmarkFailure.MaximumRun, "Explicit per-profile runtime limit is independent of progress");
        command.MaximumRunMilliseconds = 0; receiver = new FileBenchmarkReceiver(command); receiver.Start(0); first.Data[0] ^= 1;
        Check(!receiver.Accept(first, 10) && receiver.Status == FileBenchmarkStatus.InvalidData && receiver.Failure == FileBenchmarkFailure.DataMismatch && receiver.ReceivedBytes == 0, "One mismatching deterministic byte fails integrity without credit");
        receiver = new FileBenchmarkReceiver(command); receiver.Start(0); Packet wrong = Data(command, 0, 0); wrong.Count++;
        receiver.Accept(wrong, 20); Check(receiver.Failure == FileBenchmarkFailure.InvalidMetadata, "Segment packet shape must match declared profile and length");
        receiver = new FileBenchmarkReceiver(command); receiver.Start(0); receiver.Cancel(15); receiver.Accept(Data(command, 0, 0), 16);
        Check(receiver.Status == FileBenchmarkStatus.Cancelled && receiver.ReceivedBytes == 0, "Cancelled receiver cannot accept later data");
    }

    private static void ClientHandshakeAndReports()
    {
        using (var owner = new Control())
        {
            var services = new Services();
            using (var bridge = new ClientAudioBridge(owner, services))
            {
                bridge.ObserveFrame(640, 360); // Actual capture precedes Prepare decoding in ReceiverContext.
                FileBenchmarkCommand command = Command(1024, VideoProfile.Compatible);
                Check(bridge.HandleVideo(Announcement(command)), "File announcement is intercepted before clipboard");
                Check(Last(services, command).Status == FileBenchmarkStatus.Ready && services.Enabled == 1 && services.Microphones == 0, "Ready uses reverse playback with no microphone");
                Check(services.BenchmarkMode && services.Modes.Count == 1, "Initial Prepare pauses reverse clipboard before Ready");
                bridge.HandleVideo(Announcement(command)); services.Now = 7999; bridge.HandleVideo(Announcement(command));
                Check(services.Sent.Count == 1, "Repeated Prepare cannot flood or cancel its own Ready audio");
                services.Now = 8000; bridge.HandleVideo(Announcement(command)); Check(services.Sent.Count == 2, "Ready repeats after8 seconds");
                bridge.HandleVideo(Data(command, 0, 0)); Check(services.Sent.Count == 2, "Early data cannot start timing or produce false completion");
                command.Phase = FileBenchmarkPhase.Start; services.Now = 9000; bridge.HandleVideo(Announcement(command));
                bridge.ObserveFrame(640, 360); bridge.ObserveFrame(640, 360);
                services.Now = 9010; bridge.HandleVideo(Data(command, 0, 2));
                FileBenchmarkReport report = Last(services, command);
                Check(report.Status == FileBenchmarkStatus.Progress && report.ReceivedBytes == 256 && report.ElapsedMilliseconds == 10 && report.Frames == 2 && report.FrameWidth == 640, "First valid packet sends immediate progress with geometry");
                int sent = services.Sent.Count; bridge.HandleVideo(Data(command, 0, 2)); bridge.HandleVideo(Data(command, 0, 1));
                Check(services.Sent.Count == sent, "Duplicates and subsequent packets do not flood progress audio");
                services.Now = 17010; services.Tick();
                Check(Last(services, command).ReceivedBytes == 512 && services.Sent.Count == sent + 1, "Unique progress is reported at8-second intervals");
                services.Now = 18000; bridge.HandleVideo(Data(command, 0, 3)); bridge.HandleVideo(Data(command, 0, 0));
                report = Last(services, command);
                Check(report.Status == FileBenchmarkStatus.Completed && report.ReceivedBytes == 1024 && report.ElapsedMilliseconds == 9000, "Only full verified coverage generates completion");
                Check(services.BenchmarkMode, "Completion keeps reverse clipboard paused while final audio acknowledgements repeat");
                sent = services.Sent.Count; services.Now = 26000; services.Tick();
                Check(services.Sent.Count == sent + 1 && Last(services, command).ElapsedMilliseconds == 9000, "Final report repeats with frozen elapsed for audio-loss recovery");
                command.Phase = FileBenchmarkPhase.Cancel; bridge.HandleVideo(Announcement(command)); sent = services.Sent.Count;
                services.Now = 50000; services.Tick();
                Check(services.Sent.Count == sent && bridge.HandleVideo(Data(command, 0, 0)), "Cancel stops report timer and keeps old file bytes outside clipboard");
                Check(!services.BenchmarkMode, "Explicit Cancel resumes reverse clipboard without disabling reports or session");
                Check(!bridge.HandleVideo(new Packet { TransferId = Guid.NewGuid(), Data = new byte[1] }), "Ordinary clipboard transfer remains eligible");
            }
        }
    }

    private static void ClientTimeouts()
    {
        using (var owner = new Control())
        {
            var services = new Services();
            using (var bridge = new ClientAudioBridge(owner, services))
            {
                bridge.ObserveFrame(640, 360); // Actual capture precedes Prepare decoding in ReceiverContext.
                FileBenchmarkCommand command = Command(4096, VideoProfile.Compatible);
                bridge.HandleVideo(Announcement(command)); services.Now = 500000; services.Tick();
                Check(Last(services, command).Status == FileBenchmarkStatus.Ready, "A slow ready handshake cannot consume the data timeout");
                command.Phase = FileBenchmarkPhase.Start; bridge.HandleVideo(Announcement(command)); services.Now += 15000; services.Tick();
                Check(Last(services, command).Status == FileBenchmarkStatus.TimedOut && Last(services, command).Failure == FileBenchmarkFailure.NoData, "Client reports no data only after explicit Start");
                FileBenchmarkCommand next = Command(4096, VideoProfile.Compatible); next.Session = command.Session;
                bridge.HandleVideo(Announcement(next)); next.Phase = FileBenchmarkPhase.Start; bridge.HandleVideo(Announcement(next));
                services.Now += 1; Packet bad = Data(next, 0, 0); bad.Data[9] ^= 1; bridge.HandleVideo(bad);
                Check(Last(services, next).Status == FileBenchmarkStatus.InvalidData && Last(services, next).ReceivedBytes == 0, "Corrupted data reports integrity failure without counting bytes");
                Check(services.Microphones == 0, "No file-test phase touches a microphone");
                bridge.HandleHello(new BridgeHello { Session = next.Session, Seconds = 0 }, "ignored");
                Check(!services.BenchmarkMode, "Ordinary hello after terminal state resumes clipboard when Cancel was lost");
            }
        }
    }

    private static void ClientReplayAndDispatch()
    {
        using (var owner = new Control())
        {
            var services = new Services { DeferDispatch = true };
            using (var bridge = new ClientAudioBridge(owner, services))
            {
                bridge.ObserveFrame(640, 360); // Actual capture precedes Prepare decoding in ReceiverContext.
                FileBenchmarkCommand old = Command(4096, VideoProfile.Compatible); bridge.HandleVideo(Announcement(old));
                old.Phase = FileBenchmarkPhase.Cancel; bridge.HandleVideo(Announcement(old)); services.Flush();
                Check(services.Enabled == 0 && services.Sent.Count == 0, "A cancelled queued STA callback cannot enable playback or send stale Ready");
                old.Phase = FileBenchmarkPhase.Prepare; bridge.HandleVideo(Announcement(old)); services.Flush();
                Check(services.Enabled == 0, "Replayed cancelled Prepare cannot resurrect a test");
                FileBenchmarkCommand next = Command(4096, VideoProfile.Compatible); bridge.HandleVideo(Announcement(next)); services.Flush();
                Check(services.Enabled == 1 && Last(services, next).Status == FileBenchmarkStatus.Ready, "A fresh test can start after cancellation");
                old.Phase = FileBenchmarkPhase.Cancel; bridge.HandleVideo(Announcement(old)); services.Flush();
                Check(services.BenchmarkMode, "A stale Cancel cannot resume clipboard during a newer test");
                Timer stale = services.Timers[services.Timers.Count - 1];
                bridge.HandleHello(new BridgeHello { Session = Guid.NewGuid(), Seconds = 0 }, "ignored"); int sent = services.Sent.Count;
                services.Now = 999999; stale.Fire();
                Check(services.Sent.Count == sent && bridge.HandleVideo(Data(next, 0, 0)), "New session retires callbacks but reserves previous test data");
                bridge.Dispose(); bridge.HandleVideo(Announcement(Command(4096, VideoProfile.Compatible))); services.Flush();
                Check(services.Sent.Count == sent, "Disposed bridge cannot publish test reports");
                Check(!services.BenchmarkMode, "Dispose releases benchmark clipboard pause");
            }
        }
    }

    private static void ClientNormalHelloRecovery()
    {
        using (var owner = new Control())
        {
            var services = new Services();
            using (var bridge = new ClientAudioBridge(owner, services))
            {
                bridge.ObserveFrame(640, 360); // Actual capture precedes Prepare decoding in ReceiverContext.
                FileBenchmarkCommand command = Command(1024, VideoProfile.Fast);
                bridge.HandleVideo(Announcement(command));
                bridge.HandleHello(new BridgeHello { Session = command.Session, Seconds = 0 }, "ignored");
                Check(services.BenchmarkMode, "An older queued ordinary hello cannot undo a fresh Prepare");
                command.Phase = FileBenchmarkPhase.Start; bridge.HandleVideo(Announcement(command)); services.Now = 10; bridge.HandleVideo(Data(command, 0, 0));
                Check(Last(services, command).Status == FileBenchmarkStatus.Completed && services.BenchmarkMode, "Full small file completes with mode held for repeated ACKs");
                Timer terminalTimer = services.Timers[services.Timers.Count - 1];
                bridge.HandleHello(new BridgeHello { Session = Guid.NewGuid(), Seconds = 0 }, "ignored");
                int sent = services.Sent.Count; services.Now = 100000; terminalTimer.Fire();
                Check(!services.BenchmarkMode && terminalTimer.Disposed && services.Sent.Count == sent, "Terminal file followed by a new-session hello retires its timer and resumes clipboard");
                FileBenchmarkCommand waiting = Command(1024, VideoProfile.Fast); bridge.HandleVideo(Announcement(waiting));
                services.Now += 20000; bridge.HandleHello(new BridgeHello { Session = waiting.Session, Seconds = 0 }, "ignored");
                Check(!services.BenchmarkMode, "An abandoned Ready handshake resumes on an ordinary hello after the server's ready deadline");
            }
        }
    }

    private static FileBenchmarkCommand Command(long size, VideoProfile profile)
    {
        return new FileBenchmarkCommand { Session = Guid.NewGuid(), Test = Guid.NewGuid(), Profile = profile, FileSize = size, Seed = 0x0123456789abcdefUL, Phase = FileBenchmarkPhase.Prepare };
    }

    private static void ClientContinuesAfterLostReady()
    {
        foreach (bool throws in new[] { false, true })
        using (var owner = new Control())
        {
            var services = new Services { DropReady = true, ThrowReady = throws };
            using (var bridge = new ClientAudioBridge(owner, services))
            {
                bridge.ObserveFrame(640, 360); // Actual capture precedes Prepare decoding in ReceiverContext.
                FileBenchmarkCommand command = Command(1024, VideoProfile.Compatible);
                bridge.HandleVideo(Announcement(command));
                Check(services.DroppedReady == 1 && services.Sent.Count == 0 && services.BenchmarkMode,
                    "A lost or failing Ready audio callback leaves the prepared receiver active");
                command.Phase = FileBenchmarkPhase.Start; services.Now = 1500; bridge.HandleVideo(Announcement(command));
                for (int index = 0; index < 4; index++) { services.Now = 1510 + index; bridge.HandleVideo(Data(command, 0, index)); }
                FileBenchmarkReport completed = Last(services, command);
                Check(completed.Status == FileBenchmarkStatus.Completed && completed.ReceivedBytes == 1024 && completed.ElapsedMilliseconds == 13,
                    "Explicit Start and full verified data complete independently of Ready reaching the server");
                command.Phase = FileBenchmarkPhase.Prepare; services.Now = 10000; bridge.HandleVideo(Announcement(command));
                Check(Last(services, command).Status == FileBenchmarkStatus.Completed && Last(services, command).ElapsedMilliseconds == 13,
                    "Repeating Prepare after Start cannot reset completed coverage or its elapsed time");
            }
        }
    }

    private static void ClientStartWithoutPrepare()
    {
        using (var owner = new Control())
        {
            var services = new Services { DeferDispatch = true };
            using (var bridge = new ClientAudioBridge(owner, services))
            {
                bridge.ObserveFrame(640, 360); // Actual capture precedes Prepare decoding in ReceiverContext.
                FileBenchmarkCommand command = Command(1024, VideoProfile.Compatible); command.Phase = FileBenchmarkPhase.Start;
                bridge.HandleVideo(Announcement(command));
                Check(services.Enabled == 0 && services.Sent.Count == 0, "Start recognition performs no audio I/O before its owner STA runs");
                for (int index = 0; index < 4; index++)
                {
                    services.Now = 10 * (index + 1); Packet packet = Data(command, 0, index);
                    Check(bridge.HandleVideo(packet), "Data after Start remains reserved while STA initialization is pending");
                    packet.Data[0] ^= 1; // The bounded queue owns its copy.
                }
                bridge.HandleVideo(Announcement(command)); services.Now = 1000; services.Flush();
                FileBenchmarkReport report = Last(services, command);
                Check(report.Status == FileBenchmarkStatus.Completed && report.ReceivedBytes == 1024 && report.ElapsedMilliseconds == 40,
                    "Start without Prepare or Hello initializes the receiver and preserves early data and arrival timing");
                Check(services.Enabled == 1 && services.BenchmarkMode && services.Microphones == 0,
                    "Implicit preparation enables only the normal reverse reports and benchmark pause on STA");
                FileBenchmarkCommand mismatched = FileBenchmarkProtocol.Copy(command); mismatched.Seed++;
                bridge.HandleVideo(Announcement(mismatched)); services.Flush();
                Check(Last(services, command).Status == FileBenchmarkStatus.Completed && services.Enabled == 1,
                    "A repeated Start with mismatched metadata cannot reinitialize an existing test identity");
                command.Phase = FileBenchmarkPhase.Prepare; services.Now = 9000; bridge.HandleVideo(Announcement(command)); services.Flush();
                Check(Last(services, command).ElapsedMilliseconds == 40, "A later compatible Prepare preserves the implicitly started test");
            }
        }
    }

    private static void ClientEarlyQueueBoundsAndCancel()
    {
        using (var owner = new Control())
        {
            var services = new Services { DeferDispatch = true };
            using (var bridge = new ClientAudioBridge(owner, services))
            {
                bridge.ObserveFrame(640, 360); // Actual capture precedes Prepare decoding in ReceiverContext.
                FileBenchmarkCommand command = Command(64 * 1024, VideoProfile.Compatible); command.Phase = FileBenchmarkPhase.Start;
                bridge.HandleVideo(Announcement(command));
                for (int index = 0; index < 160; index++) { services.Now++; bridge.HandleVideo(Data(command, 0, index)); }
                services.Now = 1000; services.Flush();
                Check(Last(services, command).Status == FileBenchmarkStatus.Progress && Last(services, command).ReceivedBytes == 128 * 256,
                    "Delayed STA initialization retains only the bounded128-packet early queue");
                for (int index = 128; index < 256; index++) { services.Now++; bridge.HandleVideo(Data(command, 0, index)); }
                Check(Last(services, command).Status == FileBenchmarkStatus.Completed && Last(services, command).ReceivedBytes == command.FileSize,
                    "Normal video repetition recovers data beyond the early queue budget");
                FileBenchmarkCommand cancelled = Command(1024, VideoProfile.Compatible); cancelled.Phase = FileBenchmarkPhase.Start;
                bridge.HandleVideo(Announcement(cancelled)); bridge.HandleVideo(Data(cancelled, 0, 0));
                cancelled.Phase = FileBenchmarkPhase.Cancel; bridge.HandleVideo(Announcement(cancelled));
                int reports = services.Sent.Count, enabled = services.Enabled; services.Flush();
                cancelled.Phase = FileBenchmarkPhase.Start; bridge.HandleVideo(Announcement(cancelled)); services.Flush();
                Check(services.Sent.Count == reports && services.Enabled == enabled,
                    "Cancel discards early data and prevents a delayed or replayed Start from resurrecting the test");
            }
        }
    }
    private static Packet Announcement(FileBenchmarkCommand command) { return BridgeControl.Single(command.Session, FileBenchmarkProtocol.Command(command)); }
    private static int PacketCount(FileBenchmarkCommand command, int segment)
    {
        int size = VisualProtocol.GetChunkSize(command.Profile); return (FileBenchmarkProtocol.SegmentLength(command, segment) + size - 1) / size;
    }
    private static Packet Data(FileBenchmarkCommand command, int segment, int index)
    {
        int chunkSize = VisualProtocol.GetChunkSize(command.Profile), total = FileBenchmarkProtocol.SegmentLength(command, segment), count = (total + chunkSize - 1) / chunkSize;
        int length = Math.Min(chunkSize, total - index * chunkSize); var bytes = new byte[length];
        BenchmarkData.Fill(command.Seed, (long)segment * command.SegmentSize + (long)index * chunkSize, bytes, 0, bytes.Length);
        return new Packet { TransferId = FileBenchmarkProtocol.SegmentTransferId(command.Test, segment), Index = index, Count = count, TotalLength = total, Data = bytes, Profile = command.Profile, ChunkSize = chunkSize };
    }
    private static FileBenchmarkReport Last(Services services, FileBenchmarkCommand command)
    {
        FileBenchmarkReport report;
        if (services.Sent.Count == 0 || !FileBenchmarkProtocol.TryReport(services.Sent[services.Sent.Count - 1], command.Session, out report) || report.Test != command.Test) throw new Exception("Missing matching file report."); return report;
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, message); }
    private sealed class Services : ClientAudioBridgeServices
    {
        internal long Now;
        internal int Enabled, Microphones;
        internal bool DeferDispatch, BenchmarkMode, DropReady, ThrowReady;
        internal int DroppedReady;
        private Guid enabledSession;
        internal readonly List<bool> Modes = new List<bool>();
        internal readonly List<byte[]> Sent = new List<byte[]>();
        internal readonly List<Timer> Timers = new List<Timer>();
        private readonly Queue<Action> dispatch = new Queue<Action>();
        internal Services() : base(null) { }
        internal override long Milliseconds { get { return Now; } }
        internal override void Enable(Guid session) { Enabled++; enabledSession = session; }
        internal override void SetBenchmarkMode(bool active) { BenchmarkMode = active; Modes.Add(active); }
        internal override void Send(byte[] bytes, Guid transfer)
        {
            FileBenchmarkReport report;
            if (DropReady && FileBenchmarkProtocol.TryReport(bytes, enabledSession, out report) && report.Status == FileBenchmarkStatus.Ready)
            { DroppedReady++; if (ThrowReady) throw new InvalidOperationException("Ready callback fixture failure"); return; }
            Sent.Add((byte[])bytes.Clone());
        }
        internal override void Dispatch(Control owner, Action action) { if (DeferDispatch) dispatch.Enqueue(action); else action(); }
        internal override AudioEndpoint[] EnumerateMicrophones() { Microphones++; throw new Exception("File tests must not enumerate a microphone."); }
        internal override IDisposable StartMicrophone(string id, Action<float[]> samples) { Microphones++; throw new Exception("File tests must not open a microphone."); }
        internal override IDisposable Schedule(Action action, int milliseconds) { var timer = new Timer(action); Timers.Add(timer); return timer; }
        internal override void Queue(Action action) { action(); }
        internal void Tick() { if (Timers.Count != 0) Timers[Timers.Count - 1].Fire(); }
        internal void Flush() { while (dispatch.Count > 0) dispatch.Dequeue()(); }
    }
    private sealed class Timer : IDisposable
    {
        private readonly Action callback;
        internal bool Disposed;
        internal Timer(Action action) { callback = action; }
        internal void Fire() { callback(); }
        public void Dispose() { Disposed = true; }
    }
}
