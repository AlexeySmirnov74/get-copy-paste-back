using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using HorizonClient;

internal static class ServerLanguageTests
{
    private static int checks, externalCalls;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            UiText.SetLanguage(UiLanguage.Russian);
            LiveSelectionPreservesTransferState();
            EnglishStartupAndDpi();
            Check(externalCalls == 0, "Changing language must not touch cameras, clipboard or sounds.");
            Console.WriteLine("Server language checks passed: " + checks + ". Hidden forms and synthetic choices only.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { UiText.SetLanguage(UiLanguage.Russian); }
    }

    private static ServerForm Create()
    {
        return new ServerForm(null, false, delegate { externalCalls++; throw new Exception("Unexpected camera access"); },
            new ServerClipboardServices {
                Sequence = delegate { externalCalls++; return 0; },
                Capture = delegate { externalCalls++; throw new Exception("Unexpected clipboard access"); },
                Accepted = delegate { externalCalls++; }
            }, delegate { externalCalls++; }, delegate { externalCalls++; }, delegate { externalCalls++; });
    }

    private static void LiveSelectionPreservesTransferState()
    {
        using (ServerForm form = Create())
        {
            Check(Find<ComboBox>(form, "LanguageChoice").SelectedIndex == 0, "An explicitly selected Russian language is reflected in the selector.");
            Check(Find<TabControl>(form, "ServerTabs").TabPages[0].Text == "Настройки", "Russian static text is present initially.");
            form.ApplyCameraPresence(true, "Настройки", false);
            var type = typeof(ServerForm);
            type.GetField("restoringSettings", Fields).SetValue(form, true);
            type.GetField("loadingOutputs", Fields).SetValue(form, true);
            ComboBox profiles = Find<ComboBox>(form, "VideoProfile"), modes = Find<ComboBox>(form, "CameraMode");
            ComboBox sizes = Find<ComboBox>(form, "BenchmarkSize"), limits = Find<ComboBox>(form, "BenchmarkLimit"), outputs = Find<ComboBox>(form, "AudioOutput");
            profiles.SelectedIndex = 4; modes.SelectedIndex = 1; sizes.SelectedIndex = 4; limits.SelectedIndex = 2;
            int opaqueOutputIndex = outputs.Items.Add(new AudioEndpoint("waveout:7", "Настройки")); outputs.SelectedIndex = opaqueOutputIndex;
            type.GetField("selectedAudioId", Fields).SetValue(form, "waveout:7");
            type.GetField("restoringSettings", Fields).SetValue(form, false);
            type.GetField("loadingOutputs", Fields).SetValue(form, false);
            Guid session = Guid.NewGuid(), transfer = Guid.NewGuid(); byte[] pending = { 11, 12, 13 };
            type.GetField("bridgeSession", Fields).SetValue(form, session);
            type.GetField("transferId", Fields).SetValue(form, transfer);
            type.GetField("current", Fields).SetValue(form, pending);
            object generation = type.GetField("generation", Fields).GetValue(form);
            CheckBox keepCamera = Find<CheckBox>(form, "KeepCameraRunning"); keepCamera.Checked = true;
            CheckBox copyStartSound = Find<CheckBox>(form, "AcceptedSoundEnabled"), remoteReadySound = Find<CheckBox>(form, "DeliveredSoundEnabled");
            copyStartSound.Checked = remoteReadySound.Checked = false;
            object source = modes.SelectedItem;
            int sizeIndex = sizes.SelectedIndex, limitIndex = limits.SelectedIndex;

            Find<ComboBox>(form, "LanguageChoice").SelectedIndex = 1;
            Check(UiText.Language == UiLanguage.English, "English is applied immediately by the language selector.");
            CheckEnglishPresentation(form);
            Check(keepCamera.Checked, "Changing language preserves the keep-camera preference.");
            Check(!copyStartSound.Checked && !remoteReadySound.Checked && copyStartSound.Text == "Copy start sound" && remoteReadySound.Text == "Remote clipboard ready sound", "English captions preserve both independent muted sound preferences.");
            Check(profiles.Items.Count == 5 && profiles.SelectedIndex == 4 && profiles.Text.Contains("error correction"), "English preserves the original selected profile alongside four color experiments.");
            Check(Object.ReferenceEquals(modes.SelectedItem, source) && modes.Text.Contains("without JPEG"), "The exact camera mode is preserved and redisplayed in English.");
            Check(sizes.SelectedIndex == sizeIndex && sizes.Text == "10 MB" && limits.SelectedIndex == limitIndex && limits.Text == "30 min", "File-size and deadline values survive translation.");
            Check(outputs.SelectedIndex == opaqueOutputIndex && outputs.Text == "Настройки" && (string)type.GetField("selectedAudioId", Fields).GetValue(form) == "waveout:7", "A device name identical to a Russian caption is not translated or reselected.");
            Check(Find<ComboBox>(form, "CameraNameDropDown").Text == "Настройки", "The camera's custom identity stays unchanged.");
            CheckDynamicCameraName(form, "Found camera: Настройки.");
            Check((Guid)type.GetField("bridgeSession", Fields).GetValue(form) == session && (Guid)type.GetField("transferId", Fields).GetValue(form) == transfer &&
                Object.ReferenceEquals(type.GetField("current", Fields).GetValue(form), pending) && Object.Equals(type.GetField("generation", Fields).GetValue(form), generation), "Language changes must preserve the session and pending transfer.");

            Find<ComboBox>(form, "LanguageChoice").SelectedIndex = 0;
            Check(profiles.Items.Count == 5 && profiles.SelectedIndex == 4 && profiles.Text.Contains("коррекция ошибок"), "Russian round-trip keeps selected profile metadata and color choices.");
            Check(sizes.Text == "10 МБ" && limits.Text == "30 мин" && modes.Text.Contains("без JPEG"), "Russian combo captions are restored immediately.");
            Check(Find<TextBox>(form, "DocumentationText").Text.StartsWith("ПЕРВАЯ НАСТРОЙКА", StringComparison.Ordinal), "Documentation switches back to Russian.");
            Check(keepCamera.Checked && keepCamera.Text == "Оставлять камеру работающей после выхода", "Russian round-trip restores the keep-camera caption without changing its value.");
            Check(!copyStartSound.Checked && !remoteReadySound.Checked && copyStartSound.Text == "Звук начала копирования" && remoteReadySound.Text == "Звук готовности буфера на удалённом ПК", "Russian round-trip restores sound captions without reenabling them.");
            CheckDynamicCameraName(form, "Найдена камера «Настройки».");
            Check((bool)type.GetField("restoringSettings", Fields).GetValue(form) == false && (bool)type.GetField("loadingOutputs", Fields).GetValue(form) == false, "Language refresh restores both side-effect guards.");
        }
    }

