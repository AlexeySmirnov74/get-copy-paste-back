using System;
using System.Diagnostics;
using System.IO;

// A deliberately reproducible local H.264 experiment, independent of Horizon.
internal static class VideoCompressionBench
{
    private const int Frames = 30;
    public static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: VideoCompressionBench ffmpeg.exe output-directory"); return 2; }
        string directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
        Console.WriteLine("width,height,cell,payload_mbit_30,target_video_mbit,actual_video_mbit,bit_errors,perfect_frames,total_frames,encode_ms");
        foreach (int[] mode in new[] { new[] { 1920, 1080, 3 }, new[] { 1920, 1080, 4 }, new[] { 1920, 1080, 6 }, new[] { 3840, 2160, 3 }, new[] { 3840, 2160, 4 }, new[] { 3840, 2160, 6 } })
        {
            int width = mode[0], height = mode[1], cell = mode[2], columns = width / cell, rows = height / cell;
            string source = Path.Combine(directory, "source-gray.raw"), encoded = Path.Combine(directory, "encoded.mp4"), decoded = Path.Combine(directory, "decoded-gray.raw");
            byte[][] frames = new byte[Frames][];
            var random = new Random(673115);
            using (FileStream output = File.Create(source)) for (int i = 0; i < Frames; i++)
            {
                byte[] bytes = frames[i] = new byte[columns * rows / 8]; random.NextBytes(bytes);
                byte[] pixels = new byte[width * height];
                for (int y = 0; y < rows; y++)
                {
                    for (int x = 0; x < columns; x++)
                    {
                        int bit = y * columns + x;
                        byte value = bit < bytes.Length * 8 && (bytes[bit / 8] & (1 << (7 - bit % 8))) != 0 ? (byte)255 : (byte)0;
                        for (int dx = 0; dx < cell; dx++) pixels[y * cell * width + x * cell + dx] = value;
                    }
                    for (int dy = 1; dy < cell; dy++) Buffer.BlockCopy(pixels, y * cell * width, pixels, (y * cell + dy) * width, width);
                }
                output.Write(pixels, 0, pixels.Length);
            }
            foreach (int target in new[] { 5, 20, 50, 100 })
            {
                var watch = Stopwatch.StartNew();
                Run(args[0], "-hide_banner -loglevel error -y -f rawvideo -pixel_format gray -video_size " + width + "x" + height + " -framerate 30 -i " + Q(source) + " -c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -b:v " + target + "M -minrate " + target + "M -maxrate " + target + "M -bufsize " + (target * 100) + "k -x264-params nal-hrd=cbr:force-cfr=1 -g 30 " + Q(encoded));
                double encodeMs = watch.Elapsed.TotalMilliseconds;
                Run(args[0], "-hide_banner -loglevel error -y -i " + Q(encoded) + " -pix_fmt gray -f rawvideo " + Q(decoded));
                long errors = 0; int perfect = 0;
                using (FileStream input = File.OpenRead(decoded))
                {
                    for (int i = 0; i < Frames; i++)
                    {
                        byte[] pixels = new byte[width * height]; int offset = 0;
                        while (offset < pixels.Length) { int read = input.Read(pixels, offset, pixels.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
                        long before = errors;
                        for (int bit = 0; bit < frames[i].Length * 8; bit++)
                        {
                            int x = bit % columns, y = bit / columns;
                            if ((pixels[(y * cell + cell / 2) * width + x * cell + cell / 2] >= 128) != ((frames[i][bit / 8] & (1 << (7 - bit % 8))) != 0)) errors++;
                        }
                        if (errors == before) perfect++;
                    }
                }
                Console.WriteLine(String.Join(",", new[] { width.ToString(), height.ToString(), cell.ToString(), F(frames[0].Length * 8.0 * 30 / 1000000), target.ToString(), F(new FileInfo(encoded).Length * 8.0 * 30 / Frames / 1000000), errors.ToString(), perfect.ToString(), Frames.ToString(), F(encodeMs) }));
            }
            File.Delete(source); File.Delete(encoded); File.Delete(decoded);
        }
        return 0;
    }
    private static string F(double value) { return value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); }
    private static string Q(string value) { if (value.IndexOf('"') >= 0) throw new ArgumentException(); return "\"" + value + "\""; }
    private static void Run(string executable, string arguments)
    {
        using (Process process = Process.Start(new ProcessStartInfo { FileName = executable, Arguments = arguments, CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true }))
        {
            string error = process.StandardError.ReadToEnd(); process.WaitForExit(); if (process.ExitCode != 0) throw new IOException(error);
        }
    }
}
