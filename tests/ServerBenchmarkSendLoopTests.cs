// Exercises the actual ServerForm producer against an authenticated synthetic
// loopback receiver. No camera, Windows audio endpoint or clipboard is opened.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

internal static class ServerBenchmarkSendLoopTests
{
    private const string TestKey = "public-server-producer-fixture-key-0123456789";
    private const ulong Seed = 0x123456789abcdef0UL;
    // Cases run sequentially and join both fixture threads before changing size.
    private static int Width, Height;
    private static int checks;

    [STAThread]
    private static int Main()
    {
        string keyPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(keyPath, TestKey, new UTF8Encoding(false));
            IdleAcknowledgementChanges();
            MeasureIdleDecode();
            RunIdleCase(keyPath);
            RunCase(keyPath, false, 640, 480, VideoProfile.Compatible);
            RunCase(keyPath, true, 640, 480, VideoProfile.Compatible);
            RunCase(keyPath, true, 1920, 1080, VideoProfile.HighDefinitionRobust);
            RunClipboardCase(keyPath, 10240); RunClipboardCase(keyPath, 65536);
            Console.WriteLine("Server benchmark send-loop checks passed: " + checks + ". Actual hidden ServerForm producer; ordinary/finished-test-tail idle transitions, authenticated JPEG/YUY2 benchmark and Fast10/64KiB clipboard loopback; every first-pass packet is contiguous across control slots. No camera, clipboard or audio access.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { File.Delete(keyPath); }
    }

    private static void IdleAcknowledgementChanges()
    {
        Guid session = Guid.NewGuid(), acknowledged = Guid.NewGuid();
        using (var form = new ServerForm(null, false))
        {
            Set(form, "bridgeSession", session);
            MethodInfo idle = typeof(ServerForm).GetMethod("IdleFrame", BindingFlags.Instance | BindingFlags.NonPublic);
            lock (Get(form, "stateLock")) Set(form, "acknowledgedAudio", acknowledged);
            using (var frame = (Bitmap)idle.Invoke(form, new object[] { true }))
            {
                Packet[] packets = VisualProtocol.DecodePackets(frame); Guid received;
                Check(packets.Length == 1 && packets[0].TransferId == session &&
                    BridgeControl.TryAcknowledge(packets[0], out received) && received == acknowledged,
                    "An ordinary idle frame must carry the exact reverse acknowledgement instead of a stale Hello.");
            }
            lock (Get(form, "stateLock")) Set(form, "acknowledgedAudio", Guid.Empty);
            using (var frame = (Bitmap)idle.Invoke(form, new object[] { true }))
            {
                Packet[] packets = VisualProtocol.DecodePackets(frame); BridgeHello hello;
                Check(packets.Length == 1 && BridgeControl.TryHello(packets[0], out hello) && hello.Session == session,
                    "Clearing the acknowledgement restores the exact ordinary Hello.");
            }
        }
    }

    private static void MeasureIdleDecode()
    {
        Guid session = Guid.NewGuid();
        using (var form = new ServerForm(null, false))
        {
            Set(form, "bridgeSession", session); Set(form, "activeCameraName", "Synthetic idle fixture");
            MethodInfo idle = typeof(ServerForm).GetMethod("IdleFrame", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo resize = typeof(ServerForm).GetMethod("ToCameraSize", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (int width in new[] { 1280, 1920 })
            {
                int height = width * 9 / 16;
                foreach (bool control in new[] { false, true })
                using (var frame = (Bitmap)resize.Invoke(null, new object[] { idle.Invoke(form, new object[] { control }), width, height }))
                {
                    Packet[] first = VisualProtocol.DecodePackets(frame);
                    BridgeHello hello;
                    Check(control ? first.Length == 1 && BridgeControl.TryHello(first[0], out hello) && hello.Session == session : first.Length == 0,
                        "The production idle frame is either the original neutral picture or an exact CRC-verified Hello.");
                    const int iterations = 24;
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
                    TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime;
                    var clock = Stopwatch.StartNew();
                    for (int i = 0; i < iterations; i++)
                    {
                        Packet[] packets = VisualProtocol.DecodePackets(frame, VideoProfile.HighDefinitionRobust);
                        Check(packets.Length == (control ? 1 : 0), "A stale preferred profile preserves exact idle control recognition.");
                    }
                    clock.Stop();
                    double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds / iterations;
                    Console.WriteLine(String.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Idle decode {0}x{1} {2}: mean_wall_ms={3:F3}; mean_cpu_ms={4:F3}; gen0/1/2={5}/{6}/{7}; frames={8}.",
                        width, height, control ? "cached_hello" : "painted_picture", clock.Elapsed.TotalMilliseconds / iterations, cpuMs,
                        GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2, iterations));
                }
            }
        }
    }

    private static void RunIdleCase(string keyPath)
    {
        Width = 1280; Height = 720;
        Guid session = Guid.NewGuid(); long testClock = 0;
        var run = new FileBenchmarkRunner(session, new[] { VideoProfile.Fast }, 1024, Seed, 30000, delegate { return Interlocked.Read(ref testClock); });
        using (var backend = new Backend(true, session, run.CurrentCommand.Test, VideoProfile.Fast))
        using (var stop = new ManualResetEvent(false))
        using (var form = new ServerForm(null, false, delegate(string ignored) { return new UvcCamera("127.0.0.1", backend.Port, keyPath); }))
        {
            IntPtr unused = form.Handle;
            Set(form, "bridgeSession", session); Set(form, "activeCameraName", "Synthetic idle fixture");
            MethodInfo sendLoop = typeof(ServerForm).GetMethod("SendLoop", BindingFlags.Instance | BindingFlags.NonPublic);
            Exception producerError = null;
            var producer = new Thread(delegate()
            {
                try { sendLoop.Invoke(form, new object[] { keyPath, stop }); }
                catch (TargetInvocationException error) { producerError = error.InnerException ?? error; }
                catch (Exception error) { producerError = error; }
            }) { IsBackground = true, Name = "Synthetic idle transition fixture" };
            Action<Func<bool>> wait = delegate(Func<bool> condition)
            {
                var clock = Stopwatch.StartNew();
                while (!condition() && clock.ElapsedMilliseconds < 2500)
                {
                    Application.DoEvents();
                    if (producerError != null) throw producerError;
                    if (backend.Error != null) throw new Exception("Synthetic idle backend failed.", backend.Error);
                    Thread.Sleep(10);
                }
                Check(condition(), "The actual producer reaches the expected ordinary or finished-test-tail idle state.");
            };
            producer.Start();
            try
            {
                wait(delegate { return backend.IdleHelloFrames >= 3; });
                object stateLock = Get(form, "stateLock");
                foreach (Guid acknowledged in new[] { Guid.NewGuid(), Guid.NewGuid() })
                {
                    lock (stateLock) Set(form, "acknowledgedAudio", acknowledged);
                    wait(delegate { return backend.AcknowledgementCount(acknowledged, false) >= 7 && backend.AcknowledgementCount(acknowledged, true) >= 3; });
                }
                int helloBeforeReset = backend.IdleHelloFrames;
                lock (stateLock) Set(form, "acknowledgedAudio", Guid.Empty);
                wait(delegate { return backend.IdleHelloFrames >= helloBeforeReset + 3; });
                run.Cancel(); lock (stateLock) Set(form, "speedTest", run);
                wait(delegate { return backend.PictureIdleFrames >= 3; });
                int idleBefore = backend.IdleHelloFrames, picturesBefore = backend.PictureIdleFrames;
                wait(delegate { return backend.PictureIdleFrames >= picturesBefore + 3; });
                Check(backend.IdleHelloFrames <= idleBefore + 1, "A finished benchmark retains the neutral picture during its report-retry tail.");
                Interlocked.Exchange(ref testClock, 3000);
                wait(delegate { return backend.IdleHelloFrames >= idleBefore + 4; });
                Check(run.TailComplete, "Protocol idle resumes only after the real runner tail completes.");
            }
            finally
            {
                stop.Set(); bool joined = producer.Join(2500);
                if (!joined) { backend.Dispose(); joined = producer.Join(1500); }
                Check(joined, "The idle transition producer stops and releases its synthetic socket.");
                Application.DoEvents(); if (producerError != null) throw producerError;
            }
        }
    }

    private static void RunClipboardCase(string keyPath, int length)
    {
        Width = 1280; Height = 720;
        Guid session = Guid.NewGuid(), transfer = Guid.NewGuid();
        var payload = new byte[length]; new Random(10240).NextBytes(payload);
        using (var backend = new Backend(true, session, transfer, VideoProfile.Fast, payload))
        using (var stop = new ManualResetEvent(false))
        using (var form = new ServerForm(null, false, delegate(string ignored) { return new UvcCamera("127.0.0.1", backend.Port, keyPath); }))
        {
            IntPtr unused = form.Handle;
            Guid reverseAcknowledgement = Guid.NewGuid();
            backend.FrameObserved = delegate(ulong sequence)
            {
                if (sequence == 6) lock (Get(form, "stateLock")) Set(form, "acknowledgedAudio", reverseAcknowledgement);
            };
            Set(form, "bridgeSession", session); Set(form, "activeCameraName", "Synthetic clipboard producer fixture");
            Set(form, "current", payload); Set(form, "transferId", transfer);
            Set(form, "currentProfile", VideoProfile.Fast); Set(form, "selectedProfile", VideoProfile.Fast);
            MethodInfo sendLoop = typeof(ServerForm).GetMethod("SendLoop", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(sendLoop != null, "The clipboard regression invokes the actual production SendLoop.");
            Exception producerError = null;
            var producer = new Thread(delegate()
            {
                try { sendLoop.Invoke(form, new object[] { keyPath, stop }); }
                catch (TargetInvocationException error) { producerError = error.InnerException ?? error; }
                catch (Exception error) { producerError = error; }
            }) { IsBackground = true, Name = "Synthetic clipboard send-loop fixture" };
            producer.Start(); var clock = Stopwatch.StartNew();
            try
            {
                while (!backend.Complete && clock.ElapsedMilliseconds < 6500)
                {
                    Application.DoEvents();
                    if (producerError != null) throw producerError;
                    if (backend.Error != null) throw new Exception("Synthetic clipboard backend failed.", backend.Error);
                    Thread.Sleep(10);
                }
                if (backend.Error != null) throw new Exception("Synthetic clipboard backend failed.", backend.Error);
                Check(backend.Complete, "Every synthetic clipboard byte arrives during the uninterrupted first pass.");
                long firstAck = backend.FirstAcknowledgementSequence(reverseAcknowledgement);
                Check(firstAck >= 7 && firstAck <= 11,
                    "A reverse acknowledgement during clipboard transfer is sent within five frames, without waiting for slot2 of the next31-frame group.");
                Check(backend.FirstClipboardIndex == 0, "The initial four control slots never consume clipboard packets0..3.");
                int count = (length + VisualProtocol.GetChunkSize(VideoProfile.Fast) - 1) / VisualProtocol.GetChunkSize(VideoProfile.Fast);
                Check(backend.FirstPassPackets == count, "The full first clipboard pass contains every packet exactly once, without jumping over control slots.");
                Check(backend.ControlFrames >= (length > 27 * 1536 ? 8 : 4), "The fixture covers the initial and, for64KiB, middle control-frame group.");
                Check(Get(form, "current") == payload, "A local send never fabricates a delivery acknowledgement or clears the pending clipboard.");
            }
            finally
            {
                stop.Set(); bool joined = producer.Join(2500);
                if (!joined) { backend.Dispose(); joined = producer.Join(1500); }
                Check(joined, "The ordinary clipboard producer stops and releases the synthetic socket.");
                Application.DoEvents(); if (producerError != null) throw producerError;
            }
        }
    }

    private static void RunCase(string keyPath, bool raw, int width, int height, VideoProfile profile)
    {
        Width = width; Height = height;
        Guid session = Guid.NewGuid();
        var run = new FileBenchmarkRunner(session, new[] { profile }, 1024, Seed, 30000);
        Guid test = run.CurrentCommand.Test;
        using (var backend = new Backend(raw, session, test, profile))
        using (var stop = new ManualResetEvent(false))
        using (var form = new ServerForm(null, false, delegate(string ignored) { return new UvcCamera("127.0.0.1", backend.Port, keyPath); }))
        {
            IntPtr unused = form.Handle; // Create a hidden dispatch target; never show the form.
            form.SetTransmissionPreview(true);
            Set(form, "bridgeSession", session);
            Set(form, "speedTest", run);
            Set(form, "activeCameraName", "Synthetic producer fixture");
            MethodInfo sendLoop = typeof(ServerForm).GetMethod("SendLoop", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(sendLoop != null, "The regression must invoke the production SendLoop.");
            Exception producerError = null;
            var producer = new Thread(delegate()
            {
                try { sendLoop.Invoke(form, new object[] { keyPath, stop }); }
                catch (TargetInvocationException error) { producerError = error.InnerException ?? error; }
                catch (Exception error) { producerError = error; }
            }) { IsBackground = true, Name = "Synthetic production send-loop fixture" };
            producer.Start();
            bool previewData = false;
            var clock = Stopwatch.StartNew();
            try
            {
                while (clock.ElapsedMilliseconds < 6500)
                {
                    Application.DoEvents();
                    if (producerError != null) throw producerError;
                    if (backend.Error != null) throw new Exception("Synthetic camera backend failed.", backend.Error);
                    if (!previewData) previewData = PreviewHasFilePacket(form, test, profile);
                    if (backend.Complete && previewData && backend.SawPrepare && backend.SawStart) break;
                    Thread.Sleep(10);
                }
                Check(backend.SawPrepare, "The actual producer sends a decodable Prepare command: " + raw);
                Check(backend.SawStart, "The actual producer sends Start without any reverse-audio Ready: " + raw);
                Check(backend.DataFrames > 0, "The actual producer sends file-data frames instead of only control/idle frames: " + raw);
                Check((long)Get(form, "benchmarkDataFrames") > 0 && (Guid)Get(form, "benchmarkDataTest") == test,
                    "The UI transmission counter counts real file frames for this exact benchmark test: " + raw);
                Check(backend.Complete, "The actual producer transmits every synthetic file byte without reverse-audio reports: " + raw);
                Check(previewData, "The server preview shows a decodable file-data frame, not a cached idle image: " + raw);
                Check(!run.Finished && run.Snapshot()[0].ReceivedBytes == 0,
                    "A successful local send must not invent a remote delivery acknowledgement: " + raw);
            }
            finally
            {
                stop.Set();
                bool joined = producer.Join(2500);
                if (!joined) { backend.Dispose(); joined = producer.Join(1500); }
                Check(joined, "The production producer must stop and release its synthetic socket.");
                Application.DoEvents();
                if (producerError != null) throw producerError;
            }
        }
    }

    private static bool PreviewHasFilePacket(ServerForm form, Guid test, VideoProfile profile)
    {
        FieldInfo field = typeof(ServerForm).BaseType.GetField("Preview", BindingFlags.Instance | BindingFlags.NonPublic);
        PictureBox preview = (PictureBox)field.GetValue(form);
        Bitmap bitmap = preview.Image as Bitmap;
        if (bitmap == null) return false;
        foreach (Packet packet in VisualProtocol.DecodePackets(bitmap))
        {
            int segment;
            if (FileBenchmarkProtocol.TrySegmentIndex(test, packet.TransferId, out segment) && segment == 0 &&
                packet.Profile == profile && packet.TotalLength == 1024 &&
                BenchmarkData.Verify(Seed, (long)packet.Index * packet.ChunkSize, packet.Data)) return true;
        }
        return false;
    }

    private static void Set(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) throw new Exception("Missing production field: " + name);
        field.SetValue(target, value);
    }

    private static object Get(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) throw new Exception("Missing production field: " + name);
        return field.GetValue(target);
    }

    private sealed class Backend : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Thread thread;
        private readonly bool raw;
        private readonly Guid session, test;
        private readonly VideoProfile profile;
        private readonly byte[] clipboardPayload;
        private TcpClient peer;
        private volatile bool stopping;
        internal volatile bool SawPrepare, SawStart, Complete;
        private int dataFrames;
        private int controlFrames, firstPassPackets, firstClipboardIndex = -1, idleHelloFrames, pictureIdleFrames;
        private readonly object acknowledgementLock = new object();
        private readonly System.Collections.Generic.Dictionary<Guid, long[]> acknowledgements = new System.Collections.Generic.Dictionary<Guid, long[]>();
        internal Action<ulong> FrameObserved;
        internal long AcknowledgementCount(Guid id, bool ordinaryOnly)
        {
            lock (acknowledgementLock) { long[] value; return acknowledgements.TryGetValue(id, out value) ? value[ordinaryOnly ? 2 : 1] : 0; }
        }
        internal long FirstAcknowledgementSequence(Guid id)
        {
            lock (acknowledgementLock) { long[] value; return acknowledgements.TryGetValue(id, out value) ? value[0] : 0; }
        }
        internal int DataFrames { get { return Volatile.Read(ref dataFrames); } }
        internal int ControlFrames { get { return Volatile.Read(ref controlFrames); } }
        internal int FirstPassPackets { get { return Volatile.Read(ref firstPassPackets); } }
        internal int FirstClipboardIndex { get { return Volatile.Read(ref firstClipboardIndex); } }
        internal int IdleHelloFrames { get { return Volatile.Read(ref idleHelloFrames); } }
        internal int PictureIdleFrames { get { return Volatile.Read(ref pictureIdleFrames); } }
        internal Exception Error;
        internal int Port { get; private set; }

        internal Backend(bool raw, Guid session, Guid test, VideoProfile profile, byte[] clipboardPayload = null)
        {
            this.raw = raw; this.session = session; this.test = test; this.profile = profile;
            this.clipboardPayload = clipboardPayload;
            listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            thread = new Thread(Run) { IsBackground = true, Name = "Synthetic authenticated camera backend" };
            thread.Start();
        }

        private void Run()
        {
            try
            {
                peer = listener.AcceptTcpClient();
                using (TcpClient connected = peer)
                using (NetworkStream wire = connected.GetStream())
                {
                    wire.ReadTimeout = wire.WriteTimeout = 1500;
                    byte[] sessionKey = Negotiate(wire);
                    ulong previous = 0;
                    var assembler = new TransferAssembler();
                    while (!stopping)
                    {
                        byte[] header = Read(wire, 44);
                        ulong sequence = 0;
                        for (int i = 0; i < 8; i++) sequence = (sequence << 8) | header[i];
                        Check(sequence == ++previous, "Production frames retain monotonic authenticated sequence numbers.");
                        uint flagged = ((uint)header[8] << 24) | ((uint)header[9] << 16) | ((uint)header[10] << 8) | header[11];
                        Check((flagged & 0xc0000000U) == (raw ? 0x40000000U : 0U), "The actual producer retains the negotiated frame format.");
                        int length = (int)(flagged & 0x3fffffffU);
                        Check(length > 0 && length <= Width * Height * 3 + 65536, "Synthetic frame payload remains bounded.");
                        byte[] bytes = Read(wire, length), prefix = new byte[12], tag = new byte[32];
                        Buffer.BlockCopy(header, 0, prefix, 0, 12); Buffer.BlockCopy(header, 12, tag, 0, 32);
                        Check(Equal(tag, Mac(sessionKey, prefix, bytes)), "The actual producer authenticates every transmitted frame.");
                        using (Bitmap frame = Decode(bytes, raw))
                        {
                            bool dataFrame = false, controlFrame = false;
                            Packet[] packets = VisualProtocol.DecodePackets(frame);
                            bool ordinarySlot = (sequence - 1) % 31 >= 4;
                            if (ordinarySlot && packets.Length == 0) Interlocked.Increment(ref pictureIdleFrames);
                            foreach (Packet packet in packets)
                            {
                                BridgeCameraFormat format; BridgeHello hello;
                                if (BridgeControl.TryCameraFormat(packet, out format)) { controlFrame = true; continue; }
                                if (BridgeControl.TryHello(packet, out hello))
                                {
                                    Check(hello.Session == session, "All producer idle and announcement frames preserve the exact bridge session.");
                                    if (ordinarySlot) Interlocked.Increment(ref idleHelloFrames);
                                    controlFrame = true; continue;
                                }
                                Guid acknowledged;
                                if (BridgeControl.TryAcknowledge(packet, out acknowledged))
                                {
                                    Check(packet.TransferId == session, "Reverse acknowledgement frames preserve the exact bridge session.");
                                    lock (acknowledgementLock)
                                    {
                                        long[] value;
                                        if (!acknowledgements.TryGetValue(acknowledged, out value)) acknowledgements.Add(acknowledged, value = new long[] { (long)sequence, 0, 0 });
                                        value[1]++; if (ordinarySlot) value[2]++;
                                    }
                                    controlFrame = true; continue;
                                }
                                FileBenchmarkCommand command;
                                if (FileBenchmarkProtocol.TryCommand(packet, out command))
                                {
                                    Check(command.Session == session && command.Test == test, "Control frames retain the current benchmark identity.");
                                    if (command.Phase == FileBenchmarkPhase.Prepare) SawPrepare = true;
                                    if (command.Phase == FileBenchmarkPhase.Start) SawStart = true;
                                    continue;
                                }
                                if (clipboardPayload != null)
                                {
                                    if (packet.TransferId != test) continue;
                                    Check(packet.Profile == profile && packet.TotalLength == clipboardPayload.Length, "Clipboard frames retain their exact transfer, profile and length.");
                                    dataFrame = true;
                                    if (Complete) continue;
                                    if (firstClipboardIndex < 0) Volatile.Write(ref firstClipboardIndex, packet.Index);
                                    Check(packet.Index == firstPassPackets, "Control slots never advance or skip the next ordinary clipboard packet.");
                                    Interlocked.Increment(ref firstPassPackets);
                                    byte[] clipboard = assembler.Add(packet);
                                    if (clipboard != null)
                                    {
                                        Check(Equal(clipboard, clipboardPayload), "The complete first-pass clipboard payload matches every synthetic source byte.");
                                        Complete = true;
                                    }
                                    continue;
                                }
                                int segment;
                                if (!FileBenchmarkProtocol.TrySegmentIndex(test, packet.TransferId, out segment)) continue;
                                Check(segment == 0 && packet.TotalLength == 1024 && packet.Profile == profile,
                                    "File data uses the current bounded synthetic transfer.");
                                dataFrame = true;
                                if (Complete) continue;
                                byte[] completed = assembler.Add(packet);
                                if (completed != null)
                                {
                                    Check(completed.Length == 1024 && BenchmarkData.Verify(Seed, 0, completed), "All transmitted benchmark bytes match their deterministic source.");
                                    Complete = true;
                                }
                            }
                            if (controlFrame) Interlocked.Increment(ref controlFrames);
                            if (dataFrame) Interlocked.Increment(ref dataFrames);
                            Action<ulong> observed = FrameObserved;
                            if (observed != null) observed(sequence);
                        }
                    }
                }
            }
            catch (EndOfStreamException) { if (!stopping && !Complete) Error = new Exception("Producer closed before sending the test file."); }
            catch (Exception error) { if (!stopping) Error = error; }
        }

        private byte[] Negotiate(NetworkStream wire)
        {
            byte[] hello = new byte[48], magic = { 0x55, 0x42, 0x56, 0x43, 0, 0, 0, 1 };
            Buffer.BlockCopy(magic, 0, hello, 0, magic.Length);
            hello[8] = (byte)(Width >> 8); hello[9] = (byte)(Width & 255);
            hello[10] = (byte)(Height >> 8); hello[11] = (byte)(Height & 255);
            if (raw) hello[12] = 128;
            hello[15] = 30;
            for (int i = 16; i < hello.Length; i++) hello[i] = (byte)i;
            wire.Write(hello, 0, hello.Length);
            byte[] secret;
            using (SHA256 hash = SHA256.Create()) secret = hash.ComputeHash(Encoding.UTF8.GetBytes(TestKey));
            byte[] signature = Read(wire, 32);
            Check(Equal(signature, Mac(secret, Encoding.ASCII.GetBytes("USBBridge video authenticate"), hello)), "Production camera connection authenticates with the public fixture key.");
            byte[] okay = Encoding.ASCII.GetBytes("OKAY"), proof = Mac(secret, Encoding.ASCII.GetBytes("USBBridge video receiver"), hello, signature);
            wire.Write(okay, 0, okay.Length); wire.Write(proof, 0, proof.Length);
            return Mac(secret, Encoding.ASCII.GetBytes("USBBridge video session"), hello);
        }

        public void Dispose()
        {
            stopping = true;
            listener.Stop();
            TcpClient active = peer;
            if (active != null) active.Close();
            Check(thread.Join(2000), "Synthetic backend must terminate and release its ephemeral listener.");
        }
    }

    private static Bitmap Decode(byte[] bytes, bool raw)
    {
        if (!raw)
        {
            using (var input = new MemoryStream(bytes))
            using (Image image = Image.FromStream(input)) return new Bitmap(image);
        }
        Check(bytes.Length == Width * Height * 2, "YUY2 contains exactly one negotiated frame.");
        var frame = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
        BitmapData target = frame.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] row = new byte[Width * 3];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    byte value = (byte)Math.Max(0, Math.Min(255, (bytes[(y * Width + x) * 2] - 16) * 255 / 219));
                    row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = value;
                }
                Marshal.Copy(row, 0, IntPtr.Add(target.Scan0, y * target.Stride), row.Length);
            }
        }
        finally { frame.UnlockBits(target); }
        return frame;
    }

    private static byte[] Read(Stream wire, int count)
    {
        byte[] result = new byte[count]; int used = 0;
        while (used < count)
        {
            int read = wire.Read(result, used, count - used);
            if (read == 0) throw new EndOfStreamException();
            used += read;
        }
        return result;
    }

    private static byte[] Mac(byte[] key, params byte[][] parts)
    {
        using (var all = new MemoryStream())
        using (var hmac = new HMACSHA256(key))
        {
            foreach (byte[] part in parts) all.Write(part, 0, part.Length);
            return hmac.ComputeHash(all.ToArray());
        }
    }

    private static bool Equal(byte[] first, byte[] second)
    {
        if (first.Length != second.Length) return false;
        int different = 0; for (int i = 0; i < first.Length; i++) different |= first[i] ^ second[i];
        return different == 0;
    }

    private static void Check(bool condition, string message)
    {
        Interlocked.Increment(ref checks);
        if (!condition) throw new Exception(message);
    }
}
