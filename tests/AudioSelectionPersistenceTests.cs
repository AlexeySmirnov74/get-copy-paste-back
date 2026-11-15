// Actual tray selection with isolated settings and fake outputs only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

internal static class AudioSelectionPersistenceTests
{
    private static int checks;

    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            ChoiceSurvivesFailedDiagnosticAndRestart();
            ChoiceSurvivesFailedConfiguration();
            LegacyChoiceSurvivesMissingEnumeration();
            MalformedChoiceCannotBecomeAutomatic();
            LatestChoiceIsSavedBeforePendingDeviceWork();
            SavedChoiceBlocksAutomaticPlaybackBeforeEnumeration();
            Console.WriteLine("Audio selection persistence checks passed: " + checks + ". Isolated settings and fake endpoints; no devices or clipboard.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void ChoiceSurvivesFailedDiagnosticAndRestart()
    {
        using (var fixture = new Fixture())
        {
            fixture.Outputs = new[] { Endpoint("waveout:3", "Same speakers", "native-target") };
            fixture.FailProbe = true;
            using (ReceiverContext receiver = fixture.Create())
            {
                fixture.Refresh(receiver); fixture.Click(receiver, 0);
                Until(delegate { return Volatile.Read(ref fixture.Failures) == 1; }, "The selected output's diagnostic fails.");
                Check(File.Exists(fixture.Paths.AudioSelection), "The user's selected output is saved even when its optional diagnostic fails.");
                string[] saved = File.ReadAllLines(fixture.Paths.AudioSelection);
                Check(saved.Length == 3 && saved[0] == "waveout:3" && saved[1] == "Same speakers" && saved[2] == "native-target",
                    "The saved selection includes the stable endpoint identity as well as its current index and caption.");
            }
            fixture.FailProbe = false;
            fixture.Outputs = new[] { Endpoint("waveout:3", "Same speakers", "native-other"), Endpoint("waveout:7", "Renamed speakers", "native-target") };
            using (ReceiverContext restarted = fixture.Create())
            {
                fixture.Refresh(restarted);
                Check(fixture.Active == "waveout:7", "Restart resolves stable identity after both the WinMM index and caption change.");
                Check(fixture.Checked(restarted, 1) && !fixture.Checked(restarted, 0), "The selected device is checked despite an identically named replacement at the old index.");
                string persisted = File.ReadAllText(fixture.Paths.AudioSelection);
                fixture.Outputs = new AudioEndpoint[0]; fixture.Refresh(restarted);
                Check(fixture.Active == "waveout:65535" && File.ReadAllText(fixture.Paths.AudioSelection) == persisted,
                    "An empty enumeration disables the explicit route without replacing the saved preference.");
                fixture.Outputs = new[] { Endpoint("waveout:2", "New caption", "native-target") }; fixture.Refresh(restarted);
                Check(fixture.Active == "waveout:2" && fixture.Checked(restarted, 0), "The requested route resumes when the same endpoint reappears at another index.");
                fixture.Outputs = new[] { Endpoint("waveout:2", "New caption", "native-replacement") }; fixture.Refresh(restarted);
                Check(fixture.Active == "waveout:65535" && !fixture.Checked(restarted, 0),
                    "A changed persistent identity at the same numeric index and caption cannot silently reuse the old route.");
                Check(Volatile.Read(ref fixture.Probes) == 1 && File.ReadAllText(fixture.Paths.AudioSelection) == persisted,
                    "Restoration, temporary loss and reconnection neither emit probes nor rewrite the saved user choice.");
            }
        }
    }

    private static void ChoiceSurvivesFailedConfiguration()
    {
        using (var fixture = new Fixture())
        {
            fixture.Outputs = new[] { Endpoint("waveout:1", "Explicit speakers", "native-explicit") };
            using (ReceiverContext receiver = fixture.Create())
            {
                fixture.Refresh(receiver); fixture.FailSet = true; fixture.Click(receiver, 0);
                Until(delegate { return Volatile.Read(ref fixture.Failures) == 1; }, "The fake endpoint rejects configuration.");
                Check(File.ReadAllLines(fixture.Paths.AudioSelection)[2] == "native-explicit",
                    "A temporary configuration error does not discard the user's explicit preference.");
            }
            fixture.FailSet = false;
            fixture.Outputs = new[] { Endpoint("waveout:5", "Explicit speakers", "native-explicit") };
            using (ReceiverContext restarted = fixture.Create())
            {
                fixture.Refresh(restarted);
                Check(fixture.Active == "waveout:5", "A saved choice that failed configuration is restored successfully on the next startup.");
            }
        }
    }

    private static void LegacyChoiceSurvivesMissingEnumeration()
    {
        using (var fixture = new Fixture())
        {
            File.WriteAllLines(fixture.Paths.AudioSelection, new[] { "waveout:3", "Legacy full speakers" });
            string original = File.ReadAllText(fixture.Paths.AudioSelection);
            fixture.Outputs = new[] { Endpoint("waveout:3", "Other speakers", "native-other"), Endpoint("waveout:9", "Legacy full speakers", "native-legacy") };
            using (ReceiverContext receiver = fixture.Create())
            {
                fixture.Refresh(receiver);
                Check(fixture.Active == "waveout:9", "Existing two-line selections resolve by a unique exact caption instead of yesterday's index.");
                fixture.EnumerationFails = true; fixture.Refresh(receiver);
                Check(fixture.Active == "waveout:65535" && File.ReadAllText(fixture.Paths.AudioSelection) == original,
                    "Enumeration exceptions preserve the legacy preference and never choose a hardware/default fallback.");
                fixture.EnumerationFails = false;
                fixture.Outputs = new[] { Endpoint("waveout:8", "Legacy full speakers", "native-legacy") }; fixture.Refresh(receiver);
                Check(fixture.Active == "waveout:8" && File.ReadAllText(fixture.Paths.AudioSelection) == original,
                    "A legacy selection reconnects without unsolicited settings migration or diagnostic sound.");
                fixture.Click(receiver, 0);
                Until(delegate { return Volatile.Read(ref fixture.Probes) == 1; }, "The explicit legacy re-selection queues its diagnostic.");
                Check(File.ReadAllLines(fixture.Paths.AudioSelection)[2] == "native-legacy", "An explicit re-selection upgrades legacy storage to stable identity.");
            }
        }
    }

    private static void MalformedChoiceCannotBecomeAutomatic()
    {
        foreach (string[] invalid in new[] { new[] { "waveout:3", "" }, new[] { "waveout:garbage", "Horizon Audio" }, new[] { "waveout:1" } })
        using (var fixture = new Fixture())
        {
            File.WriteAllLines(fixture.Paths.AudioSelection, invalid);
            fixture.Outputs = new[] { Endpoint("waveout:0", "Horizon Audio", "native-horizon") };
            using (ReceiverContext receiver = fixture.Create())
            {
                fixture.Refresh(receiver);
                Check(fixture.Active == "waveout:65535" && !fixture.Checked(receiver, 0),
                    "A malformed explicit saved selection remains unavailable instead of silently becoming automatic mode.");
            }
        }
    }

    private static void LatestChoiceIsSavedBeforePendingDeviceWork()
    {
        using (var fixture = new Fixture())
        {
            fixture.Outputs = new[] { Endpoint("waveout:1", "First output", "native-first"), Endpoint("waveout:2", "Second output", "native-second") };
            using (ReceiverContext receiver = fixture.Create())
            {
                fixture.Refresh(receiver);
                object workerGate = typeof(ReceiverContext).GetField("audioOutputGate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(receiver);
                lock (workerGate)
                {
                    fixture.Click(receiver, 0); fixture.Click(receiver, 1);
                    receiver.Menu.Items["audio.test"].PerformClick();
                    Check(File.ReadAllLines(fixture.Paths.AudioSelection)[2] == "native-second",
                        "The latest explicit preference is saved before pending device work or a newer manual diagnostic can supersede it.");
                }
                Until(delegate { return Volatile.Read(ref fixture.Probes) == 1; }, "Only the latest requested diagnostic runs.");
                Check(fixture.Active == "waveout:2", "Superseding diagnostics still apply the latest selected output.");
            }
        }
    }

    private static void SavedChoiceBlocksAutomaticPlaybackBeforeEnumeration()
    {
        using (var fixture = new Fixture())
        {
            File.WriteAllLines(fixture.Paths.AudioSelection, new[] { "waveout:3", "Explicit output", "native-explicit" });
            using (ReceiverContext receiver = fixture.Create(true))
            {
                object sender = typeof(ReceiverContext).GetField("audioSender", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(receiver);
                string requested = (string)sender.GetType().GetField("preferredOutput", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sender);
                Check(requested == "waveout:65535", "A restored explicit choice blocks automatic playback before asynchronous enumeration can resolve its current index.");
                Check(Volatile.Read(ref fixture.OutputEnumerations) == 0 && Volatile.Read(ref fixture.Probes) == 0,
                    "The initial unavailable route requires no device enumeration, playback, diagnostic or clipboard listener.");
            }
        }
    }

    private static AudioEndpoint Endpoint(string id, string name, string persistent) { return new AudioEndpoint(id, name, persistent, name); }
    private static void Until(Func<bool> condition, string message)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Yield(); }
        Check(condition(), message);
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.GetFullPath(Path.Combine("build", "audio-selection-" + Guid.NewGuid().ToString("N")));
        private string active;
        private int sets;
        internal readonly ClientPaths Paths;
        internal AudioEndpoint[] Outputs = new AudioEndpoint[0];
        internal bool FailProbe, FailSet, EnumerationFails;
        internal int Probes, Failures, OutputEnumerations;
        internal string Active { get { return Volatile.Read(ref active); } }
        internal Fixture() { Directory.CreateDirectory(root); Paths = new ClientPaths(root); }
        internal ReceiverContext Create(bool constructSender = false)
        {
            var services = new ReceiverServices(Paths) {
                ReadSelection = delegate { return null; }, WriteSelection = delegate { throw new Exception("No camera setting writes."); },
                ReadAutomaticCameraSelection = delegate { return "0"; }, ListDevices = delegate { return new List<CameraDevice>(); },
                ListAudioOutputs = delegate { Interlocked.Increment(ref OutputEnumerations); if (EnumerationFails) throw new IOException("Synthetic enumeration failure."); return Outputs; },
                SetAudioOutput = delegate(string id) { if (FailSet) throw new IOException("Synthetic endpoint failure."); Volatile.Write(ref active, id); Interlocked.Increment(ref sets); },
                TestAudioOutput = delegate { Interlocked.Increment(ref Probes); if (FailProbe) throw new IOException("Synthetic diagnostic failure."); return Guid.NewGuid(); },
                LogFailure = delegate { Interlocked.Increment(ref Failures); },
                PrepareAudioModem = delegate { }, ApplyAudioModem = delegate { },
                ShowTrayIcon = false, EnableDeferredPaste = false, EnableRetryTimer = false, EnableSessionEvents = false, RestoreSelection = true
            };
            if (!constructSender) return new ReceiverContext(services);
            // Construction stores the disabled sender's route only. It never
            // enables a session/listener or invokes its native audio services.
            return (ReceiverContext)typeof(ReceiverContext).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(ReceiverServices), typeof(bool) }, null).Invoke(new object[] { services, true });
        }
        internal void Refresh(ReceiverContext receiver)
        {
            int before = Volatile.Read(ref sets); bool complete = false;
            receiver.RefreshDevices(delegate { complete = true; });
            Until(delegate { return complete && Volatile.Read(ref sets) > before; }, "An enumerated audio snapshot is applied without opening any real endpoint.");
        }
        internal bool Checked(ReceiverContext receiver, int index) { return ((ToolStripMenuItem)((ToolStripMenuItem)receiver.Menu.Items["audio.output"]).DropDownItems[index]).Checked; }
        internal void Click(ReceiverContext receiver, int index) { ((ToolStripMenuItem)receiver.Menu.Items["audio.output"]).DropDownItems[index].PerformClick(); }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
