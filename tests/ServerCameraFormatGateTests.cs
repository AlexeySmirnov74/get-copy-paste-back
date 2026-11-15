// Hidden production-form integration; no devices, clipboard contents or real dialogs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using HorizonClient;

internal static class ServerCameraFormatGateTests
{
    private static int checks;
    [STAThread]
    private static int Main()
    {
        UiText.SetLanguage(UiLanguage.Russian); // Reconnect guidance assertions intentionally use Russian.
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            ActualSourceMismatchCancelsWholeRun(); MatchingReadyAndMidstreamMismatch(); UnknownAndTimeout(); ApplyAlwaysWarns(); AllModesFailedAdvice();
            Console.WriteLine("Server camera format gate integration passed: " + checks + " checks; hidden forms, synthetic reports/clock, no device/clipboard/dialog actions."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void ActualSourceMismatchCancelsWholeRun()
    {
        using (var f = new Fixture())
        {
            f.Modes.SelectedIndex = 1; f.Call("StartBenchmark"); FileBenchmarkRunner run = f.Run;
            Check(run.ExpectedWidth == 1920 && run.ExpectedHeight == 1080, "Preflight uses the active source, not the unapplied720p selection.");
            Check(run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare && f.Get("benchmarkDataFrames").Equals(0L), "No test data precedes Ready.");
            FileBenchmarkReport foreign = Ready(run, 640, 480); foreign.Test = Guid.NewGuid(); f.Report(foreign);
            Check(!run.Finished && f.Warnings.Count == 0, "An obsolete test cannot cancel this preflight.");
            f.OnWarning = delegate { f.Call("RefreshBenchmark"); }; // Emulate a modal dialog's nested UI loop.
            f.Report(Ready(run, 1280, 720));
            Check(run.Finished && run.FormatCheckFailureReason == "capture_format_mismatch", "A fresh mismatched Ready cancels the entire run.");
            foreach (ProfileTestResult row in run.Snapshot()) Check(row.State == ProfileTestState.Cancelled, "No profile starts after a format mismatch.");
            Check(f.Warnings.Count == 1 && f.Last.Contains("1920×1080") && f.Last.Contains("1280×720"), "One warning names both actual sizes.");
            CheckReconnect(f.Last); f.Report(Ready(run, 1280, 720)); f.Call("RefreshBenchmark");
            Check(f.Warnings.Count == 1, "Repeated reports, refresh and reentrant callbacks never duplicate the warning.");
            Check(Object.ReferenceEquals(f.Run, run) && run.CurrentCommand.Phase == FileBenchmarkPhase.Cancel, "The producer retains its Cancel tail.");
            FileBenchmarkReport late = Ready(run, 1920, 1080); late.Status = FileBenchmarkStatus.Completed; late.ReceivedBytes = late.TotalBytes; late.ElapsedMilliseconds = 2000;
            f.Report(late); Check(run.Snapshot()[0].State == ProfileTestState.Cancelled, "A late valid completion cannot revive the rejected run.");
            Check(File.Exists(Path.Combine(f.Root, "data", "last-benchmark.csv")), "Final results are written only to the isolated fixture root.");
        }
    }

    private static void MatchingReadyAndMidstreamMismatch()
    {
        using (var f = new Fixture())
        {
            f.Call("StartBenchmark"); FileBenchmarkRunner run = f.Run;
            f.Report(Ready(run, 0, 0)); Check(run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare && f.Warnings.Count == 0, "Unknown geometry cannot start or claim a known mismatch.");
            Check(run.CurrentCommand.Profile == VideoProfile.Fast && VisualProtocol.GetHeight(run.CurrentCommand.Profile) == 720, "The first logical test profile is720p.");
            f.Report(Ready(run, 1920, 1080));
            Check(run.CurrentCommand.Phase == FileBenchmarkPhase.Start && !run.Finished, "Matching1080 capture permits a720p logical test profile.");
            FileBenchmarkReport changed = Ready(run, 1280, 720); changed.Status = FileBenchmarkStatus.Progress; changed.ReceivedBytes = 1; changed.ElapsedMilliseconds = 100;
            f.Report(changed); Check(run.Finished && f.Warnings.Count == 1, "An actual midstream format change also cancels and warns once.");
        }
    }

    private static void UnknownAndTimeout()
    {
        using (var f = new Fixture())
        {
            f.Set("activeVideoMode", null); f.Call("StartBenchmark");
            Check(f.Run == null && f.Warnings.Count == 1 && f.Last.Contains("формат камеры"), "Unknown server geometry warns before creating any run.");
        }
        using (var f = new Fixture())
        {
            f.Call("StartBenchmark"); long now = 0;
            var available = (VideoProfile[])typeof(ServerForm).GetMethod("GetAvailableTransferProfiles", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(f.Form, null);
            Check(available.Length == 5, "Default Full HD tests the original five modes until color is enabled.");
            var run = new FileBenchmarkRunner((Guid)f.Get("bridgeSession"), available, f.Form.SelectedBenchmarkBytes, 1, 180000, delegate { return now; }, 1920, 1080);
            f.Set("speedTest", run); f.Set("displayedBenchmark", run);
            now = 1500; f.Call("RefreshBenchmark"); Check(run.CurrentCommand.Phase == FileBenchmarkPhase.Prepare, "Strict production preflight disables optimistic1.5s start.");
            f.Report(Ready(run, 0, 0)); now = 34999; f.Call("RefreshBenchmark"); Check(!run.Finished && f.Warnings.Count == 0, "Unknown Ready continues waiting within the35s bound.");
            now = 35000; f.Call("RefreshBenchmark");
            Check(run.Finished && run.FormatCheckFailureReason == "capture_format_unconfirmed" && f.Warnings.Count == 1 && f.Last.Contains("35 секунд"), "No fresh audio Ready ends the whole run at35s.");
            CheckReconnect(f.Last); Check(f.Last.Contains("Client.exe") && f.Last.Contains("аудиовыход"), "The no-reply warning explains client/audio setup.");
            Check(f.Last.Contains("на сервере") && f.Last.Contains("на клиенте"), "A completely unconfirmed speed test advises checking selected audio outputs on both ends.");
            now = 37999; f.Call("RefreshBenchmark"); Check(Object.ReferenceEquals(f.Run, run), "Cancel remains available for almost3s.");
            now = 38000; f.Call("RefreshBenchmark"); Check(f.Run == null && f.Warnings.Count == 1, "The tail retires at3s without repeating the warning.");
        }
    }

    private static void ApplyAlwaysWarns()
    {
        using (var f = new Fixture())
        {
            byte[] pending = { 1, 2, 3 }; Guid session = (Guid)f.Get("bridgeSession");
            f.Set("current", pending); f.Set("generation", 17); f.Call("ApplyVideoMode");
            Check(f.Warnings.Count == 1 && f.Last.Contains("уже установлен") && f.Last.Contains("1920×1080"), "Apply warns even when the source format is already active.");
            CheckReconnect(f.Last);
            Check(Object.ReferenceEquals(f.Get("current"), pending) && (int)f.Get("generation") == 17 && (Guid)f.Get("bridgeSession") == session && (bool)f.Get("ready"), "An unchanged Apply preserves the active clipboard/session/generation.");
            f.Call("ApplyVideoMode"); Check(f.Warnings.Count == 2, "Each explicit Apply click explains the required reconnect.");
            f.Call("ShowFormatReconnectWarning", f.Modes.Items[2], false);
            Check(f.Warnings.Count == 3 && f.Last.Contains("применён"), "The successful-change notification also explains reconnect."); CheckReconnect(f.Last);
        }
    }

    private static void AllModesFailedAdvice()
    {
        foreach (UiLanguage language in new[] { UiLanguage.Russian, UiLanguage.English })
        {
            UiText.SetLanguage(language);
            using (var f = new Fixture())
            {
                long now = 0;
                var run = new FileBenchmarkRunner((Guid)f.Get("bridgeSession"), new[] { VideoProfile.Fast }, 10240, 1, 180000, delegate { return now; }, 1920, 1080);
                f.Set("speedTest", run); f.Set("displayedBenchmark", run);
                f.Report(Ready(run, 1920, 1080));
                FileBenchmarkReport failed = Ready(run, 1920, 1080); failed.Status = FileBenchmarkStatus.TimedOut; failed.Failure = FileBenchmarkFailure.NoData; failed.ElapsedMilliseconds = 16000;
                now = 16000; f.Report(failed);
                Check(run.Finished && run.Snapshot()[0].State == ProfileTestState.NoData, "The synthetic receiver reports complete absence of test data.");
                string text = f.Form.Controls.Find("BenchmarkProgress", true)[0].Text;
                Check(language == UiLanguage.English ? text.Contains("audio output") && text.Contains("server") && text.Contains("client") : text.Contains("аудиовыход") && text.Contains("на сервере") && text.Contains("на клиенте"), "Entirely failed mode tests explain checking both audio selections in either language.");
            }
        }
        UiText.SetLanguage(UiLanguage.Russian);
    }

    private static FileBenchmarkReport Ready(FileBenchmarkRunner run, int width, int height)
    {
        FileBenchmarkCommand command = run.CurrentCommand;
        return new FileBenchmarkReport { Session = command.Session, Test = command.Test, Profile = command.Profile, TotalBytes = command.FileSize, Status = FileBenchmarkStatus.Ready, FrameWidth = width, FrameHeight = height };
    }
    private static void CheckReconnect(string text)
    { Check(text.Contains("закройте окно удалённого рабочего стола Horizon") && text.Contains("подключитесь заново") && text.Contains("Закрыть только наш Client.exe в трее недостаточно"), "Guidance requires reconnecting the whole Horizon desktop, not just our tray application."); }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        internal readonly ServerForm Form;
        internal readonly string Root = Path.GetFullPath(Path.Combine("build", "camera-format-fixture-" + Guid.NewGuid().ToString("N")));
        internal readonly List<string> Warnings = new List<string>();
        internal Action OnWarning;
        internal string Last { get { return Warnings[Warnings.Count - 1]; } }
        internal ComboBox Modes { get { return (ComboBox)Get("cameraMode"); } }
        internal FileBenchmarkRunner Run { get { return (FileBenchmarkRunner)Get("speedTest"); } }
        internal Fixture()
        {
            var services = new ServerClipboardServices { Sequence = delegate { return 77; }, Capture = delegate { throw new Exception("Unexpected real clipboard capture."); }, Accepted = delegate { throw new Exception("Unexpected sound."); } };
            Form = new ServerForm(null, false, delegate { throw new Exception("Unexpected camera open."); }, services, delegate(string title, string text) { Check(title.Contains("Get Copy-Paste Back — Server"), "Warnings identify their application."); Warnings.Add(text); if (OnWarning != null) OnWarning(); });
            typeof(ServerRuntime).GetProperty("RootDirectory").GetSetMethod(true).Invoke(Get("runtime"), new object[] { Root });
            IntPtr ignored = Form.Handle; Set("ready", true); Set("cameraRunning", true); Set("cameraExists", true); Set("cameraLookupKnown", true); Set("bridgeSession", Guid.NewGuid()); Set("activeVideoMode", Modes.Items[2]);
            Check(!Form.Visible, "The production form remains hidden.");
        }
        internal object Get(string name) { return typeof(ServerForm).GetField(name, Flags).GetValue(Form); }
        internal void Set(string name, object value) { typeof(ServerForm).GetField(name, Flags).SetValue(Form, value); }
        internal void Call(string method, params object[] args) { typeof(ServerForm).GetMethod(method, Flags).Invoke(Form, args); }
        internal void Report(FileBenchmarkReport report) { Call("OnFileBenchmarkReport", report); }
        public void Dispose()
        {
            ((Timer)Get("benchmarkUiTimer")).Stop(); Form.Dispose(); Check(!Form.Visible, "The fixture never showed a window.");
            if (Directory.Exists(Root) && Root.StartsWith(Path.GetFullPath("build") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(Root, true);
        }
    }
}
