using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using HorizonClient;

// Explicit opt-in UVC integration check. It never configures a device, opens
// a physical/default camera, accesses the clipboard, or plays/records sound.
internal static class FileBenchmarkCameraSmoke
{
    private const int OverallMilliseconds = 75000;
    private static readonly VideoProfile[] Profiles = { VideoProfile.Compatible, VideoProfile.Compact640, VideoProfile.Mp4Modem };

    private sealed class Counts
    {
        internal int SourceFrames, Commands, DataFrames, ReportsEncoded, ReportsDecoded, ReadyAcks, CompletedAcks;
    }

    private sealed class Harness
    {
        internal readonly object Gate = new object();
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal readonly Guid Session = Guid.NewGuid();
        internal readonly Dictionary<VideoProfile, Counts> ProfileCounts = new Dictionary<VideoProfile, Counts>();
        internal FileBenchmarkRunner Runner;
        internal ClientAudioBridge Bridge;
        internal Exception Error;
        internal int CapturedFrames, DecodedPackets, UnhandledPackets, EnableCalls, DispatchCalls, MicrophoneCalls;
        internal long SyntheticSamples;
        internal int Closing;
        internal Harness() { foreach (VideoProfile profile in Profiles) ProfileCounts.Add(profile, new Counts()); }
        internal void Fail(Exception error) { lock (Gate) if (Volatile.Read(ref Closing) == 0 && Error == null) Error = error; }
        internal bool Failed { get { lock (Gate) return Error != null; } }
        internal void ReceiveFrame(Bitmap bitmap)
        {
            if (Volatile.Read(ref Closing) != 0) return;
            try
            {
                Interlocked.Increment(ref CapturedFrames);
                Bridge.ObserveFrame(bitmap.Width, bitmap.Height);
                foreach (Packet packet in VisualProtocol.DecodePackets(bitmap))
                {
                    Interlocked.Increment(ref DecodedPackets);
                    if (!Bridge.HandleVideo(packet)) Interlocked.Increment(ref UnhandledPackets);
                    // Ordinary clipboard packets are deliberately never assembled.
                }
            }
            catch (Exception error) { Fail(error); }
        }
    }

