using System;
using System.Drawing;
using System.IO;
using HorizonClient;

internal static class PreferredProfileFallbackTests
{
    private static int checks;
    private static int Main()
    {
        try
        {
            FailedCandidatesAreNotRetried(); DamagedPacketAndProfileChange();
            Console.WriteLine("Preferred fallback checks passed: " + checks + ". Synthetic bitmaps only; no camera/audio/clipboard access."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void FailedCandidatesAreNotRetried()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preferred-fallback-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            Check(DiagnosticLog.Initialize(path, 1024 * 1024, 1), "A bounded diagnostic fixture starts.");
            using (var blank = new Bitmap(1920, 1080))
                Check(VisualProtocol.DecodePackets(blank, VideoProfile.HighDensity1080).Length == 0, "A failed preferred candidate cannot produce unverified packets.");
            Check(DiagnosticLog.Shutdown(), "Candidate counters drain.");
            string log = File.ReadAllText(path);
            string dense = "\"dense.4.headerNotDetected\":" + (4 * VisualProtocol.GetPacketsPerFrame(VideoProfile.HighDensity1080));
            Check(log.Contains(dense + ",") || log.Contains(dense + "}"), "A failed preferred dense profile is searched exactly once across all four orientations.");
            Check(log.Contains("\"legacy.0.headerNotDetected\":4,") || log.Contains("\"legacy.0.headerNotDetected\":4}"), "Control framing is searched exactly once across all four orientations.");
            Check(log.Contains("\"dense.3.headerNotDetected\"") && log.Contains("\"legacy.1.headerNotDetected\""), "Other codecs remain available after a failed preferred profile.");
        }
        finally { DiagnosticLog.Shutdown(); if (File.Exists(path)) File.Delete(path); }
    }

    private static void DamagedPacketAndProfileChange()
    {
        byte[] data = new byte[4096]; new Random(722).NextBytes(data); Guid id = Guid.NewGuid();
        using (Bitmap damaged = VisualProtocol.EncodeFrame(id, 0, 1, data.Length, data, VideoProfile.HighDensity1080))
        {
            Color pixel = damaged.GetPixel(322, 2); damaged.SetPixel(322, 2, pixel.R == 0 ? Color.White : Color.Black);
            Check(VisualProtocol.DecodePackets(damaged, VideoProfile.HighDensity1080).Length == 0, "A payload bit error remains rejected by CRC after the duplicate scan is removed.");
        }
        foreach (VideoProfile profile in new[] { VideoProfile.Fast, VideoProfile.Dense, VideoProfile.HighDensity720,
            VideoProfile.HighDensity1080, VideoProfile.HighDefinitionRobust, VideoProfile.Compact320, VideoProfile.Compact640, VideoProfile.Mp4Modem })
        {
            int bytes = Math.Min(data.Length, VisualProtocol.GetChunkSize(profile)); var chunk = new byte[bytes]; Array.Copy(data, chunk, bytes);
            using (Bitmap frame = VisualProtocol.EncodeFrame(id, 0, 1, chunk.Length, chunk, profile))
            {
                VideoProfile previous = profile == VideoProfile.Mp4Modem ? VideoProfile.HighDensity1080 : VideoProfile.Mp4Modem;
                Packet[] packets = VisualProtocol.DecodePackets(frame, previous);
                Check(packets.Length == 1 && packets[0].TransferId == id && packets[0].Profile == profile, "A stale profile hint still discovers the next actual profile.");
                Check(packets[0].Data.Length == chunk.Length, "Profile changes retain exact payload length.");
                for (int i = 0; i < chunk.Length; i++) if (packets[0].Data[i] != chunk[i]) throw new Exception("Profile fallback changed a verified byte.");
            }
        }
    }
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
}
