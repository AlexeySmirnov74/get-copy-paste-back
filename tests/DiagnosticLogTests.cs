using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using HorizonClient;

internal static class DiagnosticLogTests
{
    private static int checks;
    public static int Main()
    {
        string root = Path.Combine(Environment.CurrentDirectory, "build", "diagnostic-log-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check(!DiagnosticLog.IsEnabled, "uninitialized logger disabled");
            DiagnosticLog.Write("no-op", "payload", new byte[100]); DiagnosticLog.Error("no-op-error", new IOException("fixture"));
            Check(!Directory.Exists(root), "no-op writes create no directories");
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "client.debug.log");
            Check(DiagnosticLog.Initialize(path), "initialize explicit path");
            Check(!DiagnosticLog.Initialize(Path.Combine(root, "second.log")), "second initialization rejected");
            var counters = new Dictionary<string, long> { { "crc", 3 }, { "header", 9 } };
            DiagnosticLog.Write("fixture.metadata", "unicode", "Камера", "profile", DayOfWeek.Wednesday, "counts", counters, "indices", new[] { 1, 3, 7 }, "payload", new byte[] { 80, 82, 73, 86, 65, 84, 69 }, "key", "PRIVATE-KEY-FIXTURE", "object", new HiddenContent(), "nan", Double.NaN, "event", "forged");
            counters["crc"] = 999;
            var manyFields = new object[60];
            for (int field = 0; field < 30; field++) { manyFields[field * 2] = "field" + field; manyFields[field * 2 + 1] = field; }
            DiagnosticLog.Write("fixture.full_report", manyFields);
            DiagnosticLog.Error("fixture.error", new IOException("Synthetic failure"), "frame_bytes", 6144);
            Check(DiagnosticLog.Shutdown(2000), "graceful drain completes");
            string text = File.ReadAllText(path, new UTF8Encoding(false, true));
            Check(text.Contains("Камера") && !text.Contains("PRIVATE-KEY-FIXTURE") && !text.Contains("PRIVATE-OBJECT-FIXTURE"), "UTF8 metadata and secret/content exclusion");
            var serializer = new JavaScriptSerializer(); long previous = 0; bool metadata = false, errorFound = false, actualPath = false, fullReport = false;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                Dictionary<string, object> record = serializer.Deserialize<Dictionary<string, object>>(line);
                long sequence = Convert.ToInt64(record["seq"]); Check(sequence > previous, "written sequence monotonic"); previous = sequence;
                Check(record.ContainsKey("utc") && record.ContainsKey("thread"), "timestamp/thread fields");
                if ((string)record["event"] == "logger.started") actualPath = (string)record["path"] == path;
                if ((string)record["event"] == "fixture.metadata")
                {
                    metadata = true; var snapshot = (Dictionary<string, object>)record["counts"];
                    Check(Convert.ToInt32(snapshot["crc"]) == 3, "numeric dictionary snapshotted before mutation");
                    Check((string)record["key"] == "[redacted]" && (string)record["payload"] == "[redacted]", "sensitive field names redacted");
                    Check(record["indices"] is System.Collections.IList && ((System.Collections.IList)record["indices"]).Count == 3, "bounded numeric array retained");
                }
                if ((string)record["event"] == "fixture.error") { errorFound = true; Check(record.ContainsKey("exception_type") && record.ContainsKey("exception_hresult"), "exception metadata retained"); }
                if ((string)record["event"] == "fixture.full_report") fullReport = record.ContainsKey("field29") && Convert.ToInt32(record["field29"]) == 29;
            }
            Check(metadata && errorFound, "queued metadata/errors flushed");
            Check(actualPath, "actual destination logged at startup");
            Check(fullReport, "full stale-report metadata is not truncated after24 fields");
            Check(!File.Exists(Path.Combine(root, "second.log")), "rejected initialization does not write another file");

            string rotated = Path.Combine(root, "rotation.log");
            Check(DiagnosticLog.Initialize(rotated, 1024, 3, 2048), "rotation initialization");
            for (int i = 0; i < 120; i++) DiagnosticLog.Write("rotation.item", "index", i, "padding", new string('x', 100));
            Check(DiagnosticLog.Shutdown(2000), "rotation drain bounded");
            Check(File.Exists(rotated) && File.Exists(rotated + ".1") && File.Exists(rotated + ".2") && !File.Exists(rotated + ".3"), "retained file count exact");
            foreach (string file in new[] { rotated, rotated + ".1", rotated + ".2" })
            {
                Check(new FileInfo(file).Length <= 1024, "rotated file bounded");
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8)) Check(serializer.DeserializeObject(line) != null, "rotation retains whole JSON records");
            }
            string blocked = Path.Combine(root, "directory-as-log"); Directory.CreateDirectory(blocked);
            DiagnosticLog.Initialize(blocked);
            var deadline = Stopwatch.StartNew(); while (DiagnosticLog.IsEnabled && deadline.ElapsedMilliseconds < 2000) Thread.Sleep(5);
            Check(!DiagnosticLog.IsEnabled, "unwritable destination disables logger without fallback");
            Check(DiagnosticLog.Shutdown(500), "failed worker shuts down");
            Check(Directory.GetFiles(root, "*.log", SearchOption.AllDirectories).Length == 2, "no alternate fallback log created");
            string saturated = Path.Combine(root, "saturated.log"); DiagnosticLog.Initialize(saturated, 32768, 2, 1);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 20000; i++) DiagnosticLog.Write("burst", "index", i);
            Check(watch.ElapsedMilliseconds < 3000, "bounded queue producers do not wait on file I/O");
            Check(DiagnosticLog.Shutdown(2000), "burst shutdown bounded");
            Check(!DiagnosticLog.IsEnabled, "shutdown disables logging");
            Console.WriteLine("Diagnostic log checks passed ({0} assertions; explicit workspace fixtures only, no cameras/endpoints/clipboard or profile fallback).", checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { DiagnosticLog.Shutdown(2000); }
    }
    private sealed class HiddenContent { public override string ToString() { return "PRIVATE-OBJECT-FIXTURE"; } }
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
}
