using System;
using System.Collections.Generic;
using System.Threading;
using HorizonClient;

internal static class ParallelAudioWaveTests
{
    private static int checks;
    private static int Main()
    {
        try
        {
            Run(false); Run(true); RouteFailureStopsWholeWave();
            Console.WriteLine("Parallel audio wave checks passed: " + checks + ". Synthetic playback only."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static AudioSenderServices Services(Dictionary<AudioPlayer, string> players, List<string> closed)
    {
        var services = new AudioSenderServices();
        services.EncodeAudio = delegate(AudioModemKind kind, Guid id, int index, byte[] bytes)
        { return new AudioEncodedBlock { Packets = 1, WireBytes = 1, Samples = new[] { (float)index } }; };
        services.CreatePlayer = delegate(string output) { var player = new AudioPlayer(output); lock (players) players.Add(player, output); return player; };
        services.DisposePlayer = delegate(AudioPlayer player) { lock (players) { closed.Add(players[player]); players.Remove(player); } player.Dispose(); };
        return services;
    }
    private static void Run(bool cancelWave)
    {
        var players = new Dictionary<AudioPlayer, string>(); var closed = new List<string>();
        var services = Services(players, closed); AudioPlayer primary = services.CreatePlayer("waveout:0");
        int live = 0, total = 0, result = -1; Exception error = null; var indices = new HashSet<int>();
        using (var entered = new CountdownEvent(3))
        using (var release = new ManualResetEvent(false))
        using (var cancel = new CancellationTokenSource())
        {
            services.Play = delegate(AudioPlayer player, float[] samples, CancellationToken token)
            {
                Interlocked.Increment(ref live); Interlocked.Increment(ref total);
                lock (indices) indices.Add((int)samples[0]);
                entered.Signal();
                try { WaitHandle.WaitAny(new[] { release, token.WaitHandle }); token.ThrowIfCancellationRequested(); }
                finally { Interlocked.Decrement(ref live); }
            };
            var worker = new Thread(delegate()
            {
                try { result = ParallelAudioWave.Play(services, primary, "waveout:0", new[] { "waveout:0", "waveout:1", "waveout:2" },
                    AudioModemKind.Fsk, Guid.NewGuid(), 4, 10, new byte[2560], cancel.Token); }
                catch (Exception caught) { error = caught; }
            });
            worker.Start();
            Check(entered.Wait(4000), "All verified outputs begin playback concurrently.");
            Check(Volatile.Read(ref live) == 3 && Volatile.Read(ref total) == 3, "Three wave routes overlap rather than run serially.");
            Check(indices.SetEquals(new[] { 4, 5, 6 }), "One wave assigns contiguous different packet batches to routes.");
            if (cancelWave) cancel.Cancel(); else release.Set();
            Check(worker.Join(4000), "Whole wave stops before returning its players.");
            Check(live == 0 && closed.Count == 2 && players.ContainsKey(primary), "Only extra players close, after all playback ended.");
            Check(cancelWave ? error is OperationCanceledException && result == -1 : error == null && result == 3,
                "Packet index advance is returned only after every route succeeds; cancelled waves commit nothing.");
        }
        services.DisposePlayer(primary);
    }
    private static void RouteFailureStopsWholeWave()
    {
        var players = new Dictionary<AudioPlayer, string>(); var closed = new List<string>();
        var services = Services(players, closed); AudioPlayer primary = services.CreatePlayer("waveout:0");
        using (var entered = new CountdownEvent(2))
        {
            services.Play = delegate(AudioPlayer player, float[] samples, CancellationToken token)
            {
                entered.Signal(); Check(entered.Wait(4000), "Both routes enter before one fails.");
                if (samples[0] == 1) throw new InvalidOperationException("Synthetic route failure.");
                token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested();
            };
            bool failed = false;
            try { ParallelAudioWave.Play(services, primary, "waveout:0", new[] { "waveout:0", "waveout:1" }, AudioModemKind.Fsk,
                Guid.NewGuid(), 0, 2, new byte[512], CancellationToken.None); }
            catch (InvalidOperationException) { failed = true; }
            Check(failed && closed.Count == 1 && players.ContainsKey(primary), "One failed route cancels the others and releases extra players without committing a wave.");
        }
        services.DisposePlayer(primary);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); Interlocked.Increment(ref checks); }
}
