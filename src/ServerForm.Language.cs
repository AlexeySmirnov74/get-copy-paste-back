using System;
using System.IO;
using System.Windows.Forms;

namespace HorizonClient
{
    internal sealed partial class ServerForm
    {
        private bool updatingLanguageChoice;
        private const string EnglishDocumentationInstructions =
            "FIRST SETUP\r\n\r\n" +
            "1. Run Server.exe. Settings: choose a name → Create camera. Approve installation; restart Windows if requested. Work and Transfer log require a camera.\r\n\r\n" +
            "2. Select this camera in Horizon. After recreating it, reselect it in Horizon and our client.\r\n\r\n" +
            "3. Remotely run Client.exe. Right-click its green tray icon; select camera and an Audio output audible locally.\r\n\r\n" +
            "4. Locally, Work → Horizon audio: All audio outputs by default, or select speakers. Keep modem sound enabled.\r\n\r\n" +
            "Selecting Audio output sends a test. Audio test passed confirms reception. Otherwise try another output. Test audio repeats it.\r\n\r\n" +
            "Mute System Sounds, music and notifications in both Windows mixers. Keep our remote application and Horizon audio enabled. Avoid device changes during transfers.\r\n\r\n" +
            "COPYING\r\n\r\n" +
            "Compression is automatic. Ctrl+C locally: one beep accepts copying; two high beeps confirm remote clipboard readiness. Then Ctrl+V remotely. Settings toggles these cues.\r\n\r\n" +
            "To copy back, Ctrl+C remotely; wait for two high beeps or success in Transfer log, then Ctrl+V on the laptop.\r\n\r\n" +
            "Client files stay in memory without Received. Paste into File Explorer before exiting. Apps requiring file paths may reject virtual files.\r\n\r\n" +
            "SPEED AND RESOLUTION\r\n\r\n" +
            "Settings → Speed test: choose file size (default 10 KB), limit, then Test speed. Client resolution is confirmed over audio. Five modes are tested; Four-color modes adds four. If all fail, check server/client audio outputs.\r\n\r\n" +
            "Formats: 640×480, 1280×720, 1920×1080 YUY2. After Apply format or mismatched resolutions, close the entire Horizon remote desktop and reconnect. Closing our tray application is insufficient.\r\n\r\n" +
            "Startup ignores old clipboard data: copy again. Preview starts disabled.\r\n\r\n" +
            "Closing/minimizing hides the server in the tray; click to reopen. Exit keeps the camera running by default. Uncheck Settings → Keep camera running after exit to stop it (possible UAC). Delete camera removes it.\r\n\r\n" +
            "Server logs default on: HorizonServer\\data (server.debug.log, setup.debug.log). Client logs default off: client.debug.log beside its EXE. Toggle in Settings.\r\n\r\n" +
            "DLLs: server HorizonServer\\audio-native; client audio-native inside its settings directory, normally %LOCALAPPDATA%\\HorizonClient\\Settings. Embedded .ps1 scripts run without disk extraction. No licenses folder is created automatically.\r\n\r\n" +
            "Quiet uses libfec (LGPL-2.1). Export licenses, sources and rebuilding materials: Server.exe --export-third-party C:\\folder.";

        private void InitializeLanguageSelection()
        {
            UiText.SetText(settingsTab, "Настройки", "Settings");
            UiText.SetText(workTab, "Работа", "Work");
            UiText.SetText(transferLogTab, "Лог передачи", "Transfer log");
            UiText.SetText(documentationTab, "Документация", "Documentation");
            UiText.SetText(documentationText, DocumentationInstructions, EnglishDocumentationInstructions);
            UiText.SetText(transferLogEnabled, "Вести лог передачи", "Record transfer log");
            UiText.SetText(clearTransferLog, "Очистить", "Clear");
            UiText.SetText(previewToggle, "Предпросмотр передачи", "Transmission preview");
            UiText.SetText(deleteCamera, "Удалить камеру", "Delete camera");
            UiText.SetText(applyMode, "Применить формат", "Apply format");
            UiText.SetText(detailedLog, "Подробный системный лог", "Detailed diagnostic logging");
            UiText.SetText(colorModesEnabled, "Цветные режимы (4 цвета)", "Four-color modes");
            UiText.SetText(keepCameraRunning, "Оставлять камеру работающей после выхода", "Keep camera running after exit");
            UiText.SetText(acceptedSoundEnabled, "Звук начала копирования", "Copy start sound");
            UiText.SetText(deliveredSoundEnabled, "Звук готовности буфера на удалённом ПК", "Remote clipboard ready sound");
            UiText.SetText(audioStatus, "Обратная передача использует звук Horizon и может быть слышна.", "Return transfers use Horizon audio and may be audible.");
            UiText.SetText(benchmark, "Проверить скорость", "Test speed");
            UiText.SetText(benchmarkStop, "Остановить проверку", "Stop test");
            UiText.SetText(benchmarkPanel, "Проверка скорости", "Speed test");
            UiText.SetText(benchmarkProgressLabel, "Проверка ещё не запускалась.", "The test has not started yet.");
            updatingLanguageChoice = true;
            try
            {
                languageChoice.Items.AddRange(new object[] { "Русский", "English" });
                languageChoice.SelectedIndex = UiText.Language == UiLanguage.English ? 1 : 0;
            }
            finally { updatingLanguageChoice = false; }
            languageChoice.SelectedIndexChanged += delegate
            {
                if (updatingLanguageChoice || languageChoice.SelectedIndex < 0) return;
                UiLanguage selected = languageChoice.SelectedIndex == 1 ? UiLanguage.English : UiLanguage.Russian;
                if (UiText.Language == selected) return;
                UiText.SetLanguage(selected);
                if (loadSavedSettings)
                    try { UiText.Save(Path.Combine(ServerDataDirectory, UiText.LanguageFileName)); }
                    catch (Exception error) { DiagnosticLog.Error("server.language.save_failed", error); }
                ApplyServerLanguage();
            };
            ApplyServerLanguage();
        }

