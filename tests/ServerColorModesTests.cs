using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using HorizonClient;

internal static class ServerColorModesTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            CatalogAndLanguages(); MigrationAndPersistence(); PendingTransferAndBenchmark();
            Console.WriteLine("Server color-mode checks passed: {0}; hidden forms, isolated settings, fake clipboard sequence and clocks only.", checks); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void CatalogAndLanguages()
    {
        using (var f = new Fixture(false))
        {
            Check(!f.Toggle.Checked, "Ordinary startup keeps the original five modes."); Catalog(f, 5);
            int sourceModes = f.Form.Controls.Find("CameraMode", true)[0] is ComboBox ? ((ComboBox)f.Form.Controls.Find("CameraMode", true)[0]).Items.Count : -1;
            f.Toggle.Checked = true; Catalog(f, 9);
            foreach (VideoProfile profile in new[] { VideoProfile.Color720, VideoProfile.Color720Dense, VideoProfile.Color1080, VideoProfile.Color1080Dense })
            {
                typeof(ServerForm).GetProperty("SelectedProfileChoice", Flags).SetValue(f.Form, profile, null);
                Check((VideoProfile)f.Get("selectedProfile") == profile, "All color modes can be selected after enabling.");
            }
            f.Toggle.Checked = false; Catalog(f, 5);
            Check((VideoProfile)f.Get("selectedProfile") == VideoProfile.HighDefinitionRobust, "Disabling a selected color mode returns to the existing robust default.");
            Check(sourceModes == 3 && ((ComboBox)f.Form.Controls.Find("CameraMode", true)[0]).Items.Count == sourceModes, "The switch changes data modes without changing camera formats.");
            f.Toggle.Checked = true; typeof(ServerForm).GetProperty("SelectedProfileChoice", Flags).SetValue(f.Form, VideoProfile.Color720Dense, null);
            UiText.SetLanguage(UiLanguage.English); f.Call("ApplyServerLanguage");
            Check(f.Toggle.Text == "Four-color modes" && f.Toggle.Checked && (VideoProfile)f.Get("selectedProfile") == VideoProfile.Color720Dense, "English caption preserves the enabled color preference and selected mode.");
            UiText.SetLanguage(UiLanguage.Russian); f.Call("ApplyServerLanguage");
            Check(f.Toggle.Text == "Цветные режимы (4 цвета)" && f.Toggle.Checked && (VideoProfile)f.Get("selectedProfile") == VideoProfile.Color720Dense, "Russian caption preserves the same enabled preference and mode.");
        }
    }
    private static void MigrationAndPersistence()
    {
        foreach (string flag in new[] { "", " colorModesEnabled='false'", " colorModesEnabled='true'", " colorModesEnabled='invalid'" })
        using (var f = new Fixture(true))
        {
            Directory.CreateDirectory(f.Paths.DataDirectory);
            File.WriteAllText(f.Paths.SettingsFile, "<HorizonServer videoProfile='13'" + flag + " />");
            f.Load(); bool enabled = flag.Length == 0 || flag.Contains("'true'");
            Check(f.Toggle.Checked == enabled, "Legacy experiment selection migrates, while an explicit disabled/invalid flag wins."); Catalog(f, enabled ? 9 : 5);
            Check((VideoProfile)f.Get("selectedProfile") == (enabled ? VideoProfile.Color720Dense : VideoProfile.HighDefinitionRobust), "Settings normalize profile only when color modes are disabled.");
        }
        using (var f = new Fixture(true))
        {
            f.Toggle.Checked = true;
            var settings = new XmlDocument(); settings.Load(f.Paths.SettingsFile);
            Check(settings.DocumentElement.GetAttribute("colorModesEnabled") == "True", "The user's change persists immediately in portable server settings.");
            using (var restored = f.Create())
            {
                f.Load(restored);
                Check(((CheckBox)restored.Controls.Find("ColorModesEnabled", true)[0]).Checked, "A new server instance restores the saved color-mode choice.");
            }
        }
    }
    private static void PendingTransferAndBenchmark()
    {
        using (var f = new Fixture(false))
        {
            f.Toggle.Checked = true; typeof(ServerForm).GetProperty("SelectedProfileChoice", Flags).SetValue(f.Form, VideoProfile.Color720Dense, null);
            byte[] data = { 1, 3, 5 }; Guid before = Guid.NewGuid();
            f.Set("ready", true); f.Set("current", data); f.Set("currentProfile", VideoProfile.Color720Dense); f.Set("generation", 8); f.Set("currentGeneration", 8);
            f.Set("pendingCopySequence", (uint)42); f.Set("transferId", before); f.Set("bridgeSession", Guid.NewGuid());
            f.Toggle.Checked = false;
            Check((VideoProfile)f.Get("currentProfile") == VideoProfile.HighDefinitionRobust && (Guid)f.Get("transferId") != before, "Disabling color during a pending copy repacketizes under a new transfer identity.");
            Check(Object.ReferenceEquals(f.Get("current"), data) && (int)f.Get("generation") == 8, "A mode change preserves current data and its generation.");
            long now = 0; var run = new FileBenchmarkRunner(Guid.NewGuid(), new[] { VideoProfile.Fast }, 10240, 1, 180000, delegate { return now; });
            f.Set("speedTest", run); f.Call("UpdateBenchmarkAvailability", true);
            Check(!f.Toggle.Enabled, "A running speed test locks its mode catalog.");
            f.Toggle.Checked = true;
            Check(!f.Toggle.Checked && CatalogSize(f) == 5, "A programmatic mid-test toggle cannot mutate an active test's catalog.");
            f.Set("speedTest", null);
        }
    }
    private static int CatalogSize(Fixture f) { return ((VideoProfile[])typeof(ServerForm).GetMethod("GetAvailableTransferProfiles", Flags).Invoke(f.Form, null)).Length; }
    private static void Catalog(Fixture f, int count)
    {
        Check(CatalogSize(f) == count && ((ComboBox)f.Form.Controls.Find("VideoProfile", true)[0]).Items.Count == count, "Dropdown and benchmark catalog agree.");
    }
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private sealed class Fixture : IDisposable
    {
        internal readonly ServerForm Form;
        internal readonly ServerPaths Paths;
        private readonly object previous;
        private readonly FieldInfo current = typeof(ServerPaths).GetField("Current", BindingFlags.Static | BindingFlags.NonPublic);
        private readonly bool persisted;
        internal Fixture(bool persist)
        {
            persisted = persist; Paths = new ServerPaths(Path.GetFullPath(Path.Combine("build", "color-settings-" + Guid.NewGuid().ToString("N"))));
            previous = current.GetValue(null); current.SetValue(null, Paths); Form = Create();
        }
        internal ServerForm Create() { return new ServerForm(null, persisted, delegate { throw new Exception("No camera"); }, new ServerClipboardServices { Sequence = delegate { return 42; }, Capture = delegate { throw new Exception("No clipboard"); }, Accepted = delegate { } }, delegate { throw new Exception("No dialog"); }, delegate { }, delegate { }); }
        internal CheckBox Toggle { get { Control[] found = Form.Controls.Find("ColorModesEnabled", true); Check(found.Length == 1 && found[0] is CheckBox, "Settings must contain the color-mode checkbox."); return (CheckBox)found[0]; } }
        internal object Get(string name) { return typeof(ServerForm).GetField(name, Flags).GetValue(Form); }
        internal void Set(string name, object value) { typeof(ServerForm).GetField(name, Flags).SetValue(Form, value); }
        internal void Call(string name, params object[] args) { typeof(ServerForm).GetMethod(name, Flags).Invoke(Form, args); }
        internal void Load() { Load(Form); }
        internal void Load(ServerForm form)
        {
            var restoring = typeof(ServerForm).GetField("restoringSettings", Flags); restoring.SetValue(form, true);
            try { typeof(ServerForm).GetMethod("LoadSettings", Flags).Invoke(form, null); typeof(ServerForm).GetMethod("RefreshTransferProfileChoices", Flags).Invoke(form, null); }
            finally { restoring.SetValue(form, false); }
        }
        public void Dispose()
        {
            Form.Dispose(); current.SetValue(null, previous);
            string root = Path.GetFullPath(Path.Combine("build", "")) + Path.DirectorySeparatorChar;
            if (Directory.Exists(Paths.BaseDirectory) && Paths.BaseDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) Directory.Delete(Paths.BaseDirectory, true);
        }
    }
}
