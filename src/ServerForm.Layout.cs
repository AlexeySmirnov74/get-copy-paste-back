using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace HorizonClient
{
    internal sealed partial class ServerForm
    {
        private readonly TabControl serverTabs = new TabControl { Name = "ServerTabs", Dock = DockStyle.Fill };
        private readonly TabPage settingsTab = new TabPage("Настройки") { Name = "SettingsTab", UseVisualStyleBackColor = true };
        private readonly TabPage workTab = new TabPage("Работа") { Name = "WorkTab", UseVisualStyleBackColor = true };
        private readonly TabPage transferLogTab = new TabPage("Лог передачи") { Name = "TransferLogTab", UseVisualStyleBackColor = true };
        private readonly TabPage documentationTab = new TabPage("Документация") { Name = "DocumentationTab", UseVisualStyleBackColor = true, Padding = new Padding(12) };
        private readonly TextBox documentationText = new TextBox { Name = "DocumentationText", Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11F), BackColor = SystemColors.Window, Text = DocumentationInstructions };
        private const string DocumentationInstructions =
            "ПЕРВАЯ НАСТРОЙКА\r\n\r\n" +
            "1. Запустите Server.exe. «Настройки»: название → «Создать камеру». Без камеры «Работа» и «Лог передачи» недоступны.\r\n\r\n" +
            "2. Выберите камеру в Horizon. После удаления и создания заново выберите новую камеру в Horizon и клиенте.\r\n\r\n" +
            "3. Удалённый ПК: Client.exe → зелёный значок возле часов → правая кнопка → камера, «Аудиовыход», слышимый на ноутбуке.\r\n\r\n" +
            "4. Ноутбук: «Работа» → «Звук Horizon»: по умолчанию все аудиовыходы; можно выбрать динамики. Служебный звук не отключайте.\r\n\r\n" +
            "«Аудиовыход»: клиент сразу отправляет проверочный звук. Успех: «Тест аудио прошёл успешно». Повтор: «Проверить звук».\r\n\r\n" +
            "В микшере обеих Windows выключите «Системные звуки», музыку и уведомления. Оставьте нашу удалённую программу и звук Horizon на ноутбуке. Не меняйте устройства при передаче.\r\n\r\n" +
            "КОПИРОВАНИЕ\r\n\r\n" +
            "Сжатие автоматическое. На ноутбуке нажмите Ctrl+C. Один короткий сигнал — копирование принято. Два высоких коротких сигнала — буфер клиента готов: нажмите Ctrl+V удалённо. Эти звуки отключаются в «Настройки».\r\n\r\n" +
            "Обратно: Ctrl+C удалённо → дождитесь двух высоких сигналов или успеха в «Лог передачи» → Ctrl+V на ноутбуке.\r\n\r\n" +
            "Клиент держит файлы в памяти без Received. Вставьте в Проводник до выхода из клиента. Некоторые программы не поддерживают виртуальные файлы.\r\n\r\n" +
            "ПРОВЕРКА И РАЗРЕШЕНИЕ\r\n\r\n" +
            "«Настройки» → «Проверка скорости»: файл (по умолчанию 10 КБ), лимит → «Проверить скорость». Сервер подтвердит по звуку фактическое разрешение клиента и проверит пять режимов. «Цветные режимы (4 цвета)» добавляют ещё четыре. Итоги — в таблице. Если ничего не прошло, проверьте аудиовыход на сервере и на клиенте.\r\n\r\n" +
            "«Формат камеры»: 640×480, 1280×720, 1920×1080 YUY2. После «Применить формат» или при различающихся разрешениях полностью закройте удалённый рабочий стол в Horizon и переподключитесь. Закрыть только наш значок в трее недостаточно.\r\n\r\n" +
            "При запуске старый буфер не отправляется: скопируйте заново. «Предпросмотр передачи» выключен.\r\n\r\n" +
            "Крестик и сворачивание убирают сервер в трей; щелчок открывает окно. «Выход» по умолчанию оставляет камеру работать. Для остановки снимите «Настройки» → «Оставлять камеру работающей после выхода»; возможен UAC. «Удалить камеру» удаляет её.\r\n\r\n" +
            "«Подробный системный лог» сервера включён по умолчанию: HorizonServer\\data (server.debug.log, setup.debug.log). Лог клиента изначально выключен: client.debug.log рядом с EXE. Переключение — «Настройки».\r\n\r\n" +
            "DLL: сервер HorizonServer\\audio-native; клиент обычно %LOCALAPPDATA%\\HorizonClient\\Settings\\audio-native. Сценарии .ps1 выполняются из EXE без записи на диск. Папка licenses автоматически не создаётся.\r\n\r\n" +
            "Quiet использует libfec (LGPL-2.1). Лицензии, исходники и материалы пересборки: Server.exe --export-third-party C:\\folder.";
        private readonly CheckBox transferLogEnabled = new CheckBox { Name = "TransferLogEnabled", Text = "Вести лог передачи", Checked = true, AutoSize = true };
        private readonly Button clearTransferLog = new Button { Name = "ClearTransferLog", Text = "Очистить", Width = 140, Height = 30 };
        private readonly TextBox transferLogText = new TextBox { Name = "TransferLogText", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, MaxLength = 200000 };
        private readonly NumericUpDown audioRetries = new NumericUpDown { Name = "AudioRetries", Minimum = 0, Maximum = 10, Value = 2, Width = 70 };
        private readonly ComboBox languageChoice = new ComboBox { Name = "LanguageChoice", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly CheckBox previewToggle = new CheckBox { Name = "TransmissionPreviewEnabled", Text = "Предпросмотр передачи", Checked = false, AutoSize = true, Dock = DockStyle.Fill };
        private readonly ComboBox cameraName = new CameraNameComboBox { Name = "CameraNameDropDown", DropDownStyle = ComboBoxStyle.DropDown, MaxLength = 100, Dock = DockStyle.Fill, Enabled = false, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
        private readonly Button createCamera = new Button { Name = "CreateCamera", Text = "Создать камеру", Width = 170, Height = 34, Enabled = false };
        private readonly Button deleteCamera = new Button { Name = "DeleteCamera", Text = "Удалить камеру", Width = 170, Height = 34, Enabled = false };
        private readonly ComboBox profiles = new ComboBox { Name = "VideoProfile", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox cameraMode = new ComboBox { Name = "CameraMode", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly Button applyMode = new Button { Name = "ApplyVideoMode", Text = "Применить формат", Dock = DockStyle.Fill, Enabled = false };
        private readonly ComboBox audioOutput = new ComboBox { Name = "AudioOutput", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly CheckBox detailedLog = new CheckBox { Name = "DetailedSystemLog", Text = "Подробный системный лог", AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly CheckBox colorModesEnabled = new CheckBox { Name = "ColorModesEnabled", Text = "Цветные режимы (4 цвета)", Checked = false, AutoSize = true, Dock = DockStyle.Fill };
        private readonly CheckBox keepCameraRunning = new CheckBox { Name = "KeepCameraRunning", Text = "Оставлять камеру работающей после выхода", Checked = true, AutoSize = true, Dock = DockStyle.Fill };
        private readonly CheckBox acceptedSoundEnabled = new CheckBox { Name = "AcceptedSoundEnabled", Text = "Звук начала копирования", Checked = true, AutoSize = true, Dock = DockStyle.Fill };
        private readonly CheckBox deliveredSoundEnabled = new CheckBox { Name = "DeliveredSoundEnabled", Text = "Звук готовности буфера на удалённом ПК", Checked = true, AutoSize = true, Dock = DockStyle.Fill };
        private readonly Label audioStatus = new StableLabel { Name = "AudioStatus", Dock = DockStyle.Fill, Text = "Обратная передача использует звук Horizon и может быть слышна." };
        private readonly Button benchmark = new Button { Name = "Benchmark", Text = "Проверить скорость", Dock = DockStyle.Fill, Enabled = false };
        private readonly ComboBox benchmarkSizeCombo = new ComboBox { Name = "BenchmarkSize", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox benchmarkLimitCombo = new ComboBox { Name = "BenchmarkLimit", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly Label benchmarkProgressLabel = new StableLabel { Name = "BenchmarkProgress", Text = "Проверка ещё не запускалась.", Dock = DockStyle.Fill };
        private readonly DataGridView benchmarkResults = new BufferedResultsGrid { Name = "BenchmarkResults", Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        private readonly Button benchmarkStop = new Button { Name = "BenchmarkStop", Text = "Остановить проверку", Dock = DockStyle.Fill, Enabled = false };
        private readonly GroupBox benchmarkPanel = new GroupBox { Name = "BenchmarkPanel", Text = "Проверка скорости", Dock = DockStyle.Fill, Padding = new Padding(8, 5, 8, 7), Margin = new Padding(3, 0, 3, 0) };
        private bool fittingBenchmarkResults;
        private static readonly string[] RussianProfileCaptions = { "Совместимый: крупные ячейки, 5 блоков/с", "720p: 1,5 КиБ/кадр", "720p плотный: 6 КиБ/кадр", "720p, ячейки 1 пиксель (эксперимент)", "1080p, ячейки 1 пиксель (эксперимент)", "4K, ячейки 2 пикселя (эксперимент)", "4K, ячейки 1 пиксель (эксперимент)", "1080p, устойчивый: ячейки 4 пикселя", "4K, устойчивый: ячейки 4 пикселя", "320p: 256 байт/кадр", "640p: 7 блоков/кадр", "MP4Modem V2: коррекция ошибок", "720p, 4 цвета: 3 КиБ/кадр, 8 px", "720p, 4 цвета: 12 КиБ/кадр, 4 px", "1080p, 4 цвета: 6 КиБ/кадр, 8 px", "1080p, 4 цвета: 24 КиБ/кадр, 4 px" };
        private static readonly string[] EnglishProfileCaptions = { "Compatible: large cells, 5 blocks/s", "720p: 1.5 KiB/frame", "720p dense: 6 KiB/frame", "720p, 1-pixel cells (experimental)", "1080p, 1-pixel cells (experimental)", "4K, 2-pixel cells (experimental)", "4K, 1-pixel cells (experimental)", "1080p robust: 4-pixel cells", "4K robust: 4-pixel cells", "320p: 256 bytes/frame", "640p: 7 blocks/frame", "MP4Modem V2: error correction", "720p, 4 colors: 3 KiB/frame, 8 px", "720p, 4 colors: 12 KiB/frame, 4 px", "1080p, 4 colors: 6 KiB/frame, 8 px", "1080p, 4 colors: 24 KiB/frame, 4 px" };
        private static string[] ProfileCaptions { get { return UiText.Language == UiLanguage.English ? EnglishProfileCaptions : RussianProfileCaptions; } }

        private sealed class ProfileChoice
        {
            internal readonly VideoProfile Id;
            internal ProfileChoice(VideoProfile id) { Id = id; }
            public override string ToString() { return ProfileCaptions[(int)Id]; }
        }

        private VideoProfile SelectedProfileChoice
        {
            get { ProfileChoice choice = profiles.SelectedItem as ProfileChoice; return choice == null ? DefaultProfile : choice.Id; }
            set
            {
                for (int index = 0; index < profiles.Items.Count; index++)
                    if (((ProfileChoice)profiles.Items[index]).Id == value) { profiles.SelectedIndex = index; return; }
                if (profiles.Items.Count != 0) profiles.SelectedIndex = 0;
            }
        }

        private void PopulateProfileChoices(VideoProfile selected)
        {
            var available = new HashSet<VideoProfile>(GetAvailableTransferProfiles());
            profiles.BeginUpdate();
            try
            {
                profiles.Items.Clear();
                foreach (VideoProfile profile in GetAvailableTransferProfiles()) profiles.Items.Add(new ProfileChoice(profile));
                if (profiles.Items.Count == 0) profiles.Items.Add(new ProfileChoice(DefaultProfile));
                SelectedProfileChoice = available.Contains(selected) ? selected : DefaultProfile;
            }
            finally { profiles.EndUpdate(); }
        }

        private VideoProfile[] GetAvailableTransferProfiles()
        {
            var result = new List<VideoProfile>();
            foreach (VideoProfile profile in VisualProtocol.GetProfiles())
                if (VisualProtocol.GetWidth(profile) <= 1920 && VisualProtocol.GetHeight(profile) <= 1080 &&
                    (colorModesEnabled.Checked || !ColorVideoProtocol.Supports(profile))) result.Add(profile);
            return result.ToArray();
        }

        private bool IsAvailableTransferProfile(VideoProfile profile)
        { return Array.IndexOf(GetAvailableTransferProfiles(), profile) >= 0; }

        private void RefreshTransferProfileChoices()
        {
            bool previous = restoringSettings; restoringSettings = true;
            try { PopulateProfileChoices(selectedProfile); selectedProfile = SelectedProfileChoice; }
            finally { restoringSettings = previous; }
        }

        private sealed class BenchmarkSizeOption
        {
            internal readonly long Bytes;
            private readonly string russian, english;
            internal BenchmarkSizeOption(string russian, string english, long bytes) { this.russian = russian; this.english = english; Bytes = bytes; }
            public override string ToString() { return UiText.Get(russian, english); }
        }

        private sealed class BenchmarkLimitOption
        {
            internal readonly int Seconds;
            private readonly string russian, english;
            internal BenchmarkLimitOption(string russian, string english, int seconds) { this.russian = russian; this.english = english; Seconds = seconds; }
            public override string ToString() { return UiText.Get(russian, english); }
        }

        internal long SelectedBenchmarkBytes
        {
            get { BenchmarkSizeOption option = benchmarkSizeCombo.SelectedItem as BenchmarkSizeOption; return option == null ? 10240 : option.Bytes; }
        }

        internal int SelectedBenchmarkLimitSeconds
        {
            get { BenchmarkLimitOption option = benchmarkLimitCombo.SelectedItem as BenchmarkLimitOption; return option == null ? 180 : option.Seconds; }
        }

        private void InitializeServerLayout()
        {
            ClientSize = new Size(1060, 650);
            MinimumSize = new Size(960, 680);
            serverTabs.TabPages.Add(settingsTab); serverTabs.TabPages.Add(workTab); serverTabs.TabPages.Add(transferLogTab); serverTabs.TabPages.Add(documentationTab);
            documentationTab.Controls.Add(documentationText);
            Controls.Add(serverTabs);
            foreach (string name in CameraNames.Names) cameraName.Items.Add(name);
            SetCameraName(CameraNames.PickRandom());

            // Let native button text and border metrics determine the minimum
            // height, then let every caption row carry that height to its parent.
            foreach (Button button in new[] { createCamera, deleteCamera, applyMode, benchmark, benchmarkStop, clearTransferLog })
            {
                button.AutoSize = true;
                button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            }

            var settings = ColumnLayout(10, 10);
            settings.Name = "SettingsLayout";
            for (int row = 0; row < settings.RowCount; row++) settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var topRow = new TableLayoutPanel { Name = "SettingsTopRow", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 6, RowCount = 1, Margin = Padding.Empty };
            topRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Label languageLabel = FieldLabel("Язык", "Language"); languageLabel.Name = "SettingsLanguageLabel"; languageLabel.AutoSize = true;
            Label cameraLabel = FieldLabel("Название камеры", "Camera name"); cameraLabel.Name = "SettingsCameraLabel"; cameraLabel.AutoSize = true;
            createCamera.Dock = deleteCamera.Dock = DockStyle.Fill;
            topRow.Controls.Add(languageLabel, 0, 0); topRow.Controls.Add(languageChoice, 1, 0);
            topRow.Controls.Add(cameraLabel, 2, 0); topRow.Controls.Add(cameraName, 3, 0);
            topRow.Controls.Add(createCamera, 4, 0); topRow.Controls.Add(deleteCamera, 5, 0);
            settings.Controls.Add(topRow, 0, 0);
            var settingsState = new StableLabel { Name = "SettingsState", AutoSize = true, Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) };
            var settingsDetail = new StableLabel { Name = "SettingsDetail", AutoSize = true, Dock = DockStyle.Fill };
            settings.Controls.Add(settingsState, 0, 1); settings.Controls.Add(settingsDetail, 0, 2);
            StateLabel.TextChanged += delegate { settingsState.Text = StateLabel.Text; };
            DetailLabel.TextChanged += delegate { settingsDetail.Text = DetailLabel.Text; };
            var retryOptions = new TableLayoutPanel { Name = "AudioRetryOptions", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            retryOptions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            retryOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 460)); retryOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76)); retryOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            audioRetries.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            retryOptions.Controls.Add(FieldLabel("Повторы обратной передачи от клиента в случае ошибок", "Retry client-to-server transfers after errors"), 0, 0); retryOptions.Controls.Add(audioRetries, 1, 0);
            settings.Controls.Add(retryOptions, 0, 3);
            settings.Controls.Add(keepCameraRunning, 0, 4);
            settings.Controls.Add(acceptedSoundEnabled, 0, 5);
            settings.Controls.Add(deliveredSoundEnabled, 0, 6);
            settings.Controls.Add(colorModesEnabled, 0, 7);
            settings.Controls.Add(detailedLog, 0, 8);
            AddScrollableContent(settingsTab, settings, "SettingsScroll");

            var work = ColumnLayout(5, 12);
            work.Name = "WorkLayout";
            for (int row = 0; row < 4; row++) work.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            work.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            StateLabel.AutoSize = true; StateLabel.Dock = DockStyle.Fill; StateLabel.Font = new Font(Font, FontStyle.Bold);
            DetailLabel.AutoSize = true; DetailLabel.Dock = DockStyle.Fill;
            work.Controls.Add(StateLabel, 0, 0); work.Controls.Add(DetailLabel, 0, 1);
            var controls = new TableLayoutPanel { Name = "WorkControls", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, RowCount = 4, Margin = Padding.Empty };
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            for (int row = 0; row < 4; row++) controls.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            audioStatus.AutoSize = true;
            controls.Controls.Add(FieldLabel("Режим передачи", "Transfer mode"), 0, 0); controls.Controls.Add(profiles, 1, 0); controls.SetColumnSpan(profiles, 3);
            controls.Controls.Add(FieldLabel("Формат камеры", "Camera format"), 0, 1); controls.Controls.Add(cameraMode, 1, 1); controls.SetColumnSpan(cameraMode, 2); controls.Controls.Add(applyMode, 3, 1);
            controls.Controls.Add(FieldLabel("Звук Horizon", "Horizon audio"), 0, 2); controls.Controls.Add(audioOutput, 1, 2); controls.SetColumnSpan(audioOutput, 3);
            controls.Controls.Add(audioStatus, 0, 3); controls.SetColumnSpan(audioStatus, 4);
            work.Controls.Add(controls, 0, 2);

            var testPanel = ColumnLayout(6, 0);
            testPanel.Name = "BenchmarkLayout";
            testPanel.AutoSize = true; testPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink; testPanel.Dock = DockStyle.Top;
            benchmarkPanel.AutoSize = true; benchmarkPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink; benchmarkPanel.Dock = DockStyle.Top;
            for (int row = 0; row < 4; row++) testPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            testPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 166));
            testPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            var fileOptions = new TableLayoutPanel { Name = "BenchmarkFileOptions", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            fileOptions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            fileOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 232)); fileOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160)); fileOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fileOptions.Controls.Add(FieldLabel("Тестовый файл", "Test file"), 0, 0); fileOptions.Controls.Add(benchmarkSizeCombo, 1, 0);
            var limitOptions = new TableLayoutPanel { Name = "BenchmarkLimitOptions", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            limitOptions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            limitOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 232)); limitOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160)); limitOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            limitOptions.Controls.Add(FieldLabel("Лимит на режим", "Time limit per mode"), 0, 0); limitOptions.Controls.Add(benchmarkLimitCombo, 1, 0);
            var testActions = new TableLayoutPanel { Name = "BenchmarkActions", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            testActions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            testActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210)); testActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); testActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            testActions.Controls.Add(benchmark, 0, 0); testActions.Controls.Add(benchmarkStop, 1, 0);
            benchmarkProgressLabel.AutoSize = true;
            testPanel.Controls.Add(fileOptions, 0, 0); testPanel.Controls.Add(limitOptions, 0, 1);
            testPanel.Controls.Add(testActions, 0, 2); testPanel.Controls.Add(benchmarkProgressLabel, 0, 3);
            testPanel.Controls.Add(benchmarkResults, 0, 4);
            benchmarkPanel.Controls.Add(testPanel); settings.Controls.Add(benchmarkPanel, 0, 9);
            work.Controls.Add(previewToggle, 0, 3);
            previewToggle.CheckedChanged += delegate
            {
                LogUiAction(previewToggle.Name, "changed", "enabled", previewToggle.Checked);
                SetTransmissionPreview(previewToggle.Checked);
            };
            Preview.Dock = DockStyle.Fill; Preview.SizeMode = PictureBoxSizeMode.Zoom; Preview.BackColor = Color.FromArgb(28, 28, 28); Preview.Name = "TransmissionPreview";
            Preview.MinimumSize = new Size(0, 120);
            work.Controls.Add(Preview, 0, 4);
            Panel workScroll = AddScrollableContent(workTab, work, "WorkScroll");
            // Keep the preview using spare space, while wrapped rows can grow
            // beyond the viewport and remain reachable through outer scrolling.
            LayoutEventHandler fitPreview = delegate
            {
                int remaining = workScroll.ClientSize.Height - work.Padding.Vertical;
                int[] heights = work.GetRowHeights();
                for (int row = 0; row < 4; row++) remaining -= heights[row];
                int height = Math.Max(Preview.MinimumSize.Height + Preview.Margin.Vertical, remaining);
                if (Math.Abs(work.RowStyles[4].Height - height) > .5F) work.RowStyles[4].Height = height;
            };
            workScroll.Layout += fitPreview; work.Layout += fitPreview;
            var logPanel = ColumnLayout(2, 12);
            logPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var logOptions = new FlowLayoutPanel { Name = "TransferLogOptions", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
            logOptions.Controls.Add(transferLogEnabled); logOptions.Controls.Add(clearTransferLog);
            logPanel.Controls.Add(logOptions, 0, 0); logPanel.Controls.Add(transferLogText, 0, 1); transferLogTab.Controls.Add(logPanel);
            AddResultColumn("Profile", "Режим передачи", 38);
            AddResultColumn("Status", "Результат", 62);
            AddResultColumn("FrameSize", "Кадр клиента", 12);
            AddResultColumn("Bytes", "Получено, байт", 12);
            AddResultColumn("FileSize", "Файл, байт", 11);
            AddResultColumn("Seconds", "Приём, с", 8);
            AddResultColumn("Speed", "Скорость", 12);
            foreach (string name in new[] { "FrameSize", "Bytes", "FileSize", "Seconds", "Speed" })
            {
                benchmarkResults.Columns[name].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCellsExceptHeader;
                benchmarkResults.Columns[name].MinimumWidth = name == "FrameSize" ? 104 : name == "Seconds" ? 68 : 100;
                benchmarkResults.Columns[name].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                benchmarkResults.Columns[name].DefaultCellStyle.Padding = new Padding(5, 0, 5, 0);
            }
            benchmarkResults.Columns["FrameSize"].ToolTipText = "Реальные размеры кадра, полученного клиентом; уменьшение может повредить мелкие ячейки режима передачи";
            benchmarkResults.Columns["Seconds"].ToolTipText = "Время от начала приёма на клиенте до получения всего файла; ожидание звукового подтверждения не включено";
            benchmarkResults.Columns["Speed"].ToolTipText = "Проверенные байты / время приёма на клиенте";
            benchmarkResults.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            benchmarkResults.RowTemplate.Height = 27;
            benchmarkResults.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            testPanel.Layout += delegate { FitBenchmarkResults(testPanel); };
            benchmarkResults.ColumnHeadersHeightChanged += delegate { FitBenchmarkResults(testPanel); };
            benchmarkResults.FontChanged += delegate { FitBenchmarkResults(testPanel); };
            benchmarkResults.RowsAdded += delegate { FitBenchmarkResults(testPanel); };
            benchmarkResults.CellValueChanged += delegate { FitBenchmarkResults(testPanel); };
            benchmarkResults.SizeChanged += delegate { FitBenchmarkResults(testPanel); };

            benchmarkSizeCombo.Items.AddRange(new object[] { new BenchmarkSizeOption("1 КБ", "1 KB", 1024), new BenchmarkSizeOption("10 КБ", "10 KB", 10240), new BenchmarkSizeOption("100 КБ", "100 KB", 102400), new BenchmarkSizeOption("1 МБ", "1 MB", 1048576), new BenchmarkSizeOption("10 МБ", "10 MB", 10485760), new BenchmarkSizeOption("100 МБ", "100 MB", 104857600) });
            benchmarkSizeCombo.SelectedIndex = 1;
            benchmarkLimitCombo.Items.AddRange(new object[] { new BenchmarkLimitOption("3 мин", "3 min", 180), new BenchmarkLimitOption("10 мин", "10 min", 600), new BenchmarkLimitOption("30 мин", "30 min", 1800), new BenchmarkLimitOption("Без лимита", "No limit", 0) });
            benchmarkLimitCombo.SelectedIndex = 0;
            PopulateProfileChoices(selectedProfile);
            audioOutput.Items.Add(new AudioEndpoint(ServerAudioBridge.AllOutputsId, AllAudioOutputsCaption));
            audioOutput.Items.Add(new AudioEndpoint(null, UiText.Get("Устройство Windows по умолчанию", "Windows default device"))); audioOutput.SelectedIndex = 0;
            cameraMode.Items.AddRange(new object[] {
                new VideoModeOption(640, 480, 30, true, false), new VideoModeOption(1280, 720, 30, true, false),
                new VideoModeOption(1920, 1080, 30, true, false) });
            cameraMode.SelectedIndex = 2;
            UiText.SetText(StateLabel, "Проверка существующей камеры начнётся после открытия окна.", "The existing camera will be checked after the window opens.");
            UiText.SetText(DetailLabel, "Необходимые драйверы будут установлены автоматически. Windows может запросить права администратора.", "Required drivers will be installed automatically. Windows may request administrator permission.");
            foreach (ComboBox combo in new[] { cameraName, profiles, cameraMode, audioOutput })
            {
                combo.MaxDropDownItems = 12;
                combo.DropDown += delegate(object sender, EventArgs args) { FitDropDown((ComboBox)sender); };
            }
            Shown += delegate
            {
                FitUiToWorkArea(Screen.FromControl(this).WorkingArea);
            };
        }

        private void SetCameraName(string name)
        {
            cameraName.Text = name ?? String.Empty;
            if (cameraName.IsHandleCreated) cameraName.Select(0, 0);
        }

        private sealed class CameraNameComboBox : ComboBox
        {
            // Native edit creation can select the whole caption after ordinary
            // layout code already cleared it. Clear after the base lifecycle.
            protected override void CreateHandle()
            { base.CreateHandle(); ClearUnavailableSelection(); }
            protected override void OnEnabledChanged(EventArgs args)
            { base.OnEnabledChanged(args); ClearUnavailableSelection(); }
            protected override void OnGotFocus(EventArgs args)
            { base.OnGotFocus(args); ClearUnavailableSelection(); }
            private void ClearUnavailableSelection()
            { if (!Enabled && IsHandleCreated) Select(0, 0); }
        }

        private static TableLayoutPanel ColumnLayout(int rows, int padding)
        {
            var result = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(padding), RowCount = rows, ColumnCount = 1 };
            result.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return result;
        }

        private sealed class BufferedResultsGrid : DataGridView
        { internal BufferedResultsGrid() { DoubleBuffered = true; } }

        private static Panel AddScrollableContent(TabPage page, TableLayoutPanel content, string name)
        {
            var scroll = new Panel { Name = name, Dock = DockStyle.Fill, AutoScroll = true };
            content.Dock = DockStyle.Top; content.AutoSize = true; content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            scroll.Controls.Add(content); page.Controls.Add(scroll);
            return scroll;
        }

        private void FitBenchmarkResults(TableLayoutPanel layout)
        {
            if (fittingBenchmarkResults || benchmarkResults.Columns.Count < 7) return;
            fittingBenchmarkResults = true;
            try
            {
                // Reserve readable text widths before distributing any spare space.
                int profileWidth = 0;
                foreach (VideoProfile profile in GetAvailableTransferProfiles()) profileWidth = Math.Max(profileWidth, TextRenderer.MeasureText(BenchmarkProfileCaption(profile), benchmarkResults.Font).Width + 16);
                int statusWidth = 0;
                var outcomes = new List<ProfileTestResult>();
                foreach (ProfileTestState state in Enum.GetValues(typeof(ProfileTestState))) outcomes.Add(new ProfileTestResult { State = state });
                outcomes.Add(new ProfileTestResult { ServerFinishReason = "capture_format_mismatch" });
                outcomes.Add(new ProfileTestResult { ServerFinishReason = "capture_format_unconfirmed" });
                outcomes.Add(new ProfileTestResult { State = ProfileTestState.Partial, Failure = FileBenchmarkFailure.MaximumRun });
                outcomes.Add(new ProfileTestResult { State = ProfileTestState.Partial, Failure = FileBenchmarkFailure.Stalled });
                outcomes.Add(new ProfileTestResult { State = ProfileTestState.Partial, ServerFinishReason = "receiver_report_timeout" });
                foreach (ProfileTestResult outcome in outcomes) statusWidth = Math.Max(statusWidth, TextRenderer.MeasureText(ResultCaption(outcome), benchmarkResults.Font).Width + 16);
                foreach (DataGridViewRow row in benchmarkResults.Rows)
                {
                    profileWidth = Math.Max(profileWidth, TextRenderer.MeasureText(Convert.ToString(row.Cells[0].Value), benchmarkResults.Font).Width + 16);
                    statusWidth = Math.Max(statusWidth, TextRenderer.MeasureText(Convert.ToString(row.Cells[1].Value), benchmarkResults.Font).Width + 16);
                }
                benchmarkResults.Columns["Profile"].MinimumWidth = profileWidth;
                benchmarkResults.Columns["Status"].MinimumWidth = Math.Max(100, statusWidth);
                // Font scaling also applies to rows which DataGridView otherwise
                // leaves at the unscaled RowTemplate height.
                int rowHeight = Math.Max(27, (int)Math.Ceiling(27F * benchmarkResults.Font.SizeInPoints / 10F));
                benchmarkResults.RowTemplate.Height = rowHeight;
                foreach (DataGridViewRow row in benchmarkResults.Rows) if (row.Height != rowHeight) row.Height = rowHeight;
                int columnsWidth = 0;
                foreach (DataGridViewColumn column in benchmarkResults.Columns) if (column.Visible) columnsWidth += column.Width;
                int scrollHeight = columnsWidth > benchmarkResults.ClientSize.Width ? SystemInformation.HorizontalScrollBarHeight : 0;
                int height = benchmarkResults.ColumnHeadersHeight + rowHeight * 5 + 2 + scrollHeight + benchmarkResults.Margin.Vertical;
                if (Math.Abs(layout.RowStyles[4].Height - height) > .5F) layout.RowStyles[4].Height = height;
            }
            finally { fittingBenchmarkResults = false; }
        }

        internal void SetTransmissionPreview(bool enabled)
        {
            PreviewEnabled = enabled;
            PreviewRefreshPending = enabled;
            if (previewToggle.Checked != enabled) previewToggle.Checked = enabled;
            if (!enabled)
            {
                Image previous = Preview.Image; Preview.Image = null;
                if (previous != null) previous.Dispose();
            }
        }

        private static Label FieldLabel(string russian, string english)
        {
            var label = new StableLabel { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            UiText.SetText(label, russian, english); return label;
        }

        internal void FitUiToWorkArea(Rectangle area)
        {
            if (area.Width < 1 || area.Height < 1) throw new ArgumentException("A positive work area is required.", "area");
            MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }

        private static void FitDropDown(ComboBox combo)
        {
            int width = combo.Width;
            foreach (object item in combo.Items) width = Math.Max(width, TextRenderer.MeasureText(item.ToString(), combo.Font).Width + 32);
            combo.DropDownWidth = Math.Min(width, Screen.FromControl(combo).WorkingArea.Width);
        }

        private void AddResultColumn(string name, string heading, float weight)
        {
            benchmarkResults.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = heading, ReadOnly = true, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
        }
    }
}
