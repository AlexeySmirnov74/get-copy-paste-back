using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;
using HorizonClient;

internal static class ServerTopRowTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks, externalCalls;

    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            foreach (UiLanguage language in new[] { UiLanguage.Russian, UiLanguage.English }) CheckLanguage(language);
            CheckReadabilityMatrix();
            Check(externalCalls == 0, "Hidden layout must not access cameras, audio, clipboard or notifications.");
            Console.WriteLine("Server top-row checks passed: " + checks + ". RU/EN,100/150percent,1024wide; hidden synthetic UI only."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { UiText.SetLanguage(UiLanguage.Russian); }
    }

    private static void CheckLanguage(UiLanguage language)
    {
        UiText.SetLanguage(language);
        using (var form = new ServerForm(null, false,
            delegate { externalCalls++; throw new Exception("Unexpected camera access"); },
            new ServerClipboardServices {
                Sequence = delegate { externalCalls++; return 0; }, Capture = delegate { externalCalls++; throw new Exception("Unexpected clipboard access"); }, Accepted = delegate { externalCalls++; }
            }, delegate { externalCalls++; }, delegate { externalCalls++; }, delegate { externalCalls++; }))
        {
            IntPtr formHandle = form.Handle, tabsHandle = Find<TabControl>(form, "ServerTabs").Handle;
            IntPtr cameraHandle = Find<ComboBox>(form, "CameraNameDropDown").Handle;
            var nameEditor = Find<ComboBox>(form, "CameraNameDropDown");
            form.ApplyCameraPresence(true, true, "Elgato Facecam MK.2", false, false);
            Check(nameEditor.Text == "Elgato Facecam MK.2", "The detected camera name is shown unchanged.");
            Console.WriteLine("Camera name after discovery: selection=" + nameEditor.SelectionStart + "/" + nameEditor.SelectionLength + ", enabled=" + nameEditor.Enabled + ", handle=" + nameEditor.IsHandleCreated);
            Check(nameEditor.SelectionLength == 0, "An existing camera name must not be covered by a text selection.");
            typeof(Control).GetMethod("RecreateHandle", Hidden).Invoke(nameEditor, null);
            Check(!nameEditor.Enabled && nameEditor.SelectionLength == 0, "Recreating the native editor cannot select an unavailable existing-camera name.");
            nameEditor.SelectAll();
            typeof(Control).GetMethod("OnGotFocus", Hidden).Invoke(nameEditor, new object[] { EventArgs.Empty });
            Check(nameEditor.SelectionLength == 0, "A delayed native focus event cannot leave an unavailable camera name highlighted.");
            typeof(ServerForm).GetMethod("ApplyServerLanguage", Hidden).Invoke(form, null);
            Check(nameEditor.SelectionLength == 0, "Refreshing language leaves the unavailable camera name readable.");
            nameEditor.Select(0, nameEditor.Text.Length);
            form.ApplyCameraPresence(true, true, "Logitech C920", false, false);
            Check(nameEditor.SelectionLength == 0, "Refreshing a camera name removes stale selection.");
            form.ApplyCameraPresence(false, false, null, false, false);
            nameEditor.Text = "Custom camera";
            nameEditor.Select(0, 6);
            Check(nameEditor.Text == "Custom camera" && nameEditor.SelectionLength == 6, "Manual camera-name editing still allows deliberate text selection.");
            typeof(ServerForm).GetMethod("SetConfigurationEnabled", Hidden).Invoke(form, new object[] { true });
            Check(nameEditor.SelectionStart == 0 && nameEditor.SelectionLength == 6, "Refreshing available configuration preserves deliberate editable text selection.");
            typeof(Control).GetMethod("OnGotFocus", Hidden).Invoke(nameEditor, new object[] { EventArgs.Empty });
            typeof(ServerForm).GetMethod("ApplyServerLanguage", Hidden).Invoke(form, null);
            Check(nameEditor.SelectionLength == 6, "Focus and language updates preserve deliberate selection while the camera name remains editable.");
            typeof(ComboBox).GetMethod("OnDropDownClosed", Hidden).Invoke(nameEditor, new object[] { EventArgs.Empty });
            Check(nameEditor.Text == "Custom camera" && nameEditor.SelectionLength == 0, "Closing the camera picker leaves the chosen name readable.");
            string id = language == UiLanguage.English ? "en" : "ru";
            CheckLifecycleCaptions(form); Snapshot(form, "server-toprow-" + id + "-100.png");
            form.FitUiToWorkArea(new Rectangle(0, 0, 1024, 768));
            CheckLifecycleCaptions(form); Snapshot(form, "server-toprow-" + id + "-1024.png");
            Check(form.Width <= 1024 && form.Height <= 768, "The compact window fits1024x768.");
            form.Scale(new SizeF(1.5F, 1.5F)); ScaleFonts(form); form.FitUiToWorkArea(new Rectangle(0, 0, 1344, 1040));
            CheckLifecycleCaptions(form); Snapshot(form, "server-toprow-" + id + "-150.png");
            Check(form.Width <= 1344 && form.Height <= 1040 && !form.Visible, "150percent rendering stays hidden and inside the work area.");
        }
    }

    private static void CheckReadabilityMatrix()
    {
        var failures = new System.Collections.Generic.List<string>();
        foreach (UiLanguage language in new[] { UiLanguage.Russian, UiLanguage.English })
            foreach (int scale in new[] { 100, 150 })
                foreach (int width in scale == 150 ? new[] { 1024, 1344 } : new[] { 1024 })
                {
                    UiText.SetLanguage(language);
                    using (var form = new ServerForm(null, false,
                        delegate { externalCalls++; throw new Exception("Unexpected camera access"); },
                        new ServerClipboardServices { Sequence = delegate { externalCalls++; return 0; }, Capture = delegate { externalCalls++; throw new Exception("Unexpected clipboard access"); }, Accepted = delegate { externalCalls++; } },
                        delegate { externalCalls++; }, delegate { externalCalls++; }, delegate { externalCalls++; }))
                    {
                        int shown = 0; form.Shown += delegate { shown++; };
                        IntPtr handle = form.Handle; handle = Find<TabControl>(form, "ServerTabs").Handle;
                        string name = "Camera " + new string('Ж', 93);
                        form.ApplyCameraPresence(true, true, name, false, false);
                        var grid = Find<DataGridView>(form, "BenchmarkResults");
                        handle = grid.Handle;
                        for (int row = 0; row < 5; row++) grid.Rows.Add("1080p устойчивый", "Нет подтверждения разрешения", "1920×1080 ↓", "104 857 600", "104 857 600", "1800,00", "999,999 Мбит/с");
                        if (scale == 150) { form.Scale(new SizeF(1.5F, 1.5F)); ScaleFonts(form); }
                        form.FitUiToWorkArea(new Rectangle(0, 0, width, width == 1024 ? 768 : 1040));
                        Layout(form); Layout(form);
                        string context = language + " " + width + "@" + scale;
                        CheckGeometry(form, name);
                        CheckPreferenceRows(form, context, failures);
                        foreach (string id in new[] { "CreateCamera", "DeleteCamera", "Benchmark", "BenchmarkStop" })
                            ButtonReadable(Find<Button>(form, id), context, failures);
                        foreach (string id in new[] { "SettingsState", "SettingsDetail" }) Readable(Find<Label>(form, id), context, failures);
                        var group = Find<GroupBox>(form, "BenchmarkPanel");
                        Reachable(group, context + " benchmark", failures);
                        int totalWidth = 0;
                        foreach (DataGridViewColumn column in grid.Columns) totalWidth += column.Width;
                        int scrollHeight = totalWidth > grid.ClientSize.Width ? SystemInformation.HorizontalScrollBarHeight : 0;
                        int rowsHeight = 0; foreach (DataGridViewRow row in grid.Rows) rowsHeight += row.Height;
                        if (grid.Height < grid.ColumnHeadersHeight + rowsHeight + 2 + scrollHeight) failures.Add(context + " fifth result row is covered by the horizontal scrollbar.");
                        foreach (DataGridViewRow row in grid.Rows) foreach (DataGridViewCell cell in row.Cells)
                        {
                            int need = TextRenderer.MeasureText(Convert.ToString(cell.Value), grid.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + 8;
                            if (grid.Columns[cell.ColumnIndex].Width < need) { failures.Add(context + " unreadable result column " + grid.Columns[cell.ColumnIndex].Name); break; }
                        }
                        if (totalWidth > grid.ClientSize.Width)
                        {
                            grid.HorizontalScrollingOffset = totalWidth;
                            if (grid.HorizontalScrollingOffset <= 0) failures.Add(context + " result columns have no horizontal scroll path.");
                            grid.HorizontalScrollingOffset = 0;
                        }
                        var settingsScroll = Find<Panel>(form, "SettingsScroll");
                        settingsScroll.ScrollControlIntoView(Find<TableLayoutPanel>(form, "SettingsTopRow"));
                        Check(settingsScroll.AutoScrollPosition.Y >= -Find<TableLayoutPanel>(form, "SettingsTopRow").Top, "The Settings top row is reachable after scrolling results: " + settingsScroll.AutoScrollPosition);
                        Console.WriteLine(context + " top scroll=" + settingsScroll.AutoScrollPosition);
                        Snapshot(form, "readable-" + language + "-" + width + "-" + scale + "-settings.png");
                        settingsScroll.ScrollControlIntoView(group); Layout(form);
                        Snapshot(form, "readable-" + language + "-" + width + "-" + scale + "-bottom.png");
                        grid.HorizontalScrollingOffset = totalWidth;
                        Snapshot(form, "readable-" + language + "-" + width + "-" + scale + "-right.png");
                        grid.HorizontalScrollingOffset = 0; settingsScroll.AutoScrollPosition = Point.Empty;
                        Find<TabControl>(form, "ServerTabs").SelectedIndex = 1;
                        var state = (Label)typeof(ServerForm).GetField("StateLabel", Hidden).GetValue(form);
                        var detail = (Label)typeof(ServerForm).GetField("DetailLabel", Hidden).GetValue(form);
                        var audio = Find<Label>(form, "AudioStatus");
                        audio.Text = "Audio initialization failed: " + new string('X', 200);
                        Layout(form); Layout(form);
                        ButtonReadable(Find<Button>(form, "ApplyVideoMode"), context, failures);
                        Readable(state, context + " Work state", failures); Readable(detail, context + " Work detail", failures); Readable(audio, context, failures);
                        Reachable(Find<PictureBox>(form, "TransmissionPreview"), context + " preview", failures);
                        Snapshot(form, "readable-" + language + "-" + width + "-" + scale + "-work.png");
                        Check(Find<PictureBox>(form, "TransmissionPreview").Height >= 120, "Wrapped Work text retains a useful preview area.");
                        Find<TabControl>(form, "ServerTabs").SelectedIndex = 2; Layout(form); Layout(form);
                        ButtonReadable(Find<Button>(form, "ClearTransferLog"), context, failures);
                        Check(shown == 0 && !form.Visible, "Readability checks never show the form.");
                    }
                }
        if (failures.Count != 0) throw new Exception(String.Join(Environment.NewLine, failures.ToArray()));
    }

    private static void ButtonReadable(Button button, string context, System.Collections.Generic.List<string> failures)
    {
        // Measure real font glyphs against the themed content rectangle so a
        // caption fitting the outer bounds cannot conceal a clipped border.
        Size glyphs = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        Rectangle content = button.ClientRectangle;
        using (var bitmap = new Bitmap(Math.Max(1, button.Width), Math.Max(1, button.Height)))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            if (Application.RenderWithVisualStyles && VisualStyleRenderer.IsSupported)
                content = new VisualStyleRenderer(button.Enabled ? VisualStyleElement.Button.PushButton.Normal : VisualStyleElement.Button.PushButton.Disabled)
                    .GetBackgroundContentRectangle(graphics, button.ClientRectangle);
            else content.Inflate(-SystemInformation.Border3DSize.Width, -SystemInformation.Border3DSize.Height);
        }
        content = Rectangle.FromLTRB(content.Left + button.Padding.Left, content.Top + button.Padding.Top,
            content.Right - button.Padding.Right, content.Bottom - button.Padding.Bottom);
        Console.WriteLine(context + " " + button.Name + " glyphs=" + glyphs + " content=" + content + " bounds=" + button.Bounds);
        if (glyphs.Width > content.Width || glyphs.Height > content.Height)
            failures.Add(context + " clipped button glyphs/border " + button.Name + ": glyphs=" + glyphs + ", content=" + content + ", bounds=" + button.Bounds);
        for (Control current = button; current.Parent != null && !(current.Parent is TabPage); current = current.Parent)
        {
            Panel scroll = current.Parent as Panel;
            if (scroll != null && scroll.AutoScroll) break;
            if (!current.Parent.ClientRectangle.Contains(current.Bounds))
                failures.Add(context + " button sizing chain clips " + current.Name + " at " + current.Parent.Name);
        }
        Reachable(button, context + " " + button.Name, failures);
    }

    private static void CheckPreferenceRows(ServerForm form, string context, System.Collections.Generic.List<string> failures)
    {
        TableLayoutPanel settings = Find<TableLayoutPanel>(form, "SettingsLayout");
        Control previous = Find<TableLayoutPanel>(form, "AudioRetryOptions");
        foreach (string id in new[] { "KeepCameraRunning", "AcceptedSoundEnabled", "DeliveredSoundEnabled", "ColorModesEnabled", "DetailedSystemLog" })
        {
            CheckBox preference = Find<CheckBox>(form, id);
            if (preference.Parent != settings || settings.GetRow(previous) >= settings.GetRow(preference) || previous.Bottom > preference.Top)
                failures.Add(context + " overlapping settings preference " + id);
            Size text = TextRenderer.MeasureText(preference.Text, preference.Font, Size.Empty, TextFormatFlags.SingleLine);
            if (text.Width + SystemInformation.MenuCheckSize.Width > preference.ClientSize.Width || text.Height > preference.ClientSize.Height)
                failures.Add(context + " clipped settings preference " + id + ": text=" + text + ", bounds=" + preference.Bounds);
            Check(preference.Checked == (id == "KeepCameraRunning" || id == "AcceptedSoundEnabled" || id == "DeliveredSoundEnabled"), "Preference defaults preserve the camera and enable copy/delivery sounds: " + id);
            Reachable(preference, context + " " + id, failures);
            previous = preference;
        }
        if (previous.Bottom > Find<GroupBox>(form, "BenchmarkPanel").Top)
            failures.Add(context + " settings preferences overlap the benchmark group.");
    }

    private static void Readable(Label label, string context, System.Collections.Generic.List<string> failures)
    {
        Size needed = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.ClientSize.Width, Int32.MaxValue), TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        Console.WriteLine(context + " " + label.Name + " text=" + label.Text.Length + " needed=" + needed.Height + " actual=" + label.Height);
        if (needed.Height > label.ClientSize.Height) failures.Add(context + " clipped " + label.Name + ": needs " + needed.Height + ", receives " + label.Height);
        Reachable(label, context + " " + label.Name, failures);
    }

    private static void Reachable(Control control, string context, System.Collections.Generic.List<string> failures)
    {
        Panel scroll = null;
        for (Control parent = control.Parent; parent != null && !(parent is TabPage); parent = parent.Parent)
            if (parent is Panel && ((Panel)parent).AutoScroll) { scroll = (Panel)parent; break; }
        TabPage tab = null; for (Control parent = control.Parent; parent != null; parent = parent.Parent) if (parent is TabPage) { tab = (TabPage)parent; break; }
        if (scroll != null) { IntPtr handle = scroll.Handle; scroll.ScrollControlIntoView(control); Layout(scroll); }
        Control viewport = scroll == null ? (Control)tab : scroll;
        Point top = Point.Empty;
        for (Control current = control; current != viewport && current != null; current = current.Parent) top.Offset(current.Left, current.Top);
        Rectangle bounds = new Rectangle(top, control.Size);
        if (!viewport.ClientRectangle.Contains(bounds)) failures.Add(context + " content has no complete scroll path: " + bounds + " viewport=" + viewport.ClientRectangle);
        if (scroll != null) scroll.AutoScrollPosition = Point.Empty;
    }

    private static void CheckLifecycleCaptions(ServerForm form)
    {
        string name = "Камера 日本語 — " + new string('Ж', 87);
        Check(name.Length == 100, "The long-name fixture exercises the complete supported100characters.");
        form.ApplyCameraPresence(false, null, false); Find<ComboBox>(form, "CameraNameDropDown").Text = name;
        CheckGeometry(form, name);
        form.ApplyCameraPresence(true, false, name, false); CheckGeometry(form, name);
        form.ApplyCameraPresence(true, true, name, false, true); CheckGeometry(form, name);
        typeof(ServerForm).GetField("cameraLookupKnown", Hidden).SetValue(form, false);
        typeof(ServerForm).GetMethod("SetConfigurationEnabled", Hidden).Invoke(form, new object[] { true });
        CheckGeometry(form, name);
        form.ApplyCameraPresence(false, null, false); Find<ComboBox>(form, "CameraNameDropDown").Text = name;
        Layout(form);
    }

    private static void CheckGeometry(ServerForm form, string expectedName)
    {
        Layout(form);
        TableLayoutPanel row = Find<TableLayoutPanel>(form, "SettingsTopRow");
        string[] order = { "SettingsLanguageLabel", "LanguageChoice", "SettingsCameraLabel", "CameraNameDropDown", "CreateCamera", "DeleteCamera" };
        Control previous = null;
        foreach (string name in order)
        {
            Control control = Find<Control>(form, name);
            Check(control.Parent == row && row.GetPositionFromControl(control).Row == 0, "Every requested control shares the one top row: " + name);
            Check(row.ClientRectangle.Contains(control.Bounds), "The complete top-row control stays visible: " + name);
            Check(previous == null || previous.Right <= control.Left, "Controls remain in language/camera/create/delete order without overlap: " + name);
            if (control is Button || control is Label)
                Check(TextRenderer.MeasureText(control.Text, control.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width <= control.Width - (control is Button ? 10 : 0), "Captions fit without clipping in either language: " + control.Text);
            previous = control;
        }
        ComboBox editor = Find<ComboBox>(form, "CameraNameDropDown");
        Check(editor.Text == expectedName && editor.MaxLength == 100 && editor.DropDownStyle == ComboBoxStyle.DropDown, "Visual clipping must preserve the entire editable camera identity: actual=" + editor.Text + ", expected=" + expectedName + ", max=" + editor.MaxLength + ", style=" + editor.DropDownStyle);
        Check(editor.Width >= TextRenderer.MeasureText("Camera name", editor.Font).Width + 25, "The flexible name field remains useful on the narrow and scaled layouts.");
        Check(Find<ComboBox>(form, "LanguageChoice").Items.Count == 2 && row.Bottom <= Find<Label>(form, "SettingsState").Top, "Language is at the top before status information.");
        Check(row.Bottom <= Find<GroupBox>(form, "BenchmarkPanel").Top, "The shared top row remains outside the benchmark group.");
        Check(form.ClientSize.Height <= 1000, "The layout retains the compact default height and its150percent equivalent.");
    }

    private static void Snapshot(ServerForm form, string name)
    {
        Layout(form);
        using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(form.BackColor);
            DrawChildren(form, form, Find<TabControl>(form, "ServerTabs").SelectedTab, bitmap);
            bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name), ImageFormat.Png);
        }
    }
    private static void DrawChildren(Control root, Control parent, TabPage selected, Bitmap bitmap)
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
            DrawChildren(root, control, selected, bitmap);
        }
    }
    private static void Layout(Control root) { root.PerformLayout(); foreach (Control child in root.Controls) Layout(child); root.PerformLayout(); }
    private static void ScaleFonts(Control root) { foreach (Control child in root.Controls) ScaleFonts(child); root.Font = new Font(root.Font.FontFamily, root.Font.Size * 1.5F, root.Font.Style); }
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
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); checks++; }
}
