// Portable path/startup/helper contracts; isolated fixture files and hidden UI.
// No installer, driver, camera, service, audio device or clipboard operation.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

internal static class ServerStorageTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static int checks;
    private static readonly List<Thread> ownedLogWriters = new List<Thread>();
    [STAThread]
    private static int Main()
    {
        UiText.SetLanguage(UiLanguage.Russian); // Restart-status assertions intentionally use Russian.
        string fixture = Path.GetFullPath(Path.Combine("build", "server-storage-" + Guid.NewGuid().ToString("N")));
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var paths = new ServerPaths(Path.Combine(fixture, "package"));
            CameraLifetimePreference(fixture);
            SoundPreferences(fixture);
            PathsArePure(paths); InvalidHelpersArePure(paths); ReparseHelpersArePure(fixture); LoggingAndHelpers(paths); HiddenFormDoesNotWrite(fixture);
            Console.WriteLine("Server storage checks passed: " + checks + ". Portable fixture files and fake helper callbacks only; no profile, driver or device access.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            CaptureLogWriter();
            bool stopped = DiagnosticLog.Shutdown(10000);
            foreach (Thread writer in ownedLogWriters)
                if (!writer.Join(10000)) stopped = false;
            UiText.SetLanguage(UiLanguage.Russian);
            if (!stopped) throw new Exception("Fixture log writers must stop before their files are removed.");
            string build = Path.GetFullPath("build") + Path.DirectorySeparatorChar;
            if (Directory.Exists(fixture) && fixture.StartsWith(build, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(fixture).StartsWith("server-storage-")) Directory.Delete(fixture, true);
        }
    }

    private static void CameraLifetimePreference(string fixture)
    {
        var paths = new ServerPaths(Path.Combine(fixture, "camera-lifetime"));
        Directory.CreateDirectory(paths.DataDirectory);
        FieldInfo currentPaths = typeof(ServerPaths).GetField("Current", BindingFlags.Static | BindingFlags.NonPublic);
        object previous = currentPaths.GetValue(null); currentPaths.SetValue(null, paths);
        try
        {
            foreach (string saved in new[] { "", "true", "false", "invalid" })
            {
                string original = "<HorizonServer" + (saved.Length == 0 ? "" : " keepCameraRunning='" + saved + "'") + " />";
                File.WriteAllText(paths.SettingsFile, original);
                using (ServerForm form = PreferenceForm())
                {
                    CheckBox keep = PreferenceCheckBox(form);
                    Check(keep.Checked, "The fresh server keeps the camera running after exit by default.");
                    LoadPreference(form);
                    Check(keep.Checked == (saved != "false"), "A saved false still stops the camera; old and malformed preferences keep the new enabled default.");
                    Check(File.ReadAllText(paths.SettingsFile) == original, "Restoring the camera lifetime preference does not rewrite the user's settings.");
                    keep.Checked = !keep.Checked;
                    var document = new System.Xml.XmlDocument(); document.Load(paths.SettingsFile);
                    Check(document.DocumentElement.GetAttribute("keepCameraRunning") == keep.Checked.ToString(),
                        "Changing the checkbox immediately saves the exact preference using the actual portable XML writer.");
                    using (ServerForm restarted = PreferenceForm())
                    {
                        LoadPreference(restarted);
                        Check(PreferenceCheckBox(restarted).Checked == keep.Checked, "A restarted server restores either saved checkbox value.");
                    }
                }
            }
            Check(Directory.GetFiles(paths.DataDirectory, "*.tmp").Length == 0, "Lifetime preference atomic writes leave no temporary settings files.");
        }
        finally { currentPaths.SetValue(null, previous); }
    }

    private static void SoundPreferences(string fixture)
    {
        var paths = new ServerPaths(Path.Combine(fixture, "sound-preferences")); Directory.CreateDirectory(paths.DataDirectory);
        FieldInfo currentPaths = typeof(ServerPaths).GetField("Current", BindingFlags.Static | BindingFlags.NonPublic);
        object previous = currentPaths.GetValue(null); currentPaths.SetValue(null, paths);
        try
        {
            foreach (string saved in new[] { "", "true", "false", "invalid" })
            {
                string original = "<HorizonServer" + (saved.Length == 0 ? "" : " acceptedSoundEnabled='" + saved + "' deliveredSoundEnabled='" + saved + "'") + " />";
                File.WriteAllText(paths.SettingsFile, original);
                using (ServerForm form = PreferenceForm())
                {
                    var accepted = (CheckBox)form.Controls.Find("AcceptedSoundEnabled", true)[0];
                    var delivered = (CheckBox)form.Controls.Find("DeliveredSoundEnabled", true)[0];
                    Check(accepted.Checked && delivered.Checked, "Both forward-copy sounds default on.");
                    LoadPreference(form);
                    Check(accepted.Checked == (saved != "false") && delivered.Checked == (saved != "false"), "Saved disabled sound values override defaults; missing or invalid values keep sound enabled.");
                    Check(File.ReadAllText(paths.SettingsFile) == original, "Loading sound choices is read-only.");
                    accepted.Checked = !accepted.Checked;
                    var document = new System.Xml.XmlDocument(); document.Load(paths.SettingsFile);
                    Check(document.DocumentElement.GetAttribute("acceptedSoundEnabled") == accepted.Checked.ToString() &&
                        document.DocumentElement.GetAttribute("deliveredSoundEnabled") == delivered.Checked.ToString(), "Changing start sound saves both independent preferences immediately.");
                    delivered.Checked = !delivered.Checked;
                    using (ServerForm restarted = PreferenceForm())
                    {
                        LoadPreference(restarted);
                        Check(((CheckBox)restarted.Controls.Find("AcceptedSoundEnabled", true)[0]).Checked == accepted.Checked &&
                            ((CheckBox)restarted.Controls.Find("DeliveredSoundEnabled", true)[0]).Checked == delivered.Checked, "Both sound choices survive a fresh server instance.");
                    }
                }
            }
        }
        finally { currentPaths.SetValue(null, previous); }
    }

    private static ServerForm PreferenceForm()
    {
        return new ServerForm(null, true, delegate { throw new Exception("No camera access."); },
            new ServerClipboardServices { Sequence = delegate { throw new Exception("No clipboard access."); }, Capture = delegate { throw new Exception("No clipboard access."); }, Accepted = delegate { throw new Exception("No sound."); } },
            delegate { throw new Exception("No dialog."); }, delegate { throw new Exception("No sound."); }, delegate { throw new Exception("No notification."); });
    }

    private static CheckBox PreferenceCheckBox(ServerForm form)
    {
        Control[] found = form.Controls.Find("KeepCameraRunning", true);
        Check(found.Length == 1 && found[0] is CheckBox, "The server must expose the persisted keep-camera checkbox.");
        return (CheckBox)found[0];
    }

    private static void LoadPreference(ServerForm form)
    {
        typeof(ServerForm).GetField("restoringSettings", Instance).SetValue(form, true);
        try { typeof(ServerForm).GetMethod("LoadSettings", Instance).Invoke(form, null); }
        finally { typeof(ServerForm).GetField("restoringSettings", Instance).SetValue(form, false); }
    }

    private static void PathsArePure(ServerPaths paths)
    {
        Check(!Directory.Exists(paths.BaseDirectory), "Resolving server storage cannot create its package folder.");
        Check(paths.Root == Path.Combine(paths.BaseDirectory, "HorizonServer"), "The server owns one portable folder beside the EXE.");
        Check(paths.DataDirectory == Path.Combine(paths.Root, "data"), "Configuration and logs share the portable data directory.");
        Check(paths.DebugLog == Path.Combine(paths.DataDirectory, "server.debug.log") && paths.SetupDebugLog == Path.Combine(paths.DataDirectory, "setup.debug.log"), "Interactive and elevated processes have separate log files.");
        Check(paths.DebugFlag == DiagnosticSettings.FlagPath(paths.DataDirectory, true), "The logging flag is stored in portable data.");
        Check(paths.LanguageSetting == Path.Combine(paths.DataDirectory, "ui.language") && paths.SettingsFile == Path.Combine(paths.DataDirectory, "server.settings.xml"), "Language and user configuration stay portable.");
        Check(paths.ReceivedRoot == Path.Combine(paths.Root, "Received"), "Received files belong to the portable server folder.");
        var normalized = new ServerPaths(paths.BaseDirectory + Path.DirectorySeparatorChar);
        Check(normalized.BaseDirectory == paths.BaseDirectory && !Directory.Exists(paths.Root), "Trailing separators normalize without filesystem work.");
        foreach (string invalid in new[] { null, "", "relative", @"C:relative", @"\not-absolute" })
        {
            bool rejected = false; try { new ServerPaths(invalid); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Nonabsolute or empty storage locations cannot become a fallback user-profile directory.");
        }
        Check(DiagnosticSettings.Read(paths.DataDirectory, true, true), "A missing server flag defaults to enabled when explicitly requested.");
        Check(!DiagnosticSettings.Read(paths.DataDirectory, false) && !Directory.Exists(paths.BaseDirectory), "Client default remains disabled and checking defaults creates no folders.");
    }

    private static void InvalidHelpersArePure(ServerPaths paths)
    {
        int calls = 0; Func<int> action = delegate { calls++; return 0; };
        Check(Helper(paths, Path.Combine(paths.BaseDirectory, "foreign-runtime"), "setup", action) == 1 && calls == 0, "A foreign helper root cannot execute its action.");
        Check(Helper(paths, paths.Root, "unknown", action) == 1 && calls == 0, "An unknown helper operation cannot initialize logging.");
        Check(Helper(paths, "relative", "remove", action) == 1 && calls == 0, "Relative roots cannot initialize or remove any runtime.");
        Check(Helper(paths, paths.Root, "setup", null) == 1 && !Directory.Exists(paths.BaseDirectory), "Invalid helper requests perform no filesystem writes.");
        MethodInfo validate = typeof(Program).GetMethod("ValidateServerModeArguments", Static);
        foreach (object[] args in new[] {
            new object[] { 3840, 2160, 30, "1", "0" }, new object[] { 1920, 1080, 30, "2", "0" },
            new object[] { 1920, 1080, 30, "1", "1" }, new object[] { 1280, 720, 0, "1", "0" }
        })
        {
            bool rejected = false; try { validate.Invoke(null, args); } catch (TargetInvocationException error) { rejected = error.InnerException is ArgumentException; }
            Check(rejected, "Invalid elevated mode arguments fail before helper logging or driver access.");
        }
        Check(!Directory.Exists(paths.BaseDirectory), "Argument validation does not create server storage.");
    }

    private static void LoggingAndHelpers(ServerPaths paths)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        Check(DiagnosticSettings.Apply(paths.DataDirectory, true, DiagnosticSettings.Read(paths.DataDirectory, true, true), false), "Production server startup enables its portable log by default.");
        DiagnosticLog.Write("fixture.server.started");
        CaptureLogWriter();
        Check(DiagnosticLog.Shutdown(10000), "The interactive fixture log must finish writing before its file is inspected.");
        Check(File.Exists(paths.DebugLog) && !File.Exists(Path.Combine(paths.BaseDirectory, "server.debug.log")), "The server logger writes inside HorizonServer/data rather than beside the EXE.");
        string original = File.ReadAllText(paths.DebugLog); int calls = 0;
        Check(Helper(paths, paths.Root, "setup", delegate { calls++; DiagnosticLog.Write("fixture.driver.action"); return 7; }) == 7 && calls == 1,
            "The helper preserves its actual action exit code without running a real setup.");
        Check(!DiagnosticLog.IsEnabled && File.ReadAllText(paths.SetupDebugLog).Contains("fixture.driver.action"), "The elevated helper writes its own log and closes the writer afterwards.");
        Check(File.ReadAllText(paths.DebugLog) == original, "Helper logging cannot overwrite the interactive process log.");
        Check(Helper(paths, paths.Root, "configure", delegate { throw new IOException("fixture helper failure"); }) == 1 && !DiagnosticLog.IsEnabled,
            "Helper exceptions produce failure and always close the writer.");
        Check(File.ReadAllText(paths.SetupDebugLog).Contains("server.helper.failed"), "The helper's own log records its failure.");
        Check(!DiagnosticSettings.Apply(paths.DataDirectory, true, false, true), "The user can disable the default-enabled server log.");
        Check(File.ReadAllText(paths.DebugFlag) == "0" && !DiagnosticSettings.Read(paths.DataDirectory, true, true), "An explicit disabled flag survives enabled-by-default startup.");
        File.Delete(paths.SetupDebugLog);
        Check(Helper(paths, paths.Root, "remove", delegate { calls++; return 0; }) == 0 && calls == 2 && !File.Exists(paths.SetupDebugLog),
            "A disabled helper still executes but cannot create a new diagnostic log.");
        UiText.SetLanguage(UiLanguage.English); UiText.Save(paths.LanguageSetting); UiText.Load(paths.LanguageSetting);
        Check(UiText.Language == UiLanguage.English && File.ReadAllText(paths.LanguageSetting) == "en", "The portable language location round-trips its explicit setting.");
        UiText.SetLanguage(UiLanguage.Russian);
        Check(Directory.GetFiles(paths.DataDirectory, "*.tmp").Length == 0, "Atomic settings writes leave no temporary files.");
    }

    private static void ReparseHelpersArePure(string fixture)
    {
        for (int component = 0; component < 5; component++)
        {
            string directory = Path.Combine(fixture, "blocked-" + component);
            var expected = new ServerPaths(directory);
            string blocked = new[] { fixture, directory, expected.Root, expected.DataDirectory, expected.SetupDebugLog }[component];
            bool redirected = false, readPastRedirect = false; int calls = 0;
            var paths = new ServerPaths(directory, delegate(string path) {
                if (redirected) readPastRedirect = true;
                if (String.Equals(path, blocked, StringComparison.OrdinalIgnoreCase))
                { redirected = true; return FileAttributes.ReparsePoint | FileAttributes.Directory; }
                if (path == expected.SetupDebugLog) throw new FileNotFoundException();
                return FileAttributes.Directory;
            });
            Check(Helper(paths, paths.Root, "setup", delegate { calls++; return 0; }) == 1 && calls == 0 && !Directory.Exists(directory),
                "A reparse ancestor, base, root, data or setup log is rejected before creating storage or running an elevated action.");
            Check(redirected && !readPastRedirect && !DiagnosticLog.IsEnabled,
                "Ancestor validation stops before following the redirection and does not start a writer.");
        }

        string postDirectory = Path.Combine(fixture, "post-create"); var postExpected = new ServerPaths(postDirectory); int postCalls = 0;
        var postPaths = new ServerPaths(postDirectory, delegate(string path) {
            if (path == postExpected.DataDirectory && Directory.Exists(postExpected.DataDirectory)) return FileAttributes.Directory | FileAttributes.ReparsePoint;
            if (path == postExpected.SetupDebugLog) throw new FileNotFoundException();
            return FileAttributes.Directory;
        });
        Check(Helper(postPaths, postPaths.Root, "configure", delegate { postCalls++; return 0; }) == 1 && postCalls == 0 &&
            Directory.Exists(postPaths.DataDirectory) && !File.Exists(postPaths.SetupDebugLog),
            "A component changing after mkdir is rejected before log creation or the helper action.");

        string openDirectory = Path.Combine(fixture, "pre-open"); var openExpected = new ServerPaths(openDirectory); int inspections = 0, openCalls = 0;
        var openPaths = new ServerPaths(openDirectory, delegate(string path) {
            if (path == openExpected.SetupDebugLog)
            {
                if (++inspections == 3) return FileAttributes.ReparsePoint;
                throw new FileNotFoundException();
            }
            return FileAttributes.Directory;
        });
        Check(Helper(openPaths, openPaths.Root, "remove", delegate { openCalls++; return 0; }) == 1 && inspections == 3 && openCalls == 0 &&
            !File.Exists(openPaths.SetupDebugLog), "The log file is checked again immediately before its writer is initialized.");

        string deniedDirectory = Path.Combine(fixture, "denied"); int deniedCalls = 0;
        var deniedPaths = new ServerPaths(deniedDirectory, delegate(string path) { throw new UnauthorizedAccessException("fixture attributes denied"); });
        Check(Helper(deniedPaths, deniedPaths.Root, "setup", delegate { deniedCalls++; return 0; }) == 1 && deniedCalls == 0 && !Directory.Exists(deniedDirectory),
            "Attribute access errors fail closed rather than being mistaken for missing directories.");

        string fileDirectory = Path.Combine(fixture, "file-ancestor"); var fileExpected = new ServerPaths(fileDirectory);
        var filePaths = new ServerPaths(fileDirectory, delegate(string path) { return path == fileExpected.Root ? FileAttributes.Normal : FileAttributes.Directory; });
        Check(Helper(filePaths, filePaths.Root, "setup", delegate { throw new Exception("No helper action."); }) == 1 && !Directory.Exists(fileDirectory),
            "A regular file cannot be treated as a safe directory ancestor.");
    }

    private static void HiddenFormDoesNotWrite(string fixture)
    {
        string unused = Path.Combine(fixture, "hidden-form-runtime");
        var clipboard = new ServerClipboardServices { Sequence = delegate { throw new Exception("No clipboard sequence query."); }, Capture = delegate { throw new Exception("No clipboard access."); }, Accepted = delegate { throw new Exception("No sound."); } };
        using (var form = new ServerForm(null, false, delegate { throw new Exception("No camera."); }, clipboard))
        {
            var runtime = typeof(ServerForm).GetField("runtime", Instance).GetValue(form);
            typeof(ServerRuntime).GetProperty("RootDirectory").GetSetMethod(true).Invoke(runtime, new object[] { unused });
            typeof(ServerForm).GetMethod("SaveSettings", Instance).Invoke(form, null);
            Check(!form.Visible && !Directory.Exists(unused) && !DiagnosticLog.IsEnabled, "Hidden test forms cannot silently initialize settings or default-on logging.");
            string oldRuntime = Path.Combine(fixture, "old-confirmed-runtime");
            string oldKey = Path.Combine(oldRuntime, "data", "camera.local.key");
            string trace = (string)typeof(ServerForm).GetMethod("CameraTraceRoot", Instance).Invoke(form, new object[] { oldKey });
            Check(trace == oldRuntime && !Directory.Exists(oldRuntime), "Legacy camera diagnostics use their actual root without opening or copying a key.");
            typeof(ServerForm).GetMethod("ShowFailure", Instance).Invoke(form, new object[] { new DriverRestartRequiredException(3010, "fixture") });
            Check(((Label)typeof(ServerForm).GetField("StateLabel", Instance).GetValue(form)).Text.Contains("Требуется перезагрузка"), "A successful driver install requesting restart is not presented as ordinary camera failure.");
            UiText.SetLanguage(UiLanguage.English); UiText.Apply(form);
            Check(((Label)typeof(ServerForm).GetField("DetailLabel", Instance).GetValue(form)).Text.Contains("Restart Windows, then click Create camera again"), "The manual reboot instruction translates without running another installer.");
            Check(!Directory.Exists(unused), "Restart guidance cannot create runtime files or schedule a reboot.");
        }
    }

    private static int Helper(ServerPaths paths, string root, string operation, Func<int> action)
    {
        Thread writer = null;
        Func<int> wrapped = action == null ? null : new Func<int>(delegate
        {
            // The production helper detaches its logger during bounded shutdown.
            // Retain this fixture's worker before that reference disappears.
            writer = CaptureLogWriter();
            return action();
        });
        try { return (int)typeof(Program).GetMethod("RunServerHelperCore", Static).Invoke(null, new object[] { paths, root, operation, wrapped }); }
        finally
        {
            if (writer != null) Check(writer.Join(10000), "The fixture helper log must finish writing before inspection or cleanup.");
        }
    }

    private static Thread CaptureLogWriter()
    {
        object writer = typeof(DiagnosticLog).GetField("current", Static).GetValue(null);
        if (writer == null) return null;
        Thread thread = (Thread)writer.GetType().GetField("thread", Instance).GetValue(writer);
        if (!ownedLogWriters.Contains(thread)) ownedLogWriters.Add(thread);
        return thread;
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
