using System;
using System.IO;
using System.Security;
using System.Text;

namespace HorizonClient
{
    // Construction only resolves strings. Production explicitly chooses writable
    // profile settings storage; logs, received files and notices remain portable.
    internal sealed class ClientPaths
    {
        internal static readonly ClientPaths Current = new ClientPaths(AppDomain.CurrentDomain.BaseDirectory);
        internal string BaseDirectory { get; private set; }
        internal string SettingsDirectory { get; private set; }
        internal string CameraSelection { get { return Path.Combine(SettingsDirectory, "camera.id"); } }
        internal string AutomaticCameraSetting { get { return Path.Combine(SettingsDirectory, "camera.auto"); } }
        internal string AudioSelection { get { return Path.Combine(SettingsDirectory, "audio.id"); } }
        internal string AudioModemSetting { get { return Path.Combine(SettingsDirectory, "audio.modem"); } }
        internal string CameraResolutionSetting { get { return Path.Combine(SettingsDirectory, "camera.resolution"); } }
        internal string DebugLogSetting { get { return Path.Combine(SettingsDirectory, "client.debug.enabled"); } }
        internal string LanguageSetting { get { return Path.Combine(SettingsDirectory, UiText.LanguageFileName); } }
        internal string ErrorLog { get { return Path.Combine(BaseDirectory, "client.stderr.log"); } }
        internal string DebugLog { get { return Path.Combine(BaseDirectory, "client.debug.log"); } }
        internal string ReceivedRoot { get { return Path.Combine(BaseDirectory, "Received"); } }

        internal ClientPaths(string executableDirectory)
        {
            BaseDirectory = AbsoluteDirectory(executableDirectory);
            SettingsDirectory = BaseDirectory;
        }

        private static string AbsoluteDirectory(string executableDirectory)
        {
            if (String.IsNullOrWhiteSpace(executableDirectory) || !Path.IsPathRooted(executableDirectory)) throw new ArgumentException("An absolute executable directory is required.", "executableDirectory");
            string root = Path.GetPathRoot(executableDirectory);
            if (root.Length <= 1 || (root.Length == 2 && root[1] == ':')) throw new ArgumentException("An absolute executable directory is required.", "executableDirectory");
            string full = Path.GetFullPath(executableDirectory);
            root = Path.GetPathRoot(full);
            while (full.Length > root.Length && (full[full.Length - 1] == Path.DirectorySeparatorChar || full[full.Length - 1] == Path.AltDirectorySeparatorChar)) full = full.Substring(0, full.Length - 1);
            return full;
        }

        // Called once by the production entry point. Injected roots keep tests
        // independent of the real user's Local/Roaming application directories.
        internal void ConfigureSettingsStore(string localRoot, string roamingRoot)
        {
            string selected;
            if (!TryProfileSettings(localRoot, out selected) && !TryProfileSettings(roamingRoot, out selected))
            {
                selected = BaseDirectory;
                TryWritableDirectory(selected);
            }
            SettingsDirectory = selected;
            if (String.Equals(SettingsDirectory, BaseDirectory, StringComparison.OrdinalIgnoreCase)) return;
            foreach (string name in new[] { "camera.id", "camera.auto", "audio.id", "audio.modem", "camera.resolution", "client.debug.enabled", UiText.LanguageFileName })
                MigrateSetting(name);
        }

        private static bool TryProfileSettings(string profileRoot, out string selected)
        {
            selected = null;
            if (String.IsNullOrWhiteSpace(profileRoot)) return false;
            try
            {
                string directory = Path.Combine(AbsoluteDirectory(profileRoot), "HorizonClient", "Settings");
                if (!TryWritableDirectory(directory)) return false;
                selected = directory; return true;
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (SecurityException) { return false; }
        }

        private static bool TryWritableDirectory(string directory)
        {
            string temporary = null;
            try
            {
                Directory.CreateDirectory(directory);
                temporary = Path.Combine(directory, ".horizon-settings-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var probe = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { probe.WriteByte(1); probe.Flush(); }
                File.Delete(temporary); temporary = null;
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (SecurityException) { return false; }
            finally { DeleteOwnedTemporary(temporary); }
        }

        private void MigrateSetting(string name)
        {
            string temporary = null;
            try
            {
                string source = Path.Combine(BaseDirectory, name), destination = Path.Combine(SettingsDirectory, name);
                if (!File.Exists(source) || File.Exists(destination)) return;
                temporary = destination + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp";
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                // Move does not replace a setting which another instance wrote.
                File.Move(temporary, destination); temporary = null;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
            finally { DeleteOwnedTemporary(temporary); }
        }

        private static void DeleteOwnedTemporary(string temporary)
        {
            if (temporary == null) return;
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
        }

        internal void TryLogFailure(Exception error)
        {
            if (error == null) return;
            DiagnosticLog.Error("client.failure", error);
            if (DiagnosticLog.IsEnabled) return;
            try { File.AppendAllText(ErrorLog, DateTime.UtcNow.ToString("o") + " " + error.GetType().Name + ": " + error.Message + Environment.NewLine, new UTF8Encoding(false)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
        }
    }
}
