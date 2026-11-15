using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

internal static class AudioOutputProbeTests
{
    private static int checks;
    [STAThread]
    private static int Main()
    {
        try
        {
            DisabledProbeAndSelectedModem();
            DeferredOutputEnumeration();
            LatestOutputRefreshWinsWithoutCancellingProbe();
            CameraRetryPreservesProbe();
            NewProbeCancelsOldAndSerializes();
            ExplicitChangesCancelProbe();
            ProbeSerializesWithSessionReports();
            MissingAutomaticOutputNeverUsesHardware();
            NotificationAndPlaybackDiagnostics();
            Console.WriteLine("Audio output probe checks passed: " + checks + ". Synthetic endpoints and PCM playback only; disabled probes never read the clipboard, install a listener or require a camera/session.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void DisabledProbeAndSelectedModem()
    {
        foreach (AudioModemKind kind in new[] { AudioModemKind.Fsk, AudioModemKind.Quiet, AudioModemKind.Aicodix })
            using (var fixture = new Fixture())
            {
                fixture.Sender.SetOutput("waveout:0"); fixture.Sender.SetModem(kind);
                Guid probeId = fixture.Sender.TestOutput();
                Wait(delegate { return fixture.Played().Length == 3; }, "An explicit disabled probe completes three cycles.");
                foreach (Playback played in fixture.Played())
                {
                    AudioDiagnosticProbe probe;
                    Check(played.Kind == kind && played.Output == "waveout:0" && played.Packet.TransferId == probeId &&
                        AudioDiagnosticProbe.TryDecode(played.Packet.Data, out probe) && probe.Id == probeId && probe.Modem == kind &&
                        Math.Abs(DateTime.UtcNow.Ticks - probe.SentUtcTicks) < TimeSpan.TicksPerMinute,
                        "All cycles retain selected modem/output, exact 40-byte probe and its stable outer identity.");
                }
                fixture.Sender.NotifyClipboardChanged(); fixture.Sender.SendMessage(new byte[84], Guid.NewGuid());
                Check(fixture.SequenceReads == 0 && fixture.Captures == 0 && fixture.ListenerAdds == 0 && !fixture.Owner.IsHandleCreated,
                    "Probe does not create a fake session, clipboard listener, clipboard read or dispatch window.");
            }
    }

    private static void DeferredOutputEnumeration()
    {
        using (var fixture = new Fixture())
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            fixture.BeforeOutputs = delegate { entered.Set(); if (!release.WaitOne(3000)) throw new Exception("Output query was not released."); };
            Stopwatch elapsed = Stopwatch.StartNew(); Guid probe = fixture.Sender.TestOutput(); elapsed.Stop();
            Check(probe != Guid.Empty && elapsed.ElapsedMilliseconds < 500, "TestOutput returns without waiting for output enumeration or encoding.");
            Check(entered.WaitOne(1000), "The sender worker performs the deferred endpoint enumeration.");
            Check(fixture.Played().Length == 0, "A blocked endpoint query performs no premature playback.");
            release.Set(); Wait(delegate { return fixture.Played().Length == 3; }, "The worker resumes after the endpoint query completes.");
        }
    }

    private static void CameraRetryPreservesProbe()
    {
        using (var fixture = new Fixture())
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            fixture.BeforePlay = delegate(Packet packet, CancellationToken token)
            { entered.Set(); WaitRelease(release, token); };
            Guid probe = fixture.Sender.TestOutput(); Check(entered.WaitOne(1000), "The independent probe begins before a simulated camera retry.");
            fixture.Sender.DisableSession(); fixture.Sender.DisableSession();
            Check(fixture.Cancelled == 0, "Ending a failed camera session preserves the explicitly requested probe.");
            release.Set(); Wait(delegate { return fixture.Played().Length == 3; }, "The probe completes despite repeated camera/session failures.");
            Check(fixture.SequenceReads == 0 && fixture.ListenerAdds == 0 && fixture.Played()[0].Packet.TransferId == probe,
                "Camera-independent probing never initializes a clipboard session.");
        }
    }

    private static void LatestOutputRefreshWinsWithoutCancellingProbe()
    {
        using (var fixture = new Fixture())
        using (var olderEntered = new ManualResetEvent(false))
        using (var newerEntered = new ManualResetEvent(false))
        using (var olderRelease = new ManualResetEvent(false))
        using (var newerRelease = new ManualResetEvent(false))
        {
            fixture.Sender.SetOutput(null);
            fixture.BeforeCreatePlayer = delegate(string id)
            {
                ManualResetEvent entered = id == "waveout:1" ? olderEntered : newerEntered;
                ManualResetEvent release = id == "waveout:1" ? olderRelease : newerRelease;
                entered.Set(); if (!release.WaitOne(3000)) throw new Exception("Output refresh was not released.");
            };
            fixture.Sender.TestOutput(); Check(olderEntered.WaitOne(1000), "The older worker refresh pauses after resolving the original output.");
            fixture.Outputs = new[] { new AudioEndpoint("waveout:2", "VMware Virtual Audio new endpoint") };
            Exception refreshError = null;
            Thread refresh = new Thread(delegate()
            { try { fixture.Sender.SetOutput(null); } catch (Exception error) { refreshError = error; } }) { IsBackground = true };
            try
            {
                refresh.Start(); Check(newerEntered.WaitOne(1000), "A newer refresh resolves the hotplug output using the same automatic preference.");
                olderRelease.Set();
                Wait(delegate { return fixture.PlayerDisposals == 1; }, "The older snapshot is discarded even before the newer player is committed.");
                Check(fixture.Played().Length == 0, "An obsolete refresh neither starts old-device playback nor declares the independent probe unavailable.");
                newerRelease.Set(); Check(refresh.Join(1000) && refreshError == null, "The newer endpoint refresh completes successfully.");
                Wait(delegate { return fixture.Played().Length == 3; }, "The original independent probe survives the stale refresh and completes on the newer endpoint.");
                foreach (Playback played in fixture.Played()) Check(played.Output == "waveout:2", "A late old-device result cannot overwrite the latest output snapshot.");
                Check(fixture.MaximumConcurrent == 1, "Concurrent enumeration still uses a single serialized playback stream.");
            }
            finally { olderRelease.Set(); newerRelease.Set(); if (refresh.IsAlive) refresh.Join(1000); }
        }
    }

    private static void NewProbeCancelsOldAndSerializes()
    {
        using (var fixture = new Fixture())
        using (var entered = new ManualResetEvent(false))
        {
            int calls = 0;
            fixture.BeforePlay = delegate(Packet packet, CancellationToken token)
            { if (Interlocked.Increment(ref calls) == 1) { entered.Set(); token.WaitHandle.WaitOne(3000); token.ThrowIfCancellationRequested(); } };
            Guid older = fixture.Sender.TestOutput(); Check(entered.WaitOne(1000), "The first probe reaches the serialized playback worker.");
            Guid newer = fixture.Sender.TestOutput();
            Wait(delegate { return fixture.Played().Length == 3; }, "The newer explicit probe cancels and replaces an older one.");
            foreach (Playback played in fixture.Played()) Check(played.Packet.TransferId == newer && newer != older, "Only the current probe completes playback.");
            Check(fixture.Cancelled == 1 && fixture.MaximumConcurrent == 1, "Cancellation is bounded and probes never open parallel playback streams.");
        }
    }

    private static void ExplicitChangesCancelProbe()
    {
        foreach (string action in new[] { "modem", "output", "disable", "dispose" })
            using (var fixture = new Fixture())
            using (var entered = new ManualResetEvent(false))
            {
                fixture.BeforePlay = delegate(Packet packet, CancellationToken token)
                { entered.Set(); token.WaitHandle.WaitOne(3000); token.ThrowIfCancellationRequested(); };
                fixture.Sender.TestOutput(); Check(entered.WaitOne(1000), "The probe begins before an explicit cancellation action.");
                if (action == "modem") fixture.Sender.SetModem(AudioModemKind.Quiet);
                else if (action == "output") fixture.Sender.SetOutput("waveout:0");
                else if (action == "disable") fixture.Sender.Disable();
                else fixture.Sender.Dispose();
                Wait(delegate { return fixture.Cancelled == 1; }, "Changing " + action + " cancels the current probe.");
                Check(fixture.Played().Length == 0 && fixture.SequenceReads == 0 && fixture.ListenerAdds == 0,
                    "Cancelled probe does not restart itself or initialize a clipboard session.");
            }
    }

    private static void ProbeSerializesWithSessionReports()
    {
        using (var fixture = new Fixture())
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            int calls = 0;
            fixture.Sender.Enable(Guid.NewGuid());
            fixture.BeforePlay = delegate(Packet packet, CancellationToken token)
            { if (Interlocked.Increment(ref calls) == 1) { entered.Set(); WaitRelease(release, token); } };
            Guid probe = fixture.Sender.TestOutput(); Check(entered.WaitOne(1000), "The probe starts within an active bridge session.");
            Guid report = Guid.NewGuid(); fixture.Sender.SendMessage(new byte[84], report);
            Check(fixture.Cancelled == 0, "A queued session report does not abort an explicit diagnostic.");
            release.Set(); Wait(delegate { return fixture.Played().Length == 6; }, "The probe and queued report each complete all three cycles.");
            Playback[] played = fixture.Played();
            for (int i = 0; i < played.Length; i++) Check(played[i].Packet.TransferId == (i < 3 ? probe : report), "Probe cycles and normal reports use the same serialized output.");
            Check(fixture.MaximumConcurrent == 1 && fixture.ListenerAdds == 1 && fixture.Captures == 0,
                "Probing preserves the existing listener and opens no parallel audio stream or clipboard capture.");
        }
    }

