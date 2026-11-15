// Hidden layout checks. Construction/rendering must not trigger camera or clipboard I/O.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using HorizonClient;

internal static class UiChecks
{
    private static void CheckAutomaticUpgradeScope()
    {
        var activeOld = new ServerConnection { ProcessId = 4411, RequiresUpgrade = true };
        if (!ServerForm.ShouldUpgradeActiveCamera(activeOld, true, false)) throw new Exception("A verified active old backend must be upgraded once.");
        if (ServerForm.ShouldUpgradeActiveCamera(activeOld, true, true)) throw new Exception("A failed or declined upgrade must not create a prompt loop.");
        if (ServerForm.ShouldUpgradeActiveCamera(activeOld, false, false)) throw new Exception("Hidden fixtures and explicit test launches must not start an upgrade.");
        if (ServerForm.ShouldUpgradeActiveCamera(null, true, false)) throw new Exception("Camera absence must never create a camera automatically.");
        activeOld.ProcessId = 0;
        if (ServerForm.ShouldUpgradeActiveCamera(activeOld, true, false)) throw new Exception("Saved stopped camera metadata must not cause automatic activation.");
        activeOld.ProcessId = 4411; activeOld.MetadataRecoveryRequired = true;
        if (ServerForm.ShouldUpgradeActiveCamera(activeOld, true, false)) throw new Exception("Damaged metadata requires an explicit recovery action.");
        activeOld.MetadataRecoveryRequired = false; activeOld.RequiresUpgrade = false;
        if (ServerForm.ShouldUpgradeActiveCamera(activeOld, true, false)) throw new Exception("A current backend must remain running without a restart.");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        UiText.SetLanguage(UiLanguage.Russian); // This layout/documentation fixture checks Russian captions.
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            CheckAutomaticUpgradeScope();
            string output = args.Length == 0 ? Path.Combine(Environment.CurrentDirectory, "build") : Path.GetFullPath(args[0]);
            Directory.CreateDirectory(output);
            using (var server = new ServerForm(null, false))
            {
                int shown = 0; server.Shown += delegate { shown++; };
                CheckDeferredConstructor(server);
                server.ApplyCameraPresence(false, null, false);
                CheckCameraState(server, false, null);
                CheckFailedLookupRecovery(server);
                CheckDriverRestartGuidance(server);
                CheckTabsAndBenchmarkControls(server);
                CheckDocumentation(server);
                CheckAlreadyAppliedFormatPreservesSession(server);
                CheckBenchmarkGeometry(server);
                CheckFilteredProfileIds(server);
                CheckSourceDependentProfiles(server);
                CheckTransferJournal(server);
                ComboBox names = Find<ComboBox>(server, "CameraNameDropDown");
                names.Text = "Моя тестовая камера";
                if (names.Text != "Моя тестовая камера") throw new Exception("A custom camera name cannot be entered.");
                names.Text = CameraNames.Names[0];
                Render(server, 0, Path.Combine(output, "server-settings-ui.png"));
                string existing = CameraNames.Names[0];
                server.ApplyCameraPresence(true, existing, false); CheckCameraState(server, true, existing);
                Render(server, 0, Path.Combine(output, "server-existing-ui.png"));
                server.ApplyCameraPresence(true, false, existing, false); CheckCameraState(server, true, existing, false);
                Render(server, 0, Path.Combine(output, "server-inactive-ui.png"));
                server.ApplyCameraPresence(true, true, existing, false, true); CheckCameraState(server, true, existing, true, true);
                Render(server, 0, Path.Combine(output, "server-upgrade-ui.png"));
                server.ApplyCameraPresence(false, null, true); CheckCameraState(server, false, null);
                PopulateSampleResults(server);
                server.ApplyCameraPresence(true, existing, false);
                Render(server, 0, Path.Combine(output, "server-benchmark-settings-ui.png"));
                Render(server, 1, Path.Combine(output, "server-work-ui.png"));
                Render(server, 2, Path.Combine(output, "server-transfer-log-ui.png"));
                Render(server, 3, Path.Combine(output, "server-documentation-ui.png"));
                CheckReconnectWarningLayout(server, Path.Combine(output, "server-settings-warning-ui.png"));
                Render(server, 1, Path.Combine(output, "server-ui.png"));
                if (server.Visible || shown != 0) throw new Exception("The hidden UI test showed the form.");
                server.FitUiToWorkArea(new Rectangle(0, 0, 1024, 768));
                Render(server, 0, Path.Combine(output, "server-settings-1024-ui.png"));
                if (server.Width > 1024 || server.Height > 768) throw new Exception("The compact window exceeds1024x768.");
                server.Scale(new SizeF(1.5F, 1.5F));
                var scaledControls = new List<Control>(); Collect(server, scaledControls);
                for (int i = scaledControls.Count - 1; i >= 0; i--)
                    scaledControls[i].Font = new Font(scaledControls[i].Font.FontFamily, scaledControls[i].Font.Size * 1.5F, scaledControls[i].Font.Style);
                server.Font = new Font(server.Font.FontFamily, server.Font.Size * 1.5F, server.Font.Style);
                server.FitUiToWorkArea(new Rectangle(0, 0, 1344, 1040));
                Render(server, 1, Path.Combine(output, "server-work-150-ui.png"));
                Render(server, 0, Path.Combine(output, "server-settings-150-ui.png"));
                CheckReconnectWarningLayout(server, Path.Combine(output, "server-settings-warning-150-ui.png"));
                Render(server, 3, Path.Combine(output, "server-documentation-150-ui.png"));
                if (server.Width > 1344 || server.Height > 1040) throw new Exception("The scaled window exceeds the screen work area.");
            }
            Console.WriteLine("Server UI checks passed: camera-first access, vertical Settings fields, bordered speed-test group, five visible result rows, compact1024x768 and100/150percent bounds, default10KB, three YUY2 source modes, nine transfer profiles, short instructions; hidden rendering only, no camera/clipboard/driver actions.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckDeferredConstructor(Form form)
    {
        ComboBox names = Find<ComboBox>(form, "CameraNameDropDown");
        CheckBox diagnostic = Find<CheckBox>(form, "DetailedSystemLog");
        if (diagnostic.Checked || AncestorTab(diagnostic).Name != "SettingsTab") throw new Exception("Detailed logging must default to disabled in server settings.");
        if (!CameraNames.Contains(names.Text) || names.Enabled) throw new Exception("Camera startup discovery must precede editing; the suggestion must come from the catalog.");
        if (Find<Button>(form, "CreateCamera").Enabled || Find<Button>(form, "DeleteCamera").Enabled) throw new Exception("Constructor enabled camera mutations before startup discovery.");
    }

    private static void CheckCameraState(Form form, bool exists, string name, bool running = true, bool upgrade = false)
    {
        if (form.Text != "Get Copy-Paste Back — Server " + typeof(ServerForm).Assembly.GetName().Version.ToString() + " — локальный ПК") throw new Exception("Window title must include the compiled application version.");
        foreach (string retired in new[] { "AudioRoutesPanel", "AudioRoutesTest", "AudioRoutesResults" })
            if (form.Controls.Find(retired, true).Length != 0) throw new Exception("Retired multi-channel audio controls remain visible.");
        Button create = Find<Button>(form, "CreateCamera"), delete = Find<Button>(form, "DeleteCamera");
        ComboBox names = Find<ComboBox>(form, "CameraNameDropDown");
        string action = upgrade ? "Обновить камеру" : exists && !running ? "Запустить камеру" : "Создать камеру";
        if (create.Text != action || create.Enabled != (!exists || !running || upgrade)) throw new Exception("Wrong Create/Start/Update state.");
        if (delete.Text != "Удалить камеру" || delete.Enabled != exists) throw new Exception("Wrong Delete state.");
        if (names.Enabled == exists || (exists ? names.Text != name : !CameraNames.Contains(names.Text))) throw new Exception("Camera identity must remain locked until deletion.");
        if (names.DropDownStyle != ComboBoxStyle.DropDown || names.MaxLength != 100 || names.Items.Count != 100) throw new Exception("The editable bounded camera catalog is missing.");
        foreach (string preset in CameraNames.Names) if (!names.Items.Contains(preset)) throw new Exception("A camera preset is missing.");
        TabControl tabs = Find<TabControl>(form, "ServerTabs");
        if (!tabs.TabPages[0].Enabled || !tabs.TabPages[3].Enabled || tabs.TabPages[1].Enabled != exists || tabs.TabPages[2].Enabled != exists)
            throw new Exception("Work and transfer-log access must follow confirmed camera presence while Settings/Documentation remain available.");
        if (!exists)
        {
            IntPtr hiddenTabsHandle = tabs.Handle;
            tabs.SelectedIndex = 0; tabs.SelectedIndex = 1;
            if (tabs.SelectedIndex != 0) throw new Exception("A disabled Work tab must reject selection before camera creation.");
            tabs.SelectedIndex = 2;
            if (tabs.SelectedIndex != 0 || Find<GroupBox>(form, "BenchmarkPanel").Enabled)
                throw new Exception("Camera absence must lock transfer-log selection and the complete speed-test group.");
        }
    }

    private static void CheckFailedLookupRecovery(ServerForm form)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(ServerForm);
        FieldInfo lookup = type.GetField("cameraLookupKnown", flags), exists = type.GetField("cameraExists", flags);
        FieldInfo cleanup = type.GetField("cameraCleanupAvailable", flags);
        MethodInfo configure = type.GetMethod("SetConfigurationEnabled", flags), fail = type.GetMethod("ShowFailure", flags);
        Button create = Find<Button>(form, "CreateCamera"), delete = Find<Button>(form, "DeleteCamera");
        try
        {
            lookup.SetValue(form, false); exists.SetValue(form, false); cleanup.SetValue(form, true);
            configure.Invoke(form, new object[] { false });
            if (delete.Enabled || create.Enabled) throw new Exception("Recovery actions must remain disabled while camera configuration is busy.");
            configure.Invoke(form, new object[] { true });
            if (!delete.Enabled) throw new Exception("Saved camera cleanup must remain available when discovery fails.");
            if (!create.Enabled || create.Text != "Повторить проверку" || Find<ComboBox>(form, "CameraNameDropDown").Enabled)
                throw new Exception("An unknown camera state must allow retry while keeping identity edits locked.");
            fail.Invoke(form, new object[] { new IOException("#< CLIXML <Objs> fixture-private-stack\r\n at Fixture.Stack() 0x80070005") });
            if (form.Controls.Find("Diagnostics", true).Length != 0) throw new Exception("An unused technical diagnostics control must not remain in the GUI.");
            var controls = new List<Control>(); Collect(form, controls);
            foreach (Control control in controls)
            {
                if (!(control is Label)) continue;
                foreach (string marker in new[] { "CLIXML", "<Objs>", "fixture-private-stack", "Fixture.Stack()", "0x80070005" })
                    if (control.Text.Contains(marker)) throw new Exception("A GUI label exposed raw camera failure details: " + control.Name);
            }
            Label state = Find<Label>(form, "SettingsState"), detail = Find<Label>(form, "SettingsDetail");
            if (state.Text != "Камера не запущена." || detail.Text.Length > 180 || !detail.Text.Contains("удалить") || !detail.Text.Contains("создать"))
                throw new Exception("A recoverable camera failure must give a short delete-and-create instruction.");
            if (!delete.Enabled || !create.Enabled) throw new Exception("Displaying a failure must not remove the cleanup or retry actions.");
            MethodInfo showDiagnostics = type.GetMethod("ShowDiagnostics", flags);
            showDiagnostics.Invoke(form, new object[] { new CameraDriverConflictException("technical-secret VBoxStack at PrivateFixture.Driver()") });
            if (!detail.Text.Contains("USB") || !detail.Text.Contains("Windows") || detail.Text.Length > 180 ||
                detail.Text.Contains("удалить") || detail.Text.Contains("создать"))
                throw new Exception("A blocking driver conflict must explain the Windows USB conflict without promising delete-and-create will fix it.");
            foreach (Control control in controls)
                if (control is Label && (control.Text.Contains("technical-secret") || control.Text.Contains("VBoxStack") || control.Text.Contains("PrivateFixture.Driver()")))
                    throw new Exception("A GUI label exposed raw USB driver conflict details: " + control.Name);
            if (!delete.Enabled || !create.Enabled)
                throw new Exception("Showing a concise driver conflict must keep diagnostics hidden and preserve metadata cleanup actions.");
            form.ApplyCameraPresence(false, null, false);
            if ((bool)cleanup.GetValue(form) || !(bool)lookup.GetValue(form) || !create.Enabled || delete.Enabled || !Find<ComboBox>(form, "CameraNameDropDown").Enabled)
                throw new Exception("Confirmed deletion must clear recovery cleanup state and permit creating a fresh camera.");
            CheckCameraState(form, false, null);
        }
        finally
        {
            form.ApplyCameraPresence(false, null, false);
        }
    }

    private static void CheckDriverRestartGuidance(ServerForm form)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(ServerForm);
        MethodInfo fail = type.GetMethod("ShowFailure", flags);
        try
        {
            foreach (bool savedCamera in new[] { false, true })
            {
                form.ApplyCameraPresence(savedCamera, false, savedCamera ? CameraNames.Names[0] : null, false);
                fail.Invoke(form, new object[] { new CameraDriverRestartRequiredException() });
                Label state = Find<Label>(form, "SettingsState"), detail = Find<Label>(form, "SettingsDetail");
                if (state.Text != "Камера не запущена." || !detail.Text.Contains("Перезагрузите Windows") ||
                    !detail.Text.Contains("создайте камеру") || detail.Text.Length > 180)
                    throw new Exception("A completed driver preparation must clearly request a manual Windows restart and camera creation.");
                if (detail.Text.Contains("VBox") || detail.Text.Contains("конфликт") || detail.Text.Contains("удалить"))
                    throw new Exception("Restart guidance must not expose driver internals or suggest repeating deletion fixes a loaded driver.");
                Button create = Find<Button>(form, "CreateCamera"), delete = Find<Button>(form, "DeleteCamera");
                if (!create.Enabled || create.Text != (savedCamera ? "Запустить камеру" : "Создать камеру") || delete.Enabled != savedCamera)
                    throw new Exception("A restart request must preserve the correct camera actions for the next application run.");
                if (Find<Button>(form, "Benchmark").Enabled)
                    throw new Exception("A camera awaiting Windows restart must not allow transmission or a speed test.");
            }
        }
        finally
        {
            form.ApplyCameraPresence(false, null, false);
        }
    }

