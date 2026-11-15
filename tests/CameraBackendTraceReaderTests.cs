using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HorizonClient;

internal static class CameraBackendTraceReaderTests
{
    private static int checks;
    private static int Main()
    {
        try
        {
            var reader = new CameraBackendTraceReader();
            var output = new List<string>();
            using (var data = new MemoryStream())
            {
                Append(data, "unrelated stderr must not be copied\nHORIZON_UVC {\"event\":\"uvc.probe\",\"interval\":666666}\r\nHORIZON_UVC {\"eve");
                Check(reader.Read(data, output.Add) == 1 && output.Count == 1 && output[0].Contains("666666"), "Only a complete native metadata record is emitted.");
                Check(reader.Read(data, output.Add) == 0 && output.Count == 1, "Polling the same log never duplicates a native record.");
                Append(data, "nt\":\"uvc.commit\"}\n");
                Check(reader.Read(data, output.Add) == 1 && output[1] == "{\"event\":\"uvc.commit\"}", "Partial native records survive polls without losing bytes.");
                data.SetLength(0);
                Append(data, "HORIZON_UVC {\"event\":\"uvc.restart\"}\n");
                Check(reader.Read(data, output.Add) == 1 && output[2].Contains("restart"), "A restarted receiver's truncated log resets the cursor.");
            }
            using (var data = new MemoryStream())
            {
                Append(data, "HORIZON_UVC " + new string('x', CameraBackendTraceReader.MaximumLineBytes + 50) + "\nHORIZON_UVC accepted\n");
                var bounded = new CameraBackendTraceReader();
                Check(bounded.Read(data, output.Add) == 1 && output[output.Count - 1] == "accepted", "Oversize records are discarded as a whole and cannot poison the next line.");
            }
            using (var data = new MemoryStream())
            {
                Append(data, new string('x', CameraBackendTraceReader.MaximumReadBytes * 3) + "\nHORIZON_UVC recent\n");
                var tail = new CameraBackendTraceReader();
                Check(tail.Read(data, output.Add) == 1 && output[output.Count - 1] == "recent", "A large historical log reads a bounded tail and discards the initial partial line.");
                Append(data, new string('y', CameraBackendTraceReader.MaximumReadBytes * 2) + "\nHORIZON_UVC newest\n");
                Check(tail.Read(data, output.Add) == 1 && output[output.Count - 1] == "newest", "Large between-poll backlogs cannot create unbounded work.");
            }
            Console.WriteLine("Native camera trace reader checks passed: " + checks + ". In-memory streams only; no runtime files or cameras accessed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Append(MemoryStream target, string text) { target.Position = target.Length; byte[] bytes = Encoding.UTF8.GetBytes(text); target.Write(bytes, 0, bytes.Length); }
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
}
