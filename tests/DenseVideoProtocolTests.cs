using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Reflection;
using HorizonClient;

internal static class DenseVideoProtocolTests
{
    private static int assertions;
    private static readonly Random random = new Random(740189);
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--benchmark") { Benchmark(); return 0; }
            foreach (VideoProfile profile in VisualProtocol.GetAllProfiles())
            {
                if (!DenseVideoProtocol.Supports(profile)) continue;
                int capacity = VisualProtocol.GetFramePayloadBytes(profile);
                byte[] expected = new byte[capacity + 111]; random.NextBytes(expected);
                Guid id = Guid.NewGuid(); var assembler = new TransferAssembler();
                var watch = Stopwatch.StartNew();
                using (Bitmap frame = VisualProtocol.EncodePacketFrame(id, 0, expected, profile))
                {
                    double encodeMs = watch.Elapsed.TotalMilliseconds;
                    watch.Restart(); Packet[] packets = VisualProtocol.DecodePackets(frame); double decodeMs = watch.Elapsed.TotalMilliseconds;
                    Check(packets.Length == VisualProtocol.GetPacketsPerFrame(profile), profile + " full tile count");
                    foreach (Packet packet in packets)
                    {
                        EqualChunk(expected, packet); Check(assembler.Add(packet) == null, "First frame remains incomplete");
                    }
                    Console.WriteLine("{0}: {1} packets, {2} bytes/frame, encode {3:F1} ms, decode {4:F1} ms", profile, packets.Length, capacity, encodeMs, decodeMs);
                    foreach (long quality in new long[] { 50, 90 })
                    using (Bitmap compressed = Jpeg(frame, quality))
                    {
                        Packet[] recovered = VisualProtocol.DecodePackets(compressed);
                        Check(recovered.Length == packets.Length, profile + " JPEG" + quality + " tile count");
                        foreach (Packet packet in recovered) EqualChunk(expected, packet);
                    }
                    if (profile == VideoProfile.HighDensity720 || profile == VideoProfile.Ultra4K)
                    {
                        foreach (RotateFlipType orientation in new[] { RotateFlipType.RotateNoneFlipX, RotateFlipType.RotateNoneFlipY, RotateFlipType.RotateNoneFlipXY })
                        using (Bitmap flipped = (Bitmap)frame.Clone())
                        {
                            flipped.RotateFlip(orientation); Packet[] recovered = VisualProtocol.DecodePackets(flipped);
                            Check(recovered.Length == packets.Length, "Mirror tile count"); foreach (Packet packet in recovered) EqualChunk(expected, packet);
                        }
                    }
                }
                using (Bitmap last = VisualProtocol.EncodePacketFrame(id, VisualProtocol.GetPacketsPerFrame(profile), expected, profile))
                {
                    Packet[] packets = VisualProtocol.DecodePackets(last); Check(packets.Length == 1, "Partial final frame contains one tile");
                    byte[] complete = assembler.Add(packets[0]); Equal(expected, complete, "Complete tiled transfer");
                }
                byte[] shortPacket = new byte[111]; random.NextBytes(shortPacket);
                using (Bitmap single = VisualProtocol.EncodeFrame(Guid.NewGuid(), 0, 1, shortPacket.Length, shortPacket, profile))
                {
                    Packet decoded = VisualProtocol.DecodeFrame(single); Equal(shortPacket, decoded.Data, "Legacy single-packet API");
                }
            }
            PartialRecovery(); MirroringAndLetterbox(); CompactFrames(); InvalidMetadata(); GoldenPixelLayout(); Robust720PhaseRecovery(); CompactJpegPhaseRecovery();
            Console.WriteLine("Dense video protocol tests passed ({0} assertions).", assertions); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Benchmark()
    {
        const int iterations = 20;
        Console.WriteLine("profile,iterations,payload_bytes,encode_ms,decode_ms");
        foreach (VideoProfile profile in new[] { VideoProfile.HighDensity1080, VideoProfile.Maximum4K, VideoProfile.Ultra4KRobust })
        {
            byte[] payload = new byte[VisualProtocol.GetFramePayloadBytes(profile)]; new Random(8113).NextBytes(payload); Guid id = Guid.NewGuid();
            for (int warm = 0; warm < 3; warm++) using (Bitmap image = VisualProtocol.EncodePacketFrame(id, 0, payload, profile)) VisualProtocol.DecodePackets(image);
            double encode = 0, decode = 0; var watch = new Stopwatch();
            for (int i = 0; i < iterations; i++)
            {
                watch.Restart(); Bitmap image = VisualProtocol.EncodePacketFrame(id, 0, payload, profile); encode += watch.Elapsed.TotalMilliseconds;
                using (image)
                {
                    watch.Restart(); Packet[] packets = VisualProtocol.DecodePackets(image); decode += watch.Elapsed.TotalMilliseconds;
                    Check(packets.Length == VisualProtocol.GetPacketsPerFrame(profile), "Benchmark decodes every independent packet");
                    foreach (Packet packet in packets) EqualChunk(payload, packet);
                }
            }
            Console.WriteLine("{0},{1},{2},{3},{4}", profile, iterations, payload.Length, (encode / iterations).ToString("F3", System.Globalization.CultureInfo.InvariantCulture), (decode / iterations).ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static void GoldenPixelLayout()
    {
        // These RGB hashes were recorded from the original RGB24 renderer before
        // the byte-at-a-time optimization, including timing borders and padding.
        object[][] cases = {
            new object[] { VideoProfile.HighDensity720, 111, "aea89fb15663e1588271bd414ed01c8adcf8480d5599f79032b9ba3cfb5ea083" },
            new object[] { VideoProfile.HighDensity720, 110592, "23c2b177ddef744bdfacd53c49681e933a991ac3c4b368d85bf9882cba38d127" },
            new object[] { VideoProfile.HighDensity1080, 111, "ddace2004539768f6d1d8cd19ac304e3eba6edb8002b58d4da2df91d0bc879b8" },
            new object[] { VideoProfile.HighDensity1080, 253952, "f2121b9b608d0d6a094be9cd3004a7379487f388189fb2ba34d6369691e9cb50" },
            new object[] { VideoProfile.Maximum4K, 111, "598a7ccedd7106d48539bc29ddf4791f37bc392de3cbd3548a4863523422a1c2" },
            new object[] { VideoProfile.Maximum4K, 1019904, "e14083e8276f23a9ed9cc6a19ec9fb5788765cf9105cfcb229c9beffe1be746a" }
        };
        foreach (object[] item in cases)
        {
            byte[] payload = new byte[(int)item[1]]; new Random(8113).NextBytes(payload);
            using (Bitmap bitmap = VisualProtocol.EncodePacketFrame(new Guid("d1878357-7a59-4b26-a467-11c2f986cf3a"), 0, payload, (VideoProfile)item[0]))
            using (SHA256 hash = SHA256.Create())
            {
                BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    byte[] row = new byte[bitmap.Width * 3];
                    for (int y = 0; y < bitmap.Height; y++) { System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), row, 0, row.Length); hash.TransformBlock(row, 0, row.Length, row, 0); }
                    hash.TransformFinalBlock(new byte[0], 0, 0);
                    Check(BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant() == (string)item[2], "Optimized renderer preserves every RGB wire pixel");
                }
                finally { bitmap.UnlockBits(locked); }
            }
        }
    }