    private static void CheckTabsAndBenchmarkControls(ServerForm form)
    {
        TabControl tabs = Find<TabControl>(form, "ServerTabs");
        if (tabs.TabPages.Count != 4 || tabs.TabPages[0].Text != "Настройки" || tabs.TabPages[1].Text != "Работа" || tabs.TabPages[2].Text != "Лог передачи" || tabs.TabPages[3].Text != "Документация" || tabs.TabPages[3].Name != "DocumentationTab") throw new Exception("The requested Settings/Work/Transfer log/Documentation tabs are missing.");
        if (AncestorTab(Find<ComboBox>(form, "CameraNameDropDown")) != tabs.TabPages[0]) throw new Exception("Camera management must be on Settings.");
        foreach (string name in new[] { "VideoProfile", "CameraMode", "AudioOutput", "TransmissionPreview" })
            if (AncestorTab(Find<Control>(form, name)) != tabs.TabPages[1]) throw new Exception("The working control is on the wrong tab: " + name);
        foreach (string name in new[] { "Benchmark", "BenchmarkSize", "BenchmarkLimit", "BenchmarkResults", "BenchmarkProgress", "AudioRetries" })
            if (AncestorTab(Find<Control>(form, name)) != tabs.TabPages[0]) throw new Exception("The test/retry setting must be on Settings: " + name);
        foreach (string name in new[] { "TransferLogText", "TransferLogEnabled", "ClearTransferLog" })
            if (AncestorTab(Find<Control>(form, name)) != tabs.TabPages[2]) throw new Exception("A journal control is on the wrong tab: " + name);
        NumericUpDown retries = Find<NumericUpDown>(form, "AudioRetries");
        if (retries.Value != 2 || retries.Minimum != 0 || retries.Maximum != 10) throw new Exception("Reverse audio must offer two additional attempts by default with bounded0..10 choices.");
        ComboBox sizes = Find<ComboBox>(form, "BenchmarkSize"), limits = Find<ComboBox>(form, "BenchmarkLimit");
        long[] bytes = { 1024, 10240, 102400, 1048576, 10485760, 104857600 };
        if (sizes.Items.Count != bytes.Length || sizes.SelectedIndex != 1 || form.SelectedBenchmarkBytes != 10240) throw new Exception("Wrong file-size default or choices.");
        for (int i = 0; i < bytes.Length; i++)
            if ((long)sizes.Items[i].GetType().GetField("Bytes", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sizes.Items[i]) != bytes[i]) throw new Exception("File-size byte mapping is wrong.");
        int[] seconds = { 180, 600, 1800, 0 };
        if (limits.Items.Count != 4 || limits.SelectedIndex != 0 || form.SelectedBenchmarkLimitSeconds != 180) throw new Exception("Wrong per-profile time-limit default.");
        for (int i = 0; i < seconds.Length; i++)
            if ((int)limits.Items[i].GetType().GetField("Seconds", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(limits.Items[i]) != seconds[i]) throw new Exception("Time-limit mapping is wrong.");
        ComboBox profiles = Find<ComboBox>(form, "VideoProfile");
        if (profiles.Items.Count != 5 || profiles.Items.Count != AvailableProfiles(form).Length || profiles.SelectedIndex != Array.IndexOf(AvailableProfiles(form), VideoProfile.HighDefinitionRobust)) throw new Exception("The verified1080p robust default or source-filtered profile choices changed.");
        foreach (object item in profiles.Items)
            if (item.ToString().Contains("5 блоков") || item.ToString().Contains("Совместимый") || item.ToString().Contains("ячейки 1 пиксель")) throw new Exception("Removed profiles must never be offered.");
        if (Find<CheckBox>(form, "TransmissionPreviewEnabled").Checked) throw new Exception("Preview must be disabled at each server startup.");
        if (form.Controls.Find("JpegQuality", true).Length != 0) throw new Exception("JPEG quality is not a user setting in the uncompressed-only server.");
        ComboBox cameraModes = Find<ComboBox>(form, "CameraMode");
        if (cameraModes.Items.Count != 3) throw new Exception("Exactly three uncompressed YUY2 source modes should be offered, through1080p.");
        if (cameraModes.SelectedIndex != 2 || cameraModes.Text.IndexOf("1920×1080", StringComparison.Ordinal) < 0 || cameraModes.Text.IndexOf("YUY2", StringComparison.Ordinal) < 0)
            throw new Exception("New cameras must start from the selected1080pYUY2 configuration, not an implicit720pMJPEG setup default.");
        int[] modeWidths = { 640, 1280, 1920 }, modeHeights = { 480, 720, 1080 };
        for (int index = 0; index < cameraModes.Items.Count; index++)
        {
            object mode = cameraModes.Items[index];
            const BindingFlags modeFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            int width = (int)mode.GetType().GetField("Width", modeFlags).GetValue(mode);
            int height = (int)mode.GetType().GetField("Height", modeFlags).GetValue(mode);
            if (width != modeWidths[index] || height != modeHeights[index] || (int)mode.GetType().GetField("Fps", modeFlags).GetValue(mode) != 30)
                throw new Exception("Camera modes must retain their three stable indices and30FPS through1080p.");
            if (!(bool)mode.GetType().GetField("Raw", modeFlags).GetValue(mode) || (bool)mode.GetType().GetField("Probe", modeFlags).GetValue(mode)) throw new Exception("Source modes must be YUY2 without JPEG/H264 probe.");
        }
        if (form.Controls.Find("Pause", true).Length != 0) throw new Exception("The retired Pause button must not remain in the UI.");
        if (Find<Button>(form, "BenchmarkStop").Enabled) throw new Exception("Transmission actions are enabled before preparation.");
        if (Find<Button>(form, "BenchmarkStop").Text != "Остановить проверку") throw new Exception("The Stop action is missing.");
        if (form.Controls.Find("BenchmarkKeep", true).Length != 0) throw new Exception("The retired profile-filter button must not remain.");
        DataGridView grid = Find<DataGridView>(form, "BenchmarkResults");
        if (grid.Columns["Profile"].HeaderText != "Режим передачи" || grid.AutoSizeRowsMode != DataGridViewAutoSizeRowsMode.None) throw new Exception("Result headings or stable row geometry regressed.");
        if (grid.AllowUserToAddRows || grid.AllowUserToDeleteRows || grid.Columns.Count != 7 || grid.Columns.Contains("Keep")) throw new Exception("The benchmark grid must contain seven read-only result columns without profile-selection checkboxes.");
        foreach (DataGridViewColumn column in grid.Columns) if (column is DataGridViewCheckBoxColumn) throw new Exception("A retired profile-selection checkbox remains.");
        foreach (string name in new[] { "Profile", "Status", "FrameSize", "Bytes", "FileSize", "Seconds", "Speed" }) if (!grid.Columns[name].ReadOnly) throw new Exception("A result value is editable: " + name);
    }

    private static void CheckDocumentation(ServerForm form)
    {
        TabControl tabs = Find<TabControl>(form, "ServerTabs");
        TextBox text = Find<TextBox>(form, "DocumentationText");
        if (AncestorTab(text) != tabs.TabPages[3] || !text.ReadOnly || !text.Multiline || !text.WordWrap ||
            text.ScrollBars != ScrollBars.Vertical || text.Dock != DockStyle.Fill || text.Font.Size < 11F)
            throw new Exception("Documentation must be readable, read-only, wrapped and scrollable on its own fourth tab.");
        foreach (string instruction in new[] { "Server.exe", "Client.exe", "Создать камеру", "зелёный значок", "Аудиовыход", "Звук Horizon", "Ctrl+C", "Ctrl+V", "Один короткий сигнал", "Два высоких коротких сигнала", "Проверить скорость", "фактическое разрешение", "полностью закройте удалённый рабочий стол", "Закрыть только наш значок в трее недостаточно", "Применить формат", "старый буфер не отправляется", "Предпросмотр передачи", "Проверка скорости", "10 КБ", "недоступны", "заново выберите новую камеру" })
            if (!text.Text.Contains(instruction)) throw new Exception("Documentation is missing an essential user instruction: " + instruction);
        if (text.Text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length > 350)
            throw new Exception("The beginner instructions must remain short.");
        foreach (string removed in new[] { "Пауза", "1440p", "4K" })
            if (text.Text.Contains(removed)) throw new Exception("Documentation describes a removed control/mode: " + removed);
        if (!text.Text.Contains("выключите «Системные звуки»") || !text.Text.Contains("звук Horizon на ноутбуке") ||
            !text.Text.Contains("клиент сразу отправляет проверочный звук") || !text.Text.Contains("Тест аудио прошёл успешно") ||
            !text.Text.Contains("Подробный системный лог") || !text.Text.Contains("сервера включён по умолчанию") || !text.Text.Contains("Лог клиента изначально выключен") ||
            !text.Text.Contains("HorizonServer\\data") || !text.Text.Contains("setup.debug.log"))
            throw new Exception("Audio instructions must distinguish application sound from interfering system sounds and explain selection-triggered audio verification.");
    }

    private static void CheckTransferJournal(ServerForm form)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var begin = typeof(ServerForm).GetMethod("BeginForwardJournal", flags);
        var complete = typeof(ServerForm).GetMethod("CompleteForwardJournal", flags);
        TextBox log = Find<TextBox>(form, "TransferLogText"); CheckBox enabled = Find<CheckBox>(form, "TransferLogEnabled");
        Guid transfer = Guid.NewGuid(); begin.Invoke(form, new object[] { transfer, 182 });
        if (!log.Text.Contains("Началась передача") || !log.Text.Contains("182 байт")) throw new Exception("The journal must report the actual transfer size at its start.");
        string before = log.Text; complete.Invoke(form, new object[] { Guid.NewGuid(), 182 });
        if (log.Text != before) throw new Exception("An unrelated acknowledgement must not report success.");
        complete.Invoke(form, new object[] { transfer, 182 });
        if (!log.Text.Contains("переданы на удалённый доступ за") || !log.Text.Contains("Можно нажать Ctrl+V")) throw new Exception("The journal must show completion and paste readiness after a matching receipt.");
        before = log.Text; enabled.Checked = false;
        begin.Invoke(form, new object[] { Guid.NewGuid(), 18432 });
        if (log.Text != before) throw new Exception("A disabled journal must never append events.");
        enabled.Checked = true;
        if (ServerForm.FormatTransferBytes(18432) != "18 КБ" || ServerForm.FormatTransferBytes(1048576) != "1 МБ") throw new Exception("Human transfer units are wrong.");
        log.Clear();
    }

