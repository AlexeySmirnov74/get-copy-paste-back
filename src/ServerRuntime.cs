using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HorizonClient
{
    internal sealed class ServerConnection
    {
        public string RootDirectory { get; internal set; }
        public string KeyPath { get; internal set; }
        public int ProcessId { get; internal set; }
        public string StartTimeUtcTicks { get; internal set; }
        public string CameraSerial { get; internal set; }
        public string CameraName { get; internal set; }
        public bool RequiresUpgrade { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public int FPS { get; internal set; }
        public bool Uncompressed { get; internal set; }
        public bool H264Probe { get; internal set; }
        public bool MetadataRecoveryRequired { get; internal set; }
    }

    internal sealed class ServerRuntime
    {
        internal const string DefaultCameraName = "4k webcam";
        private const int MaximumJsonBytes = 65536;
        private static readonly string[,] Resources = {
            { "HorizonServer.runtime.usbbridge-camera.exe", "runtime\\usbbridge-camera.exe" },
            { "HorizonServer.defaults.ServerDefaults.json", "defaults\\ServerDefaults.json" },
            { "HorizonServer.DriverInstaller.exe", "runtime\\drivers\\USBip-0.9.8.1-x64.exe" }
        };

        public string RootDirectory { get; private set; }
        private readonly object ownedHelperGate = new object();
        private readonly List<Process> ownedHelpers = new List<Process>();
        private readonly Func<ProcessStartInfo, Process> helperStarter = Process.Start;

        public ServerRuntime()
        {
            // Constructing the GUI must not extract files, create a camera or
            // request elevation. All work starts with the explicit Create action.
            RootDirectory = ServerPaths.Current.Root;
        }

        internal ServerRuntime(Func<ProcessStartInfo, Process> helperStarter) : this()
        {
            if (helperStarter == null) throw new ArgumentNullException("helperStarter");
            this.helperStarter = helperStarter;
        }

        public bool HasSavedCameraMetadata()
        {
            string data = Path.Combine(RootDirectory, "data");
            return File.Exists(Path.Combine(data, "camera.local.json")) ||
                File.Exists(Path.Combine(data, "camera-process.local.json")) ||
                File.Exists(Path.Combine(data, "camera.local.status.json"));
        }

        public ServerConnection PrepareCamera(string cameraName, Action<string> progress)
        { return PrepareCameraCore(cameraName, 0, 0, 0, false, false, progress); }

        public ServerConnection PrepareCamera(string cameraName, int width, int height, int fps, bool uncompressed, bool h264Probe, Action<string> progress)
        {
            ValidateVideoMode(width, height, fps, uncompressed, h264Probe);
            return PrepareCameraCore(cameraName, width, height, fps, uncompressed, h264Probe, progress);
        }

        private ServerConnection PrepareCameraCore(string cameraName, int width, int height, int fps, bool uncompressed, bool h264Probe, Action<string> progress)
        {
            ServerConnection existing = FindExistingCamera();
            bool explicitMode = width != 0;
            if (existing != null && existing.ProcessId > 0 && !existing.RequiresUpgrade &&
                (!explicitMode || existing.Width == width && existing.Height == height && existing.FPS == fps && existing.Uncompressed == uncompressed && existing.H264Probe == h264Probe)) return existing;
            if (existing != null) cameraName = existing.CameraName;
            cameraName = ValidateCameraName(cameraName);
            Report(progress, UiText.Get("Подготовка компонентов камеры…", "Preparing camera components…"));
            ExtractResources(RootDirectory);
            string data = Path.Combine(RootDirectory, "data");
            EnsureDirectory(data);
            string key = Path.Combine(data, "camera.local.key");
            if (!File.Exists(key))
            {
                // Generate under the interactive account before UAC so the
                // sender remains the key owner. Key bytes never enter our output.
                string ignored;
                int keyExit = RunCaptured(Path.Combine(RootDirectory, "runtime", "usbbridge-camera.exe"),
                    "keygen " + QuoteArgument(key), RootDirectory, 30000, out ignored);
                if (keyExit != 0 || !File.Exists(key))
                    throw new IOException(UiText.Get("Не удалось создать ключ подключения камеры.", "Could not create the camera connection key."));
            }
            string resultPath = Path.Combine(data, "setup-result.json");
            if (File.Exists(resultPath)) File.Delete(resultPath);
            Report(progress, UiText.Get("Подтвердите настройку камеры и драйвера в запросе Windows.", "Approve camera and driver setup in the Windows prompt."));
            try
            {
                var start = new ProcessStartInfo {
                    FileName = Assembly.GetExecutingAssembly().Location,
                    Arguments = BuildSetupArguments(RootDirectory, cameraName, width, height, fps, uncompressed, h264Probe),
                    WorkingDirectory = RootDirectory,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process child = StartOwnedHelper(start);
                try
                {
                    if (child == null) throw new IOException(UiText.Get("Не удалось запустить настройку камеры.", "Could not start camera setup."));
                    Report(progress, UiText.Get("Настройка драйвера и камеры…", "Setting up the driver and camera…"));
                    if (!child.WaitForExit(600000))
                        throw new TimeoutException(UiText.Get("Настройка ещё выполняется. Дождитесь завершения установщика Windows и повторите создание камеры.", "Setup is still running. Wait for the Windows installer to finish, then try creating the camera again."));
                    LogDriverRepairResult(RootDirectory);
                    if (!File.Exists(resultPath))
                        throw new IOException(UiText.Get("Настройка не вернула результат. Запустите Server.exe под тем же пользователем Windows, который подтверждает запрос администратора.", "Setup returned no result. Run Server.exe as the same Windows user who approves the administrator prompt."));
                    Dictionary<string, object> result = ReadJson(resultPath);
                    if (child.ExitCode != 0 || !GetBoolean(result, "Success"))
                        throw OperationFailure(result, UiText.Get("Не удалось создать камеру.", "Could not create the camera."));
                    return ReadConnection(result, null);
                }
                finally { ReleaseOwnedHelper(child); }
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode == 1223)
                    throw new IOException(UiText.Get("Создание камеры отменено в запросе Windows.", "Camera creation was cancelled in the Windows prompt."));
                throw new IOException(UiText.Get("Не удалось запустить настройку камеры: ", "Could not start camera setup: ") + error.Message);
            }
        }

        // Read embedded verification code rather than a possibly outdated script
        // left by an earlier deployment. This does not extract or create files.
        public ServerConnection FindExistingCamera()
        {
            string command = EmbeddedHelperCommand() +
                "$found = Resolve-HorizonCameraBackend -ExpectedExecutable " +
                PowerShellLiteral(Path.Combine(RootDirectory, "runtime", "usbbridge-camera.exe")) +
                " -ExpectedBinaryHash " + PowerShellLiteral(EmbeddedBackendHash()) +
                "; if ($null -eq $found) { $found=Resolve-HorizonSavedCameraIdentity -Root " + PowerShellLiteral(RootDirectory) +
                " }; if ($null -eq $found) { 'null' } else { $found | ConvertTo-Json -Depth 6 -Compress }";
            string output;
            int exitCode = RunCaptured(PowerShellPath(), EncodedPowerShell(command),
                AppDomain.CurrentDomain.BaseDirectory, 30000, out output);
            if (exitCode != 0) throw new IOException(UiText.Get("Не удалось проверить существующую камеру: ", "Could not check the existing camera: ") + output.Trim());
            if (output.Trim() == "null") return null;
            Dictionary<string, object> backend = DeserializeJson(output);
            var values = BackendConnectionValues(backend);
            ServerConnection connection = ReadConnection(values, null, true);
            connection.RequiresUpgrade = !connection.MetadataRecoveryRequired && !GetBoolean(backend, "MatchesBinary");
            DiagnosticLog.Write("server.camera.discovery", "camera_exists", true, "camera_running", connection.ProcessId > 0,
                "metadata_recovery_required", connection.MetadataRecoveryRequired, "camera_name", connection.CameraName,
                "width", connection.Width, "height", connection.Height, "fps", connection.FPS);
            return connection;
        }

        public ServerConnection ChangeVideoMode(int width, int height, int fps, bool uncompressed, Action<string> progress)
        {
            return ChangeVideoMode(width, height, fps, uncompressed, false, progress);
        }

        public ServerConnection ChangeVideoMode(int width, int height, int fps, bool uncompressed, bool h264Probe, Action<string> progress)
        {
            ValidateVideoMode(width, height, fps, uncompressed, h264Probe);
            ServerConnection existing = FindExistingCamera();
            if (existing == null) throw new IOException(UiText.Get("Сначала создайте камеру.", "Create a camera first."));
            if (existing.ProcessId > 0 && !existing.RequiresUpgrade && existing.Width == width && existing.Height == height && existing.FPS == fps && existing.Uncompressed == uncompressed && existing.H264Probe == h264Probe) return existing;
            Report(progress, UiText.Get("Подготовка нового видеорежима…", "Preparing the new video format…"));
            ExtractResources(RootDirectory);
            string data = Path.Combine(RootDirectory, "data");
            EnsureDirectory(data);
            string key = Path.Combine(data, "camera.local.key");
            if (!File.Exists(key))
            {
                string ignored;
                int exitCode = RunCaptured(Path.Combine(RootDirectory, "runtime", "usbbridge-camera.exe"), "keygen " + QuoteArgument(key), RootDirectory, 30000, out ignored);
                if (exitCode != 0 || !File.Exists(key)) throw new IOException(UiText.Get("Не удалось создать ключ подключения камеры.", "Could not create the camera connection key."));
            }
            string resultPath = Path.Combine(data, "configure-result.json");
            if (File.Exists(resultPath)) File.Delete(resultPath);
            Report(progress, UiText.Get("Подтвердите изменение видеорежима в запросе Windows.", "Approve the video format change in the Windows prompt."));
            try
            {
                var start = new ProcessStartInfo {
                    FileName = Assembly.GetExecutingAssembly().Location,
                    Arguments = "--configure-server " + QuoteArgument(RootDirectory) + " " + width + " " + height + " " + fps + " " + (uncompressed ? "1" : "0") + " " + (h264Probe ? "1" : "0"),
                    WorkingDirectory = RootDirectory, UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
                };
                Process child = StartOwnedHelper(start);
                try
                {
                    if (child == null) throw new IOException(UiText.Get("Не удалось запустить настройку видеорежима.", "Could not start video format setup."));
                    if (!child.WaitForExit(600000)) throw new TimeoutException(UiText.Get("Настройка видеорежима ещё выполняется.", "Video format setup is still running."));
                    LogDriverRepairResult(RootDirectory);
                    if (!File.Exists(resultPath)) throw new IOException(UiText.Get("Настройка видеорежима не вернула результат.", "Video format setup returned no result."));
                    Dictionary<string, object> result = ReadJson(resultPath);
                    if (child.ExitCode != 0 || !GetBoolean(result, "Success")) throw OperationFailure(result, UiText.Get("Не удалось изменить видеорежим.", "Could not change the video format."));
                    return ReadConnection(result, null);
                }
                finally { ReleaseOwnedHelper(child); }
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode == 1223) throw new IOException(UiText.Get("Изменение видеорежима отменено в запросе Windows.", "The video format change was cancelled in the Windows prompt."));
                throw new IOException(UiText.Get("Не удалось запустить настройку видеорежима: ", "Could not start video format setup: ") + error.Message);
            }
        }

        public static int RunConfigure(string rootDirectory, int width, int height, int fps, bool uncompressed)
        {
            return RunConfigure(rootDirectory, width, height, fps, uncompressed, false);
        }

        public static int RunConfigure(string rootDirectory, int width, int height, int fps, bool uncompressed, bool h264Probe)
        {
            string resultPath = null;
            try
            {
                ValidateVideoMode(width, height, fps, uncompressed, h264Probe);
                string root = CanonicalPath(rootDirectory);
                if (!String.Equals(root, CanonicalPath(new ServerRuntime().RootDirectory), StringComparison.OrdinalIgnoreCase)) throw new IOException(UiText.Get("Настройку нужно запускать под тем же пользователем Windows.", "Run setup as the same Windows user."));
                EnsureDirectory(root); EnsureDirectory(Path.Combine(root, "data"));
                resultPath = Path.Combine(root, "data", "configure-result.json");
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new IOException(UiText.Get("Для настройки видеорежима нужны права администратора Windows.", "Windows administrator rights are required to change the video format."));
                ExtractResources(root);
                CameraDriverRepair.RunElevated(root);
                CameraDriverSafety.EnsureReconfigurationAllowed();
                DriverInstaller.EnsureInstalled(root, null);
                string command = EmbeddedScriptCommand("Setup-Camera.ps1") + " -RuntimeRoot " + PowerShellLiteral(root) +
                    " -CameraRuntime ([ScriptBlock]::Create((Read-HorizonEmbeddedScript 'Camera-Runtime.ps1'))) -PassThru -TakeOwnership -ConfigureMode -Width " + width +
                    " -Height " + height + " -FramesPerSecond " + fps + " -Uncompressed:$" + (uncompressed ? "true" : "false") + " -H264Probe:$" + (h264Probe ? "true" : "false") +
                    " -ExpectedBinaryHash " + PowerShellLiteral(EmbeddedBackendHash()) + " | ConvertTo-Json -Depth 6 -Compress";
                string output;
                int exitCode = RunCaptured(PowerShellPath(), EncodedPowerShell(command), root, 120000, out output, true);
                if (exitCode != 0) throw new IOException(UiText.Get("Ошибка настройки видеорежима: ", "Video format setup error: ") + output.Trim());
                var result = BackendConnectionValues(DeserializeJson(output)); result["Success"] = true;
                ReadConnection(result, null); WriteJson(resultPath, result);
                return 0;
            }
            catch (Exception error)
            {
                DiagnosticLog.Error("server.helper.operation_failed", error, "operation", "configure", "root", rootDirectory);
                if (resultPath != null)
                {
                    try { WriteOperationFailure(resultPath, error); } catch { }
                }
                return 1;
            }
        }

        public void RemoveCamera(Action<string> progress)
        {
            ExtractResources(RootDirectory);
            string data = Path.Combine(RootDirectory, "data");
            EnsureDirectory(data);
            string resultPath = Path.Combine(data, "remove-result.json");
            if (File.Exists(resultPath)) File.Delete(resultPath);
            Report(progress, UiText.Get("Подтвердите удаление камеры в запросе Windows.", "Approve camera removal in the Windows prompt."));
            try
            {
                var start = new ProcessStartInfo {
                    FileName = Assembly.GetExecutingAssembly().Location,
                    Arguments = "--remove-server " + QuoteArgument(RootDirectory),
                    WorkingDirectory = RootDirectory, UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
                };
                Process child = StartOwnedHelper(start);
                try
                {
                    if (child == null) throw new IOException(UiText.Get("Не удалось запустить удаление камеры.", "Could not start camera removal."));
                    if (!child.WaitForExit(240000)) throw new TimeoutException(UiText.Get("Удаление камеры ещё выполняется. Дождитесь завершения операции Windows.", "Camera removal is still running. Wait for the Windows operation to finish."));
                    LogDriverRepairResult(RootDirectory);
                    if (!File.Exists(resultPath)) throw new IOException(UiText.Get("Удаление камеры не вернуло результат.", "Camera removal returned no result."));
                    Dictionary<string, object> result = ReadJson(resultPath);
                    if (child.ExitCode != 0 || !GetBoolean(result, "Success"))
                        throw OperationFailure(result, UiText.Get("Не удалось удалить камеру.", "Could not remove the camera."));
                }
                finally { ReleaseOwnedHelper(child); }
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode == 1223) throw new IOException(UiText.Get("Удаление камеры отменено в запросе Windows.", "Camera removal was cancelled in the Windows prompt."));
                throw new IOException(UiText.Get("Не удалось запустить удаление камеры: ", "Could not start camera removal: ") + error.Message);
            }
        }

        public static int RunRemove(string rootDirectory)
        {
            string resultPath = null;
            try
            {
                string root = CanonicalPath(rootDirectory);
                if (!String.Equals(root, CanonicalPath(new ServerRuntime().RootDirectory), StringComparison.OrdinalIgnoreCase))
                    throw new IOException(UiText.Get("Удаление нужно запускать из Server.exe под тем же пользователем Windows.", "Start camera removal from Server.exe as the same Windows user."));
                EnsureDirectory(root); EnsureDirectory(Path.Combine(root, "data"));
                resultPath = Path.Combine(root, "data", "remove-result.json");
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                        throw new IOException(UiText.Get("Для удаления камеры нужны права администратора Windows.", "Windows administrator rights are required to remove the camera."));
                bool metadataOnly = false;
                ExtractResources(root);
                try
                {
                    CameraDriverRepair.RunElevated(root);
                    CameraDriverSafety.EnsureReconfigurationAllowed();
                }
                catch (IOException error)
                {
                    // Cleaning a stopped camera's saved identity does not change
                    // USB devices. The script refuses any active endpoint and
                    // verifies that the exact old PnP root is absent.
                    metadataOnly = true;
                    DiagnosticLog.Error("server.camera.remove_metadata_only", error);
                }
                string command = EmbeddedScriptCommand("Stop-Camera.ps1") + " -RuntimeRoot " + PowerShellLiteral(root) +
                    " -CameraRuntime ([ScriptBlock]::Create((Read-HorizonEmbeddedScript 'Camera-Runtime.ps1'))) -Remove -AnyOwnedRoot -PassThru" +
                    (metadataOnly ? " -MetadataOnly" : String.Empty) + " | ConvertTo-Json -Compress";
                string output;
                int exitCode = RunCaptured(PowerShellPath(), EncodedPowerShell(command), root, 90000, out output, true);
                if (exitCode != 0) throw new IOException(UiText.Get("Ошибка удаления камеры: ", "Camera removal error: ") + output.Trim());
                DeserializeJson(output);
                WriteJson(resultPath, new Dictionary<string, object> { { "Success", true } });
                return 0;
            }
            catch (Exception error)
            {
                DiagnosticLog.Error("server.helper.operation_failed", error, "operation", "remove", "root", rootDirectory);
                if (resultPath != null)
                {
                    try
                    {
                        WriteOperationFailure(resultPath, error);
                    }
                    catch { }
                }
                return 1;
            }
        }

        // Exit stops only the exact connection accepted by this server. A later
        // receiver or another installation can never be selected by PID alone.
        // The backend inherits setup's elevated token, so a narrow UAC helper
        // stops it. It never installs/repairs drivers or removes metadata/keys.
        internal bool StopOwnedCamera(ServerConnection expected)
        {
            WaitForOwnedHelpers();
            if (expected == null)
            {
                // Used only after this GUI attempted setup but could not read
                // its result. Never adopt a legacy/foreign runtime for cleanup.
                expected = FindExistingCamera();
                if (expected == null || !String.Equals(CanonicalPath(expected.RootDirectory), CanonicalPath(RootDirectory), StringComparison.OrdinalIgnoreCase)) return false;
            }
            long started;
            if (expected.ProcessId <= 0 || expected.MetadataRecoveryRequired ||
                !Int64.TryParse(expected.StartTimeUtcTicks, out started) || started <= 0 ||
                String.IsNullOrEmpty(expected.CameraSerial)) return false;
            string root = CanonicalPath(expected.RootDirectory);
            if (!String.Equals(CanonicalPath(expected.KeyPath), Path.Combine(root, "data", "camera.local.key"), StringComparison.OrdinalIgnoreCase))
                throw new IOException(UiText.Get("Не удалось подтвердить путь ключа камеры при завершении работы.", "The camera key path could not be verified for shutdown."));
            string command = "$horizonStopResult=& { " + EmbeddedScriptCommand("Stop-Camera.ps1") + " -RuntimeRoot " + PowerShellLiteral(root) +
                " -CameraRuntime ([ScriptBlock]::Create((Read-HorizonEmbeddedScript 'Camera-Runtime.ps1'))) -PassThru" +
                " -ExpectedProcessId " + expected.ProcessId + " -ExpectedStartTimeUtcTicks " + PowerShellLiteral(expected.StartTimeUtcTicks) +
                " -ExpectedKeyPath " + PowerShellLiteral(expected.KeyPath) + " -ExpectedCameraSerial " + PowerShellLiteral(expected.CameraSerial) +
                " -ExpectedBinaryHash " + PowerShellLiteral(EmbeddedBackendHash()) +
                " }; if ($horizonStopResult.Stopped -eq $true) { exit 0 } else { exit 2 }";
            var start = new ProcessStartInfo(PowerShellPath(), EncodedPowerShell(command)) {
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory, UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            };
            Process child = null;
            try
            {
                child = StartOwnedHelper(start);
                if (child == null) throw new IOException(UiText.Get("Не удалось запустить остановку камеры.", "The camera shutdown helper did not start."));
                if (!child.WaitForExit(45000))
                {
                    DiagnosticLog.Write("server.camera.shutdown.slow_waiting", "process_id", child.Id);
                    while (!child.WaitForExit(1000)) { }
                }
                if (child.ExitCode == 0) return true;
                if (child.ExitCode == 2) return false;
                throw new IOException(UiText.Get("Не удалось остановить процесс камеры. Код: ", "Could not stop the camera process. Exit code: ") + child.ExitCode);
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode == 1223) throw new IOException(UiText.Get("Остановка камеры отменена в запросе Windows.", "Camera shutdown was cancelled in the Windows prompt."));
                throw;
            }
            finally { ReleaseOwnedHelper(child); }
        }

        private Process StartOwnedHelper(ProcessStartInfo start)
        {
            Process child = helperStarter(start);
            if (child != null) lock (ownedHelperGate) ownedHelpers.Add(child);
            return child;
        }

        private void ReleaseOwnedHelper(Process child)
        {
            if (child == null) return;
            lock (ownedHelperGate)
            {
                if (!ownedHelpers.Contains(child) || !child.HasExited) return;
                ownedHelpers.Remove(child); child.Dispose();
            }
        }

        internal bool HasOwnedHelpers { get { lock (ownedHelperGate) return ownedHelpers.Count != 0; } }

        internal void WaitForOwnedHelpers()
        {
            bool announced = false;
            while (true)
            {
                lock (ownedHelperGate)
                {
                    if (ownedHelpers.Count == 0) return;
                    Process child = ownedHelpers[0];
                    if (child.WaitForExit(1000)) { ownedHelpers.RemoveAt(0); child.Dispose(); continue; }
                }
                if (!announced) { DiagnosticLog.Write("server.helper.exit_wait", "reason", "owned_setup_still_running"); announced = true; }
            }
        }

        private static string EmbeddedHelperCommand()
        {
            return EmbeddedScriptLoaderCommand() + ". ([ScriptBlock]::Create((Read-HorizonEmbeddedScript 'Camera-Runtime.ps1'))); ";
        }

        // PowerShell reads large sources directly from this executable. Only
        // this small loader and literal arguments cross the command-line limit;
        // no .ps1 is extracted or read from an earlier installation.
        internal static string EmbeddedScriptCommand(string scriptName)
        {
            if (scriptName != "Setup-Camera.ps1" && scriptName != "Stop-Camera.ps1" &&
                scriptName != "Camera-Runtime.ps1" && scriptName != "Repair-CameraDriver.ps1")
                throw new ArgumentException("Unknown embedded camera script.", "scriptName");
            return EmbeddedScriptLoaderCommand() + "& ([ScriptBlock]::Create((Read-HorizonEmbeddedScript " + PowerShellLiteral(scriptName) + ")))";
        }

        private static string EmbeddedScriptLoaderCommand()
        {
            return "$ErrorActionPreference='Stop'; $horizonScriptAssembly=[Reflection.Assembly]::LoadFile(" +
                PowerShellLiteral(Assembly.GetExecutingAssembly().Location) + "); " +
                "function Read-HorizonEmbeddedScript([string]$name) { " +
                "if (@('Setup-Camera.ps1','Stop-Camera.ps1','Camera-Runtime.ps1','Repair-CameraDriver.ps1') -cnotcontains $name) { throw 'Unknown embedded camera script.' }; " +
                "$stream=$horizonScriptAssembly.GetManifestResourceStream('HorizonServer.scripts.'+$name); " +
                "if ($null -eq $stream) { throw ('Embedded camera script is missing: '+$name) }; " +
                "$reader=[IO.StreamReader]::new($stream,[Text.Encoding]::UTF8); try { $reader.ReadToEnd() } finally { $reader.Dispose() } }; ";
        }

        private static string EmbeddedBackendHash()
        {
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("HorizonServer.runtime.usbbridge-camera.exe"))
            using (var sha = SHA256.Create())
            {
                if (source == null) throw new IOException(UiText.Get("В Server.exe отсутствует компонент камеры.", "The camera component is missing from Server.exe."));
                return BitConverter.ToString(sha.ComputeHash(source)).Replace("-", String.Empty);
            }
        }

        private static string EncodedPowerShell(string command)
        {
            // Keep progress records and CLIXML out of JSON results and error
            // messages. Report only the script's contextual exception message.
            string wrapped = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $OutputEncoding=[Console]::OutputEncoding; " +
                "$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; try { " + command +
                " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
            return "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        }

        // Invoked by Program before the GUI mutex or message loop. This process
        // is elevated; the normal GUI and clipboard sender remain unelevated.
        public static int RunSetup(string rootDirectory, string cameraName)
        { return RunSetupCore(rootDirectory, cameraName, 0, 0, 0, false, false); }

        public static int RunSetup(string rootDirectory, string cameraName, int width, int height, int fps, bool uncompressed, bool h264Probe)
        {
            ValidateVideoMode(width, height, fps, uncompressed, h264Probe);
            return RunSetupCore(rootDirectory, cameraName, width, height, fps, uncompressed, h264Probe);
        }

        private static int RunSetupCore(string rootDirectory, string cameraName, int width, int height, int fps, bool uncompressed, bool h264Probe)
        {
            string resultPath = null;
            try
            {
                var runtime = new ServerRuntime();
                string root = CanonicalPath(rootDirectory);
                if (!String.Equals(root, CanonicalPath(runtime.RootDirectory), StringComparison.OrdinalIgnoreCase))
                    throw new IOException(UiText.Get("Настройку нужно запускать из Server.exe под тем же пользователем Windows.", "Start setup from Server.exe as the same Windows user."));
                cameraName = ValidateCameraName(cameraName);
                EnsureDirectory(root);
                EnsureDirectory(Path.Combine(root, "data"));
                resultPath = Path.Combine(root, "data", "setup-result.json");
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                        throw new IOException(UiText.Get("Для настройки камеры нужны права администратора Windows.", "Windows administrator rights are required to set up the camera."));
                // Restore pinned native components after elevation as well;
                // scripts always execute directly from this executable.
                ExtractResources(root);
                CameraDriverRepair.RunElevated(root);
                CameraDriverSafety.EnsureReconfigurationAllowed();
                DriverInstaller.EnsureInstalled(root, null);
                string command = BuildSetupCommand(root, cameraName, width, height, fps, uncompressed, h264Probe);
                string output;
                int exitCode = RunCaptured(PowerShellPath(), EncodedPowerShell(command), root, 120000, out output, true);
                if (exitCode != 0) throw new IOException(UiText.Get("Ошибка настройки камеры: ", "Camera setup error: ") + output.Trim());
                Dictionary<string, object> backend = DeserializeJson(output);
                var result = BackendConnectionValues(backend); result["Success"] = true;
                ReadConnection(result, null);
                WriteJson(resultPath, result);
                return 0;
            }
            catch (Exception error)
            {
                DiagnosticLog.Error("server.helper.operation_failed", error, "operation", "setup", "root", rootDirectory);
                if (resultPath != null)
                {
                    try
                    {
                        WriteOperationFailure(resultPath, error);
                    }
                    catch { }
                }
                return 1;
            }
        }

        // Pure argument builders keep both the UAC and PowerShell boundaries
        // reviewable without launching setup, opening a camera or changing OS state.
        internal static string BuildSetupArguments(string root, string cameraName, int width, int height, int fps, bool uncompressed, bool h264Probe)
        {
            bool explicitMode = width != 0 || height != 0 || fps != 0 || uncompressed || h264Probe;
            if (explicitMode) ValidateVideoMode(width, height, fps, uncompressed, h264Probe);
            string arguments = "--setup-server " + QuoteArgument(root) + " " + QuoteArgument(ValidateCameraName(cameraName));
            return explicitMode ? arguments + " " + width + " " + height + " " + fps + " " + (uncompressed ? "1" : "0") + " " + (h264Probe ? "1" : "0") : arguments;
        }

        internal static string BuildSetupCommand(string root, string cameraName, int width, int height, int fps, bool uncompressed, bool h264Probe)
        {
            bool explicitMode = width != 0 || height != 0 || fps != 0 || uncompressed || h264Probe;
            if (explicitMode) ValidateVideoMode(width, height, fps, uncompressed, h264Probe);
            string mode = explicitMode ? " -Width " + width + " -Height " + height + " -FramesPerSecond " + fps +
                " -Uncompressed:$" + (uncompressed ? "true" : "false") + " -H264Probe:$" + (h264Probe ? "true" : "false") : "";
            return EmbeddedScriptCommand("Setup-Camera.ps1") + " -RuntimeRoot " + PowerShellLiteral(root) +
                " -CameraRuntime ([ScriptBlock]::Create((Read-HorizonEmbeddedScript 'Camera-Runtime.ps1')))" +
                " -CameraName " + PowerShellLiteral(ValidateCameraName(cameraName)) + " -PassThru -TakeOwnership" + mode +
                " -ExpectedBinaryHash " + PowerShellLiteral(EmbeddedBackendHash()) + " | ConvertTo-Json -Depth 6 -Compress";
        }

        private static IOException OperationFailure(Dictionary<string, object> result, string fallback)
        {
            if (GetBoolean(result, "DriverRestartRequired"))
                return new DriverRestartRequiredException(GetInteger(result, "InstallerExitCode"), GetString(result, "RestartReason", "installer_requested_restart"));
            return GetBoolean(result, "RestartRequired") ? new CameraDriverRestartRequiredException() :
                new IOException(GetString(result, "Error", fallback));
        }

        private static void LogDriverRepairResult(string root)
        {
            string path = Path.Combine(root, "data", "camera-driver-repair-result.json");
            if (!File.Exists(path)) return;
            try
            {
                Dictionary<string, object> result = ReadJson(path);
                DiagnosticLog.Write("server.camera.driver_repair.result", "success", GetBoolean(result, "Success"),
                    "changed", GetBoolean(result, "Changed"), "restart_required", GetBoolean(result, "RestartRequired"),
                    "operation", GetString(result, "Operation", null), "release_reason", GetString(result, "ReleaseReason", null),
                    "error", GetString(result, "Error", null), "recorded_utc", GetString(result, "RecordedUtc", null),
                    "result_path", path);
            }
            catch (Exception error) { DiagnosticLog.Error("server.camera.driver_repair.result_unreadable", error); }
        }

        private static void WriteOperationFailure(string path, Exception error)
        {
            string message = error.Message;
            if (message.Length > 4000) message = message.Substring(0, 4000);
            WriteJson(path, new Dictionary<string, object> {
                { "Success", false }, { "Error", message },
                { "RestartRequired", error is CameraDriverRestartRequiredException || error is DriverRestartRequiredException },
                { "DriverRestartRequired", error is DriverRestartRequiredException },
                { "InstallerExitCode", error is DriverRestartRequiredException ? ((DriverRestartRequiredException)error).InstallerExitCode : 0 },
                { "RestartReason", error is DriverRestartRequiredException ? ((DriverRestartRequiredException)error).Reason : null }
            });
        }

        internal static string ValidateCameraName(string value)
        {
            value = (value ?? String.Empty).Trim();
            if (value.Length == 0 || value.Length > 100)
                throw new ArgumentException(UiText.Get("Введите название камеры длиной от 1 до 100 символов.", "Enter a camera name between 1 and 100 characters long."));
            foreach (char character in value)
                if (Char.IsControl(character)) throw new ArgumentException(UiText.Get("Название камеры содержит управляющий символ.", "The camera name contains a control character."));
            return value;
        }

        internal static void ValidateVideoMode(int width, int height, int fps, bool uncompressed)
        {
            ValidateVideoMode(width, height, fps, uncompressed, false);
        }

        internal static void ValidateVideoMode(int width, int height, int fps, bool uncompressed, bool h264Probe)
        {
            ValidateReadableVideoMode(width, height, fps, uncompressed, h264Probe);
            if (!IsSupportedVideoMode(width, height, fps, uncompressed, h264Probe))
                throw new ArgumentException(UiText.Get("Доступны только форматы камеры 640×480, 1280×720 и 1920×1080 YUY2.", "Only 640×480, 1280×720 and 1920×1080 YUY2 camera formats are available."));
        }

        internal static bool IsSupportedVideoMode(int width, int height, int fps, bool uncompressed, bool h264Probe)
        {
            return uncompressed && !h264Probe && fps >= 1 && fps <= 60 &&
                (width == 640 && height == 480 || width == 1280 && height == 720 || width == 1920 && height == 1080);
        }

        // Old camera identities must remain verifiable so they can be migrated
        // in place or removed without changing their name, serial or key.
        private static void ValidateReadableVideoMode(int width, int height, int fps, bool uncompressed, bool h264Probe)
        {
            if (h264Probe && (uncompressed || width != 640 || height != 480 || fps != 30))
                throw new ArgumentException("H264 probe requires 640x480 at 30 FPS without YUY2.");
            if (width < 16 || width > 7680 || height < 16 || height > 4320 || (width & 1) != 0 || (height & 1) != 0 || fps < 1 || fps > 60 ||
                (uncompressed && (long)width * height * 16 * fps > UInt32.MaxValue))
                throw new ArgumentException(UiText.Get("Недопустимое разрешение, частота кадров или битрейт YUY2.", "Invalid YUY2 resolution, frame rate or bit rate."));
        }

        private static Dictionary<string, object> BackendConnectionValues(Dictionary<string, object> backend)
        {
            return new Dictionary<string, object> {
                { "RootDirectory", GetString(backend, "Root", null) }, { "KeyPath", GetString(backend, "KeyPath", null) },
                { "ProcessId", GetInteger(backend, "ProcessId") }, { "CameraName", GetString(backend, "CameraName", null) },
                { "StartTimeUtcTicks", GetString(backend, "StartTimeUtcTicks", null) }, { "CameraSerial", GetString(backend, "CameraSerial", null) },
                { "Width", GetInteger(backend, "Width") }, { "Height", GetInteger(backend, "Height") },
                { "FPS", GetInteger(backend, "FPS") }, { "Uncompressed", GetBoolean(backend, "Uncompressed") },
                { "H264Probe", GetBoolean(backend, "H264Probe") },
                { "MetadataRecoveryRequired", GetBoolean(backend, "MetadataRecoveryRequired") }
            };
        }

        private static ServerConnection ReadConnection(Dictionary<string, object> result, string expectedRoot, bool allowStopped = false)
        {
            string root = CanonicalPath(GetString(result, "RootDirectory", null));
            string key = CanonicalPath(GetString(result, "KeyPath", null));
            int processId = GetInteger(result, "ProcessId");
            int width = GetInteger(result, "Width"), height = GetInteger(result, "Height"), fps = GetInteger(result, "FPS");
            bool uncompressed = GetBoolean(result, "Uncompressed");
            bool h264Probe = GetBoolean(result, "H264Probe");
            bool metadataRecoveryRequired = GetBoolean(result, "MetadataRecoveryRequired");
            ValidateReadableVideoMode(width, height, fps, uncompressed, h264Probe);
            if ((expectedRoot != null && !String.Equals(root, CanonicalPath(expectedRoot), StringComparison.OrdinalIgnoreCase)) ||
                !String.Equals(key, Path.Combine(root, "data", "camera.local.key"), StringComparison.OrdinalIgnoreCase) || processId < 0 || (!allowStopped && processId == 0))
                throw new IOException(UiText.Get("Настройка вернула неподтверждённое подключение камеры.", "Setup returned an unverified camera connection."));
            if (metadataRecoveryRequired && processId != 0)
                throw new IOException(UiText.Get("Восстанавливаемые настройки не подтверждают работающий процесс камеры.", "The recovered settings do not identify a running camera process."));
            return new ServerConnection { RootDirectory = root, KeyPath = key, ProcessId = processId, StartTimeUtcTicks = GetString(result, "StartTimeUtcTicks", null), CameraSerial = GetString(result, "CameraSerial", null), CameraName = ValidateCameraName(GetString(result, "CameraName", null)), Width = width, Height = height, FPS = fps, Uncompressed = uncompressed, H264Probe = h264Probe, MetadataRecoveryRequired = metadataRecoveryRequired };
        }

        internal static void ExtractResources(string root)
        {
            root = CanonicalPath(root);
            EnsureDirectory(root);
            Assembly assembly = Assembly.GetExecutingAssembly();
            for (int index = 0; index < Resources.GetLength(0); index++)
            {
                string resourceName = Resources[index, 0];
                string destination = Path.GetFullPath(Path.Combine(root, Resources[index, 1]));
                if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException(UiText.Get("Недопустимый путь компонента камеры.", "Invalid camera component path."));
                EnsureDirectory(Path.GetDirectoryName(destination));
                RejectReparsePoint(destination);
                using (Stream source = assembly.GetManifestResourceStream(resourceName))
                {
                    if (source == null) throw new IOException(UiText.Get("В Server.exe отсутствует компонент: ", "A component is missing from Server.exe: ") + resourceName);
                    byte[] bytes;
                    using (var memory = new MemoryStream()) { source.CopyTo(memory); bytes = memory.ToArray(); }
                    bool nativeReceiver = resourceName == "HorizonServer.runtime.usbbridge-camera.exe";
                    string staged = Path.Combine(Path.GetDirectoryName(destination), "usbbridge-camera.next.exe");
                    if (File.Exists(destination) && FileMatches(destination, bytes))
                    {
                        if (nativeReceiver && File.Exists(staged)) { RejectReparsePoint(staged); File.Delete(staged); }
                        continue;
                    }
                    if (nativeReceiver && File.Exists(destination))
                    {
                        try
                        {
                            // Probe without writing: Windows denies write access
                            // to a running executable even when an atomic rename
                            // would succeed. Keep the verified process image and
                            // its on-disk hash consistent until setup stops it.
                            using (File.Open(destination, FileMode.Open, FileAccess.Write, FileShare.Read)) { }
                        }
                        catch (IOException) { WritePinnedFile(staged, bytes); continue; }
                    }
                    WritePinnedFile(destination, bytes);
                    if (nativeReceiver && File.Exists(staged)) { RejectReparsePoint(staged); File.Delete(staged); }
                }
            }
        }

        private static void WritePinnedFile(string destination, byte[] bytes)
        {
            RejectReparsePoint(destination);
            if (File.Exists(destination) && FileMatches(destination, bytes)) return;
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static bool FileMatches(string path, byte[] expected)
        {
            if (new FileInfo(path).Length != expected.Length) return false;
            using (var sha = SHA256.Create())
            using (var file = File.OpenRead(path))
            {
                byte[] actualHash = sha.ComputeHash(file);
                byte[] expectedHash = sha.ComputeHash(expected);
                for (int index = 0; index < actualHash.Length; index++)
                    if (actualHash[index] != expectedHash[index]) return false;
                return true;
            }
        }

        private static void EnsureDirectory(string path)
        {
            RejectReparsePoint(path);
            if (!Directory.Exists(path))
            {
                string parent = Path.GetDirectoryName(path);
                if (!String.IsNullOrEmpty(parent) && !Directory.Exists(parent)) EnsureDirectory(parent);
                Directory.CreateDirectory(path);
            }
            RejectReparsePoint(path);
        }

        private static void RejectReparsePoint(string path)
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(UiText.Get("Каталог компонентов камеры содержит перенаправление: ", "The camera component directory contains a redirection: ") + path);
        }

        private static string CanonicalPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new IOException(UiText.Get("Отсутствует путь компонентов камеры.", "The camera component path is missing."));
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        internal static string QuoteArgument(string value)
        {
            var result = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { backslashes++; continue; }
                if (character == '"') result.Append('\\', backslashes * 2 + 1).Append('"');
                else result.Append('\\', backslashes).Append(character);
                backslashes = 0;
            }
            return result.Append('\\', backslashes * 2).Append('"').ToString();
        }

        private static string PowerShellLiteral(string value) { return "'" + value.Replace("'", "''") + "'"; }
        private static string PowerShellPath() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"); }
        private static void Report(Action<string> progress, string message) { if (progress != null) progress(message); }

        internal static int RunCaptured(string executable, string arguments, string root, int timeout, out string output, bool waitForCompletionAfterTimeout = false)
        {
            var captured = new StringBuilder();
            var errors = new StringBuilder();
            var outputClosed = new TaskCompletionSource<bool>();
            var errorClosed = new TaskCompletionSource<bool>();
            var start = new ProcessStartInfo(executable, arguments) {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = new Process { StartInfo = start })
            {
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs data) {
                    if (data.Data == null) outputClosed.TrySetResult(true);
                    else AppendBounded(captured, data.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs data) {
                    if (data.Data == null) errorClosed.TrySetResult(true);
                    else AppendBounded(errors, data.Data);
                };
                bool started = false;
                try
                {
                    process.Start(); started = true;
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    if (!process.WaitForExit(timeout))
                    {
                        if (!waitForCompletionAfterTimeout)
                        {
                            CompleteCapturedChild(process, true);
                            throw new TimeoutException(UiText.Get("Компонент настройки не завершился вовремя. Дождитесь завершения операции Windows и повторите попытку.", "The setup component timed out. Wait for the Windows operation to finish, then try again."));
                        }
                        DiagnosticLog.Write("server.helper.slow_waiting", "process_id", process.Id, "timeout_ms", timeout);
                        while (!process.WaitForExit(1000)) { }
                    }
                    int exitCode = process.ExitCode;
                    // A persistent camera can inherit another copy of PowerShell's
                    // pipe handles even when its own stdio is redirected to log files.
                    // Parameterless WaitForExit also waits for pipe EOF and would
                    // therefore keep setup blocked until that camera exits.
                    if (!Task.WaitAll(new Task[] { outputClosed.Task, errorClosed.Task }, 1000))
                    {
                        try { process.CancelOutputRead(); } catch (InvalidOperationException) { }
                        try { process.CancelErrorRead(); } catch (InvalidOperationException) { }
                    }
                    lock (captured) { output = captured.ToString(); }
                    if (exitCode != 0) { lock (errors) { output += errors.ToString(); } }
                    return exitCode;
                }
                finally
                {
                    // Own the child even when setting up output capture fails.
                    // Queries/key generation may be terminated; installers must finish.
                    if (started) CompleteCapturedChild(process, !waitForCompletionAfterTimeout);
                }
            }
        }

        private static void CompleteCapturedChild(Process process, bool terminate)
        {
            if (process.HasExited) return;
            if (terminate)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { if (process.HasExited) return; }
                catch (Win32Exception error) { DiagnosticLog.Error("server.helper.termination_failed_waiting", error, "process_id", process.Id); }
            }
            // Timed waits observe process exit without waiting for inherited pipe EOF.
            // If Windows refuses termination, retain ownership until natural completion.
            while (!process.WaitForExit(1000)) { }
        }

        private static void AppendBounded(StringBuilder builder, string value)
        {
            if (value == null) return;
            lock (builder)
            {
                int available = MaximumJsonBytes - builder.Length;
                if (available > 0) builder.Append(value, 0, Math.Min(value.Length, available)).AppendLine();
            }
        }

        private static Dictionary<string, object> ReadJson(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var bytes = new byte[MaximumJsonBytes + 1];
                int count = 0;
                while (count < bytes.Length)
                {
                    int read = stream.Read(bytes, count, bytes.Length - count);
                    if (read == 0) break;
                    count += read;
                }
                if (count > MaximumJsonBytes) throw new IOException(UiText.Get("Ответ настройки камеры слишком большой.", "The camera setup response is too large."));
                bool zeroFilled = count > 0;
                for (int index = 0; index < count && zeroFilled; index++) if (bytes[index] != 0) zeroFilled = false;
                if (count == 0 || zeroFilled)
                    throw new IOException(UiText.Get("Файл ответа камеры «", "The camera response file “") + Path.GetFileName(path) + UiText.Get("» повреждён или пуст (", "” is damaged or empty (") + count + UiText.Get(" байт).", " bytes)."));
                try { return DeserializeJson(new UTF8Encoding(false, true).GetString(bytes, 0, count).TrimStart('\ufeff')); }
                catch (Exception error)
                {
                    if (!(error is ArgumentException) && !(error is InvalidOperationException) && !(error is DecoderFallbackException)) throw;
                    // Do not include raw metadata fragments from parser errors.
                    throw new IOException(UiText.Get("Файл ответа камеры «", "The camera response file “") + Path.GetFileName(path) + UiText.Get("» содержит недопустимый JSON (", "” contains invalid JSON (") + count + UiText.Get(" байт).", " bytes)."));
                }
            }
        }

        private static Dictionary<string, object> DeserializeJson(string json)
        {
            if (json.Length > MaximumJsonBytes) throw new IOException(UiText.Get("Ответ настройки камеры слишком большой.", "The camera setup response is too large."));
            var serializer = new JavaScriptSerializer { MaxJsonLength = MaximumJsonBytes, RecursionLimit = 8 };
            var result = serializer.Deserialize<Dictionary<string, object>>(json);
            if (result == null) throw new IOException(UiText.Get("Настройка камеры не вернула результат.", "Camera setup returned no result."));
            return result;
        }

        private static void WriteJson(string path, Dictionary<string, object> result)
        {
            RejectReparsePoint(path);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(result));
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string GetString(Dictionary<string, object> values, string key, string fallback)
        {
            object value;
            return values.TryGetValue(key, out value) && value is string ? (string)value : fallback;
        }
        private static bool GetBoolean(Dictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) && value is bool && (bool)value;
        }
        private static int GetInteger(Dictionary<string, object> values, string key)
        {
            object value;
            if (!values.TryGetValue(key, out value)) throw new IOException(UiText.Get("В ответе настройки отсутствует идентификатор камеры.", "The setup response is missing the camera identifier."));
            try { return Convert.ToInt32(value); }
            catch { throw new IOException(UiText.Get("В ответе настройки неверный идентификатор камеры.", "The setup response contains an invalid camera identifier.")); }
        }
    }
}
