[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureRoot = Join-Path $projectDirectory ('build\server-runtime-tests-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($fixtureRoot)
try {
    $testSource = Join-Path $fixtureRoot 'ServerRuntimeChecks.cs'
    $testExecutable = Join-Path $fixtureRoot 'ServerRuntimeChecks.exe'
    $source = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HorizonClient;

internal static class ServerRuntimeChecks {
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string command, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    private static int checks;
    private static void Check(bool condition, string description) {
        if (!condition) throw new Exception(description);
        checks++;
    }
    private static void Rejected(Action action, string description) {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, description);
    }
    private static void RoundTrip(string value) {
        int count;
        IntPtr arguments = CommandLineToArgvW("test.exe " + ServerRuntime.QuoteArgument(value), out count);
        if (arguments == IntPtr.Zero) throw new Exception("Argument parser failed.");
        try {
            Check(count == 2, "Quoting must preserve argument count.");
            Check(Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, IntPtr.Size)) == value, "Quoting must preserve argument value.");
        } finally { LocalFree(arguments); }
    }
    private static Dictionary<string, object> ReadFixtureJson(MethodInfo readJson, string path) {
        return (Dictionary<string, object>)readJson.Invoke(null, new object[] { path });
    }
    private static void SetupModeBoundaries(string root) {
        Directory.CreateDirectory(root);
        string name = "Camera 'quoted' \"USB\" $(fixture-never-executes)&";
        foreach (int[] mode in new[] { new[] { 1920, 1080, 30, 1, 0 }, new[] { 1280, 720, 30, 1, 0 }, new[] { 640, 480, 15, 1, 0 } }) {
            string command = ServerRuntime.BuildSetupArguments(root, name, mode[0], mode[1], mode[2], mode[3] == 1, mode[4] == 1);
            int count; IntPtr parsed = CommandLineToArgvW("test.exe " + command, out count);
            try {
                Check(count == 9 && Marshal.PtrToStringUni(Marshal.ReadIntPtr(parsed, IntPtr.Size)) == "--setup-server", "Selected setup mode has one complete eight-argument helper invocation.");
                Check(Marshal.PtrToStringUni(Marshal.ReadIntPtr(parsed, IntPtr.Size * 2)) == root && Marshal.PtrToStringUni(Marshal.ReadIntPtr(parsed, IntPtr.Size * 3)) == name,
                    "The selected-mode UAC boundary preserves exact root and quoted camera name.");
                for (int index = 0; index < mode.Length; index++)
                    Check(Marshal.PtrToStringUni(Marshal.ReadIntPtr(parsed, IntPtr.Size * (index + 4))) == mode[index].ToString(), "Each selected-mode field survives Windows argument parsing.");
            } finally { if (parsed != IntPtr.Zero) LocalFree(parsed); }
        }
        string legacy = ServerRuntime.BuildSetupArguments(root, name, 0, 0, 0, false, false);
        Check(!legacy.Contains(" 1280 ") && !legacy.Contains(" 1920 "), "Legacy setup does not invent an explicit mode during an existing-camera upgrade.");
        string scriptDirectory = Path.Combine(root, "scripts");
        MethodInfo encode = typeof(ServerRuntime).GetMethod("EncodedPowerShell", BindingFlags.Static | BindingFlags.NonPublic);
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        foreach (int[] mode in new[] { new[] { 1920, 1080, 30, 1, 0 }, new[] { 1280, 720, 30, 1, 0 }, new[] { 640, 480, 15, 1, 0 }, new[] { 0, 0, 0, 0, 0 } }) {
            string command = ServerRuntime.BuildSetupCommand(root, name, mode[0], mode[1], mode[2], mode[3] == 1, mode[4] == 1);
            string arguments = (string)encode.Invoke(null, new object[] { command });
            Check(arguments.Length < 32767 && !arguments.Contains("ExecutionPolicy"), "The compact resource loader fits the Windows command-line limit and does not override PowerShell policy.");
            string captured;
            int exitCode = ServerRuntime.RunCaptured(ps, arguments, root, 15000, out captured);
            Check(exitCode == 0, "The setup command executes only the isolated fixture script successfully.");
            var values = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(captured);
            Check((string)values["Name"] == name && (int)values["Width"] == mode[0] && (int)values["Height"] == mode[1] && (int)values["FPS"] == mode[2],
                "PowerShell receives the exact selected dimensions and literal camera name without evaluating its contents.");
            Check((bool)values["Raw"] == (mode[3] == 1) && (bool)values["Probe"] == (mode[4] == 1) &&
                (bool)values["RawBound"] == (mode[0] != 0) && (bool)values["ProbeBound"] == (mode[0] != 0) && !(bool)values["ConfigureBound"],
                "New-camera setup explicitly carries both true and false flags without requiring ConfigureMode or a second camera operation.");
            Check((string)values["Root"] == root && (bool)values["Dependency"], "The in-memory setup receives its explicit runtime root and dot-sources its embedded dependency.");
        }
        Check(!Directory.Exists(scriptDirectory), "Executing embedded setup and its large dependency creates no scripts directory or .ps1 file.");
        Directory.CreateDirectory(scriptDirectory);
        string oldScript = Path.Combine(scriptDirectory, "Setup-Camera.ps1"); File.WriteAllText(oldScript, "throw 'Never execute an old script on disk.'");
        string output;
        Check(ServerRuntime.RunCaptured(ps, (string)encode.Invoke(null, new object[] { ServerRuntime.BuildSetupCommand(root, name, 0, 0, 0, false, false) }), root, 15000, out output) == 0 && output.Contains("Dependency"),
            "A modified legacy disk script is ignored rather than executed or replaced.");
        Check(File.ReadAllText(oldScript).Contains("Never execute"), "In-memory execution leaves user-owned old scripts untouched.");
        string stop = ServerRuntime.EmbeddedScriptCommand("Stop-Camera.ps1") + " -RuntimeRoot '" + root.Replace("'", "''") +
            "' -CameraRuntime ([ScriptBlock]::Create((Read-HorizonEmbeddedScript 'Camera-Runtime.ps1'))) -Remove -MetadataOnly | ConvertTo-Json -Compress";
        Check(ServerRuntime.RunCaptured(ps, (string)encode.Invoke(null, new object[] { stop }), root, 15000, out output) == 0 && output.Contains("Dependency"),
            "The remove command also executes its embedded fixture and dependency without a disk Stop script.");
        CameraDriverRepairResult repaired = CameraDriverRepair.RunElevated(root);
        Check(repaired.Operation == "none" && !repaired.Changed && !repaired.RestartRequired,
            "The repair entry point executes only its synthetic embedded resource without a disk repair script.");
        Check(Directory.GetFiles(scriptDirectory).Length == 1, "Setup, stop and repair never extract additional PowerShell sources.");
        Rejected(delegate { ServerRuntime.EmbeddedScriptCommand("../foreign.ps1"); }, "Only the fixed embedded script allowlist can be invoked.");
        var runtime = new ServerRuntime(delegate(ProcessStartInfo start) {
            if (start.FileName == ps) {
                Check(start.UseShellExecute && start.Verb == "runas" && start.WindowStyle == ProcessWindowStyle.Hidden && start.Arguments.Length < 32767,
                    "Only the compact verified shutdown command requests a hidden elevated child; fixtures replace elevation with an ordinary isolated PowerShell process.");
                return Process.Start(new ProcessStartInfo(start.FileName, start.Arguments) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true });
            }
            return Process.Start(start);
        });
        typeof(ServerRuntime).GetProperty("RootDirectory").GetSetMethod(true).Invoke(runtime, new object[] { root });
        var owned = new ServerConnection { RootDirectory = root, KeyPath = Path.Combine(root, "data", "camera.local.key"), ProcessId = 4411,
            StartTimeUtcTicks = "638000000000000000", CameraSerial = "HorizonClient-01234567890123456789012345678901" };
        Check(runtime.StopOwnedCamera(owned), "Shutdown sends the exact PID, birth time, serial, root and key path to its in-memory Stop fixture without Remove.");
        owned.StartTimeUtcTicks = null;
        Check(!runtime.StopOwnedCamera(owned), "An incomplete process identity cannot authorize stopping a camera.");
        owned.StartTimeUtcTicks = "638000000000000000"; owned.ProcessId = 4412;
        Check(!runtime.StopOwnedCamera(owned), "The stop result distinguishes a changed endpoint from the expected receiver.");
        string previousMode = Environment.GetEnvironmentVariable("HORIZON_RUNTIME_FIXTURE_MODE");
        try {
            Environment.SetEnvironmentVariable("HORIZON_RUNTIME_FIXTURE_MODE", "active");
            Check(runtime.StopOwnedCamera(null), "Failed-result cleanup can rediscover a verified receiver only within this exact runtime root.");
            Environment.SetEnvironmentVariable("HORIZON_RUNTIME_FIXTURE_MODE", "foreign");
            Check(!runtime.StopOwnedCamera(null), "Fallback discovery never adopts a verified but foreign installation for exit cleanup.");
            Environment.SetEnvironmentVariable("HORIZON_RUNTIME_FIXTURE_MODE", "absent");
            Check(!runtime.StopOwnedCamera(null), "An absent receiver causes no shutdown mutation.");
        } finally { Environment.SetEnvironmentVariable("HORIZON_RUNTIME_FIXTURE_MODE", previousMode); }
        owned.ProcessId = 4411;
        string marker = Path.Combine(root, "owned-helper-finished.txt");
        Process helper = (Process)typeof(ServerRuntime).GetMethod("StartOwnedHelper", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime,
            new object[] { new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--short-helper " + ServerRuntime.QuoteArgument(marker)) { UseShellExecute = false, CreateNoWindow = true } });
        typeof(ServerRuntime).GetMethod("ReleaseOwnedHelper", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, new object[] { helper });
        var retained = (System.Collections.ICollection)typeof(ServerRuntime).GetField("ownedHelpers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(runtime);
        Check(retained.Count == 1, "Releasing a still-running helper retains its exact Process handle for exit cleanup.");
        Check(runtime.StopOwnedCamera(owned) && File.Exists(marker) && retained.Count == 0,
            "Shutdown waits for its retained helper to finish naturally before stopping the expected receiver.");
        File.Delete(marker);
        Check(ServerRuntime.RunCaptured(Assembly.GetExecutingAssembly().Location, "--short-helper " + ServerRuntime.QuoteArgument(marker), root, 1, out output, true) == 0 && File.Exists(marker),
            "Production slow-wait capture never abandons or terminates its own live helper at the timeout boundary.");
        string pidPath = Path.Combine(root, "bounded-helper.pid"), completion = Path.Combine(root, "bounded-helper.done");
        bool timedOut = false;
        try { ServerRuntime.RunCaptured(Assembly.GetExecutingAssembly().Location, "--bounded-helper " + ServerRuntime.QuoteArgument(pidPath) + " " + ServerRuntime.QuoteArgument(completion), root, 300, out output); }
        catch (TimeoutException) { timedOut = true; }
        bool alive = false;
        try
        {
            using (Process bounded = Process.GetProcessById(Int32.Parse(File.ReadAllText(pidPath))))
            {
                alive = !bounded.HasExited;
                // Test cleanup owns this synthetic process; it never addresses another PID.
                if (alive) bounded.WaitForExit(3000);
            }
        }
        catch (ArgumentException) { }
        Check(timedOut && !alive && !File.Exists(completion), "A bounded captured query must terminate and reap its exact child before returning timeout.");
    }
    private static void CorruptJsonRejected(MethodInfo readJson, string directory, string fileName, byte[] bytes, string forbiddenFragment) {
        string path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, bytes);
        IOException failure = null;
        try { ReadFixtureJson(readJson, path); }
        catch (TargetInvocationException error) { failure = error.InnerException as IOException; }
        Check(failure != null, "Corrupt metadata must return a contextual I/O error: " + fileName);
        Check(failure.Message.Contains(fileName) && failure.Message.Contains("(" + bytes.Length + " "),
            "Corrupt metadata identifies only the fixture filename and actual byte count: " + fileName);
        Check(failure.InnerException == null && (String.IsNullOrEmpty(forbiddenFragment) || !failure.ToString().Contains(forbiddenFragment)),
            "Metadata errors must not expose parser fragments or wrapped parser exceptions: " + fileName);
    }
    public static int Main(string[] args) {
        try {
            if (args.Length > 0 && args[0] == "--hold-pipes") { Thread.Sleep(6000); return 0; }
            if (args.Length > 1 && args[0] == "--short-helper") { Thread.Sleep(400); File.WriteAllText(args[1], "finished naturally"); return 0; }
            if (args.Length > 2 && args[0] == "--bounded-helper") { File.WriteAllText(args[1], Process.GetCurrentProcess().Id.ToString()); Thread.Sleep(1000); File.WriteAllText(args[2], "orphan completed"); return 0; }
            if (args.Length > 0 && args[0] == "--parent-pipe") {
                using (Process child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--hold-pipes") { UseShellExecute = false, CreateNoWindow = true }))
                    Console.WriteLine("parent-ready:" + child.Id);
                return 0;
            }
            if (args.Length > 0 && args[0] == "--failed-parent") { Console.Error.WriteLine("fixture-error-tail"); return 7; }
            string root = args[0];
            string expectedRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HorizonServer"));
            bool rootExisted = Directory.Exists(expectedRoot);
            var runtime = new ServerRuntime();
            Check(runtime.RootDirectory == expectedRoot, "Runtime stays in the portable directory beside the server executable.");
            Check(Directory.Exists(expectedRoot) == rootExisted, "Constructor must not create a runtime directory.");
            Check(ServerRuntime.DefaultCameraName == "4k webcam", "Default camera name.");
            Check(ServerRuntime.ValidateCameraName("  4k webcam  ") == "4k webcam", "Camera names trim whitespace.");
            Check(ServerRuntime.ValidateCameraName("Камера 'тест' \"USB\"") == "Камера 'тест' \"USB\"", "Unicode and quotes are allowed.");
            Rejected(delegate { ServerRuntime.ValidateCameraName(" "); }, "Empty camera names rejected.");
            Rejected(delegate { ServerRuntime.ValidateCameraName("a\nb"); }, "Controls rejected.");
            Rejected(delegate { ServerRuntime.ValidateCameraName(new string('x', 101)); }, "Names above the native limit rejected.");
            Check(ServerRuntime.ValidateCameraName(new string('x', 100)).Length == 100, "Native maximum camera name accepted.");
            foreach (int[] mode in new[] { new[] { 640, 480 }, new[] { 1280, 720 }, new[] { 1920, 1080 } }) {
                foreach (int fps in new[] { 1, 15, 30, 60 }) {
                    ServerRuntime.ValidateVideoMode(mode[0], mode[1], fps, true);
                    Check(ServerRuntime.IsSupportedVideoMode(mode[0], mode[1], fps, true, false), "Only exact supported YUY2 geometries retain the bounded native frame rates.");
                }
                Check(!ServerRuntime.IsSupportedVideoMode(mode[0], mode[1], 30, false, false) && !ServerRuntime.IsSupportedVideoMode(mode[0], mode[1], 30, true, true), "MJPEG and SEI configurations are not selectable.");
                Rejected(delegate { ServerRuntime.ValidateVideoMode(mode[0], mode[1], 30, false); }, "New compressed camera modes are rejected.");
            }
            SetupModeBoundaries(root);
            Rejected(delegate { runtime.PrepareCamera("fixture", 0, 1080, 30, true, false, null); }, "Invalid selected create mode is rejected before discovery, files or UAC.");
            foreach (int[] mode in new[] { new[] { 2560, 1440 }, new[] { 3840, 2160 }, new[] { 7680, 4320 }, new[] { 1922, 1080 }, new[] { 1920, 1082 }, new[] { 800, 600 }, new[] { 1920, 720 } }) {
                Rejected(delegate { ServerRuntime.ValidateVideoMode(mode[0], mode[1], 30, false); }, "New high-resolution MJPEG requests are rejected.");
                Rejected(delegate { ServerRuntime.ValidateVideoMode(mode[0], mode[1], 30, true); }, "New unsupported YUY2 geometries are rejected.");
                Check(!ServerRuntime.IsSupportedVideoMode(mode[0], mode[1], 30, true, false), "Capability queries cannot offer an unsupported geometry.");
                Rejected(delegate { ServerRuntime.BuildSetupArguments(root, "fixture", mode[0], mode[1], 30, true, false); }, "Setup helper arguments reject unsupported geometry before elevation.");
                Rejected(delegate { ServerRuntime.BuildSetupCommand(root, "fixture", mode[0], mode[1], 30, true, false); }, "PowerShell helper commands reject unsupported geometry before execution.");
                Rejected(delegate { runtime.PrepareCamera("fixture", mode[0], mode[1], 30, true, false, null); }, "Explicit camera creation rejects unsupported geometry before discovery or UAC.");
                Rejected(delegate { ServerRuntime.RunSetup(root, "fixture", mode[0], mode[1], 30, true, false); }, "Setup CLI rejects unsupported geometry before any runtime access.");
                string rejectedRoot = Path.Combine(root, "rejected-configuration");
                Check(ServerRuntime.RunConfigure(rejectedRoot, mode[0], mode[1], 30, true, false) == 1 && !Directory.Exists(rejectedRoot), "Configure CLI rejects unsupported geometry before creating runtime files.");
            }
            MethodInfo readConnection = typeof(ServerRuntime).GetMethod("ReadConnection", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo backendConnectionValues = typeof(ServerRuntime).GetMethod("BackendConnectionValues", BindingFlags.Static | BindingFlags.NonPublic);
            var legacyValues = new Dictionary<string, object> {
                { "RootDirectory", root }, { "KeyPath", Path.Combine(root, "data", "camera.local.key") },
                { "ProcessId", 4411 }, { "CameraName", "Legacy identity fixture" }, { "FPS", 30 }, { "Uncompressed", true }
            };
            foreach (int[] mode in new[] { new[] { 2560, 1440, 30, 1 }, new[] { 3840, 2160, 30, 1 }, new[] { 7680, 4320, 60, 0 }, new[] { 7680, 4320, 8, 1 } }) {
                legacyValues["Width"] = mode[0]; legacyValues["Height"] = mode[1]; legacyValues["FPS"] = mode[2]; legacyValues["Uncompressed"] = mode[3] == 1;
                ServerConnection legacy = (ServerConnection)readConnection.Invoke(null, new object[] { legacyValues, root, false });
                Check(legacy.Width == mode[0] && legacy.Height == mode[1] && legacy.FPS == mode[2] && legacy.ProcessId == 4411 &&
                    legacy.CameraName == "Legacy identity fixture" && legacy.KeyPath == Path.Combine(root, "data", "camera.local.key"),
                    "Legacy 4K/8K connections remain readable for identity-preserving migration without touching the key.");
                Check(!legacy.MetadataRecoveryRequired, "Legacy connections without a recovery flag retain their confirmed identity state.");
            }
            legacyValues["Width"] = 7682;
            bool malformedLegacyRejected = false;
            try { readConnection.Invoke(null, new object[] { legacyValues, root, false }); }
            catch (TargetInvocationException error) { malformedLegacyRejected = error.InnerException is ArgumentException; }
            Check(malformedLegacyRejected, "Legacy reading retains the native dimension and bitrate bounds.");
            var recoveredBackend = new Dictionary<string, object> {
                { "Root", root }, { "KeyPath", Path.Combine(root, "data", "camera.local.key") }, { "ProcessId", 0 },
                { "CameraName", "Recovered fixture webcam" }, { "Width", 640 }, { "Height", 480 }, { "FPS", 30 },
                { "Uncompressed", false }, { "H264Probe", false }, { "MetadataRecoveryRequired", true }
            };
            var recoveredValues = (Dictionary<string, object>)backendConnectionValues.Invoke(null, new object[] { recoveredBackend });
            ServerConnection recovered = (ServerConnection)readConnection.Invoke(null, new object[] { recoveredValues, root, true });
            Check(recovered.MetadataRecoveryRequired && recovered.ProcessId == 0 && recovered.RootDirectory == Path.GetFullPath(root) &&
                recovered.CameraName == "Recovered fixture webcam" && recovered.KeyPath == Path.Combine(root, "data", "camera.local.key"),
                "Backend conversion preserves a recovered identity as stopped metadata without inventing a running process.");
            recoveredValues["ProcessId"] = 4411;
            bool recoveredActiveRejected = false;
            try { readConnection.Invoke(null, new object[] { recoveredValues, root, true }); }
            catch (TargetInvocationException error) { recoveredActiveRejected = error.InnerException is IOException; }
            Check(recoveredActiveRejected, "Recovered metadata can never certify an active process, even in a read-only lookup.");
            recoveredValues["ProcessId"] = 0;
            bool recoveredSetupRejected = false;
            try { readConnection.Invoke(null, new object[] { recoveredValues, root, false }); }
            catch (TargetInvocationException error) { recoveredSetupRejected = error.InnerException is IOException; }
            Check(recoveredSetupRejected, "A stopped recovered identity cannot pass the successful setup connection validator.");
            recoveredBackend.Remove("MetadataRecoveryRequired");
            var confirmedValues = (Dictionary<string, object>)backendConnectionValues.Invoke(null, new object[] { recoveredBackend });
            ServerConnection confirmed = (ServerConnection)readConnection.Invoke(null, new object[] { confirmedValues, root, true });
            Check(!confirmed.MetadataRecoveryRequired && confirmed.ProcessId == 0, "Missing legacy backend recovery flags default to confirmed saved metadata.");
            Rejected(delegate { ServerRuntime.ValidateVideoMode(640, 480, 30, false, true); }, "SEI probe creation is disabled even for its old native fixture profile.");
            Rejected(delegate { ServerRuntime.ValidateVideoMode(640, 480, 30, true, true); }, "H264 and YUY2 cannot be enabled together.");
            Rejected(delegate { ServerRuntime.ValidateVideoMode(1280, 720, 30, false, true); }, "H264 probe rejects unsupported dimensions.");
            Rejected(delegate { ServerRuntime.ValidateVideoMode(640, 480, 60, false, true); }, "H264 probe rejects unsupported frame rate.");
            foreach (int[] mode in new[] { new[] { 15, 720, 30 }, new[] { 1280, 17, 30 }, new[] { 7682, 4320, 30 }, new[] { 1280, 720, 0 }, new[] { 1280, 720, 61 } })
                Rejected(delegate { ServerRuntime.ValidateVideoMode(mode[0], mode[1], mode[2], false); }, "Invalid dimensions or frame rate rejected.");
            Rejected(delegate { ServerRuntime.ValidateVideoMode(7680, 4320, 9, true); }, "Oversized RAW mode is rejected.");
            Rejected(delegate { runtime.ChangeVideoMode(7680, 4320, 60, true, null); }, "Invalid mode is rejected before lookup, extraction or UAC.");
            foreach (string value in new[] { "", "ordinary", "4k webcam", "a\\b\\", "a\"b", "\\\"quoted\\\"", "Русская камера", "a'$(command)&b" }) RoundTrip(value);
            string data = Path.Combine(root, "data");
            Directory.CreateDirectory(data);
            string key = Path.Combine(data, "camera.local.key");
            File.WriteAllText(key, "synthetic fixture key must remain unchanged", new UTF8Encoding(false));
            MethodInfo writeJson = typeof(ServerRuntime).GetMethod("WriteJson", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo readJson = typeof(ServerRuntime).GetMethod("ReadJson", BindingFlags.Static | BindingFlags.NonPublic);
            string metadata = Path.Combine(root, "metadata-fixtures");
            Directory.CreateDirectory(metadata);
            string metadataPath = Path.Combine(metadata, "result.json");
            MethodInfo writeOperationFailure = typeof(ServerRuntime).GetMethod("WriteOperationFailure", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo operationFailure = typeof(ServerRuntime).GetMethod("OperationFailure", BindingFlags.Static | BindingFlags.NonPublic);
            string failurePath = Path.Combine(metadata, "operation-failure.json");
            writeOperationFailure.Invoke(null, new object[] { failurePath, new CameraDriverRestartRequiredException() });
            var restartResult = ReadFixtureJson(readJson, failurePath);
            Check(!(bool)restartResult["Success"] && (bool)restartResult["RestartRequired"] &&
                operationFailure.Invoke(null, new object[] { restartResult, "fallback" }) is CameraDriverRestartRequiredException,
                "A driver restart request survives the elevated helper JSON boundary as a typed user instruction.");
            writeOperationFailure.Invoke(null, new object[] { failurePath, new DriverRestartRequiredException(3010, "installer_requested_restart") });
            var installedResult = ReadFixtureJson(readJson, failurePath);
            var installedRestart = operationFailure.Invoke(null, new object[] { installedResult, "fallback" }) as DriverRestartRequiredException;
            Check((bool)installedResult["RestartRequired"] && (bool)installedResult["DriverRestartRequired"] && installedRestart != null &&
                installedRestart.InstallerExitCode == 3010 && installedRestart.Reason == "installer_requested_restart",
                "A successful offline installation requiring reboot retains its exact exit code and typed status through JSON.");
            writeOperationFailure.Invoke(null, new object[] { failurePath, new DriverRestartRequiredException(0, "controller_not_ready_after_install") });
            installedResult = ReadFixtureJson(readJson, failurePath);
            installedRestart = operationFailure.Invoke(null, new object[] { installedResult, "fallback" }) as DriverRestartRequiredException;
            Check(installedRestart != null && installedRestart.InstallerExitCode == 0 && installedRestart.Reason == "controller_not_ready_after_install",
                "An installed controller awaiting PnP activation keeps its distinct restart reason.");
            writeOperationFailure.Invoke(null, new object[] { failurePath, new IOException("ordinary fixture failure") });
            var ordinaryResult = ReadFixtureJson(readJson, failurePath);
            Check(!(bool)ordinaryResult["RestartRequired"] &&
                !(operationFailure.Invoke(null, new object[] { ordinaryResult, "fallback" }) is CameraDriverRestartRequiredException),
                "An ordinary setup failure cannot become a driver restart instruction.");
            File.Delete(failurePath);
            var firstMetadata = new Dictionary<string, object> { { "Success", false }, { "CameraName", "fixture-камера" }, { "Generation", 1 } };
            writeJson.Invoke(null, new object[] { metadataPath, firstMetadata });
            Dictionary<string, object> firstRead = ReadFixtureJson(readJson, metadataPath);
            Check(!(bool)firstRead["Success"] && (string)firstRead["CameraName"] == "fixture-камера" && (int)firstRead["Generation"] == 1,
                "A newly created durable metadata result round-trips UTF-8 JSON without losing values.");
            byte[] firstBytes = File.ReadAllBytes(metadataPath);
            Check(firstBytes.Length > 0 && firstBytes[0] == (byte)'{' && new UTF8Encoding(false, true).GetString(firstBytes).Contains("fixture-камера"),
                "New metadata is valid UTF-8 JSON without a BOM or zero-filled prefix.");
            var replacementMetadata = new Dictionary<string, object> { { "Success", true }, { "CameraName", "замена-fixture" }, { "Generation", 2 } };
            writeJson.Invoke(null, new object[] { metadataPath, replacementMetadata });
            Dictionary<string, object> replacementRead = ReadFixtureJson(readJson, metadataPath);
            Check((bool)replacementRead["Success"] && (string)replacementRead["CameraName"] == "замена-fixture" && (int)replacementRead["Generation"] == 2,
                "Replacing an existing durable result publishes the complete new JSON document.");
            Check(Directory.GetFiles(metadata, "*.tmp").Length == 0 && Directory.GetFiles(metadata).Length == 1,
                "Successful durable create and replace leave no temporary files or backup artifacts.");
            string bomPath = Path.Combine(metadata, "bom.json");
            File.WriteAllText(bomPath, "{\"Success\":true}", new UTF8Encoding(true));
            Check((bool)ReadFixtureJson(readJson, bomPath)["Success"], "Strict metadata decoding still accepts an ordinary UTF-8 BOM.");
            CorruptJsonRejected(readJson, metadata, "empty.json", new byte[0], null);
            CorruptJsonRejected(readJson, metadata, "zero-filled.json", new byte[137], null);
            CorruptJsonRejected(readJson, metadata, "truncated.json", Encoding.UTF8.GetBytes("{\"Success\":true,\"PrivateFixture\":\"parser-fragment-private"), "parser-fragment-private");
            CorruptJsonRejected(readJson, metadata, "invalid-utf8.json", new byte[] { (byte)'{', (byte)'\"', 0xc3, 0x28, (byte)'\"', (byte)':', (byte)'0', (byte)'}' }, null);
            Check(File.ReadAllText(key) == "synthetic fixture key must remain unchanged", "Metadata create, replace and rejection never alter the fixture key.");
            ServerRuntime.ExtractResources(root);
            Check(File.ReadAllText(key) == "synthetic fixture key must remain unchanged", "Extraction preserves existing key bytes.");
            foreach (string path in new[] { "runtime\\usbbridge-camera.exe", "defaults\\ServerDefaults.json", "runtime\\drivers\\USBip-0.9.8.1-x64.exe" })
                Check(File.ReadAllText(Path.Combine(root, path)) == "synthetic embedded fixture", "Pinned native/default resource extracted: " + path);
            string script = Path.Combine(root, "scripts", "Setup-Camera.ps1");
            File.WriteAllText(script, "modified fixture script");
            ServerRuntime.ExtractResources(root);
            Check(File.ReadAllText(script) == "modified fixture script", "Extraction leaves existing disk scripts untouched.");
            Check(!File.Exists(Path.Combine(root, "runtime", "ThirdPartyNotices.zip")) && Directory.GetFiles(root, "*.ps1", SearchOption.AllDirectories).Length == 1,
                "Extraction writes neither scripts nor the third-party source archive.");
            Check(File.ReadAllText(key) == "synthetic fixture key must remain unchanged", "Re-extraction preserves key bytes.");
            string native = Path.Combine(root, "runtime", "usbbridge-camera.exe");
            string staged = Path.Combine(root, "runtime", "usbbridge-camera.next.exe");
            File.WriteAllText(native, "older running fixture image");
            using (File.Open(native, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                ServerRuntime.ExtractResources(root);
                Check(File.ReadAllText(native) == "older running fixture image", "Upgrade must preserve a locked active native image.");
                Check(File.ReadAllText(staged) == "synthetic embedded fixture", "Changed native bytes are staged until the verified backend is stopped.");
            }
            ServerRuntime.ExtractResources(root);
            Check(File.ReadAllText(native) == "synthetic embedded fixture" && !File.Exists(staged), "An unlocked image can be promoted without stale staging.");
            string captured;
            var elapsed = Stopwatch.StartNew();
            int exitCode = ServerRuntime.RunCaptured(Assembly.GetExecutingAssembly().Location, "--parent-pipe", root, 5000, out captured);
            Check(exitCode == 0 && captured.StartsWith("parent-ready:"), "Exited setup parent returns its output.");
            int childId = Int32.Parse(captured.Substring("parent-ready:".Length).Trim());
            using (Process child = Process.GetProcessById(childId)) {
                try {
                    Check(!child.HasExited, "Persistent descendant remains alive after setup returns.");
                    Check(elapsed.ElapsedMilliseconds < 3500, "Inherited pipe handles cannot prolong setup until descendant exit.");
                    Check(child.MainModule.FileName == Assembly.GetExecutingAssembly().Location, "Cleanup targets only the synthetic fixture descendant.");
                } finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
            }
            exitCode = ServerRuntime.RunCaptured(Assembly.GetExecutingAssembly().Location, "--failed-parent", root, 5000, out captured);
            Check(exitCode == 7 && captured.Contains("fixture-error-tail"), "Bounded drain preserves exit code and final stderr.");
            MethodInfo encodedPowerShell = typeof(ServerRuntime).GetMethod("EncodedPowerShell", BindingFlags.Static | BindingFlags.NonPublic);
            string powershellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            string failedCommand = (string)encodedPowerShell.Invoke(null, new object[] { "Write-Progress -Activity 'fixture-progress'; throw 'fixture-ошибка'" });
            exitCode = ServerRuntime.RunCaptured(powershellPath, failedCommand, root, 15000, out captured);
            Check(exitCode != 0 && captured.Trim() == "fixture-ошибка", "PowerShell failures retain an explicit nonzero exit and readable UTF-8 exception text.");
            Check(!captured.Contains("CLIXML") && !captured.Contains("<Objs") && !captured.Contains("fixture-progress"),
                "PowerShell progress and serialized error records never contaminate the user-facing failure.");
            string successfulCommand = (string)encodedPowerShell.Invoke(null, new object[] { "Write-Progress -Activity 'fixture-progress'; Write-Output '{\"Success\":true}'" });
            exitCode = ServerRuntime.RunCaptured(powershellPath, successfulCommand, root, 15000, out captured);
            Check(exitCode == 0 && captured.Trim() == "{\"Success\":true}", "Successful PowerShell results remain stable JSON with benign progress suppressed.");
            string previousMode = Environment.GetEnvironmentVariable("HORIZON_RUNTIME_FIXTURE_MODE");
            try {
                Environment.SetEnvironmentVariable("HORIZON_RUNTIME_FIXTURE_MODE", "active");
                ServerConnection existing = runtime.FindExistingCamera();
                Check(existing.ProcessId == 4411 && existing.CameraName == "Existing fixture webcam", "Read-only lookup uses the embedded verifier and actual camera name.");
                Check(existing.Width == 1280 && existing.Height == 720 && existing.FPS == 30 && existing.Uncompressed && !existing.H264Probe, "Lookup returns the actual saved video mode, with missing legacy probe flag defaulting to false.");
                int progressCalls = 0;
                existing = runtime.PrepareCamera("A different requested name", delegate(string ignored) { progressCalls++; });
                Check(existing.ProcessId == 4411 && existing.CameraName == "Existing fixture webcam" && progressCalls == 0, "Prepare reuses an active camera without extraction, renaming or elevation.");
                existing = runtime.PrepareCamera("Another requested name", 1280, 720, 30, true, false, delegate(string ignored) { progressCalls++; });
                Check(existing.ProcessId == 4411 && existing.CameraName == "Existing fixture webcam" && progressCalls == 0, "An explicit already-active create mode reuses the same camera with no UAC or new identity.");
                existing = runtime.ChangeVideoMode(1280, 720, 30, true, delegate(string ignored) { progressCalls++; });
                Check(existing.ProcessId == 4411 && progressCalls == 0, "Applying an already active mode avoids restart and UAC.");
                Environment.SetEnvironmentVariable("HORIZON_RUNTIME_FIXTURE_MODE", "stopped");
                ServerConnection stopped = runtime.FindExistingCamera();
                Check(stopped.ProcessId == 0 && stopped.CameraName == "Saved fixture webcam" && !stopped.MetadataRecoveryRequired, "A confirmed saved identity is reported separately from a running receiver.");
                Environment.SetEnvironmentVariable("HORIZON_RUNTIME_FIXTURE_MODE", "recovery");
                ServerConnection recoveredLookup = runtime.FindExistingCamera();
                Check(recoveredLookup.ProcessId == 0 && recoveredLookup.MetadataRecoveryRequired && !recoveredLookup.RequiresUpgrade &&
                    recoveredLookup.CameraName == "Recovered fixture webcam" && recoveredLookup.Width == 640 && recoveredLookup.Height == 480,
                    "Read-only discovery reports recovered metadata without treating it as a running camera or a binary upgrade.");
                Check(File.ReadAllText(key) == "synthetic fixture key must remain unchanged" && Directory.GetFiles(metadata, "*.tmp").Length == 0,
                    "Recovered identity discovery leaves existing fixture key and metadata files untouched.");
                Environment.SetEnvironmentVariable("HORIZON_RUNTIME_FIXTURE_MODE", "absent");
                Check(runtime.FindExistingCamera() == null, "Lookup returns null when neither a receiver nor a saved identity exists.");
                Check(Directory.Exists(expectedRoot) == rootExisted, "Read-only lookup does not create the real user runtime root.");
            } finally { Environment.SetEnvironmentVariable("HORIZON_RUNTIME_FIXTURE_MODE", previousMode); }
            Console.WriteLine("Server runtime checks passed: " + checks + " (pure validation, synthetic metadata/resources and isolated subprocess capture).");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
'@
    [IO.File]::WriteAllText($testSource, $source, [Text.UTF8Encoding]::new($false))
    $resource = Join-Path $fixtureRoot 'synthetic-resource.bin'
    [IO.File]::WriteAllText($resource, 'synthetic embedded fixture', [Text.UTF8Encoding]::new($false))
    $helperResource = Join-Path $fixtureRoot 'synthetic-camera-helper.ps1'
    $helper = @'
function Resolve-HorizonCameraBackend {
    param([string]$ExpectedExecutable, [string]$ExpectedBinaryHash)
    if ($ExpectedBinaryHash.Length -ne 64) { throw 'Expected embedded payload checksum.' }
    if ($env:HORIZON_RUNTIME_FIXTURE_MODE -notin @('active', 'foreign')) { return $null }
    $root=[IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($ExpectedExecutable))
    if ($env:HORIZON_RUNTIME_FIXTURE_MODE -eq 'foreign') { $root += '-foreign' }
    [pscustomobject]@{Root=$root;KeyPath=(Join-Path $root 'data\camera.local.key');ProcessId=4411;StartTimeUtcTicks='638000000000000000';CameraSerial='HorizonClient-01234567890123456789012345678901';CameraName='Existing fixture webcam';MatchesBinary=$true;Width=1280;Height=720;FPS=30;Uncompressed=$true}
}
function Resolve-HorizonSavedCameraIdentity {
    param([string]$Root)
    if ($env:HORIZON_RUNTIME_FIXTURE_MODE -eq 'recovery') {
        return [pscustomobject]@{Root=$Root;KeyPath=(Join-Path $Root 'data\camera.local.key');ProcessId=0;CameraName='Recovered fixture webcam';MatchesBinary=$false;Width=640;Height=480;FPS=30;Uncompressed=$false;MetadataRecoveryRequired=$true}
    }
    if ($env:HORIZON_RUNTIME_FIXTURE_MODE -ne 'stopped') { return $null }
    [pscustomobject]@{Root=$Root;KeyPath=(Join-Path $Root 'data\camera.local.key');ProcessId=0;CameraName='Saved fixture webcam';MatchesBinary=$false;Width=1280;Height=720;FPS=30;Uncompressed=$false}
}
function Test-HorizonEmbeddedDependency { $true }
'@
    $helper += "`r`n#" + ('x' * 100000)
    [IO.File]::WriteAllText($helperResource, $helper, [Text.UTF8Encoding]::new($false))
    $setupResource = Join-Path $fixtureRoot 'synthetic-setup.ps1'
    [IO.File]::WriteAllText($setupResource, @'
param([string]$CameraName,[switch]$PassThru,[switch]$TakeOwnership,[int]$Width,[int]$Height,[int]$FramesPerSecond,[switch]$Uncompressed,[switch]$H264Probe,[switch]$ConfigureMode,[string]$ExpectedBinaryHash,[string]$RuntimeRoot,[scriptblock]$CameraRuntime)
. $CameraRuntime
[pscustomobject]@{Name=$CameraName;Root=$RuntimeRoot;Dependency=(Test-HorizonEmbeddedDependency);Width=$Width;Height=$Height;FPS=$FramesPerSecond;Raw=[bool]$Uncompressed;Probe=[bool]$H264Probe;RawBound=$PSBoundParameters.ContainsKey('Uncompressed');ProbeBound=$PSBoundParameters.ContainsKey('H264Probe');ConfigureBound=$PSBoundParameters.ContainsKey('ConfigureMode')}
'@, [Text.UTF8Encoding]::new($false))
    $stopResource = Join-Path $fixtureRoot 'synthetic-stop.ps1'
    [IO.File]::WriteAllText($stopResource, @'
param([string]$RuntimeRoot,[scriptblock]$CameraRuntime,[switch]$Remove,[switch]$AnyOwnedRoot,[switch]$PassThru,[switch]$MetadataOnly,[int]$ExpectedProcessId,[string]$ExpectedStartTimeUtcTicks,[string]$ExpectedKeyPath,[string]$ExpectedCameraSerial,[string]$ExpectedBinaryHash)
. $CameraRuntime
if ($ExpectedProcessId -and ($Remove -or $AnyOwnedRoot -or $MetadataOnly -or $ExpectedStartTimeUtcTicks -ne '638000000000000000' -or $ExpectedKeyPath -ne (Join-Path $RuntimeRoot 'data\camera.local.key') -or $ExpectedCameraSerial -ne 'HorizonClient-01234567890123456789012345678901' -or $ExpectedBinaryHash.Length -ne 64)) { throw 'Expected exact non-removal shutdown identity and pinned binary hash.' }
[pscustomobject]@{Root=$RuntimeRoot;Dependency=(Test-HorizonEmbeddedDependency);Remove=[bool]$Remove;MetadataOnly=[bool]$MetadataOnly;Stopped=($ExpectedProcessId -eq 4411)}
'@, [Text.UTF8Encoding]::new($false))
    $repairResource = Join-Path $fixtureRoot 'synthetic-repair.ps1'
    [IO.File]::WriteAllText($repairResource, @'
param([string]$RuntimeRoot,[switch]$Apply)
if (-not $Apply -or -not $RuntimeRoot) { throw 'Expected isolated fixture repair arguments.' }
[pscustomobject]@{Schema=1;Success=$true;Changed=$false;RestartRequired=$false;Operation='none';DriverState=$null} | ConvertTo-Json -Compress
'@, [Text.UTF8Encoding]::new($false))
    $resourceNames = @('HorizonServer.runtime.usbbridge-camera.exe', 'HorizonServer.scripts.Setup-Camera.ps1', 'HorizonServer.scripts.Stop-Camera.ps1', 'HorizonServer.scripts.Camera-Runtime.ps1', 'HorizonServer.scripts.Repair-CameraDriver.ps1', 'HorizonServer.defaults.ServerDefaults.json', 'HorizonServer.DriverInstaller.exe', 'HorizonServer.ThirdPartyNotices.zip')
    $resourceArguments = @($resourceNames | ForEach-Object {
        $sourceResource = switch ($_) {
            'HorizonServer.scripts.Camera-Runtime.ps1' { $helperResource }
            'HorizonServer.scripts.Setup-Camera.ps1' { $setupResource }
            'HorizonServer.scripts.Stop-Camera.ps1' { $stopResource }
            'HorizonServer.scripts.Repair-CameraDriver.ps1' { $repairResource }
            default { $resource }
        }
        '/resource:' + $sourceResource + ',' + $_
    })
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /langversion:5 /codepage:65001 /target:exe /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ('/out:' + $testExecutable) @resourceArguments (Join-Path $projectDirectory 'src\ServerRuntime.cs') (Join-Path $projectDirectory 'src\ServerPaths.cs') (Join-Path $projectDirectory 'src\DriverInstaller.cs') (Join-Path $projectDirectory 'src\CameraDriverSafety.cs') (Join-Path $projectDirectory 'src\CameraDriverRepair.cs') (Join-Path $projectDirectory 'src\DiagnosticLog.cs') (Join-Path $projectDirectory 'src\UiText.cs') $testSource
    if ($LASTEXITCODE -ne 0) { throw 'Server runtime fixture compilation failed.' }
    & $testExecutable (Join-Path $fixtureRoot 'extracted')
    if ($LASTEXITCODE -ne 0) { throw 'Server runtime fixture checks failed.' }
    # Exercise the real in-memory initialization prefix only. It ends before
    # any mutex/backend/device operation and receives an inert dependency.
    foreach ($name in @('Setup-Camera.ps1', 'Stop-Camera.ps1')) {
        $sourcePath = Join-Path $projectDirectory ('scripts\' + $name)
        $tokens = $null; $errors = $null
        $sourceAst = [Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw ('Invalid script syntax: ' + $name) }
        $boundary = if ($name -eq 'Setup-Camera.ps1') {
            $sourceAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)
        } else {
            $sourceAst.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$executable' }, $true)
        }
        $prefix = $sourceAst.Extent.Text.Substring(0, $boundary.Extent.StartOffset)
        $memoryRoot = Join-Path $fixtureRoot "memory root 'quoted'"
        $probe = [ScriptBlock]::Create($prefix + '`n[pscustomobject]@{Root=$projectDirectory;Marker=(Get-HorizonMemoryMarker)}'.Replace('`n', "`n"))
        $result = & $probe -RuntimeRoot $memoryRoot -CameraRuntime { function Get-HorizonMemoryMarker { 'in-memory fixture' } }
        if ($result.Root -ne $memoryRoot -or $result.Marker -ne 'in-memory fixture' -or (Test-Path -LiteralPath $memoryRoot)) { throw ('In-memory context/dependency failed: ' + $name) }
    }
    # Evaluate the production expected-identity guard by itself. All DTOs are
    # synthetic; the matched branch never calls Stop-Process or device APIs.
    $identityGuard = $sourceAst.Find({ param($node) $node -is [Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text.StartsWith('$ExpectedProcessId -gt 0 -and') }, $true)
    if ($null -eq $identityGuard) { throw 'Missing exact shutdown identity guard.' }
    $ExpectedProcessId = 4411; $ExpectedStartTimeUtcTicks = '638000000000000000'; $ExpectedCameraSerial = 'HorizonClient-01234567890123456789012345678901'
    $projectDirectoryFixture = Join-Path $fixtureRoot 'identity-root'; $ExpectedKeyPath = Join-Path $projectDirectoryFixture 'data\camera.local.key'; $PassThru = $true
    $guardProbe = [ScriptBlock]::Create($identityGuard.Extent.Text + "`n[pscustomobject]@{Stopped=`$true}")
    foreach ($mismatch in @('none', 'absent', 'pid', 'start', 'root', 'key', 'serial')) {
        $backend = [pscustomobject]@{ ProcessId=4411;StartTimeUtcTicks=$ExpectedStartTimeUtcTicks;Root=$projectDirectoryFixture;KeyPath=$ExpectedKeyPath;CameraSerial=$ExpectedCameraSerial }
        switch ($mismatch) {
            'absent' { $backend = $null }
            'pid' { $backend.ProcessId++ }
            'start' { $backend.StartTimeUtcTicks = '638000000000000001' }
            'root' { $backend.Root += '-foreign' }
            'key' { $backend.KeyPath += '-foreign' }
            'serial' { $backend.CameraSerial += '1' }
        }
        $result = & { param($projectDirectory) & $guardProbe } $projectDirectoryFixture
        if ([bool]$result.Stopped -ne ($mismatch -eq 'none')) { throw ('Shutdown identity guard mismatch: ' + $mismatch) }
    }
    Write-Output 'In-memory source/context and exact shutdown identity checks passed: 9. No device, service or registry calls.'
    # Test the setup script's real parameter validation and pure retained-mode
    # selector without executing setup, enumerating devices or opening a key.
    foreach ($entry in @(
        @{ Path = (Join-Path $projectDirectory 'scripts\Camera-Runtime.ps1'); Name = 'Test-HorizonVideoMode' },
        @{ Path = (Join-Path $projectDirectory 'scripts\Setup-Camera.ps1'); Name = 'Test-HorizonSetupVideoMode' },
        @{ Path = (Join-Path $projectDirectory 'scripts\Setup-Camera.ps1'); Name = 'Get-HorizonSetupCompatibleMode' }
    )) {
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($entry.Path, [ref]$tokens, [ref]$errors)
        if ($errors.Count -ne 0) { throw ('PowerShell parse failure: ' + $entry.Path) }
        $functionName = $entry.Name
        $function = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName }, $true))
        if ($function.Count -ne 1) { throw ('Missing tested setup function: ' + $functionName) }
        . ([ScriptBlock]::Create($function[0].Extent.Text))
    }
    $setup = Join-Path $projectDirectory 'scripts\Setup-Camera.ps1'
    foreach ($mode in @(@{ Width = 2560; Height = 1440 }, @{ Width = 3840; Height = 2160 }, @{ Width = 7680; Height = 4320 }, @{ Width = 1922; Height = 1080 }, @{ Width = 1920; Height = 1082 })) {
        $rejected = $false
        try { & $setup @mode -PassThru | Out-Null }
        catch {
            if ($_.FullyQualifiedErrorId -like 'ParameterArgumentValidationError*') { $rejected = $true }
            else { throw }
        }
        if (-not $rejected) { throw 'Setup must reject new oversized modes before reading or modifying runtime files.' }
    }
    foreach ($mode in @(
        @{ Width = 2560; Height = 1440; FPS = 30; Uncompressed = $true },
        @{ Width = 3840; Height = 2160; FPS = 30; Uncompressed = $true },
        @{ Width = 3840; Height = 2160; FPS = 30; Uncompressed = $false },
        @{ Width = 7680; Height = 4320; FPS = 60; Uncompressed = $false },
        @{ Width = 7680; Height = 4320; FPS = 8; Uncompressed = $true }
    )) {
        $identity = [pscustomobject]($mode + @{ CameraSerial = 'HorizonClient-01234567890123456789012345678901'; CameraName = 'Existing fixture webcam'; H264Probe = $false })
        $normalized = Get-HorizonSetupCompatibleMode -Existing $identity
        if ($normalized.Width -ne 1920 -or $normalized.Height -ne 1080 -or $normalized.FPS -ne 30 -or
            $normalized.Uncompressed -ne $true -or $normalized.H264Probe -or
            $identity.Width -ne $mode.Width -or $identity.CameraSerial -cne 'HorizonClient-01234567890123456789012345678901' -or $identity.CameraName -cne 'Existing fixture webcam') {
            throw 'Legacy setup migration must select 1080p30 YUY2 and leave the source identity unchanged.'
        }
    }
    foreach ($mode in @(
        @{ Width = 1280; Height = 720; FPS = 60; Uncompressed = $true; H264Probe = $false },
        @{ Width = 1920; Height = 1080; FPS = 30; Uncompressed = $true; H264Probe = $false },
        @{ Width = 640; Height = 480; FPS = 15; Uncompressed = $true; H264Probe = $false }
    )) {
        $retained = Get-HorizonSetupCompatibleMode -Existing ([pscustomobject]$mode)
        foreach ($property in @('Width', 'Height', 'FPS', 'Uncompressed', 'H264Probe')) {
            if ($retained.$property -ne $mode[$property]) { throw 'Compatible existing modes must retain their native format settings.' }
        }
    }
    Write-Output 'Setup mode fixture checks passed (three YUY2 geometries only; legacy2K/4K/8K metadata remains readable and normalizes to1080p without identity or key changes).'
    & (Join-Path $PSScriptRoot 'SetupModes.Tests.ps1')
} finally {
    $canonicalFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $canonicalBuild = [IO.Path]::GetFullPath((Join-Path $projectDirectory 'build')) + [IO.Path]::DirectorySeparatorChar
    if ($canonicalFixture.StartsWith($canonicalBuild, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($canonicalFixture).StartsWith('server-runtime-tests-')) {
        [IO.Directory]::Delete($canonicalFixture, $true)
    }
}