    private static void CheckAlreadyAppliedFormatPreservesSession(ServerForm form)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(ServerForm);
        ComboBox modes = Find<ComboBox>(form, "CameraMode");
        var saved = new Dictionary<string, object>();
        foreach (string name in new[] { "ready", "cameraRunning", "cameraExists", "cameraRequiresUpgrade", "activeVideoMode", "bridgeSession", "current", "generation" })
            saved[name] = type.GetField(name, flags).GetValue(form);
        try
        {
            Guid session = Guid.NewGuid(); byte[] pending = { 1, 2, 3 };
            type.GetField("ready", flags).SetValue(form, true);
            type.GetField("cameraRunning", flags).SetValue(form, true);
            type.GetField("cameraExists", flags).SetValue(form, true);
            type.GetField("cameraRequiresUpgrade", flags).SetValue(form, false);
            type.GetField("activeVideoMode", flags).SetValue(form, modes.SelectedItem);
            type.GetField("bridgeSession", flags).SetValue(form, session);
            type.GetField("current", flags).SetValue(form, pending);
            type.GetMethod("ApplyVideoMode", flags).Invoke(form, null);
            if (!(bool)type.GetField("ready", flags).GetValue(form) ||
                (Guid)type.GetField("bridgeSession", flags).GetValue(form) != session ||
                !Object.ReferenceEquals(type.GetField("current", flags).GetValue(form), pending) ||
                !Object.Equals(type.GetField("generation", flags).GetValue(form), saved["generation"]))
                throw new Exception("Applying the already active format must preserve the producer session and pending clipboard data.");
        }
        finally { foreach (var item in saved) type.GetField(item.Key, flags).SetValue(form, item.Value); }
    }

    private static void CheckBenchmarkGeometry(ServerForm form)
    {
        MethodInfo reduced = typeof(ServerForm).GetMethod("ProfileWasReduced", BindingFlags.Static | BindingFlags.NonPublic);
        ComboBox modes = Find<ComboBox>(form, "CameraMode");
        var result = new ProfileTestResult { Profile = VideoProfile.HighDefinitionRobust, Width = 1920, Height = 1080 };
        if (!(bool)reduced.Invoke(null, new[] { (object)result, modes.Items[0] }))
            throw new Exception("A 640x480 source must remain marked as reduced even when the receiver upscales it to1080p.");
        if ((bool)reduced.Invoke(null, new[] { (object)result, modes.Items[2] }))
            throw new Exception("Actual1080YUY2 source and1080 receiver must not be marked reduced.");
        result.Width = 320; result.Height = 240;
        if (!(bool)reduced.Invoke(null, new[] { (object)result, modes.Items[2] }))
            throw new Exception("A receiver that silently reduces1080p to320x240 must be marked in the result.");
    }

    private static void PopulateSampleResults(Form form)
    {
        DataGridView grid = Find<DataGridView>(form, "BenchmarkResults");
        grid.Rows.Add("720p плотный", "Работает", "1280×720", "104 857 600", "104 857 600", "1800,00", "999,999 Мбит/с");
        grid.Rows[0].Tag = VideoProfile.Dense;
        grid.Rows.Add("720p", "Не завершён: лимит времени", "1280×720", "46 080", "102 400", "180,00", "0,002 Мбит/с");
        grid.Rows[1].Tag = VideoProfile.Fast;
        grid.Rows.Add("1080p устойчивый", "Нет подтверждения разрешения", "1920×1080 ↓", "10 240", "10 240", "0,48", "0,995 Мбит/с");
        grid.Rows[2].Tag = VideoProfile.HighDefinitionRobust;
        grid.Rows.Add("640p", "Разрешения различаются", "1920×1080", "10 240", "10 240", "1,12", "0,073 Мбит/с");
        grid.Rows[3].Tag = VideoProfile.Compact640;
        grid.Rows.Add("MP4Modem V2", "Ошибка проверки данных", "1920×1080", "8 192", "10 240", "180,00", "0,001 Мбит/с");
        grid.Rows[4].Tag = VideoProfile.Mp4Modem;
        Find<Label>(form, "BenchmarkProgress").Text = "Режим передачи 2 из " + AvailableProfiles((ServerForm)form).Length + " · 720p · 00:15 · 46 080 / 102 400 байт · 256 Б/с";
    }

    private static void CheckFilteredProfileIds(ServerForm form)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(ServerForm);
        FieldInfo restoring = type.GetField("restoringSettings", flags), selected = type.GetField("selectedProfile", flags);
        MethodInfo populate = type.GetMethod("PopulateProfileChoices", flags);
        PropertyInfo choice = type.GetProperty("SelectedProfileChoice", flags);
        bool before = (bool)restoring.GetValue(form); VideoProfile previous = (VideoProfile)selected.GetValue(form);
        CheckBox colors = Find<CheckBox>(form, "ColorModesEnabled"); bool oldColors = colors.Checked;
        restoring.SetValue(form, true); colors.Checked = true;
        try
        {
            populate.Invoke(form, new object[] { VideoProfile.Mp4Modem });
            ComboBox combo = Find<ComboBox>(form, "VideoProfile");
            if (combo.Items.Count != 9 || combo.SelectedIndex != Array.IndexOf(AvailableProfiles(form), VideoProfile.Mp4Modem) || (VideoProfile)choice.GetValue(form, null) != VideoProfile.Mp4Modem || (VideoProfile)selected.GetValue(form) != VideoProfile.Mp4Modem)
                throw new Exception("The complete catalog must preserve selected enumID11 independently of its dropdown index.");
            var document = new XmlDocument(); document.LoadXml("<settings enabledProfiles='11' benchmarkBytes='10240' benchmarkLimitSeconds='180' />");
            type.GetMethod("LoadBenchmarkSettings", flags).Invoke(form, new object[] { document.DocumentElement });
            populate.Invoke(form, new object[] { VideoProfile.Mp4Modem });
            if (combo.Items.Count != 9 || (VideoProfile)choice.GetValue(form, null) != VideoProfile.Mp4Modem)
                throw new Exception("A legacy saved profile filter must be ignored without losing the selected valid mode.");
            using (var text = new StringWriter())
            {
                using (XmlWriter writer = XmlWriter.Create(text))
                {
                    writer.WriteStartElement("settings"); type.GetMethod("WriteBenchmarkSettings", flags).Invoke(form, new object[] { writer }); writer.WriteEndElement();
                }
                if (text.ToString().Contains("enabledProfiles")) throw new Exception("Retired profile filters must no longer be persisted.");
            }
            populate.Invoke(form, new object[] { VideoProfile.Maximum4K });
            if (combo.Items.Count != 9 || (VideoProfile)choice.GetValue(form, null) != VideoProfile.HighDefinitionRobust)
                throw new Exception("A stale removed profile must normalize while retaining all nine offered choices.");
            populate.Invoke(form, new object[] { VideoProfile.Compatible });
            if (combo.Items.Count != 9 || (VideoProfile)choice.GetValue(form, null) != VideoProfile.HighDefinitionRobust)
                throw new Exception("A saved five-block selection must migrate to an offered profile.");
            foreach (VideoProfile color in new[] { VideoProfile.Color720, VideoProfile.Color720Dense, VideoProfile.Color1080, VideoProfile.Color1080Dense })
            {
                populate.Invoke(form, new object[] { color });
                if ((VideoProfile)choice.GetValue(form, null) != color || !combo.Text.Contains("4 цвета"))
                    throw new Exception("Every appended color ID must select the matching Russian color caption.");
            }
        }
        finally
        {
            colors.Checked = oldColors;
            populate.Invoke(form, new object[] { previous });
            restoring.SetValue(form, before);
        }
    }

    private static VideoProfile[] AvailableProfiles(ServerForm form)
    { return (VideoProfile[])typeof(ServerForm).GetMethod("GetAvailableTransferProfiles", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null); }

    private static void CheckSourceDependentProfiles(ServerForm form)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(ServerForm);
        FieldInfo source = type.GetField("activeVideoMode", flags), selected = type.GetField("selectedProfile", flags);
        MethodInfo refresh = type.GetMethod("RefreshTransferProfileChoices", flags);
        PropertyInfo selectedChoice = type.GetProperty("SelectedProfileChoice", flags);
        ComboBox modes = Find<ComboBox>(form, "CameraMode"), profiles = Find<ComboBox>(form, "VideoProfile");
        object oldSource = source.GetValue(form), oldSelected = selected.GetValue(form);
        int oldMode = modes.SelectedIndex;
        try
        {
            source.SetValue(form, null); modes.SelectedIndex = 2; refresh.Invoke(form, null);
            if (AvailableProfiles(form).Length != 5 || profiles.Items.Count != 5)
                throw new Exception("An unknown source must offer only the nine1080p-or-smaller profiles.");
            object legacyHighSource = Activator.CreateInstance(modes.Items[0].GetType(), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { 3840, 2160, 30, true, false }, null);
            source.SetValue(form, legacyHighSource); selected.SetValue(form, VideoProfile.Ultra4KRobust); refresh.Invoke(form, null);
            if (AvailableProfiles(form).Length != 5 || profiles.Items.Count != 5 || (VideoProfile)selectedChoice.GetValue(form, null) != VideoProfile.HighDefinitionRobust)
                throw new Exception("A legacy high-resolution source must not re-expose removed4K profiles.");
            foreach (VideoProfile profile in AvailableProfiles(form))
                if (VisualProtocol.GetWidth(profile) > 1920 || VisualProtocol.GetHeight(profile) > 1080)
                    throw new Exception("An oversized profile leaked into the nine-mode experimental catalog.");
            source.SetValue(form, modes.Items[2]); refresh.Invoke(form, null);
            if (AvailableProfiles(form).Length != 5 || profiles.Items.Count != 5 || (VideoProfile)selectedChoice.GetValue(form, null) != VideoProfile.HighDefinitionRobust)
                throw new Exception("Returning to1080 removes4K choices and normalizes an unavailable selection.");
            if (type.GetField("restoringSettings", flags).GetValue(form).Equals(true))
                throw new Exception("An automatic source refresh must restore the settings-event guard.");
        }
        finally
        {
            source.SetValue(form, oldSource); selected.SetValue(form, oldSelected); modes.SelectedIndex = oldMode; refresh.Invoke(form, null);
        }
    }

    private static void Render(Form form, int selected, string path)
    {
        TabControl tabs = Find<TabControl>(form, "ServerTabs"); tabs.SelectedIndex = selected;
        IntPtr handle = form.Handle; form.CreateControl(); LayoutTree(form); LayoutTree(form);
        var controls = new List<Control>(); Collect(form, controls);
        using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(form.BackColor);
            foreach (Control control in controls)
            {
                bool gridChild = false;
                for (Control parent = control.Parent; parent != null; parent = parent.Parent) if (parent is DataGridView) { gridChild = true; break; }
                if (gridChild) continue;
                TabPage page = AncestorTab(control);
                if (page != null && page != tabs.SelectedTab) continue;
                Panel scrollParent = control.Parent as Panel;
                if (control.Name.Length > 0 && control.Parent != null && control.Dock != DockStyle.Fill && (scrollParent == null || !scrollParent.AutoScroll) && !control.Parent.ClientRectangle.Contains(control.Bounds)) throw new Exception("Control exceeds its parent: " + control.Name + " " + control.Bounds);
                if (control.Text.IndexOf('\uFFFD') >= 0 || control.Text.Contains("РЎРѕР·")) throw new Exception("Corrupted Russian UI text.");
                if (control.Width < 1 || control.Height < 1) continue;
                Point origin = Point.Empty;
                for (Control current = control; current != null && current != form; current = current.Parent) origin.Offset(current.Left, current.Top);
                if (origin.X < 0 || origin.Y < 0 || origin.X + control.Width > bitmap.Width || origin.Y + control.Height > bitmap.Height) continue;
                control.DrawToBitmap(bitmap, new Rectangle(origin, control.Size));
                ComboBox combo = control as ComboBox;
                if (combo != null && !String.IsNullOrEmpty(combo.Text))
                    using (Graphics graphics = Graphics.FromImage(bitmap)) TextRenderer.DrawText(graphics, combo.Text, combo.Font, new Rectangle(origin.X + 4, origin.Y + 2, combo.Width - 26, combo.Height - 4), combo.ForeColor, combo.BackColor, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            bitmap.Save(path, ImageFormat.Png);
        }
        if (selected == 0) CheckCompactSettings(form, tabs.SelectedTab);
    }

    private static void CheckCompactSettings(Form form, TabPage page)
    {
        TableLayoutPanel topRow = Find<TableLayoutPanel>(form, "SettingsTopRow");
        TableLayoutPanel retries = Find<TableLayoutPanel>(form, "AudioRetryOptions");
        GroupBox panel = Find<GroupBox>(form, "BenchmarkPanel");
        TableLayoutPanel files = Find<TableLayoutPanel>(form, "BenchmarkFileOptions"), limits = Find<TableLayoutPanel>(form, "BenchmarkLimitOptions");
        TableLayoutPanel testActions = Find<TableLayoutPanel>(form, "BenchmarkActions");
        Control logging = Find<CheckBox>(form, "DetailedSystemLog");
        CheckBox keepCamera = Find<CheckBox>(form, "KeepCameraRunning");
        CheckBox accepted = Find<CheckBox>(form, "AcceptedSoundEnabled"), delivered = Find<CheckBox>(form, "DeliveredSoundEnabled");
        Control previousPreference = keepCamera;
        foreach (CheckBox sound in new[] { accepted, delivered })
        {
            if (!sound.Checked || sound.Parent != retries.Parent || previousPreference.Bottom > sound.Top || sound.Bottom > logging.Top ||
                TextRenderer.MeasureText(sound.Text, sound.Font, Size.Empty, TextFormatFlags.SingleLine).Width + 24 > sound.Width)
                throw new Exception("Sound preferences must default on and occupy separate readable settings rows before logging at either DPI: " + sound.Name);
            previousPreference = sound;
        }
        if (keepCamera.Parent != retries.Parent || keepCamera.Text != "Оставлять камеру работающей после выхода" ||
            retries.Bottom > keepCamera.Top || keepCamera.Bottom > logging.Top ||
            TextRenderer.MeasureText(keepCamera.Text, keepCamera.Font, Size.Empty, TextFormatFlags.SingleLine).Width + 24 > keepCamera.Width)
            throw new Exception("The keep-camera preference must occupy its own fully readable settings row before logging at either DPI.");
        const string retryCaption = "Повторы обратной передачи от клиента в случае ошибок";
        if (retries.Controls.Count != 2 || retries.Controls[0].Text != retryCaption || Find<NumericUpDown>(form, "AudioRetries").Parent != retries ||
            retries.GetPositionFromControl(Find<NumericUpDown>(form, "AudioRetries")).Row != retries.GetPositionFromControl(retries.Controls[0]).Row)
            throw new Exception("The exact expanded retry caption and its numeric choice must share one row.");
        Control previousTopControl = null;
        foreach (string name in new[] { "SettingsLanguageLabel", "LanguageChoice", "SettingsCameraLabel", "CameraNameDropDown", "CreateCamera", "DeleteCamera" })
        {
            Control control = Find<Control>(form, name);
            if (control.Parent != topRow || topRow.GetPositionFromControl(control).Row != 0 || previousTopControl != null && previousTopControl.Right > control.Left)
                throw new Exception("Language, camera name and lifecycle actions must share the top row in the requested order: " + name);
            previousTopControl = control;
        }
        if (topRow.Bottom > retries.Top || retries.Bottom > keepCamera.Top || keepCamera.Bottom > logging.Top || logging.Bottom > panel.Top)
            throw new Exception("Settings fields and detailed logging overlap or share rows.");
        if (panel.Text != "Проверка скорости" || Find<ComboBox>(form, "BenchmarkSize").Parent != files || Find<ComboBox>(form, "BenchmarkLimit").Parent != limits ||
            Find<Button>(form, "Benchmark").Parent != testActions || files.Bottom > limits.Top || limits.Bottom > testActions.Top)
            throw new Exception("The bordered test block must give file size, deadline and actions their own rows.");
        foreach (TableLayoutPanel row in new[] { topRow, retries, files, limits })
            foreach (Control control in row.Controls)
                if (control is Label && TextRenderer.MeasureText(control.Text, control.Font, Size.Empty, TextFormatFlags.SingleLine).Width > control.Width)
                    throw new Exception("A field heading must fit without wrapping at either DPI: " + control.Text);
        DataGridView grid = Find<DataGridView>(form, "BenchmarkResults");
        int expectedRows = grid.Rows.Count == 0 ? grid.RowTemplate.Height * 5 : grid.Rows[0].Height * 5;
        int columnsWidth = 0;
        foreach (DataGridViewColumn column in grid.Columns) if (column.Visible) columnsWidth += column.Width;
        int scrollbarHeight = columnsWidth > grid.ClientSize.Width ? SystemInformation.HorizontalScrollBarHeight : 0;
        int expectedHeight = grid.ColumnHeadersHeight + expectedRows + 2 + scrollbarHeight;
        if (grid.Height < expectedHeight || grid.Height > expectedHeight + 2)
            throw new Exception("The results grid must fit exactly five measured rows: expected=" + expectedHeight + ", actual=" + grid.Height + ", form=" + form.Size + ".");
        Point bottom = grid.PointToScreen(new Point(0, grid.Height));
        Point panelBottom = panel.PointToScreen(new Point(0, panel.ClientSize.Height - panel.Padding.Bottom));
        if (bottom.Y > panelBottom.Y) throw new Exception("Five result rows extend beyond the speed-test border at this DPI: grid=" + grid.Bounds + ", panel=" + panel.Bounds + ", bottom=" + bottom.Y + ", panelBottom=" + panelBottom.Y + ", form=" + form.Size + ".");
        // Hidden native row rectangles can be empty. The measured header + row
        // sum must fit above any horizontal scrollbar regardless of visibility.
        if (grid.Rows.Count >= 5 && grid.ColumnHeadersHeight + expectedRows + 2 > grid.ClientSize.Height - scrollbarHeight + 2)
            throw new Exception("The fifth result row is clipped at this DPI.");
        if (grid.Rows.Count >= 5)
        {
            foreach (DataGridViewRow row in grid.Rows)
                foreach (DataGridViewCell cell in row.Cells)
                {
                    string value = Convert.ToString(cell.FormattedValue);
                    int needed = TextRenderer.MeasureText(value, grid.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                    if (needed > grid.Columns[cell.ColumnIndex].Width - 8)
                        throw new Exception("A result cell is truncated at this DPI: column=" + grid.Columns[cell.ColumnIndex].Name + ", value=" + value + ", needed=" + needed + ", width=" + grid.Columns[cell.ColumnIndex].Width + ".");
                }
            MethodInfo resultCaption = typeof(ServerForm).GetMethod("ResultCaption", BindingFlags.Static | BindingFlags.NonPublic);
            var outcomes = new List<ProfileTestResult>();
            foreach (ProfileTestState state in Enum.GetValues(typeof(ProfileTestState))) outcomes.Add(new ProfileTestResult { State = state });
            outcomes.Add(new ProfileTestResult { ServerFinishReason = "capture_format_mismatch" });
            outcomes.Add(new ProfileTestResult { ServerFinishReason = "capture_format_unconfirmed" });
            outcomes.Add(new ProfileTestResult { State = ProfileTestState.Partial, Failure = FileBenchmarkFailure.MaximumRun });
            outcomes.Add(new ProfileTestResult { State = ProfileTestState.Partial, Failure = FileBenchmarkFailure.Stalled });
            outcomes.Add(new ProfileTestResult { State = ProfileTestState.Partial, ServerFinishReason = "receiver_report_timeout" });
            foreach (ProfileTestResult outcome in outcomes)
            {
                string caption = (string)resultCaption.Invoke(null, new object[] { outcome });
                if (outcome.State == ProfileTestState.Passed && caption != "Работает") throw new Exception("Passed must use the short requested caption.");
                if (TextRenderer.MeasureText(caption, grid.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width > grid.Columns["Status"].Width - 8)
                    throw new Exception("A production outcome caption is truncated at this DPI: " + caption);
            }
        }
        if (!(bool)typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(grid, null))
            throw new Exception("The resized results grid must retain buffered drawing.");
    }

    private static void CheckReconnectWarningLayout(ServerForm form, string path)
    {
        typeof(ServerForm).GetMethod("ShowFormatReconnectWarning", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form,
            new object[] { Find<ComboBox>(form, "CameraMode").SelectedItem, true });
        Render(form, 0, path);
        Label detail = Find<Label>(form, "SettingsDetail");
        Size needed = TextRenderer.MeasureText(detail.Text, detail.Font, new Size(detail.ClientSize.Width, Int32.MaxValue), TextFormatFlags.WordBreak);
        if (needed.Height > detail.ClientSize.Height)
            throw new Exception("The reconnect warning must remain fully readable at both DPIs: needed=" + needed + ", available=" + detail.ClientSize + ".");
    }

    private static TabPage AncestorTab(Control control)
    {
        for (Control current = control; current != null; current = current.Parent) if (current is TabPage) return (TabPage)current;
        return null;
    }
    private static T Find<T>(Control root, string name) where T : Control
    {
        if (root.Name == name && root is T) return (T)root;
        foreach (Control child in root.Controls) { T result = FindOrNull<T>(child, name); if (result != null) return result; }
        throw new Exception("Missing control: " + name);
    }
    private static T FindOrNull<T>(Control root, string name) where T : Control
    {
        if (root.Name == name && root is T) return (T)root;
        foreach (Control child in root.Controls) { T result = FindOrNull<T>(child, name); if (result != null) return result; }
        return null;
    }
    private static void LayoutTree(Control control) { control.PerformLayout(); foreach (Control child in control.Controls) LayoutTree(child); }
    private static void Collect(Control parent, List<Control> result) { foreach (Control child in parent.Controls) { result.Add(child); Collect(child, result); } }
}
