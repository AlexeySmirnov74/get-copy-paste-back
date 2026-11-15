// Pure coordination checks. Fake timers, endpoints and capture never access hardware or clipboard.
using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using HorizonClient;

internal static class ClientAudioBridgeTests
{
    private static int checks;
    [STAThread]
    private static int Main()
    {
        try
        {
            CheckMatching();
            CheckBenchmark();
            CheckRetiredSessions();
            CheckFreshBenchmarkSameSession();
            CheckMissingMicrophone();
            CheckConstruction();
            CheckTiledBenchmarkEpochs();
            CheckDeliveryReceipts();
            CheckFastDeliveryReceipts();
            CheckLostFastReceiptsAndNativeFallback();
            CheckUnannouncedAndBenchmarkedReceipts();
            CheckControlDiagnostics();
            CheckAudioRetryPolicySessions();
            CheckFileBenchmarkReservationLifetime();
            CheckFileReadyActualGeometry();
            Console.WriteLine("Client audio bridge checks passed: " + checks + " assertions; unique microphone selection, benchmark-only counting, deduplication, timers, reports, replay/session disposal. All endpoints, capture, output and clipboard operations were mocked.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckMatching()
    {
        AudioEndpoint webcam = new AudioEndpoint("virtual-camera", "Microphone (4k web camera)");
        AudioEndpoint physical = new AudioEndpoint("physical", "Microphone Array (Realtek Audio)");
        Same(webcam, ClientAudioBridge.SelectMicrophone(new AudioEndpoint[] { physical, webcam }, "4k webcam"), "Horizon webcam alias");
        Same(webcam, ClientAudioBridge.SelectMicrophone(new AudioEndpoint[] { webcam }, "4K WEB-CAM"), "case and punctuation normalization");
        AudioEndpoint russian = new AudioEndpoint("virtual-camera-ru", "Микрофон (4k webcam)");
        Same(russian, ClientAudioBridge.SelectMicrophone(new AudioEndpoint[] { russian }, "4k webcam"), "localized microphone prefix");
        AudioEndpoint suffix = new AudioEndpoint("virtual-camera-suffix", "4k webcam Microphone");
        Same(suffix, ClientAudioBridge.SelectMicrophone(new AudioEndpoint[] { suffix }, "4k webcam"), "microphone suffix");
        True(ClientAudioBridge.SelectMicrophone(new AudioEndpoint[] { webcam, new AudioEndpoint("duplicate", webcam.Name) }, "4k webcam") == null, "ambiguous endpoints rejected");
        True(ClientAudioBridge.SelectMicrophone(new AudioEndpoint[] { physical }, "4k webcam") == null, "no physical/default fallback");
        True(ClientAudioBridge.SelectMicrophone(new AudioEndpoint[] { new AudioEndpoint("similar", "Microphone (4k webcam Pro)") }, "4k webcam") == null, "partial camera name not accepted");
        True(ClientAudioBridge.SelectMicrophone(new AudioEndpoint[] { new AudioEndpoint("similar", "Microphone (4k webcam 2)") }, "4k webcam") == null, "another numbered camera not accepted");
        True(ClientAudioBridge.SelectMicrophone(null, "4k webcam") == null, "missing endpoint list");
        True(ClientAudioBridge.SelectMicrophone(new AudioEndpoint[] { webcam }, "") == null, "missing camera name");
    }

    private static void CheckBenchmark()
    {
        using (Control owner = new Control())
        {
            FakeServices services = new FakeServices();
            services.Endpoints = new AudioEndpoint[] { new AudioEndpoint("virtual-camera", "Microphone (4k web camera)") };
            using (ClientAudioBridge bridge = new ClientAudioBridge(owner, services))
            {
                Equal(0, services.Enabled.Count, "constructor does not enable sender");
                Equal(0, services.Enumerations, "constructor does not enumerate audio");
                Equal(0, services.Timers.Count, "constructor does not start timer");
                BridgeHello hello = Hello();
                Packet announcement = BridgeControl.Single(hello.Session, BridgeControl.Hello(hello.Session, hello.VideoTest, hello.AudioTest, hello.Seconds));
                True(!bridge.HandleVideo(announcement), "hello continues to STA control handler");
                Packet early = Packet(hello.VideoTest, 0, 256);
                True(bridge.HandleVideo(early), "early benchmark data never reaches clipboard before STA hello");
                bridge.HandleHello(hello, "4k webcam");
                Equal(1, services.Enabled.Count, "hello enables reverse sender");
                Equal(hello.Session, services.Enabled[0], "sender session binding");
                Equal(1, services.Enumerations, "one microphone enumeration");
                Equal("virtual-camera", services.CapturedId, "explicit selected camera microphone");
                Equal(20000, services.Timers[0].Milliseconds, "benchmark measures the announced twenty-second window");
                True(bridge.HandleVideo(early), "video benchmark counted");
                True(bridge.HandleVideo(early), "duplicate video packet consumed");
                True(bridge.HandleVideo(Packet(hello.VideoTest, 1, 256)), "second video index counted");
                True(!bridge.HandleVideo(Packet(Guid.NewGuid(), 0, 256)), "ordinary clipboard packet continues");
                Packet audio = BridgeControl.Single(hello.AudioTest, new byte[32]);
                services.Capture.Feed(AudioModem.Encode(audio));
                services.Capture.Feed(AudioModem.Encode(audio));
                services.Capture.Feed(AudioModem.Encode(BridgeControl.Single(Guid.NewGuid(), new byte[24])));
                bridge.HandleHello(hello, "4k webcam");
                Equal(1, services.Timers.Count, "same hello does not restart benchmark");
                Equal(1, services.Captures, "same hello does not reopen microphone");
                services.Timers[0].Fire();
                Equal(1, services.Sent.Count, "one completed report");
                BridgeReport report;
                True(BridgeControl.TryReport(services.Sent[0], hello.Session, out report), "valid report envelope");
                Equal(hello.VideoTest, report.Test, "report test binding");
                Equal(512L, report.VideoBytes, "unique video bytes only");
                Equal(32L, report.AudioBytes, "unique matching microphone bytes only");
                True(report.MicrophoneFound, "matched camera microphone reported");
                True(report.Milliseconds >= 1 && report.Milliseconds <= 120000, "monotonic elapsed bounds");
                True(services.Capture.Disposed, "microphone disposed after benchmark");
                bridge.HandleHello(hello, "4k webcam");
                Equal(1, services.Timers.Count, "completed hello replay does not restart");
                services.Timers[0].Fire();
                Equal(1, services.Sent.Count, "timer replay does not resend report");
                True(bridge.HandleVideo(early), "completed benchmark bytes remain excluded from clipboard");
            }
        }
    }

    private static void CheckRetiredSessions()
    {
        using (Control owner = new Control())
        {
            FakeServices services = new FakeServices { Endpoints = new AudioEndpoint[] { new AudioEndpoint("virtual-camera", "Microphone (4k webcam)") } };
            ClientAudioBridge bridge = new ClientAudioBridge(owner, services);
            BridgeHello old = Hello();
            bridge.HandleHello(old, "4k webcam");
            FakeCapture oldCapture = services.Capture;
            FakeTimer oldTimer = services.Timers[0];
            BridgeHello next = Hello();
            bridge.HandleHello(next, "4k webcam");
            True(oldCapture.Disposed && oldTimer.Disposed, "new session retires old capture and timer");
            oldCapture.Feed(AudioModem.Encode(BridgeControl.Single(old.AudioTest, new byte[32])));
            oldTimer.Fire();
            Equal(0, services.Sent.Count, "retired callbacks cannot send report");
            True(bridge.HandleVideo(Packet(old.VideoTest, 0, 256)), "old session benchmark data remains consumed");
            True(bridge.HandleVideo(Packet(next.VideoTest, 0, 256)), "new session benchmark data counted");
            services.Timers[1].Fire();
            BridgeReport report;
            True(BridgeControl.TryReport(services.Sent[0], next.Session, out report), "new session report survives old callback");
            Equal(0L, report.AudioBytes, "old audio never counted in new session");
            BridgeHello final = Hello();
            bridge.HandleHello(final, "4k webcam");
            FakeCapture finalCapture = services.Capture;
            FakeTimer finalTimer = services.Timers[2];
            bridge.Dispose(); bridge.Dispose();
            True(finalCapture.Disposed && finalTimer.Disposed, "dispose releases benchmark resources");
            finalCapture.Feed(AudioModem.Encode(BridgeControl.Single(final.AudioTest, new byte[32])));
            finalTimer.Fire();
            Equal(1, services.Sent.Count, "disposed callbacks cannot send report");
            True(bridge.HandleVideo(Packet(final.VideoTest, 0, 256)), "known speed packets remain consumed during shutdown race");
            int enabled = services.Enabled.Count;
            bridge.HandleHello(Hello(), "4k webcam");
            Equal(enabled, services.Enabled.Count, "disposed bridge ignores announcements");
        }
    }

    private static void CheckMissingMicrophone()
    {
        using (Control owner = new Control())
        {
            FakeServices services = new FakeServices { Endpoints = new AudioEndpoint[] { new AudioEndpoint("physical", "Microphone Array (Realtek Audio)") } };
            using (ClientAudioBridge bridge = new ClientAudioBridge(owner, services))
            {
                BridgeHello hello = Hello();
                bridge.HandleHello(hello, "4k webcam");
                Equal(0, services.Captures, "no fallback microphone opened");
                bridge.HandleVideo(Packet(hello.VideoTest, 0, 256));
                services.Timers[0].Fire();
                BridgeReport report;
                True(BridgeControl.TryReport(services.Sent[0], hello.Session, out report), "video-only report remains valid");
                True(!report.MicrophoneFound && report.AudioBytes == 0 && report.VideoBytes == 256, "missing microphone explicit in report");
                BridgeHello noTest = new BridgeHello { Session = hello.Session, Seconds = 0 };
                bridge.HandleHello(noTest, "4k webcam");
                Equal(1, services.Enumerations, "ordinary announcement does not open microphone");
            }
        }
    }

    private static void CheckFreshBenchmarkSameSession()
    {
        using (Control owner = new Control())
        {
            FakeServices services = new FakeServices();
            using (ClientAudioBridge bridge = new ClientAudioBridge(owner, services))
            {
                BridgeHello first = Hello();
                bridge.HandleHello(first, "4k webcam");
                bridge.HandleVideo(Packet(first.VideoTest, 0, 256));
                services.Timers[0].Fire();
                BridgeHello fresh = Hello();
                fresh.Session = first.Session;
                bridge.HandleHello(fresh, "4k webcam");
                Equal(2, services.Timers.Count, "a fresh test ID starts within the same clipboard session");
                Equal(20000, services.Timers[1].Milliseconds, "each fresh benchmark measures twenty seconds");
                True(bridge.HandleVideo(Packet(first.VideoTest, 1, 256)), "prior test data remains excluded during a fresh test");
                bridge.HandleVideo(Packet(fresh.VideoTest, 0, 256));
                bridge.HandleVideo(Packet(fresh.VideoTest, 0, 256));
                services.Timers[1].Fire();
                Equal(2, services.Sent.Count, "one report per distinct benchmark");
                BridgeReport report;
                True(BridgeControl.TryReport(services.Sent[1], fresh.Session, out report), "fresh report uses unchanged session");
                Equal(fresh.VideoTest, report.Test, "fresh report uses new test ID");
                Equal(256L, report.VideoBytes, "fresh counters exclude prior test and duplicates");
                services.Timers[0].Fire();
                Equal(2, services.Sent.Count, "retired timer cannot replace a fresh report");
            }
        }
    }

    private static void CheckConstruction()
    {
        using (Control owner = new Control())
        {
            Throws<ArgumentNullException>(delegate { new ClientAudioBridge(null, new FakeServices()); }, "owner required");
            Throws<ArgumentNullException>(delegate { new ClientAudioBridge(owner, (ClientAudioBridgeServices)null); }, "services required");
            Throws<ArgumentNullException>(delegate { new ClientAudioBridge(owner, (AudioClipboardSender)null); }, "sender required");
            FakeServices services = new FakeServices();
            using (ClientAudioBridge bridge = new ClientAudioBridge(owner, services))
            {
                bridge.HandleHello(null, "4k webcam");
                bridge.HandleHello(new BridgeHello(), "4k webcam");
                True(!bridge.HandleVideo(null), "null packet ignored");
                Equal(0, services.Enabled.Count, "invalid announcement does not enable sender");
            }
        }
    }

    private static void CheckTiledBenchmarkEpochs()
    {
        using (Control owner = new Control())
        {
            FakeServices services = new FakeServices();
            using (ClientAudioBridge bridge = new ClientAudioBridge(owner, services))
            {
                BridgeHello hello = Hello(); hello.Seconds = 60;
                Packet announcement = BridgeControl.Single(hello.Session, BridgeControl.Hello(hello.Session, hello.VideoTest, hello.AudioTest, 60));
                True(!bridge.HandleVideo(announcement), "sweep announcement continues to the STA handler");
                Guid earlyId = BridgeControl.BenchmarkEpoch(hello.VideoTest, 1);
                True(bridge.HandleVideo(Packet(earlyId, 0, 256)), "announced epoch traffic is consumed before the STA handler starts the timer");
                bridge.HandleHello(hello, "4k webcam");
                Equal(60000, services.Timers[0].Milliseconds, "profile sweep uses the complete announced receive window");
                byte[] payload = new byte[VisualProtocol.GetFramePayloadBytes(VideoProfile.Compact640)]; new Random(149).NextBytes(payload);
                for (int epoch = 1; epoch <= 2; epoch++)
                using (System.Drawing.Bitmap frame = VisualProtocol.EncodePacketFrame(BridgeControl.BenchmarkEpoch(hello.VideoTest, epoch), 0, payload, VideoProfile.Compact640))
                {
                    bridge.ObserveFrame(320, 240);
                    Packet[] packets = VisualProtocol.DecodePackets(frame);
                    Equal(7, packets.Length, "one compact frame contains seven independently validated packets");
                    foreach (Packet packet in packets)
                    {
                        True(bridge.HandleVideo(packet), "every verified tile is consumed as benchmark data");
                        True(bridge.HandleVideo(packet), "duplicate tiles remain consumed");
                    }
                }
                // A different actual delivered size supersedes previous geometry;
                // seven decoded packets above count as one frame, not seven.
                bridge.ObserveFrame(640, 480);
                services.Timers[0].Fire();
                BridgeReport report;
                True(BridgeControl.TryReport(services.Sent[0], hello.Session, out report), "sweep report is well formed");
                Equal((long)payload.Length * 2, report.VideoBytes, "same packet indices in different epochs count once per epoch");
                Equal((long)payload.Length * 2, report.ProfileBytes[(int)VideoProfile.Compact640], "only the matching profile receives the verified tile bytes");
                Equal(0L, report.ProfileBytes[(int)VideoProfile.Compatible], "pre-STA epoch was reserved but was not counted outside the receive window");
                Equal(4000, report.ProfileMilliseconds[(int)VideoProfile.Compact640], "per-profile sweep interval is explicitly reported");
                Equal(640, report.FrameWidth, "report exposes actual delivered frame width");
                Equal(480, report.FrameHeight, "report exposes actual delivered frame height");
                Equal(3, report.Frames, "frame count is independent of packet batching");
                True(bridge.HandleVideo(Packet(BridgeControl.BenchmarkEpoch(hello.VideoTest, 2000), 0, 256)), "future epochs of a completed benchmark never become clipboard data");
                Equal(1, services.Sent.Count, "late benchmark epochs do not create a second report");
                True(!bridge.HandleVideo(Packet(Guid.NewGuid(), 0, 256)), "ordinary clipboard transfers remain eligible for normal processing");
                BridgeHello fresh = Hello();
                bridge.HandleHello(fresh, "4k webcam");
                True(bridge.HandleVideo(Packet(BridgeControl.BenchmarkEpoch(hello.VideoTest, 2001), 1, 256)), "old session epochs remain excluded during a new session");
                services.Timers[1].Fire();
                True(BridgeControl.TryReport(services.Sent[1], fresh.Session, out report), "new session report uses its own session binding");
                Equal(0L, report.VideoBytes, "old epochs cannot increase a new session's throughput");
            }
        }
    }

    private static BridgeHello Hello() { return new BridgeHello { Session = Guid.NewGuid(), VideoTest = Guid.NewGuid(), AudioTest = Guid.NewGuid(), Seconds = 20 }; }

    private static void CheckDeliveryReceipts()
    {
        using (Control owner = new Control())
        {
            FakeServices services = new FakeServices();
            using (ClientAudioBridge bridge = new ClientAudioBridge(owner, services))
            {
                Guid session = Guid.NewGuid(), transfer = Guid.NewGuid();
                BridgeHello hello = OrdinaryHello(session, transfer, 8192);
                bridge.HandleHello(hello, "unused");
                Equal(0, services.Sent.Count, "An advertised transfer is never acknowledged before publication");
                bridge.NotifyPublished(transfer, 8192);
                Equal(1, services.Sent.Count, "Successful publication queues a receipt immediately");
                DeliveryReceipt receipt;
                True(DeliveryReceipt.TryDecode(services.Sent[0], session, out receipt), "Delivery receipt has a valid session and CRC");
                Equal(transfer, receipt.Transfer, "Receipt identifies the published transfer");
                Equal(8192, receipt.EncodedBytes, "Receipt identifies its exact encoded length");
                bridge.HandleHello(hello, "unused");
                services.Now = 7999; bridge.HandleHello(hello, "unused");
                Equal(1, services.Sent.Count, "Repeated video announcements do not flood reverse audio");
                services.Now = 8000; bridge.HandleHello(hello, "unused");
                Equal(2, services.Sent.Count, "A lost receipt is retried after eight seconds");
                services.Now = 16000; bridge.HandleHello(OrdinaryHello(session, Guid.Empty, 0), "unused");
                Equal(2, services.Sent.Count, "Server stopping its transfer advertisement stops receipts");
                Guid other = Guid.NewGuid();
                bridge.HandleHello(OrdinaryHello(session, other, 9000), "unused");
                Equal(2, services.Sent.Count, "Receipt for an older copy never acknowledges its replacement");
                bridge.NotifyPublished(other, 8999);
                Equal(2, services.Sent.Count, "Mismatched encoded length cannot acknowledge a transfer");
                bridge.NotifyPublished(other, 9000);
                Equal(3, services.Sent.Count, "Replacement publication acknowledges its own matching advertisement");
                Guid nextSession = Guid.NewGuid();
                services.Now = 24000; bridge.HandleHello(OrdinaryHello(nextSession, other, 9000), "unused");
                Equal(3, services.Sent.Count, "Publication bound to an old server session cannot be rebound to a new one");
                bridge.Dispose(); bridge.NotifyPublished(other, 9000);
                Equal(3, services.Sent.Count, "A disposed bridge never sends delivery receipts");
            }
        }
    }

    private static void CheckFastDeliveryReceipts()
    {
        using (Control owner = new Control())
        {
            var services = new FakeServices();
            using (var bridge = new ClientAudioBridge(owner, services))
            {
                Guid session = Guid.NewGuid(), transfer = Guid.NewGuid();
                BridgeHello legacy = OrdinaryHello(session, transfer, 8192);
                bridge.HandleHello(legacy, "unused");
                bridge.NotifyPublished(transfer, 8192);
                Equal(1, services.Sent.Count, "Unnegotiated peer retains the full legacy receipt");
                BridgeHello modern = OrdinaryHello(session, transfer, 8192); modern.FastDeliveryReceipts = true;
                bridge.HandleHello(modern, "unused");
                Equal(1, services.FastReceipts.Count, "A delayed feature advertisement immediately upgrades an already queued slow receipt");
                True(DeliveryReceipt.SameToken(services.FastReceipts[0], DeliveryReceipt.FastToken(session, transfer, 8192)), "Fast token binds the actual published transfer");
                services.Now = 749; bridge.HandleHello(legacy, "unused");
                Equal(1, services.FastReceipts.Count, "Alternating compatibility hello does not downgrade or flood fast acknowledgements");
                services.Now = 750; bridge.HandleHello(legacy, "unused");
                Equal(2, services.FastReceipts.Count, "Negotiated compact receipt is retried if the video announcement continues");
                Equal(1, services.Sent.Count, "The initial fast retries do not queue a slower legacy receipt");
                Guid next = Guid.NewGuid(), nextTransfer = Guid.NewGuid();
                bridge.HandleHello(OrdinaryHello(next, nextTransfer, 100), "unused");
                bridge.NotifyPublished(nextTransfer, 100);
                Equal(2, services.Sent.Count, "A new legacy session resets the feature latch and retains compatibility");
                Equal(2, services.FastReceipts.Count, "A new session cannot reuse the prior peer capability");
            }
        }
    }

    private static void CheckLostFastReceiptsAndNativeFallback()
    {
        using (Control owner = new Control())
        {
            var services = new FakeServices();
            using (var bridge = new ClientAudioBridge(owner, services))
            {
                Guid session = Guid.NewGuid(), transfer = Guid.NewGuid();
                BridgeHello hello = OrdinaryHello(session, transfer, 107); hello.FastDeliveryReceipts = true;
                bridge.HandleHello(hello, "unused");
                Equal(0, services.FastReceipts.Count, "An advertised fast capability cannot acknowledge unpublished bytes");
                bridge.NotifyPublished(transfer, 107); // Deliberately lose the first two emitted tokens.
                services.Now = 749; bridge.HandleHello(hello, "unused");
                Equal(1, services.FastReceipts.Count, "Short retries retain a minimum interval rather than interrupting their own293ms burst");
                services.Now = 750; bridge.HandleHello(hello, "unused");
                Equal(2, services.FastReceipts.Count, "A lost first burst retries at750ms rather than8000ms");
                services.Now = 1500; bridge.HandleHello(hello, "unused");
                Equal(3, services.FastReceipts.Count, "Two lost bursts can recover after1500ms rather than16000ms");
                True(DeliveryReceipt.SameToken(services.FastReceipts[2], DeliveryReceipt.FastToken(session, transfer, 107)), "A retried token still identifies only the exact published transfer");
                services.Now = 2249; bridge.HandleHello(hello, "unused");
                Equal(0, services.Fallbacks.Count, "Native fallback waits until the third short burst had time to complete");
                services.Now = 2250; bridge.HandleHello(hello, "unused");
                Equal(1, services.Fallbacks.Count, "If all three short attempts are lost, selected-modem fallback is queued at2250ms");
                DeliveryReceipt receipt;
                True(DeliveryReceipt.TryDecode(services.Fallbacks[0], session, out receipt) && receipt.Transfer == transfer && receipt.EncodedBytes == 107,
                    "Native fallback carries the same exact session, transfer and verified encoded size");
                services.Now = 6249; bridge.HandleHello(hello, "unused");
                Equal(3, services.FastReceipts.Count, "After initial recovery, audio retries back off to four seconds");
                services.Now = 6250; bridge.HandleHello(hello, "unused");
                Equal(4, services.FastReceipts.Count, "Persistent loss alternates an occasional short retry with native fallback");
                services.Now = 10250; bridge.HandleHello(hello, "unused");
                Equal(2, services.Fallbacks.Count, "A persistent FSK failure can retry the selected modem too");
                services.Now = 14250; bridge.HandleHello(OrdinaryHello(session, Guid.Empty, 0), "unused");
                Equal(4, services.FastReceipts.Count, "A server confirmation stopping the advertisement prevents more short audio");
                Equal(2, services.Fallbacks.Count, "A server confirmation also prevents more native fallback audio");
                Guid replacement = Guid.NewGuid(); hello = OrdinaryHello(session, replacement, 107); hello.FastDeliveryReceipts = true;
                bridge.HandleHello(hello, "unused");
                Equal(4, services.FastReceipts.Count, "Replacing a transfer cannot reuse an old publication with the same byte length");
                bridge.NotifyPublished(replacement, 107);
                Equal(5, services.FastReceipts.Count, "A new complete publication starts a fresh immediate short retry budget");
                True(!DeliveryReceipt.SameToken(services.FastReceipts[4], services.FastReceipts[0]), "Identical content length cannot create a false old-transfer acknowledgement");
                bridge.Dispose(); services.Now += 5000; bridge.HandleHello(hello, "unused");
                Equal(5, services.FastReceipts.Count, "Disposed bridge never schedules a short retry");
            }
        }
    }

    private static void CheckUnannouncedAndBenchmarkedReceipts()
    {
        using (Control owner = new Control())
        {
            FakeServices services = new FakeServices();
            using (ClientAudioBridge bridge = new ClientAudioBridge(owner, services))
            {
                Guid session = Guid.NewGuid(), transfer = Guid.NewGuid();
                bridge.NotifyPublished(transfer, 4096);
                Equal(0, services.Sent.Count, "Publication before the first hello waits for a session");
                bridge.HandleHello(OrdinaryHello(session, Guid.NewGuid(), 4096), "unused");
                Equal(0, services.Sent.Count, "A first hello advertising another transfer cannot bind a pending receipt");
                bridge.HandleHello(OrdinaryHello(session, transfer, 4096), "unused");
                Equal(1, services.Sent.Count, "A delayed matching hello binds and sends the already published receipt");
                Guid benchmarkTransfer = Guid.NewGuid();
                FileBenchmarkCommand command = new FileBenchmarkCommand
                {
                    Session = session, Test = Guid.NewGuid(), Profile = VideoProfile.Compatible,
                    FileSize = 1024, Seed = 7, Phase = FileBenchmarkPhase.Prepare
                };
                True(bridge.HandleVideo(BridgeControl.Single(session, FileBenchmarkProtocol.Command(command))), "File benchmark preparation is consumed");
                True(services.BenchmarkMode, "The benchmark owns reverse control audio");
                int preparedReports = services.Sent.Count;
                bridge.NotifyPublished(benchmarkTransfer, 5000);
                bridge.HandleHello(OrdinaryHello(session, benchmarkTransfer, 5000), "unused");
                Equal(preparedReports, services.Sent.Count, "Receipt cannot interrupt an active file benchmark");
                command.Phase = FileBenchmarkPhase.Cancel;
                bridge.HandleVideo(BridgeControl.Single(session, FileBenchmarkProtocol.Command(command)));
                True(!services.BenchmarkMode, "Cancel releases benchmark control audio");
                int cancelledReports = services.Sent.Count;
                bridge.HandleHello(OrdinaryHello(session, benchmarkTransfer, 5000), "unused");
                Equal(cancelledReports + 1, services.Sent.Count, "Matching ordinary hello resumes a held receipt after the benchmark");
                DeliveryReceipt receipt;
                True(DeliveryReceipt.TryDecode(services.Sent[services.Sent.Count - 1], session, out receipt) && receipt.Transfer == benchmarkTransfer,
                    "The resumed receipt remains bound to the published transfer and session");
                Equal(0, services.Enumerations, "Ordinary receipts and file tests never enumerate microphones");
            }
        }
    }

    private static BridgeHello OrdinaryHello(Guid session, Guid transfer, int bytes)
    { return new BridgeHello { Session = session, ForwardTransfer = transfer, ForwardEncodedBytes = bytes }; }

    private static void CheckControlDiagnostics()
    {
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client-control-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "fixture.log");
        try
        {
            True(DiagnosticLog.Initialize(path, 1024 * 1024, 1, 128), "Diagnostic fixture starts at an explicit isolated location");
            using (Control owner = new Control())
            {
                FakeServices services = new FakeServices();
                using (ClientAudioBridge bridge = new ClientAudioBridge(owner, services))
                {
                    Guid session = Guid.NewGuid(), transfer = Guid.NewGuid();
                    BridgeHello hello = OrdinaryHello(session, transfer, 4096);
                    hello.CameraName = "private-untrusted-string-not-for-logs";
                    Packet announcement = BridgeControl.Single(session, BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0, hello.CameraName, transfer, 4096));
                    for (int i = 0; i < 32; i++)
                    {
                        bridge.HandleVideo(announcement);
                        bridge.HandleHello(hello, "private-untrusted-string-not-for-logs");
                    }
                    Equal(session, bridge.SessionId, "Diagnostic state exposes the negotiated session without enabling other I/O");
                    services.Now = 5000; bridge.HandleHello(hello, "unused");
                    bridge.NotifyPublished(Guid.NewGuid(), 4096);
                    Equal(0, services.Sent.Count, "Guard logging cannot bypass the matching-transfer requirement");
                }
            }
            True(DiagnosticLog.Shutdown(), "Diagnostic fixture flushes before inspection");
            string contents = File.ReadAllText(path);
            True(!contents.Contains("private-untrusted-string-not-for-logs"), "Control logs contain bounded metadata rather than arbitrary strings from video announcements");
            int recognized = 0, applied = 0, waiting = 0; bool suppressionCount = false;
            var parser = new JavaScriptSerializer();
            foreach (string line in File.ReadAllLines(path))
            {
                var record = parser.Deserialize<Dictionary<string, object>>(line);
                string eventName = (string)record["event"];
                if (eventName == "receiver.control.hello_recognized") recognized++;
                if (eventName == "client.bridge.hello")
                { applied++; if (Convert.ToInt32(record["suppressed_duplicates"]) == 31) suppressionCount = true; }
                if (eventName == "client.delivery.receipt_waiting") waiting++;
            }
            Equal(1, recognized, "Repeated decoded Hello frames share one diagnostic event within the throttle window");
            Equal(2, applied, "An unchanged Hello is logged again after five seconds");
            True(suppressionCount && waiting >= 1, "Diagnostics expose suppressed duplicate counts and the reason a receipt cannot start");
        }
        finally
        {
            DiagnosticLog.Shutdown();
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
    private static Packet Packet(Guid transfer, int index, int size) { return new Packet { TransferId = transfer, Index = index, Count = 100, TotalLength = 25600, Data = new byte[size], Profile = VideoProfile.Compatible, ChunkSize = 256 }; }
    private static void True(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    private static void Same(object expected, object actual, string reason) { checks++; if (!Object.ReferenceEquals(expected, actual)) throw new Exception(reason); }
    private static void Equal<T>(T expected, T actual, string reason) { checks++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(reason + ": expected " + expected + ", got " + actual); }
    private static void Throws<T>(Action action, string reason) where T : Exception { checks++; try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name + ": " + reason); }

    private static void CheckAudioRetryPolicySessions()
    {
        using (Control owner = new Control())
        {
            var services = new FakeServices();
            using (var bridge = new ClientAudioBridge(owner, services))
            {
                Guid first = Guid.NewGuid(), next = Guid.NewGuid();
                bridge.ConfigureAudioRetries(new BridgeCameraFormat { Session = first, AudioRetries = 0 });
                bridge.HandleHello(new BridgeHello { Session = first }, null);
                Equal(0, services.RetryPolicy, "A policy arriving before Hello applies to that exact session");
                bridge.ConfigureAudioRetries(new BridgeCameraFormat { Session = next, AudioRetries = 10 });
                Equal(0, services.RetryPolicy, "A future or stale other-session format cannot change active audio retry policy");
                bridge.HandleHello(new BridgeHello { Session = first }, null);
                Equal(0, services.RetryPolicy, "Repeated current Hello preserves its own policy after another session was announced");
                bridge.HandleHello(new BridgeHello { Session = next }, null);
                Equal(10, services.RetryPolicy, "The new session applies only its matching previously announced policy");
                bridge.HandleHello(new BridgeHello { Session = Guid.NewGuid() }, null);
                Equal(2, services.RetryPolicy, "An unannounced legacy session gets the bounded two-retry default");
            }
        }
    }

    private static void CheckFileBenchmarkReservationLifetime()
    {
        using (Control owner = new Control())
        {
            var services = new FakeServices();
            using (var bridge = new ClientAudioBridge(owner, services))
            {
                True(!bridge.HasFileBenchmark, "A fresh bridge does not reserve the camera for a file benchmark");
                var command = new FileBenchmarkCommand { Session = Guid.NewGuid(), Test = Guid.NewGuid(), Profile = VideoProfile.Dense,
                    FileSize = 1024, Seed = 7, Phase = FileBenchmarkPhase.Prepare };
                bridge.HandleVideo(BridgeControl.Single(command.Session, FileBenchmarkProtocol.Command(command)));
                True(bridge.HasFileBenchmark, "Prepare reserves capture while its ready report is pending");
                command.Phase = FileBenchmarkPhase.Start;
                bridge.HandleVideo(BridgeControl.Single(command.Session, FileBenchmarkProtocol.Command(command)));
                True(bridge.HasFileBenchmark, "A receiving file benchmark keeps its capture reservation");
                var data = new byte[1024]; BenchmarkData.Fill(command.Seed, 0, data, 0, data.Length);
                bridge.HandleVideo(new Packet { TransferId = FileBenchmarkProtocol.SegmentTransferId(command.Test, 0), Index = 0, Count = 1,
                    TotalLength = data.Length, Data = data, Profile = command.Profile, ChunkSize = VisualProtocol.GetChunkSize(command.Profile) });
                True(bridge.HasFileBenchmark, "A completed file remains reserved until its final report can be delivered");
                FileBenchmarkReport report;
                True(FileBenchmarkProtocol.TryReport(services.Sent[services.Sent.Count - 1], command.Session, out report) && report.Status == FileBenchmarkStatus.Completed,
                    "The terminal reservation protects a real fully verified completion report");
                bridge.HandleHello(new BridgeHello { Session = command.Session }, null);
                True(!bridge.HasFileBenchmark, "An ordinary hello releases a terminal file reservation when Cancel was lost");
                command.Test = Guid.NewGuid(); command.Phase = FileBenchmarkPhase.Prepare;
                bridge.HandleVideo(BridgeControl.Single(command.Session, FileBenchmarkProtocol.Command(command)));
                True(bridge.HasFileBenchmark, "A later test acquires its own reservation");
                command.Phase = FileBenchmarkPhase.Cancel;
                bridge.HandleVideo(BridgeControl.Single(command.Session, FileBenchmarkProtocol.Command(command)));
                True(!bridge.HasFileBenchmark, "Explicit Cancel releases the file reservation");
                command.Test = Guid.NewGuid(); command.Phase = FileBenchmarkPhase.Prepare;
                bridge.HandleVideo(BridgeControl.Single(command.Session, FileBenchmarkProtocol.Command(command)));
                bridge.Dispose(); True(!bridge.HasFileBenchmark, "Disposal always releases the file reservation");
            }
        }
    }

    private static void CheckFileReadyActualGeometry()
    {
        using (Control owner = new Control())
        {
            var services = new FakeServices { DeferDispatch = true };
            var command = new FileBenchmarkCommand { Session = Guid.NewGuid(), Test = Guid.NewGuid(), Profile = VideoProfile.HighDefinitionRobust,
                FileSize = 1024, Seed = 7, Phase = FileBenchmarkPhase.Prepare };
            using (var bridge = new ClientAudioBridge(owner, services))
            {
                bridge.ObserveFrame(640, 480);
                bridge.HandleVideo(BridgeControl.Single(command.Session, FileBenchmarkProtocol.Command(command)));
                bridge.ObserveFrame(1920, 1080);
                Equal(0, services.Sent.Count, "Ready performs no audio I/O before its owner dispatch runs");
                services.Flush(); services.DeferDispatch = false;
                FileBenchmarkReport report;
                True(FileBenchmarkProtocol.TryReport(services.Sent[0], command.Session, out report) && report.Status == FileBenchmarkStatus.Ready &&
                    report.FrameWidth == 1920 && report.FrameHeight == 1080 && report.Frames == 0,
                    "The first Ready uses the latest actual frame geometry even when STA dispatch was delayed; its pre-run frame count may be zero");
                for (int i = 0; i < 40; i++)
                {
                    bridge.ObserveFrame(1920, 1080);
                    bridge.HandleVideo(BridgeControl.Single(command.Session, FileBenchmarkProtocol.Command(command)));
                }
                Equal(1, services.Sent.Count, "Repeated Prepare and stable actual frames cannot interrupt their own Ready audio");
                services.Now = 500; bridge.ObserveFrame(1280, 720);
                services.Now = 1499; bridge.ObserveFrame(1280, 720);
                Equal(1, services.Sent.Count, "An actual geometry change is debounced rather than replacing Ready on every frame");
                services.Now = 1500; bridge.ObserveFrame(1280, 720);
                True(services.Sent.Count == 2 && FileBenchmarkProtocol.TryReport(services.Sent[1], command.Session, out report) &&
                    report.FrameWidth == 1280 && report.FrameHeight == 720 && report.Status == FileBenchmarkStatus.Ready,
                    "Changed actual capture dimensions produce updated Ready before the ordinary eight-second repeat");
                bridge.ObserveFrame(0, 0); bridge.ObserveFrame(1920, 0);
                Equal(2, services.Sent.Count, "Invalid dimensions cannot replace positive actual Ready geometry");
                services.Now = 9500;
                bridge.HandleVideo(BridgeControl.Single(command.Session, FileBenchmarkProtocol.Command(command)));
                Equal(3, services.Sent.Count, "An unchanged valid Ready still repeats after eight seconds for audio-loss recovery");
                services.DeferDispatch = true; command.Test = Guid.NewGuid();
                bridge.HandleVideo(BridgeControl.Single(command.Session, FileBenchmarkProtocol.Command(command)));
                bridge.Dispose(); services.Flush();
                Equal(3, services.Sent.Count, "A stale queued Prepare cannot publish Ready after its capture bridge is disposed");
            }
            services.DeferDispatch = false; services.Sent.Clear();
            using (var bridge = new ClientAudioBridge(owner, services))
            {
                command.Test = Guid.NewGuid();
                bridge.HandleVideo(BridgeControl.Single(command.Session, FileBenchmarkProtocol.Command(command)));
                Equal(0, services.Sent.Count, "A replacement capture bridge cannot inherit geometry from the old capture or emit zero-size Ready");
                bridge.ObserveFrame(0, 0);
                Equal(0, services.Sent.Count, "Unknown capture geometry continues waiting for an actual frame");
                bridge.ObserveFrame(640, 480);
                FileBenchmarkReport report;
                True(services.Sent.Count == 1 && FileBenchmarkProtocol.TryReport(services.Sent[0], command.Session, out report) &&
                    report.Status == FileBenchmarkStatus.Ready && report.FrameWidth == 640 && report.FrameHeight == 480,
                    "The first positive actual frame releases a deferred Ready immediately without waiting eight seconds");
                for (int i = 0; i < 20; i++) bridge.ObserveFrame(640, 480);
                Equal(1, services.Sent.Count, "A deferred Ready becomes rate-limited after its first successful queue");
            }
        }
    }

    private sealed class FakeServices : ClientAudioBridgeServices
    {
        internal readonly List<Guid> Enabled = new List<Guid>();
        internal readonly List<byte[]> Sent = new List<byte[]>();
        internal readonly List<byte[]> FastReceipts = new List<byte[]>();
        internal readonly List<byte[]> Fallbacks = new List<byte[]>();
        internal readonly List<FakeTimer> Timers = new List<FakeTimer>();
        internal AudioEndpoint[] Endpoints = new AudioEndpoint[0];
        internal int Enumerations, Captures;
        internal string CapturedId;
        internal FakeCapture Capture;
        internal long Now;
        internal bool BenchmarkMode, DeferDispatch;
        private readonly Queue<Action> dispatch = new Queue<Action>();
        internal int RetryPolicy = -1;
        internal FakeServices() : base(null) { }
        internal override long Milliseconds { get { return Now; } }
        internal override void Enable(Guid session) { Enabled.Add(session); }
        internal override void Send(byte[] bytes, Guid transferId) { Sent.Add(bytes); }
        internal override void SendFastReceipt(Guid session, Guid transfer, int bytes) { FastReceipts.Add(DeliveryReceipt.FastToken(session, transfer, bytes)); }
        internal override void SendDeliveryReceiptFallback(Guid session, Guid transfer, int bytes) { Fallbacks.Add(DeliveryReceipt.Encode(session, transfer, bytes)); }
        internal override void SetBenchmarkMode(bool active) { BenchmarkMode = active; }
        internal override void SetAdditionalRetries(int retries) { RetryPolicy = retries; }
        internal override void Dispatch(Control owner, Action action) { if (DeferDispatch) dispatch.Enqueue(action); else action(); }
        internal void Flush() { while (dispatch.Count != 0) dispatch.Dequeue()(); }
        internal override AudioEndpoint[] EnumerateMicrophones() { Enumerations++; return Endpoints; }
        internal override IDisposable StartMicrophone(string endpointId, Action<float[]> samples) { Captures++; CapturedId = endpointId; Capture = new FakeCapture(samples); return Capture; }
        internal override IDisposable Schedule(Action action, int milliseconds) { FakeTimer timer = new FakeTimer(action, milliseconds); Timers.Add(timer); return timer; }
        internal override void Queue(Action action) { action(); }
    }
    private sealed class FakeTimer : IDisposable
    {
        private readonly Action callback;
        internal readonly int Milliseconds;
        internal bool Disposed;
        internal FakeTimer(Action callback, int milliseconds) { this.callback = callback; Milliseconds = milliseconds; }
        internal void Fire() { callback(); } // Also exercise the callback after disposal/replay.
        public void Dispose() { Disposed = true; }
    }
    private sealed class FakeCapture : IDisposable
    {
        private readonly Action<float[]> samples;
        internal bool Disposed;
        internal FakeCapture(Action<float[]> samples) { this.samples = samples; }
        internal void Feed(float[] values) { samples(values); } // Exercise stale callbacks too.
        public void Dispose() { Disposed = true; }
    }
}
