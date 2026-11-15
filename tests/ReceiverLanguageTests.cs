using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

internal static class ReceiverLanguageTests
{
    private static int checks;

    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "receiver-language-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Directory.CreateDirectory(root);
            EnglishDefaultAndSavedRussianRoundTrip(root);
            VisibleSubmenuDefersCaptions(root);
            PersistenceFailureIsSilent(root);
            Check(Application.OpenForms.Count == 0, "Language switching never creates a form");
            Console.WriteLine("Receiver language checks passed ({0} assertions; isolated settings and fake camera/audio services only).", checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            UiText.SetLanguage(UiLanguage.Russian);
            string resolved = Path.GetFullPath(root), parent = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (String.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("receiver-language-test-", StringComparison.Ordinal) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static void EnglishDefaultAndSavedRussianRoundTrip(string root)
    {
        UiText.Load(Path.Combine(root, "missing-language-setting"));
        Check(UiText.Language == UiLanguage.English, "An absent language setting starts the client in English");
        using (var freshFixture = new Fixture(Path.Combine(root, "fresh-default")))
        using (ReceiverContext fresh = freshFixture.Create())
        {
            Ready(fresh, freshFixture); CheckMenu(fresh, true);
            Check(!File.Exists(freshFixture.Paths.LanguageSetting), "English default does not persist a preference until the user selects one");
        }
        string russianPreference = Path.Combine(root, "saved-russian"); File.WriteAllText(russianPreference, "ru"); UiText.Load(russianPreference);
        using (var fixture = new Fixture(Path.Combine(root, "round-trip")))
        using (ReceiverContext receiver = fixture.Create())
        {
            Ready(receiver, fixture);
            CheckMenu(receiver, false);
            ToolStripItem camera = receiver.Menu.Items[0];
            var output = (ToolStripMenuItem)receiver.Menu.Items["audio.output"];
            ToolStripItem namedExit = output.DropDownItems[0];
            ToolStripItem settings = receiver.Menu.Items["settings"];
            string baseline = fixture.Snapshot();
            Language(receiver, "en").PerformClick();
            PumpUntil(delegate { return receiver.Menu.Items["settings"].Text == "Settings"; }, "Closed menu updates immediately after switching to English");
            CheckMenu(receiver, true);
            Check(File.ReadAllText(fixture.Paths.LanguageSetting) == "en", "The language menu persists English in the configured settings store");
            Check(Object.ReferenceEquals(camera, receiver.Menu.Items[0]) && Object.ReferenceEquals(settings, receiver.Menu.Items["settings"]) && Object.ReferenceEquals(namedExit, ((ToolStripMenuItem)receiver.Menu.Items["audio.output"]).DropDownItems[0]), "A language change edits existing captions without rebuilding device/menu items");
            Check(camera.Text == "Настройки" && namedExit.Text == "Выход", "Actual device names which match translated interface captions remain unchanged");
            Check(fixture.Snapshot() == baseline && receiver.SelectedCameraId == "camera:selected", "Switching language does not enumerate, reopen/dispose capture, set/test audio, select a modem or rewrite device choices");
            Language(receiver, "ru").PerformClick();
            PumpUntil(delegate { return receiver.Menu.Items["settings"].Text == "Настройки"; }, "English can be switched back to Russian");
            CheckMenu(receiver, false);
            Check(File.ReadAllText(fixture.Paths.LanguageSetting) == "ru" && fixture.Snapshot() == baseline, "Returning to Russian changes only captions and the language setting");
            Language(receiver, "en").PerformClick();
            PumpUntil(delegate { return receiver.Menu.Items["application.exit"].Text == "Exit"; }, "English selection is committed before the next process restores it");
            receiver.Dispose();
            UiText.SetLanguage(UiLanguage.Russian); UiText.Load(fixture.Paths.LanguageSetting);
            using (ReceiverContext restored = fixture.Create())
            {
                Ready(restored, fixture);
                CheckMenu(restored, true);
                Check(UiText.Language == UiLanguage.English, "A saved language is restored before creating a new receiver menu");
            }
        }
    }

    private static void VisibleSubmenuDefersCaptions(string root)
    {
        UiText.SetLanguage(UiLanguage.Russian);
        using (var fixture = new Fixture(Path.Combine(root, "visible-submenu")))
        using (ReceiverContext receiver = fixture.Create())
        {
            Ready(receiver, fixture);
            var settings = (ToolStripMenuItem)receiver.Menu.Items["settings"];
            var languages = (ToolStripMenuItem)settings.DropDownItems["settings.language"];
            ToolStripItem english = languages.DropDownItems["settings.language.en"];
            string baseline = fixture.Snapshot();
            receiver.Menu.Show(new Point(12, 12)); settings.ShowDropDown(); languages.ShowDropDown(); Application.DoEvents();
            Check(receiver.Menu.Visible && languages.DropDown.Visible, "Fixture has a visible nested language submenu");
            receiver.SelectLanguage(UiLanguage.English);
            PumpFor(80);
            Check(UiText.Language == UiLanguage.English && File.ReadAllText(fixture.Paths.LanguageSetting) == "en", "Language and persistence change without waiting for the open menu to close");
            Check(receiver.Menu.Visible && languages.DropDown.Visible && !english.IsDisposed && settings.Text == "Настройки", "A visible submenu is neither rebuilt nor dismissed by asynchronous translation");
            receiver.Menu.Close();
            PumpUntil(delegate { return settings.Text == "Settings"; }, "Closing the root menu applies the deferred captions");
            CheckMenu(receiver, true);
            Check(fixture.Snapshot() == baseline, "Deferred language updates perform no camera/audio I/O or device enumeration");
        }
    }

    private static void PersistenceFailureIsSilent(string root)
    {
        UiText.SetLanguage(UiLanguage.Russian);
        using (var fixture = new Fixture(Path.Combine(root, "blocked-setting")))
        using (ReceiverContext receiver = fixture.Create())
        {
            Ready(receiver, fixture);
            Directory.CreateDirectory(fixture.Paths.LanguageSetting);
            string baseline = fixture.Snapshot();
            Language(receiver, "en").PerformClick();
            PumpUntil(delegate { return receiver.Menu.Items["settings"].Text == "Settings"; }, "An unavailable language setting does not prevent live translation");
            Check(fixture.Failures == 1 && Directory.Exists(fixture.Paths.LanguageSetting), "A failed save is logged quietly without replacing the blocked path");
            Check(fixture.Snapshot() == baseline && Directory.GetFiles(fixture.Paths.SettingsDirectory, "*.tmp").Length == 0, "Failed persistence does not disturb devices or leave an atomic-write temporary");
        }
    }

    private static ToolStripMenuItem Language(ReceiverContext receiver, string id)
    {
        var settings = (ToolStripMenuItem)receiver.Menu.Items["settings"];
        var languages = (ToolStripMenuItem)settings.DropDownItems["settings.language"];
        return (ToolStripMenuItem)languages.DropDownItems["settings.language." + id];
    }

    private static void CheckMenu(ReceiverContext receiver, bool english)
    {
        Check(receiver.Menu.Items["settings"].Text == (english ? "Settings" : "Настройки"), "Settings caption follows the selected language");
        Check(receiver.Menu.Items["application.exit"].Text == (english ? "Exit" : "Выход"), "The previously escaped Exit caption is translated");
        Check(receiver.Menu.Items["audio.modem"].Text == (english ? "Audio modem" : "Аудиомодем") && receiver.Menu.Items["audio.test"].Text == (english ? "Test audio" : "Проверить звук"), "Audio modem and test captions are translated");
        Check(receiver.Menu.Items["audio.output"].Text == (english ? "Audio output" : "Аудиовыход"), "The output selector caption is translated");
        var modes = (ToolStripMenuItem)receiver.Menu.Items["audio.modem"];
        Check(modes.DropDownItems["audio.modem.fsk"].Text == (english ? "Compatible FSK" : "Совместимый FSK") && modes.DropDownItems["audio.modem.aicodix"].Text == "aicodix (OFDM)", "Software codec captions translate without changing codec identifiers");
        var resolutions = (ToolStripMenuItem)receiver.Menu.Items["video.resolution"];
        Check(resolutions.Text == (english ? "Camera resolution" : "Разрешение камеры") && resolutions.DropDownItems["video.resolution.auto"].Text == (english ? "Auto (server format)" : "Авто (формат сервера)") && receiver.Menu.Items["video.refresh"].Text == (english ? "Refresh camera modes" : "Обновить режимы камеры"), "Camera menu captions follow the language without changing its selected mode");
        var settings = (ToolStripMenuItem)receiver.Menu.Items["settings"];
        Check(settings.DropDownItems["settings.debug_log"].Text == (english ? "Detailed system log" : "Подробный системный лог"), "The detailed-log option is translated");
        Check(((ToolStripMenuItem)settings.DropDownItems["settings.language"]).Text == "Язык / Language" && Language(receiver, "en").Checked == english && Language(receiver, "ru").Checked != english, "The bilingual language selector checks exactly the active language");
        var version = (ToolStripMenuItem)receiver.Menu.Items["application.version"];
        Check(!version.Enabled && version.Text == "Get Copy-Paste Back — Client " + typeof(ReceiverContext).Assembly.GetName().Version.ToString(), "The disabled product, client role and build version remain correct on initial build and language refresh");
    }

    private static void Ready(ReceiverContext receiver, Fixture fixture)
    {
        bool refreshed = false; receiver.RefreshDevices(delegate { refreshed = true; });
        PumpUntil(delegate { return refreshed && fixture.Opens > 0 && fixture.OutputSets > 0; }, "Fake receiver restores its selected camera/audio output");
        PumpFor(80);
    }
    private static void PumpUntil(Func<bool> condition, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(1); }
        Check(condition(), message);
    }
    private static void PumpFor(int milliseconds)
    { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(1); } }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }

    private sealed class Fixture : IDisposable
    {
        internal readonly ClientPaths Paths;
        private readonly ReceiverServices services;
        internal int Enumerations, OutputEnumerations, Opens, Starts, Disposals, OutputSets, Probes, ModemPrepares, ModemApplies, CameraWrites, AudioWrites, Failures;
        internal Fixture(string root)
        {
            Paths = new ClientPaths(Path.Combine(root, "executable")); Paths.ConfigureSettingsStore(Path.Combine(root, "local-profile"), null);
            services = new ReceiverServices(Paths) {
                ListDevices = delegate { Interlocked.Increment(ref Enumerations); return new List<CameraDevice> { new CameraDevice("Настройки", "camera:selected") }; },
                ListAudioOutputs = delegate { Interlocked.Increment(ref OutputEnumerations); return new[] { new AudioEndpoint("waveout:3", "Выход") }; },
                OpenCapture = delegate { Interlocked.Increment(ref Opens); return new FakeCapture(this); },
                ReadSelection = delegate { return "camera:selected"; }, WriteSelection = delegate { Interlocked.Increment(ref CameraWrites); },
                ReadAudioSelection = delegate { return new[] { "waveout:3", "Выход" }; }, WriteAudioSelection = delegate { Interlocked.Increment(ref AudioWrites); },
                SetAudioOutput = delegate { Interlocked.Increment(ref OutputSets); }, TestAudioOutput = delegate { Interlocked.Increment(ref Probes); return Guid.NewGuid(); },
                PrepareAudioModem = delegate { Interlocked.Increment(ref ModemPrepares); }, ApplyAudioModem = delegate { Interlocked.Increment(ref ModemApplies); },
                LogFailure = delegate { Interlocked.Increment(ref Failures); },
                ShowTrayIcon = false, EnableDeferredPaste = false, EnableRetryTimer = false, EnableSessionEvents = false, RestoreSelection = true
            };
        }
        internal ReceiverContext Create() { return new ReceiverContext(services); }
        internal string Snapshot()
        { return String.Join("/", new[] { Enumerations, OutputEnumerations, Opens, Starts, Disposals, OutputSets, Probes, ModemPrepares, ModemApplies, CameraWrites, AudioWrites }); }
        public void Dispose() { }
        private sealed class FakeCapture : IReceiverCapture
        {
            private readonly Fixture fixture;
            internal FakeCapture(Fixture fixture) { this.fixture = fixture; }
            public event Action<Exception> OnError { add { } remove { } }
            public void Start() { Interlocked.Increment(ref fixture.Starts); }
            public void Dispose() { Interlocked.Increment(ref fixture.Disposals); }
        }
    }
}
