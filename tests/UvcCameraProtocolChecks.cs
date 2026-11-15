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
using HorizonClient;

internal static class UvcCameraProtocolChecks
{
    private const string TestKey = "public-loopback-test-key-123456789";
    private const int FrameWidth = 1280;
    private const int FrameHeight = 720;
    private const int FrameCount = 10;
    private static byte[] previousRaw, previousRow;
    private static int checks;

    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--bench-yuy2") return BenchmarkYuy2();
        if (args.Length == 1 && args[0] == "--bench-send") return BenchmarkSendBuffers();
        string keyPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(keyPath, TestKey, new UTF8Encoding(false));
            RunCase(keyPath, false);
            RunCase(keyPath, true);
            CheckYuy2Conversion();
            CheckColor4Palette();
            RunRawCase(keyPath);
            RunRawCase(keyPath);
            RunH264ProbeCase(keyPath);
            CheckMode(keyPath, 3840, 2160, 30, true, true);
            CheckMode(keyPath, 3840, 2160, 60, true, false);
            CheckMode(keyPath, 7680, 4320, 8, true, true);
            CheckMode(keyPath, 7680, 4320, 30, true, false);
            CheckMode(keyPath, 7680, 4320, 60, false, true);
            CheckMode(keyPath, 7690, 4320, 30, false, false);
            CheckMode(keyPath, 1280, 721, 30, false, false);
            CheckMode(keyPath, 640, 480, 30, false, true, true);
            CheckMode(keyPath, 1280, 720, 30, false, false, true);
            CheckMode(keyPath, 640, 480, 60, false, false, true);
            CheckMode(keyPath, 640, 480, 30, true, false, true);
            Console.WriteLine("UVC protocol checks passed: " + checks + " assertions; mutual authentication, JPEG90/100, RAW YUY2 color4 palette/conversion/type/authentication, H.264 probe signaling/video rejection/PCM, 4K30/8K8 RAW and 8K60 MJPEG bounds. Synthetic loopback only; no camera opened.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { File.Delete(keyPath); }
    }

