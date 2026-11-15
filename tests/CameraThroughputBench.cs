using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using HorizonClient;

// Explicit local UVC benchmark. It never reads or writes the clipboard, installs
// drivers, changes a camera mode, or opens a default/physical camera.
internal static class CameraThroughputBench
{
    private sealed class Options
    {
        internal string Key, Camera, Output;
        internal VideoProfile Profile = VideoProfile.Dense;
        internal int Quality = 90, Seconds = 20, Port = 13261;
    }

    private sealed class Epoch
    {
        internal readonly bool[] Seen;
        internal int Received;
        internal Epoch(int count) { Seen = new bool[count]; }
    }

    private sealed class Measurements : IDisposable
    {
        internal readonly object Gate = new object();
        internal readonly byte[] Expected;
        internal readonly VideoProfile Profile;
        internal readonly int ChunkBytes, PacketCount;
        internal readonly Dictionary<Guid, Epoch> Epochs = new Dictionary<Guid, Epoch>();
        private readonly SHA256 actualHash = SHA256.Create(), expectedHash = SHA256.Create();
        private long began, deadline;
        private bool active, finalized;
        internal long SentFrames, SentBytes, CapturedFrames, DecodeCalls, DecodedFrames, VerifiedFrames;
        internal long CrcValidPackets, VerifiedPackets, VerifiedBytes, Duplicates, ForeignPackets, InvalidMetadata, MismatchedPackets, CompleteEpochs;
        internal long EncodeTicks, SendTicks, DecodeTicks;
        internal double Seconds;
        internal Exception Error;
        internal string ActualDigest, ExpectedDigest;

        internal Measurements(byte[] expected, VideoProfile profile)
        {
            Expected = expected; Profile = profile; ChunkBytes = VisualProtocol.GetChunkSize(profile);
            PacketCount = Math.Max(1, (expected.Length + ChunkBytes - 1) / ChunkBytes);
        }

        internal void Begin(int seconds)
        {
            lock (Gate) { began = Stopwatch.GetTimestamp(); deadline = began + (long)seconds * Stopwatch.Frequency; active = true; }
        }

        internal bool Running
        {
            get { lock (Gate) return active && Error == null && Stopwatch.GetTimestamp() < deadline; }
        }

        internal void Register(Guid id) { lock (Gate) Epochs.Add(id, new Epoch(PacketCount)); }

        internal void Fail(Exception error) { lock (Gate) if (!finalized && Error == null) Error = error; }

        internal void Sent(int bytes, long encoded, long transmitted)
        {
            lock (Gate)
            {
                if (!active || Stopwatch.GetTimestamp() >= deadline) return;
                SentFrames++; SentBytes += bytes; EncodeTicks += encoded; SendTicks += transmitted;
            }
        }

        internal void Capture(Bitmap frame)
        {
            lock (Gate)
            {
                if (!active || Stopwatch.GetTimestamp() >= deadline) return;
                CapturedFrames++;
            }
            long started = Stopwatch.GetTimestamp();
            Packet[] packets;
            try { packets = VisualProtocol.DecodePackets(frame); }
            catch (Exception error) { Fail(error); return; }
            lock (Gate)
            {
                if (!active || Stopwatch.GetTimestamp() >= deadline) return;
                DecodeTicks += Stopwatch.GetTimestamp() - started;
                DecodeCalls++;
                if (packets.Length != 0) DecodedFrames++;
                long before = VerifiedPackets;
                foreach (Packet packet in packets) Accept(packet);
                if (VerifiedPackets != before) VerifiedFrames++;
            }
        }

