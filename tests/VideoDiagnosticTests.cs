using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using HorizonClient;

internal static class VideoDiagnosticTests
{
    private static int assertions;
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }

    private static int Main()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "video-diagnostics-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            Check(DiagnosticLog.Initialize(path), "Initialize portable log.");
            byte[] data = Encoding.UTF8.GetBytes("PRIVATE_CLIPBOARD_CANARY_6ed859aacdcf");
            using (Bitmap valid = VisualProtocol.EncodePacketFrame(Guid.NewGuid(), 0, data, VideoProfile.Mp4Modem))
            {
                Packet[] decoded = VisualProtocol.DecodePackets(valid);
                Check(decoded.Length == 1 && decoded[0].Data.Length == data.Length, "Diagnostics preserve MP4 decoding.");
            }
            // A genuine MP4 header with a deliberately invalid FEC body must be
            // distinguishable from an unrecognized camera frame in the journal.
            Thread.Sleep(1050);
            byte[] invalidFec = new byte[Mp4ModemProtocol.FrameDataBytes];
            using (Bitmap invalid = Mp4ModemProtocol.LuminanceToBitmap(Mp4ModemProtocol.EncodeBasisFrame(
                new Mp4ModemProtocol.CompactTag { Flags = Mp4ModemProtocol.AdapterFlag }, invalidFec)))
                Check(VisualProtocol.DecodePackets(invalid).Length == 0, "Invalid FEC remains rejected.");
            Check(DiagnosticLog.Shutdown(3000), "Drain log.");
            string text = File.ReadAllText(path, Encoding.UTF8);
            Check(!text.Contains(Encoding.UTF8.GetString(data)), "Log excludes clipboard data.");
            var serializer = new JavaScriptSerializer();
            int mp4Records = 0; bool accepted = false, fec = false, stages = false;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                Dictionary<string, object> record = serializer.Deserialize<Dictionary<string, object>>(line);
                string name = Convert.ToString(record["event"]);
                if (name == "video.decode.stages")
                {
                    stages = true;
                    Check(record["counts"] is Dictionary<string, object>, "Codec stage counters remain structured.");
                }
                if (name != "video.mp4.decode.summary") continue;
                mp4Records++;
                accepted |= Convert.ToInt64(record["acceptedFrames"]) > 0;
                var failures = record["failedAttemptsByStage"] as Dictionary<string, object>;
                Check(failures != null, "MP4 failures remain structured.");
                foreach (string key in failures.Keys) fec |= key.StartsWith("fec:", StringComparison.Ordinal) || key == "fec";
            }
            Check(mp4Records >= 2 && accepted, "Accepted MP4 frame is logged.");
            Check(fec, "FEC rejection is logged separately from header detection.");
            Check(stages, "Other codec detection stages are recorded.");
            Console.WriteLine("Video diagnostics checks passed: " + assertions); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { DiagnosticLog.Shutdown(); if (File.Exists(path)) File.Delete(path); }
    }
}
