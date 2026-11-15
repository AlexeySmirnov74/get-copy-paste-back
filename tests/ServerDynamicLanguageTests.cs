// Hidden production UI; language changes must never advance a transfer/test.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using HorizonClient;

internal static class ServerDynamicLanguageTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            BenchmarkSnapshotDoesNotTick(); WarningsProbeAndJournal();
            Console.WriteLine("Server dynamic language checks passed: " + checks + ". Hidden forms, synthetic clock/callbacks; no camera, audio, clipboard or user settings I/O.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { UiText.SetLanguage(UiLanguage.Russian); }
    }

    private static void BenchmarkSnapshotDoesNotTick()
    {
        using (var f = new Fixture())
        {
            Set(f.Form, "cameraLookupKnown", true); Set(f.Form, "cameraExists", true); Set(f.Form, "cameraRunning", true); Set(f.Form, "ready", true);
            Guid session = Guid.NewGuid(); Set(f.Form, "bridgeSession", session);
            var modes = (ComboBox)Get(f.Form, "cameraMode"); Set(f.Form, "activeVideoMode", modes.Items[2]);
            Call(f.Form, "StartBenchmark"); ((Timer)Get(f.Form, "benchmarkUiTimer")).Stop();
            long now = 0;
            var profiles = (VideoProfile[])Call(f.Form, "GetAvailableTransferProfiles");
            var run = new FileBenchmarkRunner(session, profiles, 10240, 9, 180000, delegate { return now; }, 1920, 1080);
            Set(f.Form, "speedTest", run); Set(f.Form, "displayedBenchmark", run);
            byte[] pending = { 1, 2, 3 }; Set(f.Form, "current", pending); Set(f.Form, "generation", 23);
            now = 40000; // Calling CurrentCommand/Tick here would fail preflight.
            f.Language(UiLanguage.English);
            Check(!run.Finished && run.CurrentIndex == 0 && run.Snapshot()[0].State == ProfileTestState.Preparing,
                "Language refresh renders only a snapshot even after the preflight timeout has elapsed.");
            Check(Object.ReferenceEquals(Get(f.Form, "current"), pending) && (int)Get(f.Form, "generation") == 23 && (Guid)Get(f.Form, "bridgeSession") == session,
                "Language changes retain the pending bytes, generation and bridge session.");
            var grid = (DataGridView)Get(f.Form, "benchmarkResults");
            Check((string)grid.Rows[0].Cells["Status"].Value == "Checking resolution", "Live table statuses change to English immediately.");
            string progress = ((Label)Get(f.Form, "benchmarkProgressLabel")).Text;
            Check(progress.Contains("Transfer mode") && progress.Contains("1920×1080") && progress.Contains("not being sent yet"), "Live progress retains its numeric context in English.");
            Check(f.Warnings.Count == 0 && !Directory.Exists(f.Root), "Language refresh cannot show a new preflight warning or write benchmark results.");
            f.Language(UiLanguage.Russian);
            Check((string)grid.Rows[0].Cells["Status"].Value == "Проверка разрешения" && !run.Finished, "Switching back restores Russian without advancing the test.");
            now = 1; run.Cancel(); f.Language(UiLanguage.English);
            Check((string)grid.Rows[0].Cells["Status"].Value == "Cancelled", "Finished result rows also translate without rerunning finalization.");
            var tray = (ContextMenuStrip)Get(f.Form, "serverTrayMenu");
            Check(tray.Items[0].Text == "Open" && tray.Items[2].Text == "Exit", "The existing tray menu changes language in place.");
            Check(!Directory.Exists(f.Root), "Rendering final rows cannot create an output directory.");
        }
    }

    private static void WarningsProbeAndJournal()
    {
        using (var f = new Fixture())
        {
            var modes = (ComboBox)Get(f.Form, "cameraMode"); f.Language(UiLanguage.English);
            Call(f.Form, "ShowFormatReconnectWarning", modes.Items[2], true);
            Check(f.Warnings.Count == 1 && f.Warnings[0].Contains("1920×1080") && f.Warnings[0].Contains("entire Horizon remote desktop"), "English format warnings preserve the exact dimensions and reconnect guidance.");
            Check(f.Warnings[0].Contains("Closing only our Client.exe"), "English guidance distinguishes the remote desktop from our tray client.");
            f.Language(UiLanguage.Russian);
            Check(((Label)Get(f.Form, "DetailLabel")).Text.Contains("Закрыть только наш"), "The current warning detail translates after its dialog callback has completed.");
            var probe = new AudioDiagnosticProbe { Id = Guid.NewGuid(), Modem = AudioModemKind.Aicodix, SentUtcTicks = DateTime.UtcNow.Ticks };
            Call(f.Form, "OnAudioProbeReceived", probe);
            Check(f.Probes.Count == 1 && f.Probes[0].Contains("Тест аудио прошёл успешно"), "A Russian audio probe callback uses Russian prose.");
            f.Language(UiLanguage.English);
            Check(((Label)Get(f.Form, "audioStatus")).Text.StartsWith("Audio test passed:"), "Existing audio probe status translates without receiving another probe.");
            Check(f.Probes.Count == 1, "Language changes cannot repeat an audio success notification.");
            using (var notice = new AudioProbeNotice())
            {
                notice.SetProbeCaption(AudioModemKind.Aicodix); UiText.Apply(notice);
                Check(!notice.Visible && notice.Text == "Audio test" && notice.Controls[0].Text.Contains("client's test signal"), "The hidden notice title and body support English without displaying a window.");
                UiText.SetLanguage(UiLanguage.Russian); UiText.Apply(notice);
                Check(!notice.Visible && notice.Text == "Проверка аудио" && notice.Controls[0].Text.Contains("Проверочный сигнал"), "An existing notice translates back without restarting expiry or showing it.");
            }
            ((CheckBox)Get(f.Form, "transferLogEnabled")).Checked = true;
            Guid first = Guid.NewGuid(); Call(f.Form, "BeginForwardJournal", first, 1536);
            var journal = (TextBoxBase)Get(f.Form, "transferLogText"); string previous = journal.Text;
            f.Language(UiLanguage.English); Call(f.Form, "CompleteForwardJournal", first, 1536);
            Check(journal.Text.StartsWith(previous) && journal.Text.Substring(previous.Length).Contains("Sent 1.5 KB to the remote desktop"), "History retains its original language; new journal events use the current language and culture.");
            Call(f.Form, "OnIncomingTransferCompleted", Guid.NewGuid(), 1560, 1250L);
            Check(f.Delivered == 1 && journal.Text.Contains("Received 1.5 KB") && journal.Text.Contains("1.3s"), "Translated reverse-delivery journaling retains the once-only notification callback.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly ServerForm Form;
        internal readonly string Root = Path.GetFullPath(Path.Combine("build", "server-language-dynamic-" + Guid.NewGuid().ToString("N")));
        internal readonly List<string> Warnings = new List<string>(), Probes = new List<string>();
        internal int Delivered;
        internal Fixture()
        {
            UiText.SetLanguage(UiLanguage.Russian);
            var clipboard = new ServerClipboardServices { Sequence = delegate { return 1; }, Capture = delegate { throw new Exception("No clipboard capture."); }, Accepted = delegate { throw new Exception("No sound."); } };
            Form = new ServerForm(null, false, delegate { throw new Exception("No camera."); }, clipboard,
                delegate(string title, string text) { Warnings.Add(text); }, delegate { Delivered++; }, Probes.Add);
            typeof(ServerRuntime).GetProperty("RootDirectory").GetSetMethod(true).Invoke(Get(Form, "runtime"), new object[] { Root });
            IntPtr ignored = Form.Handle; Check(!Form.Visible, "The production form remains hidden.");
        }
        internal void Language(UiLanguage language) { UiText.SetLanguage(language); UiText.Apply(Form); Call(Form, "RefreshLanguagePresentation"); }
        public void Dispose()
        {
            ((Timer)Get(Form, "benchmarkUiTimer")).Stop(); Form.Dispose();
            if (Directory.Exists(Root) && Root.StartsWith(Path.GetFullPath("build") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(Root, true);
        }
    }
    private static object Get(object value, string name) { return value.GetType().GetField(name, Flags).GetValue(value); }
    private static void Set(object value, string name, object data) { value.GetType().GetField(name, Flags).SetValue(value, data); }
    private static object Call(object value, string name, params object[] arguments) { return value.GetType().GetMethod(name, Flags).Invoke(value, arguments); }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
