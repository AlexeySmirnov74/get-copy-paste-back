using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using HorizonClient;

// Local compression experiment: generated payload -> H.264 -> independent tile
// CRCs. It does not include the Horizon transport and never opens a camera.
internal static class DenseVideoCapacityBench
{
    private const int Frames = 30;
    public static int Main(string[] args)
    {
        if (args.Length < 2 || args.Length > 3) { Console.Error.WriteLine("Usage: DenseVideoCapacityBench ffmpeg.exe output-directory [lossless|legacy|compact]"); return 2; }
        bool legacyOnly = args.Length == 3 && args[2] == "legacy", losslessOnly = args.Length == 3 && args[2] == "lossless";
        bool compactOnly = args.Length == 3 && args[2] == "compact";
        string directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
        Console.WriteLine("profile,width,height,payload_bytes_per_frame,target_video_mbit,actual_video_mbit,verified_bytes,expected_bytes,valid_tiles,expected_tiles,useful_mbit_at30,decode_fps,encode_input_ms");
        foreach (VideoProfile profile in VisualProtocol.GetProfiles())
        {
            if (DenseVideoProtocol.Supports(profile) == legacyOnly) continue;
            if (compactOnly && profile != VideoProfile.Compact320 && profile != VideoProfile.Compact640) continue;
            int width = VisualProtocol.GetWidth(profile), height = VisualProtocol.GetHeight(profile), capacity = VisualProtocol.GetFramePayloadBytes(profile), slots = VisualProtocol.GetPacketsPerFrame(profile);
            string source = Path.Combine(directory, "source-gray.raw"), encoded = Path.Combine(directory, "encoded.mp4");
            byte[] payload = new byte[capacity * Frames]; new Random(65172).NextBytes(payload); Guid id = Guid.NewGuid();
            var watch = Stopwatch.StartNew();
            using (FileStream output = File.Create(source)) for (int i = 0; i < Frames; i++)
            using (Bitmap frame = VisualProtocol.EncodePacketFrame(id, i * slots, payload, profile))
            {
                BitmapData locked = frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    byte[] row = new byte[Math.Abs(locked.Stride)], gray = new byte[width];
                    for (int y = 0; y < height; y++) { Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), row, 0, row.Length); for (int x = 0; x < width; x++) gray[x] = row[x * 3]; output.Write(gray, 0, gray.Length); }
                }
                finally { frame.UnlockBits(locked); }
            }
            double inputMs = watch.Elapsed.TotalMilliseconds;
            foreach (int target in losslessOnly ? new[] { 0 } : compactOnly ? new[] { 1, 2, 5, 10 } : new[] { 5, 20, 50, 100 })
            {
                string rate = target == 0 ? " -qp 0" : " -b:v " + target + "M -minrate " + target + "M -maxrate " + target + "M -bufsize " + (target * 100) + "k -x264-params nal-hrd=cbr:force-cfr=1";
                Run(args[0], "-hide_banner -loglevel error -y -f rawvideo -pixel_format gray -video_size " + width + "x" + height + " -framerate 30 -i " + Q(source) + " -c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p" + rate + " -g 30 " + Q(encoded));
                long verified = 0; int count = 0; var accepted = new HashSet<int>();
                watch.Restart();
                using (Process process = Process.Start(new ProcessStartInfo { FileName = args[0], Arguments = "-hide_banner -loglevel error -i " + Q(encoded) + " -pix_fmt bgr24 -f rawvideo pipe:1", CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true }))
                {
                    Task<string> error = process.StandardError.ReadToEndAsync();
                    using (var input = new BufferedStream(process.StandardOutput.BaseStream, 65536))
                    for (int i = 0; i < Frames; i++)
                    using (Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                    {
                        BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                        try
                        {
                            byte[] row = new byte[Math.Abs(locked.Stride)];
                            for (int y = 0; y < height; y++) { Read(input, row, width * 3); Marshal.Copy(row, 0, IntPtr.Add(locked.Scan0, y * locked.Stride), row.Length); }
                        }
                        finally { bitmap.UnlockBits(locked); }
                        foreach (Packet packet in VisualProtocol.DecodePackets(bitmap))
                        {
                            if (packet.TransferId != id || !accepted.Add(packet.Index)) continue;
                            int offset = packet.Index * packet.ChunkSize;
                            for (int p = 0; p < packet.Data.Length; p++) if (packet.Data[p] != payload[offset + p]) throw new Exception("CRC accepted incorrect data.");
                            verified += packet.Data.Length; count++;
                        }
                    }
                    process.WaitForExit(); if (process.ExitCode != 0) throw new IOException(error.Result);
                }
                double seconds = watch.Elapsed.TotalSeconds;
                Console.WriteLine(String.Join(",", new[] { profile.ToString(), width.ToString(), height.ToString(), capacity.ToString(), target.ToString(), F(new FileInfo(encoded).Length * 8.0 * 30 / Frames / 1000000), verified.ToString(), payload.Length.ToString(), count.ToString(), (slots * Frames).ToString(), F(verified * 8.0 * 30 / Frames / 1000000), F(Frames / seconds), F(inputMs) }));
            }
            File.Delete(source); File.Delete(encoded);
        }
        return 0;
    }
    private static void Read(Stream input, byte[] target, int count) { int offset = 0; while (offset < count) { int read = input.Read(target, offset, count - offset); if (read == 0) throw new EndOfStreamException(); offset += read; } }
    private static string F(double value) { return value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); }
    private static string Q(string value) { if (value.IndexOf('"') >= 0) throw new ArgumentException(); return "\"" + value + "\""; }
    private static void Run(string executable, string arguments)
    {
        using (Process process = Process.Start(new ProcessStartInfo { FileName = executable, Arguments = arguments, CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true }))
        { string error = process.StandardError.ReadToEnd(); process.WaitForExit(); if (process.ExitCode != 0) throw new IOException(error); }
    }
}