    private static void CheckDynamicCameraName(ServerForm form, string text)
    {
        Check(Find<Label>(form, "SettingsState").Text == text, "Dynamic camera text translates while preserving the opaque name.");
    }

    private static void EnglishStartupAndDpi()
    {
        UiText.SetLanguage(UiLanguage.Russian); UiText.Load(null);
        using (ServerForm form = Create())
        {
            CheckEnglishPresentation(form);
            Check(Find<ComboBox>(form, "LanguageChoice").SelectedIndex == 1, "Missing preferences default the server selector to English before showing the window.");
            Check(Find<ComboBox>(form, "AudioOutput").Text == "All audio outputs", "The fresh all-output default is translated at English startup.");
            IntPtr handle = form.Handle; IntPtr tabsHandle = Find<TabControl>(form, "ServerTabs").Handle;
            IntPtr cameraNameHandle = Find<ComboBox>(form, "CameraNameDropDown").Handle;
            form.ApplyCameraPresence(true, "Camera fixture", false);
            DataGridView grid = Find<DataGridView>(form, "BenchmarkResults");
            string[] profileNames = { "720p", "720p dense", "1080p robust", "640p", "MP4Modem V2" };
            for (int index = 0; index < 5; index++) grid.Rows.Add(profileNames[index], "Waiting for resolution", "1920×1080", "104 857 600", "104 857 600", "1800.00", "999.999 Mbit/s");
            Layout(form); CheckBounds(form);
            Snapshot(form, 0, "server-settings-en-ui.png");
            Snapshot(form, 1, "server-work-en-ui.png");
            Snapshot(form, 3, "server-documentation-en-ui.png");
            Find<TabControl>(form, "ServerTabs").SelectedIndex = 0;
            form.FitUiToWorkArea(new Rectangle(0, 0, 1024, 768)); Layout(form); CheckBounds(form);
            form.Scale(new SizeF(1.5F, 1.5F)); ScaleFonts(form); form.FitUiToWorkArea(new Rectangle(0, 0, 1344, 1040)); Layout(form); CheckBounds(form);
            Snapshot(form, 0, "server-settings-en-150-ui.png");
            form.FitUiToWorkArea(new Rectangle(0, 0, 1024, 768)); Layout(form); CheckBounds(form);
            Check(!form.Visible, "Language and DPI checks must never display a real form.");
        }
    }