        // Called under Gate by Capture; also exercised without device I/O by --self-test.
        internal void Accept(Packet packet)
        {
            if (!active || finalized) return;
            CrcValidPackets++;
            Epoch epoch;
            if (packet == null || !Epochs.TryGetValue(packet.TransferId, out epoch)) { ForeignPackets++; return; }
            if (packet.Profile != Profile || packet.ChunkSize != ChunkBytes || packet.Count != PacketCount ||
                packet.TotalLength != Expected.Length || packet.Index < 0 || packet.Index >= PacketCount || packet.Data == null ||
                packet.Data.Length != Math.Min(ChunkBytes, Expected.Length - packet.Index * ChunkBytes))
            { InvalidMetadata++; return; }
            int offset = packet.Index * ChunkBytes;
            for (int index = 0; index < packet.Data.Length; index++)
                if (packet.Data[index] != Expected[offset + index]) { MismatchedPackets++; return; }
            if (epoch.Seen[packet.Index]) { Duplicates++; return; }
            epoch.Seen[packet.Index] = true;
            epoch.Received++;
            if (epoch.Received == PacketCount) CompleteEpochs++;
            actualHash.TransformBlock(packet.Data, 0, packet.Data.Length, packet.Data, 0);
            expectedHash.TransformBlock(Expected, offset, packet.Data.Length, Expected, offset);
            VerifiedPackets++; VerifiedBytes += packet.Data.Length;
        }

        internal void Finish()
        {
            lock (Gate)
            {
                if (finalized) return;
                active = false;
                Seconds = Math.Max(0, (Math.Min(Stopwatch.GetTimestamp(), deadline) - began) / (double)Stopwatch.Frequency);
                actualHash.TransformFinalBlock(new byte[0], 0, 0);
                expectedHash.TransformFinalBlock(new byte[0], 0, 0);
                ActualDigest = Hex(actualHash.Hash); ExpectedDigest = Hex(expectedHash.Hash); finalized = true;
            }
        }

        public void Dispose() { actualHash.Dispose(); expectedHash.Dispose(); }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            try { SelfTest(); Console.WriteLine("Camera throughput tool self-tests passed; no camera or clipboard opened."); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        Options options;
        try { options = Parse(args); }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine("Usage: CameraThroughputBench --key PATH --camera EXACT_MONIKER --profile PROFILE --quality 90|100 --seconds 20 [--port 13261] [--output JSON]");
            return 2;
        }
        try { return Run(options); }
        catch (Exception error)
        {
            WriteResult(options.Output, new Dictionary<string, object> { { "status", "error" }, { "error", error.ToString() }, { "profile", options.Profile.ToString() }, { "scope", "local UVC only; no Horizon hop or clipboard" } });
            return 1;
        }
    }

