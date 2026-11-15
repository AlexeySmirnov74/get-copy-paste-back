using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using HorizonClient;

// Explicit opt-in real Horizon forward-video/reverse-audio probe. It only
// connects to an already-running project backend and captures render loopback.
// No camera/driver configuration, local camera capture or clipboard access.
internal static class HorizonLinkProbe
{
    private sealed class State
    {
        internal readonly object Gate = new object();
        internal readonly Guid Session = Guid.NewGuid();
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal FileBenchmarkRunner Runner;
        internal Exception Error;
        internal int Frames, HelloFrames, CommandFrames, DataFrames, Ready, Completed, Reports, ClipboardAttempts;
        internal long VideoBytes;
        internal readonly Dictionary<string, int> EndpointReports = new Dictionary<string, int>();
        internal void Fail(Exception error) { lock (Gate) { if (Error == null) Error = error; } DiagnosticLog.Error("horizon_probe.error", error); }
        internal bool Failed { get { lock (Gate) return Error != null; } }
    }
    private sealed class NullPaste : IServerDeferredPaste { public void Complete() { } public void Cancel() { } public void Dispose() { } }
    private sealed class NullTimer : IServerPublicationTimer { public void Start() { } public void Stop() { } public void Dispose() { } }
    private sealed class ProbeServices : ServerAudioBridgeServices
    {
        private readonly State state;
        internal ProbeServices(State state) { this.state = state; NativeModemsEnabled = true; }
        internal override IServerDeferredPaste CreatePaste(Control owner, Func<bool> receiving) { return new NullPaste(); }
        internal override IServerPublicationTimer CreateTimer(Action tick) { return new NullTimer(); }
        private void Forbidden() { Interlocked.Increment(ref state.ClipboardAttempts); var error = new InvalidOperationException("Probe must never decode or publish clipboard data."); state.Fail(error); throw error; }
        internal override DecodedPayload Decode(byte[] encoded) { Forbidden(); return null; }
        internal override void PrepareFiles(DecodedPayload payload, string root, Guid id) { Forbidden(); }
        internal override void Publish(DecodedPayload payload, string root, Guid id) { Forbidden(); }
    }
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 3 || args.Length > 4 || args[0] != "--run" || String.IsNullOrWhiteSpace(args[1]) || String.IsNullOrWhiteSpace(args[2]))
        { Console.Error.WriteLine("Explicit opt-in usage: HorizonLinkProbe.exe --run KEY_FILE OUTPUT_JSON [EXACT_RENDER_ENDPOINT_ID|--all-render]"); return 2; }
        string output = Path.GetFullPath(args[2]);
        string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "horizon-link-probe.debug.log");
        DiagnosticLog.Initialize(log);
        try { return Run(args[1], output, args.Length == 4 ? args[3] : null); }
        catch (Exception error)
        {
            DiagnosticLog.Error("horizon_probe.failed", error);
            Write(output, new Dictionary<string, object> { { "passed", false }, { "status", "error" }, { "error", error.ToString() }, { "scope", "real Horizon video and reverse render-loopback audio; synthetic 1KiB data; no clipboard/device changes" } });
            return 1;
        }
        finally { DiagnosticLog.Write("horizon_probe.stopped"); DiagnosticLog.Shutdown(); }
    }
    private static int Run(string key, string output, string endpoint)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var state = new State();
        var endpoints = new List<string>();
        if (endpoint == "--all-render")
        {
            foreach (AudioEndpoint device in AudioCapture.EnumerateEndpoints(true)) endpoints.Add(device.Id);
            if (endpoints.Count == 0) throw new IOException("No active render endpoints.");
        }
        else endpoints.Add(endpoint);
        DiagnosticLog.Write("horizon_probe.started", "session", state.Session, "render_endpoints", endpoints.Count,
            "scope", "real Horizon video/reverse audio; Compatible 1024-byte synthetic file; no clipboard/camera/driver changes");
        var bridges = new List<ServerAudioBridge>();
        using (var owner = new Control())
        using (var stop = new ManualResetEvent(false))
        {
            IntPtr handle = owner.Handle;
            ulong seed = unchecked((ulong)DateTime.UtcNow.Ticks);
            state.Runner = new FileBenchmarkRunner(state.Session, new[] { VideoProfile.Compatible }, 1024, seed, 40000);
            foreach (string id in endpoints)
            {
                string captureId = id;
                string label = id ?? "default";
                state.EndpointReports[label] = 0;
                var bridge = new ServerAudioBridge(owner, state.Session,
                    delegate(Guid ignored) { state.Fail(new InvalidOperationException("Unexpected reverse clipboard publication.")); },
                    delegate(string status) { DiagnosticLog.Write("horizon_probe.audio_status", "endpoint", label, "status", status); },
                    delegate(BridgeReport ignored) { DiagnosticLog.Write("horizon_probe.legacy_report", "endpoint", label); }, new ProbeServices(state));
                bridge.SetBenchmarkMode(true);
                bridge.FileReportReceived = delegate(FileBenchmarkReport report)
                {
                    state.Reports++; state.EndpointReports[label]++;
                    if (report.Status == FileBenchmarkStatus.Ready) state.Ready++;
                    if (report.Status == FileBenchmarkStatus.Completed) state.Completed++;
                    DiagnosticLog.Write("horizon_probe.remote_ack", "endpoint", label, "status", report.Status, "bytes", report.ReceivedBytes,
                        "frames", report.Frames, "width", report.FrameWidth, "height", report.FrameHeight);
                    state.Runner.Receive(report);
                };
                bridges.Add(bridge); bridge.Start(captureId);
            }
            Thread producer = new Thread(delegate() { try { Produce(state, stop, key); } catch (Exception error) { state.Fail(error); } }) { IsBackground = true, Name = "Actual Horizon 1KiB probe sender" };
            bool joined = false, timedOut = false;
            producer.Start();
            try
            {
                while (!state.Failed && !state.Runner.TailComplete)
                {
                    if (state.Clock.ElapsedMilliseconds >= 65000) { timedOut = true; state.Runner.Cancel(); break; }
                    Application.DoEvents(); state.Runner.Tick(); Thread.Sleep(5);
                }
            }
            finally
            {
                stop.Set(); if (!state.Runner.Finished) state.Runner.Cancel();
                var wait = Stopwatch.StartNew();
                while (producer.IsAlive && wait.ElapsedMilliseconds < 10000) { Application.DoEvents(); producer.Join(10); }
                joined = !producer.IsAlive;
                foreach (ServerAudioBridge bridge in bridges) bridge.Dispose();
                var drain = Stopwatch.StartNew();
                while (drain.ElapsedMilliseconds < 200) { Application.DoEvents(); Thread.Sleep(5); }
            }
            ProfileTestResult row = state.Runner.Snapshot()[0];
            bool passed = !state.Failed && !timedOut && joined && state.ClipboardAttempts == 0 && row.State == ProfileTestState.Passed &&
                row.ReceivedBytes == 1024 && state.Ready > 0 && state.Completed > 0;
            Write(output, new Dictionary<string, object> {
                { "passed", passed }, { "status", passed ? "passed" : "failed" }, { "session", state.Session.ToString() },
                { "profile", "Compatible" }, { "file_bytes", 1024 }, { "received_bytes", row.ReceivedBytes }, { "remote_ready_acks", state.Ready },
                { "remote_completed_acks", state.Completed }, { "remote_audio_reports", state.Reports }, { "endpoint_reports", state.EndpointReports },
                { "client_frame_width", row.Width }, { "client_frame_height", row.Height }, { "client_frames", row.Frames },
                { "receive_milliseconds", row.ElapsedMilliseconds }, { "receive_mbps", row.MegabitsPerSecond }, { "result", row.State.ToString() },
                { "finish_reason", row.ServerFinishReason }, { "source_frames", state.Frames }, { "source_hello_frames", state.HelloFrames },
                { "source_command_frames", state.CommandFrames }, { "source_data_frames", state.DataFrames }, { "local_video_payload_bytes", state.VideoBytes },
                { "clipboard_attempts", state.ClipboardAttempts }, { "elapsed_milliseconds", state.Clock.ElapsedMilliseconds }, { "overall_timeout", timedOut },
                { "producer_joined", joined }, { "error", state.Error == null ? null : state.Error.ToString() },
                { "scope", "real Horizon forward video + reverse render-loopback audio; synthetic 1KiB data; no clipboard/device changes" }
            });
            return passed ? 0 : 1;
        }
    }
    private static void Produce(State state, ManualResetEvent stop, string key)
    {
        using (var camera = new UvcCamera("127.0.0.1", 13261, key))
        {
            if (camera.Width != 1280 || camera.Height != 720 || camera.FramesPerSecond != 30 || camera.IsUncompressed || camera.IsH264Probe)
                throw new IOException("Probe requires the already-configured 1280x720/30 MJPEG camera and never changes its format.");
            camera.JpegQuality = 100;
            DiagnosticLog.Write("horizon_probe.backend_connected", "width", camera.Width, "height", camera.Height, "fps", camera.FramesPerSecond);
            var source = new FileBenchmarkVideoSource();
            var pace = Stopwatch.StartNew(); double due = 0; int clock = 0;
            using (var idle = new Bitmap(camera.Width, camera.Height, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(idle)) graphics.Clear(Color.Black);
                while (!stop.WaitOne(0) && !state.Failed && !state.Runner.TailComplete)
                {
                    FileBenchmarkCommand command = state.Runner.CurrentCommand;
                    Bitmap encoded = null, resized = null;
                    try
                    {
                        int phase = clock++ % 31;
                        if (clock <= 15)
                        {
                            byte[] hello = BridgeControl.Hello(state.Session, Guid.Empty, Guid.Empty, 0, "OBSBOT Meet Flip");
                            encoded = VisualProtocol.EncodeFrame(state.Session, 0, 1, hello.Length, hello, VideoProfile.Compatible);
                            Interlocked.Increment(ref state.HelloFrames);
                        }
                        else if (phase < 4)
                        {
                            byte[] bytes = FileBenchmarkProtocol.Command(command);
                            encoded = VisualProtocol.EncodeFrame(state.Session, 0, 1, bytes.Length, bytes, VideoProfile.Compatible);
                            Interlocked.Increment(ref state.CommandFrames);
                        }
                        else if (command.Phase == FileBenchmarkPhase.Start) { encoded = source.Next(command, camera.FramesPerSecond); Interlocked.Increment(ref state.DataFrames); }
                        Bitmap frame = encoded ?? idle;
                        if (frame.Width != camera.Width || frame.Height != camera.Height) resized = Fit(frame, camera.Width, camera.Height);
                        camera.Send(resized ?? frame);
                        Interlocked.Increment(ref state.Frames); Interlocked.Exchange(ref state.VideoBytes, camera.VideoBytesSent);
                    }
                    finally { if (resized != null) resized.Dispose(); if (encoded != null) encoded.Dispose(); }
                    due += 1000.0 / camera.FramesPerSecond;
                    int delay = (int)(due - pace.Elapsed.TotalMilliseconds);
                    if (delay > 0 && stop.WaitOne(delay)) break;
                    if (due < pace.Elapsed.TotalMilliseconds - 250) due = pace.Elapsed.TotalMilliseconds;
                }
            }
        }
    }
    private static Bitmap Fit(Bitmap source, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        try
        {
            using (Graphics graphics = Graphics.FromImage(result))
            {
                graphics.Clear(Color.Black); graphics.InterpolationMode = InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode = PixelOffsetMode.Half;
                double scale = Math.Min(width / (double)source.Width, height / (double)source.Height);
                int w = Math.Max(1, (int)Math.Round(source.Width * scale)), h = Math.Max(1, (int)Math.Round(source.Height * scale));
                graphics.DrawImage(source, new Rectangle((width - w) / 2, (height - h) / 2, w, h), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    private static void Write(string path, Dictionary<string, object> result)
    {
        string json = new JavaScriptSerializer().Serialize(result);
        File.WriteAllText(path, json + Environment.NewLine, new System.Text.UTF8Encoding(false)); Console.WriteLine(json);
    }
}
