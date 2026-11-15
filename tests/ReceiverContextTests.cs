// Compile with src/*.cs and /main:ReceiverContextTests. The injected services never
// open a camera, touch the clipboard, install a keyboard hook, or add a tray icon.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

internal static class ReceiverContextTests
{
    private static int assertions;

    [STAThread]
    private static int Main()
    {
        UiText.SetLanguage(UiLanguage.Russian); // Menu assertions below intentionally use Russian captions.
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AudioModemMenuSelection();
            OutputChoiceAutomaticallyProbesTheExactLatestEndpoint();
            ClosingInvalidatesPendingOutputProbe();
            AsyncModemSelectionCommitsOnlyTheLatestPreparedChoice();
            AsyncModemSelectionIsInvalidatedByCaptureChangeAndClose();
            FreshCameraMenuAndNoAutomaticChoice();
            RefreshPreservesOpenSubmenu();
            ExactChoiceStartsAndRestores();
            SwitchingDiscardsDelayedOpen();
            DisposalDiscardsDelayedOpen();
            SelectedDeviceReconnectsWithoutFallback();
            StreamingFailuresKeepSelectedCapture();
            WrappedOpenFailureKeepsSelectedCapture();
            SuccessfulFramesKeepSelectedCapture();
            CaptureBackendsRetryAfterDelayAndKeepWorkingMode();
            RealModeSelectionAndExactCameraRefresh();
            ManualHighResolutionKeepsTheWorkingDirectShowBackend();
            SourceFormatChangesRefreshCapabilitiesWithoutRestart();
            SourceRefreshIsBoundedAndHonorsManualResolution();
            AutoSourcePreferenceKeepsTheWorkingBackendWhenBothModesAreOffered();
            SourceRetriesWaitForReservedBenchmarkWithoutBlockingFirstOrManualRefresh();
            FirstFrameTimeoutReopensOnlyAfterCooldown();
            StalledWorkingStreamReconnects();
            PayloadReachesInjectedClipboardOnSta();
            CompletedOldTransferCannotDisplacePendingPublication();
            TiledFramePublishesAllPacketsOnce();
            PreferredDecoderLearnsPerCapture();
            FailuresStaySilentAndRecover();
            FailedPublicationNeverConfirmsDelivery();
            FilePreparationPrecedesReceipt();
            ExitDisposesCapture();
            Check(Application.OpenForms.Count == 0, "Receiver tests never created a form");
            Console.WriteLine("Tray receiver tests passed ({0} assertions; no forms, cameras, clipboard access, hooks, or tray icons).", assertions);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void AudioModemMenuSelection()
    {
        AudioModemKind applied = AudioModemKind.Quiet;
        int calls = 0;
        bool accept = true;
        using (ToolStripMenuItem menu = AudioModemSelection.BuildMenu(applied, delegate(AudioModemKind kind)
        {
            calls++;
            if (!accept) return false;
            applied = kind;
            return true;
        }))
        {
            Check(menu.Text == "\u0410\u0443\u0434\u0438\u043e\u043c\u043e\u0434\u0435\u043c" && menu.DropDownItems.Count == 3,
                "Tray offers all three audio modems in one submenu");
            Check(calls == 0, "Building the modem menu performs no codec or audio initialization");
            AudioModemKind[] kinds = { AudioModemKind.Fsk, AudioModemKind.Quiet, AudioModemKind.Aicodix };
            for (int i = 0; i < kinds.Length; i++)
            {
                ToolStripMenuItem item = (ToolStripMenuItem)menu.DropDownItems[i];
                Check(item.Text == AudioModemCatalog.GetCaption(kinds[i]) && (AudioModemKind)item.Tag == kinds[i],
                    "Modem captions retain stable identities");
                Check(item.Checked == (kinds[i] == AudioModemKind.Quiet), "Saved modem alone is checked initially");
            }
            for (int i = 0; i < kinds.Length; i++)
            {
                menu.DropDownItems[i].PerformClick();
                Check(applied == kinds[i] && calls == i + 1, "Clicking a modem applies exactly that modem once");
                for (int j = 0; j < kinds.Length; j++)
                    Check(((ToolStripMenuItem)menu.DropDownItems[j]).Checked == (j == i), "Successful switches retain exactly one check");
            }
            accept = false;
            menu.DropDownItems[0].PerformClick();
            Check(applied == AudioModemKind.Aicodix && !((ToolStripMenuItem)menu.DropDownItems[0]).Checked &&
                ((ToolStripMenuItem)menu.DropDownItems[2]).Checked, "A rejected modem switch preserves the active check");
        }
        using (ToolStripMenuItem rebuilt = AudioModemSelection.BuildMenu(applied, delegate { return true; }))
            Check(((ToolStripMenuItem)rebuilt.DropDownItems[2]).Checked, "Reopening the tray preserves the selected modem");
    }

    private static void AsyncModemSelectionCommitsOnlyTheLatestPreparedChoice()
    {
        using (Fixture fixture = new Fixture())
        using (var started = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var finished = new ManualResetEvent(false))
        {
            var applied = new List<AudioModemKind>(); var saved = new List<AudioModemKind>();
            int uiThread = Thread.CurrentThread.ManagedThreadId, prepareThread = uiThread, errors = 0;
            bool fail = false;
            fixture.Services.PrepareAudioModem = delegate(AudioModemKind kind)
            {
                prepareThread = Thread.CurrentThread.ManagedThreadId;
                if (fail) throw new InvalidOperationException("Synthetic codec unavailable");
                if (kind == AudioModemKind.Quiet)
                {
                    started.Set();
                    try { if (!release.WaitOne(5000)) throw new TimeoutException("Codec preparation was not released."); }
                    finally { finished.Set(); }
                }
            };
            fixture.Services.ApplyAudioModem = delegate(AudioModemKind kind)
            { Check(Thread.CurrentThread.ManagedThreadId == uiThread, "Prepared codecs commit on the receiver UI thread"); applied.Add(kind); };
            fixture.Services.WriteAudioModem = delegate(AudioModemKind kind)
            { Check(Thread.CurrentThread.ManagedThreadId == uiThread, "Only the winning UI request persists its selection"); saved.Add(kind); };
            fixture.Services.LogFailure = delegate { errors++; };
            using (ReceiverContext receiver = fixture.Create())
            {
                ToolStripMenuItem modes = AudioModemSelection.BuildMenu(AudioModemKind.Fsk, receiver.SelectAudioModem);
                modes.Name = "audio.modem"; receiver.Menu.Items.Insert(0, modes);
                try
                {
                    modes.DropDownItems[1].PerformClick();
                    PumpUntil(delegate { return started.WaitOne(0); }, "The selected slow codec starts preparing on a worker");
                    Check(prepareThread != uiThread && !finished.WaitOne(0) && applied.Count == 0 && saved.Count == 0,
                        "The UI remains responsive while native codec initialization is blocked and the working codec remains active");
                    Check(((ToolStripMenuItem)modes.DropDownItems[0]).Checked && !((ToolStripMenuItem)modes.DropDownItems[1]).Checked,
                        "Pending codec preparation does not change the active check mark");
                    modes.DropDownItems[2].PerformClick();
                    PumpUntil(delegate { return applied.Count == 1 && saved.Count == 1; }, "A later fast preparation commits without waiting for the older load");
                    Check(applied[0] == AudioModemKind.Aicodix && saved[0] == AudioModemKind.Aicodix && ((ToolStripMenuItem)modes.DropDownItems[2]).Checked,
                        "Only the latest successful request becomes active and checked");
                    release.Set(); PumpUntil(delegate { return finished.WaitOne(0); }, "The obsolete native preparation can finish safely"); PumpFor(40);
                    Check(applied.Count == 1 && saved.Count == 1 && ((ToolStripMenuItem)modes.DropDownItems[2]).Checked,
                        "An older completion cannot overwrite a newer active codec or its setting");
                    fail = true; modes.DropDownItems[1].PerformClick();
                    PumpUntil(delegate { return errors == 1; }, "A failed codec load is reported after asynchronous preparation");
                    Check(applied.Count == 1 && saved.Count == 1 && ((ToolStripMenuItem)modes.DropDownItems[2]).Checked,
                        "Failed preparation preserves the working codec and saved selection");
                }
                finally { release.Set(); finished.WaitOne(5000); }
            }
        }
    }

    private static void AsyncModemSelectionIsInvalidatedByCaptureChangeAndClose()
    {
        using (Fixture fixture = new Fixture())
        using (var started = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var finished = new ManualResetEvent(false))
        {
            int applied = 0, saved = 0;
            fixture.Services.PrepareAudioModem = delegate
            {
                started.Set();
                try { if (!release.WaitOne(5000)) throw new TimeoutException(); }
                finally { finished.Set(); }
            };
            fixture.Services.ApplyAudioModem = delegate { applied++; };
            fixture.Services.WriteAudioModem = delegate { saved++; };
            fixture.Devices.Add(Device("Camera", "modem:camera"));
            using (ReceiverContext receiver = fixture.Create())
            {
                try
                {
                    receiver.SelectAudioModem(AudioModemKind.Quiet);
                    PumpUntil(delegate { return started.WaitOne(0); }, "Capture-change codec load is pending");
                    Refresh(receiver); receiver.SelectDevice(fixture.Devices[0]);
                    PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "A new exact capture session starts while the codec is preparing");
                    release.Set(); PumpUntil(delegate { return finished.WaitOne(0); }, "Old capture-session preparation finishes"); PumpFor(40);
                    Check(applied == 0 && saved == 0, "A codec prepared for an obsolete capture session cannot apply or persist");
                    started.Reset(); release.Reset(); finished.Reset();
                    receiver.SelectAudioModem(AudioModemKind.Quiet);
                    PumpUntil(delegate { return started.WaitOne(0); }, "Second codec load is pending before choosing the current modem");
                    Check(receiver.SelectAudioModem(AudioModemKind.Fsk), "Choosing the already active codec cancels an older pending request immediately");
                    release.Set(); PumpUntil(delegate { return finished.WaitOne(0); }, "Cancelled preparation finishes"); PumpFor(40);
                    Check(applied == 0 && saved == 1, "Cancelled pending selection stays inactive and the explicit working choice is persisted");
                    started.Reset(); release.Reset(); finished.Reset();
                    receiver.SelectAudioModem(AudioModemKind.Aicodix);
                    PumpUntil(delegate { return started.WaitOne(0); }, "Close-during-load codec preparation starts");
                    receiver.Dispose(); release.Set();
                    PumpUntil(delegate { return finished.WaitOne(0); }, "Preparation exits after receiver disposal"); PumpFor(40);
                    Check(applied == 0 && saved == 1, "Closing during native initialization cannot apply a codec or write settings afterwards");
                }
                finally { release.Set(); finished.WaitOne(5000); }
            }
        }
    }

