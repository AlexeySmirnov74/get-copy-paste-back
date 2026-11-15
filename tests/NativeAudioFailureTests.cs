using System;
using System.IO;
using System.Reflection;
using System.Threading;
using HorizonClient;

internal static class NativeAudioFailureTests
{
    private static int Main()
    {
        try
        {
            Check(!DiagnosticLog.IsEnabled, "failure availability must be observable with detailed diagnostics disabled");
            StartupFailureSnapshot();
            OneFeedRecoveryThenTerminal();
            ConstructorNotificationRace();
            DisposeFailureAndLateOwner();
            RecoveryFailureDoesNotDoubleDispose();
            RecoveryBudgetSurvivesBoundary();
            PacketCallbackFailureDoesNotRecreate();
            Console.WriteLine("Native failure checks passed: startup snapshot, healthy FSK/native path, one lifetime recovery with queued PCM discard, budget across protocol reset, failed recreation without double-destroy, once-only isolated callbacks, subscribe/snapshot race, failed cleanup and late owner disposal. Detailed logs disabled; no devices.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void StartupFailureSnapshot()
    {
        int packets = 0;
        int creates = 0;
        using (var mux = new AudioReceiveMultiplexer(delegate(Packet packet) { packets++; }, true, decoderFactory: delegate(AudioModemKind kind) { Interlocked.Increment(ref creates); throw new FileNotFoundException("synthetic startup load failure"); }))
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (object worker in (Array)typeof(AudioReceiveMultiplexer).GetField("workers", flags).GetValue(mux))
                Check(((Thread)worker.GetType().GetField("thread", flags).GetValue(worker)).Join(5000), "startup workers completed");
            MethodInfo snapshot = typeof(AudioReceiveMultiplexer).GetMethod("GetDecoderFailure", flags);
            Check(snapshot != null, "terminal native startup failures must be observable after constructor startup");
            foreach (AudioModemKind kind in new[] { AudioModemKind.Quiet, AudioModemKind.Aicodix })
                Check(snapshot.Invoke(mux, new object[] { kind }) is Exception, "startup terminal snapshot for " + kind);
            Check(snapshot.Invoke(mux, new object[] { AudioModemKind.Fsk }) == null, "healthy FSK has no native failure");
            var packet = new Packet { TransferId = Guid.NewGuid(), Index = 0, Count = 1, TotalLength = 1, Data = new byte[] { 7 }, ChunkSize = 256, Profile = VideoProfile.Compatible };
            mux.Feed(AudioModem.Encode(packet));
            Check(packets == 1, "FSK keeps decoding after both native workers fail");
            Check(creates == 2, "startup load/create errors must not be retried");
        }
    }

    private static void OneFeedRecoveryThenTerminal()
    {
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var recreated = new ManualResetEvent(false))
        using (var delivered = new ManualResetEvent(false))
        using (var healthyDelivered = new ManualResetEvent(false))
        using (var terminal = new ManualResetEvent(false))
        {
            Guid quietId = Guid.NewGuid(), healthyId = Guid.NewGuid();
            byte[] quietWire = Wire(AudioModemKind.Quiet, quietId), healthyWire = Wire(AudioModemKind.Aicodix, healthyId);
            int creates = 0, failures = 0, packets = 0, abandoned = 0, exhaust = 0;
            using (var mux = new AudioReceiveMultiplexer(delegate(Packet packet) {
                if (packet.TransferId == quietId) delivered.Set();
                else if (packet.TransferId == healthyId) healthyDelivered.Set();
                else Interlocked.Increment(ref packets);
            }, true, decoderFactory: delegate(AudioModemKind kind) {
                if (kind == AudioModemKind.Aicodix) return new Decoder(delegate(float[] input) { return new[] { healthyWire }; }, null);
                int index = Interlocked.Increment(ref creates);
                if (index == 1) return new Decoder(delegate(float[] input) { entered.Set(); Check(release.WaitOne(5000), "release first native Feed"); throw new InvalidDataException("first managed Feed error"); }, null);
                recreated.Set();
                return new Decoder(delegate(float[] input) {
                    if (input[0] == 9) Interlocked.Increment(ref abandoned);
                    if (Volatile.Read(ref exhaust) != 0) throw new InvalidDataException("second managed Feed error");
                    return new[] { quietWire };
                }, null);
            }))
            {
                mux.DecoderFailed += delegate { throw new Exception("broken subscriber must not prevent next subscriber or cleanup"); };
                mux.DecoderFailed += delegate(AudioModemKind kind, Exception error) { if (kind == AudioModemKind.Quiet) { Interlocked.Increment(ref failures); terminal.Set(); } };
                mux.Feed(new float[] { 1 }); Check(entered.WaitOne(5000), "first Feed reached native decoder");
                mux.Feed(new float[] { 9 }); release.Set();
                Check(recreated.WaitOne(5000), "one managed Feed error must recreate the native decoder");
                mux.Feed(new float[] { 2 }); Check(delivered.WaitOne(5000), "future PCM decodes after transient recovery");
                Check(abandoned == 0 && creates == 2 && failures == 0 && mux.GetDecoderFailure(AudioModemKind.Quiet) == null, "recovery drops abandoned PCM and does not publish terminal failure");
                Interlocked.Exchange(ref exhaust, 1); mux.Feed(new float[] { 4 });
                Check(terminal.WaitOne(5000), "second Feed error is terminal");
                Join(mux, AudioModemKind.Quiet);
                Check(creates == 2 && failures == 1 && mux.GetDecoderFailure(AudioModemKind.Quiet) is InvalidDataException, "lifetime recovery budget and terminal notification are once-only");
                healthyDelivered.Reset(); mux.Feed(new float[] { 5 });
                Check(healthyDelivered.WaitOne(5000) && mux.GetDecoderFailure(AudioModemKind.Aicodix) == null, "other native modem keeps receiving after one terminal failure");
                mux.Feed(AudioModem.Encode(new Packet { TransferId = Guid.NewGuid(), Index = 0, Count = 1, TotalLength = 1, Data = new byte[] { 7 }, ChunkSize = 256, Profile = VideoProfile.Compatible }));
                Check(packets == 1 && failures == 1, "FSK survives exhausted native recovery without repeated terminal notifications");
            }
        }
    }

    private static void ConstructorNotificationRace()
    {
        for (int iteration = 0; iteration < 8; iteration++)
        using (var release = new ManualResetEvent(false))
        {
            int observations = 0;
            object gate = new object(); bool quiet = false, aicodix = false;
            Action<AudioModemKind, Exception> observe = delegate(AudioModemKind kind, Exception error) {
                Check(error != null, "failure has exception evidence");
                lock (gate) { if (kind == AudioModemKind.Quiet ? quiet : aicodix) return; if (kind == AudioModemKind.Quiet) quiet = true; else aicodix = true; observations++; }
            };
            using (var mux = new AudioReceiveMultiplexer(delegate { }, true, decoderFactory: delegate { release.WaitOne(5000); throw new InvalidOperationException("constructor failure"); }))
            {
                if ((iteration & 1) == 0) { release.Set(); Join(mux, AudioModemKind.Quiet); Join(mux, AudioModemKind.Aicodix); }
                mux.DecoderFailed += observe;
                release.Set();
                foreach (AudioModemKind kind in new[] { AudioModemKind.Quiet, AudioModemKind.Aicodix }) { Exception error = mux.GetDecoderFailure(kind); if (error != null) observe(kind, error); }
                Join(mux, AudioModemKind.Quiet); Join(mux, AudioModemKind.Aicodix);
                Check(observations == 2, "subscribe then snapshot observes startup failures exactly once regardless of constructor race");
            }
        }
    }

    private static void DisposeFailureAndLateOwner()
    {
        using (var failed = new ManualResetEvent(false))
        {
            int failures = 0;
            using (var mux = new AudioReceiveMultiplexer(delegate { }, true, decoderFactory: delegate {
                return new Decoder(delegate { throw new InvalidOperationException("terminal feed"); }, delegate { throw new InvalidOperationException("failed destroy"); });
            }))
            {
                mux.DecoderFailed += delegate { Interlocked.Increment(ref failures); failed.Set(); };
                mux.Feed(new float[] { 1 }); Check(failed.WaitOne(5000), "destroy failure cannot prevent failure propagation");
                Join(mux, AudioModemKind.Quiet); Join(mux, AudioModemKind.Aicodix);
                foreach (object worker in Workers(mux))
                {
                    var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                    Check((long)worker.GetType().GetField("queuedSamples", flags).GetValue(worker) == 0, "cleanup clears pending PCM even when destroy fails");
                    bool disposedWake = false;
                    try { ((AutoResetEvent)worker.GetType().GetField("wake", flags).GetValue(worker)).Set(); } catch (ObjectDisposedException) { disposedWake = true; }
                    Check(disposedWake, "cleanup releases wake even when destroy fails");
                }
                Check(failures == 2, "one terminal notification per native modem even when destroy also fails");
            }
        }
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            int failures = 0;
            var mux = new AudioReceiveMultiplexer(delegate { }, true, decoderFactory: delegate { entered.Set(); release.WaitOne(5000); throw new InvalidOperationException("late create after owner disposal"); });
            mux.DecoderFailed += delegate { Interlocked.Increment(ref failures); };
            Check(entered.WaitOne(5000), "constructor worker entered"); mux.Dispose(); release.Set();
            Join(mux, AudioModemKind.Quiet); Join(mux, AudioModemKind.Aicodix);
            Check(failures == 0 && mux.GetDecoderFailure(AudioModemKind.Quiet) == null && mux.GetDecoderFailure(AudioModemKind.Aicodix) == null, "expected owner disposal suppresses late failure notifications and snapshots");
        }
    }

