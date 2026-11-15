using System.Collections.Generic;
using System.Diagnostics;

namespace HorizonClient
{
    // Aggregate codec rejection stages; never log every bit or damaged tile.
    internal static class VideoDecoderDiagnostics
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, long> Counts = new Dictionary<string, long>();
        private static long last;

        internal static void Count(string codec, VideoProfile profile, string stage, int count)
        {
            if (!DiagnosticLog.IsEnabled || count <= 0) return;
            string key = codec + "." + (int)profile + "." + stage;
            lock (Gate)
            {
                long previous; Counts.TryGetValue(key, out previous); Counts[key] = previous + count;
            }
        }

        internal static void Flush(int width, int height)
        {
            if (!DiagnosticLog.IsEnabled) return;
            lock (Gate)
            {
                long time = Stopwatch.GetTimestamp();
                if (last != 0 && (time - last) * 1000.0 / Stopwatch.Frequency < 1000) return;
                DiagnosticLog.Write("video.decode.stages", "width", width, "height", height,
                    "counts", new Dictionary<string, long>(Counts),
                    "note", "Candidate attempts by codec.profile.stage; headerNotDetected is expected for other profiles and control/idle frames.");
                Counts.Clear(); last = time;
            }
        }
    }
}
