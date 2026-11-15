// Pure coordination: synthetic payloads, capture, dispatch, timers, hooks and clipboard.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

internal static class ServerAudioBridgeTests
{
    private static int checks;
    [STAThread]
    private static int Main()
    {
        try
        {
            ConstructionAndCaptureLifetime();
            AutomaticAllOutputsSeparatesAndDeduplicates();
            AutomaticAllOutputsHandlesEnumerationFailure();
            NativeStartupFailureRemainsVisible();
            NativeStartupStatusRelocalizes();
            NativeRuntimeStatusRelocalizes();
            NativeErrorStatusRelocalizes();
            NativeRouteFailuresAreIndependent();
            CaptureFailureWinsLateNativeNotification();
            ExplicitMultipleCaptureSeparatesStreamsAndDeduplicates();
            MultichunkDuplicatesReportsAndEcho();
            SessionAndSupersession();
            ClipboardRetriesAndFailure();
            DecodeRetriesAndIncomingMetadata();
            RepeatedFileDropsCompleteIndependently();
            PartialInactivityIsBounded();
            LiveRetryPolicyUpdatesPendingTransfer();
            StaleDecodeAndDispose();
            CaptureErrorsCancelPendingPaste();
            FileReportsThroughSyntheticAudio();
            DeliveryReceiptsThroughSyntheticAudio();
            IndependentAudioProbe();
            AudioProbeCallbacksAreBoundedAndIsolated();
            BenchmarkRetiresPendingClipboard();
            ExplicitVerifiedCaptureRoutes();
            HeartbeatIsActivityNotDelivery();
            HeartbeatFreshnessControlsInactivity();
            EarlyParallelPacketsCompleteFirstPass();
            EarlyPacketBoundsAndRetirement();
            Console.WriteLine("Server audio bridge checks passed: " + checks + " assertions (synthetic multi-packet payloads, sessions, duplicates, progress, reports, echo callback, busy clipboard, supersession, capture and disposal). No audio, clipboard, files, hooks, timers or windows were opened.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void AutomaticAllOutputsSeparatesAndDeduplicates()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Services.RenderEndpoints = new[] { new AudioEndpoint("speakers", "Speakers"), new AudioEndpoint("headphones", "Headphones"),
                new AudioEndpoint("speakers", "Duplicate"), null, new AudioEndpoint(null, "Invalid"), new AudioEndpoint("", "Empty") };
            fixture.Bridge.Start("all_render"); fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
            Equal(1, fixture.Services.RenderEnumerations, "All-output reception enumerates active endpoints once on the queued worker.");
            Equal(2, fixture.Services.CaptureStarts, "Every distinct valid output gets an independent capture.");
            True(fixture.Services.RouteSamples.ContainsKey("speakers") && fixture.Services.RouteSamples.ContainsKey("headphones") && !fixture.Services.RouteSamples.ContainsKey("all_render"),
                "The all-output sentinel never reaches the native capture API.");
            Packet[] packets = Packets(fixture.Session, Guid.NewGuid(), "same return copy heard by two outputs");
            foreach (Packet packet in packets)
            {
                float[] samples = AudioModem.Encode(packet);
                fixture.Services.RouteSamples["headphones"](samples);
                fixture.Services.RouteSamples["speakers"](samples);
            }
            fixture.Services.DrainUi();
            Equal(1, fixture.Services.Publications.Count, "Duplicate audio on several outputs publishes the clipboard once.");
            Equal(1, fixture.Published.Count, "Duplicate audio emits one clipboard completion.");
            fixture.Services.RouteErrors["speakers"](new Exception("synthetic unplugged speakers")); fixture.Services.DrainUi();
            Packet[] next = Packets(fixture.Session, Guid.NewGuid(), "copy on the remaining healthy output");
            foreach (Packet packet in next) fixture.Services.RouteSamples["headphones"](AudioModem.Encode(packet));
            fixture.Services.DrainUi();
            Equal(2, fixture.Services.Publications.Count, "One failed output leaves the other output receiving.");
            fixture.Bridge.Dispose(); fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
            foreach (FakeCapture capture in fixture.Services.AllCaptures) Equal(1, capture.Disposals, "All-output captures are released exactly once.");
        }
    }

    private static void AutomaticAllOutputsHandlesEnumerationFailure()
    {
        foreach (bool fail in new[] { false, true })
        using (Fixture fixture = new Fixture())
        {
            fixture.Services.RenderEndpoints = new AudioEndpoint[0];
            if (fail) fixture.Services.RenderEnumerationError = new InvalidOperationException("synthetic enumeration failure");
            fixture.Bridge.Start("all_render"); fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
            Equal(0, fixture.Services.CaptureStarts, "Empty or failed all-output enumeration cannot capture a fabricated endpoint or silently fall back.");
            True(fixture.Statuses.Count > 0 && !fixture.Statuses[fixture.Statuses.Count - 1].Contains("enabled"), "The user receives an unavailable status rather than a false ready status.");
        }
    }

