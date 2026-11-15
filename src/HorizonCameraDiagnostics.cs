using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace HorizonClient
{
    // Reads a small, recent part of Horizon's own diagnostics after a streaming
    // access denial. Never opens a camera or changes vendor files/settings.
    internal static class HorizonCameraDiagnostics
    {
        internal const int MaximumTailBytes = 256 * 1024;
        internal const int MaximumLines = 80;
        internal const int MaximumLineCharacters = 512;
        private const int MaximumFiles = 6, MaximumDirectoryEntries = 2048, MaximumLinesPerFile = 32;
        private const int MaximumCollectionMilliseconds = 5000;
        private const int MaximumInputLines = 4096, ContextBefore = 3, ContextAfter = 8;
        private static int queued;
        private static readonly Stopwatch CaptureClock = Stopwatch.StartNew();
        private static readonly SnapshotSchedule Schedule = new SnapshotSchedule();
        private static readonly Regex Timestamp = Pattern(@"\b20\d\d-\d\d-\d\d[T ]\d\d:\d\d:\d\d(?:[.,]\d{1,7})?(?:Z|[+-]\d\d:\d\d)?");
        private static readonly Regex Component = Pattern(@"rtav|webcam|videoinput|virtual[ _-]?camera");
        private static readonly Regex Action = Pattern(@"error|fail|denied|start|stop|abort|0x80070005");
        private static readonly Regex TrustedRtavAction = Pattern(@"error|fail|denied|start|stop|abort|format|resource|resolution|0x[0-9a-f]+");
        private static readonly Regex PrivateContent = Pattern(@"clipboard|payload|password|passwd|authorization|bearer|cookie|\btoken\b|\bsecret\b|\b(?:keys?|key_file|keyfile|private[_-]?key|encryption[_-]?key)\b");
        private static readonly Regex FormatContext = Pattern(@"\b(?:width|height|resolution|fps|framerate|frame[_ -]?rate|pixel[_ -]?format|pixelformat|codec|subtype|media[_ -]?type|format|stride|bit[_ -]?rate|bitrate|h264|h265|mjpeg|mjpg|yuy2|nv12|rgb24|rgb32|resw|resh|imagewidth|imageheight|deviceindex|deviceid|preferences?|prefs|version|build|fourcc|compression|frameinterval|avgtimeperframe|colorspace|bitdepth)\b|\b[a-z_]*(?:width|height|fps|codec|framerate|frame_rate|pixel_format|pixelformat|fourcc|stride|subtype)[a-z_]*\b|\b\d{2,5}\s*[x\u00d7]\s*\d{2,5}\b");
        private static readonly Regex ExplicitFailure = Pattern(@"\bEr\(\d+\)|\b(?:error|fail|failed|failure|denied|abort|unavailable|insufficient)\b|0x8[0-9a-f]{7}\b|\u043d\u0435\u0434\u043e\u0441\u0442\u0430\u0442\u043e\u0447\u043d\u043e|\u043e\u0442\u043a\u0430\u0437\u0430\u043d\u043e|\u043e\u0448\u0438\u0431\u043a\u0430");
        private static readonly Regex Url = Pattern(@"\b(?:https?|rtsp|ftp|wss?|tcp|udp)://[^\s\""'<>]+");
        private static readonly Regex Ipv4 = Pattern(@"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?\b");
        private static readonly Regex Ipv6 = Pattern(@"\[[0-9a-f:]*:[0-9a-f:]+\](?::\d{1,5})?|(?<![0-9a-f:])(?:[0-9a-f]{0,4}:){4,}[0-9a-f]{0,4}(?![0-9a-f:])");
        private static readonly Regex UserPath = Pattern(@"(?<prefix>[a-z]:\\Users\\)[^\\\s\""'<>]+");
        private static readonly Regex Account = Pattern(@"\b(?<label>user(?:name)?|account|domain)\s*[:=]\s*(?:\""[^\""\r\n]*\""|'[^'\r\n]*'|[^\s,;]+)");

        internal static void CaptureOnce(string cameraId)
        {
            if (!DiagnosticLog.IsEnabled || Interlocked.CompareExchange(ref queued, 1, 0) != 0) return;
            Capture(cameraId, "stream_access_denied");
        }

        internal static void Capture(string cameraId, string trigger)
        {
            if (!DiagnosticLog.IsEnabled) return;
            trigger = NormalizeTrigger(trigger);
            int snapshot;
            string reason;
            if (!Schedule.TryAcquire(CaptureClock.ElapsedMilliseconds, out snapshot, out reason))
            {
                DiagnosticLog.Write("capture.vendor_diagnostics.skipped", "trigger", trigger, "reason", reason,
                    "maximum_snapshots", SnapshotSchedule.MaximumSnapshots, "minimum_interval_ms", SnapshotSchedule.MinimumIntervalMilliseconds);
                return;
            }
            DateTime failureUtc = DateTime.UtcNow;
            try { ThreadPool.QueueUserWorkItem(delegate { Collect(cameraId, failureUtc, trigger, snapshot); }); }
            catch (Exception error) { LogFailure("capture.vendor_diagnostics.queue_failed", error, null); }
        }

        private static Regex Pattern(string expression)
        { return new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }

        private static void Collect(string cameraId, DateTime failureUtc, string trigger, int snapshot)
        {
            try
            {
                Stopwatch elapsed = Stopwatch.StartNew();
                DiagnosticLog.Write("capture.vendor_diagnostics.started", "camera_id", cameraId, "failure_utc", failureUtc,
                    "snapshot_id", snapshot, "trigger", trigger,
                    "maximum_files", MaximumFiles, "maximum_tail_bytes", MaximumTailBytes, "maximum_lines", MaximumLines,
                    "maximum_lines_per_file", MaximumLinesPerFile, "maximum_collection_ms", MaximumCollectionMilliseconds,
                    "diagnostic_schema", 2, "application_version", typeof(HorizonCameraDiagnostics).Assembly.GetName().Version.ToString(),
                    "context_before", ContextBefore, "context_after", ContextAfter,
                    "scope", "recent camera/RTAV component messages only; read-only; no audio/video/clipboard contents");
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                KnownRoot[] roots = KnownRoots(programData, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Path.GetTempPath(), Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                List<Candidate> candidates = new List<Candidate>();
                foreach (KnownRoot root in roots)
                {
                    if (elapsed.ElapsedMilliseconds >= MaximumCollectionMilliseconds) break;
                    FindCandidates(root, failureUtc, candidates, elapsed);
                }
                candidates.Sort(delegate(Candidate first, Candidate second)
                {
                    int byKind = second.TrustedRtav.CompareTo(first.TrustedRtav);
                    return byKind != 0 ? byKind : second.ModifiedUtc.CompareTo(first.ModifiedUtc);
                });
                // A busy agent directory must not hide the local client's most
                // recent camera record. Give each fixed source one first pass.
                var firstByRoot = new List<Candidate>();
                var rest = new List<Candidate>();
                var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Candidate candidate in candidates)
                    if (seenRoots.Add(candidate.RootPath)) firstByRoot.Add(candidate); else rest.Add(candidate);
                firstByRoot.AddRange(rest);
                int files = 0, filesRead = 0, lines = 0;
                bool explicitFailure = false;
                foreach (Candidate candidate in firstByRoot)
                {
                    if (files >= MaximumFiles || lines >= MaximumLines || elapsed.ElapsedMilliseconds >= MaximumCollectionMilliseconds) break;
                    files++;
                    try
                    {
                        Tail tail;
                        using (FileStream input = new FileStream(candidate.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) tail = ReadTail(input, MaximumTailBytes);
                        filesRead++;
                        Selection selection = SelectMessages(tail.Text, failureUtc, candidate.TrustedRtav);
                        string[] relevant = selection.Lines;
                        explicitFailure |= selection.ExplicitFailurePresent;
                        DiagnosticLog.Write("capture.vendor_diagnostics.file", "file", Path.GetFileName(candidate.Path), "last_write_utc", candidate.ModifiedUtc,
                            "snapshot_id", snapshot, "trigger", trigger, "source", candidate.RootLabel,
                            "file_bytes", tail.FileBytes, "tail_offset", tail.Offset, "read_bytes", tail.BytesRead, "matching_lines", relevant.Length,
                            "first_message_utc", selection.FirstUtc, "last_message_utc", selection.LastUtc, "file_snapshot_utc", DateTime.UtcNow,
                            "explicit_failure_line_present", selection.ExplicitFailurePresent,
                            "cause_status", selection.ExplicitFailurePresent ? "vendor_error_record_present; root_cause_not_confirmed" : "not_present_in_selected_vendor_messages");
                        int firstLine = Math.Max(0, relevant.Length - Math.Min(MaximumLinesPerFile, MaximumLines - lines));
                        for (int index = firstLine; index < relevant.Length; index++)
                        {
                            if (lines >= MaximumLines) break;
                            DiagnosticLog.Write("capture.vendor_diagnostics.line", "snapshot_id", snapshot, "source", candidate.RootLabel,
                                "file", Path.GetFileName(candidate.Path), "text", relevant[index]);
                            lines++;
                        }
                    }
                    catch (Exception error) { LogFailure("capture.vendor_diagnostics.file_unavailable", error, Path.GetFileName(candidate.Path)); }
                }
                DiagnosticLog.Write("capture.vendor_diagnostics.completed", "files_found", candidates.Count, "files_attempted", files, "files_read", filesRead,
                    "snapshot_id", snapshot, "trigger", trigger, "elapsed_ms", elapsed.ElapsedMilliseconds,
                    "time_budget_reached", elapsed.ElapsedMilliseconds >= MaximumCollectionMilliseconds,
                    "roots_considered", roots.Length,
                    "lines", lines, "failure_utc", failureUtc, "no_matching_recent_messages", lines == 0,
                    "explicit_failure_line_present", explicitFailure,
                    "cause_status", explicitFailure ? "vendor_error_record_present; root_cause_not_confirmed" : "not_present_in_selected_vendor_messages");
            }
            catch (Exception error) { LogFailure("capture.vendor_diagnostics.failed", error, null); }
        }

        private static void FindCandidates(KnownRoot root, DateTime failureUtc, List<Candidate> candidates, Stopwatch elapsed)
        {
            try
            {
                if (!Directory.Exists(root.Path)) return;
                int visited = 0;
                foreach (string path in Directory.EnumerateFiles(root.Path))
                {
                    if (elapsed.ElapsedMilliseconds >= MaximumCollectionMilliseconds) break;
                    if (++visited > MaximumDirectoryEntries)
                    { DiagnosticLog.Write("capture.vendor_diagnostics.directory_limited", "root", root.Label, "maximum_entries", MaximumDirectoryEntries); break; }
                    string name = Path.GetFileName(path);
                    if (!IsVendorFileName(name)) continue;
                    DateTime modified = File.GetLastWriteTimeUtc(path);
                    if (Math.Abs((modified - failureUtc).TotalMinutes) > 5) continue;
                    candidates.Add(new Candidate { Path = path, ModifiedUtc = modified, RootPath = root.Path,
                        RootLabel = root.Label, TrustedRtav = name.StartsWith("rtav", StringComparison.OrdinalIgnoreCase) });
                }
            }
            catch (Exception error) { LogFailure("capture.vendor_diagnostics.directory_unavailable", error, root.Label); }
        }

        internal static bool IsVendorFileName(string name)
        {
            if (String.IsNullOrEmpty(name) || !String.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)) return false;
            bool extension = name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
            return extension && (name.StartsWith("debug-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("rtav", StringComparison.OrdinalIgnoreCase) || name.StartsWith("vmware", StringComparison.OrdinalIgnoreCase) || name.StartsWith("log-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("omnissa-horizon-client-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("horizon-protocol-", StringComparison.OrdinalIgnoreCase));
        }

        internal static KnownRoot[] KnownRoots(string programData, string localData, string temp, string userName, string profilePath)
        {
            var roots = new List<KnownRoot>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddRoot(roots, paths, programData, "agent:VMware/VDM/logs", "VMware", "VDM", "logs");
            AddRoot(roots, paths, programData, "agent:Omnissa/VDM/logs", "Omnissa", "VDM", "logs");
            AddRoot(roots, paths, programData, "agent:Omnissa/Horizon/logs", "Omnissa", "Horizon", "logs");
            AddRoot(roots, paths, programData, "agent:VDM/logs", "VDM", "logs");
            AddRoot(roots, paths, localData, "client:VMware/VDM/logs", "VMware", "VDM", "logs");
            AddRoot(roots, paths, localData, "client:Omnissa/VDM/logs", "Omnissa", "VDM", "logs");
            AddRoot(roots, paths, localData, "client:Omnissa/Horizon/logs", "Omnissa", "Horizon", "logs");
            AddRoot(roots, paths, localData, "client:VDM/logs", "VDM", "logs");
            string profileLeaf = String.IsNullOrEmpty(profilePath) ? null : Path.GetFileName(profilePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            foreach (string leaf in new[] { userName, profileLeaf })
            {
                if (String.IsNullOrEmpty(leaf) || leaf == "." || leaf == ".." || leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                AddRoot(roots, paths, temp, "client:temp/vmware-current-user", "vmware-" + leaf);
                AddRoot(roots, paths, temp, "client:temp/omnissa-current-user", "omnissa-" + leaf);
            }
            return roots.ToArray();
        }

        private static void AddRoot(List<KnownRoot> roots, HashSet<string> paths, string basePath, string label, params string[] relative)
        {
            if (String.IsNullOrWhiteSpace(basePath) || !Path.IsPathRooted(basePath)) return;
            string path = basePath;
            foreach (string part in relative) path = Path.Combine(path, part);
            if (paths.Add(path)) roots.Add(new KnownRoot { Path = path, Label = label });
        }

        internal static string NormalizeTrigger(string trigger)
        {
            if (String.IsNullOrEmpty(trigger)) return "unspecified";
            var result = new StringBuilder();
            foreach (char value in trigger)
            {
                if (result.Length >= 64) break;
                result.Append(value >= 'a' && value <= 'z' || value >= 'A' && value <= 'Z' || value >= '0' && value <= '9' || value == '_' || value == '-' ? value : '_');
            }
            return result.ToString();
        }

        internal sealed class KnownRoot { internal string Path, Label; }

        internal sealed class SnapshotSchedule
        {
            internal const int MaximumSnapshots = 4, MinimumIntervalMilliseconds = 30000;
            private readonly object gate = new object();
            private int count;
            private long last;

            internal bool TryAcquire(long nowMilliseconds, out int snapshot, out string reason)
            {
                lock (gate)
                {
                    snapshot = count;
                    if (nowMilliseconds < 0) { reason = "invalid_clock"; return false; }
                    if (count >= MaximumSnapshots) { reason = "process_snapshot_limit"; return false; }
                    if (count > 0 && (nowMilliseconds < last || nowMilliseconds - last < MinimumIntervalMilliseconds))
                    { reason = "minimum_interval"; return false; }
                    last = nowMilliseconds;
                    snapshot = ++count;
                    reason = "accepted";
                    return true;
                }
            }
        }

        private static void LogFailure(string name, Exception error, string file)
        {
            // Exception messages may contain user paths; keep only type/HRESULT.
            try { DiagnosticLog.Write(name, "file", file, "exception_type", error.GetType().FullName,
                "hresult", "0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture)); }
            catch { }
        }

        internal static Tail ReadTail(Stream input, int maximumBytes)
        {
            if (input == null || !input.CanRead || !input.CanSeek) throw new ArgumentException("A readable seekable stream is required.", "input");
            if (maximumBytes < 1 || maximumBytes > MaximumTailBytes) throw new ArgumentOutOfRangeException("maximumBytes");
            long length = input.Length;
            byte[] prefix = new byte[3]; input.Position = 0;
            int prefixLength = input.Read(prefix, 0, prefix.Length);
            Encoding encoding = Encoding.UTF8; bool wide = false;
            if (prefixLength >= 2 && prefix[0] == 255 && prefix[1] == 254) { encoding = Encoding.Unicode; wide = true; }
            else if (prefixLength >= 2 && prefix[0] == 254 && prefix[1] == 255) { encoding = Encoding.BigEndianUnicode; wide = true; }
            long offset = Math.Max(0, length - maximumBytes);
            if (wide && (offset & 1) != 0) offset++;
            int bytes = (int)Math.Min(maximumBytes, length - offset);
            byte[] data = new byte[bytes]; input.Position = offset;
            int used = 0, count;
            while (used < bytes && (count = input.Read(data, used, bytes - used)) > 0) used += count;
            string text = encoding.GetString(data, 0, used).TrimStart('\ufeff');
            // A tail may begin inside a log line or multibyte character. Never
            // treat that partial line as a complete diagnostic record.
            if (offset != 0) { int newline = text.IndexOf('\n'); text = newline < 0 ? String.Empty : text.Substring(newline + 1); }
            return new Tail { Text = text, Offset = offset, BytesRead = used, FileBytes = length };
        }

        internal static string[] SelectLines(string text, DateTime failureUtc, bool trustedRtav = false)
        {
            return SelectMessages(text, failureUtc, trustedRtav).Lines;
        }

        internal static Selection SelectMessages(string text, DateTime failureUtc, bool trustedRtav = false)
        {
            var result = new Selection { Lines = new string[0] };
            if (String.IsNullOrEmpty(text)) return result;
            // The public pure selector also stays bounded for synthetic inputs.
            if (text.Length > MaximumTailBytes)
            {
                text = text.Substring(text.Length - MaximumTailBytes);
                int boundary = text.IndexOf('\n');
                text = boundary < 0 ? String.Empty : text.Substring(boundary + 1);
            }
            var bounded = new Queue<string>();
            using (StringReader reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length > 2048) line = line.Substring(0, 2048);
                    bounded.Enqueue(line);
                    if (bounded.Count > MaximumInputLines) bounded.Dequeue();
                }
            }
            string[] source = bounded.ToArray();
            var records = new Message[source.Length];
            DateTime? inheritedUtc = null;
            for (int index = 0; index < source.Length; index++)
            {
                string line = source[index];
                var record = new Message { Text = line };
                records[index] = record;
                try
                {
                    // Sensitive records are a barrier: never inherit their
                    // timestamp to capture an unlabelled continuation.
                    if (PrivateContent.IsMatch(line)) { inheritedUtc = null; continue; }
                    Match stamp = Timestamp.Match(line);
                    DateTime utc = default(DateTime);
                    bool stamped = stamp.Success && DateTime.TryParse(stamp.Value.Replace(',', '.'), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
                    if (stamp.Success && !stamped) { inheritedUtc = null; continue; }
                    if (stamped)
                    {
                        inheritedUtc = Math.Abs((utc - failureUtc.ToUniversalTime()).TotalMinutes) <= 5 ? (DateTime?)utc : null;
                    }
                    bool continuation = !stamped && line.Length != 0 && Char.IsWhiteSpace(line[0]);
                    if (!inheritedUtc.HasValue || (!stamped && !continuation)) continue;
                    record.Utc = inheritedUtc;
                    // Unstamped text can only be bounded, whitelisted context,
                    // never a standalone diagnostic anchor.
                    record.Anchor = stamped && (trustedRtav || Component.IsMatch(line)) &&
                        (trustedRtav ? TrustedRtavAction : Action).IsMatch(line);
                    record.Context = FormatContext.IsMatch(line);
                }
                catch (RegexMatchTimeoutException) { inheritedUtc = null; }
            }
            var selected = new List<Message>();
            for (int index = 0; index < records.Length; index++)
            {
                Message record = records[index];
                if (!record.Utc.HasValue) continue;
                bool include = record.Anchor;
                if (!include && record.Context)
                {
                    // A context line can follow an anchor by ContextAfter or
                    // precede the next anchor by ContextBefore physical lines.
                    for (int nearby = Math.Max(0, index - ContextAfter); nearby <= Math.Min(records.Length - 1, index + ContextBefore); nearby++)
                        if (records[nearby].Anchor) { include = true; break; }
                }
                if (include)
                {
                    selected.Add(record);
                    if (selected.Count > MaximumLines) selected.RemoveAt(0);
                }
            }
            var lines = new List<string>();
            foreach (Message record in selected)
            {
                try
                {
                    lines.Add(Redact(record.Text));
                    if (!result.FirstUtc.HasValue) result.FirstUtc = record.Utc;
                    result.LastUtc = record.Utc;
                    result.ExplicitFailurePresent |= ExplicitFailure.IsMatch(record.Text);
                }
                catch (RegexMatchTimeoutException) { }
            }
            result.Lines = lines.ToArray();
            return result;
        }

        internal static string Redact(string line)
        {
            if (line == null) return String.Empty;
            string result = Url.Replace(line, "[url]");
            result = UserPath.Replace(result, "${prefix}[user]");
            result = Account.Replace(result, "${label}=[redacted]");
            result = Ipv4.Replace(result, "[ip]");
            result = Ipv6.Replace(result, "[ip]");
            return result.Length <= MaximumLineCharacters ? result : result.Substring(0, MaximumLineCharacters);
        }

        internal sealed class Tail { internal string Text; internal long Offset, FileBytes; internal int BytesRead; }
        internal sealed class Selection { internal string[] Lines; internal DateTime? FirstUtc, LastUtc; internal bool ExplicitFailurePresent; }
        private sealed class Message { internal string Text; internal DateTime? Utc; internal bool Anchor, Context; }
        private sealed class Candidate { internal string Path, RootPath, RootLabel; internal DateTime ModifiedUtc; internal bool TrustedRtav; }
    }
}
