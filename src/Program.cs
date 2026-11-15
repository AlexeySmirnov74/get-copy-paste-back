using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace HorizonClient
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--export-third-party")
            {
                if (args.Length != 2) { Environment.ExitCode = 2; return; }
                try { ThirdPartyMaterials.Export(args[1]); Environment.ExitCode = 0; }
                catch (Exception error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 1; }
                return;
            }
#if HORIZON_SERVER
            if (args.Length == 7 && args[0] == "--configure-server")
            {
                try {
                    int width = Int32.Parse(args[2]), height = Int32.Parse(args[3]), fps = Int32.Parse(args[4]);
                    ValidateServerModeArguments(width, height, fps, args[5], args[6]);
                    Environment.ExitCode = RunServerHelper(args[1], "configure", delegate { return ServerRuntime.RunConfigure(args[1], width, height, fps, args[5] == "1", args[6] == "1"); });
                }
                catch { Environment.ExitCode = 1; }
                return;
            }
            if (args.Length == 2 && args[0] == "--remove-server")
            {
                try { Environment.ExitCode = RunServerHelper(args[1], "remove", delegate { return ServerRuntime.RunRemove(args[1]); }); }
                catch { Environment.ExitCode = 1; }
                return;
            }
            if (args.Length == 3 && args[0] == "--setup-server")
            {
                try {
                    ServerRuntime.ValidateCameraName(args[2]);
                    Environment.ExitCode = RunServerHelper(args[1], "setup", delegate { return ServerRuntime.RunSetup(args[1], args[2]); });
                }
                catch { Environment.ExitCode = 1; }
                return;
            }
            if (args.Length == 8 && args[0] == "--setup-server")
            {
                try
                {
                    ServerRuntime.ValidateCameraName(args[2]);
                    int width = Int32.Parse(args[3]), height = Int32.Parse(args[4]), fps = Int32.Parse(args[5]);
                    ValidateServerModeArguments(width, height, fps, args[6], args[7]);
                    Environment.ExitCode = RunServerHelper(args[1], "setup", delegate { return ServerRuntime.RunSetup(args[1], args[2], width, height, fps, args[6] == "1", args[7] == "1"); });
                }
                catch { Environment.ExitCode = 1; }
                return;
            }
            bool sender = true;
#else
            bool sender = false;