    private static void NativeStartupFailureRemainsVisible()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Services.NativeModemsEnabled = true;
            fixture.Services.NativeFactory = delegate { throw new FileNotFoundException("synthetic native startup load failure"); };
            fixture.Services.OnReceiverCreated = JoinNativeWorkers;
            fixture.Bridge.Start("native-startup-output"); fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
            True(fixture.Statuses.Count != 0 && fixture.Statuses[fixture.Statuses.Count - 1].Contains("quiet") && fixture.Statuses[fixture.Statuses.Count - 1].Contains("FSK") && fixture.Statuses[fixture.Statuses.Count - 1].Contains("native-startup-output"),
                "native startup failure stays visible in ordinary status after late generic capture-enabled status");
            Guid test = Guid.NewGuid();
            fixture.Services.Samples(AudioModem.Encode(BridgeControl.Single(Guid.NewGuid(), BridgeControl.Report(new BridgeReport { Session = fixture.Session, Test = test, Milliseconds = 1000, VideoBytes = 9 }))));
            fixture.Services.DrainUi();
            True(fixture.Reports.Exists(delegate(BridgeReport value) { return value.Test == test; }), "native failure must preserve the healthy FSK route");
        }
    }

    private static void JoinNativeWorkers(AudioReceiveMultiplexer mux)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (object worker in (Array)typeof(AudioReceiveMultiplexer).GetField("workers", flags).GetValue(mux))
            True(((Thread)worker.GetType().GetField("thread", flags).GetValue(worker)).Join(5000), "native startup completes before owner subscribes");
    }

    private static void NativeStartupStatusRelocalizes()
    {
        UiLanguage previous = UiText.Language;
        try
        {
            // These are external identities/errors, even when a caption has the same text.
            UiText.Get("Недоступен", "Unavailable");
            foreach (UiLanguage language in new[] { UiLanguage.Russian, UiLanguage.English })
            foreach (string endpoint in new string[] { null, "Unavailable" })
            using (Fixture fixture = new Fixture())
            using (var label = new Label())
            {
                UiText.SetLanguage(language);
                fixture.StatusChanged = delegate(string message) { UiText.SetKnownText(label, message); };
                fixture.Services.NativeModemsEnabled = true;
                fixture.Services.NativeFactory = delegate { throw new FileNotFoundException("Unavailable"); };
                fixture.Services.OnReceiverCreated = JoinNativeWorkers;
                fixture.Bridge.Start(endpoint); fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
                string russian = "Приём обратного звука включён для " + (endpoint == null ? "устройства Windows по умолчанию." : "выбранного устройства.") +
                    " Ограничения декодеров: quiet: недоступен (" + (endpoint ?? "устройство Windows по умолчанию") + "); aicodix: недоступен (" + (endpoint ?? "устройство Windows по умолчанию") + "). FSK остаётся доступен.";
                string english = "Return audio reception is enabled for the " + (endpoint == null ? "default Windows device." : "selected device.") +
                    " Decoder availability: quiet: unavailable (" + (endpoint ?? "default Windows device") + "); aicodix: unavailable (" + (endpoint ?? "default Windows device") + "). FSK remains available.";
                AssertStatusRelocalizes(fixture, label, language, russian, english, "startup generic status retains both native failures");
                fixture.Services.CaptureError(new Exception("Unavailable")); fixture.Services.DrainUi();
                AssertStatusRelocalizes(fixture, label, language, "Обратный звук: Unavailable", "Return audio: Unavailable", "capture failure retains literal external error");
            }
        }
        finally { UiText.SetLanguage(previous); }
    }

    private static void NativeRuntimeStatusRelocalizes()
    {
        UiLanguage previous = UiText.Language;
        try
        {
            UiText.Get("Недоступен", "Unavailable");
            foreach (UiLanguage language in new[] { UiLanguage.Russian, UiLanguage.English })
            using (Fixture fixture = new Fixture())
            using (var label = new Label())
            using (var failed = new AutoResetEvent(false))
            {
                UiText.SetLanguage(language);
                fixture.StatusChanged = delegate(string message) { UiText.SetKnownText(label, message); };
                fixture.Services.NativeModemsEnabled = true;
                fixture.Services.NativeFactory = delegate(string endpoint, AudioModemKind kind) { return new NativeDecoder(delegate {
                    if (kind == AudioModemKind.Quiet) throw new InvalidOperationException("Unavailable");
                    return new byte[0][];
                }); };
                fixture.Bridge.StartWithRoutes(new[] { "Unavailable", "two" }); fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
                foreach (string endpoint in new[] { "Unavailable", "two" })
                    fixture.Services.Receivers[endpoint].DecoderFailed += delegate(AudioModemKind kind, Exception error) { if (kind == AudioModemKind.Quiet) failed.Set(); };
                fixture.Services.RouteSamples["Unavailable"](new float[] { 0 });
                True(failed.WaitOne(5000), "first synthetic native failure completes"); fixture.Services.DrainUi();
                AssertStatusRelocalizes(fixture, label, language,
                    "Приём обратного звука работает с ограничениями. Ограничения декодеров: quiet: доступен частично (Unavailable). FSK остаётся доступен.",
                    "Return audio reception is degraded. Decoder availability: quiet: partially available (Unavailable). FSK remains available.", "partial native failure");
                fixture.Services.RouteSamples["two"](new float[] { 0 });
                True(failed.WaitOne(5000), "second synthetic native failure completes"); fixture.Services.DrainUi();
                AssertStatusRelocalizes(fixture, label, language,
                    "Приём обратного звука работает с ограничениями. Ограничения декодеров: quiet: недоступен (Unavailable, two). FSK остаётся доступен.",
                    "Return audio reception is degraded. Decoder availability: quiet: unavailable (Unavailable, two). FSK remains available.", "all routes lose one native modem");
                fixture.Services.RouteErrors["two"](new Exception("Unavailable")); fixture.Services.DrainUi();
                AssertStatusRelocalizes(fixture, label, language,
                    "Приём обратного звука продолжается на доступных устройствах. Ограничения декодеров: quiet: недоступен (Unavailable). FSK остаётся доступен.",
                    "Return audio reception continues on available devices. Decoder availability: quiet: unavailable (Unavailable). FSK remains available.", "capture loss updates degraded route set");
            }
        }
        finally { UiText.SetLanguage(previous); }
    }

    private static void AssertStatusRelocalizes(Fixture fixture, Label label, UiLanguage initial, string russian, string english, string scenario)
    {
        Equal(initial == UiLanguage.Russian ? russian : english, label.Text, scenario + " before switching");
        int callbacks = fixture.Statuses.Count;
        UiText.SetLanguage(initial == UiLanguage.Russian ? UiLanguage.English : UiLanguage.Russian); UiText.Apply(label);
        Equal(initial == UiLanguage.Russian ? english : russian, label.Text, scenario + " switches existing binding");
        UiText.SetLanguage(initial); UiText.Apply(label);
        Equal(initial == UiLanguage.Russian ? russian : english, label.Text, scenario + " switches back");
        Equal(callbacks, fixture.Statuses.Count, scenario + " language changes need no new bridge callback");
    }

    private static void NativeErrorStatusRelocalizes()
    {
        UiLanguage previous = UiText.Language;
        try
        {
            UiText.Get("Недоступен", "Unavailable");
            foreach (UiLanguage language in new[] { UiLanguage.Russian, UiLanguage.English })
            foreach (bool decodeFailure in new[] { true, false })
            using (Fixture fixture = new Fixture())
            using (var label = new Label())
            {
                UiText.SetLanguage(language);
                fixture.StatusChanged = delegate(string message) { UiText.SetKnownText(label, message); };
                fixture.Services.NativeModemsEnabled = true;
                fixture.Services.NativeFactory = delegate { throw new FileNotFoundException("Unavailable"); };
                fixture.Services.OnReceiverCreated = JoinNativeWorkers;
                fixture.Bridge.Start(null); fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
                fixture.Bridge.AdditionalRetries = 0;
                Action fail = delegate { throw new InvalidOperationException("Unavailable"); };
                if (decodeFailure) fixture.Services.BeforeDecode = fail; else fixture.Services.BeforePublish = fail;
                foreach (Packet packet in Packets(fixture.Session, Guid.NewGuid(), "synthetic payload")) fixture.Bridge.ReceivePacket(packet);
                fixture.Services.DrainUi();
                AssertStatusRelocalizes(fixture, label, language,
                    (decodeFailure ? "Данные из Horizon не приняты: " : "Обратный звук: ") + "Unavailable Ограничения декодеров: quiet: недоступен (устройство Windows по умолчанию); aicodix: недоступен (устройство Windows по умолчанию). FSK остаётся доступен.",
                    (decodeFailure ? "Data from Horizon could not be received: " : "Return audio: ") + "Unavailable Decoder availability: quiet: unavailable (default Windows device); aicodix: unavailable (default Windows device). FSK remains available.",
                    (decodeFailure ? "decode" : "publication") + " failure retains literal external error with native degradation");
                Equal(0, fixture.Published.Count, "failed payload never acknowledges publication");
            }
        }
        finally { UiText.SetLanguage(previous); }
    }

    private static void NativeRouteFailuresAreIndependent()
    {
        UiLanguage previous = UiText.Language;
        try
        {
            foreach (UiLanguage language in new[] { UiLanguage.English, UiLanguage.Russian })
            using (Fixture fixture = new Fixture())
            using (var firstFailed = new ManualResetEvent(false))
            using (var secondFailed = new ManualResetEvent(false))
            using (var nativePacket = new ManualResetEvent(false))
            {
                UiText.SetLanguage(language);
                int healthyFeeds = 0;
                fixture.Services.NativeModemsEnabled = true;
                fixture.Services.NativeFactory = delegate(string endpoint, AudioModemKind kind) {
                    return new NativeDecoder(delegate(float[] samples) {
                        if (kind == AudioModemKind.Quiet) throw new InvalidOperationException("terminal native fault on " + endpoint);
                        Interlocked.Increment(ref healthyFeeds); nativePacket.Set(); return new byte[0][];
                    });
                };
                fixture.Bridge.StartWithRoutes(new[] { "one", "two" }); fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
                fixture.Services.Receivers["one"].DecoderFailed += delegate(AudioModemKind kind, Exception error) { if (kind == AudioModemKind.Quiet) firstFailed.Set(); };
                fixture.Services.Receivers["two"].DecoderFailed += delegate(AudioModemKind kind, Exception error) { if (kind == AudioModemKind.Quiet) secondFailed.Set(); };
                fixture.Services.RouteSamples["one"](new float[] { 0 });
                True(firstFailed.WaitOne(5000), "one route's decoder fails after capture starts"); fixture.Services.DrainUi();
                string message = fixture.Statuses[fixture.Statuses.Count - 1];
                True(message.Contains("quiet") && message.Contains("one") && message.Contains(language == UiLanguage.English ? "partially available" : "доступен частично"), "one route's modem failure shows partial availability in RU and EN");
                fixture.Services.RouteSamples["two"](new float[] { 0 });
                True(secondFailed.WaitOne(5000), "second route's decoder fails independently"); fixture.Services.DrainUi();
                message = fixture.Statuses[fixture.Statuses.Count - 1];
                True(message.Contains("one") && message.Contains("two") && message.Contains(language == UiLanguage.English ? "unavailable" : "недоступен"), "modem becomes globally unavailable only when all healthy routes lose it");
                True(nativePacket.WaitOne(5000) && healthyFeeds > 0, "other native modem still receives PCM on degraded routes");
                Guid test = Guid.NewGuid();
                fixture.Services.RouteSamples["one"](AudioModem.Encode(BridgeControl.Single(Guid.NewGuid(), BridgeControl.Report(new BridgeReport { Session = fixture.Session, Test = test, Milliseconds = 1000, VideoBytes = 9 }))));
                fixture.Services.DrainUi();
                True(fixture.Reports.Exists(delegate(BridgeReport value) { return value.Test == test; }), "degraded native availability does not mark capture/FSK route failed");
                fixture.Services.RouteErrors["one"](new Exception("independent capture fault"));
                fixture.Services.RouteSamples["two"](AudioModem.Encode(BridgeControl.Single(Guid.NewGuid(), BridgeControl.Report(new BridgeReport { Session = fixture.Session, Test = Guid.NewGuid(), Milliseconds = 1000, VideoBytes = 9 }))));
                fixture.Services.DrainUi(); True(fixture.Reports.Count == 2, "capture failure remains independent of healthy second route");
                fixture.Services.RouteErrors["two"](new Exception("all capture routes failed")); fixture.Services.DrainUi();
                True(fixture.Statuses[fixture.Statuses.Count - 1].Contains("all capture routes failed") && !fixture.Statuses[fixture.Statuses.Count - 1].Contains("FSK"), "all capture failures do not claim FSK availability");
            }
            using (Fixture fixture = new Fixture())
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                fixture.Services.NativeModemsEnabled = true;
                fixture.Services.NativeFactory = delegate { entered.Set(); release.WaitOne(5000); throw new InvalidOperationException("late owner fault"); };
                fixture.Bridge.Start("late"); fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
                True(entered.WaitOne(5000), "native constructor is blocked before late owner disposal");
                AudioReceiveMultiplexer mux = fixture.Services.Receivers["late"];
                int statuses = fixture.Statuses.Count;
                fixture.Bridge.Dispose(); fixture.Services.DrainWorkers(); release.Set(); JoinNativeWorkers(mux); fixture.Services.DrainUi();
                Equal(statuses, fixture.Statuses.Count, "late native failure after owner disposal cannot update visible status");
            }
        }
        finally { UiText.SetLanguage(previous); }
    }

    private sealed class NativeDecoder : INativeAudioDecoder
    {
        private readonly Func<float[], byte[][]> feed;
        internal NativeDecoder(Func<float[], byte[][]> feed) { this.feed = feed; }
        public byte[][] Feed(float[] samples) { return feed(samples); }
        public void Dispose() { }
    }

    private static void CaptureFailureWinsLateNativeNotification()
    {
        using (Fixture fixture = new Fixture())
        using (var release = new ManualResetEvent(false))
        {
            fixture.Services.NativeModemsEnabled = true;
            fixture.Services.NativeFactory = delegate { release.WaitOne(5000); throw new FileNotFoundException("late native startup failure"); };
            fixture.Services.OnCaptureStart = delegate { fixture.Services.CaptureError(new Exception("primary capture failure")); };
            fixture.Bridge.Start("late-native"); fixture.Services.DrainWorkers();
            release.Set(); JoinNativeWorkers(fixture.Services.Receivers["late-native"]); fixture.Services.DrainUi();
            True(fixture.Statuses[fixture.Statuses.Count - 1].Contains("primary capture failure"), "late native failure must preserve an already failed capture's visible error");
        }
        UiLanguage previous = UiText.Language;
        try
        {
            UiText.SetLanguage(UiLanguage.English);
            using (Fixture fixture = new Fixture())
            using (var failed = new ManualResetEvent(false))
            {
                fixture.Services.NativeModemsEnabled = true;
                fixture.Services.NativeFactory = delegate(string endpoint, AudioModemKind kind) { return new NativeDecoder(delegate {
                    if (endpoint == "one" && kind == AudioModemKind.Quiet) throw new InvalidOperationException("one quiet lost");
                    return new byte[0][];
                }); };
                fixture.Bridge.StartWithRoutes(new[] { "one", "two" }); fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
                fixture.Services.Receivers["one"].DecoderFailed += delegate(AudioModemKind kind, Exception error) { if (kind == AudioModemKind.Quiet) failed.Set(); };
                fixture.Services.RouteSamples["one"](new float[] { 0 }); True(failed.WaitOne(5000), "one modem fails before healthy route capture loss"); fixture.Services.DrainUi();
                fixture.Services.RouteErrors["two"](new Exception("healthy native route capture lost")); fixture.Services.DrainUi();
                True(fixture.Statuses[fixture.Statuses.Count - 1].Contains("quiet: unavailable"), "capture loss refreshes decoder availability over only healthy capture routes");
            }
        }
        finally { UiText.SetLanguage(previous); }
    }

    private static void ConstructionAndCaptureLifetime()
    {
        using (Fixture fixture = new Fixture())
        {
            Equal(0, fixture.Services.CaptureStarts, "constructor opens no capture");
            Equal(0, fixture.Services.PasteCreates, "constructor installs no hook");
            Equal(0, fixture.Services.TimerCreates, "constructor creates no timer");
            fixture.Bridge.Start("explicit-output");
            fixture.Bridge.Start("ignored-second-output");
            Equal(0, fixture.Services.CaptureStarts, "capture creation is queued");
            fixture.Services.DrainWorkers();
            Equal(1, fixture.Services.CaptureStarts, "start is idempotent");
            Equal("explicit-output", fixture.Services.Endpoint, "explicit output preserved");
            fixture.Bridge.Dispose(); fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
            Equal(1, fixture.Services.Capture.Disposals, "active capture disposed once");
            True(fixture.Services.Timer.Disposed && fixture.Services.Paste.Disposed, "timer and paste released");
            Equal(0, fixture.Statuses.Count, "queued status ignored after disposal");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Bridge.Start(null);
            fixture.Bridge.Dispose();
            fixture.Services.DrainWorkers();
            Equal(0, fixture.Services.CaptureStarts, "dispose before worker prevents capture");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Services.OnCaptureStart = delegate { fixture.Bridge.Dispose(); };
            fixture.Bridge.Start(null); fixture.Services.DrainWorkers();
            Equal(1, fixture.Services.Capture.Disposals, "late-open capture is disposed rather than started again after disposal");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Services.OnCaptureStart = delegate { fixture.Services.CaptureError(new Exception("early capture error")); };
            fixture.Bridge.Start(null); fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
            Equal(1, fixture.Statuses.Count, "a synchronous capture error is not overwritten by enabled status");
            True(fixture.Statuses[0].Contains("early capture error"), "capture error remains visible");
        }
    }

    private static void ExplicitMultipleCaptureSeparatesStreamsAndDeduplicates()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Services.RenderEndpoints = new[] { new AudioEndpoint("dfx", "DFX"), new AudioEndpoint("realtek", "Realtek"),
                new AudioEndpoint("amm", "AMM"), new AudioEndpoint("realtek", "Duplicate"), null, new AudioEndpoint(null, "Invalid") };
            fixture.Bridge.StartWithRoutes(new[] { "dfx", "realtek", "amm" }); fixture.Services.DrainWorkers(); fixture.Services.DrainUi(); fixture.Statuses.Clear();
            Equal(0, fixture.Services.RenderEnumerations, "explicit internal routes do not enumerate other devices");
            Equal(3, fixture.Services.CaptureStarts, "explicit internal route list opens exactly its requested endpoints");
            Guid firstTest = Guid.NewGuid(), secondTest = Guid.NewGuid();
            float[] first = AudioModem.Encode(BridgeControl.Single(Guid.NewGuid(), BridgeControl.Report(new BridgeReport {
                Session = fixture.Session, Test = firstTest, Milliseconds = 1000, VideoBytes = 256, AudioBytes = 0 })));
            float[] second = AudioModem.Encode(BridgeControl.Single(Guid.NewGuid(), BridgeControl.Report(new BridgeReport {
                Session = fixture.Session, Test = secondTest, Milliseconds = 1000, VideoBytes = 512, AudioBytes = 0 })));
            int half = first.Length / 2;
            float[] begin = new float[half], end = new float[first.Length - half];
            Array.Copy(first, 0, begin, 0, begin.Length); Array.Copy(first, half, end, 0, end.Length);
            fixture.Services.RouteSamples["realtek"](begin);
            fixture.Services.RouteSamples["dfx"](second);
            fixture.Services.RouteSamples["realtek"](end);
            fixture.Services.DrainUi();
            Equal(2, fixture.Reports.Count, "interleaved callbacks from different outputs retain independent decoder state");
            True(fixture.Reports.Exists(delegate(BridgeReport report) { return report.Test == firstTest; }) &&
                fixture.Reports.Exists(delegate(BridgeReport report) { return report.Test == secondTest; }), "both independently decoded reports retain their test identities");
            Packet[] packets = Packets(fixture.Session, Guid.NewGuid(), "same sound on two outputs");
            foreach (Packet packet in packets)
            {
                float[] samples = AudioModem.Encode(packet);
                fixture.Services.RouteSamples["realtek"](samples);
                fixture.Services.RouteSamples["amm"](samples);
            }
            fixture.Services.DrainUi();
            Equal(1, fixture.Services.Publications.Count, "the same reverse clipboard decoded on two outputs publishes once");
            Equal(1, fixture.Published.Count, "multi-output duplicates acknowledge one clipboard publication");
            Equal(1, fixture.Services.Paste.Completions, "multi-output duplicates paste only once");
            fixture.Services.RouteErrors["dfx"](new Exception("one route unavailable"));
            fixture.Services.DrainUi();
            int reports = fixture.Reports.Count;
            fixture.Services.RouteSamples["realtek"](first); fixture.Services.DrainUi();
            Equal(reports + 1, fixture.Reports.Count, "one failed capture route does not disable healthy outputs");
            Packet[] fresh = Packets(fixture.Session, Guid.NewGuid(), "copy after one output fails");
            foreach (Packet packet in fresh) fixture.Bridge.ReceivePacket(packet);
            fixture.Services.DrainUi();
            Equal(2, fixture.Services.Publications.Count, "one failed route does not fault the shared clipboard assembler");
            fixture.Bridge.Dispose(); fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
            foreach (FakeCapture capture in fixture.Services.AllCaptures) Equal(1, capture.Disposals, "every automatic capture is disposed exactly once");
            True(fixture.Services.Timer.Disposed && fixture.Services.Paste.Disposed, "automatic mode still owns only one publication timer and paste coordinator");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Services.RenderEndpoints = new[] { new AudioEndpoint("other", "Other") };
            fixture.Bridge.Start("explicit"); fixture.Services.DrainWorkers();
            Equal(0, fixture.Services.RenderEnumerations, "an explicit audio choice never enumerates or opens unrelated outputs");
            Equal(1, fixture.Services.CaptureStarts, "an explicit audio choice opens exactly one capture");
            Equal("explicit", fixture.Services.Endpoint, "an explicit endpoint remains unchanged");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Services.RenderEndpoints = new[] { new AudioEndpoint("dfx", "DFX"), new AudioEndpoint("realtek", "Realtek") };
            fixture.Bridge.Start(null); fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
            Equal(0, fixture.Services.RenderEnumerations, "Windows default mode does not enumerate or fan out to all render devices");
            Equal(1, fixture.Services.CaptureStarts, "Windows default mode opens exactly one loopback capture");
            True(fixture.Services.Endpoint == null && fixture.Services.RouteSamples.ContainsKey("default"), "Default render endpoint is passed explicitly as null to the capture API");
        }
    }

    private static void MultichunkDuplicatesReportsAndEcho()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Start();
            Packet[] packets = Packets(fixture.Session, Guid.NewGuid(), "first");
            fixture.Bridge.ReceivePacket(packets[0]); fixture.Services.DrainUi();
            True(fixture.Bridge.IsReceiving, "first valid block enables deferred paste");
            int statuses = fixture.Statuses.Count;
            fixture.Services.Now = fixture.Services.Now.AddSeconds(2);
            fixture.Bridge.ReceivePacket(packets[0]); fixture.Services.DrainUi();
            Equal(statuses, fixture.Statuses.Count, "duplicates never report false progress");
            byte[] changed = (byte[])packets[0].Data.Clone(); changed[changed.Length - 1] ^= 1;
            fixture.Bridge.ReceivePacket(new Packet { TransferId = packets[0].TransferId, Index = 0, Count = packets[0].Count, TotalLength = packets[0].TotalLength, Data = changed, Profile = VideoProfile.Compatible, ChunkSize = 256 });
            fixture.Services.DrainUi();
            Equal(statuses, fixture.Statuses.Count, "conflicting duplicates never replace accepted bytes or count as progress");
            BridgeReport report = new BridgeReport { Session = fixture.Session, Test = Guid.NewGuid(), VideoBytes = 1000, AudioBytes = 500, Milliseconds = 20000, MicrophoneFound = true };
            fixture.Bridge.ReceivePacket(BridgeControl.Single(Guid.NewGuid(), BridgeControl.Report(report)));
            for (int i = packets.Length - 1; i >= 1; i--) fixture.Bridge.ReceivePacket(packets[i]);
            fixture.Services.DrainUi();
            Equal(1, fixture.Reports.Count, "benchmark report bypasses clipboard assembler");
            Equal(1, fixture.Services.Publications.Count, "out-of-order multi-chunk payload publishes once");
            Equal(packets[0].TransferId, fixture.Published[0], "echo suppression receives published transfer");
            Equal("publish,published,paste", String.Join(",", fixture.Events), "publication then echo suppression then deferred paste");
            True(!fixture.Bridge.IsReceiving, "successful publication ends receive state");
            foreach (Packet packet in packets) fixture.Bridge.ReceivePacket(packet);
            fixture.Services.DrainUi();
            Equal(1, fixture.Services.Publications.Count, "completed transfer replay does not republish");
            True(!fixture.Bridge.IsReceiving, "completed replay cannot block Ctrl+V again");
        }
    }

    private static void SessionAndSupersession()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Start();
            Packet[] foreign = Packets(Guid.NewGuid(), Guid.NewGuid(), "foreign");
            foreach (Packet packet in foreign) fixture.Bridge.ReceivePacket(packet);
            fixture.Services.DrainUi();
            True(!fixture.Bridge.IsReceiving && fixture.Services.Publications.Count == 0, "foreign session ignored");
            Packet[] first = Packets(fixture.Session, Guid.NewGuid(), "older");
            Packet[] next = Packets(fixture.Session, Guid.NewGuid(), "newer");
            fixture.Bridge.ReceivePacket(next[1]);
            True(!fixture.Bridge.IsReceiving, "new transfer must begin with its session envelope");
            fixture.Bridge.ReceivePacket(first[0]);
            fixture.Bridge.ReceivePacket(next[0]);
            foreach (Packet packet in first) fixture.Bridge.ReceivePacket(packet);
            for (int i = 1; i < next.Length; i++) fixture.Bridge.ReceivePacket(next[i]);
            fixture.Services.DrainUi();
            Equal(1, fixture.Services.Publications.Count, "late older transfer never replaces newer transfer");
            Equal(next[0].TransferId, fixture.Published[0], "newest transfer publishes");
        }
    }

    private static void ClipboardRetriesAndFailure()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); fixture.Services.Busy = 2;
            foreach (Packet packet in Packets(fixture.Session, Guid.NewGuid(), "retry")) fixture.Bridge.ReceivePacket(packet);
            fixture.Services.DrainUi();
            True(fixture.Services.Timer.Enabled && fixture.Bridge.IsReceiving, "busy clipboard retains data and deferred paste");
            Equal(0, fixture.Published.Count, "busy clipboard cannot suppress echo or acknowledge publication");
            fixture.Services.Timer.Fire(); fixture.Services.Timer.Fire();
            Equal(3, fixture.Services.PublishAttempts, "clipboard publication retries actual busy failures");
            Equal(1, fixture.Published.Count, "retry completion acknowledges once");
            Equal(1, fixture.Services.Paste.Completions, "retry completion pastes once");
            True(!fixture.Services.Timer.Enabled && !fixture.Bridge.IsReceiving, "success stops retry state");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); fixture.Services.Busy = 20;
            foreach (Packet packet in Packets(fixture.Session, Guid.NewGuid(), "blocked")) fixture.Bridge.ReceivePacket(packet);
            fixture.Services.DrainUi();
            for (int i = 0; i < 20; i++) { fixture.Services.Timer.Fire(); fixture.Services.DrainUi(); }
            Equal(12, fixture.Services.PublishAttempts, "busy retry count is bounded");
            Equal(0, fixture.Published.Count, "failed publication never acknowledges");
            True(!fixture.Bridge.IsReceiving && !fixture.Services.Timer.Enabled, "failed publication releases Ctrl+V and timer");
            Equal(1, fixture.Services.Paste.Cancellations, "failed publication cancels deferred paste");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); fixture.Services.Busy = 1;
            Packet[] old = Packets(fixture.Session, Guid.NewGuid(), "old pending");
            foreach (Packet packet in old) fixture.Bridge.ReceivePacket(packet);
            fixture.Services.DrainUi();
            Packet[] replacement = Packets(fixture.Session, Guid.NewGuid(), "new pending");
            fixture.Bridge.ReceivePacket(replacement[0]); fixture.Services.Timer.Fire();
            for (int i = 1; i < replacement.Length; i++) fixture.Bridge.ReceivePacket(replacement[i]);
            fixture.Services.DrainUi();
            Equal(1, fixture.Published.Count, "replacing a busy clipboard transfer publishes only the new payload");
            Equal(replacement[0].TransferId, fixture.Published[0], "retry timer cannot publish a superseded payload");
        }
    }

    private static void StaleDecodeAndDispose()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Start();
            Packet[] first = Packets(fixture.Session, Guid.NewGuid(), "stale");
            Packet[] next = Packets(fixture.Session, Guid.NewGuid(), "replacement");
            fixture.Services.BeforeDecode = delegate { fixture.Services.BeforeDecode = null; fixture.Bridge.ReceivePacket(next[0]); };
            foreach (Packet packet in first) fixture.Bridge.ReceivePacket(packet);
            fixture.Services.DrainUi();
            Equal(0, fixture.Services.Publications.Count, "stale decode completion cannot publish over a new transfer");
            for (int i = 1; i < next.Length; i++) fixture.Bridge.ReceivePacket(next[i]);
            fixture.Services.DrainUi();
            Equal(next[0].TransferId, fixture.Published[0], "replacement still completes");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Start();
            foreach (Packet packet in Packets(fixture.Session, Guid.NewGuid(), "dispose")) fixture.Bridge.ReceivePacket(packet);
            fixture.Bridge.Dispose(); fixture.Services.DrainUi(); fixture.Services.DrainWorkers();
            Equal(0, fixture.Services.Publications.Count, "queued completion after disposal cannot access clipboard");
        }
    }

    private static void CaptureErrorsCancelPendingPaste()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Start();
            fixture.Bridge.ReceivePacket(Packets(fixture.Session, Guid.NewGuid(), "interrupted")[0]);
            fixture.Services.CaptureError(new Exception("synthetic endpoint failure"));
            fixture.Services.DrainUi();
            var currentField = typeof(ServerAudioBridge).GetField("current", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object failed = currentField.GetValue(fixture.Bridge);
            var assemblerField = failed.GetType().GetField("Assembler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var assemblyBuffer = typeof(TransferAssembler).GetField("buffer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            True(assemblyBuffer.GetValue(assemblerField.GetValue(failed)) == null, "A final capture failure immediately releases the incomplete payload buffer");
            True(!fixture.Bridge.IsReceiving, "capture failure ends incomplete transfer");
            Equal(1, fixture.Services.Paste.Cancellations, "capture failure releases pending paste");
            foreach (Packet packet in Packets(fixture.Session, Guid.NewGuid(), "post failure")) fixture.Bridge.ReceivePacket(packet);
            fixture.Services.DrainUi();
            Equal(0, fixture.Services.Publications.Count, "queued callbacks after capture failure cannot start another transfer");
        }
    }

    private static void FileReportsThroughSyntheticAudio()
    {
        using (Fixture fixture = new Fixture())
        {
            var received = new List<FileBenchmarkReport>(); fixture.Bridge.FileReportReceived = received.Add;
            fixture.Services.BeforeDecode = delegate { throw new Exception("A file report must bypass clipboard decoding."); };
            fixture.Start();
            var report = new FileBenchmarkReport { Session = fixture.Session, Test = Guid.NewGuid(), Profile = VideoProfile.Mp4Modem,
                Status = FileBenchmarkStatus.Completed, ReceivedBytes = 100L * 1024 * 1024, TotalBytes = 100L * 1024 * 1024,
                ElapsedMilliseconds = 5L * 60 * 60 * 1000, FrameWidth = 640, FrameHeight = 360, Frames = 540000 };
            byte[] bytes = FileBenchmarkProtocol.Report(report);
            Equal(84, bytes.Length, "Full-file report fits one audio packet");
            var packet = new Packet { TransferId = Guid.NewGuid(), Index = 0, Count = 1, TotalLength = bytes.Length, Data = bytes,
                Profile = VideoProfile.Compatible, ChunkSize = AudioModem.MaxPayloadBytes };
            float[] samples = AudioModem.Encode(packet);
            for (int offset = 0; offset < samples.Length; offset += 257)
            {
                var chunk = new float[Math.Min(257, samples.Length - offset)]; Array.Copy(samples, offset, chunk, 0, chunk.Length); fixture.Services.Samples(chunk);
            }
            Equal(0, received.Count, "Decoded reports wait for the server STA callback"); fixture.Services.DrainUi();
            Equal(1, received.Count, "Real audio modulation/demodulation delivers the report callback");
            True(received[0].Test == report.Test && received[0].Profile == report.Profile && received[0].ReceivedBytes == report.TotalBytes &&
                received[0].ElapsedMilliseconds == report.ElapsedMilliseconds && received[0].Status == FileBenchmarkStatus.Completed,
                "Audio callback retains full100MiB integrity acknowledgement and long timing");
            True(!fixture.Bridge.IsReceiving && fixture.Services.Publications.Count == 0 && fixture.Published.Count == 0,
                "Benchmark audio never starts a clipboard transfer or publication");
            bytes[44] ^= 1; fixture.Services.Samples(AudioModem.Encode(packet)); fixture.Services.DrainUi();
            Equal(1, received.Count, "An intact audio frame containing a damaged binary-report CRC is ignored");
            bytes[44] ^= 1; report.Session = Guid.NewGuid(); packet.Data = FileBenchmarkProtocol.Report(report);
            fixture.Services.Samples(AudioModem.Encode(packet)); fixture.Services.DrainUi();
            Equal(1, received.Count, "Another session's audio report is ignored");
            report.Session = fixture.Session; packet.Data = FileBenchmarkProtocol.Report(report);
            fixture.Services.Samples(AudioModem.Encode(packet)); fixture.Bridge.Dispose(); fixture.Services.DrainUi();
            Equal(1, received.Count, "A queued report from a disposed reverse receiver cannot reach the controller");
        }
    }

    private static void DeliveryReceiptsThroughSyntheticAudio()
    {
        using (Fixture fixture = new Fixture())
        {
            var received = new List<DeliveryReceipt>(); fixture.Bridge.DeliveryReceived = received.Add;
            fixture.Services.BeforeDecode = delegate { throw new Exception("A video delivery receipt must bypass clipboard decoding."); };
            fixture.Start();
            Guid transfer = Guid.NewGuid();
            byte[] bytes = DeliveryReceipt.Encode(fixture.Session, transfer, 10240);
            var packet = BridgeControl.Single(Guid.NewGuid(), bytes);
            float[] samples = AudioModem.Encode(packet);
            for (int offset = 0; offset < samples.Length; offset += 257)
            {
                var chunk = new float[Math.Min(257, samples.Length - offset)]; Array.Copy(samples, offset, chunk, 0, chunk.Length);
                fixture.Services.Samples(chunk);
            }
            Equal(0, received.Count, "Delivery receipts wait for the server STA callback"); fixture.Services.DrainUi();
            Equal(1, received.Count, "Real audio modulation and demodulation deliver a video receipt");
            True(received[0].Session == fixture.Session && received[0].Transfer == transfer && received[0].EncodedBytes == 10240,
                "Receipt preserves exact forward video transfer and payload size");
            True(!fixture.Bridge.IsReceiving && fixture.Services.Publications.Count == 0 && fixture.Published.Count == 0 && fixture.Reports.Count == 0,
                "Receipt does not enter the clipboard, echo suppression or benchmark-report path");
            fixture.Services.Samples(AudioModem.Encode(packet)); fixture.Services.DrainUi();
            Equal(2, received.Count, "Repeated receipts reach the controller for current-transfer and once-only validation");
            foreach (AudioModemKind kind in new[] { AudioModemKind.Quiet, AudioModemKind.Aicodix })
            {
                int packetCount;
                byte[] wire = AudioPacketWire.Encode(kind, Guid.NewGuid(), 0, bytes, 1024, out packetCount);
                Packet[] decoded = AudioPacketWire.Decode(kind, wire);
                True(packetCount == 1 && decoded != null && decoded.Length == 1, "Native modem envelope retains a receipt in one packet");
                fixture.Bridge.ReceivePacket(decoded[0]); fixture.Services.DrainUi();
            }
            Equal(4, received.Count, "Both Quiet and aicodix packet envelopes route the same video-delivery acknowledgement");
            bytes[40] ^= 1; fixture.Services.Samples(AudioModem.Encode(packet)); fixture.Services.DrainUi();
            Equal(4, received.Count, "Damaged receipt CRC is ignored even inside an intact audio frame");
            packet.Data = DeliveryReceipt.Encode(Guid.NewGuid(), transfer, 10240);
            fixture.Services.Samples(AudioModem.Encode(packet)); fixture.Services.DrainUi();
            Equal(4, received.Count, "Foreign-session delivery receipt is ignored");
            fixture.Bridge.SetBenchmarkMode(true);
            packet.Data = DeliveryReceipt.Encode(fixture.Session, transfer, 10240);
            fixture.Services.Samples(AudioModem.Encode(packet)); fixture.Services.DrainUi();
            Equal(5, received.Count, "Delivery control remains available while clipboard transfers are paused for a benchmark");
            fixture.Services.Samples(AudioModem.Encode(packet)); fixture.Bridge.Dispose(); fixture.Services.DrainUi();
            Equal(5, received.Count, "A queued delivery receipt cannot reach the controller after disposal");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Start();
            var received = new List<DeliveryReceipt>(); fixture.Bridge.DeliveryReceived = received.Add;
            Packet[] reverse = Packets(fixture.Session, Guid.NewGuid(), "reverse concurrent with delivery receipt");
            fixture.Bridge.ReceivePacket(reverse[0]);
            fixture.Bridge.ReceivePacket(BridgeControl.Single(Guid.NewGuid(), DeliveryReceipt.Encode(fixture.Session, Guid.NewGuid(), 700)));
            fixture.Services.DrainUi();
            True(fixture.Bridge.IsReceiving && received.Count == 1 && fixture.Services.Publications.Count == 0,
                "Forward receipt does not replace a partially received reverse clipboard transfer");
            for (int i = 1; i < reverse.Length; i++) fixture.Bridge.ReceivePacket(reverse[i]);
            fixture.Services.DrainUi();
            True(fixture.Services.Publications.Count == 1 && fixture.Published.Count == 1 && fixture.Published[0] == reverse[0].TransferId,
                "Reverse clipboard publication completes independently of the delivery receipt");
        }
    }

    private static void IndependentAudioProbe()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); fixture.Bridge.SetBenchmarkMode(true);
            fixture.Services.BeforeDecode = delegate { throw new Exception("An audio check must bypass clipboard decoding."); };
            Guid probeId = Guid.NewGuid();
            byte[] bytes = AudioDiagnosticProbe.Encode(probeId, AudioModemKind.Fsk, DateTime.UtcNow.Ticks);
            Packet packet = BridgeControl.Single(probeId, bytes);
            fixture.Services.Samples(AudioModem.Encode(packet));
            Equal(0, fixture.Statuses.Count, "Probe waits for STA dispatch");
            fixture.Services.DrainUi();
            True(fixture.Statuses.Count == 1 && fixture.Statuses[0].Contains("fsk"), "Independent probe is reported without a video Hello or benchmark Ready");
            True(!fixture.Bridge.IsReceiving && fixture.Services.Publications.Count == 0 && fixture.Published.Count == 0 && fixture.Reports.Count == 0,
                "An independent probe never reads, publishes or suppresses clipboard content");
            fixture.Bridge.ReceivePacket(BridgeControl.Single(Guid.NewGuid(), bytes)); fixture.Services.DrainUi();
            Equal(1, fixture.Statuses.Count, "Probe ID must match its outer audio transfer");
            byte[] corrupt = (byte[])bytes.Clone(); corrupt[30] ^= 1;
            fixture.Bridge.ReceivePacket(BridgeControl.Single(probeId, corrupt)); fixture.Services.DrainUi();
            Equal(1, fixture.Statuses.Count, "Corrupt probe does not report success");
            fixture.Bridge.ReceivePacket(packet); fixture.Bridge.Dispose(); fixture.Services.DrainUi();
            Equal(1, fixture.Statuses.Count, "Disposed audio receiver cannot report a queued probe");
        }
    }

    private static void AudioProbeCallbacksAreBoundedAndIsolated()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); int callbacks = 0; Guid expected = Guid.NewGuid();
            fixture.Bridge.AudioProbeReceived = delegate(AudioDiagnosticProbe probe) { callbacks++; Equal(expected, probe.Id, "Probe callback retains its bound audio transfer identity"); };
            byte[] bytes = AudioDiagnosticProbe.Encode(expected, AudioModemKind.Fsk, fixture.Services.Now.Ticks);
            var valid = BridgeControl.Single(expected, bytes);
            fixture.Bridge.ReceivePacket(BridgeControl.Single(Guid.NewGuid(), bytes));
            byte[] corrupt = (byte[])bytes.Clone(); corrupt[30] ^= 1;
            fixture.Bridge.ReceivePacket(BridgeControl.Single(expected, corrupt));
            fixture.Bridge.ReceivePacket(new Packet { TransferId = expected, Index = 1, Count = 1, TotalLength = bytes.Length, Data = bytes, Profile = VideoProfile.Compatible, ChunkSize = 256 });
            fixture.Services.DrainUi(); Equal(0, callbacks, "Outer mismatch, CRC damage and malformed packet shape cannot produce a success callback");
            for (int pass = 0; pass < 3; pass++) fixture.Bridge.ReceivePacket(valid);
            Equal(0, callbacks, "The diagnostic callback waits for owner STA dispatch"); fixture.Services.DrainUi();
            Equal(1, callbacks, "Three playback passes dispatch exactly one success callback");
            fixture.Bridge.ReceivePacket(valid); fixture.Services.DrainUi(); Equal(1, callbacks, "A replay after the callback is suppressed while its identity is remembered");
            True(!fixture.Bridge.IsReceiving && fixture.Published.Count == 0 && fixture.Services.Publications.Count == 0 && fixture.Services.Paste.Completions == 0,
                "Neither valid nor rejected diagnostics allocate a clipboard transfer, publish, acknowledge or paste");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); int callbacks = 0;
            fixture.Bridge.AudioProbeReceived = delegate { callbacks++; };
            foreach (int skewDays in new[] { -365, 365 })
            {
                Guid id = Guid.NewGuid(); byte[] bytes = AudioDiagnosticProbe.Encode(id, AudioModemKind.Quiet, fixture.Services.Now.AddDays(skewDays).Ticks);
                fixture.Bridge.ReceivePacket(BridgeControl.Single(id, bytes)); fixture.Services.DrainUi();
            }
            Equal(2, callbacks, "Independent machine clocks are diagnostic metadata, not a false audio-channel rejection");
            fixture.Bridge.AudioProbeReceived = delegate { throw new Exception("Synthetic popup handler failure."); };
            Guid other = Guid.NewGuid(); fixture.Bridge.ReceivePacket(BridgeControl.Single(other, AudioDiagnosticProbe.Encode(other, AudioModemKind.Aicodix, fixture.Services.Now.Ticks))); fixture.Services.DrainUi();
            foreach (Packet packet in Packets(fixture.Session, Guid.NewGuid(), "clipboard survives popup failure")) fixture.Bridge.ReceivePacket(packet); fixture.Services.DrainUi();
            Equal(1, fixture.Published.Count, "A failed notification callback cannot interrupt later clipboard reception");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); int callbacks = 0; fixture.Bridge.AudioProbeReceived = delegate { callbacks++; };
            Packet latest = null;
            for (int index = 0; index < ServerAudioBridge.MaximumRememberedProbes + 2; index++)
            {
                Guid id = Guid.NewGuid(); latest = BridgeControl.Single(id, AudioDiagnosticProbe.Encode(id, AudioModemKind.Fsk, fixture.Services.Now.Ticks)); fixture.Bridge.ReceivePacket(latest);
            }
            fixture.Bridge.ReceivePacket(latest); fixture.Services.DrainUi();
            Equal(ServerAudioBridge.MaximumRememberedProbes + 2, callbacks, "The bounded FIFO remains usable after more than 512 user selections and still deduplicates recent passes");
            var seen = (HashSet<Guid>)typeof(ServerAudioBridge).GetField("acceptedProbes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(fixture.Bridge);
            Equal(ServerAudioBridge.MaximumRememberedProbes, seen.Count, "Diagnostic deduplication memory has a fixed identity bound");
        }
        foreach (bool dispose in new[] { false, true })
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); int callbacks = 0; fixture.Bridge.AudioProbeReceived = delegate { callbacks++; };
            Guid id = Guid.NewGuid(); fixture.Bridge.ReceivePacket(BridgeControl.Single(id, AudioDiagnosticProbe.Encode(id, AudioModemKind.Fsk, fixture.Services.Now.Ticks)));
            if (dispose) fixture.Bridge.Dispose(); else fixture.Services.CaptureError(new Exception("Synthetic capture failure before popup dispatch."));
            fixture.Services.DrainUi(); Equal(0, callbacks, "A queued success cannot appear after " + (dispose ? "receiver disposal" : "all capture routes fail"));
        }
    }

    private static void BenchmarkRetiresPendingClipboard()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); fixture.Services.Busy = 2;
            Packet[] old = Packets(fixture.Session, Guid.NewGuid(), "busy before benchmark");
            foreach (Packet packet in old) fixture.Bridge.ReceivePacket(packet);
            fixture.Services.DrainUi();
            True(fixture.Services.Timer.Enabled && fixture.Bridge.IsReceiving && fixture.Services.PublishAttempts == 1,
                "Fixture has a decoded clipboard payload waiting for a busy-clipboard retry");
            fixture.Bridge.SetBenchmarkMode(true); fixture.Bridge.SetBenchmarkMode(true);
            True(!fixture.Bridge.IsReceiving && !fixture.Services.Timer.Enabled, "Benchmark entry retires pending publication and stops its retry timer");
            Equal(1, fixture.Services.Paste.Cancellations, "Benchmark entry cancels deferred paste exactly once");
            fixture.Services.Busy = 0; fixture.Services.Timer.FireStale(); fixture.Services.DrainUi();
            Equal(1, fixture.Services.PublishAttempts, "A stale retry callback cannot publish while benchmark mode is active");
            foreach (Packet packet in Packets(fixture.Session, Guid.NewGuid(), "copy during benchmark")) fixture.Bridge.ReceivePacket(packet);
            fixture.Services.DrainUi();
            Equal(0, fixture.Services.Publications.Count, "New reverse clipboard packets are ignored while benchmark mode is active");
            var fileReports = new List<FileBenchmarkReport>(); fixture.Bridge.FileReportReceived = fileReports.Add;
            byte[] bytes = FileBenchmarkProtocol.Report(new FileBenchmarkReport { Session = fixture.Session, Test = Guid.NewGuid(), Profile = VideoProfile.Compatible,
                Status = FileBenchmarkStatus.Progress, ReceivedBytes = 256, TotalBytes = 1024, ElapsedMilliseconds = 2000 });
            fixture.Services.Samples(AudioModem.Encode(new Packet { TransferId = Guid.NewGuid(), Index = 0, Count = 1, TotalLength = bytes.Length,
                Data = bytes, Profile = VideoProfile.Compatible, ChunkSize = AudioModem.MaxPayloadBytes }));
            fixture.Bridge.ReceivePacket(BridgeControl.Single(Guid.NewGuid(), BridgeControl.Report(new BridgeReport {
                Session = fixture.Session, Test = Guid.NewGuid(), Milliseconds = 1000, VideoBytes = 256, AudioBytes = 0 })));
            fixture.Services.DrainUi();
            True(fileReports.Count == 1 && fileReports[0].ReceivedBytes == 256 && fixture.Reports.Count == 1,
                "Both the84-byte file report through real synthetic audio and legacy control reports remain routed during pause");
            fixture.Bridge.SetBenchmarkMode(false); fixture.Bridge.SetBenchmarkMode(false);
            foreach (Packet packet in old) fixture.Bridge.ReceivePacket(packet);
            fixture.Services.Timer.FireStale(); fixture.Services.DrainUi();
            True(fixture.Services.Publications.Count == 0 && !fixture.Bridge.IsReceiving,
                "Resuming cannot resurrect the retired pre-benchmark clipboard transfer or its retry callback");
            Packet[] fresh = Packets(fixture.Session, Guid.NewGuid(), "fresh after benchmark");
            foreach (Packet packet in fresh) fixture.Bridge.ReceivePacket(packet);
            fixture.Services.DrainUi();
            True(fixture.Services.Publications.Count == 1 && fixture.Published.Count == 1 && fixture.Published[0] == fresh[0].TransferId,
                "A genuinely new transfer can publish after benchmark mode ends");
            Equal(1, fixture.Services.Paste.Completions, "Only the fresh post-benchmark transfer completes a deferred paste");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Start();
            foreach (Packet packet in Packets(fixture.Session, Guid.NewGuid(), "queued UI before benchmark")) fixture.Bridge.ReceivePacket(packet);
            fixture.Bridge.SetBenchmarkMode(true); fixture.Services.DrainUi(); fixture.Bridge.SetBenchmarkMode(false);
            fixture.Services.Timer.FireStale(); fixture.Services.DrainUi();
            True(fixture.Services.PublishAttempts == 0 && fixture.Services.Publications.Count == 0 && !fixture.Bridge.IsReceiving,
                "A publication queued on the STA before benchmark entry remains retired after resume");
        }
    }

    private static void ExplicitVerifiedCaptureRoutes()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Services.RenderEndpoints = new[] { new AudioEndpoint("unrelated", "Unrelated") };
            string[] endpoints = { "verified-b", "verified-a" };
            fixture.Bridge.StartWithRoutes(endpoints); endpoints[0] = "mutated-by-caller";
            Equal(0, fixture.Services.CaptureStarts, "Verified captures remain queued until the worker runs");
            fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
            Equal(0, fixture.Services.RenderEnumerations, "Two explicit verified routes never enumerate automatic outputs");
            Equal(2, fixture.Services.CaptureStarts, "Exactly two verified endpoint captures are opened");
            True(fixture.Services.RouteSamples.ContainsKey("verified-a") && fixture.Services.RouteSamples.ContainsKey("verified-b") &&
                !fixture.Services.RouteSamples.ContainsKey("mutated-by-caller") && !fixture.Services.RouteSamples.ContainsKey("unrelated"),
                "Verified endpoint IDs are snapshotted and no unrelated route is substituted");
            fixture.Bridge.Dispose(); fixture.Services.DrainWorkers();
            foreach (FakeCapture capture in fixture.Services.AllCaptures) Equal(1, capture.Disposals, "Each explicitly selected capture is disposed once");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Services.RenderEndpoints = new[] { new AudioEndpoint("automatic", "Automatic") };
            fixture.Bridge.StartWithRoutes(new string[0]); fixture.Services.DrainWorkers(); fixture.Services.DrainUi();
            True(fixture.Services.RenderEnumerations == 0 && fixture.Services.CaptureStarts == 0 && fixture.Services.RouteSamples.Count == 0,
                "An explicit empty verified set opens no capture and never falls back to automatic/default audio");
            True(!fixture.Bridge.IsReceiving && fixture.Services.Publications.Count == 0, "An explicit mute creates no receive or publication state");
        }
    }

    private static Packet Heartbeat(Guid session, Guid transfer, uint sequence, long sent, long total, int pass, AudioTransferPhase phase)
    {
        var value = new AudioTransferStatus { Session = session, Transfer = transfer, Sequence = sequence,
            SentBytes = sent, TotalBytes = total, PassIndex = pass, Phase = phase };
        return BridgeControl.Single(AudioTransferStatusProtocol.TransferId(value), AudioTransferStatusProtocol.Encode(value));
    }

    private static void HeartbeatIsActivityNotDelivery()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); Guid transfer = Guid.NewGuid(); var received = new List<AudioTransferStatus>();
            int starts = 0, completed = 0;
            fixture.Bridge.IncomingStarted = delegate { starts++; };
            fixture.Bridge.IncomingCompleted = delegate { completed++; };
            fixture.Bridge.IncomingActivityReceived = delegate(AudioTransferStatus status, int bytes)
            { received.Add(status); Equal(0, bytes, "Sender-reported bytes never become receiver-verified bytes"); };
            Packet first = Heartbeat(fixture.Session, transfer, 1, 0, 4096, 0, AudioTransferPhase.Sending);
            fixture.Services.Samples(AudioModem.Encode(first)); fixture.Services.DrainUi();
            True(received.Count == 1 && !fixture.Bridge.IsReceiving && !fixture.Services.Timer.Enabled && starts == 0,
                "A heartbeat arriving before packet zero reports activity without creating an assembler or deferred-paste state");
            fixture.Bridge.ReceivePacket(first); fixture.Services.DrainUi();
            Equal(1, received.Count, "The same sequence is deduplicated even before any clipboard payload arrives");
            fixture.Bridge.ReceivePacket(Heartbeat(fixture.Session, transfer, 2, 1024, 4097, 0, AudioTransferPhase.Sending)); fixture.Services.DrainUi();
            Equal(1, received.Count, "A pre-payload heartbeat cannot change the transfer's advertised total bytes");
            Packet wrongOuter = Heartbeat(fixture.Session, transfer, 2, 1024, 4096, 0, AudioTransferPhase.Sending);
            wrongOuter.TransferId = Guid.NewGuid(); fixture.Bridge.ReceivePacket(wrongOuter); fixture.Services.DrainUi();
            Equal(1, received.Count, "A status with the wrong derived outer transfer ID is ignored");
            fixture.Bridge.ReceivePacket(Heartbeat(Guid.NewGuid(), transfer, 2, 1024, 4096, 0, AudioTransferPhase.Sending)); fixture.Services.DrainUi();
            Equal(1, received.Count, "A correctly framed foreign-session heartbeat is ignored");
            fixture.Bridge.ReceivePacket(Heartbeat(fixture.Session, transfer, 2, 1024, 4096, 0, AudioTransferPhase.Sending)); fixture.Services.DrainUi();
            fixture.Bridge.ReceivePacket(Heartbeat(fixture.Session, transfer, 3, 0, 4096, 1, AudioTransferPhase.Sending)); fixture.Services.DrainUi();
            True(received.Count == 3 && received[2].PassIndex == 1 && received[2].SentBytes == 0,
                "A new pass can reset sent-byte progress even when the preceding PassComplete was lost");
            fixture.Bridge.ReceivePacket(Heartbeat(fixture.Session, transfer, 4, 4096, 4096, 1, AudioTransferPhase.PassComplete)); fixture.Services.DrainUi();
            Equal(4, received.Count, "A bound playback-complete status is visible as sender activity");
            True(!fixture.Bridge.IsReceiving && !fixture.Services.Timer.Enabled && fixture.Services.Publications.Count == 0 && fixture.Published.Count == 0 &&
                completed == 0 && starts == 0 && fixture.Services.Paste.Completions == 0,
                "PassComplete cannot publish, acknowledge delivery, create an assembler or complete a deferred paste");
        }
    }

    private static void HeartbeatFreshnessControlsInactivity()
    {
        foreach (string kind in new[] { "fresh", "next_pass", "duplicate", "stale", "backwards_bytes", "backwards_pass", "wrong_total", "foreign_session", "foreign_transfer", "wrong_outer", "cancelled" })
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); Packet[] packets = Packets(fixture.Session, Guid.NewGuid(), "heartbeat " + kind);
            Guid transfer = packets[0].TransferId; long total = packets[0].TotalLength;
            int activity = 0, failures = 0, completions = 0;
            fixture.Bridge.IncomingActivityReceived = delegate { activity++; };
            fixture.Bridge.IncomingFailed = delegate { failures++; };
            fixture.Bridge.IncomingCompleted = delegate { completions++; };
            fixture.Bridge.ReceivePacket(packets[0]); fixture.Services.DrainUi();
            fixture.Bridge.ReceivePacket(Heartbeat(fixture.Session, transfer, 5, 512, total, 1, AudioTransferPhase.Sending)); fixture.Services.DrainUi();
            fixture.Services.Now = fixture.Services.Now.AddSeconds(39);
            Guid statusSession = kind == "foreign_session" ? Guid.NewGuid() : fixture.Session;
            Guid statusTransfer = kind == "foreign_transfer" ? Guid.NewGuid() : transfer;
            uint sequence = kind == "duplicate" ? 5U : kind == "stale" ? 4U : 6U;
            long sent = kind == "duplicate" ? 512 : kind == "backwards_bytes" ? 128 : kind == "next_pass" ? 0 : 1024;
            int pass = kind == "backwards_pass" ? 0 : kind == "next_pass" ? 2 : 1;
            Packet heartbeat = Heartbeat(statusSession, statusTransfer, sequence, sent, kind == "wrong_total" ? total + 1 : total,
                pass, kind == "cancelled" ? AudioTransferPhase.Cancelled : AudioTransferPhase.Sending);
            if (kind == "wrong_outer") heartbeat.TransferId = Guid.NewGuid();
            fixture.Bridge.ReceivePacket(heartbeat); fixture.Services.DrainUi();
            bool fresh = kind == "fresh" || kind == "next_pass";
            Equal(fresh || kind == "cancelled" ? 2 : 1, activity, "Only identity/sequence/pass/total-valid status reaches activity callback: " + kind);
            fixture.Services.Now = fixture.Services.Now.AddSeconds(1); fixture.Services.Timer.Fire(); fixture.Services.DrainUi();
            True(fixture.Bridge.IsReceiving == fresh && failures == (fresh ? 0 : 1),
                "Only fresh active status extends the forty-second partial-transfer deadline: " + kind);
            True(fixture.Published.Count == 0 && fixture.Services.Publications.Count == 0 && completions == 0,
                "Heartbeat freshness never substitutes sender progress for actual publication: " + kind);
            if (fresh)
            {
                fixture.Services.Now = fixture.Services.Now.AddSeconds(38); fixture.Services.Timer.Fire();
                True(fixture.Bridge.IsReceiving, "Fresh heartbeat gives its own full forty-second inactivity interval: " + kind);
                fixture.Services.Now = fixture.Services.Now.AddSeconds(1); fixture.Services.Timer.Fire();
                True(!fixture.Bridge.IsReceiving && failures == 1, "Even accepted activity has a bounded deadline: " + kind);
                fixture.Bridge.ReceivePacket(Heartbeat(fixture.Session, transfer, 7, 1024, total, kind == "next_pass" ? 2 : 1, AudioTransferPhase.Sending));
                fixture.Services.DrainUi();
                True(!fixture.Bridge.IsReceiving && !fixture.Services.Timer.Enabled && fixture.Services.Publications.Count == 0,
                    "A heartbeat alone cannot resume a stalled assembler or claim data arrival: " + kind);
            }
        }
    }

    private static Packet[] Packets(Guid session, Guid id, string label)
    {
        Random random = new Random(label.GetHashCode());
        StringBuilder text = new StringBuilder(label + ":");
        for (int i = 0; i < 5000; i++) text.Append((char)(' ' + random.Next(90)));
        ClipboardSnapshot snapshot = new ClipboardSnapshot();
        snapshot.Formats.Add(DataFormats.UnicodeText, Encoding.UTF8.GetBytes(text.ToString()));
        string description;
        byte[] bytes = BridgeControl.WrapClipboard(session, ClipboardPayload.Encode(snapshot, out description));
        True(bytes.Length > 1000, "fixture exercises an actual multi-kilobyte encoded payload");
        int count = (bytes.Length + 255) / 256;
        Packet[] result = new Packet[count];
        for (int i = 0; i < count; i++)
        {
            byte[] chunk = new byte[Math.Min(256, bytes.Length - i * 256)];
            Buffer.BlockCopy(bytes, i * 256, chunk, 0, chunk.Length);
            result[i] = new Packet { TransferId = id, Index = i, Count = count, TotalLength = bytes.Length, Data = chunk, Profile = VideoProfile.Compatible, ChunkSize = 256 };
        }
        return result;
    }

    private static void EarlyParallelPacketsCompleteFirstPass()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); fixture.Bridge.AdditionalRetries = 0;
            int completed = 0; fixture.Bridge.IncomingCompleted = delegate { completed++; };
            Packet[] packets = Packets(fixture.Session, Guid.NewGuid(), "parallel reordered first pass");
            fixture.Services.Samples(AudioModem.Encode(packets[1]));
            for (int index = packets.Length - 1; index >= 2; index--) fixture.Bridge.ReceivePacket(packets[index]);
            fixture.Services.DrainUi();
            Equal(packets.Length - 1, fixture.Bridge.EarlyPacketCount, "A faster route can buffer all later packets before route zero");
            True(!fixture.Bridge.IsReceiving && completed == 0 && fixture.Published.Count == 0 && fixture.Services.Publications.Count == 0, "Early packets cannot create an assembler, publish, acknowledge or enable paste");
            packets[1].Data[0] ^= 1;
            fixture.Bridge.ReceivePacket(packets[0]); fixture.Services.DrainUi();
            Equal(1, fixture.Published.Count, "A valid session header drains later routes and completes the first pass with zero retries");
            Equal(1, completed, "Out-of-order publication dispatches one completion");
            Equal(0, fixture.Bridge.EarlyPacketCount, "Transferred early packet storage is released");
            fixture.Bridge.ReceivePacket(packets[0]); fixture.Services.DrainUi(); Equal(1, completed, "Header replay cannot duplicate an out-of-order publication");
            True(fixture.Services.Publications[0].Formats.ContainsKey(DataFormats.UnicodeText), "Buffered bytes are cloned and retain the fully verified payload despite caller mutation");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); Packet[] packets = Packets(fixture.Session, Guid.NewGuid(), "early metadata mismatch");
            var wrong = new Packet { TransferId = packets[1].TransferId, Index = 1, Count = packets[1].Count + 1, TotalLength = packets[1].TotalLength + 256,
                Profile = VideoProfile.Compatible, ChunkSize = 256, Data = packets[1].Data };
            fixture.Bridge.ReceivePacket(wrong); fixture.Bridge.ReceivePacket(packets[0]); fixture.Services.DrainUi();
            Equal(0, fixture.Published.Count, "A different total/count cannot enter the session-bound assembler");
            Equal(0, fixture.Bridge.EarlyPacketCount, "Incompatible early metadata is discarded on the real header");
            for (int index = 1; index < packets.Length; index++) fixture.Bridge.ReceivePacket(packets[index]); fixture.Services.DrainUi();
            Equal(1, fixture.Published.Count, "Correct live packets can still complete after rejecting early metadata");
        }
    }

    private static void EarlyPacketBoundsAndRetirement()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); Guid id = Guid.NewGuid();
            for (int index = 1; index < 300; index++) fixture.Bridge.ReceivePacket(new Packet { TransferId = id, Index = index, Count = 300,
                TotalLength = 300 * 256, ChunkSize = 256, Profile = VideoProfile.Compatible, Data = new byte[256] });
            Equal(256, fixture.Bridge.EarlyPacketCount, "One unknown transfer retains at most 256 chunks");
            Equal(64 * 1024, fixture.Bridge.EarlyPacketBytes, "Unknown transfer memory is capped at 64KiB, not its advertised total size");
            fixture.Bridge.ReceivePacket(new Packet { TransferId = Guid.NewGuid(), Index = 1, Count = 300, TotalLength = 300 * 256,
                ChunkSize = 256, Profile = VideoProfile.Compatible, Data = new byte[256] });
            Equal(256, fixture.Bridge.EarlyPacketCount, "A second unknown transfer cannot grow pending storage");
            fixture.Services.Now += TimeSpan.FromMilliseconds(ServerAudioBridge.EarlyPacketLifetimeMilliseconds);
            Equal(0, fixture.Bridge.EarlyPacketBytes, "Unknown packets expire at the five-second eligibility boundary");
            True(!fixture.Bridge.IsReceiving && fixture.Published.Count == 0, "Bounds/expiry never create a clipboard transfer");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); Packet[] stale = Packets(fixture.Session, Guid.NewGuid(), "expired early packets");
            for (int index = 1; index < stale.Length; index++) fixture.Bridge.ReceivePacket(stale[index]);
            fixture.Services.Now += TimeSpan.FromSeconds(6); fixture.Bridge.ReceivePacket(stale[0]); fixture.Services.DrainUi();
            Equal(0, fixture.Published.Count, "An authenticated late header cannot drain expired chunks");
            for (int index = 1; index < stale.Length; index++) fixture.Bridge.ReceivePacket(stale[index]); fixture.Services.DrainUi();
            Equal(1, fixture.Published.Count, "Fresh chunks after a late header can complete normally");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); Packet[] wrongSession = Packets(Guid.NewGuid(), Guid.NewGuid(), "wrong session early");
            for (int index = 1; index < wrongSession.Length; index++) fixture.Bridge.ReceivePacket(wrongSession[index]); fixture.Bridge.ReceivePacket(wrongSession[0]); fixture.Services.DrainUi();
            Equal(0, fixture.Bridge.EarlyPacketCount, "A wrong-session header discards its pending data");
            True(!fixture.Bridge.IsReceiving && fixture.Published.Count == 0, "Session validation still precedes assembler allocation");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); Packet[] old = Packets(fixture.Session, Guid.NewGuid(), "known old copy"); Packet[] fresh = Packets(fixture.Session, Guid.NewGuid(), "new valid copy");
            fixture.Bridge.ReceivePacket(old[0]);
            for (int index = 1; index < fresh.Length; index++) fixture.Bridge.ReceivePacket(fresh[index]);
            fixture.Bridge.ReceivePacket(fresh[0]); fixture.Bridge.ReceivePacket(old[0]); fixture.Services.DrainUi();
            True(fixture.Published.Count == 1 && fixture.Published[0] == fresh[0].TransferId, "A new authenticated copy drains its early routes and retires the known old copy before header replay");
        }
        foreach (string retire in new[] { "benchmark", "capture_fault", "dispose" })
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); Packet[] packets = Packets(fixture.Session, Guid.NewGuid(), "retire early " + retire); fixture.Bridge.ReceivePacket(packets[1]);
            if (retire == "benchmark") { fixture.Bridge.SetBenchmarkMode(true); fixture.Bridge.SetBenchmarkMode(false); }
            else if (retire == "capture_fault") fixture.Services.CaptureError(new Exception("Synthetic all-route fault."));
            else fixture.Bridge.Dispose();
            Equal(0, fixture.Bridge.EarlyPacketCount, "Early buffers clear on " + retire);
            foreach (Packet packet in packets) fixture.Bridge.ReceivePacket(packet); fixture.Services.DrainUi();
            Equal(0, fixture.Published.Count, "Old queued packets cannot revive after " + retire);
        }
    }

    private static void DecodeRetriesAndIncomingMetadata()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); fixture.Bridge.AdditionalRetries = 2;
            int decodes = 0, starts = 0, completions = 0; var retries = new List<int>();
            fixture.Services.BeforeDecode = delegate { if (++decodes <= 2) throw new InvalidOperationException("Synthetic decode corruption."); };
            fixture.Bridge.IncomingStarted = delegate { starts++; };
            fixture.Bridge.IncomingRetrying = delegate(Guid id, int retry, int maximum) { retries.Add(retry); Equal(2, maximum, "Retry metadata preserves its configured bound"); };
            fixture.Bridge.IncomingCompleted = delegate(Guid id, int bytes, long elapsed) { completions++; True(fixture.Services.Publications.Count == 1 && fixture.Published.Count == 1 && bytes > 24 && elapsed >= 0, "Completion metadata follows actual publication and echo suppression"); };
            Packet[] packets = Packets(fixture.Session, Guid.NewGuid(), "retry fixture");
            for (int pass = 0; pass < 3; pass++) { foreach (Packet packet in packets) fixture.Bridge.ReceivePacket(packet); fixture.Services.DrainUi(); }
            True(decodes == 3 && starts == 1 && retries.Count == 2 && retries[0] == 1 && retries[1] == 2 && completions == 1,
                "The same transfer recovers after two failed decodes and publishes exactly once on its final bounded pass");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); fixture.Bridge.AdditionalRetries = 0; int failures = 0, decodes = 0;
            fixture.Services.BeforeDecode = delegate { decodes++; throw new InvalidOperationException("Synthetic exhausted decode."); };
            fixture.Bridge.IncomingFailed = delegate { failures++; };
            Packet[] packets = Packets(fixture.Session, Guid.NewGuid(), "no retry fixture");
            for (int pass = 0; pass < 3; pass++) { foreach (Packet packet in packets) fixture.Bridge.ReceivePacket(packet); fixture.Services.DrainUi(); }
            True(decodes == 1 && failures == 1 && !fixture.Bridge.IsReceiving && fixture.Published.Count == 0,
                "Zero configured retries fails once without acknowledging, publishing or re-decoding further repeats");
        }
    }

    private static void PartialInactivityIsBounded()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); int starts = 0, failures = 0;
            fixture.Bridge.IncomingStarted = delegate { starts++; }; fixture.Bridge.IncomingFailed = delegate { failures++; };
            fixture.Bridge.ReceivePacket(Packets(fixture.Session, Guid.NewGuid(), "delayed UI start")[0]);
            fixture.Services.Now = fixture.Services.Now.AddSeconds(40); fixture.Services.Timer.FireStale();
            fixture.Services.DrainUi();
            True(starts == 0 && failures == 1 && !fixture.Services.Timer.Enabled && !fixture.Bridge.IsReceiving,
                "A delayed start/progress callback cannot revive a stalled transfer without a new block");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); int failures = 0;
            fixture.Bridge.IncomingFailed = delegate(Guid id, string reason) { Equal("audio_inactive_partial", reason, "A partial timeout identifies audio inactivity"); failures++; };
            Packet[] packets = Packets(fixture.Session, Guid.NewGuid(), new string('x', 2048));
            True(packets.Length > 1, "Partial fixture has more than one packet");
            fixture.Bridge.ReceivePacket(packets[0]); fixture.Services.DrainUi();
            fixture.Services.Now = fixture.Services.Now.AddSeconds(39); fixture.Services.Timer.Fire();
            True(fixture.Bridge.IsReceiving && failures == 0, "A redirected native frame still has its bounded delivery grace");
            fixture.Bridge.ReceivePacket(packets[0]); fixture.Services.Now = fixture.Services.Now.AddSeconds(39); fixture.Services.Timer.Fire();
            True(fixture.Bridge.IsReceiving && failures == 0, "A valid duplicate proves active retry audio even without new unique bytes");
            fixture.Services.Now = fixture.Services.Now.AddSeconds(1); fixture.Services.Timer.Fire();
            True(!fixture.Bridge.IsReceiving && failures == 1 && fixture.Published.Count == 0, "Forty seconds pauses partial reception without false completion");
            fixture.Services.Timer.FireStale(); fixture.Services.DrainUi();
            Equal(1, failures, "A stale timer cannot repeat the same waiting notification");
            int resumes = 0;
            fixture.Bridge.IncomingResumed = delegate { resumes++; };
            fixture.Bridge.ReceivePacket(packets[0]); fixture.Services.DrainUi();
            True(!fixture.Bridge.IsReceiving && resumes == 0, "Duplicates cannot resume a stalled transfer without actual new bytes");
            for (int index = 1; index < packets.Length; index++) fixture.Bridge.ReceivePacket(packets[index]);
            fixture.Services.DrainUi();
            True(fixture.Published.Count == 1 && fixture.Services.Publications.Count == 1 && resumes == 1,
                "A long first pass resumes with retained chunks and publishes exactly once after complete validation");
            foreach (Packet packet in packets) fixture.Bridge.ReceivePacket(packet);
            fixture.Services.Timer.FireStale(); fixture.Services.DrainUi();
            True(fixture.Published.Count == 1 && resumes == 1, "Replay after delayed completion cannot resume or publish again");
        }
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); int resumes = 0; fixture.Bridge.IncomingResumed = delegate { resumes++; };
            Packet[] abandoned = Packets(fixture.Session, Guid.NewGuid(), new string('x', 2048));
            fixture.Bridge.ReceivePacket(abandoned[0]); fixture.Services.DrainUi();
            fixture.Services.Now = fixture.Services.Now.AddSeconds(40); fixture.Services.Timer.Fire();
            foreach (Packet packet in Packets(fixture.Session, Guid.NewGuid(), "newer clipboard")) fixture.Bridge.ReceivePacket(packet);
            foreach (Packet packet in abandoned) fixture.Bridge.ReceivePacket(packet);
            fixture.Services.DrainUi();
            True(resumes == 0 && fixture.Services.Publications.Count == 1 && Encoding.UTF8.GetString(fixture.Services.Publications[0].Formats[DataFormats.UnicodeText]).StartsWith("newer clipboard:", StringComparison.Ordinal),
                "A newer clipboard retires stalled state; old packets cannot resurrect it");
        }
    }

    private static void RepeatedFileDropsCompleteIndependently()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Start();
            byte[] content = new byte[1536]; new Random(617).NextBytes(content);
            byte[] wire = BridgeControl.WrapClipboard(fixture.Session, SyntheticFilePayload(content));
            var completed = new List<Guid>(); var started = new List<Guid>();
            fixture.Bridge.IncomingStarted = delegate(Guid id, int bytes) { started.Add(id); };
            fixture.Bridge.IncomingCompleted = delegate(Guid id, int bytes, long elapsed)
            {
                completed.Add(id);
                Equal(wire.Length, bytes, "File completion preserves its exact encoded transfer size");
                Equal(completed.Count, fixture.Services.Publications.Count, "File completion follows successful clipboard publication");
                Equal(completed.Count, fixture.Published.Count, "File completion follows echo suppression");
                True(fixture.Services.Publications[completed.Count - 1].IsFileDrop && elapsed >= 0, "Completion belongs to a decoded file drop");
            };
            for (int operation = 0; operation < 2; operation++)
            {
                Guid id = Guid.NewGuid(); Packet[] packets = Packetize(id, wire);
                fixture.Services.Busy = 1;
                foreach (Packet packet in packets) fixture.Bridge.ReceivePacket(packet);
                fixture.Services.DrainUi();
                Equal(operation, completed.Count, "A busy clipboard must not issue a file completion");
                fixture.Services.Timer.Fire();
                Equal(operation + 1, completed.Count, "A successful retry completes this copy exactly once");
                Equal(id, completed[operation], "Equal file contents do not deduplicate a different copy identity");
                foreach (Packet packet in packets) fixture.Bridge.ReceivePacket(packet);
                fixture.Services.Timer.FireStale(); fixture.Services.DrainUi();
                Equal(operation + 1, completed.Count, "Replayed audio and a stale timer cannot duplicate file completion");
            }
            Equal(2, started.Count, "Both independently copied files have a start event");
            Equal(2, fixture.Services.PreparedFiles.Count, "Each file drop is prepared once without touching the real filesystem");
            Equal(2, fixture.Services.Publications.Count, "Both copies reach the synthetic clipboard");
            True(completed[0] != completed[1], "Two identical file drops retain their distinct transfer identities");
            foreach (DecodedPayload payload in fixture.Services.Publications)
            {
                Equal("repeat.bin", payload.Files[0].Path, "The synthetic file metadata survives decoding");
                True(Convert.ToBase64String(payload.Files[0].Data) == Convert.ToBase64String(content), "Every decoded synthetic file byte is preserved");
            }
        }
    }

    private static byte[] SyntheticFilePayload(byte[] content)
    {
        byte[] raw;
        using (var body = new MemoryStream())
        {
            using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
            { writer.Write(1); writer.Write(true); writer.Write(1); writer.Write("repeat.bin"); writer.Write(false); writer.Write(content.Length); writer.Write(content); }
            raw = body.ToArray();
        }
        using (var result = new MemoryStream())
        {
            byte[] magic = Encoding.ASCII.GetBytes("HCBPAY01"); result.Write(magic, 0, magic.Length);
            using (SHA256 hash = SHA256.Create()) { byte[] digest = hash.ComputeHash(raw); result.Write(digest, 0, digest.Length); }
            byte[] length = BitConverter.GetBytes(raw.Length); result.Write(length, 0, length.Length);
            using (var zip = new DeflateStream(result, CompressionLevel.Fastest, true)) zip.Write(raw, 0, raw.Length);
            return result.ToArray();
        }
    }

    private static Packet[] Packetize(Guid id, byte[] wire)
    {
        int count = (wire.Length + 255) / 256; var packets = new Packet[count];
        for (int index = 0; index < count; index++)
        {
            byte[] chunk = new byte[Math.Min(256, wire.Length - index * 256)]; Buffer.BlockCopy(wire, index * 256, chunk, 0, chunk.Length);
            packets[index] = new Packet { TransferId = id, Index = index, Count = count, TotalLength = wire.Length, ChunkSize = 256, Profile = VideoProfile.Compatible, Data = chunk };
        }
        return packets;
    }

    private static void LiveRetryPolicyUpdatesPendingTransfer()
    {
        foreach (bool increase in new[] { true, false })
        using (Fixture fixture = new Fixture())
        {
            fixture.Start(); fixture.Bridge.AdditionalRetries = increase ? 0 : 2;
            int decodes = 0, retries = 0;
            fixture.Services.BeforeDecode = delegate { if (++decodes == 1) throw new InvalidOperationException("Synthetic live policy decode."); };
            fixture.Bridge.IncomingRetrying = delegate { retries++; };
            Packet[] packets = Packets(fixture.Session, Guid.NewGuid(), new string('z', 1024));
            fixture.Bridge.ReceivePacket(packets[0]); fixture.Services.DrainUi();
            fixture.Bridge.AdditionalRetries = increase ? 2 : 0;
            for (int index = 1; index < packets.Length; index++) fixture.Bridge.ReceivePacket(packets[index]); fixture.Services.DrainUi();
            foreach (Packet packet in packets) fixture.Bridge.ReceivePacket(packet); fixture.Services.DrainUi();
            True(increase ? decodes == 2 && retries == 1 && fixture.Published.Count == 1 : decodes == 1 && retries == 0 && fixture.Published.Count == 0,
                "Changing retry policy on a pending partial transfer applies its " + (increase ? "increased" : "decreased") + " live bound without resetting used retries");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Guid Session = Guid.NewGuid();
        internal readonly Control Owner = new Control();
        internal readonly FakeServices Services = new FakeServices();
        internal readonly List<Guid> Published = new List<Guid>();
        internal readonly List<BridgeReport> Reports = new List<BridgeReport>();
        internal readonly List<string> Statuses = new List<string>(), Events = new List<string>();
        internal Action<string> StatusChanged;
        internal readonly ServerAudioBridge Bridge;
        internal Fixture()
        {
            Services.Events = Events;
            Bridge = new ServerAudioBridge(Owner, Session, delegate(Guid id) { Published.Add(id); Events.Add("published"); },
                delegate(string message) { Statuses.Add(message); if (StatusChanged != null) StatusChanged(message); }, Reports.Add, Services);
        }
        internal void Start() { Bridge.Start("fixture-output"); Services.DrainWorkers(); Services.DrainUi(); Statuses.Clear(); }
        public void Dispose() { Bridge.Dispose(); Services.DrainWorkers(); Services.DrainUi(); Owner.Dispose(); }
    }
    private sealed class FakeServices : ServerAudioBridgeServices
    {
        private readonly Queue<Action> workers = new Queue<Action>(), ui = new Queue<Action>();
        internal readonly FakeCapture Capture = new FakeCapture();
        internal readonly List<FakeCapture> AllCaptures = new List<FakeCapture>();
        internal readonly Dictionary<string, Action<float[]>> RouteSamples = new Dictionary<string, Action<float[]>>();
        internal readonly Dictionary<string, Action<Exception>> RouteErrors = new Dictionary<string, Action<Exception>>();
        internal readonly Dictionary<string, AudioReceiveMultiplexer> Receivers = new Dictionary<string, AudioReceiveMultiplexer>();
        internal Func<string, AudioModemKind, INativeAudioDecoder> NativeFactory;
        internal Action<AudioReceiveMultiplexer> OnReceiverCreated;
        internal AudioEndpoint[] RenderEndpoints = new AudioEndpoint[0];
        internal readonly List<DecodedPayload> Publications = new List<DecodedPayload>();
        internal readonly List<Guid> PreparedFiles = new List<Guid>();
        internal FakeTimer Timer;
        internal FakePaste Paste;
        internal int CaptureStarts, RenderEnumerations, PasteCreates, TimerCreates, PublishAttempts, Busy;
        internal Exception RenderEnumerationError;
        internal string Endpoint;
        internal List<string> Events;
        internal Action OnCaptureStart, BeforeDecode, BeforePublish;
        internal Action<Exception> CaptureError;
        internal Action<float[]> Samples;
        internal DateTime Now = DateTime.UtcNow;
        internal override DateTime UtcNow { get { return Now; } }
        internal override void Queue(Action action) { workers.Enqueue(action); }
        internal override void Dispatch(Control owner, Action action) { lock (ui) ui.Enqueue(action); }
        internal void DrainWorkers() { while (workers.Count != 0) workers.Dequeue()(); }
        internal void DrainUi() { while (true) { Action action; lock (ui) { if (ui.Count == 0) return; action = ui.Dequeue(); } action(); } }
        internal override AudioReceiveMultiplexer CreateReceiver(Action<Packet> receive, Action<byte[]> fastReceipt, Action<byte[]> compactFileReport, string endpoint)
        {
            var mux = NativeFactory == null ? base.CreateReceiver(receive, fastReceipt, compactFileReport, endpoint) :
                new AudioReceiveMultiplexer(receive, NativeModemsEnabled, fastReceipt, compactFileReport, endpoint, delegate(AudioModemKind kind) { return NativeFactory(endpoint, kind); });
            Receivers[endpoint ?? "default"] = mux;
            if (OnReceiverCreated != null) OnReceiverCreated(mux);
            return mux;
        }
        internal override AudioEndpoint[] EnumerateRenderEndpoints() { RenderEnumerations++; if (RenderEnumerationError != null) throw RenderEnumerationError; return RenderEndpoints; }
        internal override IDisposable StartCapture(string endpoint, Action<float[]> samples, Action<Exception> error)
        {
            CaptureStarts++; Endpoint = endpoint; CaptureError = error; Samples = samples;
            RouteSamples[endpoint ?? "default"] = samples; RouteErrors[endpoint ?? "default"] = error;
            FakeCapture capture = AllCaptures.Count == 0 ? Capture : new FakeCapture(); AllCaptures.Add(capture);
            if (OnCaptureStart != null) OnCaptureStart();
            return capture;
        }
        internal override IServerDeferredPaste CreatePaste(Control owner, Func<bool> receiving) { PasteCreates++; return Paste = new FakePaste(Events); }
        internal override IServerPublicationTimer CreateTimer(Action tick) { TimerCreates++; return Timer = new FakeTimer(tick); }
        internal override DecodedPayload Decode(byte[] bytes) { if (BeforeDecode != null) BeforeDecode(); return base.Decode(bytes); }
        internal override void PrepareFiles(DecodedPayload payload, string root, Guid id) { PreparedFiles.Add(id); }
        internal override void Publish(DecodedPayload payload, string root, Guid id) { PublishAttempts++; if (BeforePublish != null) BeforePublish(); if (Busy > 0) { Busy--; throw new ExternalException("synthetic busy clipboard"); } Publications.Add(payload); Events.Add("publish"); }
    }
    private sealed class FakeCapture : IDisposable { internal int Disposals; public void Dispose() { Disposals++; } }
    private sealed class FakePaste : IServerDeferredPaste
    {
        private readonly List<string> events;
        internal int Completions, Cancellations;
        internal bool Disposed;
        internal FakePaste(List<string> events) { this.events = events; }
        public void Complete() { Completions++; events.Add("paste"); }
        public void Cancel() { Cancellations++; }
        public void Dispose() { Disposed = true; }
    }
    private sealed class FakeTimer : IServerPublicationTimer
    {
        private readonly Action tick;
        internal bool Enabled, Disposed;
        internal FakeTimer(Action tick) { this.tick = tick; }
        internal void Fire() { if (Enabled && !Disposed) tick(); }
        internal void FireStale() { tick(); }
        public void Start() { Enabled = true; }
        public void Stop() { Enabled = false; }
        public void Dispose() { Disposed = true; Enabled = false; }
    }
    private static void True(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual, string message) { True(EqualityComparer<T>.Default.Equals(expected, actual), message + " (expected " + expected + ", actual " + actual + ")"); }
}
