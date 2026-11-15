using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace HorizonClient
{
    // A wave owns all extra players until every route has stopped. Packet
    // indices are committed by the caller only after the whole wave succeeds.
    internal static class ParallelAudioWave
    {
        internal static int Play(AudioSenderServices services, AudioPlayer primary, string primaryId, string[] outputs,
            AudioModemKind modem, Guid transfer, int firstIndex, int totalPackets, byte[] bytes, CancellationToken token)
        { return PlayCore(services, primary, primaryId, outputs, modem, transfer, firstIndex, totalPackets, bytes, null, token); }

        internal static void Broadcast(AudioSenderServices services, AudioPlayer primary, string primaryId, string[] outputs,
            Guid transfer, AudioEncodedBlock block, CancellationToken token)
        {
            if (block == null || block.Packets != 1) throw new ArgumentException("A single control frame is required.");
            PlayCore(services, primary, primaryId, outputs, AudioModemKind.Fsk, transfer, 0, outputs == null ? 0 : outputs.Length, null, block, token);
        }

        private static int PlayCore(AudioSenderServices services, AudioPlayer primary, string primaryId, string[] outputs,
            AudioModemKind modem, Guid transfer, int firstIndex, int totalPackets, byte[] bytes, AudioEncodedBlock broadcast, CancellationToken token)
        {
            if (services == null || primary == null || outputs == null || outputs.Length < 1 || outputs.Length > 16)
                throw new ArgumentException("A bounded verified output set and primary player are required.");
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (string output in outputs) if (output == null || !unique.Add(output)) throw new ArgumentException("Unique verified routes are required.");
            var jobs = new List<Job>(); int next = firstIndex;
            try
            {
                foreach (string output in outputs)
                {
                    if (next >= totalPackets) break;
                    token.ThrowIfCancellationRequested();
                    AudioEncodedBlock block = broadcast ?? services.EncodeAudio(modem, transfer, next, bytes);
                    if (block == null || block.Samples == null || block.Packets < 1 || block.Packets > totalPackets - next)
                        throw new InvalidOperationException("Audio modem returned an invalid striped batch.");
                    var job = new Job { Output = output, Index = broadcast == null ? next : 0, Block = block };
                    jobs.Add(job); next += block.Packets;
                    if (String.Equals(output, primaryId, StringComparison.Ordinal)) job.Player = primary;
                    else { job.Player = services.CreatePlayer(output); job.Owned = true; }
                    if (job.Player == null) throw new InvalidOperationException("Cannot open a verified audio route.");
                }
                token.ThrowIfCancellationRequested();
                using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
                using (var start = new ManualResetEvent(false))
                using (var done = new CountdownEvent(jobs.Count))
                {
                    Exception failure = null; object failureGate = new object(); int launched = 0;
                    try
                    {
                        foreach (Job nextJob in jobs)
                        {
                            Job job = nextJob;
                            var worker = new Thread(delegate()
                            {
                                try
                                {
                                    start.WaitOne(); cancel.Token.ThrowIfCancellationRequested();
                                    long began = Stopwatch.GetTimestamp();
                                    DiagnosticLog.Write("client.audio.stripe.play_begin", "audioTransfer", transfer, "endpoint", job.Output,
                                        "first_index", job.Index, "packets", job.Block.Packets, "samples", job.Block.Samples.Length, "modem", AudioModemCatalog.GetId(modem), "broadcast", broadcast != null);
                                    services.Play(job.Player, job.Block.Samples, cancel.Token);
                                    DiagnosticLog.Write("client.audio.stripe.play_end", "audioTransfer", transfer, "endpoint", job.Output,
                                        "first_index", job.Index, "packets", job.Block.Packets,
                                        "play_ms", (Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency);
                                }
                                catch (Exception error)
                                {
                                    lock (failureGate) { if (failure == null) failure = error; }
                                    cancel.Cancel();
                                }
                                finally { done.Signal(); }
                            }) { IsBackground = true, Name = "Get Copy-Paste Back client verified audio route" };
                            worker.Start(); launched++;
                        }
                    }
                    catch (Exception error)
                    {
                        lock (failureGate) { if (failure == null) failure = error; }
                        cancel.Cancel();
                        for (int index = launched; index < jobs.Count; index++) done.Signal();
                    }
                    finally { start.Set(); }
                    // Playback cancellation stops every route before any player
                    // or synchronization object can be disposed/reused.
                    done.Wait(); token.ThrowIfCancellationRequested();
                    if (failure != null) throw new InvalidOperationException("Verified audio wave failed.", failure);
                }
                return next - firstIndex;
            }
            finally
            {
                foreach (Job job in jobs)
                    if (job.Owned && job.Player != null)
                    {
                        try { services.DisposePlayer(job.Player); }
                        catch (Exception error) { DiagnosticLog.Error("client.audio.stripe.close_failed", error, "endpoint", job.Output); }
                    }
            }
        }
        private sealed class Job
        {
            internal string Output;
            internal int Index;
            internal AudioEncodedBlock Block;
            internal AudioPlayer Player;
            internal bool Owned;
        }
    }
}