    private static void MissingAutomaticOutputNeverUsesHardware()
    {
        using (var fixture = new Fixture())
        {
            fixture.Outputs = new[] { new AudioEndpoint("waveout:0", "Physical speakers") };
            fixture.Sender.TestOutput(); Wait(delegate { return fixture.OutputQueries > 0; }, "Automatic probing enumerates available outputs.");
            Thread.Sleep(40);
            Check(fixture.Played().Length == 0 && fixture.Players == 0, "Automatic probing never falls back to physical hardware when no Horizon output exists.");
            Check(fixture.SequenceReads == 0 && fixture.ListenerAdds == 0, "Missing audio output does not initialize clipboard state.");
        }
    }

    private static void NotificationAndPlaybackDiagnostics()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio-probe-test-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            Check(DiagnosticLog.Initialize(path), "Diagnostics use the explicitly isolated test destination.");
            using (var fixture = new Fixture())
            {
                fixture.Sender.SetModem(AudioModemKind.Quiet); fixture.Sender.NotifyClipboardChanged();
                Guid probe = fixture.Sender.TestOutput();
                Wait(delegate { return fixture.Played().Length == 3; }, "A probe exercises actual enqueue, encode and playback log events.");
                Check(DiagnosticLog.Shutdown(5000), "Probe diagnostic events drain within a bounded wait.");
                string log = File.ReadAllText(path);
                Check(log.Contains("client.audio.probe.requested") && log.Contains("client.audio.probe.priority_queued") && log.Contains("client.audio.probe.encoded") &&
                    log.Contains("client.audio.probe.play_begin") && log.Contains("client.audio.probe.play_end") && log.Contains(probe.ToString()),
                    "Logs distinguish the explicit request, queued probe, successful encoding and actual playback.");
                Check(log.Contains("client.audio.clipboard_notification_skipped") && log.Contains("no_video_session") &&
                    log.Contains("\"modem\":\"quiet\""), "Logs identify skipped clipboard notifications and the selected queued modem without fake FSK metadata.");
            }
        }
        finally { DiagnosticLog.Shutdown(5000); if (File.Exists(path)) File.Delete(path); }
    }

    private static void WaitRelease(ManualResetEvent release, CancellationToken token)
    {
        if (WaitHandle.WaitAny(new[] { release, token.WaitHandle }, 3000) == WaitHandle.WaitTimeout) throw new Exception("Synthetic playback was not released.");
        token.ThrowIfCancellationRequested();
    }
    private sealed class Playback { internal Packet Packet; internal AudioModemKind Kind; internal string Output; }
    private sealed class Fixture : IDisposable
    {
        internal readonly Control Owner = new Control();
        internal readonly AudioClipboardSender Sender;
        internal AudioEndpoint[] Outputs = { new AudioEndpoint("waveout:0", "Physical speakers"), new AudioEndpoint("waveout:1", "VMware Virtual Audio (DevTap)") };
        internal Action BeforeOutputs;
        internal Action<string> BeforeCreatePlayer;
        internal Action<Packet, CancellationToken> BeforePlay;
        private readonly object gate = new object();
        private readonly List<Playback> played = new List<Playback>();
        private readonly Dictionary<AudioPlayer, string> players = new Dictionary<AudioPlayer, string>();
        private readonly Dictionary<float[], AudioModemKind> kinds = new Dictionary<float[], AudioModemKind>();
        private readonly List<Exception> errors = new List<Exception>();
        private int sequenceReads, captures, listenerAdds, outputQueries, cancelled, concurrent, maximumConcurrent, playerDisposals;
        internal Fixture()
        {
            var services = new AudioSenderServices();
            services.ValidateModem = delegate { };
            services.Sequence = delegate { Interlocked.Increment(ref sequenceReads); return 1; };
            services.Capture = delegate { Interlocked.Increment(ref captures); throw new Exception("A probe must not capture clipboard data."); };
            services.AddListener = delegate { Interlocked.Increment(ref listenerAdds); return true; };
            services.RemoveListener = delegate { return true; };
            services.Outputs = delegate { Interlocked.Increment(ref outputQueries); Action before = BeforeOutputs; if (before != null) before(); return Outputs; };
            services.CreatePlayer = delegate(string id) { Action<string> before = BeforeCreatePlayer; if (before != null) before(id); var player = new AudioPlayer(id); lock (gate) players.Add(player, id); return player; };
            services.DisposePlayer = delegate(AudioPlayer player) { player.Dispose(); Interlocked.Increment(ref playerDisposals); };
            services.EncodeAudio = delegate(AudioModemKind kind, Guid transfer, int index, byte[] data)
            {
                AudioEncodedBlock block = AudioTransmissionEncoder.Encode(AudioModemKind.Fsk, transfer, index, data);
                lock (gate) kinds[block.Samples] = kind; return block;
            };
            services.Play = delegate(AudioPlayer player, float[] samples, CancellationToken token)
            {
                try
                {
                    lock (gate) { concurrent++; maximumConcurrent = Math.Max(maximumConcurrent, concurrent); }
                    token.ThrowIfCancellationRequested(); Packet decoded = null;
                    var decoder = new AudioDecoder(); decoder.PacketReceived += delegate(Packet packet) { decoded = packet; }; decoder.Feed(samples);
                    if (decoded == null) throw new Exception("Synthetic playback waveform did not decode.");
                    Action<Packet, CancellationToken> before = BeforePlay; if (before != null) before(decoded, token);
                    token.ThrowIfCancellationRequested();
                    lock (gate) played.Add(new Playback { Packet = decoded, Kind = kinds[samples], Output = players[player] });
                }
                catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
                finally { lock (gate) concurrent--; }
            };
            Sender = new AudioClipboardSender(Owner, services);
            Sender.Error = delegate(Exception error) { lock (gate) errors.Add(error); };
        }
        internal int SequenceReads { get { return Volatile.Read(ref sequenceReads); } }
        internal int Captures { get { return Volatile.Read(ref captures); } }
        internal int ListenerAdds { get { return Volatile.Read(ref listenerAdds); } }
        internal int OutputQueries { get { return Volatile.Read(ref outputQueries); } }
        internal int Cancelled { get { return Volatile.Read(ref cancelled); } }
        internal int PlayerDisposals { get { return Volatile.Read(ref playerDisposals); } }
        internal int MaximumConcurrent { get { lock (gate) return maximumConcurrent; } }
        internal int Players { get { lock (gate) return players.Count; } }
        internal Playback[] Played() { lock (gate) return played.ToArray(); }
        public void Dispose()
        {
            Sender.Dispose(); Owner.Dispose();
            lock (gate) { if (errors.Count != 0) throw new Exception("Unexpected synthetic sender error.", errors[0]); }
        }
    }
    private static void Wait(Func<bool> condition, string message)
    { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.ElapsedMilliseconds < 4000) { Application.DoEvents(); Thread.Sleep(1); } Check(condition(), message); }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