#endif
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string key = null;
                for (int i = 0; i < args.Length; i++)
                {
#if HORIZON_SERVER
                    if (args[i] == "--sender" || args[i] == "--server") sender = true;
                    else if (args[i] == "--receiver") return;
#else
                    if (args[i] == "--receiver") sender = false;
#endif
                    else if (args[i] == "--key" && i + 1 < args.Length) key = Path.GetFullPath(args[++i]);
                    else return;
                }
                bool created;
                using (Mutex instance = new Mutex(true, sender ? "Local\\HorizonServer" : "Local\\HorizonClient.Receiver", out created))
                {
                    if (!created) return;
#if HORIZON_SERVER
                    ServerPaths paths = ServerPaths.Current;
                    Directory.CreateDirectory(paths.DataDirectory);
                    string logPath = paths.DebugLog;
                    string settingsDirectory = paths.DataDirectory;
                    string logDirectory = paths.DataDirectory;
                    UiText.Load(paths.LanguageSetting);
                    bool detailedLogging = DiagnosticSettings.Read(settingsDirectory, true, true);
#else
                    ClientPaths.Current.ConfigureSettingsStore(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
                    string logPath = ClientPaths.Current.DebugLog;
                    string settingsDirectory = ClientPaths.Current.SettingsDirectory;
                    string logDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    UiText.Load(ClientPaths.Current.LanguageSetting);
                    bool detailedLogging = DiagnosticSettings.Read(settingsDirectory, false);
#endif
                    DiagnosticSettings.Apply(settingsDirectory, sender, detailedLogging, false, logDirectory);
                    DiagnosticLog.Write("application.started", "role", sender ? "server" : "client", "version", typeof(Program).Assembly.GetName().Version.ToString(), "log_path", logPath, "settings_directory", settingsDirectory, "ui_language", UiText.Id, "process_id", System.Diagnostics.Process.GetCurrentProcess().Id);
#if HORIZON_SERVER
                    DiagnosticLog.Write("server.storage", "root", paths.Root, "data_directory", paths.DataDirectory,
                        "debug_log", paths.DebugLog, "setup_debug_log", paths.SetupDebugLog, "debug_flag", paths.DebugFlag, "settings_file", paths.SettingsFile,
                        "language_setting", paths.LanguageSetting, "received_root", paths.ReceivedRoot, "logging_default_enabled", true);
#endif
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    Application.ThreadException += delegate(object source, ThreadExceptionEventArgs error) { DiagnosticLog.Error("application.thread_exception", error.Exception); };
                    AppDomain.CurrentDomain.UnhandledException += delegate(object source, UnhandledExceptionEventArgs error) { DiagnosticLog.Error("application.unhandled_exception", error.ExceptionObject as Exception, "terminating", error.IsTerminating); DiagnosticLog.Shutdown(500); };
#if HORIZON_SERVER
                    using (var monitor = new UiResponsivenessMonitor()) Application.Run(key == null ? new ServerForm() : new ServerForm(key));
#else
                        using (var monitor = new UiResponsivenessMonitor())
                        using (ReceiverContext receiver = new ReceiverContext()) Application.Run(receiver);
#endif
                }
            }
            catch (Exception error)
            {
                DiagnosticLog.Error("application.failed", error);
                if (sender) MessageBox.Show(error.Message, "Get Copy-Paste Back — Server", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { DiagnosticLog.Write("application.stopped"); DiagnosticLog.Shutdown(); }
        }

#if HORIZON_SERVER
        private static void ValidateServerModeArguments(int width, int height, int fps, string raw, string probe)
        {
            if ((raw != "0" && raw != "1") || (probe != "0" && probe != "1"))
                throw new ArgumentException("Invalid setup video mode flags.");
            ServerRuntime.ValidateVideoMode(width, height, fps, raw == "1", probe == "1");
        }

        // An elevated helper owns a separate writer. It must never open the
        // interactive server's log, copy a legacy key, or initialize storage for
        // an arbitrary command-line root. Argument validation precedes this call.
        private static int RunServerHelper(string root, string operation, Func<int> action)
        { return RunServerHelperCore(ServerPaths.Current, root, operation, action); }

        private static int RunServerHelperCore(ServerPaths paths, string root, string operation, Func<int> action)
        {
            if (paths == null || action == null || (operation != "setup" && operation != "configure" && operation != "remove")) return 1;
            try
            {
                if (String.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root) ||
                    !String.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), paths.Root, StringComparison.OrdinalIgnoreCase)) return 1;
            }
            catch (ArgumentException) { return 1; }
            catch (NotSupportedException) { return 1; }
            catch (PathTooLongException) { return 1; }
            catch (System.Security.SecurityException) { return 1; }
            try
            {
                paths.ValidateHelperStorage();
                Directory.CreateDirectory(paths.DataDirectory);
                paths.ValidateHelperStorage();
                if (DiagnosticSettings.Read(paths.DataDirectory, true, true))
                {
                    paths.ValidateHelperStorage();
                    DiagnosticLog.Initialize(paths.SetupDebugLog, queueCapacity: 2048);
                }
                DiagnosticLog.Write("server.helper.started", "role", "server_helper", "operation", operation,
                    "version", typeof(Program).Assembly.GetName().Version.ToString(), "root", paths.Root, "log_path", paths.SetupDebugLog);
                int result = action();
                DiagnosticLog.Write("server.helper.finished", "operation", operation, "exit_code", result);
                return result;
            }
            catch (Exception error) { DiagnosticLog.Error("server.helper.failed", error, "operation", operation, "root", paths.Root); return 1; }
            finally { DiagnosticLog.Shutdown(); }
        }
#endif

    }
}
