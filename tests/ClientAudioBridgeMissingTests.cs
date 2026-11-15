using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

internal static class ClientAudioBridgeMissingTests
{
    private static int checks;
    [STAThread]
    private static int Main()
    {
        try { CapabilityLatchAndMissingPackets(); CompactSenderUsesFixedShortFsk(); Console.WriteLine("Client benchmark feedback integration passed: " + checks + "; legacy opt-in, exact-test capability latch, bounded missing window/completion and production sender short PCM. All I/O mocked."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    private static FileBenchmarkCommand Command()
    { return new FileBenchmarkCommand { Session = Guid.NewGuid(), Test = Guid.NewGuid(), Profile = VideoProfile.Compatible, FileSize = 1024, Seed = 123, MaximumRunMilliseconds = 180000 }; }
    private static Packet Data(FileBenchmarkCommand c, int index)
    {
        byte[] bytes = new byte[256]; BenchmarkData.Fill(c.Seed, index * 256L, bytes, 0, bytes.Length);
        return new Packet { TransferId = FileBenchmarkProtocol.SegmentTransferId(c.Test, 0), Index = index, Count = 4, TotalLength = 1024, ChunkSize = 256, Profile = c.Profile, Data = bytes };
    }
    private static void Control(ClientAudioBridge bridge, FileBenchmarkCommand c)
    { Check(bridge.HandleVideo(BridgeControl.Single(c.Session, FileBenchmarkProtocol.Command(c))), "Benchmark control is reserved from the ordinary clipboard path."); }
    private static void CapabilityLatchAndMissingPackets()
    {
        using (Control owner = new Control())
        {
            var io = new BridgeServices();
            using (var bridge = new ClientAudioBridge(owner, io))
            {
                bridge.ObserveFrame(640, 480); // Receiver observes the Prepare frame before decoding it.
                FileBenchmarkCommand c = Command(); FileBenchmarkReport report;
                Control(bridge, c);
                Check(io.Legacy.Count == 1 && io.Legacy[0].Length == 84 && io.Compact.Count == 0, "An old server gets unchanged84byte Ready and no unknown compact framing.");
                c.CompactReports = true; Control(bridge, c);
                Check(io.Compact.Count == 1 && CompactFileBenchmarkReport.TryDecode(io.Compact[0], c, out report) && report.Status == FileBenchmarkStatus.Ready, "A later modern announcement upgrades the same exact pending test immediately.");
                c.CompactReports = false; c.Phase = FileBenchmarkPhase.Start; Control(bridge, c);
                Check(bridge.HandleVideo(Data(c, 0)), "First verified data is reserved for this logical file.");
                Check(CompactFileBenchmarkReport.TryDecode(io.Compact[io.Compact.Count - 1], c, out report) && report.ReceivedBytes == 256 && report.MissingIndices == null,
                    "Legacy control alternation cannot downgrade a latched compact test; the first packet does not request future unsent packets.");
                bridge.HandleVideo(Data(c, 1)); bridge.HandleVideo(Data(c, 3));
                io.Now = 9000; io.Tick();
                Check(CompactFileBenchmarkReport.TryDecode(io.Compact[io.Compact.Count - 1], c, out report) && report.ReceivedBytes == 768 && report.MissingIndices.Length == 1 && report.MissingIndices[0] == 2,
                    "Periodic feedback requests the actual hole rather than all source data.");
                bridge.HandleVideo(Data(c, 2));
                Check(CompactFileBenchmarkReport.TryDecode(io.Compact[io.Compact.Count - 1], c, out report) && report.Status == FileBenchmarkStatus.Completed && report.ReceivedBytes == 1024 && report.MissingIndices == null,
                    "Only complete verified coverage sends terminal feedback without NACK data.");
                int before = io.Compact.Count;
                c.Phase = FileBenchmarkPhase.Prepare; Control(bridge, c);
                Check(io.Compact.Count == before, "A duplicate Prepare neither resets completed coverage nor bypasses repeat limiting.");
                FileBenchmarkCommand next = Command(); next.Session = c.Session; Control(bridge, next);
                Check(io.Legacy[io.Legacy.Count - 1].Length == 84, "A new legacy logical test does not inherit the previous test's opt-in.");
                next.Phase = FileBenchmarkPhase.Start; Control(bridge, next); bridge.HandleVideo(Data(next, 0));
                Check(FileBenchmarkProtocol.TryReport(io.Legacy[io.Legacy.Count - 1], next.Session, out report) && report.MissingIndices == null && io.Legacy[io.Legacy.Count - 1].Length == 84,
                    "Old servers never receive the new NACK envelope implicitly.");
            }
        }
    }
    private static void CompactSenderUsesFixedShortFsk()
    {
        using (Control owner = new Control())
        {
            int plays = 0, captures = 0, selectedEncoderCalls = 0;
            Exception workerError = null; FileBenchmarkCommand c = Command();
            var report = new FileBenchmarkReport { Session = c.Session, Test = c.Test, Profile = c.Profile, Status = FileBenchmarkStatus.Ready, TotalBytes = c.FileSize };
            byte[] wire = CompactFileBenchmarkReport.Encode(c, report);
            var services = new AudioSenderServices { ValidateModem = delegate { }, AddListener = delegate { return true; }, RemoveListener = delegate { return true; }, Sequence = delegate { return 1; },
                Outputs = delegate { return new[] { new AudioEndpoint("waveout:1", "VMware Virtual Audio (DevTap)") }; },
                CreatePlayer = delegate(string id) { return new AudioPlayer(id); }, DisposePlayer = delegate(AudioPlayer player) { player.Dispose(); },
                Capture = delegate { Interlocked.Increment(ref captures); throw new Exception("Clipboard access is forbidden."); },
                EncodeAudio = delegate { Interlocked.Increment(ref selectedEncoderCalls); throw new Exception("The selected bulk codec must not encode compact control."); },
                Play = delegate(AudioPlayer player, float[] pcm, CancellationToken cancel)
                {
                    cancel.ThrowIfCancellationRequested(); Check(pcm.Length < 22000, "Each control burst remains shorter than460ms.");
                    var decoder = new AudioDecoder(); int received = 0;
                    decoder.CompactFileReportReceived += delegate(byte[] message) { FileBenchmarkReport decoded; Check(CompactFileBenchmarkReport.TryDecode(message, c, out decoded) && decoded.Status == FileBenchmarkStatus.Ready, "Production sender PCM contains the exact compact report."); received++; };
                    decoder.Feed(pcm); Check(received == 1, "One play contains one complete decodable short frame."); Interlocked.Increment(ref plays);
                } };
            using (var sender = new AudioClipboardSender(owner, services))
            {
                sender.Error = delegate(Exception error) { workerError = error; };
                sender.SetModem(AudioModemKind.Quiet); sender.Enable(c.Session); sender.SetBenchmarkMode(true);
                sender.SendCompactFileReport(wire, Guid.NewGuid());
                Stopwatch timer = Stopwatch.StartNew();
                while (Volatile.Read(ref plays) < 3 && timer.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(5); }
                Check(plays == 3 && workerError == null && captures == 0 && selectedEncoderCalls == 0 && sender.SelectedModem == AudioModemKind.Quiet,
                    "Three fixed-FSK control repeats are independent of bulk-modem choice and never capture the clipboard.");
                sender.Disable();
            }
        }
    }
    private sealed class BridgeServices : ClientAudioBridgeServices
    {
        internal readonly List<byte[]> Legacy = new List<byte[]>(), Compact = new List<byte[]>();
        internal readonly List<TimerFixture> Timers = new List<TimerFixture>();
        internal long Now;
        internal BridgeServices() : base(null) { }
        internal override long Milliseconds { get { return Now; } }
        internal override void Enable(Guid session) { }
        internal override void SetBenchmarkMode(bool active) { }
        internal override void Send(byte[] bytes, Guid transfer) { Legacy.Add(bytes); }
        internal override void SendCompactFileReport(byte[] wire, Guid transfer) { Compact.Add(wire); }
        internal override void Dispatch(Control owner, Action action) { action(); }
        internal override IDisposable Schedule(Action action, int milliseconds) { var result = new TimerFixture(action); Timers.Add(result); return result; }
        internal override void Queue(Action action) { action(); }
        internal void Tick() { TimerFixture[] timers = Timers.ToArray(); foreach (TimerFixture timer in timers) if (!timer.Disposed) timer.Fire(); }
    }
    private sealed class TimerFixture : IDisposable
    {
        private readonly Action action;
        internal bool Disposed;
        internal TimerFixture(Action action) { this.action = action; }
        internal void Fire() { action(); }
        public void Dispose() { Disposed = true; }
    }
}
