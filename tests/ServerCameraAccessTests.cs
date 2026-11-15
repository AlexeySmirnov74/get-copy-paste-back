using System;
using System.Reflection;
using System.Windows.Forms;
using HorizonClient;

internal static class ServerCameraAccessTests
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value, string text) { checks++; if (!value) throw new Exception(text); }
    static void Set(ServerForm form, string name, object value) { typeof(ServerForm).GetField(name, Flags).SetValue(form, value); }
    static void Call(ServerForm form, string method, params object[] args) { typeof(ServerForm).GetMethod(method, Flags).Invoke(form, args); }
    static T Find<T>(Control owner, string name) where T : Control { return (T)owner.Controls.Find(name, true)[0]; }
    [STAThread]
    static int Main()
    {
        try {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new ServerForm(null, false)) {
                var tabs = Find<TabControl>(form, "ServerTabs"); IntPtr ignored = tabs.Handle;
                TabPage settings = tabs.TabPages[0], work = tabs.TabPages[1], log = tabs.TabPages[2], docs = tabs.TabPages[3];
                GroupBox panel = Find<GroupBox>(form, "BenchmarkPanel");
                Check(!work.Enabled && !log.Enabled && !panel.Enabled && docs.Enabled, "Unknown startup camera permits settings/documentation, blocks work/log/tests.");
                tabs.SelectedTab = work; Check(tabs.SelectedTab != work, "A disabled native tab cannot be selected programmatically.");
                tabs.SelectedTab = log; Check(tabs.SelectedTab != log, "The disabled log tab is protected by a selecting handler.");
                tabs.SelectedTab = docs; Check(tabs.SelectedTab == docs, "Documentation stays available before camera creation.");
                form.ApplyCameraPresence(false, null, false);
                Set(form, "ready", true); Call(form, "StartBenchmark");
                Check(typeof(ServerForm).GetField("speedTest", Flags).GetValue(form) == null, "Even a forced ready flag cannot start a test without an actual camera.");
                form.ApplyCameraPresence(true, false, "Existing stopped camera", false);
                Set(form, "ready", false); Call(form, "SetConfigurationEnabled", true);
                Check(work.Enabled && log.Enabled && !panel.Enabled, "Created but stopped camera unlocks work/log while tests still require active transmission.");
                Set(form, "ready", true); Call(form, "SetConfigurationEnabled", true);
                Check(panel.Enabled && Find<Button>(form, "Benchmark").Enabled, "The camera-ready state enables the bounded video test.");
                tabs.SelectedTab = work; Check(tabs.SelectedTab == work, "A created camera makes the Work tab selectable.");
                Set(form, "preparing", true); Call(form, "SetConfigurationEnabled", false);
                Check(!panel.Enabled && !Find<Button>(form, "Benchmark").Enabled, "Configuration in progress disables the whole test panel.");
                Set(form, "preparing", false); form.ApplyCameraPresence(false, null, false);
                Check(!work.Enabled && !log.Enabled && !panel.Enabled && tabs.SelectedTab == settings, "Camera deletion relocks pages and returns the user to settings.");
                form.ApplyCameraPresence(true, "Known camera", false); Set(form, "cameraLookupKnown", false); Call(form, "SetConfigurationEnabled", true);
                Check(!work.Enabled && !log.Enabled && !panel.Enabled, "A failed camera lookup cannot expose stale readiness.");
                Check(form.Controls.Find("Pause", true).Length == 0 && typeof(ServerForm).GetField("paused", Flags) == null, "The pause control and user pause state have both been removed.");
                Check(!form.Visible, "All access checks leave the server window hidden.");
            }
            Console.WriteLine("Server camera access checks passed: " + checks + "; actual hidden pages/handlers and fake lifecycle state, no camera/clipboard/audio/driver."); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
