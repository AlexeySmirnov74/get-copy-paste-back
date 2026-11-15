using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using HorizonClient;

internal static class ProtocolTests
{
    private static int assertions;

    public static int Main()
    {
        try
        {
            OfferedProfiles();
            RoundTrip(Encoding.UTF8.GetBytes("Текст буфера обмена: Hello, Horizon!\r\n第二行"));
            byte[] binary = new byte[VisualProtocol.ChunkSize * 4 + 19];
            new Random(173).NextBytes(binary);
            RoundTrip(binary);
            RoundTrip(new byte[0]);
            ImagesSurviveTransport();
            FastProfiles();
            RejectsCorruptionAndInvalidInput();
            AssemblerState();
            Console.WriteLine("Protocol tests passed ({0} assertions).", assertions);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void OfferedProfiles()
    {
        int[] expected = { 1, 2, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15 };
        VideoProfile[] profiles = VisualProtocol.GetProfiles();
        Check(profiles.Length == expected.Length && !VisualProtocol.IsOfferedProfile(VideoProfile.Compact320), "Experimental catalog appends four color modes without restoring 320p.");
        for (int index = 0; index < profiles.Length; index++)
        {
            Check((int)profiles[index] == expected[index], "Offered profiles retain their wire IDs and order");
            Check(VisualProtocol.IsOfferedProfile(profiles[index]) && VisualProtocol.GetWidth(profiles[index]) <= 3840 && VisualProtocol.GetHeight(profiles[index]) <= 2160,
                "Every catalog profile is at most3840x2160");
        }
        Check(VisualProtocol.GetAllProfiles().Length == 16, "Historical wire profiles remain defined alongside four appended color IDs.");
        Check(VisualProtocol.IsDefinedProfile(VideoProfile.Compatible) && !VisualProtocol.IsOfferedProfile(VideoProfile.Compatible),
            "Legacy compatible packets remain decodable but are not offered");
        foreach (VideoProfile experimental in new[] { VideoProfile.HighDensity720, VideoProfile.HighDensity1080 })
        {
            Check(VisualProtocol.IsDefinedProfile(experimental) && !VisualProtocol.IsOfferedProfile(experimental),
                "Historical one-pixel experimental packets remain decodable but are not offered");
            Check(VisualProtocol.GetChunkSize(experimental) > 0, "Historical one-pixel profile metadata remains valid");
        }
        foreach (VideoProfile legacy in new[] { VideoProfile.Ultra4K, VideoProfile.Maximum4K, VideoProfile.Ultra4KRobust })
        {
            Check(VisualProtocol.IsDefinedProfile(legacy) && VisualProtocol.IsOfferedProfile(legacy), "4K catalog entries preserve their historical wire IDs");
            Check(VisualProtocol.GetChunkSize(legacy) > 0, "Historical4K metadata remains valid");
        }
        Check(!VisualProtocol.IsOfferedProfile((VideoProfile)(-1)) && !VisualProtocol.IsOfferedProfile((VideoProfile)99), "Undefined profiles are not offered");
        profiles[0] = VideoProfile.Maximum4K;
        Check(VisualProtocol.GetProfiles()[0] == VideoProfile.Fast, "Offered choices are returned as independent arrays");
    }

    private static void RoundTrip(byte[] data)
    {
        Guid id = Guid.NewGuid();
        int count = Math.Max(1, (data.Length + VisualProtocol.ChunkSize - 1) / VisualProtocol.ChunkSize);
        TransferAssembler assembler = new TransferAssembler();
        byte[] output = null;
        for (int i = count - 1; i >= 0; i--)
        {
            Packet packet = MakePacket(id, data, i);
            using (Bitmap frame = VisualProtocol.EncodeFrame(id, i, count, data.Length, packet.Data))
            {
                Packet decoded = VisualProtocol.DecodeFrame(frame);
                Check(decoded != null && decoded.TransferId == id && decoded.Index == i,
                    "Decoded packet metadata");
                Equal(packet.Data, decoded.Data, "Decoded packet bytes");
                output = assembler.Add(decoded) ?? output;
                Check(assembler.Add(decoded) == null, "Duplicates do not re-emit completion");
            }
        }
        Equal(data, output, "Out-of-order reassembly");
        Check(assembler.ReceivedCount == count && assembler.TotalCount == count, "Completed progress");
    }

    private static void ImagesSurviveTransport()
    {
        byte[] data = new byte[VisualProtocol.ChunkSize];
        new Random(491).NextBytes(data);
        Guid id = Guid.NewGuid();
        using (Bitmap frame = VisualProtocol.EncodeFrame(id, 0, 1, data.Length, data))
        {
            for (int orientation = 0; orientation < 4; orientation++)
            {
                using (Bitmap changed = (Bitmap)frame.Clone())
                {
                    if ((orientation & 1) != 0) changed.RotateFlip(RotateFlipType.RotateNoneFlipX);
                    if ((orientation & 2) != 0) changed.RotateFlip(RotateFlipType.RotateNoneFlipY);
                    Packet decoded = VisualProtocol.DecodeFrame(changed);
                    Check(decoded != null, "Mirrored/upside-down frame");
                    Equal(data, decoded.Data, "Oriented bytes");
                }
            }
            using (Bitmap small = Resize(frame, 320, 240))
            {
                Packet decoded = VisualProtocol.DecodeFrame(small);
                Check(decoded != null, "320x240 frame");
                Equal(data, decoded.Data, "Scaled bytes");
                using (Bitmap jpeg = Jpeg(small, 40L))
                {
                    decoded = VisualProtocol.DecodeFrame(jpeg);
                    Check(decoded != null, "Lossy 320x240 JPEG frame");
                    Equal(data, decoded.Data, "JPEG bytes");
                }
            }
            using (Bitmap letterbox = new Bitmap(960, 540))
            {
                using (Graphics graphics = Graphics.FromImage(letterbox))
                {
                    graphics.Clear(Color.Black);
                    graphics.DrawImage(frame, new Rectangle(120, 0, 720, 540));
                }
                Packet decoded = VisualProtocol.DecodeFrame(letterbox);
                Check(decoded != null, "Centered 4:3 frame in 16:9 video");
                Equal(data, decoded.Data, "Letterboxed bytes");
            }
            using (Bitmap pillarbox = new Bitmap(640, 640))
            {
                using (Graphics graphics = Graphics.FromImage(pillarbox))
                {
                    graphics.Clear(Color.Black);
                    graphics.DrawImage(frame, new Rectangle(0, 80, 640, 480));
                }
                Check(VisualProtocol.DecodeFrame(pillarbox) != null, "Centered frame with top/bottom bars");
            }
        }
    }

    private static void RejectsCorruptionAndInvalidInput()
    {
        byte[] data = new byte[VisualProtocol.ChunkSize];
        Guid id = Guid.NewGuid();
        using (Bitmap frame = VisualProtocol.EncodeFrame(id, 0, 1, data.Length, data))
        {
            // First payload bit (byte 36), leaving the header intact.
            int bit = 36 * 8;
            int x = (2 + bit % 60) * 10;
            int y = (2 + bit / 60) * 10;
            using (Graphics graphics = Graphics.FromImage(frame))
                graphics.FillRectangle(Brushes.White, x, y, 10, 10);
            Check(VisualProtocol.DecodeFrame(frame) == null, "CRC rejects altered payload");
        }
        using (Bitmap frame = VisualProtocol.EncodeFrame(id, 0, 1, data.Length, data))
        {
            // Make the declared payload length 257, which must be rejected
            // before a payload-sized allocation or read is attempted.
            int bit = 6 * 8 + 7;
            using (Graphics graphics = Graphics.FromImage(frame))
                graphics.FillRectangle(Brushes.White, (2 + bit % 60) * 10, (2 + bit / 60) * 10, 10, 10);
            Check(VisualProtocol.DecodeFrame(frame) == null, "Oversize header rejected");
        }
        Check(VisualProtocol.DecodeFrame(null) == null, "Null input");
        using (Bitmap small = new Bitmap(64, 48))
            Check(VisualProtocol.DecodeFrame(small) == null, "Too-small image");
        using (Bitmap blank = new Bitmap(640, 480))
            Check(VisualProtocol.DecodeFrame(blank) == null, "Idle/blank image");
        Throws(delegate { VisualProtocol.EncodeFrame(id, 0, 1, 257, data); }, "Wrong packet count");
        Throws(delegate { VisualProtocol.EncodeFrame(id, -1, 1, 256, data); }, "Negative index");
        Throws(delegate { VisualProtocol.EncodeFrame(id, 0, 1, VisualProtocol.MaxTransferBytes + 1, data); },
            "Transfer byte limit");
        Throws(delegate { VisualProtocol.EncodeFrame(id, 0, int.MaxValue, 256, data); }, "Count limit");
    }

    private static void FastProfiles()
    {
        foreach (VideoProfile profile in new VideoProfile[] { VideoProfile.Fast, VideoProfile.Dense })
        {
            int size = VisualProtocol.GetChunkSize(profile);
            byte[] data = new byte[size];
            new Random(709 + size).NextBytes(data);
            Guid id = Guid.NewGuid();
            using (Bitmap frame = VisualProtocol.EncodeFrame(id, 0, 1, data.Length, data, profile))
            {
                Check(frame.Width == 1280 && frame.Height == 720, "720p profile dimensions");
                foreach (long quality in new long[] { 80, 50 })
                {
                    for (int orientation = 0; orientation < 4; orientation++)
                    {
                        using (Bitmap oriented = (Bitmap)frame.Clone())
                        {
                            if ((orientation & 1) != 0) oriented.RotateFlip(RotateFlipType.RotateNoneFlipX);
                            if ((orientation & 2) != 0) oriented.RotateFlip(RotateFlipType.RotateNoneFlipY);
                            using (Bitmap jpeg = Jpeg(oriented, quality))
                            {
                                Packet decoded = VisualProtocol.DecodeFrame(jpeg);
                                Check(decoded != null && decoded.Profile == profile && decoded.ChunkSize == size,
                                    profile + " JPEG" + quality + " orientation " + orientation);
                                Equal(data, decoded.Data, profile + " JPEG bytes");
                            }
                        }
                    }
                }
                int scaledWidth = profile == VideoProfile.Fast ? 640 : 960;
                int scaledHeight = profile == VideoProfile.Fast ? 360 : 540;
                using (Bitmap scaled = Resize(frame, scaledWidth, scaledHeight))
                using (Bitmap jpeg = Jpeg(scaled, 80))
                {
                    Packet decoded = VisualProtocol.DecodeFrame(jpeg);
                    Check(decoded != null, profile + " reduced resolution JPEG80");
                    Equal(data, decoded.Data, profile + " scaled bytes");
                }
                Stopwatch timer = Stopwatch.StartNew();
                const int iterations = 20;
                for (int i = 0; i < iterations; i++)
                    Check(VisualProtocol.DecodeFrame(frame) != null, profile + " repeated decode");
                timer.Stop();
                double decodeMilliseconds = timer.Elapsed.TotalMilliseconds / iterations;
                timer.Restart();
                for (int i = 0; i < iterations; i++)
                    using (Bitmap generated = VisualProtocol.EncodeFrame(id, 0, 1, data.Length, data, profile))
                        Check(generated.Width == 1280, profile + " repeated encode");
                timer.Stop();
                Console.WriteLine("{0}: 720p JPEG50/80 and mirrors passed; encode {1:F1}, decode {2:F1} ms/frame.",
                    profile, timer.Elapsed.TotalMilliseconds / iterations, decodeMilliseconds);
            }

            byte[] all = new byte[size * 2 + 19];
            new Random(size).NextBytes(all);
            TransferAssembler assembler = new TransferAssembler();
            byte[] result = null;
            for (int index = 2; index >= 0; index--)
            {
                byte[] chunk = new byte[Math.Min(size, all.Length - index * size)];
                Buffer.BlockCopy(all, index * size, chunk, 0, chunk.Length);
                using (Bitmap frame = VisualProtocol.EncodeFrame(id, index, 3, all.Length, chunk, profile))
                {
                    Packet packet = VisualProtocol.DecodeFrame(frame);
                    Check(packet != null, profile + " multi-packet decode");
                    result = assembler.Add(packet) ?? result;
                    Check(assembler.Add(packet) == null, profile + " duplicate packet");
                }
            }
            Equal(all, result, profile + " reverse-order multi-packet assembly");

            using (Bitmap damaged = VisualProtocol.EncodeFrame(id, 0, 1, data.Length, data, profile))
            {
                int cell = profile == VideoProfile.Fast ? 8 : 4;
                int columns = 1280 / cell;
                int bit = 36 * 8;
                int x = (2 + bit % (columns - 4)) * cell;
                int y = (2 + bit / (columns - 4)) * cell;
                bool white = damaged.GetPixel(x + cell / 2, y + cell / 2).R >= 128;
                using (Graphics graphics = Graphics.FromImage(damaged))
                    graphics.FillRectangle(white ? Brushes.Black : Brushes.White, x, y, cell, cell);
                Check(VisualProtocol.DecodeFrame(damaged) == null, profile + " CRC corruption");
            }
        }

        TransferAssembler conflictAssembler = new TransferAssembler();
        Guid transfer = Guid.NewGuid();
        Packet fast = new Packet { TransferId = transfer, Index = 0, Count = 2, TotalLength = 3072,
            Profile = VideoProfile.Fast, ChunkSize = 1536, Data = new byte[1536] };
        Check(conflictAssembler.Add(fast) == null && conflictAssembler.ReceivedCount == 1,
            "Fast profile starts assembly");
        Packet changed = new Packet { TransferId = transfer, Index = 0, Count = 1, TotalLength = 3072,
            Profile = VideoProfile.Dense, ChunkSize = 6144, Data = new byte[3072] };
        Check(conflictAssembler.Add(changed) == null && conflictAssembler.ReceivedCount == 1
            && conflictAssembler.TotalCount == 2, "Profile change rejected within one transfer");
        fast.Index = 1;
        fast.ChunkSize = 6144;
        Check(conflictAssembler.Add(fast) == null && conflictAssembler.ReceivedCount == 1,
            "Inconsistent declared chunk size rejected");
        fast.ChunkSize = 1536;
        Check(conflictAssembler.Add(fast).Length == 3072, "Valid original profile completes");
        Throws(delegate { VisualProtocol.GetChunkSize((VideoProfile)99); }, "Unknown profile");
    }

    private static void AssemblerState()
    {
        byte[] first = new byte[513];
        byte[] second = new byte[513];
        new Random(2).NextBytes(first);
        new Random(3).NextBytes(second);
        Guid id = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        TransferAssembler assembler = new TransferAssembler();
        Check(assembler.Add(MakePacket(id, first, 0)) == null, "Start transfer");
        Packet conflict = MakePacket(id, first, 1);
        conflict.TotalLength++;
        Check(assembler.Add(conflict) == null && assembler.ReceivedCount == 1, "Reject metadata conflict");
        conflict = MakePacket(id, first, 0);
        conflict.Data[0] ^= 255;
        Check(assembler.Add(conflict) == null && assembler.ReceivedCount == 1, "Reject conflicting duplicate");
        assembler.Add(MakePacket(other, second, 2));
        Check(assembler.TransferId == other && assembler.ReceivedCount == 1, "New transfer replaces unfinished state");
        assembler.Add(MakePacket(other, second, 0));
        Equal(second, assembler.Add(MakePacket(other, second, 1)), "New transfer completed");
        assembler.Add(MakePacket(id, first, 0));
        Check(assembler.Add(MakePacket(other, second, 0)) == null && assembler.TransferId == id,
            "Old completed frames cannot interrupt current transfer");
        Check(assembler.Add(new Packet { Count = int.MaxValue, TotalLength = int.MaxValue,
            Data = new byte[0] }) == null, "Malformed assembler metadata");
        assembler.Reset();
        Check(assembler.ReceivedCount == 0 && assembler.TotalCount == 0 && assembler.TransferId == Guid.Empty,
            "Reset clears progress");
        TransferAssembler expires = new TransferAssembler(TimeSpan.FromMilliseconds(5));
        expires.Add(MakePacket(id, first, 0));
        Thread.Sleep(20);
        expires.Add(MakePacket(id, first, 1));
        Check(expires.ReceivedCount == 1, "Expired transfer bytes discarded");
    }

    private static Packet MakePacket(Guid id, byte[] data, int index)
    {
        int count = Math.Max(1, (data.Length + VisualProtocol.ChunkSize - 1) / VisualProtocol.ChunkSize);
        int length = Math.Min(VisualProtocol.ChunkSize, data.Length - index * VisualProtocol.ChunkSize);
        byte[] chunk = new byte[length];
        Buffer.BlockCopy(data, index * VisualProtocol.ChunkSize, chunk, 0, length);
        return new Packet { TransferId = id, Index = index, Count = count,
            TotalLength = data.Length, Data = chunk };
    }

    private static Bitmap Resize(Bitmap image, int width, int height)
    {
        Bitmap result = new Bitmap(width, height);
        using (Graphics graphics = Graphics.FromImage(result))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image, new Rectangle(0, 0, width, height));
        }
        return result;
    }

    private static Bitmap Jpeg(Bitmap image, long quality)
    {
        ImageCodecInfo codec = null;
        foreach (ImageCodecInfo candidate in ImageCodecInfo.GetImageEncoders())
            if (candidate.MimeType == "image/jpeg") codec = candidate;
        using (MemoryStream stream = new MemoryStream())
        using (EncoderParameters parameters = new EncoderParameters(1))
        {
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            image.Save(stream, codec, parameters);
            stream.Position = 0;
            using (Bitmap compressed = new Bitmap(stream)) return new Bitmap(compressed);
        }
    }

    private static void Equal(byte[] expected, byte[] actual, string message)
    {
        Check(actual != null && expected.Length == actual.Length, message + " length");
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != actual[i]) throw new Exception(message + " mismatch at " + i);
        assertions++;
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { assertions++; return; }
        throw new Exception(message + " was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
    }
}
