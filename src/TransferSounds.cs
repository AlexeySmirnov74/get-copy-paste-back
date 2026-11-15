using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Text;
using System.Threading;

namespace HorizonClient
{
    internal enum TransferSoundKind { Accepted, Delivered }

    // Local notifications only: these tones never use the modem's output queue.
    // One bounded worker plays WAVs from memory on the Windows default output.
    internal static class TransferSounds
    {
        private const int SampleRate = 22050;
        private const int MaximumQueuedSounds = 4;
        private static readonly object Gate = new object();
        private static Worker current;
        private static bool stopped;

        internal static void Accepted() { Enqueue(TransferSoundKind.Accepted); }
        internal static void Delivered() { Enqueue(TransferSoundKind.Delivered); }

        // Does not wait on playback or block the UI. A playing cue is at most
        // 200 ms long; its background worker then releases all audio resources.
        internal static void Shutdown()
        {
            try
            {
                lock (Gate)
                {
                    stopped = true;
                    if (current == null) return;
                    current.Stopping = true;
                    current.Pending.Clear();
                    current.Wake.Set();
                }
            }
            catch (Exception error) { LogError("server.sound.shutdown.failed", error); }
        }

        private static void Enqueue(TransferSoundKind kind)
        {
            try
            {
                lock (Gate)
                {
                    if (stopped) return;
                    if (current == null)
                    {
                        Worker worker = new Worker();
                        try
                        {
                            current = worker;
                            new Thread(worker.Run) { IsBackground = true, Name = "Get Copy-Paste Back server notification sounds" }.Start();
                        }
                        catch
                        {
                            current = null;
                            worker.Wake.Dispose();
                            throw;
                        }
                    }
                    // Rapid repeated copies must not leave a long sound backlog.
                    if (current.Pending.Count >= MaximumQueuedSounds) current.Pending.Dequeue();
                    current.Pending.Enqueue(kind);
                    current.Wake.Set();
                }
                DiagnosticLog.Write("server.sound.queued", "kind", kind,
                    "first_hz", kind == TransferSoundKind.Delivered ? 1800 : 600,
                    "second_hz", kind == TransferSoundKind.Delivered ? 2400 : 0,
                    "duration_ms", kind == TransferSoundKind.Delivered ? 200 : 80);
            }
            catch (Exception error) { LogError("server.sound.queue.failed", error); }
        }

        private static void LogError(string name, Exception error)
        {
            try { DiagnosticLog.Error(name, error); }
            catch { } // A notification must never interrupt clipboard handling.
        }

        // Pure synthesis used by tests; does not enumerate or open audio devices.
        internal static byte[] BuildWave(TransferSoundKind kind)
        {
            int samples;
            if (kind == TransferSoundKind.Accepted) samples = Milliseconds(80);
            else if (kind == TransferSoundKind.Delivered) samples = Milliseconds(90) * 2 + Milliseconds(20);
            else throw new ArgumentOutOfRangeException("kind");

            short[] pcm = new short[samples];
            if (kind == TransferSoundKind.Accepted) Tone(pcm, 0, Milliseconds(80), 600);
            else
            {
                Tone(pcm, 0, Milliseconds(90), 1800);
                Tone(pcm, Milliseconds(90) + Milliseconds(20), Milliseconds(90), 2400);
            }

            using (MemoryStream stream = new MemoryStream(44 + samples * 2))
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + samples * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1); // PCM
                writer.Write((short)1); // Mono
                writer.Write(SampleRate);
                writer.Write(SampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(samples * 2);
                foreach (short sample in pcm) writer.Write(sample);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static int Milliseconds(int value) { return (SampleRate * value + 500) / 1000; }

        private static void Tone(short[] pcm, int offset, int length, double frequency)
        {
            int fade = Milliseconds(5);
            for (int index = 0; index < length; index++)
            {
                double envelope = Math.Min(1.0, Math.Min(index, length - 1 - index) / (double)fade);
                pcm[offset + index] = (short)Math.Round(32767 * 0.12 * envelope * Math.Sin(2 * Math.PI * frequency * index / SampleRate));
            }
        }

        private sealed class Worker
        {
            internal readonly Queue<TransferSoundKind> Pending = new Queue<TransferSoundKind>();
            internal readonly AutoResetEvent Wake = new AutoResetEvent(false);
            internal bool Stopping;

            internal void Run()
            {
                try
                {
                    for (;;)
                    {
                        TransferSoundKind kind = TransferSoundKind.Accepted;
                        bool available;
                        lock (Gate)
                        {
                            if (Stopping) break;
                            available = Pending.Count != 0;
                            if (available) kind = Pending.Dequeue();
                        }
                        if (!available) { Wake.WaitOne(); continue; }
                        try
                        {
                            using (MemoryStream stream = new MemoryStream(BuildWave(kind), false))
                            using (SoundPlayer player = new SoundPlayer(stream))
                            {
                                player.Load();
                                lock (Gate) { if (Stopping) break; }
                                DiagnosticLog.Write("server.sound.playing", "kind", kind);
                                player.PlaySync();
                                DiagnosticLog.Write("server.sound.played", "kind", kind);
                            }
                        }
                        catch (Exception error) { LogError("server.sound.playback.failed", error); }
                    }
                }
                catch (Exception error) { LogError("server.sound.worker.failed", error); }
                finally
                {
                    lock (Gate) { if (current == this) current = null; }
                    Wake.Dispose();
                }
            }
        }
    }
}
