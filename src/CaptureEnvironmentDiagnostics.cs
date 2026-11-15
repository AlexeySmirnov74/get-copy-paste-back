using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HorizonClient
{
    // Failure-only, bounded, read-only metadata. This never opens a camera,
    // inspects clipboard/history/command lines or changes Windows settings.
    internal static class CaptureEnvironmentDiagnostics
    {
        internal const int MaximumSnapshots = 4, MaximumMilliseconds = 3000;
        private static readonly SnapshotThrottle Throttle = new SnapshotThrottle();
        internal static readonly string[] ProcessNames = { "Teams", "ms-teams", "Squadus", "SquadusClient", "zoom", "WindowsCamera", "Camera", "ffmpeg", "Client", "Server", "HorizonClient", "HorizonServer" };
        private static readonly string[] DeviceValues = { "FriendlyName", "DeviceDesc", "Service", "ClassGUID", "Driver", "ConfigFlags", "Problem", "Capabilities", "Mfg", "ContainerID", "HardwareID", "CompatibleIDs", "UpperFilters", "LowerFilters" };
        private static readonly string[] DriverValues = { "DriverDesc", "DriverVersion", "DriverDate", "ProviderName", "InfPath", "InfSection", "MatchingDeviceId", "DriverDateData", "UpperFilters", "LowerFilters" };
        private static readonly string[] ServiceValues = { "ImagePath", "Type", "Start", "DisplayName", "ErrorControl", "DependOnService" };

        internal static void Capture(string deviceId, string stage)
        {
            if (!DiagnosticLog.IsEnabled || !Throttle.TryReserve(deviceId, stage)) return;
            try { ThreadPool.QueueUserWorkItem(delegate {
                try { Collect(deviceId, stage, new CaptureEnvironmentDiagnosticServices(), DiagnosticLog.Write); }
                catch (Exception error) { try { Failure(DiagnosticLog.Write, "snapshot", null, error); } catch { } }
            }); }
            catch (Exception error) { try { Failure(DiagnosticLog.Write, "queue", null, error); } catch { } }
        }

        internal static void Collect(string deviceId, string stage, CaptureEnvironmentDiagnosticServices services, Action<string, object[]> emit)
        {
            if (services == null || emit == null) throw new ArgumentNullException();
            long start = services.Milliseconds;
            emit("capture.environment.started", new object[] { "camera_id", BoundValue(deviceId), "stage", BoundValue(stage), "maximum_ms", MaximumMilliseconds,
                "scope", "selected-device metadata, relevant privacy/policy, allowlisted process presence only; no camera/clipboard access or OS changes" });
            try { EmitMetadata(emit, "capture.environment.process", services.EnvironmentMetadata(), null); }
            catch (Exception error) { Failure(emit, "process_environment", null, error); }
            if (WithinBudget(services, start)) ReadKey(services, emit, "HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                new[] { "ProductName", "DisplayVersion", "CurrentBuildNumber", "UBR", "EditionID", "InstallationType" });
            string instance;
            if (TryGetDeviceInstanceId(deviceId, out instance) && WithinBudget(services, start))
            {
                string path = @"SYSTEM\CurrentControlSet\Enum\" + instance;
                Dictionary<string, object> device = ReadKey(services, emit, "HKLM", path, DeviceValues);
                string value;
                if (TryString(device, "Driver", out value) && TryGetDriverKey(value, out path) && WithinBudget(services, start))
                    ReadKey(services, emit, "HKLM", path, DriverValues);
                if (TryString(device, "Service", out value) && IsServiceName(value) && WithinBudget(services, start))
                {
                    path = @"SYSTEM\CurrentControlSet\Services\" + value;
                    Dictionary<string, object> service = ReadKey(services, emit, "HKLM", path, ServiceValues);
                    string image, resolved;
                    if (TryString(service, "ImagePath", out image) && TryResolveDriverImage(image, services.WindowsDirectory, out resolved) && WithinBudget(services, start))
                    {
                        try { EmitMetadata(emit, "capture.environment.driver_file", services.FileMetadata(resolved), resolved); }
                        catch (Exception error) { Failure(emit, "driver_file_metadata", resolved, error); }
                    }
                }
            }
            else emit("capture.environment.device_resolution", new object[] { "resolved", false, "reason", "not_a_supported_exact_pnp_interface_moniker_or_budget_exhausted" });

            string[,] privacy = {
                { "HKCU", @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam", "Value" },
                { "HKCU", @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged", "Value" },
                { "HKLM", @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam", "Value" },
                { "HKLM", @"Software\Policies\Microsoft\Camera", "AllowCamera" },
                { "HKLM", @"Software\Policies\Microsoft\Windows\AppPrivacy", "LetAppsAccessCamera" }
            };
            for (int i = 0; i < privacy.GetLength(0) && WithinBudget(services, start); i++)
                ReadKey(services, emit, privacy[i, 0], privacy[i, 1], new[] { privacy[i, 2] });
            int recorded = 0;
            foreach (string name in ProcessNames)
            {
                if (!WithinBudget(services, start) || recorded >= 16) break;
                try
                {
                    CaptureProcessPresence[] matches = services.ProcessesByName(name);
                    if (matches == null) continue;
                    for (int index = 0; index < Math.Min(4, matches.Length) && recorded < 16; index++)
                    {
                        CaptureProcessPresence process = matches[index];
                        if (process == null || !IsAllowedProcessName(process.Name)) continue;
                        emit("capture.environment.process_presence", new object[] { "name", process.Name, "pid", process.ProcessId, "session_id", process.SessionId,
                            "session_query_hresult", process.SessionQueryHresult, "note", "Process presence does not establish that this process is using the camera." });
                        recorded++;
                    }
                }
                catch (Exception error) { Failure(emit, "allowlisted_process_query", name, error); }
            }
            emit("capture.environment.completed", new object[] { "camera_id", BoundValue(deviceId), "stage", BoundValue(stage), "elapsed_ms", Math.Max(0, services.Milliseconds - start),
                "budget_exhausted", !WithinBudget(services, start), "process_records", recorded });
        }

        private static bool WithinBudget(CaptureEnvironmentDiagnosticServices services, long start) { return services.Milliseconds - start < MaximumMilliseconds; }
        private static void EmitMetadata(Action<string, object[]> emit, string eventName, Dictionary<string, object> values, string path)
        {
            var fields = new List<object>();
            if (path != null) { fields.Add("path"); fields.Add(path); fields.Add("operation"); fields.Add("read_file_version_only_not_LoadLibrary"); }
            if (values != null) foreach (KeyValuePair<string, object> pair in values)
            { if (fields.Count >= 56) break; fields.Add(pair.Key); fields.Add(BoundValue(pair.Value)); }
            emit(eventName, fields.ToArray());
        }
        private static Dictionary<string, object> ReadKey(CaptureEnvironmentDiagnosticServices services, Action<string, object[]> emit, string hive, string path, string[] names)
        {
            try
            {
                Dictionary<string, object> values = services.RegistryValues(hive, path, names);
                emit("capture.environment.registry_status", new object[] { "hive", hive, "registry_path", path, "status", values == null ? "absent" : "read", "hresult", "0x00000000" });
                if (values != null) foreach (string name in names)
                {
                    object value;
                    emit("capture.environment.registry_value", new object[] { "hive", hive, "registry_path", path, "value_name", name,
                        "value", values.TryGetValue(name, out value) ? BoundValue(value) : null, "value_type", value == null ? "not_set" : value.GetType().FullName });
                }
                return values;
            }
            catch (Exception error) { Failure(emit, "registry_read", hive + "\\" + path, error); return null; }
        }
        private static void Failure(Action<string, object[]> emit, string operation, string path, Exception error)
        { emit("capture.environment.read_failed", new object[] { "operation", operation, "path", path, "exception_type", error.GetType().FullName, "hresult", "0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture) }); }
        private static bool TryString(Dictionary<string, object> values, string name, out string value)
        { object raw; value = null; if (values == null || !values.TryGetValue(name, out raw)) return false; value = raw as string; return !String.IsNullOrEmpty(value) && value.Length <= 1024; }

        internal static bool TryGetDeviceInstanceId(string moniker, out string instance)
        {
            instance = null;
            const string prefix = @"@device:pnp:\\?\";
            if (String.IsNullOrEmpty(moniker) || moniker.Length > 4096 || !moniker.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string[] pieces = moniker.Substring(prefix.Length).Split('#');
            if (pieces.Length != 4) return false;
            for (int i = 0; i < 3; i++) if (!SafeComponent(pieces[i], 512)) return false;
            string category = pieces[3].Split('\\')[0]; Guid parsed;
            if (!Guid.TryParse(category, out parsed)) return false;
            instance = pieces[0] + "\\" + pieces[1] + "\\" + pieces[2]; return true;
        }
        private static bool SafeComponent(string value, int maximum)
        {
            if (String.IsNullOrEmpty(value) || value.Length > maximum || value == "." || value == "..") return false;
            foreach (char c in value) if (!(Char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '&' || c == '{' || c == '}')) return false;
            return true;
        }
        internal static bool TryGetDriverKey(string driver, out string path)
        {
            path = null; if (String.IsNullOrEmpty(driver) || driver.Length > 128) return false;
            string[] pieces = driver.Split('\\'); Guid id; int index;
            if (pieces.Length != 2 || !Guid.TryParse(pieces[0], out id) || pieces[1].Length != 4 || !Int32.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out index)) return false;
            path = @"SYSTEM\CurrentControlSet\Control\Class\" + driver; return true;
        }
        private static bool IsServiceName(string name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > 128) return false;
            foreach (char c in name) if (!(Char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')) return false;
            return true;
        }
        internal static bool TryResolveDriverImage(string image, string windowsDirectory, out string path)
        {
            path = null;
            if (String.IsNullOrWhiteSpace(image) || image.Length > 1024 || String.IsNullOrWhiteSpace(windowsDirectory)) return false;
            string value = image.Trim();
            if (value.StartsWith(@"\??\", StringComparison.Ordinal)) value = value.Substring(4);
            if (value.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) value = Path.Combine(windowsDirectory, value.Substring(12));
            else if (value.StartsWith(@"%SystemRoot%\", StringComparison.OrdinalIgnoreCase)) value = Path.Combine(windowsDirectory, value.Substring(13));
            else if (value.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase)) value = Path.Combine(windowsDirectory, value);
            try
            {
                string root = Path.GetFullPath(windowsDirectory).TrimEnd('\\') + "\\";
                string full = Path.GetFullPath(value);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)) return false;
                path = full; return true;
            }
            catch (Exception error) { if (error is ArgumentException || error is NotSupportedException || error is PathTooLongException) return false; throw; }
        }
        internal static bool IsAllowedProcessName(string name)
        { if (String.IsNullOrEmpty(name)) return false; foreach (string allowed in ProcessNames) if (String.Equals(name, allowed, StringComparison.OrdinalIgnoreCase)) return true; return false; }
        internal static object BoundValue(object value)
        {
            if (value == null || value is int || value is uint || value is long || value is ulong || value is bool) return value;
            string text = value as string; if (text != null) return text.Length <= 1024 ? text : text.Substring(0, 1024);
            string[] strings = value as string[];
            if (strings != null) { var result = new List<string>(); for (int i = 0; i < Math.Min(16, strings.Length); i++) { string s = strings[i] ?? String.Empty; result.Add(s.Length <= 256 ? s : s.Substring(0, 256)); } string joined = String.Join("; ", result.ToArray()); return joined.Length <= 1024 ? joined : joined.Substring(0, 1024); }
            byte[] bytes = value as byte[]; if (bytes != null) return BitConverter.ToString(bytes, 0, Math.Min(32, bytes.Length));
            return "unsupported_metadata_type:" + value.GetType().Name;
        }
        internal sealed class SnapshotThrottle
        {
            private readonly HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            internal bool TryReserve(string deviceId, string stage)
            {
                if (String.IsNullOrEmpty(deviceId) || deviceId.Length > 4096 || String.IsNullOrEmpty(stage) || stage.Length > 128) return false;
                lock (keys) { return keys.Count < MaximumSnapshots && keys.Add(deviceId + "\n" + stage); }
            }
        }
    }

    internal sealed class CaptureProcessPresence { internal string Name; internal int ProcessId; internal int? SessionId; internal string SessionQueryHresult; }
    internal class CaptureEnvironmentDiagnosticServices
    {
        internal virtual long Milliseconds { get { return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)); } }
        internal virtual string WindowsDirectory { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }
        internal virtual Dictionary<string, object> EnvironmentMetadata()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Process process = Process.GetCurrentProcess()) return new Dictionary<string, object> {
                { "os_version", Environment.OSVersion.Version.ToString() }, { "os_64bit", Environment.Is64BitOperatingSystem }, { "process_64bit", Environment.Is64BitProcess },
                { "session_id", process.SessionId }, { "terminal_server_session", SystemInformation.TerminalServerSession }, { "interactive", Environment.UserInteractive },
                { "executable_path", assembly.Location }, { "assembly_version", assembly.GetName().Version.ToString() },
                { "clr_version", Environment.Version.ToString() }, { "snapshot_worker_apartment", Thread.CurrentThread.GetApartmentState().ToString() }
            };
        }
        internal virtual Dictionary<string, object> RegistryValues(string hive, string path, string[] names)
        {
            RegistryKey root = hive == "HKLM" ? Registry.LocalMachine : hive == "HKCU" ? Registry.CurrentUser : null;
            if (root == null) throw new ArgumentException("Unsupported diagnostic registry hive.");
            using (RegistryKey key = root.OpenSubKey(path, false))
            {
                if (key == null) return null;
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string name in names) { object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); if (value != null) result[name] = value; }
                return result;
            }
        }
        internal virtual Dictionary<string, object> FileMetadata(string path)
        {
            var file = new FileInfo(path); FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            return new Dictionary<string, object> { { "bytes", file.Length }, { "file_version", version.FileVersion }, { "product_version", version.ProductVersion },
                { "company", version.CompanyName }, { "description", version.FileDescription }, { "last_write_utc", file.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture) } };
        }
        internal virtual CaptureProcessPresence[] ProcessesByName(string name)
        {
            if (!CaptureEnvironmentDiagnostics.IsAllowedProcessName(name)) throw new ArgumentException("Process is outside diagnostic allowlist.");
            Process[] processes = Process.GetProcessesByName(name); var result = new List<CaptureProcessPresence>();
            try
            {
                for (int index = 0; index < Math.Min(4, processes.Length); index++)
                {
                    Process process = processes[index]; var item = new CaptureProcessPresence { Name = name, ProcessId = process.Id };
                    try { item.SessionId = process.SessionId; item.SessionQueryHresult = "0x00000000"; }
                    catch (Exception error) { item.SessionQueryHresult = "0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture); }
                    result.Add(item);
                }
            }
            finally { foreach (Process process in processes) process.Dispose(); }
            return result.ToArray();
        }
    }
}