    private static void PartialRecovery()
    {
        VideoProfile profile = VideoProfile.HighDensity720;
        byte[] expected = new byte[VisualProtocol.GetFramePayloadBytes(profile)]; random.NextBytes(expected);
        using (Bitmap frame = VisualProtocol.EncodePacketFrame(Guid.NewGuid(), 0, expected, profile))
        {
            // One corrupted pixel in the first payload must reject only its tile.
            int bit = (40 + 123) * 8, columns = 1280 - 4, x = 2 + bit % columns, y = 2 + bit / columns;
            Color previous = frame.GetPixel(x, y); frame.SetPixel(x, y, previous.R >= 128 ? Color.Black : Color.White);
            Packet[] recovered = VisualProtocol.DecodePackets(frame);
            Check(recovered.Length == VisualProtocol.GetPacketsPerFrame(profile) - 1, "CRC damage discards one tile");
            foreach (Packet packet in recovered) { Check(packet.Index != 0, "Corrupted tile was rejected"); EqualChunk(expected, packet); }
        }
    }

    private static void MirroringAndLetterbox()
    {
        VideoProfile profile = VideoProfile.HighDefinitionRobust;
        byte[] expected = new byte[VisualProtocol.GetFramePayloadBytes(profile)]; random.NextBytes(expected);
        using (Bitmap frame = VisualProtocol.EncodePacketFrame(Guid.NewGuid(), 0, expected, profile))
        using (Bitmap letterbox = new Bitmap(1920, 1200))
        {
            using (Graphics graphics = Graphics.FromImage(letterbox)) { graphics.Clear(Color.Black); graphics.DrawImageUnscaled(frame, 0, 60); }
            Packet[] packets = VisualProtocol.DecodePackets(letterbox); Check(packets.Length == 3, "1080p four-pixel cells in letterbox");
            foreach (Packet packet in packets) EqualChunk(expected, packet);
        }
        profile = VideoProfile.Ultra4K;
        expected = new byte[VisualProtocol.GetFramePayloadBytes(profile)]; random.NextBytes(expected);
        using (Bitmap frame = VisualProtocol.EncodePacketFrame(Guid.NewGuid(), 0, expected, profile))
        using (Bitmap small = new Bitmap(1920, 1080))
        {
            using (Graphics graphics = Graphics.FromImage(small)) { graphics.InterpolationMode = InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode = PixelOffsetMode.Half; graphics.DrawImage(frame, 0, 0, 1920, 1080); }
            Packet[] packets = VisualProtocol.DecodePackets(small); Check(packets.Length == VisualProtocol.GetPacketsPerFrame(profile), "4K two-pixel cells downsample to 1080p");
            foreach (Packet packet in packets) EqualChunk(expected, packet);
        }
    }