    private static void CheckEnglishPresentation(ServerForm form)
    {
        Check(form.Text == "Get Copy-Paste Back — Server " + typeof(ServerForm).Assembly.GetName().Version.ToString() + " — local PC", "The branded versioned window title switches immediately.");
        Check(Find<CheckBox>(form, "KeepCameraRunning").Text == "Keep camera running after exit", "The keep-camera preference is translated at startup and on live language changes.");
        TabControl tabs = Find<TabControl>(form, "ServerTabs");
        Check(tabs.TabPages[0].Text == "Settings" && tabs.TabPages[1].Text == "Work" && tabs.TabPages[2].Text == "Transfer log" && tabs.TabPages[3].Text == "Documentation", "All tab captions translate.");
        Check(Find<Button>(form, "Benchmark").Text == "Test speed" && Find<Button>(form, "BenchmarkStop").Text == "Stop test" && Find<GroupBox>(form, "BenchmarkPanel").Text == "Speed test", "Static benchmark actions and border translate.");
        DataGridView grid = Find<DataGridView>(form, "BenchmarkResults");
        Check(grid.Columns["Status"].HeaderText == "Result" && grid.Columns["FrameSize"].HeaderText == "Client frame" && grid.Columns["FrameSize"].ToolTipText.StartsWith("Actual frame size", StringComparison.Ordinal), "Result headings and tooltip translate.");
        string instructions = Find<TextBox>(form, "DocumentationText").Text;
        Check(instructions.StartsWith("FIRST SETUP", StringComparison.Ordinal) && instructions.Contains("Server.exe") && instructions.Contains("Client.exe") && instructions.Contains("10 KB") && instructions.Contains("Ctrl+V"), "English instructions identify both renamed executables and cover the workflow.");
        Check(instructions.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length <= 350, "English instructions stay concise.");
    }