    private static int Run(Options options)
    {
        CameraDevice selected = null;
        foreach (CameraDevice device in CameraDevices.List())
            if (String.Equals(device.Id, options.Camera, StringComparison.Ordinal))
            { if (selected != null) throw new InvalidOperationException("The exact camera moniker is ambiguous."); selected = device; }
        if (selected == null) throw new InvalidOperationException("The exact project-owned camera moniker is absent.");
        byte[] expected = new byte[VisualProtocol.MaxTransferBytes];
        using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(expected);
        string sourceDigest;
        using (SHA256 hash = SHA256.Create()) sourceDigest = Hex(hash.ComputeHash(expected));
        var measurement = new Measurements(expected, options.Profile);
        CameraCapture capture = null;
        UvcCamera camera = null;
        Thread producer = null;
        bool joined = true;
        int captureWidth = 0, captureHeight = 0, nativeWidth = 0, nativeHeight = 0, fps = 0;
        bool raw = false;
        try
        {
            // The software camera advertises one mode. Ask for its largest native
            // size without assuming the source profile and capture size are equal.
            capture = new CameraCapture(options.Camera, measurement.Capture, 32768, 32768);
            capture.OnError += measurement.Fail;
            capture.Start();
            captureWidth = capture.Width; captureHeight = capture.Height;
            camera = new UvcCamera("127.0.0.1", options.Port, options.Key);
            if (camera.IsH264Probe) throw new InvalidOperationException("Use the compressed H.264/SEI capture probe for the fixed H.264 camera mode.");
            camera.JpegQuality = options.Quality;
            nativeWidth = camera.Width; nativeHeight = camera.Height; fps = camera.FramesPerSecond; raw = camera.IsUncompressed;
            UvcCamera sender = camera;
            measurement.Begin(options.Seconds);
            producer = new Thread(delegate()
            {
                try { Produce(sender, measurement, options.Profile); }
                catch (Exception error) { measurement.Fail(error); }
            }) { IsBackground = true, Name = "Synthetic UVC throughput producer" };
            producer.Start();
            while (measurement.Running) Thread.Sleep(10);
        }
        finally
        {
            measurement.Finish();
            if (producer != null) joined = producer.Join(15000);
            if (capture != null) capture.Dispose();
            if (camera != null) camera.Dispose();
        }
        Dictionary<string, object> result;
        lock (measurement.Gate)
        {
            double seconds = Math.Max(0.000001, measurement.Seconds);
            result = new Dictionary<string, object> {
                { "status", measurement.Error == null && joined ? "completed" : "error" },
                { "scope", "local UVC only; no Horizon hop or clipboard" },
                { "camera_name", selected.Name }, { "camera_moniker", selected.Id },
                { "profile", options.Profile.ToString() }, { "jpeg_quality", options.Quality }, { "native_format", raw ? "YUY2" : "MJPEG" },
                { "source_width", VisualProtocol.GetWidth(options.Profile) }, { "source_height", VisualProtocol.GetHeight(options.Profile) },
                { "native_width", nativeWidth }, { "native_height", nativeHeight }, { "native_fps", fps },
                { "capture_width", captureWidth }, { "capture_height", captureHeight },
                { "seconds", measurement.Seconds }, { "requested_seconds", options.Seconds },
                { "sender_fps", measurement.SentFrames / seconds }, { "capture_fps", measurement.CapturedFrames / seconds },
                { "capture_fps_scope", "serial frame-handler deliveries; the capture latest-frame queue may drop frames while decoding" },
                { "verified_mbps", measurement.VerifiedBytes * 8.0 / seconds / 1000000.0 },
                { "sender_payload_mbps", measurement.SentBytes * 8.0 / seconds / 1000000.0 },
                { "source_payload_sha256", sourceDigest }, { "verified_expected_sha256", measurement.ExpectedDigest }, { "verified_actual_sha256", measurement.ActualDigest },
                { "exact_digest", measurement.VerifiedPackets > 0 && measurement.MismatchedPackets == 0 && measurement.ExpectedDigest == measurement.ActualDigest },
                { "sent_frames", measurement.SentFrames }, { "sent_payload_bytes", measurement.SentBytes },
                { "captured_frames", measurement.CapturedFrames }, { "decode_calls", measurement.DecodeCalls }, { "decoded_frames", measurement.DecodedFrames }, { "frames_with_new_data", measurement.VerifiedFrames },
                { "crc_valid_packets", measurement.CrcValidPackets }, { "verified_unique_packets", measurement.VerifiedPackets }, { "verified_unique_bytes", measurement.VerifiedBytes },
                { "duplicate_packets", measurement.Duplicates }, { "foreign_packets", measurement.ForeignPackets }, { "invalid_metadata_packets", measurement.InvalidMetadata },
                { "mismatched_packets", measurement.MismatchedPackets }, { "generated_epochs", measurement.Epochs.Count }, { "complete_epochs", measurement.CompleteEpochs },
                { "source_payload_bytes", expected.Length }, { "profile_bytes_per_frame", VisualProtocol.GetFramePayloadBytes(options.Profile) },
                { "mean_encode_scale_ms", Milliseconds(measurement.EncodeTicks, measurement.SentFrames) },
                { "mean_native_send_ms", Milliseconds(measurement.SendTicks, measurement.SentFrames) },
                { "mean_decode_ms", Milliseconds(measurement.DecodeTicks, measurement.DecodeCalls) },
                { "producer_joined", joined }, { "error", measurement.Error == null ? null : measurement.Error.ToString() }
            };
        }
        WriteResult(options.Output, result);
        if (joined) measurement.Dispose();
        return (string)result["status"] == "completed" ? 0 : 1;
    }

