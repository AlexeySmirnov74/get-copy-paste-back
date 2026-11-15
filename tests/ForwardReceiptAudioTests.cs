using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

// Exercises the production sender codecs, automatic audio demodulation, server
// control dispatch and once-only delivery state. Only PCM arrays are played.
internal static class ForwardReceiptAudioTests
{
    private static int checks;

    [STAThread]
    private static int Main()
    {
        try
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            VerifyCompactReceipt();
            foreach (AudioModemKind kind in new[] { AudioModemKind.Fsk, AudioModemKind.Quiet, AudioModemKind.Aicodix })
            {
                VerifyModem(kind);
                VerifyIndependentProbe(kind);
            }
            Console.WriteLine("Forward receipt audio checks passed: " + checks + " assertions, " + elapsed.ElapsedMilliseconds +
                " ms. Real FSK/Quiet/aicodix PCM codecs, automatic receiver, STA delivery and once-only acknowledgement. No speaker, microphone, camera or clipboard was opened.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void VerifyModem(AudioModemKind kind)
    {
        const int bytes = 10240, generation = 7;
        const uint sequence = 24;
        Guid session = Guid.NewGuid(), transfer = Guid.NewGuid();
        var state = new ForwardDeliveryState(); state.Expect(session, transfer, bytes, generation, sequence);
        var services = new SyntheticServices();
        int ownerThread = Thread.CurrentThread.ManagedThreadId, receipts = 0, confirmations = 0;
        using (Control owner = new Control())
        using (var bridge = new ServerAudioBridge(owner, session,
            delegate { throw new Exception("A receipt must not suppress clipboard echo."); }, delegate { },
            delegate { throw new Exception("A receipt must not become a benchmark report."); }, services))
        {
            bridge.DeliveryReceived = delegate(DeliveryReceipt receipt)
            {
                Check(Thread.CurrentThread.ManagedThreadId == ownerThread, "Receipt callback runs on the server owner thread.");
                Check(receipt.Session == session, "Foreign-session receipts are filtered before delivery dispatch.");
                receipts++;
                bool accepted = state.Confirm(receipt, generation, sequence);
                Check(accepted == (receipts == 3), "Only the first exact receipt confirms; wrong transfer, wrong size and duplicate each fail independently.");
                if (accepted) confirmations++;
            };
            bridge.Start("synthetic-output"); services.DrainUi();
            Check(services.Samples != null, "Synthetic capture is wired to the production automatic audio decoder.");
            byte[][] messages = {
                DeliveryReceipt.Encode(Guid.NewGuid(), transfer, bytes),
                DeliveryReceipt.Encode(session, Guid.NewGuid(), bytes),
                DeliveryReceipt.Encode(session, transfer, bytes + 1),
                DeliveryReceipt.Encode(session, transfer, bytes),
                DeliveryReceipt.Encode(session, transfer, bytes)
            };
            var waves = new List<float[]>(); long totalSamples = 0;
            foreach (byte[] message in messages)
            {
                Check(message.Length == 52, "Every delivery receipt is exactly 52 bytes.");
                AudioEncodedBlock encoded = AudioTransmissionEncoder.Encode(kind, Guid.NewGuid(), 0, message);
                Check(encoded.Packets == 1, "A receipt occupies exactly one packet with every modem.");
                Quantize(encoded.Samples); waves.Add(encoded.Samples); totalSamples += encoded.Samples.Length;
            }
            Check(waves[0].Length + waves[1].Length < AudioModem.SampleRate * 10,
                "At most two in-flight receipt frames fit within each automatic decoder's bounded queue.");
            Stopwatch timer = Stopwatch.StartNew();
            int[] lengths = { 1, 257, 960, 8192, 313 };
            for (int wave = 0; wave < waves.Count; wave++)
            {
                float[] samples = waves[wave];
                for (int offset = 0, chunk = 0; offset < samples.Length; chunk++)
                {
                    int count = Math.Min(lengths[chunk % lengths.Length], samples.Length - offset);
                    float[] block = new float[count]; Array.Copy(samples, offset, block, 0, count); offset += count;
                    services.Samples(block);
                }
                // The foreign-session frame intentionally has no callback.
                // Pair it with the next frame, then drain before adding more
                // PCM so synthetic faster-than-real-time capture cannot overrun
                // a native worker's ten-second queue.
                if (wave == 0) continue;
                Stopwatch wait = Stopwatch.StartNew();
                while (receipts < wave && wait.ElapsedMilliseconds < 5000)
                { services.DrainUi(); if (receipts < wave) Thread.Sleep(1); }
                Check(receipts == wave, "The next same-session receipt is decoded before more synthetic PCM is queued.");
            }
            services.DrainUi();
            Check(receipts == 4, AudioModemCatalog.GetId(kind) + " delivers all same-session receipts and rejects the foreign session.");
            Check(confirmations == 1, "Only the matching transfer and exact encoded size acknowledge delivery; a duplicate adds no second signal.");
            Check(!bridge.IsReceiving && services.Publications == 0 && services.Decodes == 0,
                "Receipt PCM cannot publish clipboard data or start a reverse clipboard receive.");
            Console.WriteLine("{0}: receipts={1}, delivery_signals={2}, samples={3}, synthetic_audio_seconds={4:F3}, decode_ms={5}",
                AudioModemCatalog.GetId(kind), receipts, confirmations, totalSamples, totalSamples / (double)AudioModem.SampleRate, timer.ElapsedMilliseconds);
        }
    }

    private static void VerifyIndependentProbe(AudioModemKind kind)
    {
        var services = new SyntheticServices(); var statuses = new List<string>();
        using (Control owner = new Control())
        using (var bridge = new ServerAudioBridge(owner, Guid.NewGuid(),
            delegate { throw new Exception("Probe cannot acknowledge clipboard publication."); }, statuses.Add,
            delegate { throw new Exception("Probe cannot become a speed report."); }, services))
        {
            bridge.Start("synthetic-output"); services.DrainUi(); statuses.Clear(); bridge.SetBenchmarkMode(true);
            Guid id = Guid.NewGuid(); byte[] payload = AudioDiagnosticProbe.Encode(id, kind, DateTime.UtcNow.Ticks);
            AudioEncodedBlock encoded = AudioTransmissionEncoder.Encode(kind, id, 0, payload);
            Quantize(encoded.Samples);
            for (int repeat = 0; repeat < 3; repeat++)
            {
                if (repeat == 2)
                {
                    id = Guid.NewGuid(); payload = AudioDiagnosticProbe.Encode(id, kind, DateTime.UtcNow.Ticks);
                    encoded = AudioTransmissionEncoder.Encode(kind, id, 0, payload); Quantize(encoded.Samples);
                }
                for (int offset = 0; offset < encoded.Samples.Length; offset += 960)
                {
                    float[] block = new float[Math.Min(960, encoded.Samples.Length - offset)];
                    Array.Copy(encoded.Samples, offset, block, 0, block.Length); services.Samples(block);
                }
                Stopwatch wait = Stopwatch.StartNew();
                int expected = repeat < 2 ? 1 : 2;
                while (statuses.Count < expected && wait.ElapsedMilliseconds < 5000)
                { services.DrainUi(); if (statuses.Count < expected) Thread.Sleep(1); }
                Check(statuses.Count == expected && statuses[expected - 1].Contains(AudioModemCatalog.GetId(kind)),
                    "Independent probes need no video Hello, repeated passes notify once and a new user probe notifies again.");
            }
            Check(!bridge.IsReceiving && services.Publications == 0 && services.Decodes == 0,
                "Independent diagnostic PCM never reaches clipboard decoding or publication.");
        }
    }

    private static void VerifyCompactReceipt()
    {
        Guid session = Guid.NewGuid(), transfer = Guid.NewGuid(); int ownerThread = Thread.CurrentThread.ManagedThreadId;
        var state = new ForwardDeliveryState(); state.Expect(session, transfer, 10240, 7, 9);
        var services = new SyntheticServices { NativeModemsEnabled = false };
        int dispatched = 0, confirmed = 0;
        using (Control owner = new Control())
        using (var bridge = new ServerAudioBridge(owner, session, delegate { throw new Exception("No clipboard publication."); }, delegate { }, delegate { }, services))
        {
            bridge.FastDeliveryReceived = delegate(byte[] token)
            {
                Check(Thread.CurrentThread.ManagedThreadId == ownerThread, "Compact receipt dispatch uses the owner thread.");
                dispatched++; DeliveryReceipt receipt;
                if (state.ConfirmFast(token, 7, 9, out receipt)) confirmed++;
            };
            bridge.Start("synthetic-output"); services.DrainUi();
            foreach (Guid sentSession in new[] { Guid.NewGuid(), session, session })
            {
                float[] pcm = AudioModem.EncodeFastReceipt(DeliveryReceipt.EncodeFast(sentSession, transfer, 10240)); Quantize(pcm);
                services.Samples(pcm); services.DrainUi();
            }
            Check(dispatched == 3 && confirmed == 1, "Automatic receiver dispatches compact tokens, and only the current exact state confirms once.");
            Check(services.Publications == 0 && services.Decodes == 0 && !bridge.IsReceiving, "Compact receipts never enter the reverse clipboard assembler.");
            byte[] mutable = DeliveryReceipt.FastToken(session, transfer, 10240);
            bridge.ReceiveFastReceipt(mutable); mutable[0] ^= 1; services.DrainUi();
            Check(dispatched == 4 && confirmed == 1, "Callback lifetime and duplicate handling cannot confirm twice.");
        }
    }

    private static void Quantize(float[] samples)
    {
        Check(samples != null && samples.Length != 0, "The modem emits a nonempty PCM waveform.");
        for (int i = 0; i < samples.Length; i++)
        {
            if (Single.IsNaN(samples[i]) || Single.IsInfinity(samples[i]) || Math.Abs(samples[i]) > AudioModem.PeakAmplitude + 0.0001f)
                throw new InvalidOperationException("Invalid modem PCM amplitude.");
            samples[i] = (float)(Math.Round(samples[i] * 0.67 * Int16.MaxValue) / Int16.MaxValue);
        }
    }

    private sealed class SyntheticServices : ServerAudioBridgeServices
    {
        private readonly object gate = new object();
        private readonly Queue<Action> ui = new Queue<Action>();
        internal Action<float[]> Samples;
        internal int Publications, Decodes;
        internal SyntheticServices() { NativeModemsEnabled = true; }
        internal override void Queue(Action action) { action(); }
        internal override void Dispatch(Control owner, Action action) { lock (gate) ui.Enqueue(action); }
        internal void DrainUi()
        {
            for (;;)
            {
                Action next; lock (gate) { if (ui.Count == 0) return; next = ui.Dequeue(); }
                next();
            }
        }
        internal override IDisposable StartCapture(string endpoint, Action<float[]> samples, Action<Exception> error)
        { Samples = samples; return new Nothing(); }
        internal override IServerPublicationTimer CreateTimer(Action tick) { return new Nothing(); }
        internal override IServerDeferredPaste CreatePaste(Control owner, Func<bool> receiving) { return new Nothing(); }
        internal override DecodedPayload Decode(byte[] encoded) { Decodes++; throw new Exception("No clipboard payload was requested."); }
        internal override void Publish(DecodedPayload payload, string root, Guid id) { Publications++; throw new Exception("No clipboard publication was requested."); }
    }
    private sealed class Nothing : IDisposable, IServerPublicationTimer, IServerDeferredPaste
    {
        public void Dispose() { }
        public void Start() { }
        public void Stop() { }
        public void Complete() { }
        public void Cancel() { }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
}
