// Fake clipboard sequence numbers and snapshots only. Never opens Windows
// clipboard, a camera/audio endpoint, a driver, or a visible application window.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

internal static class ServerClipboardIntentTests
{
    private static int checks;
    [STAThread]
    private static int Main()
    {
        try
        {
            CopyStartSoundCanBeDisabled();
            QueuedCopiesEncodeOnlyTheLatest(); ReplacedRunningCopyCannotPublish(); PreparationInvalidationDiscardsQueuedSnapshots();
            PreparedResultsStayBoundedWhileStaWaits(); PreparationErrorsRecoverOnFreshCopy();
            StartupRequiresNewNotification(); NotificationsCoalesceAndDeduplicate(); GenuineNewCopyIsEncoded(); InactiveAndRestartedCopiesStayIgnored();
            BenchmarkCompletionRequiresNewCopy(); FreshCopyAfterBenchmarkTailSurvivesRetirement(); ReversePublicationDoesNotEcho(); SequenceZeroAndWrap();
            SequenceRacesInvalidateOlderIntent(); ProfileChangesReuseEncodedData(); PreviewStartsDisabled();
            Console.WriteLine("Server clipboard intent checks passed: " + checks + ". Injected sequence/capture/sound counters only; no Windows clipboard, camera/audio or driver actions.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CopyStartSoundCanBeDisabled()
    {
        using (var f = new Fixture(90, true))
        {
            f.Reset("start"); f.Set("ready", true);
            Control[] found = f.Form.Controls.Find("AcceptedSoundEnabled", true);
            Check(found.Length == 1 && found[0] is CheckBox && ((CheckBox)found[0]).Checked, "Copy-start sound defaults on in server settings.");
            var enabled = (CheckBox)found[0]; enabled.Checked = false;
            f.Copy("muted local copy"); f.DrainPreparations(); Application.DoEvents();
            f.CheckCurrent("muted local copy");
            Check(f.Captures == 1 && f.Accepted == 0, "Muting copy-start sound does not suppress a new clipboard transfer.");
            enabled.Checked = true;
            f.Copy("audible local copy"); f.DrainPreparations(); Application.DoEvents();
            f.CheckCurrent("audible local copy");
            Check(f.Captures == 2 && f.Accepted == 1, "Reenabling copy-start sound plays exactly once for the next genuine copy.");
        }
    }

    private static void QueuedCopiesEncodeOnlyTheLatest()
    {
        using (var f = new Fixture(100, true))
        {
            f.Reset("start"); f.Set("ready", true);
            for (int i = 0; i < 25; i++) f.Copy("copy-" + i);
            Check(f.Captures == 25 && f.Accepted == 25, "Every captured fresh copy retains its accepted feedback before worker dispatch.");
            f.DrainPreparations(); Application.DoEvents();
            Check(f.Encoded.Count == 1 && f.Encoded[0] == "copy-24", "Obsolete queued copies must never encode; only the latest captured snapshot may be prepared.");
            f.CheckCurrent("copy-24");
        }
    }

    private static void ReplacedRunningCopyCannotPublish()
    {
        using (var f = new Fixture(200, true))
        {
            f.Reset("start"); f.Set("ready", true); f.Copy("first");
            f.DuringEncode = delegate {
                f.DuringEncode = null;
                for (int i = 0; i < 10; i++) f.Copy("replacement-" + i);
                f.DrainPreparations();
                Application.DoEvents();
                Check(f.Get("current") == null, "A superseded in-flight encode cannot publish while newer work waits.");
            };
            f.DrainPreparations(); Application.DoEvents();
            Check(f.Encoded.Count == 2 && f.Encoded[0] == "first" && f.Encoded[1] == "replacement-9",
                "A running encoder may finish, but all replaced pending snapshots must skip encoding.");
            Check(f.MaximumEncoders == 1, "Forward preparation never runs two encoders concurrently.");
            f.CheckCurrent("replacement-9");
        }
    }

    private static void PreparationInvalidationDiscardsQueuedSnapshots()
    {
        foreach (string reason in new[] { "reset", "benchmark", "reverse", "stop", "dispose" })
        foreach (bool running in new[] { false, true })
        using (var f = new Fixture(300, true))
        {
            f.Reset("start"); f.Set("ready", true); f.Copy("obsolete");
            Action invalidate = delegate {
                f.Copy("waiting");
                if (reason == "reverse") f.Invoke("SuppressEcho", Guid.NewGuid());
                else if (reason == "benchmark") f.StartSyntheticBenchmark();
                else if (reason == "stop") f.Invoke("StopServerResources");
                else if (reason == "dispose") f.Dispose();
                else f.Reset("observation_reset");
            };
            if (running) f.DuringEncode = delegate { f.DuringEncode = null; invalidate(); };
            else invalidate();
            f.DrainPreparations(); Application.DoEvents();
            Check(f.Encoded.Count == (running ? 1 : 0), "Invalidation by " + reason + " drops pending snapshots before costly encoding.");
            Check(f.Get("current") == null, "Invalidation by " + reason + " prevents a stale encoder result from publishing.");
            if (reason == "stop" || reason == "dispose") continue;
            if (reason == "benchmark") { f.Set("speedTest", null); f.Reset("benchmark_finished"); }
            f.Copy("fresh"); f.DrainPreparations(); Application.DoEvents(); f.CheckCurrent("fresh");
        }
    }

    private static void PreparedResultsStayBoundedWhileStaWaits()
    {
        using (var f = new Fixture(400, true))
        {
            f.Reset("start"); f.Set("ready", true);
            for (int i = 0; i < 20; i++) { f.Copy("prepared-" + i); f.DrainPreparations(); }
            Check(f.Encoded.Count == 20 && f.Get("current") == null, "Prepared callbacks wait for the STA before changing the active transfer.");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int retained = 0; foreach (WeakReference buffer in f.EncodedBuffers) if (buffer.IsAlive) retained++;
            Check(retained == 1, "A stalled STA retains only the latest encoded buffer instead of one closure and buffer per copy.");
            Application.DoEvents(); f.CheckCurrent("prepared-19");
        }
        foreach (string reason in new[] { "reset", "reverse", "benchmark", "stop", "dispose" })
        using (var f = new Fixture(450, true))
        {
            f.Reset("start"); f.Set("ready", true); f.Copy("result waiting for STA"); f.DrainPreparations();
            if (reason == "reverse") f.Invoke("SuppressEcho", Guid.NewGuid());
            else if (reason == "benchmark") f.StartSyntheticBenchmark();
            else if (reason == "stop") f.Invoke("StopServerResources");
            else if (reason == "dispose") f.Dispose();
            else f.Reset("reset_before_STA");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Check(!f.EncodedBuffers[0].IsAlive, "Invalidation by " + reason + " promptly releases the queued encoded result before the STA callback runs.");
            Application.DoEvents(); Check(f.Get("current") == null, "Invalidated prepared results never publish after " + reason + ".");
            if (reason == "stop" || reason == "dispose") continue;
            if (reason == "benchmark") { f.Set("speedTest", null); f.Reset("benchmark_finished"); }
            f.Copy("fresh after result invalidation"); f.DrainPreparations(); Application.DoEvents(); f.CheckCurrent("fresh after result invalidation");
        }
    }

    private static void PreparationErrorsRecoverOnFreshCopy()
    {
        using (var f = new Fixture(500, true))
        {
            f.Reset("start"); f.Set("ready", true);
            Action<Action> schedule = f.Services.SchedulePreparation;
            f.Services.SchedulePreparation = delegate { throw new InvalidOperationException("synthetic scheduler error"); };
            f.Copy("dispatch failure");
            Check(f.Get("current") == null && ((Label)f.Get("StateLabel")).Text.Contains("synthetic scheduler error"),
                "A scheduler failure retains the existing clipboard error feedback and never publishes.");
            f.Services.SchedulePreparation = schedule; f.Copy("after dispatch failure"); f.DrainPreparations(); Application.DoEvents(); f.CheckCurrent("after dispatch failure");
            f.DuringEncode = delegate { f.DuringEncode = null; throw new InvalidOperationException("synthetic encoder error"); };
            f.Copy("encode failure"); f.DrainPreparations(); Application.DoEvents();
            Check(f.Get("current") == null && ((Label)f.Get("StateLabel")).Text.Contains("synthetic encoder error"),
                "A failed current encoder clears the old transfer and shows the existing preparation error feedback.");
            f.Copy("after encode failure"); f.DrainPreparations(); Application.DoEvents(); f.CheckCurrent("after encode failure");
            f.DuringEncode = delegate { f.DuringEncode = null; f.Copy("newer than failure"); throw new InvalidOperationException("obsolete failure"); };
            f.Copy("obsolete encoder"); f.DrainPreparations(); Application.DoEvents(); f.CheckCurrent("newer than failure");
        }
    }

    private static void StartupRequiresNewNotification()
    {
        using (var f = new Fixture(40))
        {
            f.Reset("transmission_started"); f.Set("ready", true);
            f.ScheduleAndCapture(); Check(f.Captures == 0, "Startup observes a sequence baseline without reading an existing copied file.");
            f.Notify(); f.Capture(); Check(f.Captures == 0, "A same-sequence startup notification never sends the old clipboard.");
            f.Sequence++; f.ScheduleAndCapture(); Check(f.Captures == 0, "A changed sequence without a fresh notification cannot arm capture.");
            foreach (string reason in new[] { "resumed", "benchmark_finished", "camera_changed", "settings_changed" })
            {
                f.Reset(reason); f.ScheduleAndCapture(); Check(f.Captures == 0, "Lifecycle/settings baselines never read existing clipboard contents.");
            }
            Check(f.Accepted == 0, "An ignored old clipboard never plays copy-accepted feedback.");
        }
    }

    private static void NotificationsCoalesceAndDeduplicate()
    {
        using (var f = new Fixture(100))
        {
            f.Reset("start"); f.Set("ready", true); f.Sequence = 101; f.Notify();
            int generation = (int)f.Get("generation"); f.Notify();
            Check((int)f.Get("generation") == generation, "Duplicate notifications do not restart the same copy's debounce or invalidate its generation.");
            f.Capture(); Check(f.Captures == 1 && !(bool)f.Get("clipboardCaptureRequested"), "A fresh notification permits exactly one capture, including an unsupported/empty snapshot.");
            f.Notify(); f.ScheduleAndCapture(); Check(f.Captures == 1, "A replayed sequence cannot re-read the same clipboard.");
            f.Sequence = 102; f.Notify(); f.Sequence = 103; f.Notify(); f.Capture();
            Check(f.Captures == 2 && f.LastCapturedSequence == 103, "Debounce coalesces multiple real copies to the newest notified sequence.");
            f.Capture(); Check(f.Captures == 2 && f.Accepted == 0, "Consumed or unsupported intent cannot silently restart a transfer or play a success sound.");
        }
    }

    private static void InactiveAndRestartedCopiesStayIgnored()
    {
        using (var f = new Fixture(10))
        {
            f.Reset("start"); f.Sequence = 11; f.Notify(); f.Set("ready", true); f.ScheduleAndCapture(); f.Notify(); f.Capture();
            Check(f.Captures == 0, "An inactive notification is consumed and is not replayed when the server becomes ready.");
            f.Sequence = 12; f.Notify(); f.Capture(); Check(f.Captures == 1, "A new copy after readiness is accepted normally.");
            f.Sequence = 13; f.Notify(); f.Set("ready", false); f.Reset("camera_changed"); f.Capture();
            Check(f.Captures == 1, "Changing camera cancels an armed copy before its contents are read.");
            f.Sequence = 14; f.Notify(); f.Set("ready", true); f.Reset("camera_restarted"); f.ScheduleAndCapture(); f.Notify(); f.Capture();
            Check(f.Captures == 1, "A restarted camera does not send a copy made while inactive.");
            f.Sequence = 15; f.Notify(); f.Capture(); Check(f.Captures == 2, "A fresh post-restart notification arms one capture.");
            f.Set("ready", false); f.Sequence = 16; f.Set("ready", true); f.Reset("camera_restarted"); f.Notify(); f.Capture();
            Check(f.Captures == 2, "Restart also baselines a copy whose notification was delayed until camera readiness.");
            f.Sequence = 17; f.Notify(); f.Capture(); Check(f.Captures == 3, "A later fresh copy remains eligible after an ignored delayed notification.");
        }
    }

    private static void GenuineNewCopyIsEncoded()
    {
        using (var f = new Fixture(20))
        {
            f.Reset("start"); f.Set("ready", true); f.NextSnapshot = new ClipboardSnapshot();
            byte[] expected = Encoding.UTF8.GetBytes("Synthetic newly copied text."); f.NextSnapshot.Formats[DataFormats.UnicodeText] = expected;
            f.Sequence = 21; f.Notify(); f.Capture();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (f.Get("current") == null && clock.ElapsedMilliseconds < 2500) { Application.DoEvents(); Thread.Sleep(5); }
            byte[] encoded = f.Get("current") as byte[];
            Check(f.Captures == 1 && f.Accepted == 1 && encoded != null && (Guid)f.Get("transferId") != Guid.Empty,
                "A genuine newly notified copy still captures, acknowledges and prepares a video transfer normally.");
            byte[] actual = ClipboardPayload.Decode(encoded).Formats[DataFormats.UnicodeText];
            Check(Convert.ToBase64String(actual) == Convert.ToBase64String(expected), "The new-copy gate preserves every encoded clipboard byte.");
        }
    }

    private static void BenchmarkCompletionRequiresNewCopy()
    {
        foreach (bool notifyDuringTest in new[] { false, true })
        using (var f = new Fixture(30))
        {
            f.Reset("start"); f.Set("ready", true);
            long now = 0; var run = new FileBenchmarkRunner(Guid.NewGuid(), new[] { VideoProfile.Dense }, 1024, 77, 0, delegate { return now; });
            f.Set("speedTest", run); f.Set("displayedBenchmark", run); f.Set("benchmarkTableFinalized", true);
            f.Set("benchmarkClipboardGeneration", f.Get("generation"));
            f.Sequence = 31; if (notifyDuringTest) f.Notify(); f.ScheduleAndCapture();
            Check(f.Captures == 0, "Benchmark mode never reads an existing or newly notified clipboard.");
            run.Cancel(); now = 4000; f.Invoke("RefreshBenchmark"); f.ScheduleAndCapture(); f.Notify(); f.Capture();
            Check(f.Get("speedTest") == null && f.Captures == 0, "The actual finished-benchmark tail path establishes a new baseline without replaying copies made during the test.");
            f.Sequence = 32; f.Notify(); f.Capture(); Check(f.Captures == 1, "Only a new copy after benchmark completion resumes clipboard transfer.");
        }
    }

    private static void FreshCopyAfterBenchmarkTailSurvivesRetirement()
    {
        using (var f = new Fixture(30))
        {
            f.Reset("benchmark_started"); f.Set("ready", true);
            long now = 0; var run = new FileBenchmarkRunner(Guid.NewGuid(), new[] { VideoProfile.Dense }, 1024, 77, 0, delegate { return now; });
            f.Set("speedTest", run); f.Set("displayedBenchmark", run); f.Set("benchmarkTableFinalized", true);
            f.Set("benchmarkClipboardGeneration", f.Get("generation"));
            f.Sequence = 31; f.Notify(); run.Cancel(); now = 4000;
            f.Sequence = 32; f.Notify(); f.Invoke("RefreshBenchmark"); f.Capture();
            Check(f.Get("speedTest") == null && f.Captures == 1 && f.LastCapturedSequence == 32,
                "A genuine new copy after the benchmark tail ends survives the later UI retirement tick.");
            f.Sequence = 33; f.Notify(); f.Invoke("RefreshBenchmark"); f.Capture();
            Check(f.Captures == 2 && f.LastCapturedSequence == 33,
                "A repeated finished/late-result UI refresh never cancels a newer pending local copy.");
        }
    }

    private static void ReversePublicationDoesNotEcho()
    {
        using (var f = new Fixture(50))
        {
            f.Reset("start"); f.Set("ready", true); f.Sequence = 51; f.Notify();
            f.Sequence = 52; f.Invoke("SuppressEcho", Guid.NewGuid()); f.Notify(); f.ScheduleAndCapture();
            Check(f.Captures == 0 && !(bool)f.Get("clipboardCaptureRequested"), "A reverse clipboard publication cancels pending forward intent and its own notification never echoes.");
            f.Sequence = 53; f.Notify(); f.Capture(); Check(f.Captures == 1, "A later genuine local copy is still accepted after reverse publication.");
        }
    }

    private static void SequenceZeroAndWrap()
    {
        using (var f = new Fixture(0))
        {
            f.Reset("start"); f.Set("ready", true); f.Notify(); f.Capture();
            Check(f.Captures == 0, "Initial sequence zero is an ordinary baseline, not a new copy.");
            f.Sequence = UInt32.MaxValue; f.Notify(); f.Capture();
            f.Sequence = 0; f.Notify(); f.Capture(); f.Notify(); f.Capture();
            Check(f.Captures == 2, "UInt32 sequence wrap to zero is accepted once and never mistaken for an uninitialized echo-suppression sentinel.");
        }
    }

    private static void SequenceRacesInvalidateOlderIntent()
    {
        using (var f = new Fixture(70))
        {
            f.Reset("start"); f.Set("ready", true); f.Sequence = 71; f.Notify();
            f.Sequence = 72; f.Capture(); Check(f.Captures == 0, "An unnotified sequence change invalidates capture of the older armed copy.");
            f.Notify(); f.DuringCapture = delegate { f.Sequence = 73; }; f.Capture();
            Check(f.Captures == 1 && f.Accepted == 0 && f.Get("current") == null, "A delayed clipboard renderer cannot publish or acknowledge content replaced during Capture.");
            f.DuringCapture = null; f.Notify(); f.Capture();
            Check(f.Captures == 2 && f.LastCapturedSequence == 73, "The later notification remains eligible after an older capture was invalidated.");
        }
    }

    private static void ProfileChangesReuseEncodedData()
    {
        using (var f = new Fixture(80))
        {
            f.Reset("start"); f.Set("ready", true); f.Set("selectedProfile", VideoProfile.Fast);
            f.Invoke("ApplySelectedTransferProfile"); f.ScheduleAndCapture();
            Check(f.Captures == 0 && f.Get("current") == null, "A profile change without a pending new copy cannot read or send the old clipboard.");
            var encoded = new byte[] { 1, 2, 3, 4 }; f.Set("current", encoded); f.Set("transferId", Guid.NewGuid());
            f.Set("pendingCopySequence", f.Sequence);
            f.Set("currentGeneration", f.Get("generation")); f.Set("currentProfile", VideoProfile.Fast); f.Set("selectedProfile", VideoProfile.Dense);
            f.Invoke("ApplySelectedTransferProfile"); f.ScheduleAndCapture();
            Check(Object.ReferenceEquals(f.Get("current"), encoded) && (VideoProfile)f.Get("currentProfile") == VideoProfile.Dense && f.Captures == 0,
                "Changing an active transfer profile reuses its already encoded bytes instead of re-reading Windows clipboard contents.");
        }
    }

    private static void PreviewStartsDisabled()
    {
        using (var f = new Fixture(1))
        {
            CheckBox option = (CheckBox)f.Form.Controls.Find("TransmissionPreviewEnabled", true)[0];
            Check(!option.Checked && !(bool)f.Get("PreviewEnabled"), "Every launch starts with the transmission preview disabled.");
            f.Form.SetTransmissionPreview(true); Check(option.Checked && (bool)f.Get("PreviewEnabled"), "Preview can be enabled explicitly without reading the clipboard.");
            f.Form.SetTransmissionPreview(false); Check(!option.Checked && f.Captures == 0, "Disabling preview never arms an old clipboard transfer.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly ServerForm Form;
        internal uint Sequence, LastCapturedSequence;
        internal int Captures, Accepted;
        internal Action DuringCapture;
        internal ClipboardSnapshot NextSnapshot;
        internal readonly Queue<Action> Preparations = new Queue<Action>();
        internal readonly List<string> Encoded = new List<string>();
        internal readonly List<WeakReference> EncodedBuffers = new List<WeakReference>();
        internal readonly ServerClipboardServices Services;
        internal Action DuringEncode;
        internal int ActiveEncoders, MaximumEncoders;
        private bool disposed;
        internal Fixture(uint sequence, bool controlledPreparation = false)
        {
            Sequence = sequence;
            var services = new ServerClipboardServices {
                Sequence = delegate { return Sequence; },
                Capture = delegate { Captures++; LastCapturedSequence = Sequence; if (DuringCapture != null) DuringCapture(); return NextSnapshot; },
                Accepted = delegate { Accepted++; }
            };
            if (controlledPreparation)
            {
                services.SchedulePreparation = delegate(Action action) { Preparations.Enqueue(action); };
                services.Encode = delegate(ClipboardSnapshot snapshot, out string description) {
                    ActiveEncoders++; MaximumEncoders = Math.Max(MaximumEncoders, ActiveEncoders);
                    try {
                        Encoded.Add(Encoding.UTF8.GetString(snapshot.Formats[DataFormats.UnicodeText]));
                        if (DuringEncode != null) DuringEncode();
                        byte[] result = ClipboardPayload.Encode(snapshot, out description);
                        EncodedBuffers.Add(new WeakReference(result)); return result;
                    } finally { ActiveEncoders--; }
                };
            }
            Services = services; Form = new ServerForm(null, false, null, services);
            IntPtr unused = Form.Handle;
            Set("bridgeSession", Guid.NewGuid());
        }
        internal void Reset(string reason) { Invoke("ResetClipboardObservation", reason); }
        internal void Notify() { Invoke("HandleClipboardUpdate"); }
        internal void Capture() { Invoke("CaptureClipboard"); }
        internal void ScheduleAndCapture() { Invoke("ScheduleClipboard"); Capture(); }
        internal void Copy(string text) {
            NextSnapshot = new ClipboardSnapshot(); NextSnapshot.Formats[DataFormats.UnicodeText] = Encoding.UTF8.GetBytes(text);
            Sequence++; Notify(); Capture();
        }
        internal void DrainPreparations() { while (Preparations.Count != 0) Preparations.Dequeue()(); }
        internal void StartSyntheticBenchmark() {
            Set("cameraLookupKnown", true); Set("cameraExists", true);
            Set("activeVideoMode", ((ComboBox)Get("cameraMode")).Items[0]); Invoke("StartBenchmark");
            Check(Get("speedTest") != null, "The synthetic speed test starts through its real clipboard-invalidation path.");
        }
        internal void CheckCurrent(string text) {
            byte[] encoded = Get("current") as byte[];
            Check(encoded != null && Encoding.UTF8.GetString(ClipboardPayload.Decode(encoded).Formats[DataFormats.UnicodeText]) == text,
                "Only the latest eligible clipboard bytes become the current transfer: " + text);
        }
        internal object Get(string name) { return Field(name).GetValue(Form); }
        internal void Set(string name, object value) { Field(name).SetValue(Form, value); }
        private FieldInfo Field(string name)
        {
            for (Type type = typeof(ServerForm); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new Exception("Missing production field: " + name);
        }
        internal void Invoke(string name, params object[] arguments)
        {
            MethodInfo method = typeof(ServerForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new Exception("Missing production method: " + name);
            try { method.Invoke(Form, arguments); } catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            ((System.Windows.Forms.Timer)Get("debounce")).Stop(); ((System.Windows.Forms.Timer)Get("benchmarkUiTimer")).Stop(); Form.Dispose();
        }
    }
    private static void Check(bool condition, string reason) { checks++; if (!condition) throw new Exception(reason); }
}
