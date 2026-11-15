using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;
using HorizonClient;

internal static class SeiProbeSmoke
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--list")
        {
            foreach (CameraDevice camera in CameraDevices.List()) Console.WriteLine(new JavaScriptSerializer().Serialize(new { name = camera.Name, id = camera.Id }));
            return 0;
        }
        if (args.Length != 2) { Console.Error.WriteLine("Usage: SeiProbeSmoke.exe EXACT_CAMERA_MONIKER RESULT_JSON"); return 2; }
        int samples = 0, found = 0; long bytes = 0; string failure = null;
        H264SeiCapture capture = null;
        try
        {
            capture = new H264SeiCapture(args[0], delegate(byte[] data)
            {
                Interlocked.Increment(ref samples); Interlocked.Add(ref bytes, data.Length);
                foreach (H264SeiMessage message in H264Sei.ParseSample(data, capture.NalFormat, capture.AvccLengthSize)) if (H264Sei.IsProbe(message)) Interlocked.Increment(ref found);
            });
            capture.OnError += delegate(Exception error) { failure = error.Message; };
            capture.Start();
            for (int i = 0; i < 100 && found < 10 && failure == null; i++) Thread.Sleep(100);
        }
        catch (Exception error) { failure = error.ToString(); }
        finally { if (capture != null) capture.Dispose(); }
        var result = new Dictionary<string, object> {
            { "utc", DateTime.UtcNow.ToString("o") }, { "samples", samples }, { "bytes", bytes }, { "probe_markers", found },
            { "native_formats", capture == null ? null : capture.ExposedFormats }, { "subtype", capture == null ? null : capture.ConnectedSubtype.ToString() },
            { "error", failure }, { "local_only", true }, { "clipboard_accessed", false }
        };
        string json = new JavaScriptSerializer().Serialize(result); File.WriteAllText(args[1], json); Console.WriteLine(json);
        return found > 0 && failure == null ? 0 : 1;
    }
}
