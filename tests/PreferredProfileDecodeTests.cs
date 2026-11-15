using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using HorizonClient;

internal static class PreferredProfileDecodeTests
{
    private static int checks;
    private static int Main()
    {
        try
        {
            PreferredModemSkipsOtherCodecs(); RoundTripsAndChanges(); MalformedFrames();
            Console.WriteLine("Preferred video decoder checks passed: " + checks + ". Synthetic bitmaps only; no camera/audio/clipboard access."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void PreferredModemSkipsOtherCodecs()
    {
        byte[] data = new byte[1082]; new Random(123).NextBytes(data); Guid id = Guid.NewGuid();
        using (Bitmap source = VisualProtocol.EncodePacketFrame(id, 0, data, VideoProfile.Mp4Modem))
        using (Bitmap frame = To1080(source))
        {
            string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preferred-video-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                Check(DiagnosticLog.Initialize(log, 1024 * 1024, 1), "Bounded diagnostic fixture starts.");
                CheckPackets(data, id, VisualProtocol.DecodePackets(frame, VideoProfile.Mp4Modem), 1);
                Check(DiagnosticLog.Shutdown(), "Diagnostic fixture drains.");
                string text = File.ReadAllText(log);
                Check(text.Contains("video.decode.stages") && !text.Contains("\"dense."), "A verified preferred MP4 frame never enters the expensive Dense candidate scan.");
            }
            finally { DiagnosticLog.Shutdown(); if (File.Exists(log)) File.Delete(log); }
            VisualProtocol.DecodePackets(frame); VisualProtocol.DecodePackets(frame, VideoProfile.Mp4Modem);
            const int count = 8; var timer = Stopwatch.StartNew();
            for (int i = 0; i < count; i++) CheckPackets(data, id, VisualProtocol.DecodePackets(frame), 1);
            double before = timer.Elapsed.TotalMilliseconds / count; timer.Restart();
            for (int i = 0; i < count; i++) CheckPackets(data, id, VisualProtocol.DecodePackets(frame, VideoProfile.Mp4Modem), 1);
            double after = timer.Elapsed.TotalMilliseconds / count;
            Console.WriteLine("1080p MP4 decode CPU: full_search={0:F3}ms, preferred={1:F3}ms, identical verified packets. Timing is diagnostic, not a flaky pass threshold.", before, after);
        }
    }

    private static void RoundTripsAndChanges()
    {
        foreach (VideoProfile profile in new[] { VideoProfile.Fast, VideoProfile.Dense, VideoProfile.HighDensity720,
            VideoProfile.HighDensity1080, VideoProfile.HighDefinitionRobust, VideoProfile.Compact320, VideoProfile.Compact640, VideoProfile.Mp4Modem })
        {
            byte[] data = new byte[Math.Min(102400, VisualProtocol.GetFramePayloadBytes(profile))]; new Random(3987).NextBytes(data); Guid id = Guid.NewGuid();
            int expected = Math.Min(VisualProtocol.GetPacketsPerFrame(profile), (data.Length + VisualProtocol.GetChunkSize(profile) - 1) / VisualProtocol.GetChunkSize(profile));
            using (Bitmap native = VisualProtocol.EncodePacketFrame(id, 0, data, profile))
            using (Bitmap frame = To1080(native))
            {
                CheckPackets(data, id, VisualProtocol.DecodePackets(frame, profile), expected);
                VideoProfile wrong = profile == VideoProfile.Mp4Modem ? VideoProfile.HighDefinitionRobust : VideoProfile.Mp4Modem;
                CheckPackets(data, id, VisualProtocol.DecodePackets(frame, wrong), expected);
                CheckPackets(data, id, VisualProtocol.DecodePackets(frame, (VideoProfile)999), expected);
            }
        }
        byte[] control = { 1, 2, 3, 4 }; Guid controlId = Guid.NewGuid();
        using (Bitmap native = VisualProtocol.EncodeFrame(controlId, 0, 1, control.Length, control))
        using (Bitmap frame = To1080(native))
        {
            Packet[] packets = VisualProtocol.DecodePackets(frame, VideoProfile.Mp4Modem);
            CheckPackets(control, controlId, packets, 1);
            Check(packets[0].Profile == VideoProfile.Compatible, "Control framing stays available with a data profile hint.");
        }
    }

    private static void MalformedFrames()
    {
        Check(VisualProtocol.DecodePackets(null, VideoProfile.Mp4Modem).Length == 0, "Null bitmap is rejected.");
        foreach (Size size in new[] { new Size(1, 1), new Size(128, 96), new Size(320, 240), new Size(640, 480) })
        foreach (PixelFormat format in new[] { PixelFormat.Format24bppRgb, PixelFormat.Format32bppArgb })
        using (Bitmap frame = new Bitmap(size.Width, size.Height, format))
        {
            using (Graphics g = Graphics.FromImage(frame))
            {
                g.Clear(Color.FromArgb(128, 128, 128)); var random = new Random(size.Width + (int)format);
                for (int i = 0; i < 50; i++) using (Brush brush = new SolidBrush(Color.FromArgb(random.Next(256), random.Next(256), random.Next(256))))
                    g.FillRectangle(brush, random.Next(size.Width), random.Next(size.Height), 11, 7);
            }
            Check(VisualProtocol.DecodePackets(frame, VideoProfile.Mp4Modem).Length == 0, "Finite malformed bitmap cannot bypass packet CRC.");
        }
    }

    private static Bitmap To1080(Bitmap input)
    {
        var output = new Bitmap(1920, 1080, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(output))
        {
            graphics.Clear(Color.Black); graphics.InterpolationMode = InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode = PixelOffsetMode.Half;
            double scale = Math.Min(1920.0 / input.Width, 1080.0 / input.Height);
            int w = (int)Math.Round(input.Width * scale), h = (int)Math.Round(input.Height * scale);
            graphics.DrawImage(input, new Rectangle((1920 - w) / 2, (1080 - h) / 2, w, h), new Rectangle(0, 0, input.Width, input.Height), GraphicsUnit.Pixel);
        }
        return output;
    }
    private static void CheckPackets(byte[] data, Guid id, Packet[] packets, int count)
    {
        Check(packets != null && packets.Length == count, "The expected independent packet count is recovered.");
        foreach (Packet packet in packets)
        {
            Check(packet.TransferId == id, "Transfer identity is unchanged.");
            int at = packet.Index * packet.ChunkSize;
            for (int i = 0; i < packet.Data.Length; i++) if (at + i >= data.Length || packet.Data[i] != data[at + i]) throw new Exception("Preferred decoder accepted different bytes.");
            checks++;
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
}
