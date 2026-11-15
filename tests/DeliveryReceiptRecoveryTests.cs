// Production receipt policy/sender/codecs/receiver; all I/O is synthetic PCM.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

internal static class DeliveryReceiptRecoveryTests
{
    private static int checks;
    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            VerifyScheduledReceiptTrain(); VerifyStaleReceiptTimers();
            VerifyLossRecovery(false); VerifyLossRecovery(true);
            Console.WriteLine("Delivery receipt recovery passed: " + checks + " checks. Production PCM pipeline; no speaker, capture device, camera or clipboard opened.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    // Removing scheduled retries must fail this test even when no second camera
    // hello arrives. The clock advances only through the injected scheduler.
    private static void VerifyScheduledReceiptTrain()
    {
        using (Control owner = new Control())
        {
            var send = new ScheduledServices();
            using (var client = new ClientAudioBridge(owner, send))
            {
                Guid session = Guid.NewGuid(), transfer = Guid.NewGuid();
                client.HandleHello(Hello(session, transfer, 107), "synthetic camera");
                send.Advance(10000);
                Check(send.Fast.Count == 0 && send.Pending == 0, "An unpublished advertisement never starts a receipt timer or sends audio.");
                client.NotifyPublished(transfer, 107);
                Check(send.Fast.Count == 1, "Publication sends the first exact receipt immediately.");
                send.Advance(10749);
                Check(send.Fast.Count == 1, "The scheduled retry leaves the first short burst intact.");
                send.Advance(10750);
                Check(send.Fast.Count == 2, "One matching hello must retry a lost short receipt at750ms without another video frame.");
                send.Advance(11500);
                Check(send.Fast.Count == 3, "The third short retry follows at1500ms without a repeated hello.");
                send.Advance(12250);
                Check(send.Fallback.Count == 1, "The same scheduled train queues selected-modem fallback at2250ms.");
                DeliveryReceipt receipt;
                Check(DeliveryReceipt.SameToken(send.Fast[2], DeliveryReceipt.FastToken(session, transfer, 107)) &&
                    DeliveryReceipt.TryDecode(send.Fallback[0], session, out receipt) && receipt.Transfer == transfer && receipt.EncodedBytes == 107,
                    "Timed retries preserve the exact published session, transfer and encoded length.");
                send.Advance(1000000);
                Check(send.Fast.Count == 3 && send.Fallback.Count == 1 && send.Pending == 0 && send.MaximumPending == 1,
                    "A disappeared peer leaves no permanent audio train and at most one scheduled receipt callback.");
                client.HandleHello(Hello(session, transfer, 107), "synthetic camera");
                Check(send.Fast.Count == 4, "A fresh matching hello can resume receipt recovery after the peer lease expires.");
            }
        }
    }

    private static void VerifyStaleReceiptTimers()
    {
        // Fire canceled callbacks too, and delay owner dispatch until after state
        // changes. This catches timer-disposal races as well as stale snapshots.
        for (int scenario = 0; scenario < 8; scenario++)
        using (Control owner = new Control())
        {
            var send = new ScheduledServices();
            using (var client = new ClientAudioBridge(owner, send))
            {
                Guid session = Guid.NewGuid(), transfer = Guid.NewGuid();
                BridgeHello hello = Hello(session, transfer, 107);
                client.HandleHello(hello, "synthetic camera"); client.NotifyPublished(transfer, 107);
                Check(send.Timers.Count == 1, "A publication has one pending deadline callback.");
                ScheduledTimer obsolete = send.Timers[0];
                send.Now = 750; obsolete.Fire(); // The callback must only enqueue STA work.
                Check(send.Fast.Count == 1, "A receipt timer marshals to the owner before sending audio.");
                if (scenario == 0) client.HandleHello(Hello(session, Guid.Empty, 0), null);
                else if (scenario == 1) client.HandleHello(Hello(session, Guid.NewGuid(), 107), null);
                else if (scenario == 2) client.HandleHello(Hello(session, transfer, 108), null);
                else if (scenario == 3) client.HandleHello(Hello(Guid.NewGuid(), transfer, 107), null);
                else if (scenario == 4) { hello.VideoTest = Guid.NewGuid(); client.HandleHello(hello, null); }
                else if (scenario == 5) client.Dispose();
                else if (scenario == 6) client.NotifyPublished(Guid.NewGuid(), 107);
                else
                {
                    var command = new FileBenchmarkCommand { Session = session, Test = Guid.NewGuid(), Profile = VideoProfile.Compatible,
                        FileSize = 1024, Seed = 7, Phase = FileBenchmarkPhase.Prepare };
                    client.HandleVideo(BridgeControl.Single(session, FileBenchmarkProtocol.Command(command)));
                }
                send.Drain(); obsolete.Fire(); send.Drain();
                Check(send.Fast.Count == 1 && send.Fallback.Count == 0 && obsolete.Disposed,
                    "Obsolete receipt callbacks cannot send after invalidation scenario " + scenario + ".");
            }
        }
        using (Control owner = new Control())
        {
            var send = new ScheduledServices();
            using (var client = new ClientAudioBridge(owner, send))
            {
                Guid session = Guid.NewGuid(), transfer = Guid.NewGuid();
                BridgeHello hello = Hello(session, transfer, 107);
                client.HandleHello(hello, null); client.NotifyPublished(transfer, 107);
                ScheduledTimer obsolete = send.Timers[0];
                client.HandleHello(Hello(session, Guid.Empty, 0), null);
                client.HandleHello(hello, null);
                send.Now = 750; obsolete.Fire(); send.Drain();
                Check(send.Fast.Count == 1, "An invalidated callback cannot revive when the same advertisement returns.");
                send.Advance(750);
                Check(send.Fast.Count == 2, "The new generation retains its own eligible scheduled retry.");
                send.Now = 100000; send.Timers[send.Timers.Count - 1].Fire(); send.Drain();
                Check(send.Fast.Count == 2 && send.Fallback.Count == 0 && send.Pending == 0,
                    "A callback delivered after the peer lease expires cannot catch up with stale audio.");
            }
        }
    }

    private static BridgeHello Hello(Guid session, Guid transfer, int bytes)
    { return new BridgeHello { Session = session, ForwardTransfer = transfer, ForwardEncodedBytes = bytes, FastDeliveryReceipts = true }; }

    private sealed class ScheduledServices : ClientAudioBridgeServices
    {
        internal long Now;
        internal readonly List<byte[]> Fast = new List<byte[]>(), Fallback = new List<byte[]>();
        internal readonly List<ScheduledTimer> Timers = new List<ScheduledTimer>();
        private readonly Queue<Action> dispatch = new Queue<Action>();
        internal int MaximumPending;
        internal ScheduledServices() : base(null) { }
        internal int Pending { get { int count = 0; foreach (ScheduledTimer timer in Timers) if (!timer.Disposed && !timer.Fired) count++; return count; } }
        internal override long Milliseconds { get { return Now; } }
        internal override void Enable(Guid session) { }
        internal override void SetBenchmarkMode(bool active) { }
        internal override void Send(byte[] bytes, Guid id) { }
        internal override void SendFastReceipt(Guid session, Guid transfer, int bytes) { Fast.Add(DeliveryReceipt.FastToken(session, transfer, bytes)); }
        internal override void SendDeliveryReceiptFallback(Guid session, Guid transfer, int bytes) { Fallback.Add(DeliveryReceipt.Encode(session, transfer, bytes)); }
        internal override void Dispatch(Control owner, Action action) { dispatch.Enqueue(action); }
        internal override IDisposable Schedule(Action action, int milliseconds)
        { var timer = new ScheduledTimer(action, Now + milliseconds); Timers.Add(timer); MaximumPending = Math.Max(MaximumPending, Pending); return timer; }
        internal void Drain() { while (dispatch.Count != 0) dispatch.Dequeue()(); }
        internal void Advance(long now)
        {
            Now = now;
            for (;;) { ScheduledTimer due = Timers.Find(delegate(ScheduledTimer timer) { return !timer.Disposed && !timer.Fired && timer.At <= Now; }); if (due == null) break; due.Fire(); Drain(); }
        }
    }

    private sealed class ScheduledTimer : IDisposable
    {
        private readonly Action callback;
        internal readonly long At;
        internal bool Fired, Disposed;
        internal ScheduledTimer(Action callback, long at) { this.callback = callback; At = at; }
        internal void Fire() { Fired = true; callback(); }
        public void Dispose() { Disposed = true; }
    }

    private static void VerifyLossRecovery(bool loseAllFast)
    {
        Guid session = Guid.NewGuid(), transfer = Guid.NewGuid();
        var state = new ForwardDeliveryState(); state.Expect(session, transfer, 107, 7, 9);
        var receive = new ReceiveServices { NativeModemsEnabled = loseAllFast };
        int fastPlayed = 0, nativePlayed = 0, confirmations = 0; Exception senderError = null;
        long nativeSamples = 0;
        using (Control owner = new Control())
        using (var server = new ServerAudioBridge(owner, session,
            delegate { throw new Exception("A receipt cannot suppress clipboard data."); }, delegate { },
            delegate { throw new Exception("A receipt cannot become a benchmark."); }, receive))
        {
            server.FastDeliveryReceived = delegate(byte[] token) { DeliveryReceipt receipt; if (state.ConfirmFast(token, 7, 9, out receipt)) confirmations++; };
            server.DeliveryReceived = delegate(DeliveryReceipt receipt) { if (state.Confirm(receipt, 7, 9)) confirmations++; };
            server.Start("synthetic-render"); receive.Drain();
            Check(receive.Samples != null, "Production audio receiver is connected to the synthetic loopback source.");
            var senderServices = new AudioSenderServices {
                Sequence = delegate { return 42; }, AddListener = delegate { return true; }, RemoveListener = delegate { return true; },
                Capture = delegate { throw new Exception("Receipt workflow cannot read clipboard contents."); },
                Outputs = delegate { return new[] { new AudioEndpoint("waveout:0", "Horizon synthetic output") }; },
                CreatePlayer = delegate(string id) { return new AudioPlayer(id); },
                DisposePlayer = delegate(AudioPlayer player) { player.Dispose(); },
                Play = delegate(AudioPlayer player, float[] samples, CancellationToken token) {
                    token.ThrowIfCancellationRequested();
                    if (samples.Length == 14080 || samples.Length == 28160)
                    {
                        int attempt = Interlocked.Increment(ref fastPlayed);
                        if (loseAllFast || attempt <= 2) return; // Deterministic transport loss, not a false ACK.
                    }
                    else { Interlocked.Increment(ref nativePlayed); Interlocked.Exchange(ref nativeSamples, samples.Length); }
                    for (int offset = 0; offset < samples.Length; offset += 960)
                    {
                        token.ThrowIfCancellationRequested();
                        int count = Math.Min(960, samples.Length - offset); var part = new float[count];
                        for (int i = 0; i < count; i++) part[i] = (float)(Math.Round(samples[offset + i] * 0.67 * Int16.MaxValue) / Int16.MaxValue);
                        receive.Samples(part);
                    }
                }
            };
            using (var sender = new AudioClipboardSender(owner, senderServices))
            {
                sender.Error = delegate(Exception error) { senderError = error; };
                if (loseAllFast) sender.SetModem(AudioModemKind.Aicodix);
                var send = new SendServices(sender);
                using (var client = new ClientAudioBridge(owner, send))
                {
                    var hello = new BridgeHello { Session = session, ForwardTransfer = transfer, ForwardEncodedBytes = 107, FastDeliveryReceipts = true };
                    client.HandleHello(hello, "synthetic camera");
                    Check(Volatile.Read(ref fastPlayed) == 0, "Hello alone cannot acknowledge data that has not been published.");
                    client.NotifyPublished(transfer, 107);
                    Until(delegate { return Volatile.Read(ref fastPlayed) == 1; }, receive, "The first short publication receipt is attempted immediately.");
                    for (int attempt = 2; attempt <= 3; attempt++)
                    {
                        send.Advance((attempt - 1) * 750);
                        int expected = attempt;
                        Until(delegate { return Volatile.Read(ref fastPlayed) == expected; }, receive, "A lost short burst is retried at the new750ms bound.");
                    }
                    if (loseAllFast)
                    {
                        Check(confirmations == 0, "Dropping all short PCM produces no fabricated delivery notification.");
                        send.Advance(2250);
                    }
                    Until(delegate { return confirmations == 1; }, receive, "A real demodulated exact receipt confirms the publication once.");
                    Check(senderError == null, "No sender error occurred during receipt recovery.");
                    Check(Volatile.Read(ref fastPlayed) == 3 && Volatile.Read(ref nativePlayed) == (loseAllFast ? 1 : 0),
                        "Loss recovery emits three short bursts and at most one selected-modem fallback pass.");
                    send.Now += 10000; hello.ForwardTransfer = Guid.Empty; hello.ForwardEncodedBytes = 0;
                    client.HandleHello(hello, "synthetic camera"); Pump(receive, 40);
                    Check(Volatile.Read(ref fastPlayed) == 3 && Volatile.Read(ref nativePlayed) == (loseAllFast ? 1 : 0), "Server confirmation stops further playback.");
                    byte[] duplicate = DeliveryReceipt.FastToken(session, transfer, 107); server.ReceiveFastReceipt(duplicate); receive.Drain();
                    Check(confirmations == 1 && receive.Publications == 0 && receive.Decodes == 0,
                        "Duplicate or fallback receipts share exact once-only state and cannot publish reverse clipboard data.");
                    if (loseAllFast) Console.WriteLine("All short ACKs lost: selected aicodix fallback queued at2250ms; one native PCM pass={0:F3}ms confirmed.",
                        Interlocked.Read(ref nativeSamples) * 1000.0 / AudioModem.SampleRate);
                    else Console.WriteLine("First two short ACKs lost: third queued by timer at1500ms; protected PCM586.667ms. Previous retry policy queued the third at16000ms.");
                }
            }
        }
    }

    private sealed class SendServices : ClientAudioBridgeServices
    {
        internal long Now;
        private readonly List<ScheduledTimer> timers = new List<ScheduledTimer>();
        internal SendServices(AudioClipboardSender sender) : base(sender) { }
        internal override long Milliseconds { get { return Now; } }
        internal override void Dispatch(Control owner, Action action) { action(); }
        internal override IDisposable Schedule(Action action, int milliseconds)
        { var timer = new ScheduledTimer(action, Now + milliseconds); timers.Add(timer); return timer; }
        internal void Advance(long now)
        {
            Now = now;
            for (;;) { ScheduledTimer due = timers.Find(delegate(ScheduledTimer timer) { return !timer.Disposed && !timer.Fired && timer.At <= Now; }); if (due == null) break; due.Fire(); }
        }
    }

    private sealed class ReceiveServices : ServerAudioBridgeServices
    {
        private readonly object gate = new object();
        private readonly Queue<Action> ui = new Queue<Action>();
        internal Action<float[]> Samples;
        internal int Publications, Decodes;
        internal override void Queue(Action action) { action(); }
        internal override void Dispatch(Control owner, Action action) { lock (gate) ui.Enqueue(action); }
        internal void Drain() { for (;;) { Action action; lock (gate) { if (ui.Count == 0) return; action = ui.Dequeue(); } action(); } }
        internal override IDisposable StartCapture(string id, Action<float[]> samples, Action<Exception> error) { Samples = samples; return new Nothing(); }
        internal override IServerPublicationTimer CreateTimer(Action tick) { return new Nothing(); }
        internal override IServerDeferredPaste CreatePaste(Control owner, Func<bool> receiving) { return new Nothing(); }
        internal override DecodedPayload Decode(byte[] data) { Decodes++; throw new Exception("No clipboard decode allowed."); }
        internal override void Publish(DecodedPayload payload, string root, Guid id) { Publications++; throw new Exception("No clipboard publish allowed."); }
    }
    private sealed class Nothing : IDisposable, IServerPublicationTimer, IServerDeferredPaste
    { public void Dispose() { } public void Start() { } public void Stop() { } public void Complete() { } public void Cancel() { } }
    private static void Until(Func<bool> condition, ReceiveServices services, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.ElapsedMilliseconds < 5000) { services.Drain(); Application.DoEvents(); Thread.Sleep(1); }
        services.Drain(); Check(condition(), message);
    }
    private static void Pump(ReceiveServices services, int milliseconds)
    { var elapsed = Stopwatch.StartNew(); while (elapsed.ElapsedMilliseconds < milliseconds) { services.Drain(); Application.DoEvents(); Thread.Sleep(1); } }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
