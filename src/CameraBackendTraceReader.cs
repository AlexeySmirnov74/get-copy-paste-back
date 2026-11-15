using System;
using System.IO;
using System.Text;

namespace HorizonClient
{
    // Follows only the owned receiver's fixed stderr file. The native trace has
    // a dedicated metadata-only prefix; arbitrary stderr and frame data are ignored.
    internal sealed class CameraBackendTraceReader
    {
        internal const string Prefix = "HORIZON_UVC ";
        internal const int MaximumReadBytes = 16384, MaximumLineBytes = 4096;
        private long offset = -1;
        private readonly MemoryStream partial = new MemoryStream();
        private bool discardPartial;
        private DateTime creation;
        private string lastFailure;

        internal void Poll(string runtimeRoot)
        {
            if (!DiagnosticLog.IsEnabled || String.IsNullOrEmpty(runtimeRoot)) return;
            string path = Path.Combine(runtimeRoot, "data", "camera.stderr.log");
            try
            {
                DateTime currentCreation = File.GetCreationTimeUtc(path);
                if (creation != currentCreation) { Reset(); creation = currentCreation; }
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    Read(input, delegate(string line) { DiagnosticLog.Write("server.camera.native", "record", line); });
                lastFailure = null;
            }
            catch (Exception error)
            {
                string failure = error.GetType().FullName + "/" + error.HResult.ToString("X8");
                if (lastFailure == failure) return;
                lastFailure = failure;
                DiagnosticLog.Write("server.camera.native_trace.unavailable", "exception_type", error.GetType().FullName,
                    "hresult", "0x" + error.HResult.ToString("X8"));
            }
        }

        private void Reset() { offset = -1; partial.SetLength(0); discardPartial = false; }

        internal int Read(Stream input, Action<string> emit)
        {
            if (input == null || !input.CanSeek || !input.CanRead || emit == null) throw new ArgumentException("A readable seekable trace and sink are required.");
            long length = input.Length;
            if (offset < 0 || length < offset)
            {
                partial.SetLength(0);
                offset = Math.Max(0, length - MaximumReadBytes);
                discardPartial = offset > 0;
            }
            // Skip stale backlogs, retaining a bounded recent diagnostic tail.
            if (length - offset > MaximumReadBytes)
            { offset = length - MaximumReadBytes; partial.SetLength(0); discardPartial = true; }
            input.Position = offset;
            byte[] bytes = new byte[(int)Math.Min(MaximumReadBytes, length - offset)];
            int read = 0, count;
            while (read < bytes.Length && (count = input.Read(bytes, read, bytes.Length - read)) > 0) read += count;
            offset += read;
            int emitted = 0;
            for (int index = 0; index < read; index++)
            {
                byte value = bytes[index];
                if (value == 10)
                {
                    if (!discardPartial && partial.Length != 0)
                    {
                        string line = Encoding.UTF8.GetString(partial.ToArray()).TrimEnd('\r');
                        if (line.StartsWith(Prefix, StringComparison.Ordinal))
                        { emit(line.Substring(Prefix.Length)); emitted++; }
                    }
                    partial.SetLength(0); discardPartial = false;
                }
                else if (!discardPartial)
                {
                    if (partial.Length >= MaximumLineBytes) { partial.SetLength(0); discardPartial = true; }
                    else partial.WriteByte(value);
                }
            }
            return emitted;
        }
    }
}