    private static void InvalidMetadata()
    {
        Check(!VisualProtocol.IsDefinedProfile((VideoProfile)99), "Unknown profile rejected");
        Throws(delegate { DenseVideoProtocol.EncodePacketFrame(Guid.NewGuid(), -1, new byte[123], VideoProfile.HighDensity720); }, "Negative packet index");
        Throws(delegate { DenseVideoProtocol.EncodeFrame(Guid.NewGuid(), 0, 1, 4097, new byte[4097], VideoProfile.HighDensity720); }, "Tile size limit");
        Throws(delegate { DenseVideoProtocol.EncodeFrame(Guid.NewGuid(), 0, 1, VisualProtocol.MaxTransferBytes + 1, new byte[1], VideoProfile.HighDensity720); }, "Total transfer bound");
        VideoProfile[] profiles = VisualProtocol.GetProfiles(); profiles[0] = (VideoProfile)99;
        Check(VisualProtocol.GetProfiles()[0] == VideoProfile.Fast, "Profile list does not expose mutable shared state");
    }

    private static void CompactFrames()
    {
        foreach (VideoProfile profile in new[] { VideoProfile.Compact320, VideoProfile.Compact640 })
        {
            byte[] payload = new byte[VisualProtocol.GetFramePayloadBytes(profile)]; random.NextBytes(payload);
            using (Bitmap original = VisualProtocol.EncodePacketFrame(Guid.NewGuid(), 0, payload, profile))
            using (Bitmap reduced = new Bitmap(320, 240))
            {
                using (Graphics graphics = Graphics.FromImage(reduced))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBilinear; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(original, new Rectangle(0, 0, 320, 240), 0, 0, original.Width, original.Height, GraphicsUnit.Pixel);
                }
                using (Bitmap video = Jpeg(reduced, 40))
                {
                    Packet[] packets = VisualProtocol.DecodePackets(video);
                    Check(packets.Length == VisualProtocol.GetPacketsPerFrame(profile), profile + " 320x240 JPEG40");
                    foreach (Packet packet in packets) EqualChunk(payload, packet);
                }
            }
        }
    }

    private static Bitmap Jpeg(Bitmap image, long quality)
    {
        ImageCodecInfo encoder = null; foreach (ImageCodecInfo candidate in ImageCodecInfo.GetImageEncoders()) if (candidate.FormatID == ImageFormat.Jpeg.Guid) encoder = candidate;
        using (var stream = new MemoryStream()) using (var parameters = new EncoderParameters(1))
        {
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality); image.Save(stream, encoder, parameters);
            stream.Position = 0; using (Bitmap decoded = new Bitmap(stream)) return new Bitmap(decoded);
        }
    }

    private static void CompactJpegPhaseRecovery()
    {
        const VideoProfile profile = VideoProfile.Compact640;
        string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "compact-phase-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            Check(DiagnosticLog.Initialize(log, 1024 * 1024, 1), "Compact recovery diagnostics start");
            Guid[] identities = { new Guid("0d2a47a9-e5ec-7e32-ddc9-7a963528bb22"), new Guid("2f41fd1a-6439-640e-7d6f-031f71ea524a") };
            int[] seeds = { 915008, 915046 };
            for (int fixture = 0; fixture < identities.Length; fixture++)
            {
                byte[] expected = new byte[1792]; new Random(seeds[fixture]).NextBytes(expected);
                using (Bitmap native = VisualProtocol.EncodePacketFrame(identities[fixture], 0, expected, profile))
                using (Bitmap reduced = ReduceCompact(native))
                using (Bitmap video = Jpeg(reduced, 40))
                {
                    long attempted = PhaseCount(profile, "phaseAttempted"), recovered = PhaseCount(profile, "phaseRecovered");
                    Packet[] packets = DenseVideoProtocol.DecodePackets(video, profile);
                    Check(packets.Length == 7, "Fixed JPEG40 regression recovers payload/header sampling loss, fixture " + fixture);
                    foreach (Packet packet in packets) { Check(packet.TransferId == identities[fixture], "Recovered compact tile preserves its exact transfer identity"); EqualChunk(expected, packet); }
                    Check(PhaseCount(profile, "phaseAttempted") > attempted && PhaseCount(profile, "phaseAttempted") - attempted <= 8 &&
                        PhaseCount(profile, "phaseRecovered") > recovered, "The compact regression actually exercises bounded CRC-validated phase recovery");
                }
            }
            var ids = new Random(740189);
            for (int fixture = 0; fixture < 64; fixture++)
            {
                byte[] identity = new byte[16]; ids.NextBytes(identity);
                byte[] expected = new byte[1792]; new Random(915000 + fixture).NextBytes(expected);
                using (Bitmap native = VisualProtocol.EncodePacketFrame(new Guid(identity), 0, expected, profile))
                using (Bitmap reduced = ReduceCompact(native))
                using (Bitmap video = Jpeg(reduced, 40))
                {
                    long before = PhaseCount(profile, "phaseAttempted"); Packet[] packets = VisualProtocol.DecodePackets(video);
                    Check(packets.Length == 7, "Deterministic GUID/payload sweep retains every compact packet after resize/JPEG40");
                    foreach (Packet packet in packets) EqualChunk(expected, packet);
                    Check(PhaseCount(profile, "phaseAttempted") - before <= 8, "A compact frame never exceeds eight recovery candidates");
                }
            }
            using (Bitmap idle = new Bitmap(320, 240))
            {
                long before = PhaseCount(profile, "phaseAttempted");
                Check(DenseVideoProtocol.DecodePackets(idle, profile).Length == 0 && PhaseCount(profile, "phaseAttempted") == before,
                    "Idle without any CRC-valid tile/header does not trigger compact recovery");
            }
            byte[] damagedPayload = new byte[1792]; new Random(915046).NextBytes(damagedPayload);
            using (Bitmap damaged = VisualProtocol.EncodePacketFrame(identities[1], 0, damagedPayload, profile))
            {
                int bit = (40 + 123) * 8, x = (2 + bit % 156) * 4, y = (2 + bit / 156) * 4;
                Color previous = damaged.GetPixel(x, y);
                using (Graphics graphics = Graphics.FromImage(damaged)) graphics.FillRectangle(previous.R < 128 ? Brushes.White : Brushes.Black, x, y, 4, 4);
                using (Bitmap reduced = ReduceCompact(damaged))
                using (Bitmap video = Jpeg(reduced, 40))
                {
                    long before = PhaseCount(profile, "phaseAttempted"); Packet[] packets = DenseVideoProtocol.DecodePackets(video, profile);
                    Check(packets.Length == 6, "A truly inverted payload cell still rejects its packet after compact phase recovery");
                    foreach (Packet packet in packets) { Check(packet.Index != 0, "Recovery never bypasses compact CRC damage"); EqualChunk(damagedPayload, packet); }
                    Check(PhaseCount(profile, "phaseAttempted") - before <= 8, "Compact CRC damage cannot trigger an unbounded search");
                }
            }
        }
        finally { DiagnosticLog.Shutdown(); if (File.Exists(log)) File.Delete(log); }
    }

    private static Bitmap ReduceCompact(Bitmap native)
    {
        var reduced = new Bitmap(320, 240);
        using (Graphics graphics = Graphics.FromImage(reduced))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(native, new Rectangle(0, 0, 320, 240), 0, 0, native.Width, native.Height, GraphicsUnit.Pixel);
        }
        return reduced;
    }

    private static void Robust720PhaseRecovery()
    {
        string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dense-phase-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            Check(DiagnosticLog.Initialize(log, 1024 * 1024, 1), "Phase recovery diagnostic fixture starts");
            const VideoProfile profile = VideoProfile.HighDefinitionRobust;
            byte[] expected = new byte[102400]; new Random(8113).NextBytes(expected);
            Guid id = new Guid("d1878357-7a59-4b26-a467-11c2f986cf3a");
            using (Bitmap native = VisualProtocol.EncodePacketFrame(id, 0, expected, profile))
            {
                long before = PhaseCount("phaseAttempted");
                Check(DenseVideoProtocol.DecodePackets(native, profile).Length == 3, "Healthy full-resolution robust frame remains valid");
                using (Bitmap reduced = ReduceRobust(native, InterpolationMode.HighQualityBilinear, 0, 0))
                using (Bitmap video = Jpeg(reduced, 40))
                    Check(DenseVideoProtocol.DecodePackets(video, profile).Length == 3, "Healthy720p resize/JPEG remains valid");
                using (Bitmap idle = new Bitmap(1280, 720))
                    Check(DenseVideoProtocol.DecodePackets(idle, profile).Length == 0, "No-header idle frame stays undecoded");
                Check(PhaseCount("phaseAttempted") == before, "Healthy full/native/downscaled and no-header idle frames perform zero phase retries");
            }
            int frames = 0; var watch = Stopwatch.StartNew();
            foreach (InterpolationMode mode in new[] { InterpolationMode.HighQualityBilinear, InterpolationMode.HighQualityBicubic })
            foreach (float phase in new[] { -.5F, .5F })
            {
                var assembler = new TransferAssembler(); byte[] complete = null;
                for (int index = 0; index < 25; index += 3)
                using (Bitmap native = VisualProtocol.EncodePacketFrame(id, index, expected, profile))
                using (Bitmap reduced = ReduceRobust(native, mode, phase, -.5F))
                using (Bitmap video = Jpeg(reduced, 40))
                {
                    long before = PhaseCount("phaseAttempted"); Packet[] packets = DenseVideoProtocol.DecodePackets(video, profile);
                    Check(packets.Length == Math.Min(3, 25 - index), "Half-pixel720p resize/JPEG recovers every independent robust packet");
                    Check(PhaseCount("phaseAttempted") - before <= 8, "Phase recovery is bounded to eight candidates for the complete frame, including all orientations");
                    foreach (Packet packet in packets) { EqualChunk(expected, packet); byte[] bytes = assembler.Add(packet); if (bytes != null) complete = bytes; }
                    frames++;
                }
                Equal(expected, complete, "The complete100KiB file survives fractional720p resampling/JPEG40");
            }
            Check(PhaseCount("phaseRecovered") > 0, "The regression actually exercises CRC-validated phase recovery");
            using (Bitmap damaged = VisualProtocol.EncodePacketFrame(id, 0, expected, profile))
            {
                int bit = (40 + 123) * 8, columns = 1920 / 4 - 4, x = (2 + bit % columns) * 4, y = (2 + bit / columns) * 4;
                Color original = damaged.GetPixel(x, y);
                using (Graphics graphics = Graphics.FromImage(damaged))
                    graphics.FillRectangle(original.R < 128 ? Brushes.White : Brushes.Black, x, y, 4, 4);
                using (Bitmap reduced = ReduceRobust(damaged, InterpolationMode.HighQualityBilinear, 0, 0))
                using (Bitmap video = Jpeg(reduced, 40))
                {
                    long before = PhaseCount("phaseAttempted"); Packet[] packets = DenseVideoProtocol.DecodePackets(video, profile);
                    Check(packets.Length == 2, "An inverted complete payload cell still rejects only its damaged packet");
                    foreach (Packet packet in packets) { Check(packet.Index != 0, "Phase recovery never bypasses a damaged packet's CRC"); EqualChunk(expected, packet); }
                    Check(PhaseCount("phaseAttempted") - before <= 8, "Unrecoverable CRC damage cannot trigger an unbounded search");
                }
            }
            Console.WriteLine("Robust720 phase recovery: verified100KiB across4 synthetic bilinear/bicubic/JPEG40 variants, {0} frames, {1:F2}ms/frame including rendering+JPEG; not a Horizon measurement.", frames, watch.Elapsed.TotalMilliseconds / frames);
        }
        finally { DiagnosticLog.Shutdown(); if (File.Exists(log)) File.Delete(log); }
    }

    private static Bitmap ReduceRobust(Bitmap native, InterpolationMode mode, float phaseX, float phaseY)
    {
        var reduced = new Bitmap(1280, 720, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(reduced))
        {
            graphics.Clear(Color.Black); graphics.InterpolationMode = mode; graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(native, new RectangleF(phaseX, phaseY, 1280, 720), new RectangleF(0, 0, native.Width, native.Height), GraphicsUnit.Pixel);
        }
        return reduced;
    }

    private static long PhaseCount(string stage)
    { return PhaseCount(VideoProfile.HighDefinitionRobust, stage); }

    private static long PhaseCount(VideoProfile profile, string stage)
    {
        Type type = typeof(VideoDecoderDiagnostics);
        object gate = type.GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        lock (gate)
        {
            var counts = (System.Collections.IDictionary)type.GetField("Counts", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            object count = counts["dense." + (int)profile + "." + stage]; return count == null ? 0 : (long)count;
        }
    }
    private static void EqualChunk(byte[] expected, Packet packet)
    {
        Check(packet != null && packet.ChunkSize == VisualProtocol.GetChunkSize(packet.Profile), "Tiled packet size");
        int offset = packet.Index * packet.ChunkSize; for (int i = 0; i < packet.Data.Length; i++) if (packet.Data[i] != expected[offset + i]) throw new Exception("Recovered payload differs"); assertions++;
    }
    private static void Equal(byte[] expected, byte[] actual, string message)
    {
        Check(actual != null && actual.Length == expected.Length, message); for (int i = 0; i < expected.Length; i++) if (actual[i] != expected[i]) throw new Exception(message); assertions++;
    }
    private static void Throws(Action action, string message) { try { action(); } catch (ArgumentException) { assertions++; return; } throw new Exception(message); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }
}
