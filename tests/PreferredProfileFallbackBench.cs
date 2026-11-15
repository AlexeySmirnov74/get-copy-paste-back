using System;
using System.Diagnostics;
using System.Drawing;
using HorizonClient;

// Opt-in synthetic CPU benchmark, never opens a capture or audio endpoint.
internal static class PreferredProfileFallbackBench
{
    private static int Main()
    {
        try
        {
            foreach (VideoProfile profile in new[] { VideoProfile.HighDensity720, VideoProfile.HighDensity1080 })
            {
                byte[] payload = new byte[VisualProtocol.GetFramePayloadBytes(profile)]; new Random(578).NextBytes(payload);
                using (Bitmap frame = VisualProtocol.EncodePacketFrame(Guid.NewGuid(), 0, payload, profile))
                {
                    int columns = frame.Width - 4, slotBytes = 40 + DenseVideoProtocol.GetChunkSize(profile) + 4;
                    for (int index = 0; index < VisualProtocol.GetPacketsPerFrame(profile); index++)
                    {
                        int bit = (index * slotBytes + 40) * 8, x = 2 + bit % columns, y = 2 + bit / columns;
                        Color pixel = frame.GetPixel(x, y); frame.SetPixel(x, y, pixel.R == 0 ? Color.White : Color.Black);
                    }
                    if (VisualProtocol.DecodePackets(frame, profile).Length != 0) throw new Exception("Corrupted payload unexpectedly passed CRC.");
                    const int count = 12; var watch = Stopwatch.StartNew();
                    for (int attempt = 0; attempt < count; attempt++)
                        if (VisualProtocol.DecodePackets(frame, profile).Length != 0) throw new Exception("Corrupted payload unexpectedly passed CRC.");
                    Console.WriteLine("{0}: {1:F3} ms/frame; {2} damaged packets/frame; {3} frames; CRC rejected every payload.",
                        profile, watch.Elapsed.TotalMilliseconds / count, VisualProtocol.GetPacketsPerFrame(profile), count);
                }
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
