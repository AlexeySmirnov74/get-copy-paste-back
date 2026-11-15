using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace HorizonClient
{
    internal sealed class AudioEncodedBlock
    {
        internal float[] Samples;
        internal int Packets, WireBytes;
    }

    internal static class AudioTransmissionEncoder
    {
        internal static void Validate(AudioModemKind kind)
        {
            AudioModemCatalog.GetId(kind);
            if (kind != AudioModemKind.Fsk) NativeAudioCodec.Get(kind);
        }

        internal static AudioEncodedBlock Encode(AudioModemKind kind, Guid transfer, int firstIndex, byte[] data)
        {
            if (kind == AudioModemKind.Fsk)
            {
                int count = Math.Max(1, (data.Length + AudioModem.MaxPayloadBytes - 1) / AudioModem.MaxPayloadBytes);
                int length = Math.Min(AudioModem.MaxPayloadBytes, data.Length - firstIndex * AudioModem.MaxPayloadBytes);
                byte[] chunk = new byte[length]; Buffer.BlockCopy(data, firstIndex * AudioModem.MaxPayloadBytes, chunk, 0, length);
                var packet = new Packet { TransferId = transfer, Index = firstIndex, Count = count, TotalLength = data.Length,
                    Data = chunk, ChunkSize = AudioModem.MaxPayloadBytes, Profile = VideoProfile.Compatible };
                return new AudioEncodedBlock { Samples = AudioModem.Encode(packet), Packets = 1, WireBytes = AudioModem.HeaderSize + length + 4 };
            }
            NativeAudioCodec codec = NativeAudioCodec.Get(kind);
            int packets;
            byte[] wire = AudioPacketWire.Encode(kind, transfer, firstIndex, data, codec.MaximumPayload, out packets);
            return new AudioEncodedBlock { Samples = codec.Encode(wire), Packets = packets, WireBytes = wire.Length };
        }

        internal static AudioEncodedBlock EncodeFastReceipt(byte[] wire)
        {
            // Keep two independently protected frames in one playback. A short
            // startup/payload erasure can destroy one frame without delaying
            // confirmation until a multi-second native fallback. Old receivers
            // already accept each frame; the owner confirms the exact token once.
            float[] frame = AudioModem.EncodeFastReceipt(wire);
            float[] samples = new float[checked(frame.Length * 2)];
            Array.Copy(frame, samples, frame.Length);
            Array.Copy(frame, 0, samples, frame.Length, frame.Length);
            DiagnosticLog.Write("client.audio.fast_receipt.encoded", "protected_copies", 2,
                "logical_wire_bytes", wire.Length, "transmitted_wire_bytes", wire.Length * 2,
                "samples", samples.Length, "nominal_audio_ms", samples.Length * 1000L / AudioModem.SampleRate);
            return new AudioEncodedBlock { Samples = samples, Packets = 1, WireBytes = AudioModem.FastReceiptWireSize };
        }
        internal static AudioEncodedBlock EncodeCompactFileReport(byte[] wire)
        { return new AudioEncodedBlock { Samples = AudioModem.EncodeCompactFileReport(wire), Packets = 1, WireBytes = wire.Length }; }
    }

    internal interface INativeAudioDecoder : IDisposable { byte[][] Feed(float[] samples); }

    // WASAPI callbacks keep their existing FSK path. Each native decoder owns a
    // separate bounded worker so OFDM work never blocks the capture callback.
    internal sealed class AudioReceiveMultiplexer : IDisposable
    {
        private readonly AudioDecoder fsk = new AudioDecoder();
        private readonly NativeWorker[] workers;
        private readonly object fskGate = new object();
        private readonly string decoderId = Guid.NewGuid().ToString("N"), context;
        private readonly Stopwatch diagnosticsClock = Stopwatch.StartNew();
        private long diagnosticsAt;
        private AudioDecoderDiagnostics previousDiagnostics = new AudioDecoderDiagnostics();
        private string nativeBoundary;
        private volatile bool disposed;
        internal event Action<AudioModemKind, Exception> DecoderFailed;

        internal AudioReceiveMultiplexer(Action<Packet> receive, bool nativeEnabled, Action<byte[]> fastReceipt = null, Action<byte[]> compactFileReport = null, string diagnosticContext = null, Func<AudioModemKind, INativeAudioDecoder> decoderFactory = null)
        {
            if (receive == null) throw new ArgumentNullException("receive");
            fsk.PacketReceived += delegate(Packet packet) { nativeBoundary = "verified_fsk_packet"; receive(packet); };
            fsk.FastReceiptReceived += delegate(byte[] token) { nativeBoundary = "verified_fsk_fast_receipt"; if (fastReceipt != null) fastReceipt(token); };
            fsk.CompactFileReportReceived += delegate(byte[] wire) { nativeBoundary = "verified_fsk_file_report"; if (compactFileReport != null) compactFileReport(wire); };
            context = diagnosticContext;
            decoderFactory = decoderFactory ?? delegate(AudioModemKind kind) { return new NativeDecoder(NativeAudioCodec.Get(kind).NewDecoder()); };
            workers = nativeEnabled ? new[] { new NativeWorker(AudioModemKind.Quiet, receive, decoderId, context, decoderFactory, NotifyDecoderFailure), new NativeWorker(AudioModemKind.Aicodix, receive, decoderId, context, decoderFactory, NotifyDecoderFailure) } : new NativeWorker[0];
        }
        internal Exception GetDecoderFailure(AudioModemKind kind)
        {
            foreach (NativeWorker worker in workers) if (worker.Kind == kind) return worker.Failure;
            return null;
        }
        private void NotifyDecoderFailure(AudioModemKind kind, Exception error)
        {
            Action<AudioModemKind, Exception> handlers = DecoderFailed;
            if (disposed || handlers == null) return;
            foreach (Action<AudioModemKind, Exception> handler in handlers.GetInvocationList())
            {
                if (disposed) return;
                try { handler(kind, error); }
                catch (Exception callbackError) { DiagnosticLog.Error("server.audio.modem.failure_callback_failed", callbackError, "modem", AudioModemCatalog.GetId(kind)); }
            }
        }

        private sealed class NativeDecoder : INativeAudioDecoder
        {
            private readonly NativeAudioCodec.Decoder decoder;
            internal NativeDecoder(NativeAudioCodec.Decoder decoder) { this.decoder = decoder; }
            public byte[][] Feed(float[] samples) { return decoder.Feed(samples); }
            public void Dispose() { decoder.Dispose(); }
        }
        internal void Feed(float[] samples)
        {
            if (disposed || samples == null || samples.Length == 0) return;
            lock (fskGate)
            {
                if (disposed) return;
                if (workers.Length == 0) fsk.Feed(samples);
                else
                {
                    // Own FSK controls interrupt native playback. Keep a reset
                    // behind the already queued PCM, never clear good frames.
                    // Ten-ms slices end within the FSK guard plus native lead,
                    // even when a capture callback contains several waveforms.
                    for (int offset = 0; offset < samples.Length && !disposed; offset += AudioModem.GuardSamples)
                    {
                        int count = Math.Min(AudioModem.GuardSamples, samples.Length - offset);
                        float[] copy = new float[count]; Array.Copy(samples, offset, copy, 0, count);
                        nativeBoundary = null; fsk.Feed(copy);
                        foreach (NativeWorker worker in workers) worker.Feed(copy);
                        if (nativeBoundary != null) foreach (NativeWorker worker in workers) worker.Boundary(nativeBoundary);
                    }
                }
                LogFskDiagnostics(false);
            }
        }
        public void Dispose() { lock (fskGate) { if (disposed) return; disposed = true; LogFskDiagnostics(true); } foreach (NativeWorker worker in workers) worker.Dispose(); }

        private void LogFskDiagnostics(bool final)
        {
            if (!DiagnosticLog.IsEnabled || (!final && diagnosticsClock.ElapsedMilliseconds - diagnosticsAt < 5000)) return;
            AudioDecoderDiagnostics now = fsk.GetDiagnostics(), prior = previousDiagnostics;
            long samples = now.InputSamples - prior.InputSamples;
            double squares = Math.Max(0, now.SignalSquares - prior.SignalSquares);
            DiagnosticLog.Write("server.audio.fsk.summary", "decoder", decoderId, "context", context, "final", final,
                "interval_ms", diagnosticsClock.ElapsedMilliseconds - diagnosticsAt, "input_samples", samples, "nonzero_samples", now.NonzeroSamples - prior.NonzeroSamples,
                "rms", samples == 0 ? 0 : Math.Sqrt(squares / samples), "total_peak", now.Peak, "nonfinite", now.NonfiniteSamples - prior.NonfiniteSamples,
                "preamble_matches", now.PreambleMatches - prior.PreambleMatches, "sync_rejections", now.SyncFailures - prior.SyncFailures,
                "frames_aligned", now.SyncCandidates - prior.SyncCandidates, "byte_framing_failures", now.ByteFailures - prior.ByteFailures,
                "header_rejections", now.HeaderFailures - prior.HeaderFailures, "crc_or_wire_rejections", now.CrcFailures - prior.CrcFailures,
                "buffer_overruns", now.Overruns - prior.Overruns, "legacy_packets", now.Packets - prior.Packets,
                "fast_receipts", now.FastReceipts - prior.FastReceipts, "compact_file_reports", now.FileReports - prior.FileReports,
                "buffered_samples", now.BufferedSamples, "frame_active", now.FrameActive, "frame_bytes", now.FrameBytes, "expected_bytes", now.ExpectedBytes,
                "last_symbol_period", now.LastPeriod, "last_sync_score", now.LastSyncScore);
            previousDiagnostics = now; diagnosticsAt = diagnosticsClock.ElapsedMilliseconds;
        }

        private sealed class NativeWorker : IDisposable
        {
            private readonly AudioModemKind kind;
            private readonly Action<Packet> receive;
            private readonly Func<AudioModemKind, INativeAudioDecoder> createDecoder;
            private readonly Action<AudioModemKind, Exception> failed;
            private readonly object gate = new object();
            private readonly Queue<DecoderWork> pending = new Queue<DecoderWork>();
            private readonly AutoResetEvent wake = new AutoResetEvent(false);
            private readonly Thread thread;
            private long queuedSamples, droppedSamples;
            private readonly string decoderId, context;
            private readonly Stopwatch diagnosticsClock = Stopwatch.StartNew();
            private long diagnosticsAt, inputSamples, processedSamples, decodedBatches, rejectedBatches, resets, totalDroppedSamples, processingTicks;
            private bool stopping, discontinuity;
            private Exception failure;
            internal AudioModemKind Kind { get { return kind; } }
            internal Exception Failure { get { lock (gate) return failure; } }
            internal NativeWorker(AudioModemKind kind, Action<Packet> receive, string decoderId, string context, Func<AudioModemKind, INativeAudioDecoder> createDecoder, Action<AudioModemKind, Exception> failed)
            {
                this.kind = kind; this.receive = receive;
                this.decoderId = decoderId; this.context = context;
                this.createDecoder = createDecoder; this.failed = failed;
                thread = new Thread(Run) { IsBackground = true, Name = AudioModemCatalog.GetId(kind) + " audio decoder" };
                thread.Start();
            }
            internal void Feed(float[] samples)
            {
                lock (gate)
                {
                    if (stopping) return;
                    inputSamples += samples.Length;
                    if (samples.Length > AudioModem.SampleRate * 10) { droppedSamples += samples.Length; discontinuity = true; return; }
                    if (pending.Count >= 1023 || queuedSamples + samples.Length > AudioModem.SampleRate * 10)
                    {
                        droppedSamples += queuedSamples; queuedSamples = 0; pending.Clear(); discontinuity = true;
                    }
                    pending.Enqueue(new DecoderWork { Samples = samples }); queuedSamples += samples.Length; wake.Set();
                }
            }
            internal void Boundary(string reason)
            {
                lock (gate)
                {
                    if (stopping) return;
                    pending.Enqueue(new DecoderWork { Boundary = reason }); wake.Set();
                }
            }
            private void Run()
            {
                INativeAudioDecoder decoder = null;
                Exception terminal = null;
                bool feedRecoveryUsed = false;
                try
                {
                    decoder = createDecoder(kind);
                    DiagnosticLog.Write("server.audio.modem.started", "modem", AudioModemCatalog.GetId(kind));
                    while (true)
                    {
                        DecoderWork work = null; bool reset; long lost;
                        lock (gate)
                        {
                            if (stopping) return;
                            reset = discontinuity; discontinuity = false; lost = droppedSamples; droppedSamples = 0;
                            if (pending.Count != 0) { work = pending.Dequeue(); if (work.Samples != null) queuedSamples -= work.Samples.Length; }
                        }
                        if (reset)
                        {
                            INativeAudioDecoder old = decoder; decoder = null; old.Dispose(); decoder = createDecoder(kind);
                            resets++; totalDroppedSamples += lost;
                            DiagnosticLog.Write("server.audio.modem.overrun", "modem", AudioModemCatalog.GetId(kind), "dropped_samples", lost);
                        }
                        LogDiagnostics(false);
                        if (work == null) { wake.WaitOne(5000); continue; }
                        if (work.Boundary != null)
                        {
                            INativeAudioDecoder old = decoder; decoder = null; old.Dispose(); decoder = createDecoder(kind); resets++;
                            DiagnosticLog.Write("server.audio.modem.reset", "decoder", decoderId, "context", context,
                                "modem", AudioModemCatalog.GetId(kind), "reason", work.Boundary);
                            continue;
                        }
                        long started = DiagnosticLog.IsEnabled ? Stopwatch.GetTimestamp() : 0;
                        byte[][] messages;
                        try { messages = decoder.Feed(work.Samples); }
                        catch (InvalidDataException error)
                        {
                            if (feedRecoveryUsed) throw;
                            feedRecoveryUsed = true;
                            long abandoned;
                            lock (gate)
                            {
                                if (stopping) return;
                                abandoned = queuedSamples + droppedSamples + work.Samples.Length;
                                pending.Clear(); queuedSamples = 0; droppedSamples = 0; discontinuity = false;
                            }
                            // A partially consumed block cannot be replayed safely.
                            // Only future PCM reaches the replacement context.
                            INativeAudioDecoder old = decoder; decoder = null; old.Dispose();
                            decoder = createDecoder(kind); resets++; totalDroppedSamples += abandoned;
                            DiagnosticLog.Error("server.audio.modem.feed_recovered", error, "modem", AudioModemCatalog.GetId(kind),
                                "dropped_samples", abandoned, "feed_recreations", 1);
                            continue;
                        }
                        if (started != 0) processingTicks += Stopwatch.GetTimestamp() - started;
                        processedSamples += work.Samples.Length;
                        foreach (byte[] message in messages)
                        {
                            Packet[] packets = AudioPacketWire.Decode(kind, message);
                            if (packets == null) { rejectedBatches++; DiagnosticLog.Write("server.audio.modem.wire_rejected", "modem", AudioModemCatalog.GetId(kind), "wire_bytes", message.Length); continue; }
                            decodedBatches++;
                            DiagnosticLog.Write("server.audio.modem.batch_decoded", "modem", AudioModemCatalog.GetId(kind), "wire_bytes", message.Length,
                                "transfer", packets[0].TransferId, "first_index", packets[0].Index, "packets", packets.Length, "total_packets", packets[0].Count);
                            foreach (Packet packet in packets)
                            {
                                lock (gate) { if (stopping) return; }
                                try { receive(packet); } catch (Exception error) { DiagnosticLog.Error("server.audio.modem.packet_failed", error, "modem", AudioModemCatalog.GetId(kind)); }
                            }
                        }
                    }
                }
                catch (Exception error) { terminal = error; }
                finally
                {
                    try { if (decoder != null) decoder.Dispose(); }
                    catch (Exception error) { if (terminal == null) terminal = error; }
                    finally
                    {
                        LogDiagnostics(true);
                        lock (gate)
                        {
                            if (!stopping && terminal != null) failure = terminal;
                            stopping = true; pending.Clear(); queuedSamples = 0; wake.Dispose();
                        }
                    }
                    if (failure != null)
                    {
                        DiagnosticLog.Error("server.audio.modem.failed", failure, "modem", AudioModemCatalog.GetId(kind));
                        failed(kind, failure);
                    }
                    DiagnosticLog.Write("server.audio.modem.stopped", "modem", AudioModemCatalog.GetId(kind));
                }
            }
            private void LogDiagnostics(bool final)
            {
                if (!DiagnosticLog.IsEnabled || (!final && diagnosticsClock.ElapsedMilliseconds - diagnosticsAt < 5000)) return;
                lock (gate)
                {
                    DiagnosticLog.Write("server.audio.modem.summary", "decoder", decoderId, "context", context, "modem", AudioModemCatalog.GetId(kind), "final", final,
                        "elapsed_ms", diagnosticsClock.ElapsedMilliseconds, "input_samples", inputSamples, "processed_samples", processedSamples,
                        "queued_samples", queuedSamples, "queued_blocks", pending.Count, "decoded_batches", decodedBatches, "wire_rejected", rejectedBatches,
                        "resets", resets, "dropped_samples", totalDroppedSamples + droppedSamples, "processing_ms", processingTicks * 1000.0 / Stopwatch.Frequency);
                }
                diagnosticsAt = diagnosticsClock.ElapsedMilliseconds;
            }
            private sealed class DecoderWork { internal float[] Samples; internal string Boundary; }
            public void Dispose()
            {
                lock (gate) { if (stopping) return; stopping = true; pending.Clear(); queuedSamples = 0; wake.Set(); }
                if (Thread.CurrentThread != thread) thread.Join(100);
            }
        }
    }
}
