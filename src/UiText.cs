using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Windows.Forms;

namespace HorizonClient
{
    internal enum UiLanguage { Russian, English }

    // Interface language never changes wire IDs, device names, file contents or
    // the thread's culture. Settings I/O is explicit and uses an injected path.
    internal static class UiText
    {
        internal const string LanguageFileName = "ui.language";
        private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");
        private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
        private static readonly object gate = new object();
        private static readonly Dictionary<string, Translation> translations = new Dictionary<string, Translation>(StringComparer.Ordinal);
        private static readonly Dictionary<string, FormattedValue> recentMessages = new Dictionary<string, FormattedValue>(StringComparer.Ordinal);
        private static readonly Queue<string> recentOrder = new Queue<string>();
        private static readonly ConditionalWeakTable<Control, Binding> bindings = new ConditionalWeakTable<Control, Binding>();
        private static volatile UiLanguage language = UiLanguage.English;

        internal static UiLanguage Language { get { return language; } }
        internal static CultureInfo Culture { get { return language == UiLanguage.English ? EnglishCulture : RussianCulture; } }
        internal static string Id { get { return language == UiLanguage.English ? "en" : "ru"; } }

        internal static void SetLanguage(UiLanguage value)
        {
            if (value != UiLanguage.Russian && value != UiLanguage.English) throw new ArgumentOutOfRangeException("value");
            language = value;
        }

        internal static string Get(string russian, string english)
        {
            if (russian == null || english == null) throw new ArgumentNullException(russian == null ? "russian" : "english");
            if (!String.Equals(russian, english, StringComparison.Ordinal))
            {
                lock (gate)
                {
                    Translation existing;
                    if (!translations.TryGetValue(russian, out existing) || existing.Russian != russian || existing.English != english)
                    {
                        var pair = new Translation { Russian = russian, English = english };
                        translations[russian] = pair; translations[english] = pair;
                    }
                }
            }
            return language == UiLanguage.English ? english : russian;
        }

        internal static string Format(string russian, string english, params object[] arguments)
        {
            Get(russian, english);
            var value = new FormattedValue { Russian = russian, English = english, Arguments = arguments == null ? new object[0] : (object[])arguments.Clone() };
            string text = value.ToString();
            lock (gate)
            {
                if (!recentMessages.ContainsKey(text)) recentOrder.Enqueue(text);
                recentMessages[text] = value;
                // Only explicit localized messages are remembered. A bounded
                // hand-off lets existing string callbacks retain their template.
                while (recentMessages.Count > 512) recentMessages.Remove(recentOrder.Dequeue());
            }
            return text;
        }

        internal static object Value(string russian, string english)
        { Get(russian, english); return new LocalizedValue { Russian = russian, English = english }; }

        // Use only for software-generated captions, never device names or data.
        internal static object KnownValue(string message)
        {
            if (message == null) return String.Empty;
            lock (gate)
            {
                Translation pair;
                if (translations.TryGetValue(message, out pair)) return new LocalizedValue { Russian = pair.Russian, English = pair.English };
                FormattedValue value;
                if (recentMessages.TryGetValue(message, out value)) return value;
            }
            return message;
        }

        internal static void SetKnownText(Control control, string message)
        { SetText(control, "{0}", "{0}", KnownValue(message)); }

        // Dynamic labels keep the message template, rather than trying to parse
        // already formatted bytes/times/device names when the language changes.
        internal static void SetText(Control control, string russian, string english, params object[] arguments)
        {
            if (control == null) throw new ArgumentNullException("control");
            Get(russian, english);
            Binding binding = bindings.GetValue(control, delegate { return new Binding(); });
            binding.Russian = russian; binding.English = english;
            binding.Arguments = arguments == null ? new object[0] : (object[])arguments.Clone();
            ApplyBinding(control, binding);
        }

        private static void ApplyBinding(Control control, Binding binding)
        {
            string template = Get(binding.Russian, binding.English);
            control.Text = binding.Arguments.Length == 0 ? template : String.Format(Culture, template, binding.Arguments);
        }

        internal static string Translate(string text)
        {
            if (text == null) return null;
            lock (gate)
            {
                Translation pair;
                if (translations.TryGetValue(text, out pair)) return language == UiLanguage.English ? pair.English : pair.Russian;
            }
            return text;
        }

        internal static void Apply(Control root)
        {
            if (root == null || root.IsDisposed) return;
            Binding binding;
            if (bindings.TryGetValue(root, out binding)) ApplyBinding(root, binding);
            else if (!(root is TextBoxBase) && !(root is ComboBox) && !(root is ListBox)) root.Text = Translate(root.Text);
            DataGridView grid = root as DataGridView;
            if (grid != null)
                foreach (DataGridViewColumn column in grid.Columns)
                { column.HeaderText = Translate(column.HeaderText); column.ToolTipText = Translate(column.ToolTipText); }
            ToolStrip strip = root as ToolStrip;
            if (strip != null) Apply(strip.Items);
            foreach (Control child in root.Controls) Apply(child);
        }

        internal static void Apply(ToolStripItemCollection items)
        {
            if (items == null) return;
            foreach (ToolStripItem item in items)
            {
                item.Text = Translate(item.Text); item.ToolTipText = Translate(item.ToolTipText);
                ToolStripDropDownItem submenu = item as ToolStripDropDownItem;
                if (submenu != null) Apply(submenu.DropDownItems);
            }
        }

        internal static void Load(string path)
        {
            SetLanguage(UiLanguage.English);
            try
            {
                if (String.IsNullOrEmpty(path) || !File.Exists(path) || new FileInfo(path).Length > 16) return;
                string id = File.ReadAllText(path).Trim();
                if (String.Equals(id, "en", StringComparison.OrdinalIgnoreCase)) SetLanguage(UiLanguage.English);
                else if (String.Equals(id, "ru", StringComparison.OrdinalIgnoreCase)) SetLanguage(UiLanguage.Russian);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
        }

        internal static void Save(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("An absolute language setting path is required.", "path");
            string temporary = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, Id, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                temporary = null;
            }
            finally
            {
                if (temporary != null)
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (SecurityException) { }
            }
        }

        private sealed class Translation { internal string Russian, English; }
        private sealed class LocalizedValue
        {
            internal string Russian, English;
            public override string ToString() { return Get(Russian, English); }
        }
        private sealed class FormattedValue
        {
            internal string Russian, English;
            internal object[] Arguments;
            public override string ToString() { return String.Format(Culture, Get(Russian, English), Arguments); }
        }
        private sealed class Binding { internal string Russian, English; internal object[] Arguments; }
    }
}
