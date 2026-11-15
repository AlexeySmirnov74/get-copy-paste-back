using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace HorizonClient
{
    // Explicit portable destination only. Producers never perform filesystem I/O;
    // a bounded background worker owns serialization, rotation and the file handle.
    internal static class DiagnosticLog
    {
        private static Writer current;
        private static long sequence;
        private static readonly int ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
        internal static bool IsEnabled { get { Writer writer = Volatile.Read(ref current); return writer != null && writer.Accepting; } }

        internal static bool Initialize(string absolutePath, int maximumFileBytes = 16 * 1024 * 1024, int retainedFiles = 4, int queueCapacity = 512)
        {
            if (String.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathRooted(absolutePath) || Path.GetPathRoot(absolutePath).Length < 3 || String.IsNullOrEmpty(Path.GetFileName(absolutePath))) throw new ArgumentException("An absolute log file path is required.", "absolutePath");
            if (maximumFileBytes < 256 || retainedFiles < 1 || retainedFiles > 16 || queueCapacity < 1 || queueCapacity > 8192) throw new ArgumentOutOfRangeException("maximumFileBytes");
            var writer = new Writer(Path.GetFullPath(absolutePath), maximumFileBytes, retainedFiles, queueCapacity);
            if (Interlocked.CompareExchange(ref current, writer, null) != null) { writer.Discard(); return false; }
            try { writer.Start(); }
            catch { Interlocked.CompareExchange(ref current, null, writer); writer.Discard(); return false; }
            return true;
        }

        internal static void Write(string eventName, params object[] keyValues)
        {
            Writer writer = Volatile.Read(ref current);
            if (writer == null || !writer.Accepting) return;
            writer.Enqueue(MakeRecord(eventName, keyValues));
        }

        internal static void Error(string eventName, Exception error, params object[] keyValues)
        {
            Writer writer = Volatile.Read(ref current);
            if (writer == null || !writer.Accepting) return;
            Dictionary<string, object> record = MakeRecord(eventName, keyValues);
            if (error != null)
            {
                record["exception_type"] = error.GetType().FullName;
                record["exception_hresult"] = "0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture);
                record["exception_message"] = Limit(error.Message, 1024);
                record["exception_stack"] = Limit(error.StackTrace, 4096);
                if (error.InnerException != null)
                {
                    record["inner_exception_type"] = error.InnerException.GetType().FullName;
                    record["inner_exception_hresult"] = "0x" + error.InnerException.HResult.ToString("X8", CultureInfo.InvariantCulture);
                    record["inner_exception_message"] = Limit(error.InnerException.Message, 1024);
                }
            }
            writer.Enqueue(record);
        }

        internal static bool Shutdown(int waitMilliseconds = 1500)
        {
            if (waitMilliseconds < 0 || waitMilliseconds > 10000) throw new ArgumentOutOfRangeException("waitMilliseconds");
            Writer writer = Interlocked.Exchange(ref current, null);
            return writer == null || writer.Stop(waitMilliseconds);
        }

        private static Dictionary<string, object> MakeRecord(string name, object[] values)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "seq", 0L },
                { "thread", Thread.CurrentThread.ManagedThreadId }, { "process_id", ProcessId }, { "event", Limit(name ?? "unknown", 160) }
            };
            if (values != null)
                for (int index = 0; index + 1 < values.Length && index < 128; index += 2)
                {
                    string key = values[index] as string;
                    if (String.IsNullOrEmpty(key) || key == "utc" || key == "seq" || key == "thread" || key == "event" || key == "process_id") continue;
                    key = Limit(key, 80);
                    string lower = key.ToLowerInvariant();
                    result[key] = lower == "key" || lower == "secret" || lower == "password" || lower == "token" || lower == "payload" || lower == "clipboard_text" || lower == "file_contents" ? "[redacted]" : Scalar(values[index + 1]);
                }
            return result;
        }

        private static object Scalar(object value)
        {
            if (value == null || value is bool || value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is decimal) return value;
            if (value is float) { float number = (float)value; return Single.IsNaN(number) || Single.IsInfinity(number) ? null : value; }
            if (value is double) { double number = (double)value; return Double.IsNaN(number) || Double.IsInfinity(number) ? null : value; }
            if (value is string) return Limit((string)value, 1024);
            if (value is Guid || value is Enum) return value.ToString();
            if (value is DateTime) return ((DateTime)value).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            IDictionary<string, long> longs = value as IDictionary<string, long>;
            if (longs != null) return NumberDictionary(longs);
            IDictionary<string, int> integers = value as IDictionary<string, int>;
            if (integers != null) return NumberDictionary(integers);
            if (value is int[] || value is long[] || value is double[] || value is bool[] || value is Guid[])
            {
                Array array = (Array)value; var copy = new object[Math.Min(array.Length, 64)];
                for (int index = 0; index < copy.Length; index++) copy[index] = Scalar(array.GetValue(index));
                return copy;
            }
            // Byte arrays, payload objects and arbitrary ToString overrides must
            // never turn clipboard/file contents into a debug record.
            return "[" + value.GetType().Name + "]";
        }

        private static Dictionary<string, object> NumberDictionary<T>(IDictionary<string, T> values)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                foreach (KeyValuePair<string, T> pair in values)
                {
                    if (result.Count >= 64) break;
                    if (pair.Key != null) result[Limit(pair.Key, 80)] = pair.Value;
                }
            }
            catch (InvalidOperationException) { result["snapshot_interrupted"] = true; }
            return result;
        }

        private static string Limit(string text, int count) { return text == null || text.Length <= count ? text : text.Substring(0, count); }

        private sealed class Writer
        {
            private readonly string path;
            private readonly int maximum, retained, capacity;
            private readonly object gate = new object();
            private readonly Queue<Dictionary<string, object>> queue = new Queue<Dictionary<string, object>>();
            private readonly AutoResetEvent wake = new AutoResetEvent(false);
            private readonly Thread thread;
            private volatile bool accepting = true;
            private volatile bool stopping;
            private bool completed;
            private long dropped;
            internal bool Accepting { get { return accepting; } }
            internal Writer(string path, int maximum, int retained, int capacity)
            {
                this.path = path; this.maximum = maximum; this.retained = retained; this.capacity = capacity;
                thread = new Thread(Run) { IsBackground = true, Name = "Portable diagnostic log" };
            }
            internal void Start() { thread.Start(); }
            internal void Discard() { accepting = false; wake.Dispose(); }
            internal void Enqueue(Dictionary<string, object> record)
            {
                if (!Monitor.TryEnter(gate)) { Interlocked.Increment(ref dropped); return; }
                try
                {
                    if (!accepting) return;
                    if (queue.Count >= capacity) { Interlocked.Increment(ref dropped); return; }
                    queue.Enqueue(record); wake.Set();
                }
                finally { Monitor.Exit(gate); }
            }
            internal bool Stop(int milliseconds)
            {
                lock (gate) { if (completed) return true; accepting = false; stopping = true; wake.Set(); }
                return Thread.CurrentThread != thread && thread.Join(milliseconds);
            }
            private void Run()
            {
                FileStream stream = null;
                try
                {
                    var serializer = new JavaScriptSerializer();
                    stream = Open();
                    WriteRecord(ref stream, serializer, MakeRecord("logger.started", new object[] { "path", path, "maximum_file_bytes", maximum, "retained_files", retained, "queue_capacity", capacity }));
                    while (true)
                    {
                        Dictionary<string, object> record = null;
                        lock (gate) { if (queue.Count != 0) record = queue.Dequeue(); else if (stopping) break; }
                        if (record == null) { stream.Flush(); wake.WaitOne(250); continue; }
                        long lost = Interlocked.Exchange(ref dropped, 0); if (lost != 0) record["dropped_before"] = lost;
                        WriteRecord(ref stream, serializer, record);
                    }
                    WriteRecord(ref stream, serializer, MakeRecord("logger.stopped", new object[] { "dropped_before", Interlocked.Exchange(ref dropped, 0) }));
                    stream.Flush();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (SecurityException) { }
                catch (Exception) { /* Diagnostics cannot terminate the application. */ }
                finally
                {
                    accepting = false;
                    if (stream != null) { try { stream.Dispose(); } catch { } }
                    lock (gate) { queue.Clear(); completed = true; wake.Dispose(); }
                }
            }
            private FileStream Open() { return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read); }
            private void WriteRecord(ref FileStream stream, JavaScriptSerializer serializer, Dictionary<string, object> record)
            {
                record["seq"] = Interlocked.Increment(ref sequence);
                byte[] bytes = Encoding.UTF8.GetBytes(serializer.Serialize(record) + Environment.NewLine);
                if (stream.Length > 0 && stream.Length + bytes.Length > maximum)
                {
                    stream.Dispose(); stream = null;
                    string oldest = path + "." + (retained - 1).ToString(CultureInfo.InvariantCulture);
                    if (retained > 1 && File.Exists(oldest)) File.Delete(oldest);
                    for (int index = retained - 2; index >= 1; index--)
                    {
                        string source = path + "." + index.ToString(CultureInfo.InvariantCulture);
                        if (File.Exists(source)) File.Move(source, path + "." + (index + 1).ToString(CultureInfo.InvariantCulture));
                    }
                    if (retained > 1) File.Move(path, path + ".1"); else File.Delete(path);
                    stream = Open();
                }
                stream.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
