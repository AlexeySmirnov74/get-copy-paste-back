// Synthetic MP4Modem packet -> H.264 -> bitmap -> exact byte comparison.
// This does not open a camera or exercise Horizon/RDP. FFmpeg is a development
// benchmark dependency only; neither application depends on it at runtime.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using HorizonClient;

internal static class Mp4ModemCompressionBench
{
    private const int Frames = 30;
    private const int FPS = 30;
    public static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: Mp4ModemCompressionBench ffmpeg.exe output-directory"); return 2; }
        string directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
        string fixture = Path.Combine(directory, "mp4modem-h264-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            string source = Path.Combine(fixture, "source-gray.raw");
            var packets = new Dictionary<Guid, Packet>(); var random = new Random(831974);
            var protocolEncode = new Stopwatch();
            using (FileStream output = File.Create(source))
                for (int i = 0; i < Frames; i++)
                {
                    var payload = new byte[Mp4ModemProtocol.ChunkSize]; random.NextBytes(payload);
                    var guid = new byte[16]; random.NextBytes(guid);
                    var packet = new Packet { TransferId = new Guid(guid), Index = 0, Count = 1, TotalLength = payload.Length, Data = payload, ChunkSize = payload.Length, Profile = (VideoProfile)11 };
                    packets.Add(packet.TransferId, packet);
                    protocolEncode.Start(); Bitmap frame = Mp4ModemProtocol.EncodeFrame(packet); protocolEncode.Stop();
                    using (frame) WriteGray(output, frame);
                }
            const string header = "case,width,height,target_mbps,actual_h264_mbps,valid_frames,unique_packets,verified_bytes,expected_bytes,nominal_seconds,useful_mbps_at30,protocol_encode_ms_total,protocol_decode_ms_total,decode_fps_cpu,h264_encode_ms,h264_decode_ms,incorrect_packets,preset,vbv_buffer_ms";
            using (var csv = new StreamWriter(Path.Combine(directory, "video-mp4modem-h264.csv"), false, new UTF8Encoding(false)))
            {
                Emit(csv, header);
                foreach (Size size in new[] { new Size(1280, 720), new Size(640, 360), new Size(320, 240) })
                    foreach (double target in new[] { 1.0, 2.6, 5.0, 10.0 })
                    {
                        string encoded = Path.Combine(fixture, "encoded.h264"), decoded = Path.Combine(fixture, "decoded-bgr.raw");
                        string rate = target.ToString("0.0", CultureInfo.InvariantCulture) + "M";
                        string buffer = (target * 100).ToString("0.0", CultureInfo.InvariantCulture) + "k";
                        var codec = Stopwatch.StartNew();
                        Run(args[0], "-hide_banner -loglevel error -y -f rawvideo -pixel_format gray -video_size 1280x720 -framerate 30 -i " + Q(source) +
                            " -vf scale=" + size.Width + ":" + size.Height + ":flags=bicubic -frames:v 30 -an -c:v libx264 -preset veryfast -tune zerolatency -pix_fmt yuv420p" +
                            " -b:v " + rate + " -minrate " + rate + " -maxrate " + rate + " -bufsize " + buffer + " -x264-params nal-hrd=cbr:force-cfr=1 -g 30 -bf 0 -f h264 " + Q(encoded));
                        double codecEncodeMs = codec.Elapsed.TotalMilliseconds;
                        codec.Restart(); Run(args[0], "-hide_banner -loglevel error -y -i " + Q(encoded) + " -frames:v 30 -pix_fmt bgr24 -f rawvideo " + Q(decoded));
                        double codecDecodeMs = codec.Elapsed.TotalMilliseconds;
                        int valid = 0, incorrect = 0; long verified = 0; var accepted = new HashSet<Guid>(); var protocolDecode = new Stopwatch();
                        using (FileStream input = File.OpenRead(decoded))
                            for (int i = 0; i < Frames; i++)
                                using (Bitmap bitmap = ReadBitmap(input, size.Width, size.Height))
                                {
                                    protocolDecode.Start(); Packet actual = Mp4ModemProtocol.DecodeFrame(bitmap); protocolDecode.Stop();
                                    if (actual == null) continue;
                                    Packet expected;
                                    if (!packets.TryGetValue(actual.TransferId, out expected) || !Matches(expected, actual)) { incorrect++; continue; }
                                    valid++;
                                    if (accepted.Add(actual.TransferId)) { verified += actual.Data.Length; }
                                }
                        double nominalSeconds = (double)Frames / FPS;
                        Emit(csv, String.Join(",", new[] {
                            size.Width == 1280 ? "native" : size.Width == 640 ? "resize16x9" : "hardresize4x3",
                            size.Width.ToString(), size.Height.ToString(), F(target), F(new FileInfo(encoded).Length * 8.0 / nominalSeconds / 1000000),
                            valid.ToString(), accepted.Count.ToString(), verified.ToString(), (Frames * Mp4ModemProtocol.ChunkSize).ToString(), F(nominalSeconds),
                            F(verified * 8.0 / nominalSeconds / 1000000), F(protocolEncode.Elapsed.TotalMilliseconds), F(protocolDecode.Elapsed.TotalMilliseconds),
                            F(Frames / protocolDecode.Elapsed.TotalSeconds), F(codecEncodeMs), F(codecDecodeMs), incorrect.ToString(), "veryfast", "100"
                        }));
                        if (incorrect != 0) throw new InvalidDataException("A decoder accepted an incorrect synthetic packet.");
                        File.Delete(encoded); File.Delete(decoded);
                    }
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            // Only this generated fixture directory is eligible for cleanup.
            string canonical = Path.GetFullPath(fixture), parent = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (canonical.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(canonical).StartsWith("mp4modem-h264-", StringComparison.Ordinal)) Directory.Delete(canonical, true);
        }
    }

