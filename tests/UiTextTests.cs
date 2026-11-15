using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

internal static class UiTextTests
{
    private static int checks;
    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "language-fixture-" + Guid.NewGuid().ToString("N"));
        CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture, originalUiCulture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Check(UiText.Language == UiLanguage.English && UiText.Id == "en" && UiText.Get("Настройки", "Settings") == "Settings", "Fresh server/client captions default to English without reading the user profile.");
            TextBindings(); KnownMessages(); Settings(root); WeakBindings();
            Check(Object.ReferenceEquals(originalCulture, Thread.CurrentThread.CurrentCulture) && Object.ReferenceEquals(originalUiCulture, Thread.CurrentThread.CurrentUICulture), "Language selection cannot change the process or wire-format culture.");
            Console.WriteLine("UI localization checks passed: " + checks + ". Hidden controls and isolated settings only; no device, clipboard or profile I/O.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            UiText.SetLanguage(UiLanguage.Russian);
            string parent = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), full = Path.GetFullPath(root);
            if (Path.GetDirectoryName(full).Equals(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("language-fixture-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, true);
        }
    }

    private static void TextBindings()
    {
        UiText.SetLanguage(UiLanguage.Russian);
        using (var root = new Panel())
        using (var name = new TextBox { Text = "Настройки" })
        using (var choices = new ComboBox { Text = "Settings" })
        using (var docs = new TextBox())
        using (var status = new Label())
        using (var caption = new Label { Text = UiText.Get("Настройки", "Settings") })
        using (var grid = new DataGridView())
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = UiText.Get("Результат", "Result"), ToolTipText = UiText.Get("Полученные байты", "Received bytes") });
            root.Controls.AddRange(new Control[] { name, choices, docs, status, caption, grid });
            UiText.SetText(docs, "Справка {камера}", "Help {camera}");
            UiText.SetText(status, "Камера «{0}»: {1:N0} байт.", "Camera “{0}”: {1:N0} bytes.", "Настройки {0}", 12345);
            Check(status.Text.Contains("Настройки {0}") && status.Text.EndsWith(" байт."), "Dynamic arguments are literal user data, including braces.");
            UiText.SetLanguage(UiLanguage.English); UiText.Apply(root);
            Check(caption.Text == "Settings" && docs.Text == "Help {camera}", "Static controls and bound documentation switch immediately.");
            Check(status.Text == "Camera “Настройки {0}”: 12,345 bytes.", "A bound status is reformatted from its template without translating device names.");
            Check(name.Text == "Настройки" && choices.Text == "Settings", "Editable names are never translated, even if they match a caption.");
            Check(grid.Columns[0].HeaderText == "Result" && grid.Columns[0].ToolTipText == "Received bytes", "Table headings and help are localized.");
            UiText.SetText(status, "Файл {0}: {1}", "File {0}: {1}", "Выход", UiText.Value("Готово", "Ready"));
            Check(status.Text == "File Выход: Ready", "Only explicitly semantic fragments are translated.");
            UiText.SetLanguage(UiLanguage.Russian); UiText.Apply(root);
            Check(status.Text == "Файл Выход: Готово" && caption.Text == "Настройки" && docs.Text == "Справка {камера}", "Switching back restores templates and live semantic values.");
            Check(grid.Columns[0].HeaderText == "Результат", "Table headings survive a full language round trip.");
        }
        using (var menu = new ContextMenuStrip())
        {
            var parent = new ToolStripMenuItem(UiText.Get("Настройки", "Settings"));
            parent.DropDownItems.Add(new ToolStripMenuItem(UiText.Get("Выход", "Exit")) { Enabled = false }); menu.Items.Add(parent);
            UiText.SetLanguage(UiLanguage.English); UiText.Apply(menu);
            Check(parent.Text == "Settings" && parent.DropDownItems[0].Text == "Exit" && !parent.DropDownItems[0].Enabled, "Nested static menu localization preserves behavior and disabled state.");
        }
    }

    private static void KnownMessages()
    {
        UiText.SetLanguage(UiLanguage.Russian);
        using (var label = new Label())
        {
            string phase = UiText.Get("Клиент отправляет", "The client is sending");
            string message = UiText.Format("{0}: {1:N0} из {2:N0} байт.", "{0}: {1:N0} of {2:N0} bytes.", UiText.KnownValue(phase), 128, 1024);
            UiText.SetKnownText(label, message);
            for (int index = 0; index < 2000; index++) UiText.Format("Проверка {0}", "Check {0}", index);
            object cache = typeof(UiText).GetField("recentMessages", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Check((int)cache.GetType().GetProperty("Count").GetValue(cache, null) <= 512, "Formatted callback hand-off storage is bounded during long transfers.");
            UiText.SetLanguage(UiLanguage.English); UiText.Apply(label);
            Check(label.Text == "The client is sending: 128 of 1,024 bytes.", "A current callback retains its semantic template after cache eviction.");
            UiText.SetKnownText(label, "Unknown device error {0}"); UiText.Apply(label);
            Check(label.Text == "Unknown device error {0}", "Unknown messages remain literal and cannot become format strings.");
            UiText.SetKnownText(label, UiText.Get("Готово", "Ready"));
            UiText.SetLanguage(UiLanguage.Russian); UiText.Apply(label);
            Check(label.Text == "Готово", "Explicit known static callbacks survive language changes.");
        }
    }

    private static void Settings(string root)
    {
        string path = Path.Combine(root, "settings", UiText.LanguageFileName);
        UiText.SetLanguage(UiLanguage.Russian); UiText.Load(path);
        Check(UiText.Language == UiLanguage.English && !Directory.Exists(root), "Absent setting defaults to English and creates no directories.");
        UiText.SetLanguage(UiLanguage.English); UiText.Save(path);
        Check(File.ReadAllText(path) == "en", "The selected language is saved as a stable identifier.");
        UiText.SetLanguage(UiLanguage.Russian); UiText.Load(path);
        Check(UiText.Language == UiLanguage.English, "English is restored before constructing controls.");
        UiText.SetLanguage(UiLanguage.Russian); UiText.Save(path); UiText.SetLanguage(UiLanguage.English); UiText.Load(path);
        Check(UiText.Language == UiLanguage.Russian && File.ReadAllText(path) == "ru", "An existing setting is replaced atomically with Russian.");
        File.WriteAllText(path, "unknown"); UiText.Load(path);
        Check(UiText.Language == UiLanguage.English && File.ReadAllText(path) == "unknown", "Invalid settings default to English without silently rewriting the file.");
        UiText.SetLanguage(UiLanguage.Russian); File.WriteAllText(path, new string('x', 4096)); UiText.Load(path);
        Check(UiText.Language == UiLanguage.English, "Oversized language settings default to English.");
        File.WriteAllText(path, " \r\nEN\r\n"); UiText.Load(path);
        Check(UiText.Language == UiLanguage.English, "A short language setting accepts surrounding whitespace and stable ID case.");
        File.WriteAllText(path, " \r\nRU\r\n"); UiText.Load(path);
        Check(UiText.Language == UiLanguage.Russian, "An explicit saved Russian choice overrides the new English default, with whitespace and stable ID case.");
        File.WriteAllText(path, "ru");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            UiText.SetLanguage(UiLanguage.Russian); UiText.Load(path);
            Check(UiText.Language == UiLanguage.English, "An unreadable preference defaults to English without requiring a write.");
        }
        UiText.Load(path);
        Check(UiText.Language == UiLanguage.Russian && File.ReadAllText(path) == "ru", "An unreadable Russian preference remains intact and restores on the next load.");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            UiText.SetLanguage(UiLanguage.English); UiText.Load(path);
            Check(UiText.Language == UiLanguage.Russian && File.ReadAllText(path) == "ru", "A readable saved Russian preference is respected even when it cannot be overwritten.");
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        foreach (string unavailable in new[] { null, "", root })
        {
            UiText.SetLanguage(UiLanguage.Russian); UiText.Load(unavailable);
            Check(UiText.Language == UiLanguage.English, "An absent or directory-valued preference falls back to English.");
        }
        string blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "unchanged");
        UiText.SetLanguage(UiLanguage.Russian); UiText.Load(Path.Combine(blocked, UiText.LanguageFileName));
        Check(UiText.Language == UiLanguage.English && File.ReadAllText(blocked) == "unchanged", "An unwritable preference store defaults to English without modifying its blocking file.");
        bool failed = false; try { UiText.Save(Path.Combine(blocked, UiText.LanguageFileName)); } catch (IOException) { failed = true; }
        Check(failed && File.ReadAllText(blocked) == "unchanged", "An unwritable store fails without inventing a new location.");
        Check(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length == 0, "Language persistence leaves no owned temporary files.");
        bool relativeRejected = false; try { UiText.Save("relative.language"); } catch (ArgumentException) { relativeRejected = true; }
        Check(relativeRejected, "Language settings never depend on the process working directory.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateWeakBinding()
    {
        var control = new Label(); UiText.SetText(control, "Количество {0}", "Count {0}", 7); control.Dispose();
        return new WeakReference(control);
    }

    private static void WeakBindings()
    {
        WeakReference weak = CreateWeakBinding();
        for (int attempt = 0; attempt < 3 && weak.IsAlive; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!weak.IsAlive, "Localization bindings cannot retain closed controls.");
    }

    private static void Check(bool value, string description) { if (!value) throw new Exception(description); checks++; }
}
