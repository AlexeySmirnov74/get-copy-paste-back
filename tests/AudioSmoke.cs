// Explicit manual hardware check: plays a short synthetic tone and captures only
// system playback (WASAPI loopback). Never opens a physical microphone or clipboard.
using System;
using System.Threading;
using HorizonClient;

internal static class AudioSmoke
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1 || args[0] != "--play-test-tone")
            { Console.WriteLine("Manual only: AudioSmoke.exe --play-test-tone"); return 2; }
            var outputs = AudioPlayer.EnumerateEndpoints();
            var loopbacks = AudioCapture.EnumerateEndpoints(true);
            Console.WriteLine("Playback endpoints: " + outputs.Length + "; loopback endpoints: " + loopbacks.Length + ".");
            if (outputs.Length == 0 || loopbacks.Length == 0) throw new Exception("No playback endpoint is available.");
            byte[] expected = new byte[256]; new Random(92).NextBytes(expected);
            Guid id = Guid.NewGuid();
            var packet = new Packet { TransferId = id, Index = 0, Count = 1, TotalLength = expected.Length, Data = expected, ChunkSize = 256, Profile = VideoProfile.Compatible };
            var decoder = new AudioDecoder();
            Exception error = null;
            int callbacks = 0;
            using (var done = new ManualResetEvent(false))
            using (var capture = new AudioCapture(delegate(float[] samples) { Interlocked.Increment(ref callbacks); decoder.Feed(samples); }, true))
            using (var player = new AudioPlayer())
            {
                decoder.PacketReceived += delegate(Packet actual)
                {
                    if (actual.TransferId != id || actual.Data.Length != expected.Length) return;
                    for (int i = 0; i < expected.Length; i++) if (actual.Data[i] != expected[i]) return;
                    done.Set();
                };
                capture.OnError += delegate(Exception failure) { error = failure; done.Set(); };
                capture.Start();
                for (int i = 0; i < 100 && !capture.IsRunning && error == null; i++) Thread.Sleep(20);
                if (error != null) throw error;
                if (!capture.IsRunning) throw new Exception("WASAPI loopback did not start.");
                Thread.Sleep(100);
                player.Play(AudioModem.Encode(packet));
                if (!done.WaitOne(2500)) throw new Exception("No verified synthetic packet received; callbacks=" + callbacks + ", dropped=" + capture.DroppedChunks + ", discontinuities=" + capture.Discontinuities + ".");
                if (error != null) throw error;
                Console.WriteLine("Actual Windows playback -> WASAPI loopback -> modem: 256 synthetic bytes matched exactly. No Horizon hop was tested.");
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