    private static void Produce(UvcCamera camera, Measurements measurement, VideoProfile profile)
    {
        int slots = VisualProtocol.GetPacketsPerFrame(profile), index = 0;
        Guid id = Guid.NewGuid(); measurement.Register(id);
        var pacing = Stopwatch.StartNew();
        double next = 0;
        while (measurement.Running)
        {
            long started = Stopwatch.GetTimestamp();
            using (Bitmap source = VisualProtocol.EncodePacketFrame(id, index, measurement.Expected, profile))
            using (Bitmap scaled = source.Width == camera.Width && source.Height == camera.Height ? null : Fit(source, camera.Width, camera.Height))
            {
                long encoded = Stopwatch.GetTimestamp() - started;
                if (!measurement.Running) break;
                long sending = Stopwatch.GetTimestamp();
                camera.Send(scaled ?? source);
                int bytes = Math.Min(slots * measurement.ChunkBytes, measurement.Expected.Length - index * measurement.ChunkBytes);
                measurement.Sent(bytes, encoded, Stopwatch.GetTimestamp() - sending);
            }
            index += slots;
            if (index >= measurement.PacketCount) { index = 0; id = Guid.NewGuid(); measurement.Register(id); }
            next += 1.0 / camera.FramesPerSecond;
            while (measurement.Running && pacing.Elapsed.TotalSeconds < next) Thread.Sleep(1);
            if (next < pacing.Elapsed.TotalSeconds - 0.25) next = pacing.Elapsed.TotalSeconds;
        }
    }

