// Actual server selection/XML with injected endpoint snapshots; no audio APIs.
using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using HorizonClient;

internal static class ServerAudioSelectionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;

    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            AllOutputsIsTheFreshAndLegacyDefault();
            SavedRouteSurvivesUnavailableSnapshots();
            ExplicitChoiceSurvivesRestart();
            ExplicitDefaultClearsThePreference();
            ExplicitAllOutputsSurvivesRestartAndLanguage();
            Console.WriteLine("Server audio selection checks passed: " + checks + ". Hidden forms, isolated XML and fake endpoint snapshots only.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { UiText.SetLanguage(UiLanguage.Russian); }
    }

    private static void AllOutputsIsTheFreshAndLegacyDefault()
    {
        foreach (string settings in new string[] { null, "<HorizonServer videoProfile='1' />" })
        using (var fixture = new Fixture())
        {
            if (settings != null) File.WriteAllText(fixture.Paths.SettingsFile, settings);
            using (ServerForm form = fixture.Create())
            {
                fixture.Snapshot(form, new[] { new AudioEndpoint("render-a", "Speakers"), new AudioEndpoint("render-b", "Headphones") });
                Check(fixture.Selected(form) == "all_render" && fixture.SelectedItem(form).Id == "all_render",
                    "Fresh and legacy settings without an audio preference select reception from all outputs.");
                fixture.Snapshot(form, new AudioEndpoint[0]);
                Check(fixture.SelectedItem(form).Id == "all_render" && !fixture.Combo(form).Text.Contains("unavailable"),
                    "An empty snapshot keeps the all-output option instead of a missing-device placeholder.");
                Check(settings == null ? !File.Exists(fixture.Paths.SettingsFile) : File.ReadAllText(fixture.Paths.SettingsFile) == settings,
                    "Automatic discovery must not rewrite preferences before a user changes them.");
            }
        }
    }

    private static void ExplicitAllOutputsSurvivesRestartAndLanguage()
    {
        using (var fixture = new Fixture())
        {
            File.WriteAllText(fixture.Paths.SettingsFile, "<HorizonServer audioOutput='render-a' />");
            using (ServerForm form = fixture.Create())
            {
                fixture.Snapshot(form, new[] { new AudioEndpoint("render-a", "Speakers") });
                File.Delete(fixture.Paths.SettingsFile);
                fixture.Select(form, "all_render");
                var settings = new XmlDocument(); settings.Load(fixture.Paths.SettingsFile);
                Check(settings.DocumentElement.GetAttribute("audioOutput") == "all_render", "Explicit all-output selection is saved by the real writer.");
            }
            using (ServerForm form = fixture.Create())
            {
                fixture.Snapshot(form, new AudioEndpoint[0]);
                Check(fixture.SelectedItem(form).Id == "all_render", "All-output reception survives restart without requiring an available device snapshot.");
                foreach (UiLanguage language in new[] { UiLanguage.Russian, UiLanguage.English })
                {
                    UiText.SetLanguage(language);
                    typeof(ServerForm).GetMethod("ApplyServerLanguage", Private).Invoke(form, null);
                    Check(fixture.SelectedItem(form).Id == "all_render" && fixture.Combo(form).Text ==
                        (language == UiLanguage.English ? "All audio outputs" : "Все аудиоустройства вывода"),
                        "Switching language preserves and translates the selected all-output option.");
                }
            }
        }
    }

    private static void SavedRouteSurvivesUnavailableSnapshots()
    {
        using (var fixture = new Fixture())
        {
            string original = "<HorizonServer audioOutput='render-target' />";
            File.WriteAllText(fixture.Paths.SettingsFile, original);
            using (ServerForm form = fixture.Create())
            {
                fixture.Snapshot(form, new AudioEndpoint[0]);
                Check(fixture.Selected(form) == "render-target", "A temporarily empty enumeration must retain the explicitly saved server output.");
                Check(fixture.SelectedItem(form).Id == "render-target" && fixture.Combo(form).SelectedIndex > 0,
                    "An unavailable explicit route stays selected rather than displaying Windows default.");
                Check(File.ReadAllText(fixture.Paths.SettingsFile) == original, "A missing endpoint cannot rewrite or erase the user's saved preference.");
                fixture.Snapshot(form, new[] { new AudioEndpoint("render-unrelated", "Unrelated speakers") });
                Check(fixture.Selected(form) == "render-target" && fixture.SelectedItem(form).Id == "render-target",
                    "Other available speakers cannot replace the missing explicit route.");
                fixture.Snapshot(form, new[] { new AudioEndpoint("render-unrelated", "Same caption"), new AudioEndpoint("render-target", "Renamed target") });
                Check(fixture.SelectedItem(form).Id == "render-target" && fixture.Combo(form).Text == "Renamed target",
                    "The exact stable endpoint is restored after reordering and renaming.");
                Check(File.ReadAllText(fixture.Paths.SettingsFile) == original, "Automatic recovery neither rewrites settings nor adopts a fallback.");
                fixture.Snapshot(form, new AudioEndpoint[0]);
                UiText.SetLanguage(UiLanguage.English);
                typeof(ServerForm).GetMethod("ApplyServerLanguage", Private).Invoke(form, null);
                Check(fixture.Combo(form).Text == "Selected audio output is unavailable", "The unavailable output placeholder follows the UI language without changing its endpoint identity.");
                UiText.SetLanguage(UiLanguage.Russian);
                int generation = (int)typeof(ServerForm).GetField("outputGeneration", Private).GetValue(form);
                typeof(ServerForm).GetMethod("ApplyAudioOutputSnapshot", Private).Invoke(form, new object[] { new[] { new AudioEndpoint("render-target", "Stale caption") }, generation - 1 });
                Check(fixture.SelectedItem(form).Id == "render-target" && fixture.Combo(form).Text != "Stale caption", "A stale asynchronous enumeration cannot replace a newer unavailable snapshot.");
            }
            using (ServerForm restarted = fixture.Create())
            {
                fixture.Snapshot(restarted, new[] { new AudioEndpoint("render-target", "After restart") });
                Check(fixture.Selected(restarted) == "render-target" && fixture.Combo(restarted).Text == "After restart",
                    "Restart restores the saved route after an unavailable shutdown.");
            }
        }
    }

    private static void ExplicitChoiceSurvivesRestart()
    {
        using (var fixture = new Fixture())
        {
            using (ServerForm form = fixture.Create())
            {
                fixture.Snapshot(form, new[] { new AudioEndpoint("render-first", "Identical speakers"), new AudioEndpoint("render-second", "Identical speakers") });
                fixture.Select(form, "render-second");
                Check(fixture.Selected(form) == "render-second", "The UI applies the user's exact endpoint, including identical captions.");
                var settings = new XmlDocument(); settings.Load(fixture.Paths.SettingsFile);
                Check(settings.DocumentElement.GetAttribute("audioOutput") == "render-second", "An explicit server choice is immediately persisted through the actual XML writer.");
            }
            using (ServerForm restarted = fixture.Create())
            {
                fixture.Snapshot(restarted, new[] { new AudioEndpoint("render-second", "Renamed speakers"), new AudioEndpoint("render-first", "Identical speakers") });
                Check(fixture.SelectedItem(restarted).Id == "render-second" && fixture.Selected(restarted) == "render-second",
                    "Restart selects the same WASAPI identity independently of enumeration order and caption.");
            }
        }
    }

    private static void ExplicitDefaultClearsThePreference()
    {
        using (var fixture = new Fixture())
        {
            File.WriteAllText(fixture.Paths.SettingsFile, "<HorizonServer audioOutput='render-missing' />");
            using (ServerForm form = fixture.Create())
            {
                fixture.Snapshot(form, new AudioEndpoint[0]);
                // This test owns the file. Use a new atomic destination so an
                // unrelated File.Replace filter cannot hide selection behavior.
                File.Delete(fixture.Paths.SettingsFile);
                fixture.Select(form, null);
                Check(fixture.Selected(form) == null, "Only an explicit Windows-default choice clears the saved endpoint.");
                var settings = new XmlDocument(); settings.Load(fixture.Paths.SettingsFile);
                Check(settings.DocumentElement.HasAttribute("audioOutput") && settings.DocumentElement.GetAttribute("audioOutput") == "",
                    "Choosing Windows default writes an explicit empty preference, distinct from the fresh all-output default.");
            }
            using (ServerForm restarted = fixture.Create())
            {
                fixture.Snapshot(restarted, new[] { new AudioEndpoint("render-missing", "Returned speakers") });
                Check(fixture.Selected(restarted) == null && fixture.SelectedItem(restarted).Id == null, "A restarted server preserves an explicit Windows-default choice even if the old device returns.");
            }
        }
    }

    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.GetFullPath(Path.Combine("build", "server-audio-selection-" + Guid.NewGuid().ToString("N")));
        private readonly FieldInfo current = typeof(ServerPaths).GetField("Current", BindingFlags.Static | BindingFlags.NonPublic);
        private readonly object previous;
        internal readonly ServerPaths Paths;
        internal Fixture()
        {
            Paths = new ServerPaths(root); Directory.CreateDirectory(Paths.DataDirectory);
            previous = current.GetValue(null); current.SetValue(null, Paths);
        }
        internal ServerForm Create()
        {
            var form = new ServerForm(null, true, delegate { throw new Exception("No camera."); },
                new ServerClipboardServices { Sequence = delegate { throw new Exception("No clipboard."); }, Capture = delegate { throw new Exception("No clipboard."); }, Accepted = delegate { throw new Exception("No sound."); } },
                delegate { throw new Exception("No dialogs."); }, delegate { throw new Exception("No sound."); }, delegate { throw new Exception("No notification."); });
            typeof(ServerForm).GetField("restoringSettings", Private).SetValue(form, true);
            try { typeof(ServerForm).GetMethod("LoadSettings", Private).Invoke(form, null); }
            finally { typeof(ServerForm).GetField("restoringSettings", Private).SetValue(form, false); }
            return form;
        }
        internal ComboBox Combo(ServerForm form) { return (ComboBox)typeof(ServerForm).GetField("audioOutput", Private).GetValue(form); }
        internal string Selected(ServerForm form) { return (string)typeof(ServerForm).GetField("selectedAudioId", Private).GetValue(form); }
        internal AudioEndpoint SelectedItem(ServerForm form) { return (AudioEndpoint)Combo(form).SelectedItem; }
        internal void Select(ServerForm form, string id)
        {
            for (int index = 0; index < Combo(form).Items.Count; index++)
                if (String.Equals(((AudioEndpoint)Combo(form).Items[index]).Id, id, StringComparison.Ordinal)) { Combo(form).SelectedIndex = index; return; }
            throw new Exception("Requested audio option is missing: " + id);
        }
        internal void Snapshot(ServerForm form, AudioEndpoint[] outputs)
        {
            const int generation = 7;
            typeof(ServerForm).GetField("outputGeneration", Private).SetValue(form, generation);
            typeof(ServerForm).GetMethod("ApplyAudioOutputSnapshot", Private).Invoke(form, new object[] { outputs, generation });
        }
        public void Dispose() { current.SetValue(null, previous); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