        private void ApplyServerLanguage()
        {
            bool previousRestoring = restoringSettings, previousLoading = loadingOutputs;
            restoringSettings = true; loadingOutputs = true;
            updatingLanguageChoice = true;
            try
            {
                UiText.Apply(this);
                RefreshTransferProfileChoices();
                RefreshLanguageCombo(benchmarkSizeCombo);
                RefreshLanguageCombo(benchmarkLimitCombo);
                RefreshLanguageCombo(cameraMode);
                int outputIndex = audioOutput.SelectedIndex;
                for (int index = 0; index < audioOutput.Items.Count; index++)
                {
                    AudioEndpoint endpoint = audioOutput.Items[index] as AudioEndpoint;
                    if (endpoint != null && endpoint.Id == ServerAudioBridge.AllOutputsId)
                        audioOutput.Items[index] = new AudioEndpoint(endpoint.Id, AllAudioOutputsCaption);
                    else if (endpoint != null && endpoint.Id == null)
                        audioOutput.Items[index] = new AudioEndpoint(null, UiText.Get("Устройство Windows по умолчанию", "Windows default device"));
                }
                if (outputIndex >= 0 && outputIndex < audioOutput.Items.Count) audioOutput.SelectedIndex = outputIndex;
                RefreshUnavailableAudioOutputCaption();
                RefreshLanguageCombo(audioOutput);
                languageChoice.SelectedIndex = UiText.Language == UiLanguage.English ? 1 : 0;
                SetResultColumnLanguage("Profile", "Режим передачи", "Transfer mode");
                SetResultColumnLanguage("Status", "Результат", "Result");
                SetResultColumnLanguage("FrameSize", "Кадр клиента", "Client frame");
                SetResultColumnLanguage("Bytes", "Получено, байт", "Received, bytes");
                SetResultColumnLanguage("FileSize", "Файл, байт", "File, bytes");
                SetResultColumnLanguage("Seconds", "Приём, с", "Receive, s");
                SetResultColumnLanguage("Speed", "Скорость", "Speed");
                benchmarkResults.Columns["FrameSize"].ToolTipText = UiText.Get("Реальные размеры кадра, полученного клиентом; уменьшение может повредить мелкие ячейки режима передачи", "Actual frame size received by the client; downscaling can damage small data cells");
                benchmarkResults.Columns["Seconds"].ToolTipText = UiText.Get("Время от начала приёма на клиенте до получения всего файла; ожидание звукового подтверждения не включено", "Time from the first received data to the complete file; audio confirmation delay is excluded");
                benchmarkResults.Columns["Speed"].ToolTipText = UiText.Get("Проверенные байты / время приёма на клиенте", "Verified bytes / client receive time");
                RefreshLanguagePresentation();
                PerformLayout();
            }
            finally
            {
                updatingLanguageChoice = false;
                restoringSettings = previousRestoring; loadingOutputs = previousLoading;
            }
        }

        private static void RefreshLanguageCombo(ComboBox combo)
        {
            int selected = combo.SelectedIndex;
            combo.BeginUpdate();
            try { if (selected >= 0) { combo.SelectedIndex = -1; combo.SelectedIndex = selected; } }
            finally { combo.EndUpdate(); }
            combo.Refresh();
        }

        private void SetResultColumnLanguage(string name, string russian, string english)
        { benchmarkResults.Columns[name].HeaderText = UiText.Get(russian, english); }
    }
}