    private static int BenchmarkSendBuffers()
    {
        const int width = 1920, height = 1080, iterations = 120;
        // Isolate exactly the buffer/conversion helper called by synchronous Send;
        // this fixture owns no network connection or external camera.
        var camera = (UvcCamera)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(UvcCamera));
        MethodInfo method = typeof(UvcCamera).GetMethod("PrepareYuy2", BindingFlags.Instance | BindingFlags.NonPublic);
        var prepare = (Func<Bitmap, byte[]>)Delegate.CreateDelegate(typeof(Func<Bitmap, byte[]>), camera, method);
        using (var frame = new Bitmap(width, height, PixelFormat.Format24bppRgb))
        {
            using (Graphics graphics = Graphics.FromImage(frame)) graphics.Clear(Color.White);
            var clock = Stopwatch.StartNew();
            byte[] first = prepare(frame), row = (byte[])Scratch(camera, "rawRow");
            double firstMilliseconds = clock.Elapsed.TotalMilliseconds;
            AppDomain.MonitoringIsEnabled = true;
            long allocatedBefore = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            clock.Restart();
            for (int i = 0; i < iterations; i++)
            {
                frame.SetPixel(0, 0, (i & 1) == 0 ? Color.Black : Color.White);
                byte[] output = prepare(frame);
                Assert(Object.ReferenceEquals(first, output) && Object.ReferenceEquals(row, Scratch(camera, "rawRow")), "Send helper allocates only its first raw output and RGB row.");
                Assert(output[0] == ((i & 1) == 0 ? 16 : 235), "Benchmarked Send helper re-reads mutable bitmap pixels.");
            }
            double milliseconds = clock.Elapsed.TotalMilliseconds;
            long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - allocatedBefore;
            Console.WriteLine("Send buffer helper: 1920x1080 RGB24, iterations=" + iterations + ", first_ms=" + firstMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ", steady_ms_per_frame=" + (milliseconds / iterations).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ", output_buffers=1, raw_bytes=" + first.Length +
                ", row_buffers=1, row_bytes=" + row.Length + ", steady_total_managed_allocated=" + allocated + ", former_raw_output_bytes_for_120_frames=" + (120L * first.Length) + ". Excludes socket/HMAC; AppDomain allocation includes instrumentation.");
            camera.Dispose();
        }
        return 0;
    }

    private static int BenchmarkYuy2()
    {
        const int width = 3840, height = 2160, iterations = 12;
        using (var frame = new Bitmap(width, height, PixelFormat.Format24bppRgb))
        {
            BitmapData locked = frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte[] row = new byte[width * 3]; var random = new Random(927);
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++) { byte value = random.Next(2) == 0 ? (byte)0 : (byte)255; row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = value; }
                    Marshal.Copy(row, 0, IntPtr.Add(locked.Scan0, y * locked.Stride), row.Length);
                }
            }
            finally { frame.UnlockBits(locked); }
            MethodInfo mac = typeof(UvcCamera).GetMethod("Mac", BindingFlags.Static | BindingFlags.NonPublic);
            byte[] key = new byte[32], prefix = new byte[12], payload = UvcCamera.EncodeYuy2(frame), tag = null;
            for (int i = 0; i < key.Length; i++) key[i] = (byte)i;
            prefix[7] = 1; uint flagged = 0x40000000U | (uint)payload.Length;
            for (int i = 0; i < 4; i++) prefix[8 + i] = (byte)(flagged >> (24 - i * 8));
            mac.Invoke(null, new object[] { key, new byte[][] { prefix, payload } });
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) payload = UvcCamera.EncodeYuy2(frame);
            double conversion = clock.Elapsed.TotalMilliseconds / iterations;
            clock.Restart();
            for (int i = 0; i < iterations; i++) tag = (byte[])mac.Invoke(null, new object[] { key, new byte[][] { prefix, payload } });
            double authentication = clock.Elapsed.TotalMilliseconds / iterations;
            string digest;
            using (SHA256 hash = new SHA256CryptoServiceProvider()) digest = BitConverter.ToString(hash.ComputeHash(payload)).Replace("-", "");
            Console.WriteLine("{\"width\":3840,\"height\":2160,\"iterations\":12,\"raw_bytes\":" + payload.Length + ",\"yuy2_ms\":" + conversion.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ",\"hmac_ms\":" + authentication.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ",\"yuy2_sha256\":\"" + digest + "\",\"packet_hmac\":\"" + BitConverter.ToString(tag).Replace("-", "") + "\"}");
        }
        return 0;
    }

    private static void RunCase(string keyPath, bool forgedProof)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Exception serverError = null;
        var worker = new Thread(delegate()
        {
            try
            {
                using (TcpClient peer = listener.AcceptTcpClient())
                using (NetworkStream wire = peer.GetStream())
                {
                    wire.ReadTimeout = 5000;
                    wire.WriteTimeout = 5000;
                    byte[] hello = new byte[48];
                    byte[] magic = { 0x55, 0x42, 0x56, 0x43, 0, 0, 0, 1 };
                    Buffer.BlockCopy(magic, 0, hello, 0, magic.Length);
                    hello[8] = (byte)(FrameWidth >> 8); hello[9] = (byte)(FrameWidth & 255);
                    hello[10] = (byte)(FrameHeight >> 8); hello[11] = (byte)(FrameHeight & 255); hello[15] = 30;
                    for (int i = 16; i < hello.Length; i++) hello[i] = (byte)i;
                    wire.Write(hello, 0, hello.Length);
                    byte[] secret;
                    using (var sha = SHA256.Create()) secret = sha.ComputeHash(Encoding.UTF8.GetBytes(TestKey));
                    byte[] signature = Read(wire, 32);
                    Assert(Equal(signature, Mac(secret, Encoding.ASCII.GetBytes("USBBridge video authenticate"), hello)), "Wrong client authentication signature.");
                    byte[] proof = Mac(secret, Encoding.ASCII.GetBytes("USBBridge video receiver"), hello, signature);
                    if (forgedProof) proof[0] ^= 1;
                    byte[] okay = Encoding.ASCII.GetBytes("OKAY");
                    wire.Write(okay, 0, okay.Length);
                    wire.Write(proof, 0, proof.Length);
                    if (forgedProof) return;
                    byte[] session = Mac(secret, Encoding.ASCII.GetBytes("USBBridge video session"), hello);
                    for (int frame = 1; frame <= FrameCount; frame++)
                    {
                        byte[] header = Read(wire, 44);
                        ulong sequence = 0;
                        for (int i = 0; i < 8; i++) sequence = (sequence << 8) | header[i];
                        Assert(sequence == (ulong)frame, "Incorrect frame sequence.");
                        int length = (header[8] << 24) | (header[9] << 16) | (header[10] << 8) | header[11];
                        Assert(length > 4 && length <= FrameWidth * FrameHeight * 3 + 65536, "Unbounded JPEG packet.");
                        byte[] jpeg = Read(wire, length);
                        byte[] prefix = new byte[12];
                        byte[] tag = new byte[32];
                        Buffer.BlockCopy(header, 0, prefix, 0, 12);
                        Buffer.BlockCopy(header, 12, tag, 0, 32);
                        Assert(Equal(tag, Mac(session, prefix, jpeg)), "Incorrect frame authentication.");
                        using (var input = new MemoryStream(jpeg))
                        using (Image decoded = Image.FromStream(input))
                            Assert(decoded.Width == FrameWidth && decoded.Height == FrameHeight, "Wrong JPEG dimensions.");
                        Assert(jpeg[0] == 255 && jpeg[1] == 216 && jpeg[length - 2] == 255 && jpeg[length - 1] == 217, "Invalid JPEG envelope.");
                        bool baseline = false;
                        for (int i = 2; i < jpeg.Length - 1; i++)
                            if (jpeg[i] == 255 && jpeg[i + 1] == 192) baseline = true;
                        Assert(baseline, "JPEG is not baseline.");
                    }
                    for (int part = 0; part < 2; part++)
                    {
                        byte[] header = Read(wire, 44);
                        ulong sequence = 0; for (int i = 0; i < 8; i++) sequence = (sequence << 8) | header[i];
                        Assert(sequence == (ulong)(FrameCount + part + 1), "JPEG and PCM must share one monotonic sequence.");
                        uint flagged = ((uint)header[8] << 24) | ((uint)header[9] << 16) | ((uint)header[10] << 8) | header[11];
                        Assert((flagged & 0x80000000U) != 0, "PCM packets need an authenticated audio type flag.");
                        int length = (int)(flagged & 0x7fffffffU);
                        Assert(length == (part == 0 ? 96000 : 4002), "PCM must be split into bounded even-sized chunks.");
                        byte[] pcm = Read(wire, length), prefix = new byte[12], tag = new byte[32];
                        Buffer.BlockCopy(header, 0, prefix, 0, 12); Buffer.BlockCopy(header, 12, tag, 0, 32);
                        Assert(Equal(tag, Mac(session, prefix, pcm)), "The audio flag, sequence and PCM must all be authenticated.");
                        if (part == 0)
                        {
                            Assert(BitConverter.ToInt16(pcm, 0) == (short)Math.Round(-0.2f * 32767), "PCM16LE sample conversion changed.");
                            Assert(BitConverter.ToInt16(pcm, 2) == (short)Math.Round(0.25f * 32767), "Audio must stay below the output amplitude limit.");
                        }
                    }
                }
            }
            catch (Exception error) { serverError = error; }
        });
        worker.IsBackground = true;
        worker.Start();
        Exception clientError = null;
        try
        {
            using (var camera = new UvcCamera("127.0.0.1", port, keyPath))
            {
                Assert(!forgedProof, "Client trusted a forged receiver proof.");
                Assert(camera.IsConnected && camera.Width == FrameWidth && camera.Height == FrameHeight && camera.FramesPerSecond == 30, "Invalid negotiated mode.");
                Assert(!camera.IsUncompressed && camera.JpegQuality == 90, "Legacy JPEG defaults changed.");
                bool invalidQuality = false;
                try { camera.JpegQuality = 101; } catch (ArgumentOutOfRangeException) { invalidQuality = true; }
                Assert(invalidQuality, "Invalid JPEG quality must be rejected before writing a frame.");
                using (var bitmap = new Bitmap(FrameWidth, FrameHeight))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(Color.White);
                        for (int y = 0; y < FrameHeight; y += 16)
                            for (int x = 0; x < FrameWidth; x += 16)
                                if (((x / 16 + y / 16) & 1) == 0) graphics.FillRectangle(Brushes.Black, x, y, 16, 16);
                    }
                    for (int i = 0; i < FrameCount; i++) { if (i == FrameCount - 1) camera.JpegQuality = 100; camera.Send(bitmap); }
                }
                float[] audio = new float[50001]; audio[0] = -0.2f; audio[1] = 0.5f;
                camera.SendAudio(audio);
                bool invalid = false;
                try { camera.SendAudio(new float[] { Single.NaN }); } catch (ArgumentException) { invalid = true; }
                Assert(invalid, "Invalid audio must be rejected before any bytes are sent.");
            }
        }
        catch (Exception error) { clientError = error; }
        finally
        {
            Assert(worker.Join(7000), "Protocol mock did not finish.");
            listener.Stop();
        }
        if (serverError != null) throw new Exception("Mock receiver failed.", serverError);
        if (forgedProof) Assert(clientError is IOException, "Forged proof did not produce an authentication error.");
        else if (clientError != null) throw clientError;
    }

    private static void CheckYuy2Conversion()
    {
        using (Bitmap frame = new Bitmap(2, 2))
        {
            frame.SetPixel(0, 0, Color.Black); frame.SetPixel(1, 0, Color.White);
            frame.SetPixel(0, 1, Color.Red); frame.SetPixel(1, 1, Color.Lime);
            Assert(Equal(UvcCamera.EncodeYuy2(frame), new byte[] { 16, 128, 235, 128, 82, 72, 144, 137 }), "BT.601 YUY2 pair layout, luma, row order or chroma changed.");
            byte[] first = UvcCamera.EncodeYuy2(frame), second = UvcCamera.EncodeYuy2(frame);
            Assert(!Object.ReferenceEquals(first, second) && Equal(first, second), "Static encoder results must have independent ownership.");
            first[0] = 99;
            Assert(second[0] == 16, "Mutating one static result must not change another.");
        }
        using (Bitmap odd = new Bitmap(3, 2))
        {
            bool rejected = false;
            try { UvcCamera.EncodeYuy2(odd); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "Odd-width raw frames must be rejected.");
        }
        using (Bitmap shades = new Bitmap(512, 1, PixelFormat.Format24bppRgb))
        {
            var pixels = new byte[512 * 3];
            for (int value = 0; value < 256; value++)
                for (int channel = 0; channel < 3; channel++) { pixels[value * 6 + channel] = (byte)value; pixels[value * 6 + 3 + channel] = (byte)(255 - value); }
            BitmapData locked = shades.LockBits(new Rectangle(0, 0, 512, 1), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try { Marshal.Copy(pixels, 0, locked.Scan0, pixels.Length); }
            finally { shades.UnlockBits(locked); }
            byte[] encoded = UvcCamera.EncodeYuy2(shades);
            Assert(encoded.Length == 1024, "Grayscale conversion must retain its exact bounded size.");
            for (int value = 0; value < 256; value++)
            {
                Assert(encoded[value * 4] == ((220 * value + 128) >> 8) + 16 && encoded[value * 4 + 2] == ((220 * (255 - value) + 128) >> 8) + 16, "Grayscale fast path changed limited-range luma.");
                Assert(encoded[value * 4 + 1] == 128 && encoded[value * 4 + 3] == 128, "Grayscale fast path introduced colored chroma.");
            }
        }
        using (Bitmap mixed = new Bitmap(6, 1, PixelFormat.Format24bppRgb))
        {
            mixed.SetPixel(0, 0, Color.White); mixed.SetPixel(1, 0, Color.Blue);
            mixed.SetPixel(2, 0, Color.FromArgb(128, 128, 128)); mixed.SetPixel(3, 0, Color.Red);
            mixed.SetPixel(4, 0, Color.Blue); mixed.SetPixel(5, 0, Color.Blue);
            Assert(Equal(UvcCamera.EncodeYuy2(mixed), new byte[] { 235, 184, 41, 119, 126, 109, 82, 184, 41, 240, 41, 110 }), "Mixed grayscale/color pairs must retain the full color conversion.");
        }
        IntPtr negativePixels = Marshal.AllocHGlobal(16);
        try
        {
            // Two RGB24 rows, padded to eight bytes. Scan0 is the logical top
            // row; a negative stride walks backwards to the red/green row.
            Marshal.Copy(new byte[] { 0, 0, 255, 0, 255, 0, 0, 0, 0, 0, 0, 255, 255, 255, 0, 0 }, 0, negativePixels, 16);
            using (var negative = new Bitmap(2, 2, -8, PixelFormat.Format24bppRgb, IntPtr.Add(negativePixels, 8)))
                Assert(Equal(UvcCamera.EncodeYuy2(negative), new byte[] { 16, 128, 235, 128, 82, 72, 144, 137 }), "Negative stride must retain logical row order and exact BT601 bytes.");
        }
        finally { Marshal.FreeHGlobal(negativePixels); }
    }

    private static void CheckColor4Palette()
    {
        Color[] colors = { Color.Black, Color.Red, Color.Yellow, Color.Blue, Color.White };
        byte[] solid = { 16, 128, 16, 128, 82, 90, 82, 240, 210, 16, 210, 146, 41, 240, 41, 110, 235, 128, 235, 128 };
        Color[] mixed = { Color.Red, Color.Blue, Color.Red, Color.Yellow, Color.Yellow, Color.Blue, Color.Black, Color.Yellow };
        byte[] blended = { 82, 165, 41, 175, 82, 53, 210, 193, 210, 128, 41, 128, 16, 72, 210, 137 };
        foreach (PixelFormat format in new[] { PixelFormat.Format24bppRgb, PixelFormat.Format32bppArgb })
        {
            using (var frame = new Bitmap(colors.Length * 2, 1, format))
            {
                for (int index = 0; index < colors.Length; index++)
                { frame.SetPixel(index * 2, 0, colors[index]); frame.SetPixel(index * 2 + 1, 0, colors[index]); }
                Assert(Equal(UvcCamera.EncodeYuy2(frame), solid), "Solid protocol colors retain limited-range luma and non-neutral chroma in RGB24 and ARGB32 input.");
            }
            using (var frame = new Bitmap(mixed.Length, 1, format))
            {
                for (int index = 0; index < mixed.Length; index++) frame.SetPixel(index, 0, mixed[index]);
                // YUY2 is 4:2:2. Complementary yellow/blue pixels necessarily
                // average to neutral chroma; protocol cells must span pairs.
                Assert(Equal(UvcCamera.EncodeYuy2(frame), blended), "Mixed protocol colors average horizontal pair chroma without discarding individual luma.");
            }
        }
    }

    private static void RunRawCase(string keyPath)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Exception serverError = null;
        var worker = new Thread(delegate()
        {
            try
            {
                using (TcpClient peer = listener.AcceptTcpClient())
                using (NetworkStream wire = peer.GetStream())
                {
                    wire.ReadTimeout = wire.WriteTimeout = 5000;
                    byte[] session = Negotiate(wire, 16, 16, 30, true, true);
                    for (int part = 0; part < 6; part++)
                    {
                        byte[] header = Read(wire, 44);
                        Assert(header[7] == part + 1, "Repeated raw frames and PCM must retain their monotonic sequence.");
                        uint flagged = ((uint)header[8] << 24) | ((uint)header[9] << 16) | ((uint)header[10] << 8) | header[11];
                        Assert((flagged & 0xc0000000U) == (part < 5 ? 0x40000000U : 0x80000000U), "RAW and PCM type flags must be distinct and exclusive.");
                        int length = (int)(flagged & 0x3fffffffU);
                        Assert(length == (part < 5 ? 512 : 2), "Raw frame length must be exact.");
                        byte[] payload = Read(wire, length), prefix = new byte[12], tag = new byte[32];
                        Buffer.BlockCopy(header, 0, prefix, 0, 12); Buffer.BlockCopy(header, 12, tag, 0, 32);
                        Assert(Equal(tag, Mac(session, prefix, payload)), "RAW/PCM flag and payload authentication changed.");
                        if (part < 5)
                        {
                            byte[][] pairs = { new byte[] { 16, 128, 235, 128 }, new byte[] { 82, 72, 144, 137 }, new byte[] { 41, 240, 41, 110 },
                                new byte[] { 82, 90, 82, 240 }, new byte[] { 210, 16, 210, 146 } };
                            for (int i = 0; i < payload.Length; i++)
                                Assert(payload[i] == (i < 4 ? pairs[part][i] : (i % 2 == 0 ? 235 : 128)), "Mutable same-Bitmap send must fill every exact payload byte.");
                        }
                    }
                }
            }
            catch (Exception error) { serverError = error; }
        }) { IsBackground = true };
        worker.Start();
        try
        {
            using (UvcCamera camera = new UvcCamera("127.0.0.1", port, keyPath))
            using (Bitmap frame = new Bitmap(16, 16, PixelFormat.Format24bppRgb))
            {
                Assert(camera.IsUncompressed && camera.FramesPerSecond == 30, "Raw handshake flag must be removed from FPS.");
                using (Graphics graphics = Graphics.FromImage(frame)) graphics.Clear(Color.White);
                frame.SetPixel(0, 0, Color.Black);
                camera.Send(frame);
                object raw = Scratch(camera, "rawFrame"), row = Scratch(camera, "rawRow");
                frame.SetPixel(0, 0, Color.Red); frame.SetPixel(1, 0, Color.Lime);
                camera.Send(frame);
                frame.SetPixel(0, 0, Color.Blue); frame.SetPixel(1, 0, Color.Blue);
                camera.Send(frame);
                frame.SetPixel(0, 0, Color.Red); frame.SetPixel(1, 0, Color.Red);
                camera.Send(frame);
                frame.SetPixel(0, 0, Color.Yellow); frame.SetPixel(1, 0, Color.Yellow);
                camera.Send(frame);
                camera.SendAudio(new float[] { 0.1f });
                Assert(raw != null && row != null, "RAW sends must retain a connection-owned output and RGB row buffer.");
                Assert(Object.ReferenceEquals(raw, Scratch(camera, "rawFrame")) && Object.ReferenceEquals(row, Scratch(camera, "rawRow")), "Repeated Send must reuse both connection buffers.");
                Assert(!Object.ReferenceEquals(previousRaw, raw) && !Object.ReferenceEquals(previousRow, row), "Different connections must never share output or row scratch.");
                previousRaw = (byte[])raw; previousRow = (byte[])row;
                Assert(camera.VideoFramesSent == 5 && camera.VideoBytesSent == 2560 && camera.LastVideoPayloadBytes == 512, "Raw counters must account for each mutable frame.");
                camera.Dispose();
                Assert(Scratch(camera, "rawFrame") == null && Scratch(camera, "rawRow") == null, "Disposed connections release retained frame storage.");
            }
        }
        finally { Assert(worker.Join(7000), "Raw mock receiver did not finish."); listener.Stop(); }
        if (serverError != null) throw new Exception("Raw mock receiver failed.", serverError);
    }

    private static object Scratch(UvcCamera camera, string name)
    {
        FieldInfo field = typeof(UvcCamera).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        return field == null ? null : field.GetValue(camera);
    }

    private static void RunH264ProbeCase(string keyPath)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Exception serverError = null;
        var worker = new Thread(delegate()
        {
            try
            {
                using (TcpClient peer = listener.AcceptTcpClient())
                using (NetworkStream wire = peer.GetStream())
                {
                    wire.ReadTimeout = wire.WriteTimeout = 5000;
                    byte[] session = Negotiate(wire, 640, 480, 30, false, true, true);
                    byte[] header = Read(wire, 44);
                    Assert(header[7] == 1, "Rejected probe Bitmap must not advance the packet sequence.");
                    Assert(header[8] == 0x80 && header[9] == 0 && header[10] == 0 && header[11] == 2, "Probe must continue allowing distinct PCM packets.");
                    byte[] payload = Read(wire, 2), prefix = new byte[12], tag = new byte[32];
                    Buffer.BlockCopy(header, 0, prefix, 0, 12); Buffer.BlockCopy(header, 12, tag, 0, 32);
                    Assert(Equal(tag, Mac(session, prefix, payload)), "Probe PCM authentication changed.");
                }
            }
            catch (Exception error) { serverError = error; }
        }) { IsBackground = true };
        worker.Start();
        try
        {
            using (UvcCamera camera = new UvcCamera("127.0.0.1", port, keyPath))
            using (Bitmap frame = new Bitmap(640, 480))
            {
                Assert(camera.IsH264Probe && !camera.IsUncompressed && camera.FramesPerSecond == 30, "Probe mode must be distinct from RAW and removed from FPS.");
                bool rejected = false;
                try { camera.Send(frame); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "Probe Bitmap sending must fail before any network bytes or JPEG encoding.");
                camera.SendAudio(new float[] { 0.1f });
            }
        }
        finally { Assert(worker.Join(7000), "Probe mock receiver did not finish."); listener.Stop(); }
        if (serverError != null) throw new Exception("Probe mock receiver failed.", serverError);
    }

    private static void CheckMode(string keyPath, int width, int height, uint rate, bool raw, bool valid, bool h264Probe = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Exception serverError = null;
        var worker = new Thread(delegate()
        {
            try
            {
                using (TcpClient peer = listener.AcceptTcpClient())
                using (NetworkStream wire = peer.GetStream()) { wire.ReadTimeout = wire.WriteTimeout = 5000; Negotiate(wire, width, height, rate, raw, valid, h264Probe); }
            }
            catch (Exception error) { serverError = error; }
        }) { IsBackground = true };
        worker.Start();
        bool accepted = false;
        try { using (UvcCamera camera = new UvcCamera("127.0.0.1", port, keyPath)) accepted = camera.Width == width && camera.Height == height && camera.FramesPerSecond == rate && camera.IsUncompressed == raw && camera.IsH264Probe == h264Probe; }
        catch (IOException) { }
        finally { Assert(worker.Join(7000), "Mode mock receiver did not finish."); listener.Stop(); }
        if (serverError != null) throw new Exception("Mode mock receiver failed.", serverError);
        Assert(accepted == valid, "Unexpected high-resolution/RAW handshake validation result.");
    }

    private static byte[] Negotiate(NetworkStream wire, int width, int height, uint rate, bool raw, bool authenticate, bool h264Probe = false)
    {
        byte[] hello = new byte[48];
        Buffer.BlockCopy(new byte[] { 0x55, 0x42, 0x56, 0x43, 0, 0, 0, 1 }, 0, hello, 0, 8);
        hello[8] = (byte)(width >> 8); hello[9] = (byte)width;
        hello[10] = (byte)(height >> 8); hello[11] = (byte)height;
        uint flagged = rate | (raw ? 0x80000000U : 0) | (h264Probe ? 0x20000000U : 0);
        for (int i = 0; i < 4; i++) hello[12 + i] = (byte)(flagged >> (24 - 8 * i));
        for (int i = 16; i < hello.Length; i++) hello[i] = (byte)i;
        wire.Write(hello, 0, hello.Length);
        if (!authenticate)
        {
            Assert(wire.ReadByte() < 0, "An invalid mode must be rejected before client authentication.");
            return null;
        }
        byte[] secret;
        using (SHA256 hash = SHA256.Create()) secret = hash.ComputeHash(Encoding.UTF8.GetBytes(TestKey));
        byte[] signature = Read(wire, 32);
        Assert(Equal(signature, Mac(secret, Encoding.ASCII.GetBytes("USBBridge video authenticate"), hello)), "Mode negotiation HMAC mismatch.");
        wire.Write(Encoding.ASCII.GetBytes("OKAY"), 0, 4);
        byte[] proof = Mac(secret, Encoding.ASCII.GetBytes("USBBridge video receiver"), hello, signature);
        wire.Write(proof, 0, proof.Length);
        return Mac(secret, Encoding.ASCII.GetBytes("USBBridge video session"), hello);
    }

    private static byte[] Read(Stream stream, int count)
    {
        var result = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int received = stream.Read(result, offset, count - offset);
            if (received == 0) throw new EndOfStreamException();
            offset += received;
        }
        return result;
    }

    private static byte[] Mac(byte[] key, params byte[][] parts)
    {
        using (var value = new MemoryStream())
        using (var hmac = new HMACSHA256(key))
        {
            foreach (byte[] part in parts) value.Write(part, 0, part.Length);
            return hmac.ComputeHash(value.ToArray());
        }
    }

    private static bool Equal(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Interlocked.Increment(ref checks);
    }
}