    private static bool Matches(Packet expected, Packet actual)
    {
        if (actual.Index != 0 || actual.Count != 1 || actual.TotalLength != expected.TotalLength || actual.ChunkSize != expected.ChunkSize || (int)actual.Profile != 11 || actual.Data == null || actual.Data.Length != expected.Data.Length) return false;
        for (int i = 0; i < actual.Data.Length; i++) if (actual.Data[i] != expected.Data[i]) return false;
        return true;
    }
    private static void WriteGray(Stream output, Bitmap frame)
    {
        BitmapData locked = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[frame.Width * 3]; var gray = new byte[frame.Width];
            for (int y = 0; y < frame.Height; y++) { Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), row, 0, row.Length); for (int x = 0; x < gray.Length; x++) gray[x] = row[x * 3]; output.Write(gray, 0, gray.Length); }
        }
        finally { frame.UnlockBits(locked); }
    }
    private static Bitmap ReadBitmap(Stream input, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[width * 3];
            for (int y = 0; y < height; y++) { int offset = 0; while (offset < row.Length) { int read = input.Read(row, offset, row.Length - offset); if (read == 0) throw new EndOfStreamException("FFmpeg returned fewer than30 frames."); offset += read; } Marshal.Copy(row, 0, IntPtr.Add(locked.Scan0, y * locked.Stride), row.Length); }
        }
        catch { bitmap.UnlockBits(locked); bitmap.Dispose(); throw; }
        bitmap.UnlockBits(locked); return bitmap;
    }
    private static void Run(string executable, string arguments)
    {
        using (Process process = Process.Start(new ProcessStartInfo { FileName = executable, Arguments = arguments, CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true }))
        {
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(); process.WaitForExit(5000); throw new TimeoutException("Synthetic FFmpeg benchmark exceeded120 seconds."); }
            if (!error.Wait(5000)) throw new IOException("FFmpeg error output did not close.");
            if (process.ExitCode != 0) throw new IOException(error.Result);
        }
    }
    private static string Q(string value) { if (value.IndexOf('"') >= 0) throw new ArgumentException(); return "\"" + value + "\""; }
    private static string F(double value) { return value.ToString("F6", CultureInfo.InvariantCulture); }
    private static void Emit(StreamWriter output, string line) { output.WriteLine(line); output.Flush(); Console.WriteLine(line); }
}
