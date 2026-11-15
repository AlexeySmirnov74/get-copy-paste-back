using System;
using System.IO;
using HorizonClient;
internal static class DiagnosticSettingsTests
{
    static int checks;
    static void Check(bool value, string text) { if (!value) throw new Exception(text); checks++; }
    static int Main()
    {
        string root = Path.Combine(Environment.CurrentDirectory, "build", "diagnostic-settings-" + Guid.NewGuid().ToString("N"));
        try {
            Directory.CreateDirectory(root);
            foreach (bool server in new[] { false, true }) {
                Check(!DiagnosticSettings.Read(root, server), "Absent setting defaults to disabled.");
                DiagnosticSettings.Apply(root, server, false, false);
                Check(Directory.GetFiles(root).Length == (server ? 2 : 0), "Disabled startup creates no files.");
                Check(DiagnosticSettings.Apply(root, server, true, true) && DiagnosticSettings.Read(root, server), "Enable is immediate and persists.");
                DiagnosticLog.Write("setting.test");
                Check(!DiagnosticSettings.Apply(root, server, false, true) && !DiagnosticSettings.Read(root, server), "Disable stops and persists.");
                string path = Path.Combine(root, server ? "server.debug.log" : "client.debug.log"); long size = new FileInfo(path).Length;
                DiagnosticLog.Write("must.not.write");
                Check(new FileInfo(path).Length == size && !DiagnosticLog.IsEnabled, "Disabled calls cannot append a log.");
                Check(File.ReadAllText(path).Contains("setting.test"), "Existing diagnostic history remains intact.");
            }
            Check(Directory.GetFiles(root, "*.tmp").Length == 0, "Atomic setting writes leave no temporary files.");
            string settings = Path.Combine(root, "profile-settings"), logs = Path.Combine(root, "portable-client");
            Directory.CreateDirectory(settings); Directory.CreateDirectory(logs);
            Check(DiagnosticSettings.Apply(settings, false, true, true, logs), "Profile settings and portable logs can be separated.");
            DiagnosticLog.Write("profile.setting.test");
            DiagnosticSettings.Apply(settings, false, false, true, logs);
            Check(File.Exists(Path.Combine(settings, "client.debug.enabled")) && !DiagnosticSettings.Read(settings, false), "The logging preference is persisted in the selected settings directory.");
            Check(!File.Exists(Path.Combine(logs, "client.debug.enabled")) && !File.Exists(Path.Combine(settings, "client.debug.log")), "A client setting cannot silently move its diagnostic log into AppData.");
            Check(File.ReadAllText(Path.Combine(logs, "client.debug.log")).Contains("profile.setting.test"), "The client diagnostic log remains beside its executable.");
            Console.WriteLine("Diagnostic settings checks passed: " + checks + ". Isolated portable files only."); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { DiagnosticLog.Shutdown(); }
    }
}
