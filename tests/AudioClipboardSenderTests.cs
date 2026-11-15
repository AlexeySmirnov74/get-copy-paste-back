using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

internal static class AudioClipboardSenderTests
{
    private static int checks;

    [STAThread]
    private static int Main()
    {
        try
        {
            EndpointsAndLazyConstruction();
            CaptureSuppressAndAcknowledge();
            LatestCopyWinsDuringEncoding();
            PriorityAndOutputChanges();
            BenchmarkModeLifecycle();
            BenchmarkInvalidatesPendingEncoder();
            ModemSelectionAndFailedValidation();
            ModemChangesCancelAndResumePlayback();
            ModemChangesWhileBenchmarkPaused();
            ObsoleteModemEncoderCannotDiscardPendingTransfer();
            DiagnosticPriorityReportMetadata();
            RepeatedReceiptLetsReverseFrameFinish();
            RepeatedReceiptLetsNativeFallbackFinish();
            NewReceiptIdentityStillPreempts();
            RepeatedReceiptWakesSilentAckGrace();
            FastReceiptPreemptsBulk();
            LowerPriorityCannotInterruptFastReceipt();
            ReceiptPreemptionRetainsReversePacketPosition();
            LowerPriorityCannotInterruptNativeReceipt();
            BoundedClipboardPasses();
            PreemptionPreservesCompletedPasses();
            RetryPolicyCanExtendSilentAckGrace();
            RouteCalibrationSuspendsEveryPriority();
            VerifiedRoutesStripeAndResumeCancelledWave();
            EmptyVerifiedRoutesMuteWithoutFallback();
            TransferHeartbeatsPrecedeSingleAndParallelData();
            TransferHeartbeatPassIdentity();
            StaleVerifiedRouteSessionCannotMutateNewSession();
            StaleVerifiedRouteCommandCannotMutateSameSession();
            Console.WriteLine("Audio clipboard sender checks passed: " + checks + ". Synthetic clipboard/audio services only; no real clipboard listener, microphone, or speaker was opened.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void EndpointsAndLazyConstruction()
    {
        Check(AudioClipboardSender.IsHorizonOutput("Speakers (VMware Virtual Audio)"), "VMware virtual render endpoints must be recognized.");
        Check(AudioClipboardSender.IsHorizonOutput("VMware DevTap"), "DevTap render endpoints must be recognized.");
        Check(AudioClipboardSender.IsHorizonOutput("Omnissa Virtual Audio") && AudioClipboardSender.IsHorizonOutput("Horizon Audio"), "Omnissa/Horizon render endpoints must be recognized.");
        Check(!AudioClipboardSender.IsHorizonOutput("Speakers (Realtek HD Audio)") && !AudioClipboardSender.IsHorizonOutput("VMware microphone"), "Automatic selection must not choose ordinary hardware.");
        using (Fixture fixture = new Fixture())
        {
            fixture.Sender.SetOutput("waveout:0");
            fixture.Sender.NotifyClipboardChanged();
            fixture.Sender.SuppressCurrentClipboard();
            fixture.Sender.SendMessage(new byte[] { 1 }, Guid.NewGuid());
            Check(fixture.OutputQueries == 0 && fixture.SequenceReads == 0 && fixture.ListenerAdds == 0 && !fixture.Owner.IsHandleCreated,
                "Construction and inactive calls must not access clipboard or audio.");
            fixture.Sender.Enable(fixture.Session);
            Pump(240);
            Check(fixture.Captures == 0 && fixture.Played().Length == 0, "Enable must not send pre-existing clipboard contents.");
            fixture.Sequence = 2;
            fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Played().Length > 0; }, "Explicit output must receive the first new copy.");
            Check(fixture.Played()[0].Output == "waveout:0", "Explicit selection must be honored.");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Outputs = new[] { new AudioEndpoint("waveout:0", "Speakers (Realtek HD Audio)") };
            fixture.Sender.Enable(fixture.Session);
            fixture.Sequence = 2;
            fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Encodes > 0; }, "Capture must still prepare a pending copy when no virtual render endpoint exists.");
            Pump(80);
            Check(fixture.Played().Length == 0, "Automatic selection must never fall back to a hardware/default output.");
        }
    }

    private static void CaptureSuppressAndAcknowledge()
    {
        using (Fixture fixture = new Fixture())
        using (var decoding = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            int decodedPackets = 0;
            CancellationToken delayedToken = CancellationToken.None;
            fixture.BeforePacketPublished = delegate(Packet packet, CancellationToken token)
            {
                int decoded = ++decodedPackets;
                if (decoded != 3 && decoded != 5) return;
                delayedToken = token;
                decoding.Set();
                if (!release.WaitOne(4000)) throw new TimeoutException("The in-flight synthetic packet was not released.");
            };
            try
            {
                fixture.Sender.Enable(fixture.Session);
                fixture.Sequence = 2;
                fixture.Sender.NotifyClipboardChanged();
                fixture.Sender.NotifyClipboardChanged();
                Until(delegate { return fixture.Played().Length >= 2; }, "A new clipboard must be repeated until acknowledged.");
                Played first = fixture.Played()[0];
                byte[] encoded;
                Check(BridgeControl.TryClipboard(first.Packet.Data, fixture.Session, out encoded) && BitConverter.ToInt32(encoded, 0) == 2,
                    "Normal clipboard data must carry the current session envelope.");
                Check(fixture.Captures == 1, "Duplicate notifications must be debounced.");
                Until(delegate { return decoding.WaitOne(0); }, "A third packet can already be decoding when acknowledgement arrives.");
                int started = fixture.PlayStarts;
                fixture.Sender.Acknowledge(first.Packet.TransferId);
                Check(delayedToken.IsCancellationRequested, "Acknowledgement must cancel the already-started playback while its decoder is blocked.");
                // A decoder admitted before cancellation can still publish its
                // packet. Wait for sender retirement before measuring silence.
                release.Set();
                Until(delegate { return fixture.PlaybackIdle; }, "Acknowledged playback must retire after the in-flight decoder returns.");
                int stopped = fixture.Played().Length;
                Check(stopped == 3 && fixture.PlayStarts == started && fixture.ListenerAdds == 1 && fixture.ListenerRemoves == 0,
                    "Acknowledgement permits the in-flight packet to finish but starts no further playback and retains the listener.");

                fixture.Sequence = 3;
                fixture.Sender.SuppressCurrentClipboard();
                fixture.Sender.NotifyClipboardChanged();
                Pump(240);
                Check(fixture.Captures == 1 && fixture.Played().Length == stopped && fixture.PlayStarts == started,
                    "A delayed notification for a remote publish must not echo it back.");

                decoding.Reset();
                release.Reset();
                fixture.Sequence = 4;
                fixture.Sender.NotifyClipboardChanged();
                Until(delegate { return fixture.Played().Length > stopped; }, "A copy after suppression must still transmit.");
                Guid current = fixture.Played()[stopped].Packet.TransferId;
                Until(delegate { return decoding.WaitOne(0); }, "The newer copy must have an in-flight packet before testing a stale acknowledgement.");
                fixture.Sender.Acknowledge(first.Packet.TransferId);
                Check(!delayedToken.IsCancellationRequested, "An old acknowledgement must leave the newer copy's active playback uncancelled.");
                int beforeOldAck = fixture.Played().Length;
                release.Set();
                Until(delegate { return fixture.Played().Length > beforeOldAck; }, "An old acknowledgement must not cancel the current copy.");
                fixture.Sender.Acknowledge(current);
            }
            finally
            {
                release.Set();
                fixture.Sender.Disable();
                Until(delegate { return fixture.PlaybackIdle; }, "Synthetic playback must retire before disposing its synchronization events.");
            }
        }
    }

    private static void LatestCopyWinsDuringEncoding()
    {
        using (Fixture fixture = new Fixture())
        using (ManualResetEvent encoderStarted = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            fixture.Encode = delegate(ClipboardSnapshot snapshot)
            {
                byte[] data = snapshot.Formats["fixture"];
                if (BitConverter.ToInt32(data, 0) == 2) { encoderStarted.Set(); release.WaitOne(3000); }
                return data;
            };
            fixture.Sender.Enable(fixture.Session);
            fixture.Sequence = 2;
            fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return encoderStarted.WaitOne(0); }, "The first encoder must start.");
            fixture.Sequence = 3;
            fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Captures >= 2; }, "A later copy must replace the pending snapshot while encoding is busy.");
            release.Set();
            Until(delegate { return fixture.Played().Length > 0; }, "The newest encoded copy must be sent.");
            byte[] bytes;
            foreach (Played played in fixture.Played())
                Check(BridgeControl.TryClipboard(played.Packet.Data, fixture.Session, out bytes) && BitConverter.ToInt32(bytes, 0) == 3,
                    "An obsolete encoder must not transmit a previous clipboard.");
        }
        using (Fixture fixture = new Fixture())
        using (ManualResetEvent encoderStarted = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            fixture.Encode = delegate(ClipboardSnapshot snapshot) { encoderStarted.Set(); release.WaitOne(3000); return snapshot.Formats["fixture"]; };
            fixture.Sender.Enable(fixture.Session);
            fixture.Sequence = 2;
            fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return encoderStarted.WaitOne(0); }, "The encoder must start before a remote publish.");
            fixture.Sequence = 3;
            fixture.Sender.SuppressCurrentClipboard();
            fixture.Sender.NotifyClipboardChanged();
            release.Set();
            Pump(280);
            Check(fixture.Played().Length == 0, "Suppressing a remote publish must invalidate an already-running encoder.");
        }
    }

    private static void PriorityAndOutputChanges()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Sender.Enable(fixture.Session);
            byte[] report = new byte[600];
            new Random(531).NextBytes(report);
            Guid reportId = Guid.NewGuid();
            fixture.Sender.SendMessage(report, reportId);
            Until(delegate { return fixture.Played().Length >= 9; }, "Priority control messages must send three complete cycles.");
            Pump(100);
            Played[] played = fixture.Played();
            Check(played.Length == 9, "Priority messages must stop after exactly three packet cycles.");
            TransferAssembler assembler = new TransferAssembler();
            byte[] complete = null;
            foreach (Played item in played)
            {
                Check(item.Packet.TransferId == reportId && item.Packet.Count == 3 && item.Packet.TotalLength == 600,
                    "Priority messages must preserve caller ID and bounded packet metadata.");
                complete = assembler.Add(item.Packet) ?? complete;
            }
            Check(complete != null && complete.Length == report.Length, "Multi-packet priority messages must reconstruct.");
            for (int index = 0; index < report.Length; index++) if (report[index] != complete[index]) throw new Exception("Priority messages must not receive a clipboard envelope.");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Sender.Enable(fixture.Session);
            fixture.Sequence = 2;
            fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Played().Length > 0; }, "Automatic virtual output must begin sending.");
            Check(fixture.Played()[0].Output == "waveout:1", "Automatic output must select the Horizon virtual endpoint.");
            int playersBeforeRefresh = fixture.PlayerCount;
            fixture.Sender.SetOutput(null);
            Check(fixture.PlayerCount == playersBeforeRefresh, "Refreshing an unchanged automatic selection must retain its player.");
            fixture.Sender.SetOutput("waveout:0");
            Until(delegate { return Array.Exists(fixture.Played(), delegate(Played item) { return item.Output == "waveout:0"; }); }, "Output changes must restart pending playback on the explicit output.");
            fixture.Sender.Disable();
            Until(delegate { return fixture.PlaybackIdle; }, "Disabled playback must retire before measuring silence.");
            int stopped = fixture.Played().Length;
            Pump(60);
            Check(fixture.Played().Length == stopped && fixture.ListenerRemoves == 1, "Disable must cancel playback and unsubscribe.");
        }
    }

    private static void BenchmarkModeLifecycle()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Sender.Enable(fixture.Session); fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Played().Length > 0; }, "A pending clipboard starts before benchmark mode.");
            fixture.Sender.SetBenchmarkMode(true); fixture.Sender.SetBenchmarkMode(true);
            Until(delegate { return fixture.PlaybackIdle; }, "Benchmark pause must retire admitted clipboard playback before measuring silence.");
            int captures = fixture.Captures, reads = fixture.SequenceReads, stopped = fixture.Played().Length, players = fixture.PlayerCount;
            fixture.Sequence = 3; fixture.Sender.NotifyClipboardChanged(); Pump(240);
            Check(fixture.Captures == captures && fixture.SequenceReads == reads && fixture.Played().Length == stopped,
                "Benchmark mode ignores clipboard notifications without even reading a sequence or capturing user data.");
            Guid reportId = Guid.NewGuid(); fixture.Sender.SendMessage(new byte[84], reportId);
            Until(delegate { return Array.FindAll(fixture.Played(), delegate(Played item) { return item.Packet.TransferId == reportId; }).Length == 3; },
                "Short priority reports still complete all three audio cycles while clipboard is paused.");
            fixture.Sender.SetBenchmarkMode(false); fixture.Sender.SetBenchmarkMode(false); fixture.Sender.NotifyClipboardChanged(); Pump(240);
            Check(fixture.Captures == captures && fixture.PlayerCount == players && fixture.ListenerAdds == 1 && fixture.ListenerRemoves == 0,
                "Resume leaves copies made during pause unsent and retains the existing session, listener and endpoint.");
            int before = fixture.Played().Length; fixture.Sequence = 4; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Played().Length > before; }, "Only a new copy after resume starts clipboard playback.");
            byte[] encoded;
            Check(BridgeControl.TryClipboard(fixture.Played()[before].Packet.Data, fixture.Session, out encoded) && BitConverter.ToInt32(encoded, 0) == 4,
                "Resumed clipboard uses the unchanged session and newest sequence.");
            fixture.Sender.SetBenchmarkMode(true); fixture.Sender.Disable(); fixture.Sender.Enable(fixture.Session);
            before = fixture.Played().Length; fixture.Sequence = 5; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Played().Length > before; }, "Disable clears benchmark mode before a later Enable.");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Sender.Enable(fixture.Session); Guid id = Guid.NewGuid(); fixture.Sender.SendMessage(new byte[84], id);
            Until(delegate { return fixture.Played().Length >= 1; }, "Priority report begins before entering benchmark mode.");
            fixture.Sender.SetBenchmarkMode(true);
            Until(delegate { return fixture.Played().Length == 3; }, "Entering benchmark mode does not cancel an active priority report.");
            Until(delegate { return fixture.PlaybackIdle; }, "The priority report must retire before checking its bounded lifetime.");
            Check(fixture.Played().Length == 3, "Priority lifetime remains bounded while paused.");
        }
    }

    private static void BenchmarkInvalidatesPendingEncoder()
    {
        using (Fixture fixture = new Fixture())
        using (var started = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            fixture.Encode = delegate(ClipboardSnapshot snapshot) { started.Set(); release.WaitOne(3000); return snapshot.Formats["fixture"]; };
            fixture.Sender.Enable(fixture.Session); fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return started.WaitOne(0); }, "The synthetic encoder starts before benchmark mode.");
            fixture.Sender.SetBenchmarkMode(true); release.Set(); Pump(240);
            Check(fixture.Played().Length == 0, "An encoder that completes after Prepare cannot enqueue stale clipboard bytes.");
            Guid reportId = Guid.NewGuid(); fixture.Sender.SendMessage(new byte[84], reportId);
            Until(delegate { return fixture.Played().Length == 3; }, "Reports remain available after invalidating a pending clipboard encoder.");
            foreach (Played item in fixture.Played()) Check(item.Packet.TransferId == reportId, "No stale clipboard packet interrupts benchmark reports.");
        }
    }

    private static AudioEncodedBlock SyntheticModem(AudioModemKind kind, Guid transfer, int index, byte[] data)
    {
        // Transport-switch tests retain a genuine decodable waveform while
        // replacing the native codec and recording the requested modem kind.
        return AudioTransmissionEncoder.Encode(AudioModemKind.Fsk, transfer, index, data);
    }

    private static void ModemSelectionAndFailedValidation()
    {
        using (Fixture fixture = new Fixture())
        using (ManualResetEvent playing = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            int validations = 0;
            fixture.ValidateModem = delegate(AudioModemKind kind)
            {
                validations++;
                if (kind == AudioModemKind.Aicodix) throw new InvalidOperationException("Synthetic codec unavailable");
            };
            fixture.EncodeAudio = SyntheticModem;
            Check(fixture.Sender.SelectedModem == AudioModemKind.Fsk, "An unconfigured sender starts with the compatible modem.");
            fixture.Sender.SetModem(AudioModemKind.Quiet);
            fixture.Sender.SetModem(AudioModemKind.Quiet);
            Check(fixture.Sender.SelectedModem == AudioModemKind.Quiet && validations == 1,
                "Selecting a different modem validates once; selecting it again retains the current engine.");
            Check(fixture.OutputQueries == 0 && fixture.SequenceReads == 0 && fixture.Captures == 0 && fixture.ListenerAdds == 0 && !fixture.Owner.IsHandleCreated,
                "Selecting an inactive modem does not access the clipboard, notification handle, listener or output.");
            CancellationToken blockedToken = CancellationToken.None;
            fixture.BeforePlay = delegate(AudioModemKind kind, CancellationToken token)
            {
                blockedToken = token;
                playing.Set();
                if (!release.WaitOne(3000)) throw new TimeoutException("Synthetic playback was not released");
            };
            fixture.Sender.Enable(fixture.Session);
            Pump(220);
            Check(fixture.Captures == 0 && fixture.Played().Length == 0, "Enabling a selected modem does not send a pre-existing clipboard.");
            fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return playing.WaitOne(0); }, "Selected Quiet transport begins the pending synthetic copy.");
            int reads = fixture.SequenceReads, captures = fixture.Captures;
            bool rejected = false;
            try { fixture.Sender.SetModem(AudioModemKind.Aicodix); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && fixture.Sender.SelectedModem == AudioModemKind.Quiet && !blockedToken.IsCancellationRequested,
                "Failed validation retains the active modem and leaves its pending playback uncancelled.");
            Check(fixture.SequenceReads == reads && fixture.Captures == captures, "Failed modem validation does not inspect or recapture clipboard data.");
            release.Set();
            Until(delegate { return fixture.Played().Length >= 2; }, "The same pending copy continues after a rejected modem change.");
            Played[] played = fixture.Played(); Guid id = played[0].Packet.TransferId;
            foreach (Played item in played)
                Check(item.Modem == AudioModemKind.Quiet && item.Packet.TransferId == id, "Rejected selection cannot replace the current pending copy or engine.");
            fixture.Sender.Acknowledge(id);
        }
    }

    private static void ModemChangesCancelAndResumePlayback()
    {
        using (Fixture fixture = new Fixture())
        using (ManualResetEvent playing = new ManualResetEvent(false))
        {
            fixture.ValidateModem = delegate { };
            fixture.EncodeAudio = SyntheticModem;
            int attempts = 0, cancellations = 0;
            fixture.BeforePlay = delegate(AudioModemKind kind, CancellationToken token)
            {
                if (Interlocked.Increment(ref attempts) != 1) return;
                playing.Set();
                if (!token.WaitHandle.WaitOne(3000)) throw new TimeoutException("Old modem playback was not cancelled");
                Interlocked.Increment(ref cancellations);
                token.ThrowIfCancellationRequested();
            };
            fixture.Sender.Enable(fixture.Session); fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return playing.WaitOne(0); }, "Original FSK playback starts before a modem change.");
            int reads = fixture.SequenceReads, captures = fixture.Captures;
            fixture.Sender.SetModem(AudioModemKind.Quiet);
            Until(delegate { return Array.Exists(fixture.Played(), delegate(Played item) { return item.Modem == AudioModemKind.Quiet; }); },
                "Changing to Quiet restarts the existing pending clipboard using the new modem.");
            Check(Volatile.Read(ref cancellations) == 1 && fixture.Sender.SelectedModem == AudioModemKind.Quiet,
                "A modem change cancels the previous waveform before starting its replacement.");
            Check(fixture.SequenceReads == reads && fixture.Captures == captures && captures == 1,
                "Switching a live modem resumes buffered bytes without rereading or recapturing the clipboard.");
            Guid id = fixture.Played()[0].Packet.TransferId;
            fixture.Sender.SetModem(AudioModemKind.Aicodix);
            Until(delegate { return Array.Exists(fixture.Played(), delegate(Played item) { return item.Modem == AudioModemKind.Aicodix; }); },
                "The same pending clipboard can also resume using aicodix.");
            foreach (Played item in fixture.Played()) Check(item.Packet.TransferId == id, "Changing waveform preserves the transfer identity.");
            fixture.Sender.Acknowledge(id);
        }
    }

    private static void ModemChangesWhileBenchmarkPaused()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.ValidateModem = delegate { }; fixture.EncodeAudio = SyntheticModem;
            fixture.Sender.Enable(fixture.Session); fixture.Sender.SetBenchmarkMode(true);
            int reads = fixture.SequenceReads;
            fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            fixture.Sender.SetModem(AudioModemKind.Quiet);
            Guid quietReport = Guid.NewGuid(); fixture.Sender.SendMessage(new byte[84], quietReport);
            Until(delegate { return Array.FindAll(fixture.Played(), delegate(Played item) { return item.Packet.TransferId == quietReport; }).Length == 3; },
                "Quiet sends complete priority report cycles while ordinary clipboard transmission is paused.");
            fixture.Sender.SetModem(AudioModemKind.Aicodix);
            Guid aicodixReport = Guid.NewGuid(); fixture.Sender.SendMessage(new byte[84], aicodixReport);
            Until(delegate { return Array.FindAll(fixture.Played(), delegate(Played item) { return item.Packet.TransferId == aicodixReport; }).Length == 3; },
                "aicodix sends complete priority report cycles while clipboard transmission remains paused.");
            Check(fixture.Captures == 0 && fixture.SequenceReads == reads,
                "Choosing either accelerated modem while paused does not read copied user data.");
            foreach (Played item in fixture.Played())
                Check(item.Modem == (item.Packet.TransferId == quietReport ? AudioModemKind.Quiet : AudioModemKind.Aicodix),
                    "Every priority message uses the currently selected modem.");
        }
    }

    private static void ObsoleteModemEncoderCannotDiscardPendingTransfer()
    {
        using (Fixture fixture = new Fixture())
        using (ManualResetEvent encoding = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            fixture.ValidateModem = delegate { };
            int attempts = 0;
            fixture.EncodeAudio = delegate(AudioModemKind kind, Guid transfer, int index, byte[] data)
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    encoding.Set();
                    if (!release.WaitOne(3000)) throw new TimeoutException("Synthetic stale encoder was not released");
                    throw new IOException("Synthetic old codec failed after it was replaced");
                }
                return SyntheticModem(kind, transfer, index, data);
            };
            fixture.Sender.Enable(fixture.Session); fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return encoding.WaitOne(0); }, "The original audio encoder is blocked before switching modem.");
            fixture.Sender.SetModem(AudioModemKind.Quiet);
            Guid report = Guid.NewGuid(); fixture.Sender.SendMessage(new byte[84], report);
            release.Set();
            Until(delegate { return Array.FindAll(fixture.Played(), delegate(Played item) { return item.Packet.TransferId == report; }).Length == 3; },
                "An obsolete encoder failure cannot discard a newer priority transmission.");
            Until(delegate { return Array.Exists(fixture.Played(), delegate(Played item) { return item.Packet.TransferId != report; }); },
                "The buffered clipboard survives an obsolete encoder failure and resumes after the priority report.");
            foreach (Played item in fixture.Played()) Check(item.Modem == AudioModemKind.Quiet, "No obsolete waveform reaches playback after the switch.");
            Check(fixture.Captures == 1, "Recovering from obsolete encoder work never recaptures private clipboard data.");
            foreach (Played item in fixture.Played()) if (item.Packet.TransferId != report) { fixture.Sender.Acknowledge(item.Packet.TransferId); break; }
        }
    }

    private static void DiagnosticPriorityReportMetadata()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio-sender-log-" + Guid.NewGuid().ToString("N") + ".jsonl");
        const string privateCanary = "synthetic-private-clipboard-canary-must-never-be-logged";
        Guid test = Guid.NewGuid(), audio = Guid.NewGuid();
        try
        {
            Check(!DiagnosticLog.IsEnabled && DiagnosticLog.Initialize(path), "A synthetic log uses only its explicit test executable directory.");
            using (Fixture fixture = new Fixture())
            {
                fixture.Encode = delegate { return Encoding.UTF8.GetBytes(privateCanary); };
                fixture.Sender.Enable(fixture.Session); fixture.Sender.SetBenchmarkMode(true);
                byte[] report = FileBenchmarkProtocol.Report(new FileBenchmarkReport { Session = fixture.Session, Test = test, Profile = VideoProfile.Mp4Modem,
                    Status = FileBenchmarkStatus.Progress, ReceivedBytes = 1082, TotalBytes = 102400, ElapsedMilliseconds = 2000 });
                fixture.Sender.SendMessage(report, audio);
                Until(delegate { return fixture.Played().Length == 3; }, "Synthetic diagnostics observe actual three-cycle report playback.");
                fixture.Sender.SetBenchmarkMode(false); fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
                Until(delegate { return fixture.Played().Length > 3; }, "A synthetic clipboard canary exercises metadata-only transmission logging.");
                fixture.Sender.SetBenchmarkMode(true);
            }
            Check(DiagnosticLog.Shutdown(5000), "Synthetic diagnostic output drains within a bounded wait.");
            string log = File.ReadAllText(path);
            Check(log.Contains("client.audio.priority_queued") && log.Contains("client.audio.play_begin") && log.Contains("client.audio.play_end") && log.Contains("client.audio.transmission_completed"),
                "Diagnostics distinguish priority enqueue from actual playback and completion.");
            Check(log.Contains(test.ToString()) && log.Contains(audio.ToString()) && log.Contains("Mp4Modem") && log.Contains("Progress") && log.Contains("1082"),
                "Playback diagnostics retain report test/profile/status/received-byte correlation metadata.");
            Check(!log.Contains(privateCanary), "Clipboard payload contents never enter diagnostic events.");
        }
        finally { DiagnosticLog.Shutdown(5000); if (File.Exists(path)) File.Delete(path); }
    }

    private static void FastReceiptPreemptsBulk()
    {
        using (Fixture fixture = new Fixture())
        using (var bulkStarted = new ManualResetEvent(false))
        {
            fixture.ValidateModem = delegate { };
            fixture.EncodeAudio = SyntheticModem;
            fixture.Sender.SetModem(AudioModemKind.Quiet);
            fixture.BeforePlay = delegate(AudioModemKind kind, CancellationToken token)
            {
                if (kind == AudioModemKind.Quiet) { bulkStarted.Set(); token.WaitHandle.WaitOne(3000); token.ThrowIfCancellationRequested(); }
            };
            fixture.Sender.Enable(fixture.Session);
            fixture.Sender.SendMessage(new byte[] { 1, 2, 3 }, Guid.NewGuid());
            Until(delegate { return bulkStarted.WaitOne(0); }, "Synthetic slow bulk playback starts.");
            Guid transfer = Guid.NewGuid();
            fixture.Sender.SendFastReceipt(fixture.Session, transfer, 10240);
            Until(delegate { return fixture.FastPlayed().Length == 1; }, "Compact receipt preempts the selected Quiet bulk encoder.");
            Pump(80);
            Check(fixture.FastPlayed().Length == 1, "A fast receipt uses exactly one burst, not three bulk repetitions.");
            Check(DeliveryReceipt.SameToken(fixture.FastPlayed()[0], DeliveryReceipt.FastToken(fixture.Session, transfer, 10240)), "The production fast PCM contains the exact publication token.");
            Check(fixture.Sender.SelectedModem == AudioModemKind.Quiet, "Compact control transport does not change the user's bulk codec selection.");
            fixture.Sender.SendFastReceipt(Guid.NewGuid(), Guid.NewGuid(), 10240); Pump(40);
            Check(fixture.FastPlayed().Length == 1, "A foreign session cannot queue a compact receipt.");
            fixture.Sender.Disable();
        }
    }

    private static void RepeatedReceiptLetsReverseFrameFinish()
    {
        foreach (int bytes in new[] { 4, 1024 })
        using (Fixture fixture = new Fixture())
        using (var started = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            fixture.ValidateModem = delegate { }; fixture.EncodeAudio = SyntheticModem;
            fixture.Sender.SetModem(AudioModemKind.Aicodix); fixture.Sender.SetAdditionalRetries(0);
            fixture.SenderServices.AcknowledgementGraceMilliseconds = 5000;
            fixture.Encode = delegate { return new byte[bytes]; };
            Guid forward = Guid.NewGuid(); int cancelled = 0, nativeCalls = 0;
            fixture.BeforePlay = delegate(AudioModemKind kind, CancellationToken token)
            {
                if (kind != AudioModemKind.Aicodix || Interlocked.Increment(ref nativeCalls) != 1) return;
                started.Set(); WaitHandle.WaitAny(new[] { release, token.WaitHandle }, 3000);
                if (token.IsCancellationRequested) Interlocked.Increment(ref cancelled);
                token.ThrowIfCancellationRequested();
            };
            fixture.Sender.Enable(fixture.Session); fixture.Sender.SendFastReceipt(fixture.Session, forward, 107);
            Until(delegate { return fixture.FastPlayed().Length == 1; }, "The first forward publication receipt precedes reverse data.");
            fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return started.WaitOne(0); }, "A reverse native frame is in flight before same-publication retries.");
            for (int i = 0; i < 5; i++) fixture.Sender.SendFastReceipt(fixture.Session, forward, 107);
            fixture.Sender.SendDeliveryReceiptFallback(fixture.Session, forward, 107);
            Pump(50);
            Check(Volatile.Read(ref cancelled) == 0, "Repeated receipts for the same publication cannot cancel the in-flight reverse native frame.");
            var latency = Stopwatch.StartNew(); release.Set();
            Until(delegate { return Array.Exists(fixture.Played(), delegate(Played item) { DeliveryReceipt receipt; return DeliveryReceipt.TryDecode(item.Packet.Data, fixture.Session, out receipt) && receipt.Transfer == forward; }); },
                "The latest pending receipt runs at the completed reverse packet boundary, including the final packet before ACK grace.");
            Check(latency.ElapsedMilliseconds < 1000, "A queued receipt does not wait for the reverse copy's five-second ACK grace.");
            Until(delegate {
                Played[] all = fixture.Played();
                Played first = Array.Find(all, delegate(Played item) { DeliveryReceipt receipt; return !DeliveryReceipt.TryDecode(item.Packet.Data, fixture.Session, out receipt); });
                return first != null && Array.FindAll(all, delegate(Played item) { return item.Packet.TransferId == first.Packet.TransferId; }).Length == first.Packet.Count;
            }, "The same reverse copy completes every packet without another capture or pass.");
            Played[] played = fixture.Played(); var indices = new HashSet<int>(); int receipts = 0;
            foreach (Played item in played)
            {
                DeliveryReceipt receipt;
                if (DeliveryReceipt.TryDecode(item.Packet.Data, fixture.Session, out receipt)) receipts++;
                else Check(indices.Add(item.Packet.Index), "Yielding to a repeated receipt retains NextIndex and never repeats a completed reverse packet.");
            }
            Check(receipts == 1 && fixture.FastPlayed().Length == 1 && fixture.Captures == 1 && Volatile.Read(ref cancelled) == 0,
                "Same-publication retries coalesce to the latest control while reverse data progresses once.");
            fixture.Sender.Disable();
        }
    }

    private static void RepeatedReceiptLetsNativeFallbackFinish()
    {
        using (Fixture fixture = new Fixture())
        using (var started = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            fixture.ValidateModem = delegate { }; fixture.EncodeAudio = SyntheticModem; fixture.Sender.SetModem(AudioModemKind.Aicodix);
            int nativeCalls = 0, cancelled = 0; Guid forward = Guid.NewGuid();
            fixture.BeforePlay = delegate(AudioModemKind kind, CancellationToken token)
            {
                if (kind != AudioModemKind.Aicodix || Interlocked.Increment(ref nativeCalls) != 1) return;
                started.Set(); WaitHandle.WaitAny(new[] { release, token.WaitHandle }, 3000);
                if (token.IsCancellationRequested) Interlocked.Increment(ref cancelled);
                token.ThrowIfCancellationRequested();
            };
            fixture.Sender.Enable(fixture.Session); fixture.Sender.SendDeliveryReceiptFallback(fixture.Session, forward, 107);
            Until(delegate { return started.WaitOne(0); }, "A native fallback is in flight before its next short retry.");
            fixture.Sender.SendDeliveryReceiptFallback(fixture.Session, forward, 107);
            fixture.Sender.SendFastReceipt(fixture.Session, forward, 107); Pump(50);
            Check(Volatile.Read(ref cancelled) == 0, "A repeated same-publication receipt cannot truncate its active native fallback.");
            release.Set();
            Until(delegate { return fixture.Played().Length == 1 && fixture.FastPlayed().Length == 1; }, "The complete native fallback is followed by the latest coalesced short retry.");
            DeliveryReceipt receipt;
            Check(DeliveryReceipt.TryDecode(fixture.Played()[0].Packet.Data, fixture.Session, out receipt) && receipt.Transfer == forward && receipt.EncodedBytes == 107 && cancelled == 0,
                "The completed native fallback still carries its exact original publication identity.");
            fixture.Sender.Disable();
        }
    }

    private static void NewReceiptIdentityStillPreempts()
    {
        foreach (bool changeBytes in new[] { false, true })
        using (Fixture fixture = new Fixture())
        using (var started = new ManualResetEvent(false))
        {
            fixture.ValidateModem = delegate { }; fixture.EncodeAudio = SyntheticModem; fixture.Sender.SetModem(AudioModemKind.Aicodix);
            Guid forward = Guid.NewGuid(); int cancelled = 0;
            fixture.BeforePlay = delegate(AudioModemKind kind, CancellationToken token)
            {
                if (kind != AudioModemKind.Aicodix) return;
                started.Set(); token.WaitHandle.WaitOne(3000);
                if (token.IsCancellationRequested) Interlocked.Increment(ref cancelled);
                token.ThrowIfCancellationRequested();
            };
            fixture.Sender.Enable(fixture.Session); fixture.Sender.SendDeliveryReceiptFallback(fixture.Session, forward, 107);
            Until(delegate { return started.WaitOne(0); }, "An old native receipt is active before a distinct publication arrives.");
            Guid next = changeBytes ? forward : Guid.NewGuid(); int nextBytes = changeBytes ? 108 : 107;
            var latency = Stopwatch.StartNew(); fixture.Sender.SendFastReceipt(fixture.Session, next, nextBytes);
            Until(delegate { return fixture.FastPlayed().Length == 1; }, "A different receipt identity retains immediate priority over the old native receipt.");
            Check(latency.ElapsedMilliseconds < 1000 && Volatile.Read(ref cancelled) == 1 && fixture.Played().Length == 0 &&
                DeliveryReceipt.SameToken(fixture.FastPlayed()[0], DeliveryReceipt.FastToken(fixture.Session, next, nextBytes)),
                "Transfer or encoded-byte identity changes preempt immediately and send only the exact new token.");
            fixture.Sender.Disable();
        }
    }

    private static void RepeatedReceiptWakesSilentAckGrace()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.SenderServices.AcknowledgementGraceMilliseconds = 5000; fixture.Sender.SetAdditionalRetries(0);
            fixture.Sender.Enable(fixture.Session); Guid forward = Guid.NewGuid();
            fixture.Sender.SendFastReceipt(fixture.Session, forward, 107);
            Until(delegate { return fixture.FastPlayed().Length == 1; }, "The publication has already emitted its initial receipt.");
            fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Played().Length == 1; }, "The reverse copy finishes playback before ACK grace.");
            Pump(40); var latency = Stopwatch.StartNew(); fixture.Sender.SendFastReceipt(fixture.Session, forward, 107);
            Until(delegate { return fixture.FastPlayed().Length == 2; }, "A same-publication retry wakes an already silent ACK grace immediately.");
            Check(latency.ElapsedMilliseconds < 1000 && fixture.Played().Length == 1, "Waking silent grace does not restart the completed reverse data pass.");
            fixture.Sender.Disable();
        }
    }

    private static void LowerPriorityCannotInterruptFastReceipt()
    {
        using (Fixture fixture = new Fixture())
        using (var started = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            int cancelled = 0;
            fixture.ValidateModem = delegate { }; fixture.EncodeAudio = SyntheticModem;
            fixture.Sender.SetModem(AudioModemKind.Quiet);
            fixture.BeforePlay = delegate(AudioModemKind kind, CancellationToken cancel)
            {
                if (kind != AudioModemKind.Fsk) return;
                started.Set();
                WaitHandle.WaitAny(new[] { release, cancel.WaitHandle }, 3000);
                if (cancel.IsCancellationRequested) Interlocked.Increment(ref cancelled);
                cancel.ThrowIfCancellationRequested();
            };
            fixture.Sender.Enable(fixture.Session);
            fixture.Sender.SendFastReceipt(fixture.Session, Guid.NewGuid(), 1234);
            Until(delegate { return started.WaitOne(0); }, "Compact playback starts before lower-priority reports.");
            fixture.Sender.SendMessage(new byte[] { 3, 2, 1 }, Guid.NewGuid());
            fixture.Sender.TestOutput();
            fixture.Sender.SetModem(AudioModemKind.Aicodix);
            Pump(60);
            Check(Volatile.Read(ref cancelled) == 0, "A later bulk report, diagnostic probe or bulk codec change cannot interrupt the fixed-FSK ACK.");
            release.Set();
            Until(delegate { return fixture.FastPlayed().Length == 1; }, "The original compact burst completes once without restart.");
            Check(Volatile.Read(ref cancelled) == 0, "The original fast playback token remained valid.");
            fixture.Sender.Disable();
        }
    }

    private static void ReceiptPreemptionRetainsReversePacketPosition()
    {
        foreach (AudioModemKind kind in new[] { AudioModemKind.Quiet, AudioModemKind.Aicodix })
        foreach (bool fallback in new[] { false, true })
        using (Fixture fixture = new Fixture())
        using (var interrupted = new ManualResetEvent(false))
        {
            fixture.ValidateModem = delegate { }; fixture.EncodeAudio = SyntheticModem;
            fixture.SenderServices.AcknowledgementGraceMilliseconds = 20;
            fixture.Sender.SetAdditionalRetries(0); fixture.Sender.SetModem(kind);
            fixture.Encode = delegate { return new byte[1024]; };
            int calls = 0;
            fixture.BeforePlay = delegate(AudioModemKind modem, CancellationToken token)
            {
                if (Interlocked.Increment(ref calls) == 2)
                { interrupted.Set(); token.WaitHandle.WaitOne(3000); token.ThrowIfCancellationRequested(); }
            };
            fixture.Sender.Enable(fixture.Session); fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return interrupted.WaitOne(0); }, "A second reverse packet is in flight before publication receipt preemption.");
            Played first = fixture.Played()[0]; Guid forward = Guid.NewGuid(); var latency = Stopwatch.StartNew();
            if (fallback) fixture.Sender.SendDeliveryReceiptFallback(fixture.Session, forward, 107);
            else fixture.Sender.SendFastReceipt(fixture.Session, forward, 107);
            Until(delegate {
                if (!fallback) return fixture.FastPlayed().Length == 1;
                return Array.Exists(fixture.Played(), delegate(Played item) { DeliveryReceipt receipt; return DeliveryReceipt.TryDecode(item.Packet.Data, fixture.Session, out receipt) && receipt.Transfer == forward; });
            }, "A delivery receipt interrupts long reverse playback immediately.");
            Check(latency.ElapsedMilliseconds < 1000, "Receipt priority does not wait for a three-second bulk playback timeout.");
            Until(delegate { return Array.FindAll(fixture.Played(), delegate(Played item) { return item.Packet.TransferId == first.Packet.TransferId; }).Length == first.Packet.Count; },
                "The reverse copy resumes at its interrupted packet and completes its original zero-retry pass.");
            Pump(60); Played[] all = fixture.Played(); var indices = new HashSet<int>(); int receipts = 0;
            foreach (Played item in all)
            {
                if (item.Packet.TransferId == first.Packet.TransferId)
                    Check(indices.Add(item.Packet.Index), "Receipt preemption never skips or consumes an unfinished reverse packet, or repeats a completed one.");
                else
                {
                    DeliveryReceipt receipt;
                    Check(fallback && DeliveryReceipt.TryDecode(item.Packet.Data, fixture.Session, out receipt) && receipt.Transfer == forward && receipt.EncodedBytes == 107 && item.Modem == kind,
                        "Native fallback uses the selected codec and exact publication identity only."); receipts++;
                }
            }
            Check(indices.Count == first.Packet.Count && receipts == (fallback ? 1 : 0), "Native fallback has one pass; reverse clipboard keeps its identity, packet count and retry budget.");
            Check(fixture.Sender.SelectedModem == kind, "Fast-control preemption never changes the selected bulk modem.");
        }
    }

    private static void LowerPriorityCannotInterruptNativeReceipt()
    {
        using (Fixture fixture = new Fixture())
        using (var started = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            fixture.ValidateModem = delegate { }; fixture.EncodeAudio = SyntheticModem; fixture.Sender.SetModem(AudioModemKind.Aicodix);
            int cancelled = 0, calls = 0;
            fixture.BeforePlay = delegate(AudioModemKind kind, CancellationToken token)
            {
                if (Interlocked.Increment(ref calls) != 1) return;
                started.Set(); WaitHandle.WaitAny(new[] { release, token.WaitHandle }, 3000);
                if (token.IsCancellationRequested) Interlocked.Increment(ref cancelled);
                token.ThrowIfCancellationRequested();
            };
            fixture.Sender.Enable(fixture.Session); Guid transfer = Guid.NewGuid();
            fixture.Sender.SendDeliveryReceiptFallback(fixture.Session, transfer, 191);
            Until(delegate { return started.WaitOne(0); }, "A selected-modem receipt begins before lower-priority work.");
            fixture.Sender.SendMessage(new byte[] { 3, 2, 1 }, Guid.NewGuid()); fixture.Sender.TestOutput(); Pump(50);
            Check(Volatile.Read(ref cancelled) == 0, "New reports and an output probe cannot interrupt the selected-modem receipt.");
            release.Set();
            Until(delegate { return Array.Exists(fixture.Played(), delegate(Played item) { DeliveryReceipt receipt; return DeliveryReceipt.TryDecode(item.Packet.Data, fixture.Session, out receipt) && receipt.Transfer == transfer; }); },
                "The selected-modem receipt completes after lower-priority arrivals.");
            Pump(70);
            Check(Array.FindAll(fixture.Played(), delegate(Played item) { DeliveryReceipt receipt; return DeliveryReceipt.TryDecode(item.Packet.Data, fixture.Session, out receipt); }).Length == 1,
                "Selected-modem fallback is one complete burst, never the ordinary three report repetitions.");
            Check(Volatile.Read(ref cancelled) == 0, "No lower-priority action cancelled the fallback receipt token."); fixture.Sender.Disable();
        }
    }

    private sealed class Played
    {
        internal Packet Packet;
        internal string Output;
        internal AudioModemKind Modem;
    }

    private static void BoundedClipboardPasses()
    {
        foreach (int retries in new[] { 0, 2 })
        using (Fixture fixture = new Fixture())
        {
            fixture.SenderServices.AcknowledgementGraceMilliseconds = 20;
            fixture.Sender.SetAdditionalRetries(retries); fixture.Sender.Enable(fixture.Session);
            fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Played().Length == retries + 1; }, "A copy sends exactly its configured initial pass plus retries.");
            Pump(100);
            Check(fixture.Played().Length == retries + 1 && fixture.Captures == 1, "Missing ACK cannot create infinite audio or re-read the clipboard after its bounded passes.");
            fixture.Sender.SetOutput("waveout:0"); Pump(60);
            Check(fixture.Played().Length == retries + 1, "An exhausted copy cannot resurrect on output refresh.");
        }
    }

    private static void PreemptionPreservesCompletedPasses()
    {
        foreach (string action in new[] { "modem", "output", "priority" })
        using (Fixture fixture = new Fixture())
        using (var secondPass = new ManualResetEvent(false))
        {
            fixture.SenderServices.AcknowledgementGraceMilliseconds = 20;
            fixture.ValidateModem = delegate { }; fixture.EncodeAudio = SyntheticModem;
            int playAttempts = 0;
            fixture.BeforePlay = delegate(AudioModemKind kind, CancellationToken token)
            {
                if (Interlocked.Increment(ref playAttempts) != 2) return;
                secondPass.Set(); if (!token.WaitHandle.WaitOne(3000)) throw new Exception("Expected bounded preemption was not delivered."); token.ThrowIfCancellationRequested();
            };
            fixture.Sender.Enable(fixture.Session); fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return secondPass.WaitOne(0); }, "One complete clipboard pass precedes simulated preemption.");
            Guid id = fixture.Played()[0].Packet.TransferId;
            if (action == "modem") fixture.Sender.SetModem(AudioModemKind.Quiet);
            else if (action == "output") fixture.Sender.SetOutput("waveout:0");
            else fixture.Sender.SendMessage(new byte[] { 4, 5, 6 }, Guid.NewGuid());
            Until(delegate { return Array.FindAll(fixture.Played(), delegate(Played p) { return p.Packet.TransferId == id; }).Length == 3; }, "Buffered bytes finish only the two remaining passes after " + action + " preemption.");
            Pump(100);
            Check(Array.FindAll(fixture.Played(), delegate(Played p) { return p.Packet.TransferId == id; }).Length == 3 && fixture.Captures == 1,
                "A " + action + " change preserves transfer identity and completed-pass budget instead of restarting three more passes.");
        }
    }

    private static void RetryPolicyCanExtendSilentAckGrace()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.SenderServices.AcknowledgementGraceMilliseconds = 500;
            fixture.Sender.SetAdditionalRetries(0); fixture.Sender.Enable(fixture.Session);
            fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Played().Length == 1; }, "A zero-retry copy completes its initial pass before silent ACK grace.");
            Pump(30); fixture.Sender.SetAdditionalRetries(2);
            Until(delegate { return fixture.Played().Length == 3; }, "An increased retry policy resumes the same buffered copy during ACK grace.");
            Check(fixture.Captures == 1 && fixture.Played()[0].Packet.TransferId == fixture.Played()[2].Packet.TransferId,
                "Extending the limit preserves identity and does not recapture user data.");
            fixture.Sender.Acknowledge(fixture.Played()[0].Packet.TransferId);
        }
    }

    private static void RouteCalibrationSuspendsEveryPriority()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Sender.Enable(fixture.Session);
            fixture.Sender.SetRouteCalibration(true);
            fixture.Sender.SendMessage(new byte[] { 41 }, Guid.NewGuid());
            fixture.Sender.TestOutput();
            fixture.Sender.SendFastReceipt(fixture.Session, Guid.NewGuid(), 12);
            fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Pump(250);
            Check(fixture.Played().Length == 0 && fixture.FastPlayed().Length == 0 && fixture.Captures == 0,
                "Calibration suspends bulk, probe, control and fast receipts without reading the clipboard.");
            fixture.Sender.SetRouteCalibration(false);
            Until(delegate { return fixture.FastPlayed().Length == 1 && fixture.Played().Length != 0; }, "Priority queues resume after calibration ends.");
            fixture.Sender.NotifyClipboardChanged(); Pump(220);
            Check(fixture.Captures == 0, "Copies made during calibration become the fresh baseline and are never sent later.");
        }
        using (Fixture fixture = new Fixture())
        using (var started = new ManualResetEvent(false))
        {
            int calls = 0;
            fixture.BeforePlay = delegate(AudioModemKind kind, CancellationToken token)
            { if (Interlocked.Increment(ref calls) == 1) { started.Set(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); } };
            fixture.Sender.Enable(fixture.Session);
            fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return started.WaitOne(0); }, "An active copy exists before calibration retires it.");
            fixture.Sender.SetRouteCalibration(true);
            fixture.Sequence = 3; fixture.Sender.NotifyClipboardChanged();
            fixture.Sender.SetRouteCalibration(false); Pump(240);
            Check(fixture.Played().Length == 0 && fixture.Captures == 1, "Calibration drops the old transfer and never resumes a server-retired ID.");
            fixture.Sequence = 4; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Played().Length != 0; }, "A fresh copy after calibration starts a new transfer.");
            Check(fixture.Captures == 2, "A user must copy again after the test, with a fresh baseline.");
        }
    }

    private static void VerifiedRoutesStripeAndResumeCancelledWave()
    {
        using (Fixture fixture = new Fixture())
        using (var started = new ManualResetEvent(false))
        {
            fixture.SenderServices.AcknowledgementGraceMilliseconds = 30;
            fixture.Sender.SetAdditionalRetries(0);
            fixture.Encode = delegate { return new byte[1024]; };
            int calls = 0;
            fixture.BeforePlay = delegate(AudioModemKind modem, CancellationToken token)
            { if (Interlocked.Increment(ref calls) == 1) { started.Set(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); } };
            fixture.Sender.Enable(fixture.Session);
            fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return started.WaitOne(0); }, "The first wave starts before calibration interrupts playback.");
            string[] outputs = { "waveout:1", "waveout:0" };
            fixture.Sender.SetVerifiedOutputs(Guid.NewGuid(), outputs);
            outputs[1] = "waveout:999";
            Until(delegate { return fixture.Played().Length == 5; }, "One initial pass sends all five packets through verified stripes after cancellation.");
            Pump(80);
            Played[] played = fixture.Played(); var indices = new HashSet<int>();
            foreach (Played item in played)
            {
                Check(item.Packet.TransferId == played[0].Packet.TransferId && indices.Add(item.Packet.Index), "Wave cancellation preserves one transfer ID and does not consume or duplicate a completed packet.");
                Check(item.Output == "waveout:0" || item.Output == "waveout:1", "Verified route arrays are copied before a caller can mutate them.");
            }
            Check(played.Length == 5 && indices.Count == 5 && fixture.Captures == 1, "Route changes and striped waves retain the zero-retry budget and original buffered copy.");
            Check(Array.Exists(played, delegate(Played item) { return item.Output == "waveout:0"; }) &&
                Array.Exists(played, delegate(Played item) { return item.Output == "waveout:1"; }), "Different packet indices use both verified outputs.");
        }
    }

    private static void EmptyVerifiedRoutesMuteWithoutFallback()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Sender.Enable(fixture.Session);
            fixture.Sender.SetAdditionalRetries(0);
            fixture.Sender.SetVerifiedOutputs(Guid.NewGuid(), new string[0]);
            fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Encodes == 1; }, "An explicit mute can keep a buffered new copy.");
            Pump(80); Check(fixture.Played().Length == 0, "An empty verified route set never falls back to the manual or default output.");
            fixture.Sender.SetVerifiedOutputs(Guid.NewGuid(), new[] { "waveout:0" });
            Until(delegate { return fixture.Played().Length == 1; }, "Enabling one verified route resumes the buffered copy.");
            Check(fixture.Played()[0].Output == "waveout:0", "The only enabled verified output is used.");
            fixture.Sender.SetVerifiedOutputs(Guid.Empty, null);
            fixture.Sequence = 3; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Played().Length == 2; }, "Null verified routes restore ordinary single-output behavior.");
            Check(fixture.Played()[1].Output == "waveout:1", "Clearing verified routes restores the original automatic/manual choice, not the verified primary.");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Sender.SetOutput("waveout:0"); fixture.Sender.Enable(fixture.Session);
            fixture.Sender.SetAdditionalRetries(0);
            fixture.Sender.SetVerifiedOutputs(Guid.NewGuid(), new[] { "waveout:1" });
            fixture.Sender.Enable(Guid.NewGuid());
            fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Played().Length == 1; }, "A new server session invalidates the old verified plan.");
            Check(fixture.Played()[0].Output == "waveout:0", "A new session restores explicit manual output instead of carrying stale verified indices forward.");
        }
    }

    private static void TransferHeartbeatsPrecedeSingleAndParallelData()
    {
        foreach (bool parallel in new[] { false, true })
        using (Fixture fixture = new Fixture())
        using (var interrupted = new CountdownEvent(parallel ? 2 : 1))
        {
            fixture.SenderServices.EnableTransferStatus = true;
            fixture.SenderServices.AcknowledgementGraceMilliseconds = 20;
            fixture.ValidateModem = delegate { }; fixture.EncodeAudio = SyntheticModem;
            fixture.Sender.SetModem(AudioModemKind.Quiet);
            fixture.Sender.SetAdditionalRetries(0);
            fixture.Encode = delegate { return new byte[1024]; };
            int bulkCalls = 0, routes = parallel ? 2 : 1;
            fixture.BeforePlay = delegate(AudioModemKind kind, CancellationToken token)
            {
                if (kind == AudioModemKind.Quiet && Interlocked.Increment(ref bulkCalls) <= routes)
                { interrupted.Signal(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
            };
            fixture.Sender.Enable(fixture.Session);
            if (parallel) fixture.Sender.SetVerifiedOutputs(Guid.NewGuid(), new[] { "waveout:1", "waveout:0" });
            fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return interrupted.IsSet; }, "The first native data wave begins after initial route heartbeats.");
            fixture.Sender.SendFastReceipt(fixture.Session, Guid.NewGuid(), 123);
            Until(delegate { return fixture.FastPlayed().Length == 1; }, "A fast ACK can interrupt every bulk route.");
            Until(delegate
            {
                int complete = 0;
                foreach (Played played in fixture.Played()) { AudioTransferStatus status; if (AudioTransferStatusProtocol.TryDecode(played.Packet.Data, played.Packet.TransferId, out status) && status.Phase == AudioTransferPhase.PassComplete) complete++; }
                return complete == routes;
            }, "The resumed single/parallel pass emits terminal sending status on every route.");
            int sending = 0, data = 0, finished = 0; Guid transfer = Guid.Empty; uint last = 0;
            foreach (Played played in fixture.Played())
            {
                AudioTransferStatus status;
                if (AudioTransferStatusProtocol.TryDecode(played.Packet.Data, played.Packet.TransferId, out status))
                {
                    Check(played.Modem == AudioModemKind.Fsk && status.Session == fixture.Session && status.PassIndex == 0, "Heartbeats use fixed FSK, current session and zero-based pass even when Quiet is selected.");
                    if (transfer == Guid.Empty) transfer = status.Transfer;
                    Check(status.Transfer == transfer && status.Sequence >= last, "Route broadcasts share a bound sequence; later status advances it."); last = status.Sequence;
                    if (status.Phase == AudioTransferPhase.Sending) { sending++; Check(data == 0 && status.SentBytes == 0, "Initial and post-cancellation boundaries precede uncommitted native data."); }
                    else { finished++; Check(data == 5 && status.SentBytes == status.TotalBytes, "PassComplete means every buffered byte finished playback, after five data packets."); }
                }
                else { data++; Check(played.Packet.TransferId == transfer && played.Modem == AudioModemKind.Quiet, "All striped data retain one logical transfer and selected modem."); }
            }
            Check(sending == routes * 2 && finished == routes && data == 5,
                "Cancellation forces a fresh heartbeat on ALL routes within the normal rate limit, without consuming an extra pass.");
        }
    }

    private static void TransferHeartbeatPassIdentity()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.SenderServices.EnableTransferStatus = true;
            fixture.SenderServices.AcknowledgementGraceMilliseconds = 20;
            fixture.Sender.SetAdditionalRetries(1); fixture.Sender.Enable(fixture.Session);
            fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Played().Length == 6; }, "Two complete sending passes each emit begin/data/PassComplete.");
            Pump(80); uint sequence = 0; int statuses = 0;
            foreach (Played item in fixture.Played())
            {
                AudioTransferStatus status;
                if (!AudioTransferStatusProtocol.TryDecode(item.Packet.Data, item.Packet.TransferId, out status)) continue;
                Check(status.Sequence > sequence && status.PassIndex == statuses / 2, "Heartbeat sequence never resets when a new pass resets sent byte progress.");
                Check(status.Phase == (statuses % 2 == 0 ? AudioTransferPhase.Sending : AudioTransferPhase.PassComplete), "Every pass has explicit sending and playback-complete phases.");
                sequence = status.Sequence; statuses++;
            }
            Check(statuses == 4 && fixture.Played().Length == 6, "Retry statuses do not create additional data passes or imply remote publication.");
        }
    }

    private static void StaleVerifiedRouteSessionCannotMutateNewSession()
    {
        using (Fixture fixture = new Fixture())
        using (var release = new ManualResetEvent(false))
        {
            fixture.Sender.SetOutput("waveout:0"); fixture.Sender.Enable(fixture.Session);
            fixture.Sender.SetAdditionalRetries(0);
            bool staleAccepted = true;
            var delayed = new Thread(delegate()
            {
                release.WaitOne();
                staleAccepted = fixture.Sender.SetVerifiedOutputs(fixture.Session, Guid.NewGuid(), new[] { "waveout:0" });
            });
            delayed.Start();
            Guid next = Guid.NewGuid(); fixture.Sender.Enable(next);
            Check(fixture.Sender.SetVerifiedOutputs(next, Guid.NewGuid(), new[] { "waveout:1" }), "Current-session verified selection is accepted atomically.");
            release.Set(); Check(delayed.Join(4000), "The delayed old-session selection returns without blocking.");
            Check(!staleAccepted && fixture.Sender.SelectedOutputId == "waveout:1", "A stale asynchronous selection cannot overwrite the new session's verified primary.");
            fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Played().Length == 1; }, "The new session still uses its own selected output after stale completion.");
            Check(fixture.Played()[0].Output == "waveout:1", "Old-session selection leaves the new verified route and playback unchanged.");
            fixture.Sender.Disable();
            Check(!fixture.Sender.SetVerifiedOutputs(next, Guid.NewGuid(), new[] { "waveout:0" }), "Disabled sessions reject route-selection mutations too.");
        }
    }

    private static void StaleVerifiedRouteCommandCannotMutateSameSession()
    {
        using (Fixture fixture = new Fixture())
        using (var release = new ManualResetEvent(false))
        {
            fixture.Sender.SetOutput("waveout:0"); fixture.Sender.Enable(fixture.Session);
            fixture.Sender.SetAdditionalRetries(0);
            int generation = 1, guardCalls = 0; bool staleAccepted = true, guardedUnderSenderGate = false;
            object senderGate = typeof(AudioClipboardSender).GetField("gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(fixture.Sender);
            var delayed = new Thread(delegate()
            {
                release.WaitOne();
                staleAccepted = fixture.Sender.SetVerifiedOutputs(fixture.Session, Guid.NewGuid(), new[] { "waveout:0" }, delegate
                {
                    Interlocked.Increment(ref guardCalls);
                    guardedUnderSenderGate = Monitor.IsEntered(senderGate);
                    return Volatile.Read(ref generation) == 1;
                });
            });
            delayed.Start();
            Volatile.Write(ref generation, 2);
            Check(fixture.Sender.SetVerifiedOutputs(fixture.Session, Guid.NewGuid(), new[] { "waveout:1" }, delegate { return true; }),
                "The current same-session command can install a verified route using its final guard.");
            release.Set(); Check(delayed.Join(4000), "A delayed old command completes its final generation check.");
            Check(!staleAccepted && guardCalls == 1 && guardedUnderSenderGate && fixture.Sender.SelectedOutputId == "waveout:1",
                "A stale command in the SAME session is rejected under sender gate without replacing the latest route.");
            fixture.Sequence = 2; fixture.Sender.NotifyClipboardChanged();
            Until(delegate { return fixture.Played().Length == 1; }, "The current route still carries a fresh copy after an obsolete same-session completion.");
            Check(fixture.Played()[0].Output == "waveout:1", "Obsolete same-session commands cannot change actual playback.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Control Owner = new Control();
        internal readonly Guid Session = Guid.NewGuid();
        internal readonly AudioClipboardSender Sender;
        internal readonly AudioSenderServices SenderServices;
        private int sequence = 1, captures, encodes, sequenceReads, outputQueries, listenerAdds, listenerRemoves, playStarts;
        private readonly object gate = new object();
        private readonly List<Played> played = new List<Played>();
        private readonly List<byte[]> fastPlayed = new List<byte[]>();
        private readonly Dictionary<AudioPlayer, string> players = new Dictionary<AudioPlayer, string>();
        private readonly Dictionary<float[], AudioModemKind> waveformModems = new Dictionary<float[], AudioModemKind>();
        private readonly List<Exception> errors = new List<Exception>();
        internal AudioEndpoint[] Outputs = { new AudioEndpoint("waveout:0", "Speakers (Realtek HD Audio)"), new AudioEndpoint("waveout:1", "VMware VirtualAudio (DevTap)") };
        internal Func<ClipboardSnapshot, byte[]> Encode;
        internal Action<AudioModemKind> ValidateModem;
        internal Func<AudioModemKind, Guid, int, byte[], AudioEncodedBlock> EncodeAudio;
        internal Action<AudioModemKind, CancellationToken> BeforePlay;
        internal Action<Packet, CancellationToken> BeforePacketPublished;

        internal Fixture()
        {
            AudioSenderServices services = new AudioSenderServices();
            SenderServices = services;
            services.ValidateModem = delegate(AudioModemKind kind)
            {
                Action<AudioModemKind> validate = ValidateModem;
                if (validate != null) validate(kind); else AudioTransmissionEncoder.Validate(kind);
            };
            services.EncodeAudio = delegate(AudioModemKind kind, Guid transfer, int index, byte[] data)
            {
                Func<AudioModemKind, Guid, int, byte[], AudioEncodedBlock> encode = EncodeAudio;
                AudioEncodedBlock block = encode == null ? AudioTransmissionEncoder.Encode(kind, transfer, index, data) : encode(kind, transfer, index, data);
                lock (gate) waveformModems[block.Samples] = kind;
                return block;
            };
            services.EncodeFastReceipt = delegate(byte[] wire)
            {
                AudioEncodedBlock block = AudioTransmissionEncoder.EncodeFastReceipt(wire);
                lock (gate) waveformModems[block.Samples] = AudioModemKind.Fsk;
                return block;
            };
            services.Sequence = delegate { Interlocked.Increment(ref sequenceReads); return (uint)Volatile.Read(ref sequence); };
            services.AddListener = delegate { Interlocked.Increment(ref listenerAdds); return true; };
            services.RemoveListener = delegate { Interlocked.Increment(ref listenerRemoves); return true; };
            services.Outputs = delegate { Interlocked.Increment(ref outputQueries); return Outputs; };
            services.CreatePlayer = delegate(string id)
            {
                AudioPlayer player = new AudioPlayer(id); // Constructor opens no device.
                lock (gate) players.Add(player, id);
                return player;
            };
            services.Capture = delegate
            {
                Interlocked.Increment(ref captures);
                ClipboardSnapshot snapshot = new ClipboardSnapshot();
                snapshot.Formats.Add("fixture", BitConverter.GetBytes(Volatile.Read(ref sequence)));
                return snapshot;
            };
            services.Encode = delegate(ClipboardSnapshot snapshot)
            {
                Interlocked.Increment(ref encodes);
                Func<ClipboardSnapshot, byte[]> callback = Encode;
                return callback == null ? snapshot.Formats["fixture"] : callback(snapshot);
            };
            services.Play = delegate(AudioPlayer player, float[] samples, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref playStarts);
                AudioModemKind kind;
                lock (gate) kind = waveformModems[samples];
                Action<AudioModemKind, CancellationToken> before = BeforePlay;
                if (before != null) before(kind, token);
                token.ThrowIfCancellationRequested();
                AudioDecoder decoder = new AudioDecoder();
                byte[] logicalReceipt = null;
                decoder.FastReceiptReceived += delegate(byte[] receiptToken)
                {
                    if (logicalReceipt == null) logicalReceipt = receiptToken;
                    else Check(DeliveryReceipt.SameToken(logicalReceipt, receiptToken), "Redundant frames in one logical fast burst agree on the exact receipt token.");
                };
                decoder.PacketReceived += delegate(Packet packet)
                {
                    Action<Packet, CancellationToken> beforePacket = BeforePacketPublished;
                    if (beforePacket != null) beforePacket(packet, token);
                    lock (gate) played.Add(new Played { Packet = packet, Output = players[player], Modem = kind });
                };
                decoder.Feed(samples);
                if (logicalReceipt != null) lock (gate) fastPlayed.Add(logicalReceipt);
                if (token.WaitHandle.WaitOne(8)) token.ThrowIfCancellationRequested();
            };
            Sender = new AudioClipboardSender(Owner, services);
            Sender.Error = delegate(Exception error) { lock (gate) errors.Add(error); };
        }

        internal int Sequence { set { Volatile.Write(ref sequence, value); } }
        internal int Captures { get { return Volatile.Read(ref captures); } }
        internal int Encodes { get { return Volatile.Read(ref encodes); } }
        internal int SequenceReads { get { return Volatile.Read(ref sequenceReads); } }
        internal int OutputQueries { get { return Volatile.Read(ref outputQueries); } }
        internal int ListenerAdds { get { return Volatile.Read(ref listenerAdds); } }
        internal int ListenerRemoves { get { return Volatile.Read(ref listenerRemoves); } }
        internal int PlayStarts { get { return Volatile.Read(ref playStarts); } }
        internal bool PlaybackIdle
        {
            get
            {
                const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                object senderGate = typeof(AudioClipboardSender).GetField("gate", fields).GetValue(Sender);
                lock (senderGate)
                    return typeof(AudioClipboardSender).GetField("active", fields).GetValue(Sender) == null &&
                        typeof(AudioClipboardSender).GetField("playback", fields).GetValue(Sender) == null;
            }
        }
        internal int PlayerCount { get { lock (gate) return players.Count; } }
        internal Played[] Played() { lock (gate) return played.ToArray(); }
        internal byte[][] FastPlayed() { lock (gate) return fastPlayed.ToArray(); }
        public void Dispose()
        {
            Sender.Dispose();
            Owner.Dispose();
            lock (gate) { if (errors.Count != 0) throw new Exception("Unexpected sender error.", errors[0]); }
        }
    }

    private static void Pump(int milliseconds)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(3); }
    }

    private static void Until(Func<bool> condition, string description)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < 4000) { Application.DoEvents(); Thread.Sleep(3); }
        Check(condition(), description);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        checks++;
    }
}
