using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using HorizonClient;

// Explicit opt-in local UVC integration check. It never accesses the clipboard.
internal static class CameraSmoke
{
    private const int PacketCount = 8;
    private const int CaptureSeconds = 5;

    private sealed class Result
    {
        public int Sent;
        public int Captured;
        public int Decoded;
        public int Unique;
        public bool Complete;
        public double CompletedSeconds;
        public Exception Error;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1 && args.Length != 2)
            {
                Console.Error.WriteLine("Usage: CameraSmoke.exe PATH_TO_THIS_PROJECT_CAMERA_KEY [EXACT_OWN_CAMERA_MONIKER]");
                return 2;
            }
            if (args.Length == 2 && (args[1].IndexOf("vid_1209", StringComparison.OrdinalIgnoreCase) < 0 ||
                args[1].IndexOf("pid_f002", StringComparison.OrdinalIgnoreCase) < 0))
                throw new ArgumentException("Only an exact project-owned UVC camera moniker is accepted.");
            List<CameraDevice> candidates = new List<CameraDevice>();
            var enumeration = Stopwatch.StartNew();
            do
            {
                candidates.Clear();
                foreach (CameraDevice device in CameraDevices.List())
                {
                    bool ownSerial = device.Id.IndexOf("horizonclient-camera-1", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool ownName = device.Name.IndexOf("HorizonClient", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool softwareUvc = device.Id.IndexOf("vid_1209", StringComparison.OrdinalIgnoreCase) >= 0
                        && device.Id.IndexOf("pid_f002", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (args.Length == 2 ? softwareUvc && String.Equals(device.Id, args[1], StringComparison.Ordinal) :
                        ownSerial || (ownName && softwareUvc)) candidates.Add(device);
                }
                if (candidates.Count != 0) break;
                Thread.Sleep(200);
            } while (enumeration.Elapsed.TotalSeconds < 10);
            if (candidates.Count != 1) throw new InvalidOperationException("Expected exactly one project-owned UVC camera; run Setup-Camera.ps1 first.");
            Console.WriteLine("Local UVC check: {0}. No Horizon hop and no clipboard access.", candidates[0].Name);
            bool fast = Run(candidates[0], args[0], VideoProfile.Fast);
            bool dense = Run(candidates[0], args[0], VideoProfile.Dense);
            return fast && dense ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Camera smoke failed: {0}", error.Message);
            return 1;
        }
    }

    private static bool Run(CameraDevice device, string keyFile, VideoProfile profile)
    {
        int chunkSize = VisualProtocol.GetChunkSize(profile);
        int width = VisualProtocol.GetWidth(profile);
        int height = VisualProtocol.GetHeight(profile);
        byte[] expected = new byte[chunkSize * PacketCount];
        new Random(7291 + (int)profile).NextBytes(expected);
        Guid transferId = Guid.NewGuid();
        var frames = new List<Bitmap>();
        var result = new Result();
        var gate = new object();
        var clock = new Stopwatch();
        var assembler = new TransferAssembler();
        var seen = new bool[PacketCount];
        using (var stop = new ManualResetEvent(false))
        {
            Thread producer = null;
            CameraCapture capture = null;
            try
            {
                for (int index = 0; index < PacketCount; index++)
                {
                    var chunk = new byte[chunkSize];
                    Buffer.BlockCopy(expected, index * chunkSize, chunk, 0, chunkSize);
                    frames.Add(VisualProtocol.EncodeFrame(transferId, index, PacketCount, expected.Length, chunk, profile));
                }
                capture = new CameraCapture(device.Id, delegate(Bitmap frame)
                {
                    lock (gate) result.Captured++;
                    try
                    {
                        Packet packet = VisualProtocol.DecodeFrame(frame);
                        if (packet == null || packet.TransferId != transferId) return;
                        if (packet.Profile != profile || packet.Index < 0 || packet.Index >= PacketCount
                            || packet.Count != PacketCount || packet.TotalLength != expected.Length || packet.Data.Length != chunkSize)
                            throw new InvalidDataException("Decoded packet metadata differs from the source.");
                        for (int i = 0; i < chunkSize; i++)
                            if (packet.Data[i] != expected[packet.Index * chunkSize + i]) throw new InvalidDataException("Decoded packet bytes differ from the source.");
                        byte[] complete = assembler.Add(packet);
                        lock (gate)
                        {
                            result.Decoded++;
                            if (!seen[packet.Index]) { seen[packet.Index] = true; result.Unique++; }
                            if (complete != null && !result.Complete)
                            {
                                if (complete.Length != expected.Length) throw new InvalidDataException("Reassembled data length differs from the source.");
                                for (int i = 0; i < complete.Length; i++)
                                    if (complete[i] != expected[i]) throw new InvalidDataException("Reassembled bytes differ from the source.");
                                result.Complete = true;
                                result.CompletedSeconds = clock.Elapsed.TotalSeconds;
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        lock (gate) { if (result.Error == null) result.Error = error; }
                        stop.Set();
                    }
                }, width, height);
                capture.OnError += delegate(Exception error)
                {
                    lock (gate) { if (result.Error == null) result.Error = error; }
                    stop.Set();
                };
                capture.Start();
                Console.WriteLine("{0}: requested {1}x{2}; captured {3}x{4}.", profile, width, height, capture.Width, capture.Height);
                clock.Start();
                producer = new Thread(delegate()
                {
                    try
                    {
                        using (var camera = new UvcCamera("127.0.0.1", 13261, keyFile))
                        {
                            if (camera.Width != width || camera.Height != height) throw new InvalidOperationException("The UVC receiver mode does not match the profile. Run Stop-Camera.ps1, then Setup-Camera.ps1.");
                            double next = 0;
                            int sent = 0;
                            while (!stop.WaitOne(0))
                            {
                                // Rotate cycle phase so dropped/slow capture frames cannot
                                // repeatedly omit the same packet at an integer rate ratio.
                                int index = (sent * 5 + sent / PacketCount) % PacketCount;
                                camera.Send(frames[index]);
                                sent++;
                                lock (gate) result.Sent = sent;
                                next += 1.0 / camera.FramesPerSecond;
                                int delay = (int)((next - clock.Elapsed.TotalSeconds) * 1000);
                                if (delay > 0 && stop.WaitOne(delay)) break;
                                if (next < clock.Elapsed.TotalSeconds - 0.25) next = clock.Elapsed.TotalSeconds;
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        lock (gate) { if (result.Error == null) result.Error = error; }
                        stop.Set();
                    }
                });
                producer.IsBackground = true;
                producer.Name = "Camera smoke deterministic producer";
                producer.Start();
                while (clock.Elapsed.TotalSeconds < CaptureSeconds && !stop.WaitOne(50)) { }
            }
            catch (Exception error) { lock (gate) { if (result.Error == null) result.Error = error; } }
            finally
            {
                stop.Set();
                if (producer != null && !producer.Join(12000))
                    lock (gate) result.Error = new TimeoutException("Camera producer did not stop within its I/O deadline.");
                if (capture != null) capture.Dispose();
                foreach (Bitmap frame in frames) frame.Dispose();
                clock.Stop();
            }
        }
        lock (gate)
        {
            Console.WriteLine("{0}: sent={1}, captured={2}, decoded={3}, unique={4}/{5}, exact reassembly={6}.",
                profile, result.Sent, result.Captured, result.Decoded, result.Unique, PacketCount, result.Complete);
            if (result.Complete)
                Console.WriteLine("{0}: {1} bytes completed in {2:F3} s ({3:F1} KiB/s for the first local transfer).",
                    profile, expected.Length, result.CompletedSeconds, expected.Length / Math.Max(0.001, result.CompletedSeconds) / 1024.0);
            if (result.Error != null) Console.Error.WriteLine("{0}: {1}", profile, result.Error.Message);
            return result.Error == null && result.Complete && result.Unique == PacketCount;
        }
    }
}
