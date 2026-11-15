using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace HorizonClient
{
    internal sealed partial class ServerForm
    {
        private readonly Timer benchmarkUiTimer = new Timer();
        private bool benchmarkTableFinalized;
        private FileBenchmarkRunner displayedBenchmark;
        private VideoModeOption benchmarkVideoMode;
        private int benchmarkClipboardGeneration;
        private bool benchmarkFormatWarningShown;

        private bool IsBenchmarkRunning { get { FileBenchmarkRunner run = speedTest; return run != null && !run.Finished; } }
        private bool IsBenchmarkBusy { get { FileBenchmarkRunner run = speedTest; return run != null && !run.TailComplete; } }

        private void InitializeBenchmarkEvents()
        {
            benchmarkUiTimer.Interval = 250;
            benchmarkUiTimer.Tick += delegate { RefreshBenchmark(); };
            benchmarkStop.Click += delegate { StopBenchmark(); };
        }

        private void StartBenchmark()
        {
            if (!HasCreatedCamera || !ready || preparing || IsClosing || IsBenchmarkBusy)
            {
                DiagnosticLog.Write("server.benchmark.start_ignored", "camera_created", HasCreatedCamera, "ready", ready, "preparing", preparing, "closing", IsClosing,
                    "benchmark_busy", IsBenchmarkBusy, "ui_action", lastUiAction);
                return;
            }
            if (activeVideoMode == null)
            {
                UiText.SetText(StateLabel, "Проверка скорости не запущена.", "Speed test was not started.");
                WarnUser("source_format_unknown", "Не удалось определить формат камеры сервера. Дождитесь подключения камеры и повторите проверку.", "Could not determine the server camera format. Wait for the camera to connect and try again.");
                return;
            }
            benchmarkVideoMode = activeVideoMode;
            VideoProfile[] profilesToTest = GetAvailableTransferProfiles();
            byte[] seedBytes = new byte[8]; using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(seedBytes);
            var run = new FileBenchmarkRunner(bridgeSession, profilesToTest, SelectedBenchmarkBytes, BitConverter.ToUInt64(seedBytes, 0), SelectedBenchmarkLimitSeconds * 1000L,
                benchmarkVideoMode.Width, benchmarkVideoMode.Height);
            DiagnosticLog.Write("server.benchmark.start", "session", bridgeSession, "fileBytes", SelectedBenchmarkBytes,
                "profileCount", profilesToTest.Length, "maxProfileMs", SelectedBenchmarkLimitSeconds * 1000L,
                "audioOutput", selectedAudioId ?? "default", "cameraFormat", benchmarkVideoMode == null ? "unknown" : benchmarkVideoMode.ToString(),
                "selectedCameraFormat", cameraMode.Text, "selected_format_applied", benchmarkVideoMode != null && benchmarkVideoMode.Matches(cameraMode.SelectedItem as VideoModeOption),
                "speedDefinition", "unique verified file bytes / elapsed receiver time; duplicates do not add bytes");
            if (loadSavedSettings) { HorizonCameraDiagnostics.Capture(null, "server_benchmark_started"); SaveSettings(); }
            CancelForwardJournal(UiText.Get("запущена проверка скорости", "speed test started"));
            ResetClipboardObservation("benchmark_started");
            benchmarkClipboardGeneration = generation;
            lock (stateLock) { speedTest = run; current = null; lastFingerprint = null; benchmarkDataTest = Guid.Empty; benchmarkDataFrames = 0; }
            if (reverseAudio != null) reverseAudio.SetBenchmarkMode(true);
            displayedBenchmark = run; benchmarkTableFinalized = false; benchmarkFormatWarningShown = false;
            benchmarkResults.Rows.Clear();
            foreach (VideoProfile profile in profilesToTest)
            {
                int row = benchmarkResults.Rows.Add(BenchmarkProfileCaption(profile), UiText.Get("Ожидает", "Waiting"), "—", "0", SelectedBenchmarkBytes.ToString("N0", UiText.Culture), "—", "—");
                benchmarkResults.Rows[row].Tag = profile;
                benchmarkResults.Rows[row].Cells["Profile"].ToolTipText = ProfileCaptions[(int)profile];
            }
            UiText.SetText(StateLabel, "Проверка разрешения камеры Horizon перед передачей тестового файла.", "Checking the Horizon camera resolution before sending the test file.");
            UpdateBenchmarkGeometry(null);
            benchmarkUiTimer.Start(); UpdateBenchmarkAvailability(true); RefreshBenchmark();
        }

        private void StopBenchmark()
        {
            FileBenchmarkRunner run = speedTest;
            if (run == null || run.Finished) return;
            DiagnosticLog.Write("server.benchmark.stop.requested", "session", bridgeSession, "profileIndex", run.CurrentIndex);
            if (loadSavedSettings) HorizonCameraDiagnostics.Capture(null, "server_benchmark_stopped");
            run.Cancel(); RefreshBenchmark();
        }

        private void OnFileBenchmarkReport(FileBenchmarkReport report)
        {
            FileBenchmarkRunner run = speedTest ?? displayedBenchmark;
            if (run == null) return;
            int passedBefore = CountPassed(run.Snapshot());
            bool wasFinished = run.Finished;
            run.Receive(report);
            ProfileTestResult[] rows = run.Snapshot();
            int passedAfter = CountPassed(rows);
            if (!wasFinished || passedAfter > passedBefore) RefreshBenchmark();
            if (wasFinished && passedAfter > passedBefore)
            {
                UiText.SetText(benchmarkProgressLabel, "Получено позднее подтверждение файла. Полностью прошли: {0} / {1}.", "Received a late file confirmation. Fully passed: {0} / {1}.", passedAfter, rows.Length);
                SaveBenchmarkResults(rows, run.TotalMilliseconds);
                DiagnosticLog.Write("server.benchmark.late_completion_saved", "test", report.Test, "profile", (int)report.Profile, "passed", passedAfter);
            }
        }

        private static int CountPassed(ProfileTestResult[] rows)
        { int count = 0; foreach (ProfileTestResult row in rows) if (row.State == ProfileTestState.Passed) count++; return count; }

        private void OnCompactFileBenchmarkReport(byte[] wire)
        {
            FileBenchmarkRunner run = speedTest ?? displayedBenchmark;
            if (run == null) return;
            FileBenchmarkReport report = null;
            foreach (FileBenchmarkCommand command in run.ReportCommands())
                if (CompactFileBenchmarkReport.TryDecode(wire, command, out report)) break;
            if (report == null)
            {
                DiagnosticLog.Write("server.benchmark.compact_report.ignored", "reason", "invalid_or_unmatched_test", "wire_bytes", wire == null ? 0 : wire.Length);
                return;
            }
            DiagnosticLog.Write("server.benchmark.compact_report.accepted", "test", report.Test, "profile", (int)report.Profile,
                "status", report.Status, "received_bytes", report.ReceivedBytes, "wire_bytes", wire.Length);
            OnFileBenchmarkReport(report);
        }

        private void OnSpeedReport(BridgeReport report)
        {
            if (IsBenchmarkRunning) UiText.SetText(audioStatus, "Получен отчёт старого теста. Обновите Client.exe.", "Received a legacy test report. Update Client.exe.");
        }

        private void RefreshBenchmark()
        {
            FileBenchmarkRunner run = displayedBenchmark;
            if (run == null || IsClosing) return;
            run.Tick();
            ProfileTestResult[] rows = run.Snapshot();
            int complete = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                ProfileTestResult item = rows[i];
                bool terminal = item.State != ProfileTestState.Pending && item.State != ProfileTestState.Preparing && item.State != ProfileTestState.Receiving;
                if (terminal) complete++;
                if (i >= benchmarkResults.Rows.Count) continue;
                RenderBenchmarkRow(benchmarkResults.Rows[i], item);
            }
            if (!run.Finished)
            {
                ProfileTestResult active = rows[run.CurrentIndex];
                UpdateBenchmarkGeometry(active);
                Guid activeTest = run.CurrentCommand.Test;
                long sentFrames;
                lock (stateLock) sentFrames = benchmarkDataTest == activeTest ? benchmarkDataFrames : 0;
                RenderBenchmarkProgress(run, rows, complete, sentFrames);
            }
            else
            {
                if (!benchmarkTableFinalized)
                {
                    benchmarkTableFinalized = true;
                    int passed = 0; bool noReply = false, failed = false;
                    foreach (ProfileTestResult row in rows)
                    {
                        if (row.State == ProfileTestState.Passed) passed++;
                        if (row.State == ProfileTestState.NoReply) noReply = true;
                        if (row.State == ProfileTestState.NoReply || row.State == ProfileTestState.NoData || row.State == ProfileTestState.Partial || row.State == ProfileTestState.InvalidData) failed = true;
                    }
                    if (passed == 0 && failed) UiText.SetText(benchmarkProgressLabel, "Проверка завершена за {0}. Полностью прошли: 0 / {1}.\r\n{2}", "Test completed in {0}. Fully passed: 0 / {1}.\r\n{2}", FormatDuration(run.TotalMilliseconds), rows.Length, BenchmarkAudioOutputAdvice);
                    else if (noReply) UiText.SetText(benchmarkProgressLabel, "Проверка завершена. Для части режимов нет обратного звукового отчёта; получение файла клиентом не подтверждено.", "Test complete. Some modes returned no audio report; file delivery to the client is unconfirmed.");
                    else UiText.SetText(benchmarkProgressLabel, "Проверка завершена за {0}. Полностью прошли: {1} / {2}. Выберите режим передачи во вкладке «Работа».", "Test completed in {0}. Fully passed: {1} / {2}. Select a transfer mode on the Work tab.", FormatDuration(run.TotalMilliseconds), passed, rows.Length);
                    UiText.SetText(StateLabel, "Проверка завершена. Результаты приведены в таблице.", "Test complete. Results are shown in the table.");
                    if (run.FormatCheckFailureReason != null)
                    {
                        SetBenchmarkFormatFailureSummary(benchmarkProgressLabel, run);
                        if (run.FormatCheckFailureReason == "capture_format_mismatch") UiText.SetText(StateLabel, "Проверка скорости остановлена: разрешения камеры различаются.", "Speed test stopped: camera resolutions differ.");
                        else UiText.SetText(StateLabel, "Проверка скорости остановлена: нет подтверждения разрешения камеры.", "Speed test stopped: camera resolution was not confirmed.");
                    }
                    SaveBenchmarkResults(rows, run.TotalMilliseconds);
                    foreach (ProfileTestResult result in rows)
                        DiagnosticLog.Write("server.benchmark.result", "session", bridgeSession, "profile", (int)result.Profile,
                            "state", result.State.ToString(), "failure", result.Failure, "finishReason", result.ServerFinishReason,
                            "receivedBytes", result.ReceivedBytes, "fileBytes", result.FileBytes,
                            "receiveMs", result.ElapsedMilliseconds, "verifiedMbitS", result.MegabitsPerSecond,
                            "frameWidth", result.Width, "frameHeight", result.Height, "frames", result.Frames,
                            "source_width", benchmarkVideoMode == null ? 0 : benchmarkVideoMode.Width,
                            "source_height", benchmarkVideoMode == null ? 0 : benchmarkVideoMode.Height,
                            "source_format", benchmarkVideoMode == null ? "unknown" : benchmarkVideoMode.Raw ? "YUY2" : "MJPEG",
                            "profile_reduced", ProfileWasReduced(result, benchmarkVideoMode));
                    DiagnosticLog.Write("server.benchmark.finished", "session", bridgeSession, "passed", passed,
                        "profileCount", rows.Length, "totalMs", run.TotalMilliseconds);
                    if (loadSavedSettings) HorizonCameraDiagnostics.Capture(null, "server_benchmark_finished");
                }
                if (run.TailComplete)
                {
                    bool retired = false;
                    lock (stateLock) { if (Object.ReferenceEquals(speedTest, run)) { speedTest = null; retired = true; } }
                    // Retire the test's clipboard baseline once. A new copy
                    // accepted after its tail must survive a delayed UI tick
                    // or a late report for the preceding benchmark.
                    if (retired && generation == benchmarkClipboardGeneration) ResetClipboardObservation("benchmark_finished");
                    benchmarkUiTimer.Stop();
                    if (reverseAudio != null) reverseAudio.SetBenchmarkMode(false);
                }
            }
            UpdateBenchmarkAvailability(!preparing);
            if (run.FormatCheckFailureReason != null && !benchmarkFormatWarningShown)
            {
                // Mark before the modal window starts a nested message loop:
                // timer ticks and repeated reports must not show it again.
                benchmarkFormatWarningShown = true;
                WarnUser(run.FormatCheckFailureReason,
                    "{0}\r\n\r\n{1}{2}\r\n\r\nПосле подключения повторите проверку скорости.",
                    "{0}\r\n\r\n{1}{2}\r\n\r\nRun the speed test again after reconnecting.",
                    BenchmarkFormatFailureValue(run), run.FormatCheckFailureReason == "capture_format_unconfirmed"
                        ? UiText.Value("В удалённой Windows запустите наш Client.exe и выберите нужную камеру и аудиовыход.\r\n\r\n", "Start our Client.exe in the remote Windows session and select the camera and audio output.\r\n\r\n") : (object)String.Empty,
                    ReconnectHorizonValue);
            }
        }

        private static object BenchmarkAudioOutputAdvice
        { get { return UiText.Value("Проверьте выбранный аудиовыход на сервере и на клиенте. На клиенте нажмите «Проверить звук» и дождитесь подтверждения сервера.", "Check the selected audio output on the server and on the client. On the client, select Test audio and wait for the server's confirmation."); } }

        private static string BenchmarkFormatFailureSummary(FileBenchmarkRunner run)
        {
            string source = run.ExpectedWidth + "×" + run.ExpectedHeight;
            string summary = run.FormatCheckFailureReason == "capture_format_mismatch"
                ? UiText.Format("Проверка скорости остановлена: разрешения камеры различаются. Сервер: {0}; клиент: {1}×{2}.", "Speed test stopped: camera resolutions differ. Server: {0}; client: {1}×{2}.", source, run.ActualWidth, run.ActualHeight)
                : UiText.Format("Проверка скорости остановлена: клиент не подтвердил разрешение камеры {0} по обратному звуку за 35 секунд.", "Speed test stopped: the client did not confirm camera resolution {0} over audio within 35 seconds.", source);
            return run.FormatCheckFailureReason == "capture_format_unconfirmed"
                ? UiText.Format("{0}\r\n{1}", "{0}\r\n{1}", UiText.KnownValue(summary), BenchmarkAudioOutputAdvice) : summary;
        }

        private static object BenchmarkFormatFailureValue(FileBenchmarkRunner run)
        { return UiText.KnownValue(BenchmarkFormatFailureSummary(run)); }

        private static void SetBenchmarkFormatFailureSummary(Control target, FileBenchmarkRunner run)
        { UiText.SetKnownText(target, BenchmarkFormatFailureSummary(run)); }

        // Language changes render a snapshot only: no Tick, report dispatch,
        // timer/session restart, file write, warning or clipboard operation.
        private void RefreshBenchmarkLanguage()
        {
            FileBenchmarkRunner run = displayedBenchmark;
            if (run == null || IsClosing) return;
            ProfileTestResult[] rows = run.Snapshot(); int complete = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                ProfileTestState state = rows[i].State;
                if (state != ProfileTestState.Pending && state != ProfileTestState.Preparing && state != ProfileTestState.Receiving) complete++;
                if (i < benchmarkResults.Rows.Count) RenderBenchmarkRow(benchmarkResults.Rows[i], rows[i]);
            }
            if (!run.Finished)
            {
                FileBenchmarkCommand[] commands = run.ReportCommands();
                Guid test = commands.Length == 0 ? Guid.Empty : commands[0].Test; long sent;
                lock (stateLock) sent = benchmarkDataTest == test ? benchmarkDataFrames : 0;
                RenderBenchmarkProgress(run, rows, complete, sent);
            }
        }

        private void RenderBenchmarkRow(DataGridViewRow row, ProfileTestResult item)
        {
            SetResultValue(row, "Profile", BenchmarkProfileCaption(item.Profile));
            row.Cells["Profile"].ToolTipText = ProfileCaptions[(int)item.Profile];
            bool reduced = ProfileWasReduced(item, benchmarkVideoMode);
            SetResultValue(row, "Status", ResultCaption(item));
            row.Cells["Status"].ToolTipText = UiText.Format("Клиент: {0}; сервер: {1}. {2}", "Client: {0}; server: {1}. {2}", item.Failure, item.ServerFinishReason ?? UiText.Get("выполняется", "running"), BenchmarkGeometry(item));
            SetResultValue(row, "FrameSize", item.Width > 0 && item.Height > 0 ? item.Width + "×" + item.Height + (reduced ? " ↓" : "") : "—");
            row.Cells["FrameSize"].ToolTipText = BenchmarkGeometry(item);
            SetResultValue(row, "Bytes", item.ReceivedBytes.ToString("N0", UiText.Culture));
            SetResultValue(row, "FileSize", item.FileBytes.ToString("N0", UiText.Culture));
            SetResultValue(row, "Seconds", item.ReceiverConfirmed && item.ElapsedMilliseconds > 0 ? (item.ElapsedMilliseconds / 1000.0).ToString("N2", UiText.Culture) : "—");
            SetResultValue(row, "Speed", item.ReceivedBytes > 0 ? UiText.Format("{0:N3} Мбит/с", "{0:N3} Mbit/s", item.MegabitsPerSecond) : "—");
            System.Drawing.Color color = item.State == ProfileTestState.Passed ? System.Drawing.Color.Honeydew : item.State == ProfileTestState.NoData || item.State == ProfileTestState.InvalidData ? System.Drawing.Color.MistyRose : item.State == ProfileTestState.Partial ? System.Drawing.Color.LemonChiffon : System.Drawing.SystemColors.Window;
            if (row.DefaultCellStyle.BackColor != color) row.DefaultCellStyle.BackColor = color;
        }

        private void RenderBenchmarkProgress(FileBenchmarkRunner run, ProfileTestResult[] rows, int complete, long sentFrames)
        {
            ProfileTestResult active = rows[run.CurrentIndex];
            bool waiting = active.State == ProfileTestState.Preparing && run.ExpectedWidth > 0;
            string russian = "Режим передачи {0}/{1}: {2}. Проверено: {3}. Всего: {4}.\r\n" +
                (waiting ? "Ожидается подтверждение разрешения {10}×{11} от клиента. Тестовый файл пока не отправляется. " : "{5}. ") +
                "Кадров с данными отправлено в камеру: {6:N0}. " +
                (active.ReceiverConfirmed ? "Клиент подтвердил: {7:N0} / {8:N0} байт; {9:N3} Мбит/с." : "Звукового подтверждения пока нет.");
            string english = "Transfer mode {0}/{1}: {2}. Checked: {3}. Total: {4}.\r\n" +
                (waiting ? "Waiting for the client to confirm resolution {10}×{11}. The test file is not being sent yet. " : "{5}. ") +
                "Data frames sent to the camera: {6:N0}. " +
                (active.ReceiverConfirmed ? "Client confirmed: {7:N0} / {8:N0} bytes; {9:N3} Mbit/s." : "No audio confirmation yet.");
            UiText.SetText(benchmarkProgressLabel, russian, english, run.CurrentIndex + 1, rows.Length,
                UiText.KnownValue(ProfileCaptions[(int)active.Profile]), complete, FormatDuration(run.TotalMilliseconds),
                UiText.KnownValue(StatusCaption(active.State)), sentFrames, active.ReceivedBytes, active.FileBytes, active.MegabitsPerSecond, run.ExpectedWidth, run.ExpectedHeight);
        }

        private static string StatusCaption(ProfileTestState state)
        {
            switch (state)
            {
                case ProfileTestState.Preparing: return UiText.Get("Проверка разрешения", "Checking resolution");
                case ProfileTestState.Receiving: return UiText.Get("Передача файла", "Transferring file");
                case ProfileTestState.Passed: return UiText.Get("Работает", "Works");
                case ProfileTestState.NoData: return UiText.Get("Данные не получены", "No data received");
                case ProfileTestState.Partial: return UiText.Get("Передача не завершена", "Transfer incomplete");
                case ProfileTestState.InvalidData: return UiText.Get("Ошибка проверки данных", "Data verification failed");
                case ProfileTestState.Cancelled: return UiText.Get("Отменён", "Cancelled");
                case ProfileTestState.NoReply: return UiText.Get("Нет подтверждения", "No confirmation");
                default: return UiText.Get("Ожидает", "Waiting");
            }
        }

        private static void SetResultValue(DataGridViewRow row, string column, object value)
        { if (!Object.Equals(row.Cells[column].Value, value)) row.Cells[column].Value = value; }

        private static string ResultCaption(ProfileTestResult result)
        {
            if (result.ServerFinishReason == "capture_format_mismatch") return UiText.Get("Разрешения различаются", "Resolutions differ");
            if (result.ServerFinishReason == "capture_format_unconfirmed") return UiText.Get("Нет подтверждения разрешения", "Resolution unconfirmed");
            if (result.State == ProfileTestState.Partial)
            {
                if (result.Failure == FileBenchmarkFailure.MaximumRun) return UiText.Get("Не завершён: лимит времени", "Incomplete: time limit");
                if (result.ServerFinishReason == "receiver_report_timeout") return UiText.Get("Нет звукового отчёта", "No audio report");
                if (result.Failure == FileBenchmarkFailure.Stalled) return UiText.Get("Поток остановился", "Stream stalled");
            }
            return StatusCaption(result.State);
        }

        private static string BenchmarkProfileCaption(VideoProfile profile)
        {
            switch (profile)
            {
                case VideoProfile.Fast: return "720p";
                case VideoProfile.Dense: return UiText.Get("720p плотный", "720p dense");
                case VideoProfile.HighDefinitionRobust: return UiText.Get("1080p устойчивый", "1080p robust");
                case VideoProfile.Compact640: return "640p";
                case VideoProfile.Mp4Modem: return "MP4Modem V2";
                case VideoProfile.Color720: return UiText.Get("720p, 4 цвета, 8 px", "720p, 4 colors, 8 px");
                case VideoProfile.Color720Dense: return UiText.Get("720p, 4 цвета, 4 px", "720p, 4 colors, 4 px");
                case VideoProfile.Color1080: return UiText.Get("1080p, 4 цвета, 8 px", "1080p, 4 colors, 8 px");
                case VideoProfile.Color1080Dense: return UiText.Get("1080p, 4 цвета, 4 px", "1080p, 4 colors, 4 px");
                default: return ProfileCaptions[(int)profile];
            }
        }

        private static bool ProfileWasReduced(ProfileTestResult result, VideoModeOption source)
        {
            int width = VisualProtocol.GetWidth(result.Profile), height = VisualProtocol.GetHeight(result.Profile);
            return source != null && (source.Width < width || source.Height < height) ||
                result.Width > 0 && result.Height > 0 && (result.Width < width || result.Height < height);
        }

        private string BenchmarkGeometry(ProfileTestResult result)
        {
            return UiText.Format("Режим передачи: {0}×{1}; камера: {2}; клиент: {3}.", "Transfer mode: {0}×{1}; camera: {2}; client: {3}.",
                VisualProtocol.GetWidth(result.Profile), VisualProtocol.GetHeight(result.Profile),
                benchmarkVideoMode == null ? (object)UiText.Get("неизвестно", "unknown") : benchmarkVideoMode,
                result.Width > 0 && result.Height > 0 ? result.Width + "×" + result.Height : UiText.Get("нет отчёта о кадрах", "no frame report"));
        }

        private void UpdateBenchmarkGeometry(ProfileTestResult active)
        {
            UiText.SetText(DetailLabel, "Камера: {0} → клиент: {1}. Подтверждение файла возвращается по звуку.{2}", "Camera: {0} → client: {1}. File confirmation returns over audio.{2}",
                benchmarkVideoMode == null ? UiText.Value("неизвестно", "unknown") : (object)benchmarkVideoMode,
                active != null && active.Width > 0 && active.Height > 0 ? (object)(active.Width + "×" + active.Height) : UiText.Value("ожидается отчёт", "waiting for report"),
                active != null && ProfileWasReduced(active, benchmarkVideoMode) ? UiText.Value(" Кадр режима уменьшается; мелкие ячейки могут теряться.", " This mode's frame is scaled down; small cells may be lost.") : (object)String.Empty);
        }

        private static string FormatDuration(long milliseconds)
        {
            TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
            return ((long)time.TotalHours).ToString("00") + ":" + time.Minutes.ToString("00") + ":" + time.Seconds.ToString("00");
        }

        private void UpdateBenchmarkAvailability(bool enabled)
        {
            bool running = IsBenchmarkBusy;
            colorModesEnabled.Enabled = !running && !preparing && !IsClosing;
            benchmark.Enabled = enabled && HasCreatedCamera && ready && !preparing && !running;
            benchmarkSizeCombo.Enabled = benchmarkLimitCombo.Enabled = enabled && HasCreatedCamera && ready && !running;
            benchmarkStop.Enabled = running;
            if (running)
            {
                profiles.Enabled = cameraMode.Enabled = applyMode.Enabled = createCamera.Enabled = deleteCamera.Enabled = false;
            }
            else if (enabled)
            {
                profiles.Enabled = true; cameraMode.Enabled = true; applyMode.Enabled = cameraLookupKnown && cameraExists;
                cameraName.Enabled = cameraLookupKnown && !cameraExists;
                createCamera.Enabled = !cameraLookupKnown || !cameraExists || !cameraRunning || cameraRequiresUpgrade;
                deleteCamera.Enabled = cameraExists || cameraCleanupAvailable;
            }
            UpdateCameraAccess(enabled);
        }

        private void LoadBenchmarkSettings(XmlElement root)
        {
            // Retired enabledProfiles filters are deliberately ignored. Every
            // server offers all five ordinary modes and its optional color modes.
            long bytes; if (Int64.TryParse(root.GetAttribute("benchmarkBytes"), out bytes))
                for (int i = 0; i < benchmarkSizeCombo.Items.Count; i++) if (((BenchmarkSizeOption)benchmarkSizeCombo.Items[i]).Bytes == bytes) benchmarkSizeCombo.SelectedIndex = i;
            int seconds; if (Int32.TryParse(root.GetAttribute("benchmarkLimitSeconds"), out seconds))
                for (int i = 0; i < benchmarkLimitCombo.Items.Count; i++) if (((BenchmarkLimitOption)benchmarkLimitCombo.Items[i]).Seconds == seconds) benchmarkLimitCombo.SelectedIndex = i;
        }

        private void WriteBenchmarkSettings(XmlWriter writer)
        {
            writer.WriteAttributeString("benchmarkBytes", SelectedBenchmarkBytes.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("benchmarkLimitSeconds", SelectedBenchmarkLimitSeconds.ToString(CultureInfo.InvariantCulture));
        }

        private void SaveBenchmarkResults(ProfileTestResult[] rows, long elapsed)
        {
            try
            {
                string directory = ServerDataDirectory; Directory.CreateDirectory(directory);
                var csv = new StringBuilder("profile,status,received_bytes,file_bytes,receive_seconds,verified_mbit_s,width,height,frames,client_failure,server_finish_reason,receiver_confirmed,source_width,source_height,source_format,profile_reduced\r\n");
                foreach (ProfileTestResult row in rows)
                    csv.Append((int)row.Profile).Append(',').Append(row.State).Append(',').Append(row.ReceivedBytes).Append(',').Append(row.FileBytes).Append(',').Append(row.ReceiverConfirmed ? (row.ElapsedMilliseconds / 1000.0).ToString("F3", CultureInfo.InvariantCulture) : String.Empty).Append(',').Append(row.ReceiverConfirmed ? row.MegabitsPerSecond.ToString("F6", CultureInfo.InvariantCulture) : String.Empty).Append(',').Append(row.Width).Append(',').Append(row.Height).Append(',').Append(row.Frames).Append(',').Append(row.Failure).Append(',').Append(row.ServerFinishReason).Append(',').Append(row.ReceiverConfirmed ? "true" : "false").Append(',').Append(benchmarkVideoMode == null ? 0 : benchmarkVideoMode.Width).Append(',').Append(benchmarkVideoMode == null ? 0 : benchmarkVideoMode.Height).Append(',').Append(benchmarkVideoMode == null ? "unknown" : benchmarkVideoMode.Raw ? "YUY2" : "MJPEG").Append(',').Append(ProfileWasReduced(row, benchmarkVideoMode) ? "true" : "false").AppendLine();
                File.WriteAllText(Path.Combine(directory, "last-benchmark.csv"), csv.ToString(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(directory, "last-benchmark.txt"), DateTime.Now.ToString("s") + "\r\n" + benchmarkProgressLabel.Text + "\r\n" + UiText.Format("Всего: {0}", "Total: {0}", FormatDuration(elapsed)), new UTF8Encoding(false));
            }
            catch (IOException error) { DiagnosticLog.Error("server.benchmark.results.write.error", error); }
            catch (UnauthorizedAccessException error) { DiagnosticLog.Error("server.benchmark.results.write.error", error); }
        }
    }
}