    private static void CheckBounds(ServerForm form)
    {
        Control languages = Find<ComboBox>(form, "LanguageChoice").Parent;
        GroupBox panel = Find<GroupBox>(form, "BenchmarkPanel");
        Check(languages.Bottom <= panel.Top, "The language row must not overlap the speed-test block.");
        DataGridView grid = Find<DataGridView>(form, "BenchmarkResults");
        int columnsWidth = 0;
        foreach (DataGridViewColumn column in grid.Columns) if (column.Visible) columnsWidth += column.Width;
        int borderHeight = grid.BorderStyle == BorderStyle.None ? 0 :
            2 * (grid.BorderStyle == BorderStyle.Fixed3D ? SystemInformation.Border3DSize.Height : SystemInformation.BorderSize.Height);
        int scrollHeight = columnsWidth > grid.ClientSize.Width ? SystemInformation.HorizontalScrollBarHeight : 0;
        int required = grid.ColumnHeadersHeight + grid.Rows[0].Height * 5 + borderHeight + scrollHeight;
        string metrics = " grid=" + grid.Bounds + ", client=" + grid.ClientSize + ", required=" + required +
            ", headers=" + grid.ColumnHeadersHeight + ", row=" + grid.Rows[0].Height + ", columns=" + columnsWidth +
            ", border=" + borderHeight + ", scrollbar=" + scrollHeight + ", font=" + grid.Font.SizeInPoints +
            ", panel=" + panel.Bounds + ", form=" + form.Size + ".";
        Check(grid.Height >= required && grid.Height <= required + 2, "Exactly five measured result rows fit in English at either DPI." + metrics);
        Check(grid.PointToScreen(new Point(0, grid.Height)).Y <= panel.PointToScreen(new Point(0, panel.ClientSize.Height - panel.Padding.Bottom)).Y, "Five result rows remain inside the group border." + metrics);
        foreach (Control control in languages.Controls)
            if (control is Label) Check(TextRenderer.MeasureText(control.Text, control.Font).Width <= control.Width, "The bilingual language heading fits.");
        foreach (DataGridViewRow row in grid.Rows)
            foreach (DataGridViewCell cell in row.Cells)
                Check(TextRenderer.MeasureText(Convert.ToString(cell.FormattedValue), grid.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width <= grid.Columns[cell.ColumnIndex].Width - 8, "English result values fit without truncation.");
        MethodInfo captionMethod = typeof(ServerForm).GetMethod("ResultCaption", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (ProfileTestState state in Enum.GetValues(typeof(ProfileTestState)))
        {
            string caption = (string)captionMethod.Invoke(null, new object[] { new ProfileTestResult { State = state } });
            Check(TextRenderer.MeasureText(caption, grid.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width <= grid.Columns["Status"].Width - 8, "Every production English status fits without truncation: " + caption);
        }
    }

    private static void Layout(Control root) { root.PerformLayout(); foreach (Control control in root.Controls) Layout(control); root.PerformLayout(); }
    private static void Snapshot(ServerForm form, int selectedTab, string name)
    {
        TabControl tabs = Find<TabControl>(form, "ServerTabs"); tabs.SelectedIndex = selectedTab; Layout(form);
        // Hidden TabPages are not painted by Form.DrawToBitmap. Draw their
        // controls explicitly without displaying a window or pumping Shown.
        using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(form.BackColor);
            SnapshotChildren(form, form, tabs.SelectedTab, bitmap);
            bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name), ImageFormat.Png);
        }
    }
    private static void SnapshotChildren(Control root, Control parent, TabPage selected, Bitmap bitmap)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is TabPage && control != selected || parent is DataGridView) continue;
            Point origin = Point.Empty;
            for (Control current = control; current != null && current != root; current = current.Parent) origin.Offset(current.Left, current.Top);
            Rectangle target = new Rectangle(origin, control.Size);
            if (target.Width > 0 && target.Height > 0 && new Rectangle(Point.Empty, bitmap.Size).Contains(target))
            {
                control.DrawToBitmap(bitmap, target);
                ComboBox combo = control as ComboBox;
                if (combo != null)
                    using (Graphics graphics = Graphics.FromImage(bitmap)) TextRenderer.DrawText(graphics, combo.Text, combo.Font,
                        new Rectangle(origin.X + 4, origin.Y + 2, combo.Width - 26, combo.Height - 4), combo.ForeColor, combo.BackColor,
                        TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            SnapshotChildren(root, control, selected, bitmap);
        }
    }
    private static void ScaleFonts(Control root)
    {
        foreach (Control child in root.Controls) ScaleFonts(child);
        root.Font = new Font(root.Font.FontFamily, root.Font.Size * 1.5F, root.Font.Style);
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
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
}
