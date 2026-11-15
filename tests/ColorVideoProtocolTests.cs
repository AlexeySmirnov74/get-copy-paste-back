using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using HorizonClient;

// Only synthetic bitmaps and memory streams. No camera, native backend,
// clipboard, audio devices, network or persistent files are accessed.
internal static class ColorVideoProtocolTests
{
    private static int checks;
    private static readonly VideoProfile[] Modes = { (VideoProfile)12, (VideoProfile)13, (VideoProfile)14, (VideoProfile)15 };
    private static readonly Color[] Colors = { Color.Black, Color.Red, Color.Yellow, Color.Blue };
    private static readonly Guid Identity = new Guid("1c704b40-cd97-4b5b-a709-dc5f1b17991a");
    private static int Main()
    {
        try
        {
            MetadataAndWireLayout(); AllModesAndAssembler(); ColorsAndGeometry(); SyntheticVideoConversion(); CorruptionAndLoss();
            Console.WriteLine("Four-color protocol checks passed: " + checks + ". Synthetic RGB/JPEG/YUY2/4:2:0 fixtures only; real Horizon performance is untested.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void MetadataAndWireLayout()
    {
        Check(ColorVideoProtocol.Width == 1280 && ColorVideoProtocol.Height == 720 && ColorVideoProtocol.ChunkSize == 3072, "Default color framing doubles Fast payload without shrinking eight-pixel cells");
        Check(!ColorVideoProtocol.Supports(VideoProfile.Fast) && !ColorVideoProtocol.Supports((VideoProfile)16), "Only four explicitly allocated experimental wire IDs are supported");
        int[] sizes = { 3072, 12288, 6144, 24576 }, widths = { 1280, 1280, 1920, 1920 }, cells = { 8, 4, 8, 4 };
        for (int index = 0; index < Modes.Length; index++)
        {
            VideoProfile mode = Modes[index];
            Check(ColorVideoProtocol.GetChunkSize(mode) == sizes[index] && ColorVideoProtocol.GetWidth(mode) == widths[index] && ColorVideoProtocol.GetCellSize(mode) == cells[index], "Each mode keeps its distinct geometry and bounded chunk size");
            int capacity = (widths[index] / cells[index] - 4) * (ColorVideoProtocol.GetHeight(mode) / cells[index] - 5) / 4;
            Check(sizes[index] + 40 <= capacity, "A whole maximum packet and CRC fit after timing borders and calibration pilots");
            Throws(delegate { ColorVideoProtocol.EncodeFrame(Identity, 0, 1, sizes[index] + 1, new byte[sizes[index] + 1], mode); }, "Oversized chunks cannot overrun the cell grid");
            Throws(delegate { ColorVideoProtocol.EncodeFrame(Identity, -1, 1, 1, new byte[1], mode); }, "Negative packet indices are rejected");
            Throws(delegate { ColorVideoProtocol.EncodeFrame(Identity, 0, 2, 1, new byte[1], mode); }, "Count must match the mode's actual packetization");
            Throws(delegate { ColorVideoProtocol.EncodeFrame(Identity, 0, 1, 2, new byte[1], mode); }, "Short chunks cannot claim more bytes than they contain");
            Throws(delegate { ColorVideoProtocol.EncodeFrame(Identity, 0, 1, VisualProtocol.MaxTransferBytes + 1, new byte[1], mode); }, "Total transfer remains bounded to 32 MiB");
        }
        Throws(delegate { ColorVideoProtocol.EncodeFrame(Identity, 0, 1, 0, null); }, "Null content is invalid");
        Throws(delegate { ColorVideoProtocol.EncodeFrame(Identity, 0, 1, 1, new byte[1], VideoProfile.Fast); }, "Unrelated wire profiles cannot silently select the color renderer");
        using (Bitmap frame = ColorVideoProtocol.EncodeFrame(Identity, 0, 1, 1, new byte[] { 0x1b }))
        {
            // 0x1b = 00 01 10 11, independently fixing the two-bit symbol order.
            for (int symbol = 0; symbol < 4; symbol++)
            {
                int cell = 36 * 4 + symbol;
                Check(frame.GetPixel((2 + cell % 156) * 8 + 4, (3 + cell / 156) * 8 + 4).ToArgb() == Colors[symbol].ToArgb(), "Two-bit payload symbols render black, red, yellow and blue in documented order");
            }
            Packet packet = ColorVideoProtocol.DecodeFrame(frame);
            CheckPacket(packet, Identity, 0, 1, 1, new byte[] { 0x1b }, (VideoProfile)12);
        }
        Check(ColorVideoProtocol.DecodeFrame(null) == null, "Null images are rejected");
    }

    private static void AllModesAndAssembler()
    {
        foreach (VideoProfile mode in Modes)
        {
            int size = ColorVideoProtocol.GetChunkSize(mode); byte[] source = Bytes(size * 2 + 117, 7901 + (int)mode);
            var assembler = new TransferAssembler();
            for (int index = 0; index < 3; index++)
            {
                byte[] chunk = Slice(source, index * size, Math.Min(size, source.Length - index * size));
                var clock = Stopwatch.StartNew();
                using (Bitmap frame = ColorVideoProtocol.EncodeFrame(Identity, index, 3, source.Length, chunk, mode))
                {
                    double encodedMs = clock.Elapsed.TotalMilliseconds; clock.Restart();
                    Packet decoded = ColorVideoProtocol.DecodeFrame(frame, mode);
                    CheckPacket(decoded, Identity, index, 3, source.Length, chunk, mode);
                    if (index == 0) Console.WriteLine("{0}: {1} bytes/frame, encode={2:F2}ms decode={3:F2}ms CPU, theoretical ceiling={4:F5}Mbit/s before losses/control; synthetic only.", mode, size, encodedMs, clock.Elapsed.TotalMilliseconds, size * 8 * 30 / 1000000.0);
                    byte[] complete = assembler.Add(decoded);
                    if (index < 2) Check(complete == null, "A partial transfer cannot be published early");
                    else Same(complete, source, "Assembler reconstructs all color chunks including a partial final chunk");
                    Check(assembler.Add(decoded) == null, "Duplicate chunks cannot complete the same transfer twice");
                }
            }
            using (Bitmap empty = ColorVideoProtocol.EncodeFrame(Guid.Empty, 0, 1, 0, new byte[0], mode))
                CheckPacket(ColorVideoProtocol.DecodeFrame(empty), Guid.Empty, 0, 1, 0, new byte[0], mode);
        }
    }

    private static void ColorsAndGeometry()
    {
        foreach (VideoProfile mode in Modes)
        {
            byte[] source = Bytes(ColorVideoProtocol.GetChunkSize(mode), 221 + (int)mode);
            using (Bitmap frame = ColorVideoProtocol.EncodeFrame(Identity, 0, 1, source.Length, source, mode))
            {
                foreach (RotateFlipType flip in new[] { RotateFlipType.RotateNoneFlipX, RotateFlipType.RotateNoneFlipY, RotateFlipType.RotateNoneFlipXY })
                using (Bitmap mirror = (Bitmap)frame.Clone())
                {
                    mirror.RotateFlip(flip);
                    CheckPacket(ColorVideoProtocol.DecodeFrame(mirror, mode), Identity, 0, 1, source.Length, source, mode);
                }
                using (Bitmap letterbox = new Bitmap(frame.Width, frame.Height + 80))
                {
                    using (Graphics graphics = Graphics.FromImage(letterbox)) { graphics.Clear(Color.Black); graphics.DrawImageUnscaled(frame, 0, 40); }
                    CheckPacket(ColorVideoProtocol.DecodeFrame(letterbox, mode), Identity, 0, 1, source.Length, source, mode);
                }
                using (Bitmap pillarbox = new Bitmap(frame.Width + 160, frame.Height))
                {
                    using (Graphics graphics = Graphics.FromImage(pillarbox)) { graphics.Clear(Color.Black); graphics.DrawImageUnscaled(frame, 80, 0); }
                    CheckPacket(ColorVideoProtocol.DecodeFrame(pillarbox, mode), Identity, 0, 1, source.Length, source, mode);
                }
                using (Bitmap adjusted = Transform(frame, .77, .92, .68, 24, 16, 31))
                    CheckPacket(ColorVideoProtocol.DecodeFrame(adjusted, mode), Identity, 0, 1, source.Length, source, mode);
                using (Bitmap grayscale = Grayscale(frame)) Check(ColorVideoProtocol.DecodeFrame(grayscale, mode) == null, "Color pilots must reject a grayscale route even when luminance survives");
            }
        }
        byte[] scaledData = Bytes(3072, 719);
        using (Bitmap frame = ColorVideoProtocol.EncodeFrame(Identity, 0, 1, scaledData.Length, scaledData))
        foreach (InterpolationMode interpolation in new[] { InterpolationMode.NearestNeighbor, InterpolationMode.HighQualityBilinear })
        using (Bitmap reduced = Resize(frame, 640, 360, interpolation))
        {
            CheckPacket(ColorVideoProtocol.DecodeFrame(reduced), Identity, 0, 1, scaledData.Length, scaledData, (VideoProfile)12);
            using (Bitmap jpeg = Jpeg(reduced, 75)) CheckPacket(ColorVideoProtocol.DecodeFrame(jpeg), Identity, 0, 1, scaledData.Length, scaledData, (VideoProfile)12);
        }
    }

    private static void SyntheticVideoConversion()
    {
        foreach (VideoProfile mode in Modes)
        {
            byte[] source = Bytes(ColorVideoProtocol.GetChunkSize(mode), 460 + (int)mode);
            using (Bitmap frame = ColorVideoProtocol.EncodeFrame(Identity, 0, 1, source.Length, source, mode))
            {
                foreach (long quality in new long[] { 90, 65 })
                using (Bitmap jpeg = Jpeg(frame, quality)) CheckPacket(ColorVideoProtocol.DecodeFrame(jpeg, mode), Identity, 0, 1, source.Length, source, mode);
                foreach (bool verticallySubsampled in new[] { false, true })
                using (Bitmap chroma = ChromaSubsample(frame, verticallySubsampled))
                    CheckPacket(ColorVideoProtocol.DecodeFrame(chroma, mode), Identity, 0, 1, source.Length, source, mode);
                int targetWidth = ColorVideoProtocol.GetWidth(mode) == 1280 ? 640 : 1280;
                using (Bitmap reduced = Resize(frame, targetWidth, targetWidth * 9 / 16, InterpolationMode.HighQualityBilinear))
                using (Bitmap jpeg = Jpeg(reduced, 75))
                {
                    Packet packet = ColorVideoProtocol.DecodeFrame(jpeg, mode);
                    // A color-dense route can lose chroma after resize/JPEG. It
                    // must either recover exact bytes or reject the frame.
                    if (packet != null) CheckPacket(packet, Identity, 0, 1, source.Length, source, mode);
                    Console.WriteLine("{0} resized to{1} + JPEG75: {2}; a real Horizon route still needs the application speed test.", mode, targetWidth, packet == null ? "rejected safely" : "exact CRC-verified payload");
                }
            }
        }
    }

    private static void CorruptionAndLoss()
    {
        foreach (VideoProfile mode in Modes)
        {
            byte[] data = Bytes(Math.Min(813, ColorVideoProtocol.GetChunkSize(mode)), 111);
            foreach (int damagedByte in new[] { 0, 4, 5, 6, 8, 24, 28, 32, 36, 36 + 813 })
            using (Bitmap frame = ColorVideoProtocol.EncodeFrame(Identity, 0, 1, data.Length, data, mode))
            {
                ChangeSymbol(frame, mode, damagedByte * 4);
                Check(ColorVideoProtocol.DecodeFrame(frame, mode) == null, "Magic/version/profile/length/identity/metadata/payload/CRC corruption can never produce a packet");
            }
            using (Bitmap frame = ColorVideoProtocol.EncodeFrame(Identity, 0, 1, data.Length, data, mode))
            {
                int cell = ColorVideoProtocol.GetCellSize(mode), columns = ColorVideoProtocol.GetWidth(mode) / cell, band = (columns - 4) / 4;
                using (Graphics graphics = Graphics.FromImage(frame)) graphics.FillRectangle(Brushes.Red, (2 + 3 * band) * cell, 2 * cell, band * cell, cell);
                Check(ColorVideoProtocol.DecodeFrame(frame, mode) == null, "Collapsing blue and red pilot colors is rejected before content is guessed");
            }
        }
        foreach (Color color in new[] { Color.Black, Color.White, Color.Red, Color.Blue })
        using (Bitmap frame = new Bitmap(1280, 720))
        {
            using (Graphics graphics = Graphics.FromImage(frame)) graphics.Clear(color);
            Check(ColorVideoProtocol.DecodeFrame(frame) == null, "Blank/color-only camera frames cannot masquerade as a transfer");
        }
        using (Bitmap tiny = new Bitmap(160, 90)) Check(ColorVideoProtocol.DecodeFrame(tiny) == null, "An unreadable cell grid is rejected without decoding allocations");
        using (Bitmap grayProtocol = VisualProtocol.EncodeFrame(Identity, 0, 1, 7, new byte[7], VideoProfile.Fast))
            Check(ColorVideoProtocol.DecodeFrame(grayProtocol) == null, "Existing monochrome frames are never misidentified as color packets");
    }

    private static void ChangeSymbol(Bitmap frame, VideoProfile mode, int symbol)
    {
        int cell = ColorVideoProtocol.GetCellSize(mode), columns = ColorVideoProtocol.GetWidth(mode) / cell - 4;
        int x = (2 + symbol % columns) * cell, y = (3 + symbol / columns) * cell;
        Color previous = frame.GetPixel(x + cell / 2, y + cell / 2);
        using (Graphics graphics = Graphics.FromImage(frame))
        using (var brush = new SolidBrush(previous.ToArgb() == Color.Red.ToArgb() ? Color.Black : Color.Red)) graphics.FillRectangle(brush, x, y, cell, cell);
    }
    private static Bitmap Resize(Bitmap source, int width, int height, InterpolationMode mode)
    {
        var image = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(image)) { graphics.InterpolationMode = mode; graphics.PixelOffsetMode = PixelOffsetMode.Half; graphics.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel); }
        return image;
    }
    private static Bitmap Transform(Bitmap source, double r, double g, double b, double ro, double go, double bo)
    {
        var image = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(image))
        using (var attributes = new ImageAttributes())
        {
            attributes.SetColorMatrix(new ColorMatrix(new[] { new[] { (float)r, 0f, 0f, 0f, 0f }, new[] { 0f, (float)g, 0f, 0f, 0f }, new[] { 0f, 0f, (float)b, 0f, 0f }, new[] { 0f, 0f, 0f, 1f, 0f }, new[] { (float)(ro / 255), (float)(go / 255), (float)(bo / 255), 0f, 1f } }));
            graphics.DrawImage(source, new Rectangle(0, 0, image.Width, image.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        return image;
    }
    private static Bitmap Grayscale(Bitmap source)
    {
        var image = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(image))
        using (var attributes = new ImageAttributes())
        {
            attributes.SetColorMatrix(new ColorMatrix(new[] { new[] { .299f, .299f, .299f, 0f, 0f }, new[] { .587f, .587f, .587f, 0f, 0f }, new[] { .114f, .114f, .114f, 0f, 0f }, new[] { 0f, 0f, 0f, 1f, 0f }, new[] { 0f, 0f, 0f, 0f, 1f } }));
            graphics.DrawImage(source, new Rectangle(0, 0, image.Width, image.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        return image;
    }
    private static Bitmap Jpeg(Bitmap source, long quality)
    {
        ImageCodecInfo encoder = null; foreach (ImageCodecInfo item in ImageCodecInfo.GetImageEncoders()) if (item.FormatID == ImageFormat.Jpeg.Guid) encoder = item;
        using (var bytes = new MemoryStream())
        using (var parameters = new EncoderParameters(1))
        {
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality); source.Save(bytes, encoder, parameters);
            bytes.Position = 0; using (Bitmap decoded = new Bitmap(bytes)) return new Bitmap(decoded);
        }
    }
    private static Bitmap ChromaSubsample(Bitmap source, bool vertical)
    {
        var image = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        BitmapData input = source.LockBits(new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb),
            output = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var src = new byte[input.Stride * source.Height]; var dst = new byte[output.Stride * image.Height]; Marshal.Copy(input.Scan0, src, 0, src.Length);
            int rows = vertical ? 2 : 1;
            for (int y = 0; y < source.Height; y += rows) for (int x = 0; x < source.Width; x += 2)
            {
                double cb = 0, cr = 0;
                for (int dy = 0; dy < rows; dy++) for (int dx = 0; dx < 2; dx++)
                {
                    int offset = (y + dy) * input.Stride + (x + dx) * 3;
                    double b = src[offset], g = src[offset + 1], r = src[offset + 2];
                    cb += -.168736 * r - .331264 * g + .5 * b; cr += .5 * r - .418688 * g - .081312 * b;
                }
                cb /= 2 * rows; cr /= 2 * rows;
                for (int dy = 0; dy < rows; dy++) for (int dx = 0; dx < 2; dx++)
                {
                    int offset = (y + dy) * input.Stride + (x + dx) * 3, target = (y + dy) * output.Stride + (x + dx) * 3;
                    double luma = .299 * src[offset + 2] + .587 * src[offset + 1] + .114 * src[offset];
                    dst[target] = Byte(luma + 1.772 * cb); dst[target + 1] = Byte(luma - .344136 * cb - .714136 * cr); dst[target + 2] = Byte(luma + 1.402 * cr);
                }
            }
            Marshal.Copy(dst, 0, output.Scan0, dst.Length);
        }
        finally { source.UnlockBits(input); image.UnlockBits(output); }
        return image;
    }
    private static byte Byte(double value) { return (byte)Math.Max(0, Math.Min(255, Math.Round(value))); }
    private static byte[] Bytes(int length, int seed) { var bytes = new byte[length]; new Random(seed).NextBytes(bytes); return bytes; }
    private static byte[] Slice(byte[] source, int offset, int length) { var bytes = new byte[length]; Buffer.BlockCopy(source, offset, bytes, 0, length); return bytes; }
    private static void CheckPacket(Packet packet, Guid id, int index, int count, int total, byte[] expected, VideoProfile mode)
    {
        Check(packet != null, "A supported synthetic route must recover its packet: " + mode);
        Check(packet.TransferId == id && packet.Index == index && packet.Count == count && packet.TotalLength == total && packet.Profile == mode && packet.ChunkSize == ColorVideoProtocol.GetChunkSize(mode), "CRC-validated packets preserve all transfer/session metadata and the exact mode");
        Same(packet.Data, expected, "All decoded bytes match the source");
    }
    private static void Same(byte[] actual, byte[] expected, string message)
    { Check(actual != null && actual.Length == expected.Length, message); for (int index = 0; index < expected.Length; index++) Check(actual[index] == expected[index], message); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static void Throws(Action action, string message) { try { action(); } catch (ArgumentException) { checks++; return; } throw new Exception(message); }
}