    private static void FreshCameraMenuAndNoAutomaticChoice()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Devices.Add(Device("Built-in camera", "physical"));
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver);
                CheckMenu(receiver, "Built-in camera");
                Check(fixture.OpenCount == 0 && !receiver.IsReceiving,
                    "A physical camera is never chosen automatically on a fresh installation");
                Check(receiver.MainForm == null, "Tray receiver has no main form");
                Check(receiver.TrayIcon.Text == String.Empty && receiver.TrayIcon.BalloonTipText == String.Empty &&
                    receiver.TrayIcon.BalloonTipTitle == String.Empty, "Tray icon has no tooltip or notification text");
                fixture.Devices.Add(Device("Redirected camera", "redirected"));
                Refresh(receiver);
                CheckMenu(receiver, "Built-in camera", "Redirected camera");
                fixture.Devices.RemoveAt(0);
                Refresh(receiver);
                CheckMenu(receiver, "Redirected camera");
                fixture.Devices.Clear();
                Refresh(receiver);
                CheckMenu(receiver);
                Check(fixture.OpenCount == 0, "Refreshing cameras does not select a default device");
            }
        }
    }

    private static void ExactChoiceStartsAndRestores()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Devices.Add(Device("Same camera name", "moniker:first"));
            fixture.Devices.Add(Device("Same camera name", "moniker:second"));
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver);
                receiver.Menu.Items[1].PerformClick();
                PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "Clicking a camera starts reception");
                Check(receiver.SelectedCameraId == "moniker:second", "Duplicate names retain exact moniker identity");
                Check(fixture.StoredSelection == "moniker:second" && fixture.LastOpened == "moniker:second",
                    "The selected moniker is persisted and opened exactly");
                Check(!((ToolStripMenuItem)receiver.Menu.Items[0]).Checked &&
                    ((ToolStripMenuItem)receiver.Menu.Items[1]).Checked, "Only the chosen camera is checked");
            }
            PumpUntil(delegate { return fixture.LastCapture.Disposed; }, "Disposing receiver closes its active capture");
            fixture.Services.RestoreSelection = true;
            using (ReceiverContext restored = fixture.Create())
            {
                PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "Saved exact camera is restored automatically");
                Check(restored.SelectedCameraId == "moniker:second" && fixture.LastOpened == "moniker:second",
                    "Restoration matches ID rather than friendly name or enumeration order");
            }
            fixture.StoredSelection = "moniker:missing";
            int count = fixture.OpenCount;
            using (ReceiverContext missing = fixture.Create())
            {
                Refresh(missing);
                Check(!missing.IsReceiving && fixture.OpenCount == count,
                    "An unavailable saved camera never falls back to another camera");
            }
        }
    }

    private static void RefreshPreservesOpenSubmenu()
    {
        using (Fixture fixture = new Fixture())
        {
            fixture.Devices.Add(Device("Original camera", "original"));
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver);
                ToolStripItem originalCamera = receiver.Menu.Items[0];
                AudioModemKind selected = AudioModemKind.Fsk;
                ToolStripMenuItem modems = AudioModemSelection.BuildMenu(selected, delegate(AudioModemKind kind) { selected = kind; return true; });
                receiver.Menu.Items.Insert(1, modems);
                receiver.Menu.AutoClose = false;
                modems.DropDown.AutoClose = false;
                receiver.Menu.Show(new Point(0, 0)); modems.ShowDropDown();
                try
                {
                    Check(receiver.Menu.Visible && modems.DropDown.Visible, "Regression fixture opens a real submenu without a form or audio I/O");
                    fixture.Devices.Clear(); fixture.Devices.Add(Device("Intermediate camera", "intermediate")); Refresh(receiver);
                    fixture.Devices.Clear(); fixture.Devices.Add(Device("Latest camera", "latest")); Refresh(receiver);
                    Check(receiver.Menu.Visible && modems.DropDown.Visible && !modems.IsDisposed && !originalCamera.IsDisposed,
                        "Background device refresh leaves the active menu and submenu intact");
                    Check(Object.ReferenceEquals(receiver.Menu.Items[0], originalCamera), "An open menu retains its existing item identities");
                    modems.DropDownItems[2].PerformClick();
                    Check(selected == AudioModemKind.Aicodix && ((ToolStripMenuItem)modems.DropDownItems[2]).Checked,
                        "The user can still choose an audio modem while a device refresh is pending");
                }
                finally { modems.HideDropDown(); receiver.Menu.Close(ToolStripDropDownCloseReason.CloseCalled); }
                PumpUntil(delegate { return receiver.Menu.Items.Count != 0 && !Object.ReferenceEquals(receiver.Menu.Items[0], originalCamera); }, "Closing the menu applies the pending device snapshot");
                CheckMenu(receiver, "Latest camera");
                Check(!receiver.Menu.Visible && fixture.OpenCount == 0, "Only the latest snapshot is applied without reopening the menu or opening a camera");
            }
        }
    }

    private static void SwitchingDiscardsDelayedOpen()
    {
        using (Fixture fixture = new Fixture())
        using (ManualResetEvent entered = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            CameraDevice first = Device("First", "first"), second = Device("Second", "second");
            fixture.Devices.Add(first); fixture.Devices.Add(second);
            fixture.BeforeOpen = delegate(string id)
            {
                if (id == "first") { entered.Set(); if (!release.WaitOne(5000)) throw new TimeoutException("Test opener was not released"); }
            };
            using (ReceiverContext receiver = fixture.Create())
            {
                try
                {
                    Refresh(receiver);
                    receiver.SelectDevice(first);
                    PumpUntil(delegate { return entered.WaitOne(0); }, "First opening started");
                    receiver.SelectDevice(second);
                    release.Set();
                    PumpUntil(delegate { FakeCapture old = fixture.Capture("first"); return old != null && old.Disposed; }, "Stale opened capture is disposed");
                    PumpUntil(delegate { return IsActive(fixture.Capture("second")); }, "Second camera starts after the stale opening is retired");
                    Check(receiver.SelectedCameraId == "second" && !fixture.Capture("second").Disposed,
                        "Late old opener cannot replace the new camera");
                    Check(!fixture.Capture("first").Started, "Stale camera is not started");
                }
                finally { release.Set(); }
            }
        }
    }

    private static void DisposalDiscardsDelayedOpen()
    {
        using (Fixture fixture = new Fixture())
        using (ManualResetEvent entered = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            CameraDevice camera = Device("Camera", "late"); fixture.Devices.Add(camera);
            fixture.BeforeOpen = delegate { entered.Set(); if (!release.WaitOne(5000)) throw new TimeoutException("Test opener was not released"); };
            ReceiverContext receiver = fixture.Create();
            try
            {
                Refresh(receiver);
                receiver.SelectDevice(camera);
                PumpUntil(delegate { return entered.WaitOne(0); }, "Delayed opening started before disposal");
                receiver.Dispose();
                release.Set();
                PumpUntil(delegate { FakeCapture capture = fixture.Capture("late"); return capture != null && capture.Disposed; }, "Capture opened after disposal is immediately disposed");
                Check(!fixture.Capture("late").Started, "Disposal invalidates a delayed camera start");
            }
            finally { release.Set(); receiver.Dispose(); }
        }
    }

    private static void SelectedDeviceReconnectsWithoutFallback()
    {
        using (Fixture fixture = new Fixture())
        {
            CameraDevice selected = Device("Selected", "selected"), other = Device("Physical", "physical");
            fixture.Devices.Add(selected); fixture.Devices.Add(other);
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(selected);
                PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "Selected camera starts");
                FakeCapture original = fixture.LastCapture;
                fixture.Devices.RemoveAt(0);
                Refresh(receiver);
                PumpUntil(delegate { return original.Disposed && !receiver.IsReceiving; }, "Removed camera is closed");
                CheckMenu(receiver, "Physical");
                Check(receiver.SelectedCameraId == "selected" && fixture.OpenCount == 1,
                    "Disconnected selection is remembered without opening a physical fallback");
                fixture.Devices.Add(selected);
                Refresh(receiver);
                PumpUntil(delegate { return IsActive(fixture.LastCapture) && fixture.OpenCount == 2; }, "The exact selected camera reconnects when it returns");
                Check(fixture.LastOpened == "selected", "Reconnect uses the saved moniker");
                fixture.LastCapture.Fail(new InvalidOperationException("Disconnected"));
                FakeCapture failed = fixture.LastCapture;
                PumpUntil(delegate { return failed.Disposed; }, "Capture errors close the failed capture silently");
                Refresh(receiver);
                PumpUntil(delegate { return IsActive(fixture.LastCapture) && fixture.OpenCount == 3; }, "Capture can restart after an error");
            }
        }
    }

    private static void PayloadReachesInjectedClipboardOnSta()
    {
        using (Fixture fixture = new Fixture())
        {
            CameraDevice camera = Device("Source", "source"); fixture.Devices.Add(camera);
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "Payload test camera starts");
                Guid id = Guid.NewGuid();
                int encodedBytes = EmitPayload(fixture.LastCapture, id, "Tray receiver text");
                PumpUntil(delegate { return fixture.PublishCount == 1; }, "Video payload is published without a form");
                Check(fixture.PublishedId == id && Encoding.UTF8.GetString(fixture.Published.Formats[DataFormats.UnicodeText]) == "Tray receiver text",
                    "Real frame decoding and payload assembly preserve the clipboard data");
                Check(fixture.PublishApartment == ApartmentState.STA && fixture.PublishThread == Thread.CurrentThread.ManagedThreadId,
                    "Clipboard publication is marshalled to the interactive STA");
                Check(fixture.ReceiptCount == 1 && fixture.ReceiptId == id && fixture.ReceiptBytes == encodedBytes && fixture.ReceiptAfterPublication,
                    "Only successful publication confirms the exact transfer and encoded byte count");
                Check(receiver.MainForm == null && Application.OpenForms.Count == 0, "Reception never needs a hidden or visible form");
            }
        }
    }

    private static void CompletedOldTransferCannotDisplacePendingPublication()
    {
        using (Fixture fixture = new Fixture())
        {
            CameraDevice camera = Device("Source", "completed-id-race"); fixture.Devices.Add(camera);
            var packets = new Queue<Packet[]>();
            fixture.Services.DecodeFrames = delegate { return packets.Dequeue(); };
            using (ReceiverContext receiver = fixture.Create())
            using (Bitmap frame = new Bitmap(2, 2))
            {
                receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "Completed-ID test capture starts");
                Guid a = Guid.NewGuid(), b = Guid.NewGuid();
                Packet first = TextPacket(a, "completed A"), second = TextPacket(b, "new B");
                packets.Enqueue(new[] { first }); fixture.LastCapture.Emit(frame);
                PumpUntil(delegate { return fixture.ReceiptCount == 1; }, "A is really published before the race");
                packets.Enqueue(new[] { second, first }); fixture.LastCapture.Emit(frame);
                Check(fixture.PublishCount == 1 && fixture.ReceiptCount == 1 && receiver.IsReceiving,
                    "A fresh complete B remains receiving until its real STA publication.");
                PumpFor(40);
                Check(fixture.PublishCount == 2 && fixture.PublishedId == b && fixture.ReceiptCount == 2 && fixture.ReceiptId == b && !receiver.IsReceiving,
                    "A remembered completed A cannot displace complete B before B's STA callback publishes exactly once and becomes ready.");
                packets.Enqueue(new[] { first, second, first, second }); fixture.LastCapture.Emit(frame); PumpFor(40);
                Check(fixture.PublishCount == 2 && fixture.ReceiptCount == 2 && fixture.PublishedId == b && !receiver.IsReceiving,
                    "Further A/B repeats cannot replace B, republish, send another receipt, or restart receiving.");
                FakeCapture previous = fixture.LastCapture;
                receiver.SelectDevice(null); receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture) && fixture.LastCapture != previous; }, "A replacement receiver session starts");
                packets.Enqueue(new[] { first }); fixture.LastCapture.Emit(frame);
                PumpUntil(delegate { return fixture.ReceiptCount == 3; }, "Session replacement clears completed identity history");
                Check(fixture.PublishCount == 3 && fixture.PublishedId == a && !receiver.IsReceiving,
                    "A completed identity from the retired session remains valid in the fresh session.");
            }
        }
    }

    private static Packet TextPacket(Guid id, string text)
    {
        var snapshot = new ClipboardSnapshot(); snapshot.Formats.Add(DataFormats.UnicodeText, Encoding.UTF8.GetBytes(text));
        string description; byte[] bytes = ClipboardPayload.Encode(snapshot, out description);
        return new Packet { TransferId = id, Index = 0, Count = 1, TotalLength = bytes.Length, ChunkSize = VisualProtocol.ChunkSize,
            Profile = VideoProfile.Compatible, Data = bytes };
    }

    private static Exception StreamAccessDenied()
    { return new CameraStreamException(3, unchecked((int)0x80070005)); }

    private static void StreamingFailuresKeepSelectedCapture()
    {
        Exception[] errors = {
            StreamAccessDenied(),
            new InvalidOperationException("Wrapped streaming failure", StreamAccessDenied()),
            new COMException("Access denied", unchecked((int)0x80070005)),
            new CameraStreamException(3, unchecked((int)0x80004005))
        };
        foreach (Exception error in errors)
        {
            using (Fixture fixture = new Fixture())
            {
                CameraDevice camera = Device("Redirected", "redirected"), other = Device("Physical", "physical");
                fixture.Devices.Add(camera); fixture.Devices.Add(other);
                using (ReceiverContext receiver = fixture.Create())
                {
                    Refresh(receiver); receiver.SelectDevice(camera);
                    PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "Configured capture starts on the selected camera");
                    FakeCapture original = fixture.LastCapture;
                    original.Fail(error);
                    PumpUntil(delegate { return original.Disposed; }, "A streaming failure retires the original graph");
                    PumpFor(80);
                    Check(fixture.OpenCount == 1 && receiver.SelectedCameraId == camera.Id,
                        "A streaming error does not immediately open a driver-default graph or select another camera");
                    Refresh(receiver);
                    PumpUntil(delegate { return fixture.OpenCount == 2 && IsActive(fixture.LastCapture); }, "A normal reconnect uses the configured capture service");
                    Check(fixture.LastOpened == camera.Id && receiver.SelectedCameraId == camera.Id,
                        "Reconnect retains the exact selected camera after a streaming error");
                    EmitPayload(fixture.LastCapture, Guid.NewGuid(), "Recovered configured capture");
                    PumpUntil(delegate { return fixture.PublishCount == 1; }, "The reconnected capture still receives clipboard data");
                    receiver.SelectDevice(other);
                    PumpUntil(delegate { return IsActive(fixture.LastCapture) && fixture.LastOpened == other.Id; }, "An explicit choice can still select another camera");
                }
            }
        }
    }

    private static void WrappedOpenFailureKeepsSelectedCapture()
    {
        using (Fixture fixture = new Fixture())
        {
            CameraDevice camera = Device("Redirected", "open-stream-error"); fixture.Devices.Add(camera);
            fixture.NextOpenError = new InvalidOperationException("Open wrapped stream failure", StreamAccessDenied());
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                PumpUntil(delegate { return fixture.OpenCount == 1; }, "The selected capture open is attempted");
                PumpFor(80);
                Check(fixture.OpenCount == 1 && fixture.LastCapture == null && receiver.SelectedCameraId == camera.Id,
                    "A wrapped stream-open error does not immediately switch to a driver-default graph");
                Refresh(receiver);
                PumpUntil(delegate { return fixture.OpenCount == 2 && IsActive(fixture.LastCapture); }, "A later reconnect recovers from a wrapped open error");
                Check(fixture.LastOpened == camera.Id, "The configured capture service reopens the exact selected camera");
            }
        }
    }

    private static void SuccessfulFramesKeepSelectedCapture()
    {
        using (Fixture fixture = new Fixture())
        {
            CameraDevice camera = Device("Redirected", "already-streaming"); fixture.Devices.Add(camera);
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "Successful-frame capture starts");
                FakeCapture original = fixture.LastCapture;
                using (Bitmap frame = new Bitmap(8, 8)) original.Emit(frame);
                original.Fail(StreamAccessDenied());
                PumpUntil(delegate { return original.Disposed; }, "An error after frames retires the graph normally");
                Refresh(receiver);
                PumpUntil(delegate { return fixture.OpenCount == 2 && IsActive(fixture.LastCapture); }, "Normal reconnect follows an error after successful frames");
                Check(fixture.LastOpened == camera.Id && receiver.SelectedCameraId == camera.Id, "Already working video retains its selected capture path after a later AccessDenied");
            }
        }
    }

    private static void CaptureBackendsRetryAfterDelayAndKeepWorkingMode()
    {
        using (Fixture fixture = new Fixture())
        {
            long now = 0;
            fixture.Services.Milliseconds = delegate { return now; };
            var modes = new List<string>();
            fixture.Services.OpenCaptureBackend = delegate(string id, Action<Bitmap> callback, CaptureBackend backend) {
                lock (modes) modes.Add(backend.ToString());
                return fixture.Open(id, callback);
            };
            CameraDevice camera = Device("Redirected", "format-retry"); fixture.Devices.Add(camera);
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "Highest offered native graph opens");
                Check(modes[0] == "DirectShowHighest", "The first graph requests the highest actually offered native size up to1080p");
                FakeCapture first = fixture.LastCapture;
                first.Fail(new InvalidOperationException("Wrapped format failure", new CameraFormatException("set_format", 1280, 720, unchecked((int)0x8004022A))));
                PumpUntil(delegate { return first.Disposed; }, "Rejected format graph is fully released");
                Refresh(receiver);
                Check(fixture.OpenCount == 1, "Enumeration cannot bypass the driver release cooldown");
                now = 2999; Refresh(receiver);
                Check(fixture.OpenCount == 1, "The cooldown is enforced up to its boundary");
                now = 3000; Refresh(receiver);
                PumpUntil(delegate { return fixture.OpenCount == 2 && IsActive(fixture.LastCapture); }, "Independent capture API opens after cooldown");
                Check(modes[1] == "MediaFoundation" && receiver.SelectedCameraId == camera.Id, "Capture backend retry retains the exact selected camera");
                FakeCapture second = fixture.LastCapture;
                using (var frame = new Bitmap(8, 8)) second.Emit(frame);
                PumpFor(30);
                second.Fail(StreamAccessDenied());
                PumpUntil(delegate { return second.Disposed; }, "Working format is released after a later stream error");
                now = 6000; Refresh(receiver);
                PumpUntil(delegate { return fixture.OpenCount == 3 && IsActive(fixture.LastCapture); }, "Working format reconnects after cooldown");
                Check(modes[2] == "MediaFoundation", "A backend which delivered frames stays selected after a midstream failure");
            }
        }
    }

    private static void FirstFrameTimeoutReopensOnlyAfterCooldown()
    {
        using (Fixture fixture = new Fixture())
        {
            long now = 0;
            fixture.Services.Milliseconds = delegate { return now; };
            CameraDevice camera = Device("Redirected", "no-first-frame"); fixture.Devices.Add(camera);
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "No-frame graph starts");
                PumpFor(30);
                FakeCapture first = fixture.LastCapture;
                now = 19999; receiver.PollCaptureRetry(); PumpFor(20);
                Check(!first.Disposed, "A cold camera receives the full first-frame timeout");
                now = 20000; receiver.PollCaptureRetry();
                PumpUntil(delegate { return first.Disposed; }, "An empty running graph is retired after its first-frame deadline");
                Check(fixture.OpenCount == 1 && receiver.SelectedCameraId == camera.Id, "Timeout cannot select another device or reopen immediately");
                now = 23000; receiver.PollCaptureRetry();
                PumpUntil(delegate { return fixture.OpenCount == 2 && IsActive(fixture.LastCapture); }, "Timed-out camera retries after release delay");
            }
        }
    }

    private static void StalledWorkingStreamReconnects()
    {
        using (Fixture fixture = new Fixture())
        {
            long now = 0;
            fixture.Services.Milliseconds = delegate { return now; };
            CameraDevice camera = Device("Redirected", "stalled-stream"); fixture.Devices.Add(camera);
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "Stall test graph starts");
                FakeCapture first = fixture.LastCapture;
                using (var frame = new Bitmap(8, 8)) first.Emit(frame);
                PumpFor(30);
                now = 14999; receiver.PollCaptureRetry(); PumpFor(20);
                Check(!first.Disposed, "A working stream is retained before its stall deadline");
                now = 15000; receiver.PollCaptureRetry();
                PumpUntil(delegate { return first.Disposed; }, "A silent stalled stream is released");
                now = 18000; receiver.PollCaptureRetry();
                PumpUntil(delegate { return fixture.OpenCount == 2 && IsActive(fixture.LastCapture); }, "A stalled camera reconnects after cooldown");
                Check(receiver.SelectedCameraId == camera.Id, "Stall recovery retains the selected camera");
            }
        }
    }

    private static void FailuresStaySilentAndRecover()
    {
        using (Fixture fixture = new Fixture())
        {
            CameraDevice camera = Device("Source", "source"); fixture.Devices.Add(camera);
            fixture.EnumerationFailure = true;
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); CheckMenu(receiver);
                fixture.EnumerationFailure = false;
                Refresh(receiver); CheckMenu(receiver, "Source");
                fixture.OpenFailure = true;
                receiver.SelectDevice(camera);
                PumpUntil(delegate { return fixture.OpenCount == 1; }, "Failed opening was attempted");
                PumpFor(80);
                Check(!receiver.IsReceiving, "Open failures leave reception stopped");
                fixture.OpenFailure = false;
                receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "Reselecting after an open failure retries silently");
                fixture.PublishFailuresRemaining = 1;
                EmitPayload(fixture.LastCapture, Guid.NewGuid(), "Clipboard retries");
                PumpUntil(delegate { return fixture.PublishCount >= 2; }, "A busy clipboard retries without a popup");
                Check(fixture.Published != null && IsActive(fixture.LastCapture), "Receipt continues after clipboard retry");
                Check(fixture.ReceiptCount == 1 && fixture.PublishCount == 2 && fixture.ReceiptAfterPublication, "Clipboard retries send exactly one receipt after success");
                Check(receiver.TrayIcon.Text == String.Empty && receiver.TrayIcon.BalloonTipText == String.Empty && Application.OpenForms.Count == 0,
                    "Enumeration, open, and clipboard failures do not add text or forms");
            }
        }
    }

    private static void FailedPublicationNeverConfirmsDelivery()
    {
        using (Fixture fixture = new Fixture())
        {
            CameraDevice camera = Device("Source", "failed-publication"); fixture.Devices.Add(camera);
            int failures = 0; fixture.Services.LogFailure = delegate { Interlocked.Increment(ref failures); };
            fixture.Services.PublishPayload = delegate { throw new InvalidOperationException("Publication failed"); };
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "Failed-publication capture starts");
                EmitPayload(fixture.LastCapture, Guid.NewGuid(), "Never published");
                PumpUntil(delegate { return Volatile.Read(ref failures) > 0 && !receiver.IsReceiving; }, "Failed publication is retired");
                Check(fixture.ReceiptCount == 0 && !receiver.IsReceiving, "Failed clipboard publication never confirms delivery");
            }
        }
        using (Fixture fixture = new Fixture())
        {
            CameraDevice camera = Device("Source", "failed-preparation"); fixture.Devices.Add(camera);
            int failures = 0; fixture.Services.LogFailure = delegate { Interlocked.Increment(ref failures); };
            fixture.Services.DecodePayload = delegate { return new DecodedPayload { IsFileDrop = true }; };
            fixture.Services.PreparePayload = delegate { throw new InvalidOperationException("File preparation failed"); };
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "Failed-preparation capture starts");
                EmitPayload(fixture.LastCapture, Guid.NewGuid(), "Never prepared");
                PumpUntil(delegate { return Volatile.Read(ref failures) > 0 && !receiver.IsReceiving; }, "Failed preparation is retired");
                Check(fixture.ReceiptCount == 0 && fixture.PublishCount == 0, "Failed file preparation cannot publish or confirm delivery");
            }
        }
    }

    private static void ExitDisposesCapture()
    {
        using (Fixture fixture = new Fixture())
        {
            CameraDevice camera = Device("Camera", "exit"); fixture.Devices.Add(camera);
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "Exit test camera starts");
                FakeCapture capture = fixture.LastCapture;
                receiver.Menu.Items[receiver.Menu.Items.Count - 1].PerformClick();
                PumpUntil(delegate { return capture.Disposed; }, "Exit closes capture");
                Check(!receiver.TrayIcon.Visible, "Exit removes the tray icon");
            }
        }
    }

    private static void FilePreparationPrecedesReceipt()
    {
        using (Fixture fixture = new Fixture())
        {
            CameraDevice camera = Device("Source", "file-receipt"); fixture.Devices.Add(camera);
            bool prepared = false;
            fixture.Services.DecodePayload = delegate { return new DecodedPayload { IsFileDrop = true }; };
            fixture.Services.PreparePayload = delegate { prepared = true; };
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "File receipt capture starts");
                Guid transfer = Guid.NewGuid(); int encodedBytes = EmitPayload(fixture.LastCapture, transfer, "File fixture");
                PumpUntil(delegate { return fixture.ReceiptCount > 0; }, "Prepared files are acknowledged after publication");
                Check(prepared && fixture.Published.IsFileDrop && fixture.ReceiptAfterPublication && fixture.ReceiptId == transfer && fixture.ReceiptBytes == encodedBytes,
                    "File receipt follows preparation and clipboard publication with exact encoded metadata");
            }
        }
    }

    private static int EmitPayload(FakeCapture capture, Guid id, string text)
    {
        ClipboardSnapshot snapshot = new ClipboardSnapshot();
        snapshot.Formats.Add(DataFormats.UnicodeText, Encoding.UTF8.GetBytes(text));
        string description;
        byte[] bytes = ClipboardPayload.Encode(snapshot, out description);
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                int count = (bytes.Length + VisualProtocol.ChunkSize - 1) / VisualProtocol.ChunkSize;
                for (int i = 0; i < count; i++)
                {
                    byte[] chunk = new byte[Math.Min(VisualProtocol.ChunkSize, bytes.Length - i * VisualProtocol.ChunkSize)];
                    Buffer.BlockCopy(bytes, i * VisualProtocol.ChunkSize, chunk, 0, chunk.Length);
                    using (Bitmap frame = VisualProtocol.EncodeFrame(id, i, count, bytes.Length, chunk)) capture.Emit(frame);
                }
            }
            catch (Exception error) { capture.WorkerError = error; }
        });
        return bytes.Length;
    }

    private static void TiledFramePublishesAllPacketsOnce()
    {
        using (Fixture fixture = new Fixture())
        {
            int decodedTiles = 0;
            fixture.Services.DecodeFrames = delegate(Bitmap bitmap) { Packet[] packets = VisualProtocol.DecodePackets(bitmap); decodedTiles = packets.Length; return packets; };
            fixture.Services.DecodeFrame = delegate { throw new Exception("The production packet-array decoder must handle every tile."); };
            CameraDevice camera = Device("Tiled camera", "tiled"); fixture.Devices.Add(camera);
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture); }, "Tiled capture starts");
                byte[] noise = new byte[900]; new Random(611).NextBytes(noise);
                string text = Convert.ToBase64String(noise);
                ClipboardSnapshot snapshot = new ClipboardSnapshot(); snapshot.Formats.Add(DataFormats.UnicodeText, Encoding.UTF8.GetBytes(text));
                string description; byte[] payload = ClipboardPayload.Encode(snapshot, out description);
                Check(payload.Length > 256 && payload.Length <= VisualProtocol.GetFramePayloadBytes(VideoProfile.Compact640), "Fixture requires several packets in one camera frame");
                Guid transfer = Guid.NewGuid();
                using (Bitmap frame = VisualProtocol.EncodePacketFrame(transfer, 0, payload, VideoProfile.Compact640)) fixture.LastCapture.Emit(frame);
                PumpUntil(delegate { return fixture.PublishCount == 1; }, "One frame delivers every tile to the clipboard assembler");
                Check(decodedTiles > 1, "Array decoder returned multiple independently validated packets");
                Check(Encoding.UTF8.GetString(fixture.Published.Formats[DataFormats.UnicodeText]) == text && fixture.PublishedId == transfer, "Tiled transfer reconstructs the original clipboard payload");
                Check(fixture.PublishApartment == ApartmentState.STA && !receiver.IsReceiving, "Tiled payload publishes once on the UI STA");
                using (Bitmap duplicate = VisualProtocol.EncodePacketFrame(transfer, 0, payload, VideoProfile.Compact640)) fixture.LastCapture.Emit(duplicate);
                PumpFor(100);
                Check(fixture.PublishCount == 1, "A repeated tiled frame cannot republish completed clipboard data");
            }
        }
    }

    private static CameraDevice Device(string name, string id) { return new CameraDevice(name, id); }

    private static void RealModeSelectionAndExactCameraRefresh()
    {
        using (Fixture fixture = new Fixture())
        {
            CameraDevice selected = Device("Same camera", "exact:first");
            fixture.Devices.Add(selected);
            fixture.Devices.Add(Device("Same camera", "exact:other"));
            bool expanded = false;
            int explicitCalls = 0;
            CaptureBackend backend = CaptureBackend.DirectShowHighest;
            fixture.Services.OpenCaptureBackend = delegate(string id, Action<Bitmap> callback, CaptureBackend requestedBackend)
            {
                Check(id == selected.Id, "Refresh keeps the exact selected unit despite duplicate friendly names");
                backend = requestedBackend;
                FakeCapture result = (FakeCapture)fixture.Open(id, callback);
                result.Modes = expanded ? new[] { new CameraResolution(1920, 1080), new CameraResolution(1280, 720) } : new[] { new CameraResolution(1280, 720), new CameraResolution(640, 480) };
                result.Actual = result.Modes[0];
                return result;
            };
            fixture.Services.OpenCaptureAtResolution = delegate(string id, Action<Bitmap> callback, CameraResolution requested)
            {
                explicitCalls++;
                FakeCapture result = (FakeCapture)fixture.Services.OpenCaptureBackend(id, callback, CaptureBackend.MediaFoundation);
                foreach (CameraResolution mode in result.Modes) if (mode.Matches(requested)) { result.Actual = mode; break; }
                return result;
            };
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(selected);
                PumpUntil(delegate { return IsActive(fixture.LastCapture) && receiver.Menu.Items.ContainsKey("video.resolution") && ((ToolStripMenuItem)receiver.Menu.Items["video.resolution"]).DropDownItems.ContainsKey("video.resolution.1280x720"); }, "Real capability catalog reaches the tray");
                ToolStripMenuItem modes = (ToolStripMenuItem)receiver.Menu.Items["video.resolution"];
                Check(modes.DropDownItems.Count == 3 && modes.DropDownItems.ContainsKey("video.resolution.1280x720") && !modes.DropDownItems.ContainsKey("video.resolution.1920x1080"), "Only advertised geometries accompany auto mode");
                int opens = fixture.OpenCount;
                receiver.SelectResolution(new CameraResolution(1920, 1080)); PumpFor(40);
                Check(fixture.OpenCount == opens, "An invented or stale resolution cannot open a camera");
                FakeCapture previous = fixture.LastCapture;
                modes.DropDownItems["video.resolution.640x480"].PerformClick();
                PumpUntil(delegate { return explicitCalls == 1 && IsActive(fixture.LastCapture) && fixture.LastCapture != previous; }, "A resolution click opens an exact native MF mode");
                PumpUntil(delegate { return ((ToolStripMenuItem)((ToolStripMenuItem)receiver.Menu.Items["video.resolution"]).DropDownItems["video.resolution.640x480"]).Checked; }, "The requested actual native mode is checked");
                Check(previous.Disposed && backend == CaptureBackend.MediaFoundation, "The previous graph is released before the exact MF mode starts");
                using (Bitmap frame = new Bitmap(2, 2)) fixture.LastCapture.Emit(frame);
                PumpFor(25);
                expanded = true;
                previous = fixture.LastCapture;
                receiver.SelectResolution(null);
                PumpUntil(delegate { return IsActive(fixture.LastCapture) && fixture.LastCapture != previous && ((ToolStripMenuItem)receiver.Menu.Items["video.resolution"]).DropDownItems.ContainsKey("video.resolution.1920x1080"); }, "Explicit auto refresh sees newly advertised1080p on the same unit");
                Check(backend == CaptureBackend.MediaFoundation && previous.Disposed && explicitCalls == 1, "Refreshing a working MF source preserves its family and returns auto to actual maximum");
                using (Bitmap frame = new Bitmap(2, 2)) fixture.LastCapture.Emit(frame);
                PumpFor(25);
                previous = fixture.LastCapture;
                receiver.SelectDevice(selected);
                PumpUntil(delegate { return IsActive(fixture.LastCapture) && fixture.LastCapture != previous; }, "Clicking the selected camera refreshes instead of doing nothing");
                Check(previous.Disposed && backend == CaptureBackend.MediaFoundation, "Selected-camera refresh does not return to a failed DirectShow family");
                PumpUntil(delegate { return ((ToolStripMenuItem)receiver.Menu.Items["video.resolution"]).DropDownItems.ContainsKey("video.resolution.1920x1080"); }, "Refreshed catalog is published after capture start");
                expanded = false; previous = fixture.LastCapture;
                ((ToolStripMenuItem)receiver.Menu.Items["video.resolution"]).DropDownItems["video.resolution.1920x1080"].PerformClick();
                PumpUntil(delegate { return IsActive(fixture.LastCapture) && fixture.LastCapture != previous && ((ToolStripMenuItem)((ToolStripMenuItem)receiver.Menu.Items["video.resolution"]).DropDownItems["video.resolution.auto"]).Checked && !((ToolStripMenuItem)receiver.Menu.Items["video.resolution"]).DropDownItems.ContainsKey("video.resolution.1920x1080"); }, "A mode disappearing during fresh native enumeration clears its stale check and returns to actual auto maximum");
                opens = fixture.OpenCount; Refresh(receiver); PumpFor(40);
                Check(fixture.OpenCount == opens, "Ordinary tray enumeration never reopens a working stream");
            }
        }
    }
    private static void ManualHighResolutionKeepsTheWorkingDirectShowBackend()
    {
        using (Fixture fixture = new Fixture())
        {
            var camera = Device("Duplicated camera name", "manual-uhd:exact");
            fixture.Devices.Add(camera); fixture.Devices.Add(Device(camera.Name, "manual-uhd:foreign"));
            int preferredCalls = 0; FakeCapture original = null;
            fixture.Services.OpenCaptureBackend = delegate(string id, Action<Bitmap> callback, CaptureBackend backend)
            {
                Check(id == camera.Id && backend == CaptureBackend.DirectShowHighest, "Manual4K keeps the exact selected camera and the already working DirectShow family");
                var capture = (FakeCapture)fixture.Open(id, callback);
                capture.Modes = new[] { new CameraResolution(3840, 2160), new CameraResolution(2560, 1440), new CameraResolution(1920, 1080) };
                capture.Actual = new CameraResolution(1920, 1080); return capture;
            };
            fixture.Services.OpenCaptureBackendAtResolution = delegate(string id, Action<Bitmap> callback, CaptureBackend backend, CameraResolution requested)
            {
                preferredCalls++;
                Check(original != null && original.Disposed, "The prior graph is released before applying an explicitly selected native4K mode");
                Check(requested.Matches(new CameraResolution(3840, 2160)), "Manual4K reaches the production backend-aware native-preference factory unchanged");
                var capture = (FakeCapture)fixture.Services.OpenCaptureBackend(id, callback, backend);
                capture.Actual = requested; return capture;
            };
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture) && HasResolution(receiver, 3840, 2160); }, "Actual high-resolution capabilities are listed without an automatic upgrade");
                original = fixture.LastCapture;
                Check(preferredCalls == 0 && original.Actual.Matches(new CameraResolution(1920, 1080)), "Offering4K leaves initial automatic capture at1080p");
                using (Bitmap frame = new Bitmap(2, 2)) original.Emit(frame);
                PumpFor(20);
                receiver.SelectResolution(new CameraResolution(3840, 2160));
                PumpUntil(delegate { return preferredCalls == 1 && fixture.LastCapture != original && IsActive(fixture.LastCapture); }, "A manual4K click reopens the working DirectShow backend with the exact native request");
                PumpUntil(delegate { return ((ToolStripMenuItem)((ToolStripMenuItem)receiver.Menu.Items["video.resolution"]).DropDownItems["video.resolution.3840x2160"]).Checked; }, "The actual selected4K mode is checked after native readback");
                Check(fixture.LastCapture.Actual.Matches(new CameraResolution(3840, 2160)) && fixture.OpenCount == 2 && fixture.PublishCount == 0,
                    "Manual4K does not switch cameras, open a third graph or publish control metadata as clipboard content");
            }
        }
    }

    private static void SourceFormatChangesRefreshCapabilitiesWithoutRestart()
    {
        using (Fixture fixture = new Fixture())
        {
            long now = 1000; bool expanded = false; Packet incoming = null;
            var camera = Device("Duplicate camera", "exact:source");
            fixture.Devices.Add(camera); fixture.Devices.Add(Device(camera.Name, "exact:foreign"));
            fixture.Services.Milliseconds = delegate { return now; };
            fixture.Services.DecodeFrames = delegate { return incoming == null ? new Packet[0] : new[] { incoming }; };
            fixture.Services.OpenCaptureBackend = delegate(string id, Action<Bitmap> callback, CaptureBackend backend)
            {
                Check(id == camera.Id, "Automatic source recovery keeps the exact selected camera unit");
                FakeCapture capture = (FakeCapture)fixture.Open(id, callback);
                capture.Modes = expanded ? new[] { new CameraResolution(1920, 1080), new CameraResolution(1280, 720) } : new[] { new CameraResolution(1280, 720) };
                capture.Actual = capture.Modes[0]; return capture;
            };
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture) && HasResolution(receiver, 1280, 720); }, "Source test starts with real720p capabilities");
                FakeCapture first = fixture.LastCapture;
                Guid session = Guid.NewGuid(), firstRevision = Guid.NewGuid(), nextRevision = Guid.NewGuid();
                incoming = FormatPacket(session, firstRevision, 1280, 720);
                using (Bitmap frame = new Bitmap(1280, 720)) first.Emit(frame);
                PumpFor(20); now = 2000; receiver.PollCaptureRetry(); PumpFor(20);
                Check(fixture.OpenCount == 1, "The first source announcement matching the actual camera does not reopen it");
                incoming = FormatPacket(session, firstRevision, 1280, 720, 0);
                using (Bitmap frame = new Bitmap(1280, 720)) first.Emit(frame);
                PumpFor(20); now = 2100;
                incoming = FormatPacket(session, nextRevision, 1920, 1080);
                using (Bitmap frame = new Bitmap(1280, 720)) first.Emit(frame);
                PumpFor(20); now = 3099; receiver.PollCaptureRetry(); PumpFor(20);
                Check(fixture.OpenCount == 1, "Changed source capabilities get a settle interval rather than immediate repeated opens");
                now = 3100; receiver.PollCaptureRetry();
                PumpUntil(delegate { return fixture.OpenCount == 2 && IsActive(fixture.LastCapture) && first.Disposed; }, "Source change refreshes the same camera without restarting the client");
                FakeCapture delayed = fixture.LastCapture;
                Check(!HasResolution(receiver, 1920, 1080), "A still-stale fresh catalog does not invent1080p");
                incoming = FormatPacket(session, firstRevision, 1280, 720);
                using (Bitmap frame = new Bitmap(1280, 720)) delayed.Emit(frame);
                PumpFor(20);
                expanded = true; now = 13100; receiver.PollCaptureRetry();
                PumpUntil(delegate { return fixture.OpenCount == 3 && IsActive(fixture.LastCapture) && HasResolution(receiver, 1920, 1080); }, "A delayed advertised1080p mode is discovered by a bounded automatic retry");
                Check(delayed.Disposed && receiver.SelectedCameraId == camera.Id, "A retired source graph is disposed and the selected exact unit is retained");
                incoming = FormatPacket(session, nextRevision, 1920, 1080);
                using (Bitmap frame = new Bitmap(1920, 1080)) fixture.LastCapture.Emit(frame);
                PumpFor(20); now = 43100;
                using (Bitmap frame = new Bitmap(1920, 1080)) fixture.LastCapture.Emit(frame);
                PumpFor(20); receiver.PollCaptureRetry(); PumpFor(20);
                Check(fixture.OpenCount == 3 && fixture.PublishCount == 0, "Successful refreshed geometry stops retries and source metadata never enters the clipboard");
                using (Bitmap frame = new Bitmap(1280, 720)) first.Emit(frame);
                PumpFor(20);
                Refresh(receiver); Check(fixture.OpenCount == 3, "Retired callbacks and tray enumeration cannot restart healthy recovered video");
                Check(!receiver.Menu.Items.ContainsKey("video.sei"), "The removed SEI probe is not offered in the client tray");
            }
        }
    }

    private static void SourceRefreshIsBoundedAndHonorsManualResolution()
    {
        using (Fixture fixture = new Fixture())
        {
            long now = 1000; Packet incoming = null; bool allowManual = false;
            var camera = Device("Camera", "source:bounded"); fixture.Devices.Add(camera);
            fixture.Services.Milliseconds = delegate { return now; };
            fixture.Services.DecodeFrames = delegate { return incoming == null ? new Packet[0] : new[] { incoming }; };
            fixture.Services.OpenCaptureBackend = delegate(string id, Action<Bitmap> callback, CaptureBackend backend)
            {
                FakeCapture capture = (FakeCapture)fixture.Open(id, callback);
                capture.Modes = new[] { new CameraResolution(1280, 720), new CameraResolution(640, 480) };
                capture.Actual = capture.Modes[0]; return capture;
            };
            fixture.Services.OpenCaptureAtResolution = delegate(string id, Action<Bitmap> callback, CameraResolution requested)
            {
                FakeCapture capture = (FakeCapture)fixture.Services.OpenCaptureBackend(id, callback, CaptureBackend.MediaFoundation);
                if (allowManual) capture.Actual = requested; return capture;
            };
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture) && HasResolution(receiver, 640, 480); }, "Bounded source fixture starts");
                Guid session = Guid.NewGuid(), revision = Guid.NewGuid();
                incoming = FormatPacket(session, revision, 1920, 1080);
                using (Bitmap frame = new Bitmap(1280, 720)) fixture.LastCapture.Emit(frame);
                PumpFor(20);
                foreach (long at in new long[] { 2000, 12000, 42000 })
                {
                    now = at;
                    // Normal video remains active while the source catalog is stale.
                    using (Bitmap frame = new Bitmap(1280, 720)) fixture.LastCapture.Emit(frame);
                    PumpFor(20); int before = fixture.OpenCount; receiver.PollCaptureRetry();
                    PumpUntil(delegate { return fixture.OpenCount == before + 1 && IsActive(fixture.LastCapture); }, "Each scheduled source retry opens exactly one fresh graph");
                }
                now = 72000;
                using (Bitmap frame = new Bitmap(1280, 720)) fixture.LastCapture.Emit(frame);
                PumpFor(20); receiver.PollCaptureRetry(); PumpFor(20);
                int opens = fixture.OpenCount;
                now = 100000;
                using (Bitmap frame = new Bitmap(1280, 720)) fixture.LastCapture.Emit(frame);
                PumpFor(20); receiver.PollCaptureRetry(); Refresh(receiver);
                Check(opens == 4 && fixture.OpenCount == opens && IsActive(fixture.LastCapture), "Unavailable source geometry gets only three retries and leaves the working stream active");
                allowManual = true; receiver.SelectResolution(new CameraResolution(640, 480));
                PumpUntil(delegate { return fixture.OpenCount == opens + 1 && IsActive(fixture.LastCapture); }, "User can select an advertised manual resolution after bounded automatic recovery");
                incoming = FormatPacket(session, Guid.NewGuid(), 1920, 1080);
                using (Bitmap frame = new Bitmap(640, 480)) fixture.LastCapture.Emit(frame);
                PumpFor(20); now = 101000; receiver.PollCaptureRetry();
                PumpUntil(delegate { return fixture.OpenCount == opens + 2 && IsActive(fixture.LastCapture); }, "New source revision refreshes the real catalog while preserving a manual request");
                using (Bitmap frame = new Bitmap(640, 480)) fixture.LastCapture.Emit(frame);
                PumpFor(20); now = 111000; receiver.PollCaptureRetry(); PumpFor(20);
                Check(fixture.OpenCount == opens + 2 && fixture.LastCapture.Actual.Matches(new CameraResolution(640, 480)), "A640p manual choice is not forced up to1080p or repeatedly reopened");
            }
        }
    }

    private static void AutoSourcePreferenceKeepsTheWorkingBackendWhenBothModesAreOffered()
    {
        foreach (CaptureBackend working in new[] { CaptureBackend.DirectShowHighest, CaptureBackend.MediaFoundation })
        using (Fixture fixture = new Fixture())
        {
            long now = 1000; Packet incoming = null; bool rejectedInitialDs = false;
            int preferredCalls = 0; CaptureBackend preferredBackend = CaptureBackend.DirectShowDefault;
            CameraResolution requested = null;
            var camera = Device("Same-name source", "dual-mode:exact");
            fixture.Devices.Add(camera); fixture.Devices.Add(Device(camera.Name, "dual-mode:other"));
            fixture.Services.Milliseconds = delegate { return now; };
            fixture.Services.DecodeFrames = delegate { return incoming == null ? new Packet[0] : new[] { incoming }; };
            fixture.Services.OpenCaptureBackend = delegate(string id, Action<Bitmap> callback, CaptureBackend backend)
            {
                Check(id == camera.Id, "Dual-mode auto-follow always binds the exact selected camera unit");
                if (working == CaptureBackend.MediaFoundation && backend == CaptureBackend.DirectShowHighest && !rejectedInitialDs)
                { rejectedInitialDs = true; throw new CameraFormatException("synthetic_ds_rejection", 1920, 1080, unchecked((int)0x8004022a)); }
                FakeCapture capture = (FakeCapture)fixture.Open(id, callback);
                capture.Modes = new[] { new CameraResolution(1920, 1080), new CameraResolution(1280, 720) };
                capture.Actual = capture.Modes[0]; return capture;
            };
            fixture.Services.OpenCaptureBackendAtResolution = delegate(string id, Action<Bitmap> callback, CaptureBackend backend, CameraResolution mode)
            {
                preferredCalls++; preferredBackend = backend; requested = mode;
                FakeCapture capture = (FakeCapture)fixture.Services.OpenCaptureBackend(id, callback, backend);
                foreach (CameraResolution offered in capture.Modes) if (offered.Matches(mode)) capture.Actual = offered;
                return capture;
            };
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                if (working == CaptureBackend.MediaFoundation)
                {
                    PumpUntil(delegate { return rejectedInitialDs && typeof(ReceiverContext).GetField("current", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(receiver) == null; },
                        "The rejected DirectShow attempt retires before the independent MF retry");
                    now = 4000; receiver.PollCaptureRetry();
                }
                PumpUntil(delegate { return IsActive(fixture.LastCapture) && HasResolution(receiver, 1920, 1080) && HasResolution(receiver, 1280, 720); },
                    "The actual source advertises both1080p and720p before its format changes");
                int opens = fixture.OpenCount;
                Guid session = Guid.NewGuid();
                incoming = FormatPacket(session, Guid.NewGuid(), 1920, 1080);
                using (Bitmap frame = new Bitmap(1920, 1080)) fixture.LastCapture.Emit(frame);
                PumpFor(25); now = 5100;
                incoming = FormatPacket(session, Guid.NewGuid(), 1280, 720);
                using (Bitmap frame = new Bitmap(1920, 1080)) fixture.LastCapture.Emit(frame);
                PumpFor(25); now = 6100; receiver.PollCaptureRetry();
                PumpUntil(delegate { return preferredCalls == 1 && fixture.OpenCount == opens + 1 && IsActive(fixture.LastCapture); },
                    "Source change requests a native preference even when highest advertised geometry remains1080p");
                Check(preferredBackend == working && requested.Matches(new CameraResolution(1280, 720)) && fixture.LastCapture.Actual.Matches(requested),
                    "Auto-follow selects the advertised720p source using the already working DirectShow or MF family");
                PumpUntil(delegate { return HasResolution(receiver, 1920, 1080) && HasResolution(receiver, 1280, 720); }, "The catalog continues to expose both real native modes");
                now = 16100;
                using (Bitmap frame = new Bitmap(1280, 720)) fixture.LastCapture.Emit(frame);
                PumpFor(25); receiver.PollCaptureRetry(); PumpFor(25);
                Check(fixture.OpenCount == opens + 1 && preferredCalls == 1,
                    "The auto preference aligns capture to source rather than reopening highest1080p three times");
                ToolStripMenuItem resolutions = (ToolStripMenuItem)receiver.Menu.Items["video.resolution"];
                Check(((ToolStripMenuItem)resolutions.DropDownItems["video.resolution.auto"]).Checked &&
                    !((ToolStripMenuItem)resolutions.DropDownItems["video.resolution.1280x720"]).Checked,
                    "Following source metadata retains Auto and does not invent a manual user selection");
            }
        }
    }

    private static void SourceRetriesWaitForReservedBenchmarkWithoutBlockingFirstOrManualRefresh()
    {
        using (Fixture fixture = new Fixture())
        {
            long now = 1000; Packet incoming = null;
            var camera = Device("Benchmark source", "source:reserved"); fixture.Devices.Add(camera);
            fixture.Services.Milliseconds = delegate { return now; };
            fixture.Services.DecodeFrames = delegate { return incoming == null ? new Packet[0] : new[] { incoming }; };
            fixture.Services.OpenCaptureBackend = delegate(string id, Action<Bitmap> callback, CaptureBackend backend)
            {
                Check(id == camera.Id, "Deferred source refresh retains the exact selected unit");
                FakeCapture capture = (FakeCapture)fixture.Open(id, callback);
                capture.Modes = new[] { new CameraResolution(1280, 720), new CameraResolution(320, 240) };
                capture.Actual = capture.Modes[0]; return capture;
            };
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); receiver.SelectDevice(camera);
                PumpUntil(delegate { return IsActive(fixture.LastCapture) && HasResolution(receiver, 1280, 720); }, "Reserved source fixture begins with real720p capabilities");
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                Control owner = (Control)typeof(ReceiverContext).GetField("dispatch", flags).GetValue(receiver);
                var audio = new ClientAudioBridge(owner, new ReservedFileServices(delegate { return now; }));
                typeof(ReceiverContext).GetField("audioBridge", flags).SetValue(receiver, audio);
                var command = new FileBenchmarkCommand { Session = Guid.NewGuid(), Test = Guid.NewGuid(), Profile = VideoProfile.Dense,
                    FileSize = 1024, Seed = 27, Phase = FileBenchmarkPhase.Prepare };
                audio.HandleVideo(BridgeControl.Single(command.Session, FileBenchmarkProtocol.Command(command)));
                Check(audio.HasFileBenchmark, "The actual mocked audio bridge reserves a prepared file test");
                incoming = FormatPacket(command.Session, Guid.NewGuid(), 1920, 1080);
                using (Bitmap frame = new Bitmap(1280, 720)) fixture.LastCapture.Emit(frame);
                PumpFor(20); now = 2000; receiver.PollCaptureRetry();
                PumpUntil(delegate { return fixture.OpenCount == 2 && IsActive(fixture.LastCapture); }, "The first real source-format refresh remains allowed during a file benchmark");
                foreach (long at in new long[] { 12000, 14000 })
                {
                    now = at;
                    using (Bitmap frame = new Bitmap(1280, 720)) fixture.LastCapture.Emit(frame);
                    PumpFor(20); receiver.PollCaptureRetry(); PumpFor(20);
                }
                Check(fixture.OpenCount == 2 && audio.HasFileBenchmark, "Subsequent automatic retries leave the reserved FileRun and its capture uninterrupted");
                receiver.RefreshCameraModes();
                PumpUntil(delegate { return fixture.OpenCount == 3 && IsActive(fixture.LastCapture); }, "Explicit manual refresh bypasses benchmark deferral");
                incoming = FormatPacket(command.Session, Guid.NewGuid(), 1920, 1080);
                using (Bitmap frame = new Bitmap(1280, 720)) fixture.LastCapture.Emit(frame);
                PumpFor(20); now = 15000; receiver.PollCaptureRetry();
                PumpUntil(delegate { return fixture.OpenCount == 4 && IsActive(fixture.LastCapture); }, "A new source revision still gets its first refresh despite the benchmark reservation");
                now = 25000;
                using (Bitmap frame = new Bitmap(1280, 720)) fixture.LastCapture.Emit(frame);
                PumpFor(20); receiver.PollCaptureRetry(); PumpFor(20);
                Check(fixture.OpenCount == 4, "Deferring a retry never consumes its remaining attempt budget");
                command.Phase = FileBenchmarkPhase.Cancel;
                audio.HandleVideo(BridgeControl.Single(command.Session, FileBenchmarkProtocol.Command(command)));
                Check(!audio.HasFileBenchmark, "Cancel releases the actual reserved FileRun");
                receiver.PollCaptureRetry();
                PumpUntil(delegate { return fixture.OpenCount == 5 && IsActive(fixture.LastCapture); }, "The next poll resumes the due retry after benchmark cancellation");
                now = 55000;
                using (Bitmap frame = new Bitmap(1280, 720)) fixture.LastCapture.Emit(frame);
                PumpFor(20); receiver.PollCaptureRetry();
                PumpUntil(delegate { return fixture.OpenCount == 6 && IsActive(fixture.LastCapture); }, "Only the final bounded retry remains after the reservation ends");
                now = 85000;
                using (Bitmap frame = new Bitmap(1280, 720)) fixture.LastCapture.Emit(frame);
                PumpFor(20); receiver.PollCaptureRetry(); PumpFor(20);
                Check(fixture.OpenCount == 6 && IsActive(fixture.LastCapture), "Deferral cannot create an unbounded source-reopen loop");
            }
        }
    }

    private sealed class ReservedFileServices : ClientAudioBridgeServices
    {
        private readonly Func<long> now;
        internal ReservedFileServices(Func<long> now) : base(null) { this.now = now; }
        internal override long Milliseconds { get { return now(); } }
        internal override void Enable(Guid session) { }
        internal override void Send(byte[] bytes, Guid transferId) { }
        internal override void SetBenchmarkMode(bool active) { }
        internal override IDisposable Schedule(Action action, int milliseconds) { return new EmptyLifetime(); }
        internal override void Dispatch(Control owner, Action action) { action(); }
        private sealed class EmptyLifetime : IDisposable { public void Dispose() { } }
    }

    private static Packet FormatPacket(Guid session, Guid revision, int width, int height, int retries = 2)
    {
        byte[] wire = BridgeControl.CameraFormat(session, revision, width, height, 30, retries);
        return new Packet { TransferId = session, Index = 0, Count = 1, TotalLength = wire.Length, ChunkSize = 256,
            Profile = VideoProfile.Compatible, Data = wire };
    }

    private static bool HasResolution(ReceiverContext receiver, int width, int height)
    {
        ToolStripMenuItem menu = receiver.Menu.Items["video.resolution"] as ToolStripMenuItem;
        return menu != null && menu.DropDownItems.ContainsKey("video.resolution." + width + "x" + height);
    }

    private static bool IsActive(FakeCapture capture) { return capture != null && capture.Started && !capture.Disposed; }

    private static void Refresh(ReceiverContext receiver)
    {
        bool completed = false;
        receiver.RefreshDevices(delegate { completed = true; });
        PumpUntil(delegate { return completed; }, "Camera enumeration completes");
    }

    private static void CheckMenu(ReceiverContext receiver, params string[] cameras)
    {
        Check(receiver.Menu.Items.Count == cameras.Length + (cameras.Length == 0 ? 3 : 4) + (String.IsNullOrEmpty(receiver.SelectedCameraId) ? 0 : 3),
            "Menu contains current cameras, selected-camera controls, separators, and Exit");
        for (int i = 0; i < cameras.Length; i++)
        {
            ToolStripMenuItem item = receiver.Menu.Items[i] as ToolStripMenuItem;
            Check(item != null && item.Text == cameras[i] && item.Enabled && item.DropDownItems.Count == 0,
                "Each camera is one enabled menu entry with its device name");
            Check(!item.AutoToolTip && String.IsNullOrEmpty(item.ToolTipText), "Camera entries have no extra tooltip text");
        }
        if (cameras.Length > 0) Check(receiver.Menu.Items[cameras.Length] is ToolStripSeparator, "Exit is separated from cameras");
        ToolStripMenuItem exit = receiver.Menu.Items[receiver.Menu.Items.Count - 1] as ToolStripMenuItem;
        var settings = receiver.Menu.Items["settings"] as ToolStripMenuItem;
        var detailed = settings == null ? null : settings.DropDownItems["settings.debug_log"] as ToolStripMenuItem;
        Check(detailed != null && detailed.CheckOnClick && detailed.Checked == DiagnosticLog.IsEnabled, "Portable debug logging has an explicit checkbox reflecting actual logger state");
        Check(exit != null && exit.Text == "Выход", "Exit is the final menu action");
        var version = receiver.Menu.Items["application.version"] as ToolStripMenuItem;
        Check(version != null && !version.Enabled && version.Text.Contains(typeof(ReceiverContext).Assembly.GetName().Version.ToString(3)),
            "Tray shows a disabled version taken from the running assembly near Exit");
    }

    private static void PumpUntil(Func<bool> condition, string message)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < 5000)
        {
            Application.DoEvents();
            Check(Application.OpenForms.Count == 0, "No receiver form appears while processing messages");
            Thread.Sleep(5);
        }
        Application.DoEvents();
        Check(condition(), message);
    }

    private static void PumpFor(int milliseconds)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static void PreferredDecoderLearnsPerCapture()
    {
        using (Fixture fixture = new Fixture())
        {
            CameraDevice first = Device("First hint camera", "hint-a"), second = Device("Second hint camera", "hint-b");
            fixture.Devices.Add(first); fixture.Devices.Add(second);
            var hints = new List<VideoProfile?>(); var packets = new Queue<Packet[]>(); Guid session = Guid.NewGuid();
            var command = new FileBenchmarkCommand { Session = session, Test = Guid.NewGuid(), Profile = VideoProfile.Mp4Modem,
                Phase = FileBenchmarkPhase.Prepare, FileSize = 1024, Seed = 7 };
            packets.Enqueue(new[] { BridgeControl.Single(session, FileBenchmarkProtocol.Command(command)) });
            packets.Enqueue(new[] { new Packet { TransferId = Guid.NewGuid(), Index = 0, Count = 1, TotalLength = 1,
                Data = new byte[] { 1 }, ChunkSize = VisualProtocol.GetChunkSize(VideoProfile.Fast), Profile = VideoProfile.Fast } });
            packets.Enqueue(new[] { BridgeControl.Single(session, BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0)) });
            packets.Enqueue(new Packet[0]); packets.Enqueue(new Packet[0]);
            fixture.Services.DecodeFrames = delegate { throw new Exception("The optional preferred decoder must override the legacy hook."); };
            fixture.Services.DecodeFramesPreferred = delegate(Bitmap frame, VideoProfile? hint) { hints.Add(hint); return packets.Dequeue(); };
            fixture.Services.DecodePayload = delegate { return new DecodedPayload(); };
            using (ReceiverContext receiver = fixture.Create())
            using (Bitmap frame = new Bitmap(2, 2))
            {
                receiver.SelectDevice(first); PumpUntil(delegate { return fixture.Capture(first.Id) != null && fixture.Capture(first.Id).Started; }, "Hint capture starts");
                FakeCapture previous = fixture.Capture(first.Id);
                previous.Emit(frame); previous.Emit(frame); previous.Emit(frame); previous.Emit(frame);
                Check(hints.Count == 4 && !hints[0].HasValue && hints[1] == VideoProfile.Mp4Modem && hints[2] == VideoProfile.Fast && hints[3] == VideoProfile.Fast,
                    "A validated command hints its data decoder before publication; subsequent data learns a new profile and ordinary control does not overwrite it");
                receiver.SelectDevice(second); PumpUntil(delegate { return fixture.Capture(second.Id) != null && fixture.Capture(second.Id).Started; }, "New hint capture starts");
                previous.Emit(frame); Check(hints.Count == 4, "A retired capture cannot update the replacement's decoder hint");
                fixture.Capture(second.Id).Emit(frame);
                Check(hints.Count == 5 && !hints[4].HasValue, "A new camera session does not inherit another capture's preferred profile");
            }
        }
    }

    private sealed class FakeCapture : IReceiverCapture, IReceiverCaptureModes
    {
        private readonly Action<Bitmap> onFrame;
        internal volatile bool Started, Disposed;
        internal Exception WorkerError;
        internal CameraResolution[] Modes = new CameraResolution[0];
        internal CameraResolution Actual;
        public CameraResolution[] AvailableResolutions { get { return (CameraResolution[])Modes.Clone(); } }
        public CameraResolution CurrentResolution { get { return Actual; } }
        public event Action<Exception> OnError;
        internal FakeCapture(Action<Bitmap> callback) { onFrame = callback; }
        public void Start() { if (Disposed) throw new ObjectDisposedException("FakeCapture"); Started = true; }
        public void Dispose() { Disposed = true; }
        internal void Emit(Bitmap frame) { onFrame(frame); }
        internal void Fail(Exception error) { Action<Exception> handler = OnError; if (handler != null) handler(error); }
    }

    private static void OutputChoiceAutomaticallyProbesTheExactLatestEndpoint()
    {
        using (Fixture fixture = new Fixture())
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            object gate = new object(); var probes = new List<string>(); string active = null, saved = null; int sets = 0, block = 0;
            fixture.Services.RestoreSelection = true;
            fixture.Services.ReadAudioSelection = delegate { return new[] { "waveout:3", "Full Realtek output" }; };
            fixture.Services.ListAudioOutputs = delegate { return new[] { new AudioEndpoint("waveout:0", "AMM output"), new AudioEndpoint("waveout:3", "Full Realtek output") }; };
            fixture.Services.SetAudioOutput = delegate(string id)
            {
                if (id == "waveout:3" && Interlocked.Exchange(ref block, 0) == 1) { entered.Set(); release.WaitOne(); }
                lock (gate) { active = id; sets++; }
            };
            fixture.Services.TestAudioOutput = delegate { lock (gate) probes.Add(active); return Guid.NewGuid(); };
            fixture.Services.WriteAudioSelection = delegate(string[] value) { lock (gate) saved = value[0]; };
            using (ReceiverContext receiver = fixture.Create())
            {
                Refresh(receiver); PumpUntil(delegate { lock (gate) return sets >= 1; }, "Saved audio selection is restored asynchronously");
                ToolStripMenuItem outputs = receiver.Menu.Items["audio.output"] as ToolStripMenuItem;
                Check(outputs != null && outputs.DropDownItems.Count == 2 && !receiver.Menu.Items.ContainsKey("audio.verified"),
                    "Client offers one ordinary output selector without verified-route menus or calibration locks");
                lock (gate) Check(active == "waveout:3" && probes.Count == 0, "Startup/restoration applies the saved exact endpoint without an automatic sound");
                var realtek = (ToolStripMenuItem)outputs.DropDownItems[1];
                Check(realtek.Checked, "The saved Realtek choice is checked before a user clicks it again");
                realtek.PerformClick();
                PumpUntil(delegate { lock (gate) return probes.Count == 1 && saved == "waveout:3"; }, "Re-selecting the SAME output applies it and automatically queues one diagnostic probe");
                lock (gate) Check(probes[0] == "waveout:3", "The diagnostic uses the chosen Realtek output rather than another effective verified/default output");
                Interlocked.Exchange(ref block, 1);
                var timer = Stopwatch.StartNew(); realtek.PerformClick(); timer.Stop();
                Check(timer.ElapsedMilliseconds < 150, "A slow endpoint change never runs device work on the tray/UI thread");
                PumpUntil(delegate { return entered.WaitOne(0); }, "The old endpoint selection is blocked on its background worker");
                ((ToolStripMenuItem)outputs.DropDownItems[0]).PerformClick(); release.Set();
                PumpUntil(delegate { lock (gate) return probes.Count == 2 && saved == "waveout:0"; }, "A rapid newer output choice supersedes the blocked probe and completes");
                lock (gate) Check(probes[1] == "waveout:0" && active == "waveout:0", "Only the latest chosen endpoint gets the next probe after a slow stale change");
                Check(((ToolStripMenuItem)outputs.DropDownItems[0]).Checked && !realtek.Checked, "Tray checks stay synchronized with the latest explicit choice");
                receiver.Menu.Items["audio.test"].PerformClick();
                PumpUntil(delegate { lock (gate) return probes.Count == 3; }, "The manual diagnostic remains available for a repeat");
                lock (gate) Check(probes[2] == "waveout:0", "Manual repeat also applies the exact currently requested output before probing");
                Refresh(receiver); PumpFor(80);
                lock (gate) Check(probes.Count == 3, "Menu refresh and saved-selection resolution never create unsolicited probes");
                var version = receiver.Menu.Items["application.version"] as ToolStripMenuItem;
                Check(version != null && !version.Enabled && version.Text.Contains(typeof(ReceiverContext).Assembly.GetName().Version.ToString(3)), "The client exposes its assembly version as a disabled tray item");
            }
        }
    }

    private static void ClosingInvalidatesPendingOutputProbe()
    {
        using (Fixture fixture = new Fixture())
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            int completed = 0, probes = 0;
            fixture.Services.ListAudioOutputs = delegate { return new[] { new AudioEndpoint("waveout:3", "Manual output") }; };
            fixture.Services.SetAudioOutput = delegate(string id)
            { if (id == "waveout:3") { entered.Set(); release.WaitOne(); } Interlocked.Increment(ref completed); };
            fixture.Services.TestAudioOutput = delegate { Interlocked.Increment(ref probes); return Guid.NewGuid(); };
            fixture.Services.WriteAudioSelection = delegate { };
            ReceiverContext receiver = fixture.Create();
            try
            {
                Refresh(receiver); PumpUntil(delegate { return Volatile.Read(ref completed) == 1; }, "Initial restoration performs no diagnostic");
                var outputs = (ToolStripMenuItem)receiver.Menu.Items["audio.output"]; outputs.DropDownItems[0].PerformClick();
                PumpUntil(delegate { return entered.WaitOne(0); }, "An explicit output change is pending before close");
                receiver.Dispose(); release.Set();
                PumpUntil(delegate { return Volatile.Read(ref completed) == 2; }, "An already-running endpoint operation finishes safely after close");
                Check(Volatile.Read(ref probes) == 0, "Closing invalidates a pending output probe before it can emit sound");
            }
            finally { release.Set(); receiver.Dispose(); }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, FakeCapture> captures = new Dictionary<string, FakeCapture>();
        internal readonly List<CameraDevice> Devices = new List<CameraDevice>();
        internal readonly ReceiverServices Services;
        internal Action<string> BeforeOpen;
        internal volatile bool EnumerationFailure, OpenFailure;
        private int openCount, publishCount;
        internal int OpenCount { get { return Volatile.Read(ref openCount); } }
        internal int PublishCount { get { return Volatile.Read(ref publishCount); } }
        internal int PublishFailuresRemaining;
        internal string StoredSelection, LastOpened;
        internal string StoredAutomaticSelection = "0";
        internal Exception NextOpenError;
        private long clockMilliseconds;
        internal FakeCapture LastCapture;
        internal DecodedPayload Published;
        internal Guid PublishedId;
        internal int ReceiptCount, ReceiptBytes;
        internal Guid ReceiptId;
        internal bool ReceiptAfterPublication;
        internal int PublishThread;
        internal ApartmentState PublishApartment;

        internal Fixture()
        {
            Services = new ReceiverServices
            {
                Milliseconds = delegate { return Interlocked.Add(ref clockMilliseconds, 60000); },
                ListDevices = delegate
                {
                    if (EnumerationFailure) throw new InvalidOperationException("Enumeration test failure");
                    return new List<CameraDevice>(Devices);
                },
                OpenCapture = delegate(string id, Action<Bitmap> callback)
                {
                    return Open(id, callback);
                },
                ReadSelection = delegate { return StoredSelection; },
                WriteSelection = delegate(string id) { StoredSelection = id; },
                ReadAutomaticCameraSelection = delegate { return StoredAutomaticSelection; },
                WriteAutomaticCameraSelection = delegate(bool enabled) { StoredAutomaticSelection = enabled ? "1" : "0"; },
                PreparePayload = delegate { },
                LogFailure = delegate { },
                PublishPayload = delegate(DecodedPayload payload, string root, Guid id)
                {
                    PublishThread = Thread.CurrentThread.ManagedThreadId;
                    PublishApartment = Thread.CurrentThread.GetApartmentState();
                    Interlocked.Increment(ref publishCount);
                    if (PublishFailuresRemaining > 0) { Interlocked.Decrement(ref PublishFailuresRemaining); throw new ExternalException("Clipboard is busy"); }
                    Published = payload; PublishedId = id;
                },
                NotifyPublished = delegate(Guid id, int encodedBytes)
                {
                    ReceiptAfterPublication = Published != null && PublishedId == id;
                    ReceiptCount++; ReceiptId = id; ReceiptBytes = encodedBytes;
                },
                ShowTrayIcon = false,
                EnableDeferredPaste = false,
                EnableRetryTimer = false,
                EnableSessionEvents = false,
                RestoreSelection = false
            };
        }

        internal ReceiverContext Create() { return new ReceiverContext(Services); }
        internal IReceiverCapture Open(string id, Action<Bitmap> callback)
        {
            Interlocked.Increment(ref openCount);
            if (BeforeOpen != null) BeforeOpen(id);
            Exception error = Interlocked.Exchange(ref NextOpenError, null);
            if (error != null) throw error;
            if (OpenFailure) throw new InvalidOperationException("Open test failure");
            FakeCapture capture = new FakeCapture(callback);
            lock (gate) { captures[id] = capture; LastOpened = id; LastCapture = capture; }
            return capture;
        }
        internal FakeCapture Capture(string id) { lock (gate) { FakeCapture capture; return captures.TryGetValue(id, out capture) ? capture : null; } }
        public void Dispose()
        {
            lock (gate)
                foreach (FakeCapture capture in captures.Values)
                    if (capture.WorkerError != null) throw new Exception("Frame worker failed", capture.WorkerError);
        }
    }
}