    private static byte[] Wire(AudioModemKind kind, Guid id)
    { int count; return AudioPacketWire.Encode(kind, id, 0, new byte[] { 42 }, 2048, out count); }
    private static Array Workers(AudioReceiveMultiplexer mux)
    { return (Array)typeof(AudioReceiveMultiplexer).GetField("workers", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(mux); }
    private static void Join(AudioReceiveMultiplexer mux, AudioModemKind kind)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (object worker in Workers(mux))
            if ((AudioModemKind)worker.GetType().GetField("kind", flags).GetValue(worker) == kind)
                Check(((Thread)worker.GetType().GetField("thread", flags).GetValue(worker)).Join(5000), "native worker completes: " + kind);
    }
    private sealed class Decoder : INativeAudioDecoder
    {
        private readonly Func<float[], byte[][]> feed; private readonly Action dispose;
        internal Decoder(Func<float[], byte[][]> feed, Action dispose) { this.feed = feed; this.dispose = dispose; }
        public byte[][] Feed(float[] samples) { return feed(samples); }
        public void Dispose() { if (dispose != null) dispose(); }
    }

    private static void RecoveryFailureDoesNotDoubleDispose()
    {
        foreach (bool destroyFails in new[] { true, false })
        using (var terminal = new ManualResetEvent(false))
        {
            int creates = 0, disposals = 0, failures = 0;
            using (var mux = new AudioReceiveMultiplexer(delegate { }, true, decoderFactory: delegate(AudioModemKind kind) {
                if (kind != AudioModemKind.Quiet) return new Decoder(delegate { return new byte[0][]; }, null);
                if (Interlocked.Increment(ref creates) > 1) throw new InvalidOperationException("replacement create failed");
                return new Decoder(delegate { throw new InvalidDataException("recoverable feed"); }, delegate { Interlocked.Increment(ref disposals); if (destroyFails) throw new InvalidOperationException("replacement destroy failed"); });
            }))
            {
                mux.DecoderFailed += delegate(AudioModemKind kind, Exception error) { if (kind == AudioModemKind.Quiet) { failures++; terminal.Set(); } };
                mux.Feed(new float[] { 0 }); Check(terminal.WaitOne(5000), "failed recreation publishes terminal state"); Join(mux, AudioModemKind.Quiet);
                Check(disposals == 1 && creates == (destroyFails ? 1 : 2) && failures == 1, "failed recreation is bounded and never double-destroys the abandoned decoder");
            }
        }
        using (var ready = new ManualResetEvent(false))
        {
            int failures = 0;
            var mux = new AudioReceiveMultiplexer(delegate { }, true, decoderFactory: delegate {
                ready.Set(); return new Decoder(delegate { return new byte[0][]; }, delegate { throw new InvalidOperationException("normal owner destroy failure"); });
            });
            mux.DecoderFailed += delegate { failures++; };
            Check(ready.WaitOne(5000), "normal owner has created a native decoder"); mux.Dispose(); Join(mux, AudioModemKind.Quiet); Join(mux, AudioModemKind.Aicodix);
            Check(failures == 0 && mux.GetDecoderFailure(AudioModemKind.Quiet) == null && mux.GetDecoderFailure(AudioModemKind.Aicodix) == null, "normal owner shutdown does not report destroy failures as receive availability faults");
        }
    }

