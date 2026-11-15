using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

internal static class ReceiverAutoCameraTests
{
    private static int checks;
    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            CatalogFiltersUnknownNamesAndPreferredIds();
            CatalogCandidateKeepsCompatibilityFallback();
            ExhaustedCandidatesStayClosedUntilExplicitRetry();
            EmptyEnumerationsConsumeScheduledProbeBudget();
            ConfirmedSourceGetsItsOwnReconnectBudget();
            SavedCustomCameraRemainsExplicitlyAuthorized();
            FreshClientStartsDiscovery();
            OrdinaryCameraIsSkippedAndOnlyOurControlSelects();
            ExactPreferencesPrecedeEnumerationAndNamesProveNothing();
            ManualAndOffInvalidateOldProbes();
            EnablingDiscoveryRevalidatesAManualCapture();
            ModeRefreshPreservesUnverifiedProbeBoundary();
            BlockedOpenAndDisposalDiscardCallbacks();
            ProbeFailureUnplugAndRetryDoNotFreezeDiscovery();
            FailedOpenAndNoFramesAdvanceToTheNextCandidate();
            SettingsAndLanguageRoundTrip();
            Console.WriteLine("Automatic camera checks passed ({0} assertions; synthetic frames, fake captures and isolated settings only).", checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CatalogFiltersUnknownNamesAndPreferredIds()
    {
        using (var fixture = new Fixture())
        {
            fixture.Devices[0] = new CameraDevice("Chicony USB2.0 Camera", "ordinary");
            fixture.Devices.Add(new CameraDevice("Elgato Facecam MK.2 (redirected)", "catalog-substring"));
            fixture.Devices.Add(new CameraDevice("Horizon virtual camera", "persuasive-name"));
            fixture.Devices[1] = new CameraDevice("rAzEr kIyO pRo", "ours");
            fixture.Services.PreferredCameraIds = new[] { "ordinary", "catalog-substring", "persuasive-name", "ours", "ours" };
            using (ReceiverContext receiver = fixture.Create())
            {
                PumpUntil(delegate { return fixture.AnyCapture("ordinary") != null || fixture.AnyCapture("ours") != null; }, "Automatic discovery admits an initial exact candidate.");
                Check(fixture.OpenCount("ordinary") == 0 && fixture.OpenCount("catalog-substring") == 0 && fixture.OpenCount("persuasive-name") == 0,
                    "Unknown names, catalog substrings and optional ID hints cannot authorize automatic camera opens.");
                FakeCapture ours = WaitCapture(fixture, "ours");
                Check(receiver.SelectedCameraId == null && fixture.SelectionWrites == 0 && fixture.Publications == 0,
                    "A case-insensitive exact catalog match permits probing but does not itself select or publish.");
                receiver.SelectDevice(fixture.Devices[0]); WaitCapture(fixture, "ordinary");
                PumpUntil(delegate { return ours.Disposed; }, "Manual selection retires the catalog probe.");
                Check(receiver.SelectedCameraId == "ordinary" && !AutoOption(receiver).Checked && File.ReadAllText(Path.Combine(fixture.Root, "camera.id")) == "ordinary",
                    "An explicit manual noncatalog choice remains supported and persistently disables automatic selection.");
            }
        }
        using (var fixture = new Fixture())
        {
            fixture.Devices.Clear(); fixture.Devices.Add(new CameraDevice("Chicony USB2.0 Camera", "ordinary"));
            fixture.Services.PreferredCameraIds = new[] { "ordinary" };
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver);
                Check(fixture.OpenCount("ordinary") == 0, "No catalog or saved match leaves an unknown camera available only for manual selection.");
                typeof(ReceiverContext).GetMethod("StartCapture", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(CameraDevice), typeof(bool) }, null)
                    .Invoke(receiver, new object[] { fixture.Devices[0], true });
                Check(CurrentSession(receiver) == null && fixture.OpenCount("ordinary") == 0, "The capture boundary rejects unknown automatic candidates even outside the ordinary candidate selector.");
                fixture.Devices.Add(new CameraDevice("Elgato Facecam MK.2", "later"));
                Refresh(receiver); WaitCapture(fixture, "later");
                Check(fixture.OpenCount("ordinary") == 0 && fixture.OpenCount("later") == 1, "A newly appearing catalog ID is discovered without retrying unrelated devices.");
            }
        }
    }

    private static void CatalogCandidateKeepsCompatibilityFallback()
    {
        using (var fixture = new Fixture())
        {
            fixture.Devices.RemoveAt(0);
            int failedDirectShow = 0;
            fixture.Services.OpenCaptureBackend = delegate(string id, Action<Bitmap> frame, CaptureBackend backend) {
                fixture.Backends.Add(backend);
                if (backend == CaptureBackend.DirectShowHighest) { Interlocked.Increment(ref failedDirectShow); throw new IOException("Synthetic initial DirectShow failure."); }
                return fixture.Open(id, frame);
            };
            using (ReceiverContext receiver = fixture.Create())
            {
                PumpUntil(delegate { return Volatile.Read(ref failedDirectShow) == 1; }, "The catalog candidate's initial DirectShow open fails.");
                Refresh(receiver); fixture.Now = 4000; Refresh(receiver);
                FakeCapture capture = WaitCapture(fixture, "ours");
                Check(fixture.Backends.Count == 2 && fixture.Backends[0] == CaptureBackend.DirectShowHighest && fixture.Backends[1] == CaptureBackend.MediaFoundation,
                    "A failed initial DirectShow open retains the existing Media Foundation compatibility fallback for the same catalog ID.");
                Guid session = Guid.NewGuid(); Send(capture, session, BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0));
                PumpUntil(delegate { return receiver.SelectedCameraId == "ours"; }, "The compatibility capture confirms only after CRC-valid service control.");
                Check(fixture.SelectionWrites == 1 && fixture.Publications == 0, "A compatibility fallback does not bypass control validation or publish probe data.");
            }
        }
    }

    private static void ExhaustedCandidatesStayClosedUntilExplicitRetry()
    {
        foreach (bool timeout in new[] { false, true })
        using (var fixture = new Fixture())
        {
            fixture.Devices.RemoveAt(1);
            fixture.Services.OpenCaptureBackend = delegate(string id, Action<Bitmap> frame, CaptureBackend backend) { fixture.Backends.Add(backend); return fixture.Open(id, frame); };
            using (ReceiverContext receiver = fixture.Create())
            {
                CaptureBackend[] expected = { CaptureBackend.DirectShowHighest, CaptureBackend.MediaFoundation, CaptureBackend.DirectShowDefault, CaptureBackend.MediaFoundationDefault, CaptureBackend.DirectShow720 };
                for (int attempt = 0; attempt < expected.Length; attempt++)
                {
                    FakeCapture capture = WaitCapture(fixture, "ordinary");
                    Check(fixture.Backends.Count == attempt + 1 && fixture.Backends[attempt] == expected[attempt], "Automatic compatibility makes one finite pass through each existing backend.");
                    if (timeout) { fixture.Now += 6000; receiver.PollCaptureRetry(); } else capture.Fail();
                    PumpUntil(delegate { return capture.Disposed; }, "The failed automatic candidate is released.");
                    fixture.Now += 60000; Refresh(receiver);
                }
                for (int refresh = 0; refresh < 3; refresh++) { fixture.Now += 60000; receiver.PollCaptureRetry(); Refresh(receiver); }
                Check(fixture.OpenCount("ordinary") == 5 && CurrentSession(receiver) == null,
                    "An exhausted exact catalog ID is not reopened by ordinary enumeration or retry polls.");
                fixture.Devices.Clear(); Refresh(receiver);
                fixture.Devices.Add(new CameraDevice("Logitech C270 HD Webcam", "ordinary")); Refresh(receiver);
                Check(fixture.OpenCount("ordinary") == 5 && CurrentSession(receiver) == null, "Removing and re-enumerating the same failed ID does not reset its budget.");
                fixture.Devices.Add(new CameraDevice("Logitech C270 HD Webcam", "replacement")); Refresh(receiver);
                FakeCapture replacement = WaitCapture(fixture, "replacement");
                Guid session = Guid.NewGuid(); Send(replacement, session, BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0));
                PumpUntil(delegate { return receiver.SelectedCameraId == "replacement"; }, "A fresh catalog ID remains eligible after another ID is exhausted.");
                fixture.Devices.RemoveAt(1); Refresh(receiver);
                PumpUntil(delegate { return replacement.Disposed; }, "The confirmed camera is released on unplug.");
                fixture.Now += 60000; Refresh(receiver);
                Check(fixture.OpenCount("ordinary") == 5 && CurrentSession(receiver) == null,
                    "Confirming and losing another camera cannot revive exhausted unrelated candidates.");
                AutoOption(receiver).PerformClick(); AutoOption(receiver).PerformClick();
                PumpUntil(delegate { return fixture.OpenCount("ordinary") == 6; }, "An explicit automatic OFF/ON toggle authorizes a fresh compatibility pass.");
                Check(fixture.PayloadDecodes == 0 && fixture.Publications == 0, "Finite probing and retry reset never publish clipboard data.");
            }
        }
    }

    private static void SavedCustomCameraRemainsExplicitlyAuthorized()
    {
        using (var fixture = new Fixture())
        {
            fixture.Devices.RemoveAt(1); fixture.Devices[0] = new CameraDevice("Custom noncatalog source", "ordinary");
            File.WriteAllText(Path.Combine(fixture.Root, "camera.id"), "ordinary");
            using (ReceiverContext receiver = fixture.Create())
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    FakeCapture capture = WaitCapture(fixture, "ordinary");
                    Check(fixture.OpenCount("ordinary") == attempt + 1, "The user's saved exact custom camera is eligible despite its noncatalog name.");
                    capture.Fail(); PumpUntil(delegate { return capture.Disposed; }, "The saved camera's failed graph is retired.");
                    fixture.Now += 60000; Refresh(receiver);
                }
                Check(fixture.OpenCount("ordinary") == 5 && CurrentSession(receiver) == null && File.ReadAllText(Path.Combine(fixture.Root, "camera.id")) == "ordinary" && fixture.SelectionWrites == 0,
                    "A saved custom ID retains its preference but is still limited to five unverified automatic opens.");
                receiver.RefreshCameraModes();
                Check(CurrentSession(receiver) == null && fixture.OpenCount("ordinary") == 5, "A direct mode-refresh path cannot bypass an exhausted automatic probe budget.");
                receiver.SelectDevice(fixture.Devices[0]);
                PumpUntil(delegate { return fixture.OpenCount("ordinary") == 6; }, "An explicit manual choice can retry the exhausted custom camera.");
                Check(!AutoOption(receiver).Checked && File.ReadAllText(Path.Combine(fixture.Root, "camera.auto")) == "0", "Manual retry keeps its existing persistent automatic-OFF contract.");
            }
        }
    }

    private static void EmptyEnumerationsConsumeScheduledProbeBudget()
    {
        using (var fixture = new Fixture())
        {
            fixture.Devices.RemoveAt(1);
            CameraDevice catalog = fixture.Devices[0];
            using (ReceiverContext receiver = fixture.Create())
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    FakeCapture capture = WaitCapture(fixture, "ordinary");
                    fixture.Devices.Clear(); Refresh(receiver);
                    PumpUntil(delegate { return capture.Disposed; }, "An empty device snapshot retires the pending probe without a capture-error callback.");
                    fixture.Devices.Add(catalog); fixture.Now += 60000; Refresh(receiver);
                }
                Check(fixture.OpenCount("ordinary") == 5 && CurrentSession(receiver) == null,
                    "The finite budget counts scheduled opens, so empty enumeration cannot cause endless reopen cycles without failure callbacks.");
            }
        }
    }

    private static void ConfirmedSourceGetsItsOwnReconnectBudget()
    {
        using (var fixture = new Fixture())
        {
            fixture.Devices.RemoveAt(1);
            using (ReceiverContext receiver = fixture.Create())
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    FakeCapture failed = WaitCapture(fixture, "ordinary"); failed.Fail();
                    PumpUntil(delegate { return failed.Disposed; }, "An unverified compatibility capture is retired.");
                    fixture.Now += 60000; Refresh(receiver);
                }
                FakeCapture fifth = WaitCapture(fixture, "ordinary");
                Guid session = Guid.NewGuid(); Send(fifth, session, BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0));
                PumpUntil(delegate { return receiver.SelectedCameraId == "ordinary"; }, "The fifth allowed probe positively identifies its source.");
                fifth.Fail(); PumpUntil(delegate { return fifth.Disposed; }, "The identified source can later experience a transient capture failure.");
                fixture.Now += 60000; Refresh(receiver);
                PumpUntil(delegate { return fixture.OpenCount("ordinary") == 6; }, "Validated service control resets that camera's own budget so the verified source can reconnect.");
                Check(fixture.SelectionWrites == 1 && fixture.Publications == 0, "Reconnecting a verified source still starts as a probe without publishing or prematurely rewriting its selection.");
            }
        }
    }

    private static object CurrentSession(ReceiverContext receiver)
    { return typeof(ReceiverContext).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(receiver); }

    private static void OrdinaryCameraIsSkippedAndOnlyOurControlSelects()
    {
        using (var fixture = new Fixture())
        using (ReceiverContext receiver = fixture.Create())
        {
            FakeCapture ordinary = WaitCapture(fixture, "ordinary");
            ToolStripItem cameraItem = receiver.Menu.Items[0], settings = receiver.Menu.Items["settings"];
            using (var blank = new Bitmap(640, 480)) ordinary.Frame(blank);
            Guid transfer = new Guid("aaaaaaaa-1111-2222-3333-444444444444");
            var snapshot = new ClipboardSnapshot(); snapshot.Formats.Add(DataFormats.UnicodeText, Encoding.Unicode.GetBytes("probe payload must be ignored"));
            string description; byte[] clipboard = ClipboardPayload.Encode(snapshot, out description);
            Check(clipboard.Length <= VisualProtocol.ChunkSize, "Synthetic clipboard packet fits one visual frame");
            Send(ordinary, transfer, clipboard);
            byte[] corruptFormat = BridgeControl.CameraFormat(transfer, Guid.NewGuid(), 640, 480, 30, 2); corruptFormat[60] ^= 1;
            Send(ordinary, transfer, corruptFormat);
            byte[] wrongSession = BridgeControl.Hello(Guid.NewGuid(), Guid.Empty, Guid.Empty, 0, "Razer Kiyo Pro"); Send(ordinary, transfer, wrongSession);
            using (Bitmap badCrc = VisualProtocol.EncodeFrame(transfer, 0, 1, 60, BridgeControl.Hello(transfer, Guid.Empty, Guid.Empty, 0)))
            {
                // Flip the valid Hello's seconds 0 -> 1, preserving framing and
                // semantic validity while invalidating the visual wire CRC.
                int bit = (36 + 56) * 8, left = (bit % 60 + 2) * 10, top = (bit / 60 + 2) * 10;
                Brush flipped = badCrc.GetPixel(left + 5, top + 5).R > 128 ? Brushes.Black : Brushes.White;
                using (Graphics graphics = Graphics.FromImage(badCrc)) graphics.FillRectangle(flipped, left, top, 10, 10);
                Check(VisualProtocol.DecodeFrame(badCrc) == null, "The synthetic damaged control has an invalid visual wire CRC");
                ordinary.Frame(badCrc);
            }
            PumpFor(40);
            Check(receiver.SelectedCameraId == null && fixture.SelectionWrites == 0 && fixture.Publications == 0 && !receiver.IsReceiving,
                "Blank, non-control, corrupt, unbound and invalid visual CRC packets cannot select or publish from a probe");
            fixture.Now = 6000; receiver.PollCaptureRetry();
            FakeCapture ours = WaitCapture(fixture, "ours");
            PumpUntil(delegate { return ordinary.Disposed; }, "The ordinary camera is released before the next candidate");
            Check(Object.ReferenceEquals(cameraItem, receiver.Menu.Items[0]) && Object.ReferenceEquals(settings, receiver.Menu.Items["settings"]),
                "Probe advancement preserves existing tray menu item identities");
            Send(ours, transfer, BridgeControl.Hello(transfer, Guid.Empty, Guid.Empty, 0, "Razer Kiyo Pro"));
            PumpUntil(delegate { return receiver.SelectedCameraId == "ours"; }, "A real CRC-valid own bridge Hello selects the matching exact camera");
            Check(fixture.SelectionWrites == 1 && File.ReadAllText(Path.Combine(fixture.Root, "camera.id")) == "ours" && !ours.Disposed && fixture.OpenCount("ours") == 1,
                "Confirmation saves the exact ID and retains the already open capture");
            Check(fixture.Publications == 0 && fixture.PayloadDecodes == 0, "Discovery control handling never decodes or publishes clipboard payloads");
        }
        foreach (bool acknowledge in new[] { false, true })
        using (var fixture = new Fixture())
        using (ReceiverContext receiver = fixture.Create())
        {
            FakeCapture capture = WaitCapture(fixture, "ordinary"); Guid session = Guid.NewGuid();
            Send(capture, session, acknowledge ? BridgeControl.Acknowledge(session, Guid.NewGuid()) : BridgeControl.CameraFormat(session, Guid.NewGuid(), 640, 480, 30, 2));
            PumpUntil(delegate { return receiver.SelectedCameraId == "ordinary"; }, "CRC-valid bound CameraFormat and ACK can each identify an own bridge source");
            if (!acknowledge)
            {
                fixture.Now = 2000; receiver.PollCaptureRetry(); PumpFor(80);
                Check(fixture.OpenCount("ordinary") == 1, "A probe already delivering the announced dimensions needs no redundant capture restart after confirmation");
            }
        }
    }

    private static void ExactPreferencesPrecedeEnumerationAndNamesProveNothing()
    {
        using (var fixture = new Fixture())
        {
            File.WriteAllText(Path.Combine(fixture.Root, "camera.id"), "ours");
            fixture.Devices[0] = new CameraDevice("Horizon virtual camera", "ordinary");
            using (ReceiverContext receiver = fixture.Create())
            {
                WaitCapture(fixture, "ours"); Check(fixture.OpenCount("ordinary") == 0, "The saved exact ID is probed before enumeration order or a persuasive friendly name");
            }
        }
        using (var fixture = new Fixture())
        {
            File.WriteAllText(Path.Combine(fixture.Root, "camera.id"), "ordinary");
            FieldInfo ids = typeof(ReceiverServices).GetField("PreferredCameraIds", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(ids != null, "An available exact device ID list can prioritize automatic candidates");
            ids.SetValue(fixture.Services, new[] { "missing", "ours", "ours" });
            using (ReceiverContext receiver = fixture.Create())
            {
                FakeCapture saved = WaitCapture(fixture, "ordinary"); Check(fixture.OpenCount("ours") == 0 && fixture.OpenCount("missing") == 0, "The saved exact ID precedes optional preferred IDs and unavailable IDs are never opened");
                fixture.Now = 6000; receiver.PollCaptureRetry(); WaitCapture(fixture, "ours");
                Check(fixture.OpenCount("ordinary") == 1 && saved.Disposed, "Preferred catalog candidates follow an unusable saved camera without duplicate opens");
            }
        }
    }

    private static void ManualAndOffInvalidateOldProbes()
    {
        using (var fixture = new Fixture())
        using (ReceiverContext receiver = fixture.Create())
        {
            FakeCapture old = WaitCapture(fixture, "ordinary"); Guid session = Guid.NewGuid();
            Send(old, session, BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0));
            AutoOption(receiver).PerformClick();
            PumpUntil(delegate { return old.Disposed; }, "Turning automatic selection off promptly releases the probe");
            PumpFor(40); Check(receiver.SelectedCameraId == null && fixture.SelectionWrites == 0 && File.ReadAllText(Path.Combine(fixture.Root, "camera.auto")) == "0",
                "A proof queued before the toggle cannot select after automatic selection is disabled");
            fixture.Now = 60000; receiver.PollCaptureRetry(); PumpFor(40);
            Check(fixture.OpenCount("ours") == 0 && fixture.OpenCount("ordinary") == 1, "OFF remains off across timer polls");
            AutoOption(receiver).PerformClick();
            PumpUntil(delegate { return fixture.OpenCount("ordinary") == 2; }, "Re-enabling automatic selection starts a new probe generation");
            FakeCapture second = WaitCapture(fixture, "ordinary");
            Send(old, session, BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0));
            PumpFor(20); Check(receiver.SelectedCameraId == null, "Old-generation frame callbacks cannot confirm a replacement probe");
            receiver.SelectDevice(new CameraDevice("Manual camera", "ours")); WaitCapture(fixture, "ours");
            Send(second, session, BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0));
            PumpUntil(delegate { return second.Disposed; }, "An explicit manual choice releases an automatic probe");
            Check(receiver.SelectedCameraId == "ours" && !AutoOption(receiver).Checked && File.ReadAllText(Path.Combine(fixture.Root, "camera.auto")) == "0",
                "A manual camera selection persistently overrides automatic discovery");
            Check(fixture.SelectionWrites == 1, "Stale queued proof never overwrites the explicit manual selection");
        }
    }

    private static void BlockedOpenAndDisposalDiscardCallbacks()
    {
        using (var fixture = new Fixture())
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            fixture.BeforeOpen = delegate { entered.Set(); release.WaitOne(); };
            ReceiverContext receiver = fixture.Create();
            try
            {
                PumpUntil(delegate { return entered.WaitOne(0); }, "The synthetic slow open runs on a worker");
                Check(fixture.OpenThread != Thread.CurrentThread.ManagedThreadId, "Driver capture operations never run on the tray thread");
                AutoOption(receiver).PerformClick(); release.Set();
                PumpUntil(delegate { FakeCapture value = fixture.AnyCapture("ordinary"); return value != null && value.Disposed; }, "An open completing after OFF is disposed before it can start");
                Check(!fixture.AnyCapture("ordinary").Started, "A canceled slow probe never starts its capture graph");
                fixture.BeforeOpen = null; AutoOption(receiver).PerformClick(); FakeCapture active = WaitCapture(fixture, "ordinary");
                Guid session = Guid.NewGuid(); Send(active, session, BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0));
                receiver.Dispose();
                PumpUntil(delegate { return active.Disposed; }, "Disposal releases the active probe");
                Check(fixture.SelectionWrites == 0 && fixture.Publications == 0, "A queued proof after disposal has no selection, settings or clipboard side effects");
            }
            finally { release.Set(); receiver.Dispose(); }
        }
    }

    private static void EnablingDiscoveryRevalidatesAManualCapture()
    {
        using (var fixture = new Fixture())
        {
            File.WriteAllText(Path.Combine(fixture.Root, "camera.auto"), "0");
            using (ReceiverContext receiver = fixture.Create())
            {
                receiver.SelectDevice(new CameraDevice("Manual camera", "ordinary"));
                FakeCapture manual = WaitCapture(fixture, "ordinary");
                AutoOption(receiver).PerformClick();
                PumpUntil(delegate { return manual.Disposed && fixture.OpenCount("ordinary") == 2; }, "Enabling discovery revalidates the manual capture as a new probe");
                FakeCapture probe = WaitCapture(fixture, "ordinary");
                Guid transfer = Guid.NewGuid();
                var snapshot = new ClipboardSnapshot(); snapshot.Formats.Add(DataFormats.UnicodeText, Encoding.Unicode.GetBytes("still unverified"));
                string description; Send(probe, transfer, ClipboardPayload.Encode(snapshot, out description)); PumpFor(40);
                Check(fixture.PayloadDecodes == 0 && fixture.Publications == 0, "Enabling automatic selection cannot leave an unverified manual capture publishing payloads");
            }
        }
    }

    private static void ModeRefreshPreservesUnverifiedProbeBoundary()
    {
        using (var fixture = new Fixture())
        {
            fixture.Devices.RemoveAt(1);
            File.WriteAllText(Path.Combine(fixture.Root, "camera.id"), "ordinary");
            using (ReceiverContext receiver = fixture.Create())
            {
                WaitCapture(fixture, "ordinary");
                receiver.Menu.Items["video.refresh"].PerformClick();
                PumpUntil(delegate { return fixture.OpenCount("ordinary") == 2; }, "A user mode refresh restarts the exact unverified camera");
                FakeCapture refreshed = WaitCapture(fixture, "ordinary");
                Guid transfer = Guid.NewGuid(); var snapshot = new ClipboardSnapshot();
                snapshot.Formats.Add(DataFormats.UnicodeText, Encoding.Unicode.GetBytes("mode refresh must preserve verification"));
                string description; byte[] payload = ClipboardPayload.Encode(snapshot, out description);
                Send(refreshed, transfer, payload); PumpFor(40);
                Check(fixture.PayloadDecodes == 0 && fixture.Publications == 0 && fixture.SelectionWrites == 0,
                    "Refreshing modes cannot promote a saved but unverified automatic camera or publish its clipboard payload");
                var resolutions = (ToolStripMenuItem)receiver.Menu.Items["video.resolution"];
                resolutions.DropDownItems["video.resolution.auto"].PerformClick();
                PumpUntil(delegate { return fixture.OpenCount("ordinary") == 3; }, "The auto resolution entry can refresh an unverified automatic capture");
                FakeCapture resized = WaitCapture(fixture, "ordinary"); Send(resized, transfer, payload); PumpFor(40);
                Check(fixture.PayloadDecodes == 0 && fixture.Publications == 0 && AutoOption(receiver).Checked,
                    "Changing automatic resolution preserves the active verification boundary and automatic setting");
                Send(resized, transfer, BridgeControl.Hello(transfer, Guid.Empty, Guid.Empty, 0));
                PumpUntil(delegate { return fixture.SelectionWrites == 1; }, "A mode-refreshed probe can still confirm through genuine own control");
                resized.Fail(); PumpUntil(delegate { return resized.Disposed; }, "The confirmed source can enter a capture retry gap");
                fixture.Now = 4000;
                receiver.Menu.Items["video.refresh"].PerformClick();
                PumpUntil(delegate { return fixture.OpenCount("ordinary") == 4; }, "A stale refresh menu can request a capture while automatic retry has no current source");
                Send(WaitCapture(fixture, "ordinary"), transfer, payload); PumpFor(40);
                Check(fixture.PayloadDecodes == 0 && fixture.Publications == 0 && fixture.SelectionWrites == 1,
                    "Refreshing during an empty automatic retry gap still requires new own-control proof");
            }
        }
    }

    private static void FailedOpenAndNoFramesAdvanceToTheNextCandidate()
    {
        foreach (bool failOpen in new[] { true, false })
        using (var fixture = new Fixture())
        {
            int ordinaryAttempts = 0;
            fixture.Services.OpenCapture = delegate(string id, Action<Bitmap> frame) {
                if (id == "ordinary") { Interlocked.Increment(ref ordinaryAttempts); if (failOpen) throw new IOException("Synthetic driver open failure"); }
                return fixture.Open(id, frame);
            };
            File.WriteAllText(Path.Combine(fixture.Root, "camera.id"), "obsolete-redirected-moniker");
            using (ReceiverContext receiver = fixture.Create())
            {
                PumpUntil(delegate { return Volatile.Read(ref ordinaryAttempts) == 1; }, "A stale saved ID does not prevent trying available exact cameras");
                if (!failOpen) { WaitCapture(fixture, "ordinary"); fixture.Now = 6000; receiver.PollCaptureRetry(); }
                FakeCapture ours = WaitCapture(fixture, "ours"); Guid session = Guid.NewGuid();
                Send(ours, session, BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0));
                PumpUntil(delegate { return receiver.SelectedCameraId == "ours"; }, "Failed opens and sources delivering no frames each advance to a working bridge camera");
                Check(fixture.SelectionWrites == 1 && fixture.PayloadDecodes == 0, "Only positively identified replacement monikers are saved after stale-selection recovery");
            }
        }
    }

    private static void ProbeFailureUnplugAndRetryDoNotFreezeDiscovery()
    {
        using (var fixture = new Fixture())
        {
            fixture.Services.OpenCaptureBackend = delegate(string id, Action<Bitmap> frame, CaptureBackend backend) { fixture.Backends.Add(backend); return fixture.Open(id, frame); };
            using (ReceiverContext receiver = fixture.Create())
            {
                FakeCapture first = WaitCapture(fixture, "ordinary"); first.Fail(); FakeCapture ours = WaitCapture(fixture, "ours");
                Guid session = Guid.NewGuid(); Send(ours, session, BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0));
                PumpUntil(delegate { return receiver.SelectedCameraId == "ours"; }, "A failed first camera does not prevent finding the bridge camera");
                fixture.Devices.RemoveAt(1); Refresh(receiver);
                PumpUntil(delegate { return ours.Disposed; }, "Unplugging the confirmed source releases its capture");
                fixture.Now = 60000; receiver.PollCaptureRetry();
                PumpUntil(delegate { return fixture.OpenCount("ordinary") >= 2; }, "Discovery eventually retries after the confirmed source disappears");
                FakeCapture retry = WaitCapture(fixture, "ordinary"); fixture.Devices.Add(new CameraDevice("Elgato Facecam MK.2", "replacement"));
                fixture.Now += 6000; receiver.PollCaptureRetry();
                FakeCapture replacement = WaitCapture(fixture, "replacement");
                Send(replacement, session, BridgeControl.CameraFormat(session, Guid.NewGuid(), 640, 480, 30, 2));
                PumpUntil(delegate { return receiver.SelectedCameraId == "replacement"; }, "Periodic enumeration discovers a recreated source with a new exact moniker");
                Check(fixture.Backends.Contains(CaptureBackend.MediaFoundation), "A retried failing exact candidate uses the existing compatibility backend policy");
                Check(first.Disposed && retry.Disposed, "Superseded probes leave no held capture");
            }
        }
    }

    private static void SettingsAndLanguageRoundTrip()
    {
        using (var fixture = new Fixture())
        {
            UiText.SetLanguage(UiLanguage.English);
            using (ReceiverContext receiver = fixture.Create())
            {
                WaitCapture(fixture, "ordinary"); Check(AutoOption(receiver).Text == "Automatically select server camera", "Automatic selection has an English Settings caption");
                AutoOption(receiver).PerformClick();
            }
            using (ReceiverContext disabled = fixture.Create())
            {
                Check(!AutoOption(disabled).Checked, "The saved OFF choice survives a new receiver instance");
                Refresh(disabled); Check(fixture.OpenCount("ordinary") == 1, "Restoring saved OFF never starts automatic discovery");
                AutoOption(disabled).PerformClick(); WaitCapture(fixture, "ordinary");
                disabled.SelectLanguage(UiLanguage.Russian);
                PumpUntil(delegate { return AutoOption(disabled).Text == "Автоматически выбирать камеру сервера"; }, "Existing Settings item follows saved Russian language switching");
                Check(AutoOption(disabled).Checked && File.ReadAllText(Path.Combine(fixture.Root, "camera.auto")) == "1", "The automatic setting survives language changes");
            }
            using (ReceiverContext enabled = fixture.Create()) Check(AutoOption(enabled).Checked, "The saved ON choice survives a new receiver instance");
        }
        UiText.SetLanguage(UiLanguage.English);
    }

    private static FakeCapture WaitCapture(Fixture fixture, string id)
    { PumpUntil(delegate { return fixture.Capture(id) != null && !fixture.Capture(id).Disposed; }, "Started capture for " + id); return fixture.Capture(id); }
    private static void Refresh(ReceiverContext receiver)
    { bool done = false; receiver.RefreshDevices(delegate { done = true; }); PumpUntil(delegate { return done; }, "Device enumeration completes"); }
    private static void Send(FakeCapture capture, Guid session, byte[] bytes)
    { using (Bitmap frame = VisualProtocol.EncodeFrame(session, 0, 1, bytes.Length, bytes)) capture.Frame(frame); }
    private static void PumpFor(int milliseconds)
    { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(1); } }

    private static void FreshClientStartsDiscovery()
    {
        using (var fixture = new Fixture())
        using (ReceiverContext receiver = fixture.Create())
        {
            ToolStripMenuItem option = AutoOption(receiver);
            Check(option != null && option.Checked, "A fresh client enables automatic camera discovery in Settings");
            PumpUntil(delegate { return fixture.Capture("ordinary") != null; }, "A fresh client probes without needing a previously selected camera");
            Check(receiver.SelectedCameraId == null && fixture.SelectionWrites == 0, "An unverified camera is not selected or saved");
            Check(!File.Exists(Path.Combine(fixture.Root, "camera.auto")), "The automatic default does not write a setting");
        }
    }

    private static ToolStripMenuItem AutoOption(ReceiverContext receiver)
    {
        var settings = receiver.Menu.Items["settings"] as ToolStripMenuItem;
        return settings == null ? null : settings.DropDownItems["settings.auto_camera"] as ToolStripMenuItem;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static void PumpUntil(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(1); }
        Check(condition(), message);
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "auto-camera-test-" + Guid.NewGuid().ToString("N"));
        internal readonly ReceiverServices Services;
        internal readonly List<CameraDevice> Devices = new List<CameraDevice> { new CameraDevice("Logitech C270 HD Webcam", "ordinary"), new CameraDevice("Razer Kiyo Pro", "ours") };
        private readonly object gate = new object();
        private readonly Dictionary<string, FakeCapture> captures = new Dictionary<string, FakeCapture>();
        private readonly Dictionary<string, int> opens = new Dictionary<string, int>();
        internal readonly List<CaptureBackend> Backends = new List<CaptureBackend>();
        internal Action BeforeOpen;
        internal int SelectionWrites, Publications, PayloadDecodes, OpenThread;
        internal long Now;
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            var paths = new ClientPaths(Root);
            Services = new ReceiverServices(paths) {
                ListDevices = delegate { lock (gate) return new List<CameraDevice>(Devices); },
                OpenCapture = Open,
                ReadSelection = delegate { return File.Exists(paths.CameraSelection) ? File.ReadAllText(paths.CameraSelection) : null; },
                WriteSelection = delegate(string id) { SelectionWrites++; File.WriteAllText(paths.CameraSelection, id); },
                Milliseconds = delegate { return Interlocked.Read(ref Now); },
                DecodeFramesPreferred = VisualProtocol.DecodePackets,
                DecodePayload = delegate(byte[] bytes) { PayloadDecodes++; return ClipboardPayload.Decode(bytes); },
                PreparePayload = delegate { }, PublishPayload = delegate { Publications++; }, LogFailure = delegate { },
                ShowTrayIcon = false, EnableDeferredPaste = false, EnableRetryTimer = false, EnableSessionEvents = false, RestoreSelection = true
            };
        }
        internal ReceiverContext Create() { return new ReceiverContext(Services); }
        internal IReceiverCapture Open(string id, Action<Bitmap> callback)
        {
            OpenThread = Thread.CurrentThread.ManagedThreadId;
            if (BeforeOpen != null) BeforeOpen();
            var capture = new FakeCapture(callback);
            lock (gate) { int count; opens.TryGetValue(id, out count); opens[id] = count + 1; captures[id] = capture; }
            return capture;
        }
        internal int OpenCount(string id) { lock (gate) { int count; return opens.TryGetValue(id, out count) ? count : 0; } }
        internal FakeCapture AnyCapture(string id) { lock (gate) { FakeCapture value; return captures.TryGetValue(id, out value) ? value : null; } }
        internal FakeCapture Capture(string id) { lock (gate) { FakeCapture value; return captures.TryGetValue(id, out value) && value.Started ? value : null; } }
        public void Dispose()
        {
            string full = Path.GetFullPath(Root), parent = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (String.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("auto-camera-test-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    private sealed class FakeCapture : IReceiverCapture
    {
        internal readonly Action<Bitmap> Frame;
        internal volatile bool Started, Disposed;
        internal FakeCapture(Action<Bitmap> frame) { Frame = frame; }
        public event Action<Exception> OnError;
        public void Start() { Started = true; }
        public void Dispose() { Disposed = true; }
        internal void Fail() { Action<Exception> handler = OnError; if (handler != null) handler(new IOException("Synthetic unplug")); }
    }
}