    private static Bitmap Fit(Bitmap source, int width, int height)
    {
        Bitmap target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        try
        {
            using (Graphics graphics = Graphics.FromImage(target))
            {
                graphics.Clear(Color.Black); graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                double scale = Math.Min(width / (double)source.Width, height / (double)source.Height);
                int w = Math.Max(1, (int)Math.Round(source.Width * scale)), h = Math.Max(1, (int)Math.Round(source.Height * scale));
                graphics.DrawImage(source, new Rectangle((width - w) / 2, (height - h) / 2, w, h), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
            }
            return target;
        }
        catch { target.Dispose(); throw; }
    }

    private static Options Parse(string[] args)
    {
        var result = new Options(); var seen = new HashSet<string>(StringComparer.Ordinal);
        if (args.Length == 0 || args.Length % 2 != 0) throw new ArgumentException("Pass named option/value pairs.");
        for (int index = 0; index < args.Length; index += 2)
        {
            string option = args[index], value = args[index + 1];
            if (!seen.Add(option)) throw new ArgumentException("Duplicate option: " + option);
            switch (option)
            {
                case "--key": result.Key = value; break;
                case "--camera": result.Camera = value; break;
                case "--output": result.Output = value; break;
                case "--profile":
                    if (!Enum.TryParse<VideoProfile>(ProfileAlias(value), true, out result.Profile) || !VisualProtocol.IsDefinedProfile(result.Profile)) throw new ArgumentException("Unknown visual profile.");
                    break;
                case "--quality": result.Quality = Int32.Parse(value, CultureInfo.InvariantCulture); break;
                case "--seconds": result.Seconds = Int32.Parse(value, CultureInfo.InvariantCulture); break;
                case "--port": result.Port = Int32.Parse(value, CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException("Unknown option: " + option);
            }
        }
        if (String.IsNullOrWhiteSpace(result.Key) || String.IsNullOrWhiteSpace(result.Camera)) throw new ArgumentException("An explicit key path and exact camera moniker are required.");
        if (result.Camera.IndexOf("vid_1209", StringComparison.OrdinalIgnoreCase) < 0 || result.Camera.IndexOf("pid_f002", StringComparison.OrdinalIgnoreCase) < 0) throw new ArgumentException("The benchmark opens only the explicitly named project-owned USB camera (VID1209/PIDF002).");
        if (result.Quality != 90 && result.Quality != 100) throw new ArgumentException("Choose JPEG quality 90 or 100.");
        if (result.Seconds < 1 || result.Seconds > 300) throw new ArgumentException("Seconds must be within 1..300.");
        if (result.Port < 1 || result.Port > 65535) throw new ArgumentException("Invalid local ingress port.");
        return result;
    }

    private static string ProfileAlias(string value)
    {
        switch (value.ToLowerInvariant())
        {
            case "720dense": return "Dense";
            case "720max": case "8ksource720": return "HighDensity720";
            case "1080robust": return "HighDefinitionRobust";
            case "1080max": return "HighDensity1080";
            case "4k1": return "Maximum4K";
            case "4k2": return "Ultra4K";
            case "4k4": return "Ultra4KRobust";
            default: return value;
        }
    }

    private static string Hex(byte[] value) { return BitConverter.ToString(value).Replace("-", "").ToLowerInvariant(); }
    private static double Milliseconds(long ticks, long count) { return count == 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency / count; }
    private static void WriteResult(string path, Dictionary<string, object> value)
    {
        string json = new JavaScriptSerializer().Serialize(value);
        if (!String.IsNullOrWhiteSpace(path)) File.WriteAllText(Path.GetFullPath(path), json + Environment.NewLine, new UTF8Encoding(false));
        Console.WriteLine(json);
    }

    private static void SelfTest()
    {
        byte[] source = new byte[7000]; new Random(75).NextBytes(source);
        using (var state = new Measurements(source, VideoProfile.Dense))
        {
            Guid id = Guid.NewGuid(); state.Register(id); state.Begin(20);
            for (int index = 0; index < 2; index++)
            {
                byte[] data = new byte[Math.Min(6144, source.Length - index * 6144)]; Buffer.BlockCopy(source, index * 6144, data, 0, data.Length);
                var packet = new Packet { TransferId = id, Profile = VideoProfile.Dense, Index = index, Count = 2, TotalLength = source.Length, ChunkSize = 6144, Data = data };
                state.Accept(packet); state.Accept(packet);
            }
            state.Finish();
            if (state.VerifiedBytes != 7000 || state.VerifiedPackets != 2 || state.Duplicates != 2 || state.CompleteEpochs != 1 || state.ActualDigest != state.ExpectedDigest) throw new Exception("Unique chunk counting/digest failed.");
            using (SHA256 hash = SHA256.Create()) if (Hex(hash.ComputeHash(source)) != state.ActualDigest) throw new Exception("Complete stream digest differs from source.");
            state.Accept(new Packet()); if (state.ForeignPackets != 0) throw new Exception("A finished window accepted late packets.");
        }
        using (var state = new Measurements(source, VideoProfile.Dense))
        {
            Guid id = Guid.NewGuid(); state.Register(id); state.Begin(20);
            state.Accept(new Packet { TransferId = Guid.NewGuid() });
            state.Accept(new Packet { TransferId = id });
            state.Accept(new Packet { TransferId = id, Profile = VideoProfile.Dense, Index = 0, Count = 2, TotalLength = source.Length, ChunkSize = 6144, Data = new byte[6144] });
            state.Finish();
            if (state.ForeignPackets != 1 || state.InvalidMetadata != 1 || state.MismatchedPackets != 1 || state.VerifiedBytes != 0) throw new Exception("Invalid or incorrect data contributed to throughput.");
        }
        Options options = Parse(new[] { "--key", "fixture.key", "--camera", "@device:pnp:vid_1209&pid_f002#fixture", "--profile", "4k4", "--quality", "100", "--seconds", "3" });
        if (options.Profile != VideoProfile.Ultra4KRobust || options.Seconds != 3 || options.Quality != 100) throw new Exception("CLI parsing failed.");
        bool rejected = false;
        try { Parse(new[] { "--key", "fixture", "--camera", "physical-default" }); } catch (ArgumentException) { rejected = true; }
        if (!rejected) throw new Exception("Implicit/physical camera selection was accepted.");
        using (Bitmap sourceFrame = VisualProtocol.EncodePacketFrame(Guid.NewGuid(), 0, new byte[6144], VideoProfile.Dense))
        using (Bitmap scaled = Fit(sourceFrame, 1920, 1080))
            if (VisualProtocol.DecodePackets(scaled).Length != 1) throw new Exception("Nearest-neighbor native mode scaling failed.");
    }
}