    private static void RecoveryBudgetSurvivesBoundary()
    {
        using (var recreated = new ManualResetEvent(false))
        using (var boundary = new ManualResetEvent(false))
        using (var terminal = new ManualResetEvent(false))
        {
            int creates = 0, first = 0, exhaust = 0, packets = 0;
            using (var mux = new AudioReceiveMultiplexer(delegate { packets++; }, true, decoderFactory: delegate(AudioModemKind kind) {
                if (kind != AudioModemKind.Quiet) return new Decoder(delegate { return new byte[0][]; }, null);
                int index = Interlocked.Increment(ref creates); if (index == 2) recreated.Set(); else if (index == 3) boundary.Set();
                return new Decoder(delegate {
                    if (Interlocked.CompareExchange(ref first, 1, 0) == 0 || Volatile.Read(ref exhaust) != 0) throw new InvalidDataException("bounded Feed failure");
                    return new byte[0][];
                }, null);
            }))
            {
                mux.DecoderFailed += delegate(AudioModemKind kind, Exception error) { if (kind == AudioModemKind.Quiet) terminal.Set(); };
                mux.Feed(new float[] { 0 }); Check(recreated.WaitOne(5000), "first Feed failure recreates before protocol reset");
                mux.Feed(AudioModem.Encode(new Packet { TransferId = Guid.NewGuid(), Index = 0, Count = 1, TotalLength = 1, Data = new byte[] { 7 }, ChunkSize = 256, Profile = VideoProfile.Compatible }));
                Check(boundary.WaitOne(5000) && packets == 1, "verified FSK boundary performs an ordinary context reset");
                Interlocked.Exchange(ref exhaust, 1); mux.Feed(new float[] { 0 });
                Check(terminal.WaitOne(5000), "protocol boundary must not restore the consumed lifetime Feed recovery budget"); Join(mux, AudioModemKind.Quiet);
                Check(creates == 3, "second Feed fault after normal reset never creates a fourth context");
            }
        }
    }

    private static void PacketCallbackFailureDoesNotRecreate()
    {
        using (var delivered = new AutoResetEvent(false))
        {
            byte[] wire = Wire(AudioModemKind.Quiet, Guid.NewGuid()); int creates = 0, failures = 0;
            using (var mux = new AudioReceiveMultiplexer(delegate { delivered.Set(); throw new InvalidDataException("consumer callback failure"); }, true, decoderFactory: delegate(AudioModemKind kind) {
                if (kind != AudioModemKind.Quiet) return new Decoder(delegate { return new byte[0][]; }, null);
                Interlocked.Increment(ref creates); return new Decoder(delegate { return new[] { wire }; }, null);
            }))
            {
                mux.DecoderFailed += delegate { failures++; };
                mux.Feed(new float[] { 0 }); Check(delivered.WaitOne(5000), "first native packet reaches throwing consumer");
                mux.Feed(new float[] { 0 }); Check(delivered.WaitOne(5000), "consumer InvalidDataException does not stop native packet decoding");
                Check(creates == 1 && failures == 0 && mux.GetDecoderFailure(AudioModemKind.Quiet) == null, "only Feed failures consume decoder recovery budget");
            }
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
