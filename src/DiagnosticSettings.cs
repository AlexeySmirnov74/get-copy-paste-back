using System;
using System.IO;
using System.Text;
namespace HorizonClient
{
    internal static class DiagnosticSettings
    {
        internal static string FlagPath(string directory, bool server) { return Path.Combine(directory, server ? "server.debug.enabled" : "client.debug.enabled"); }
        internal static bool Read(string directory, bool server, bool defaultEnabled = false)
        {
            try
            {
                string path = FlagPath(directory, server);
                if (!File.Exists(path) || new FileInfo(path).Length > 16) return defaultEnabled;
                string value = File.ReadAllText(path).Trim();
                return value == "1" ? true : value == "0" ? false : defaultEnabled;
            }
            catch (IOException) { return defaultEnabled; } catch (UnauthorizedAccessException) { return defaultEnabled; }
        }
        internal static bool Apply(string directory, bool server, bool enabled, bool save, string logDirectory = null)
        {
            if (save)
            {
                string path = FlagPath(directory, server), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temporary, enabled ? "1" : "0", new UTF8Encoding(false)); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            if (!enabled) { DiagnosticLog.Shutdown(); return false; }
            if (!DiagnosticLog.IsEnabled) DiagnosticLog.Initialize(Path.Combine(logDirectory ?? directory, server ? "server.debug.log" : "client.debug.log"), queueCapacity: 2048);
            DiagnosticLog.Write("application.logging_enabled", "role", server ? "server" : "client", "version", typeof(DiagnosticSettings).Assembly.GetName().Version.ToString());
            return DiagnosticLog.IsEnabled;
        }
    }
}
