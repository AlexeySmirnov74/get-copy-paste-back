using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HorizonClient
{
    internal sealed class CameraDriverRestartRequiredException : IOException
    {
        internal CameraDriverRestartRequiredException()
            : base(UiText.Get("Конфликт драйверов устранён. Перезагрузите Windows и нажмите «Создать камеру».", "The driver conflict is resolved. Restart Windows, then click “Create camera”.")) { }
    }

    internal sealed class CameraDriverRepairResult
    {
        internal bool Changed;
        internal bool RestartRequired;
        internal uint? DriverState;
        internal string Operation;
        internal string ReleaseReason;
        internal string Error;
    }

    internal static class CameraDriverRepair
    {
        private const int MaximumOutputBytes = 65536;

        // Called only by the elevated setup/configure child. The script is read
        // from this executable in memory; the GUI itself never writes SCM state.
        internal static CameraDriverRepairResult RunElevated(string runtimeRoot)
        {
            if (String.IsNullOrWhiteSpace(runtimeRoot)) throw new ArgumentException("Missing runtime root.", "runtimeRoot");
            string root = Path.GetFullPath(runtimeRoot);
            string command = "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" +
                "[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false);try { " +
                ServerRuntime.EmbeddedScriptCommand("Repair-CameraDriver.ps1") + " -RuntimeRoot " + PowerShellLiteral(root) +
                " -Apply;exit 0 } catch { [Console]::Error.WriteLine($_.Exception.Message);exit 1 }";
            string output;
            DiagnosticLog.Write("server.camera.driver_repair.started");
            int exitCode = RunProcess(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                "-NoLogo -NoProfile -NonInteractive -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(command)), root, out output);
            if (exitCode != 0)
            {
                // This output is limited to service metadata and script errors;
                // no clipboard content, camera stream or keys enter the helper.
                DiagnosticLog.Write("server.camera.driver_repair.failed", "exit_code", exitCode, "helper_output", output);
                throw new IOException(UiText.Get("Не удалось автоматически восстановить камеру. Подробности записаны в server.debug.log.", "Automatic camera recovery failed. Details are recorded in server.debug.log."));
            }
            CameraDriverRepairResult result = ParseResult(output);
            DiagnosticLog.Write("server.camera.driver_repair.completed", "changed", result.Changed,
                "restart_required", result.RestartRequired, "operation", result.Operation,
                "driver_state", result.DriverState.HasValue ? (object)result.DriverState.Value : null,
                "release_reason", result.ReleaseReason, "release_error", result.Error);
            if (result.RestartRequired) throw new CameraDriverRestartRequiredException();
            return result;
        }

        // Pure JSON boundary for tests. Never treat malformed/missing flags or
        // an unknown driver state as successful repair of a loaded driver.
        internal static CameraDriverRepairResult ParseResult(string json)
        {
            if (String.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumOutputBytes)
                throw new IOException(UiText.Get("Некорректный ответ восстановления камеры.", "Invalid camera recovery response."));
            Dictionary<string, object> values;
            try { values = new JavaScriptSerializer { MaxJsonLength = MaximumOutputBytes }.Deserialize<Dictionary<string, object>>(json.Trim().TrimStart('\ufeff')); }
            catch (ArgumentException error) { throw new IOException(UiText.Get("Некорректный ответ восстановления камеры.", "Invalid camera recovery response."), error); }
            catch (InvalidOperationException error) { throw new IOException(UiText.Get("Некорректный ответ восстановления камеры.", "Invalid camera recovery response."), error); }
            object schema;
            if (values == null || !values.TryGetValue("Schema", out schema) || !(schema is int) || (int)schema != 1 || !ReadBoolean(values, "Success"))
                throw new IOException(UiText.Get("Восстановление камеры не завершено.", "Camera recovery is incomplete."));
            var result = new CameraDriverRepairResult {
                Changed = ReadBoolean(values, "Changed"), RestartRequired = ReadBoolean(values, "RestartRequired")
            };
            object operation;
            if (!values.TryGetValue("Operation", out operation) || !(operation is string) ||
                ((string)operation != "none" && (string)operation != "disable_known_conflicting_monitor_startup"))
                throw new IOException(UiText.Get("Неизвестный результат восстановления камеры.", "Unknown camera recovery result."));
            result.Operation = (string)operation;
            result.ReleaseReason = ReadOptionalText(values, "ReleaseReason");
            result.Error = ReadOptionalText(values, "Error");
            object state;
            if (!values.TryGetValue("DriverState", out state)) throw new IOException(UiText.Get("Не указано состояние драйвера камеры.", "The camera driver state is missing."));
            if (state != null)
            {
                if (!(state is int) || (int)state < 1 || (int)state > 7) throw new IOException(UiText.Get("Неизвестное состояние драйвера камеры.", "Unknown camera driver state."));
                result.DriverState = (uint)(int)state;
            }
            if (!result.RestartRequired && result.DriverState.HasValue && result.DriverState.Value != 1)
                throw new IOException(UiText.Get("Конфликтующий драйвер камеры всё ещё загружен.", "The conflicting camera driver is still loaded."));
            return result;
        }

        private static bool ReadBoolean(Dictionary<string, object> values, string name)
        {
            object value;
            if (!values.TryGetValue(name, out value) || !(value is bool)) throw new IOException(UiText.Get("Некорректный ответ восстановления камеры: ", "Invalid camera recovery response: ") + name + ".");
            return (bool)value;
        }

        private static string ReadOptionalText(Dictionary<string, object> values, string name)
        {
            object value;
            if (!values.TryGetValue(name, out value) || value == null) return null;
            if (!(value is string) || ((string)value).Length > 4096) throw new IOException(UiText.Get("Некорректный ответ восстановления камеры.", "Invalid camera recovery response."));
            return (string)value;
        }

        private static string PowerShellLiteral(string value) { return "'" + value.Replace("'", "''") + "'"; }

        private static int RunProcess(string executable, string arguments, string root, out string output)
        {
            var text = new StringBuilder();
            var errors = new StringBuilder();
            var closed = new TaskCompletionSource<bool>();
            var errorsClosed = new TaskCompletionSource<bool>();
            var start = new ProcessStartInfo(executable, arguments) {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = new Process { StartInfo = start })
            {
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs data) {
                    if (data.Data == null) closed.TrySetResult(true); else AppendBounded(text, data.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs data) {
                    if (data.Data == null) errorsClosed.TrySetResult(true); else AppendBounded(errors, data.Data);
                };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                if (!process.WaitForExit(90000))
                {
                    DiagnosticLog.Write("server.camera.driver_repair.slow_waiting", "process_id", process.Id);
                    while (!process.WaitForExit(1000)) { }
                }
                if (!Task.WaitAll(new Task[] { closed.Task, errorsClosed.Task }, 1000))
                {
                    try { process.CancelOutputRead(); } catch (InvalidOperationException) { }
                    try { process.CancelErrorRead(); } catch (InvalidOperationException) { }
                }
                lock (text) { output = text.ToString(); }
                if (process.ExitCode != 0) { lock (errors) { output += errors.ToString(); } }
                return process.ExitCode;
            }
        }

        private static void AppendBounded(StringBuilder text, string value)
        {
            lock (text)
            {
                int available = MaximumOutputBytes - text.Length;
                if (available > 0) text.Append(value, 0, Math.Min(value.Length, available)).AppendLine();
            }
        }
    }
}