    private sealed class SyntheticReportServices : ClientAudioBridgeServices
    {
        private readonly Harness harness;
        private readonly int staThread;
        private readonly object audioGate = new object();
        private readonly AudioDecoder decoder = new AudioDecoder();
        private readonly Dictionary<Guid, byte[]> pending = new Dictionary<Guid, byte[]>();
        internal SyntheticReportServices(Harness harness) : base(null)
        {
            this.harness = harness; staThread = Thread.CurrentThread.ManagedThreadId;
            decoder.PacketReceived += delegate(Packet packet)
            {
                byte[] expected;
                if (!pending.TryGetValue(packet.TransferId, out expected) || packet.Index != 0 || packet.Count != 1 || packet.Data == null || !Equal(expected, packet.Data))
                    throw new InvalidDataException("Synthetic audio report bytes or identity changed.");
                FileBenchmarkReport report;
                if (!FileBenchmarkProtocol.TryReport(packet.Data, harness.Session, out report)) throw new InvalidDataException("Synthetic audio decoded an invalid file report.");
                pending.Remove(packet.TransferId);
                lock (harness.Gate)
                {
                    Counts counts = harness.ProfileCounts[report.Profile]; counts.ReportsDecoded++;
                    if (report.Status == FileBenchmarkStatus.Ready) counts.ReadyAcks++;
                    if (report.Status == FileBenchmarkStatus.Completed) counts.CompletedAcks++;
                }
                harness.Runner.Receive(report);
            };
        }
        internal override long Milliseconds { get { return harness.Clock.ElapsedMilliseconds; } }
        internal override void Enable(Guid session)
        {
            if (Thread.CurrentThread.ManagedThreadId != staThread || session != harness.Session) throw new InvalidOperationException("File benchmark enable did not run on the expected owner STA/session.");
            Interlocked.Increment(ref harness.EnableCalls);
        }
        internal override void SetBenchmarkMode(bool enabled)
        {
            if (Thread.CurrentThread.ManagedThreadId != staThread) throw new InvalidOperationException("Benchmark output suppression did not run on the owner STA.");
            // The production method controls clipboard/audio output. Neither is
            // instantiated by this smoke harness, so this is an intentional no-op.
        }
        internal override void Dispatch(Control owner, Action action)
        {
            Interlocked.Increment(ref harness.DispatchCalls);
            base.Dispatch(owner, delegate
            {
                try { if (Thread.CurrentThread.ManagedThreadId != staThread) throw new InvalidOperationException("Dispatch left the owner STA."); action(); }
                catch (Exception error) { harness.Fail(error); }
            });
        }
        internal override void Send(byte[] bytes, Guid transferId)
        {
            if (Volatile.Read(ref harness.Closing) != 0) return;
            try
            {
                FileBenchmarkReport report;
                if (bytes == null || bytes.Length != 84 || !FileBenchmarkProtocol.TryReport(bytes, harness.Session, out report)) throw new InvalidDataException("Expected one actual 84-byte client file-report envelope.");
                lock (audioGate)
                {
                    lock (harness.Gate) harness.ProfileCounts[report.Profile].ReportsEncoded++;
                    pending.Add(transferId, (byte[])bytes.Clone());
                    float[] samples = AudioModem.Encode(BridgeControl.Single(transferId, bytes));
                    Interlocked.Add(ref harness.SyntheticSamples, samples.Length);
                    // No wall-clock audio pacing or output endpoint: this tests the
                    // actual modem wire encoding/decoding, not audio throughput.
                    for (int position = 0; position < samples.Length; position += 960)
                    {
                        var chunk = new float[Math.Min(960, samples.Length - position)];
                        Array.Copy(samples, position, chunk, 0, chunk.Length); decoder.Feed(chunk);
                    }
                    if (pending.ContainsKey(transferId)) throw new InvalidDataException("Synthetic audio decoder did not recover the complete report.");
                }
            }
            catch (Exception error) { harness.Fail(error); }
        }
        internal override AudioEndpoint[] EnumerateMicrophones()
        {
            Interlocked.Increment(ref harness.MicrophoneCalls); var error = new InvalidOperationException("A file test must never enumerate microphones."); harness.Fail(error); throw error;
        }
        internal override IDisposable StartMicrophone(string id, Action<float[]> samples)
        {
            Interlocked.Increment(ref harness.MicrophoneCalls); var error = new InvalidOperationException("A file test must never open microphones."); harness.Fail(error); throw error;
        }
        internal override IDisposable Schedule(Action action, int milliseconds)
        {
            return base.Schedule(delegate { try { action(); } catch (Exception error) { harness.Fail(error); } }, milliseconds);
        }
        internal override void Queue(Action action)
        {
            base.Queue(delegate { try { action(); } catch (Exception error) { harness.Fail(error); } });
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        bool synthetic = args.Length == 1 && args[0] == "--self-test";
        if (!synthetic && (args.Length != 3 || String.IsNullOrWhiteSpace(args[0]) || String.IsNullOrWhiteSpace(args[1]) || String.IsNullOrWhiteSpace(args[2])))
        { Console.Error.WriteLine("Usage: FileBenchmarkCameraSmoke.exe KEY_FILE EXACT_OWN_CAMERA_MONIKER OUTPUT_JSON | --self-test"); return 2; }
        string output = synthetic ? null : args[2];
        try
        {
            if (!synthetic)
            {
                string logPath = Path.GetFullPath(output) + ".debug.log";
                DiagnosticLog.Initialize(logPath);
                DiagnosticLog.Write("smoke.started", "scope", "actual local UVC forward; synthetic audio reports; no Horizon hop/real audio/clipboard", "log_path", logPath, "camera_moniker", args[1]);
            }
            return Run(synthetic ? null : args[0], synthetic ? null : args[1], output, synthetic);
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("smoke.failed", error);
            Write(output, new Dictionary<string, object> { { "status", "error" }, { "error", error.ToString() }, { "scope", "local UVC forward; synthetic audio reports; no clipboard or Horizon hop" } });
            return 1;
        }
        finally { DiagnosticLog.Write("smoke.stopped"); DiagnosticLog.Shutdown(); }
    }

