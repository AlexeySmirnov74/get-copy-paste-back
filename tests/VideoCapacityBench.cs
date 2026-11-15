using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

// A codec-only benchmark. It does not measure Horizon or a camera capture path.
internal static class VideoCapacityBench
{
    private static readonly Random random = new Random(924512);
    public static int Main(string[] args)
    {
        Console.WriteLine("width,height,cell,payload_bytes,raw_mbit_30,jpeg_quality,jpeg_bytes,encode_ms,jpeg_ms,decode_ms,bit_errors");
        int[][] modes = { new[] { 1280, 720 }, new[] { 1920, 1080 }, new[] { 3840, 2160 } };
        if (args.Length != 0 && args[0] == "8k") modes = new[] { new[] { 7680, 4320 } };
        foreach (int[] mode in modes)
        foreach (int cell in new[] { 1, 2, 3, 4 })
        {
            int columns = mode[0] / cell - 4, rows = mode[1] / cell - 4;
            byte[] payload = new byte[columns * rows / 8]; random.NextBytes(payload);
            var watch = Stopwatch.StartNew();
            using (Bitmap bitmap = Encode(mode[0], mode[1], cell, payload))
            {
                double encodeMs = watch.Elapsed.TotalMilliseconds;
                foreach (long quality in new long[] { 0, 50, 80, 90 })
                {
                    watch.Restart();
                    byte[] jpeg = quality == 0 ? null : Jpeg(bitmap, quality);
                    double jpegMs = watch.Elapsed.TotalMilliseconds;
                    watch.Restart();
                    int errors;
                    if (jpeg == null) errors = Errors(bitmap, cell, payload);
                    else using (var stream = new MemoryStream(jpeg)) using (Bitmap decoded = new Bitmap(stream)) errors = Errors(decoded, cell, payload);
                    double decodeMs = watch.Elapsed.TotalMilliseconds;
                    Console.WriteLine(String.Join(",", new string[] { mode[0].ToString(), mode[1].ToString(), cell.ToString(), payload.Length.ToString(), (payload.Length * 8.0 * 30 / 1000000).ToString("F3", System.Globalization.CultureInfo.InvariantCulture), quality.ToString(), (jpeg == null ? 0 : jpeg.Length).ToString(), F(encodeMs), F(jpegMs), F(decodeMs), errors.ToString() }));
                }
            }
        }
        return 0;
    }

    private static string F(double value) { return value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); }
    private static Bitmap Encode(int width, int height, int cell, byte[] data)
    {
        Bitmap frame = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        BitmapData locked = frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            int columns = width / cell - 4, rows = height / cell - 4;
            byte[] pixels = new byte[locked.Stride * height];
            for (int y = 0; y < rows; y++)
            {
                int line = (y + 2) * cell * locked.Stride;
                for (int x = 0; x < columns; x++)
                {
                    int bit = y * columns + x;
                    byte value = bit < data.Length * 8 && (data[bit / 8] & (1 << (7 - bit % 8))) != 0 ? (byte)255 : (byte)0;
                    int offset = line + (x + 2) * cell * 3;
                    for (int dx = 0; dx < cell * 3; dx++) pixels[offset + dx] = value;
                }
                for (int dy = 1; dy < cell; dy++) Buffer.BlockCopy(pixels, line, pixels, line + dy * locked.Stride, locked.Stride);
            }
            Marshal.Copy(pixels, 0, locked.Scan0, pixels.Length);
        }
        finally { frame.UnlockBits(locked); }
        return frame;
    }

    private static int Errors(Bitmap frame, int cell, byte[] expected)
    {
        BitmapData locked = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] pixels = new byte[locked.Stride * frame.Height]; Marshal.Copy(locked.Scan0, pixels, 0, pixels.Length);
            int errors = 0, columns = frame.Width / cell - 4;
            for (int bit = 0; bit < expected.Length * 8; bit++)
            {
                int x = (2 + bit % columns) * cell + cell / 2, y = (2 + bit / columns) * cell + cell / 2;
                int offset = y * locked.Stride + x * 3;
                bool white = pixels[offset] + pixels[offset + 1] + pixels[offset + 2] >= 128 * 3;
                if (white != ((expected[bit / 8] & (1 << (7 - bit % 8))) != 0)) errors++;
            }
            return errors;
        }
        finally { frame.UnlockBits(locked); }
    }

    private static byte[] Jpeg(Bitmap bitmap, long quality)
    {
        ImageCodecInfo encoder = null;
        foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders()) if (codec.FormatID == ImageFormat.Jpeg.Guid) encoder = codec;
        using (var stream = new MemoryStream()) using (var parameters = new EncoderParameters(1))
        {
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            bitmap.Save(stream, encoder, parameters); return stream.ToArray();
        }
    }
}