    private static int Run(string key, string moniker, string output, bool synthetic)
    {
        if (!synthetic)
        {
            if (moniker.IndexOf("vid_1209", StringComparison.OrdinalIgnoreCase) < 0 || moniker.IndexOf("pid_f002", StringComparison.OrdinalIgnoreCase) < 0) throw new ArgumentException("Only an explicit project-owned camera moniker (VID1209/PIDF002) is accepted.");
            int found = 0; foreach (CameraDevice device in CameraDevices.List()) if (String.Equals(device.Id, moniker, StringComparison.Ordinal)) found++;
            if (found != 1) throw new InvalidOperationException("The exact project-owned camera moniker is absent or ambiguous.");
        }
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var harness = new Harness();
        using (var owner = new Control())
        using (var stop = new ManualResetEvent(false))
        {
            IntPtr handle = owner.Handle;
            var services = new SyntheticReportServices(harness);
            CameraCapture capture = null; UvcCamera camera = null; Thread producer = null;
            bool joined = true, timedOut = false;
            int width = 640, height = 480, fps = 30, captureWidth = 640, captureHeight = 480;
            ulong seed;
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) { var bytes = new byte[8]; random.GetBytes(bytes); seed = BitConverter.ToUInt64(bytes, 0); }
            harness.Bridge = new ClientAudioBridge(owner, services);
            try
            {
                if (!synthetic)
                {
                    capture = new CameraCapture(moniker, harness.ReceiveFrame, 640, 480);
                    capture.OnError += harness.Fail; capture.Start();
                    captureWidth = capture.Width; captureHeight = capture.Height;
                    camera = new UvcCamera("127.0.0.1", 13261, key);
                    width = camera.Width; height = camera.Height; fps = camera.FramesPerSecond;
                    if (width != 640 || height != 480 || fps != 30 || camera.IsUncompressed || camera.IsH264Probe || captureWidth != 640 || captureHeight != 480)
                        throw new InvalidOperationException("This smoke test requires the already-configured 640x480/30 MJPEG camera; it does not change its mode.");
                }
                harness.Runner = new FileBenchmarkRunner(harness.Session, Profiles, 1024, seed, 15000);
                Action<Bitmap> send = synthetic ? (Action<Bitmap>)harness.ReceiveFrame : camera.Send;
                producer = new Thread(delegate() { try { Produce(harness, stop, width, height, fps, send); } catch (Exception error) { harness.Fail(error); } }) { IsBackground = true, Name = "File benchmark UVC smoke sender" };
                producer.Start();
                while (!harness.Failed && !harness.Runner.TailComplete)
                {
                    if (harness.Clock.ElapsedMilliseconds >= OverallMilliseconds) { timedOut = true; harness.Runner.Cancel(); break; }
                    Application.DoEvents(); harness.Runner.Tick(); Thread.Sleep(5);
                }
            }
            finally
            {
                stop.Set();
                if (harness.Runner != null && !harness.Runner.Finished) harness.Runner.Cancel();
                if (producer != null)
                {
                    var wait = Stopwatch.StartNew();
                    while (producer.IsAlive && wait.ElapsedMilliseconds < 15000) { Application.DoEvents(); producer.Join(10); }
                    joined = !producer.IsAlive;
                }
                Interlocked.Exchange(ref harness.Closing, 1);
                harness.Bridge.Dispose();
                if (capture != null) capture.Dispose();
                if (camera != null) camera.Dispose();
            }
            bool passed = !timedOut && joined && !harness.Failed && harness.MicrophoneCalls == 0;
            var rows = new List<object>();
            foreach (ProfileTestResult result in harness.Runner.Snapshot())
            {
                Counts counts = harness.ProfileCounts[result.Profile];
                bool complete = result.State == ProfileTestState.Passed && result.ReceivedBytes == 1024 && result.FileBytes == 1024 && counts.ReadyAcks > 0 && counts.CompletedAcks > 0;
                passed &= complete;
                rows.Add(new Dictionary<string, object> { { "profile", result.Profile.ToString() }, { "state", result.State.ToString() }, { "passed", complete },
                    { "all_expected_bytes", result.ReceivedBytes == 1024 }, { "verified_bytes", result.ReceivedBytes }, { "file_bytes", result.FileBytes },
                    { "receive_milliseconds", result.ElapsedMilliseconds }, { "receive_mbps", result.MegabitsPerSecond },
                    { "client_frame_width", result.Width }, { "client_frame_height", result.Height }, { "client_frames", result.Frames },
                    { "source_frames", counts.SourceFrames }, { "source_command_frames", counts.Commands }, { "source_data_frames", counts.DataFrames },
                    { "audio_reports_encoded", counts.ReportsEncoded }, { "audio_reports_decoded", counts.ReportsDecoded }, { "ready_acks", counts.ReadyAcks }, { "completed_acks", counts.CompletedAcks } });
            }
            Write(output, new Dictionary<string, object> { { "status", passed ? "passed" : "failed" }, { "profiles", rows },
                { "scope", synthetic ? "synthetic video and audio only; no camera/clipboard/endpoints" : "actual local UVC forward; synthetic audio reports; no Horizon hop/real audio/clipboard" },
                { "source_width", width }, { "source_height", height }, { "source_fps", fps }, { "capture_width", captureWidth }, { "capture_height", captureHeight },
                { "captured_frames", harness.CapturedFrames }, { "decoded_packets", harness.DecodedPackets }, { "ignored_nonbenchmark_packets", harness.UnhandledPackets },
                { "sta_enable_calls", harness.EnableCalls }, { "sta_dispatch_calls", harness.DispatchCalls }, { "microphone_calls", harness.MicrophoneCalls },
                { "synthetic_audio_samples", harness.SyntheticSamples }, { "elapsed_milliseconds", harness.Clock.ElapsedMilliseconds }, { "overall_timeout", timedOut },
                { "producer_joined", joined }, { "error", harness.Error == null ? null : harness.Error.ToString() } });
            return passed ? 0 : 1;
        }
    }

    private static void Produce(Harness harness, ManualResetEvent stop, int width, int height, int fps, Action<Bitmap> send)
    {
        var source = new FileBenchmarkVideoSource(); var pace = Stopwatch.StartNew();
        int clock = 0; double due = 0;
        using (var idle = new Bitmap(width, height, PixelFormat.Format24bppRgb))
        {
            using (Graphics graphics = Graphics.FromImage(idle)) graphics.Clear(Color.Black);
            while (!stop.WaitOne(0) && !harness.Failed && !harness.Runner.TailComplete)
            {
                FileBenchmarkCommand command = harness.Runner.CurrentCommand;
                bool announcement = (clock++ & Int32.MaxValue) % 31 < 4, data = false;
                Bitmap encoded = null, resized = null;
                try
                {
                    if (announcement)
                    {
                        byte[] bytes = FileBenchmarkProtocol.Command(command);
                        encoded = VisualProtocol.EncodeFrame(command.Session, 0, 1, bytes.Length, bytes, VideoProfile.Compatible);
                    }
                    else if (command.Phase == FileBenchmarkPhase.Start) { encoded = source.Next(command, fps); data = true; }
                    Bitmap frame = encoded ?? idle;
                    if (frame.Width != width || frame.Height != height) resized = Fit(frame, width, height);
                    send(resized ?? frame);
                    lock (harness.Gate)
                    {
                        Counts counts = harness.ProfileCounts[command.Profile]; counts.SourceFrames++;
                        if (announcement) counts.Commands++; if (data) counts.DataFrames++;
                    }
                }
                finally { if (resized != null) resized.Dispose(); if (encoded != null) encoded.Dispose(); }
                due += 1000.0 / fps;
                int delay = (int)(due - pace.Elapsed.TotalMilliseconds);
                if (delay > 0 && stop.WaitOne(delay)) break;
                if (due < pace.Elapsed.TotalMilliseconds - 250) due = pace.Elapsed.TotalMilliseconds;
            }
        }
    }

    private static Bitmap Fit(Bitmap source, int width, int height)
    {
        var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        try
        {
            using (Graphics graphics = Graphics.FromImage(target))
            {
                graphics.Clear(Color.Black); graphics.InterpolationMode = InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode = PixelOffsetMode.Half;
                double scale = Math.Min(width / (double)source.Width, height / (double)source.Height);
                int w = Math.Max(1, (int)Math.Round(source.Width * scale)), h = Math.Max(1, (int)Math.Round(source.Height * scale));
                graphics.DrawImage(source, new Rectangle((width - w) / 2, (height - h) / 2, w, h), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
            }
            return target;
        }
        catch { target.Dispose(); throw; }
    }
    private static bool Equal(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }
    private static void Write(string path, Dictionary<string, object> value)
    {
        string json = new JavaScriptSerializer().Serialize(value);
        if (path != null) File.WriteAllText(Path.GetFullPath(path), json + Environment.NewLine, new UTF8Encoding(false));
        Console.WriteLine(json);
    }
}
