using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;

namespace HorizonClient
{
    // Libraries remain embedded and are extracted only when actually loaded.
    // Existing DLLs are never overwritten: users can replace/relink the LGPL
    // Quiet component. No library is fetched from the network at runtime.
    internal sealed class NativeAudioCodec
    {
        private static readonly object LibrariesGate = new object();
        private static readonly Dictionary<AudioModemKind, NativeAudioCodec> Libraries = new Dictionary<AudioModemKind, NativeAudioCodec>();
        private readonly object encodingGate = new object();
        private readonly IntPtr module;
        private readonly CreateFunction create;
        private readonly DestroyFunction destroy;
        private readonly EncodeFunction encode;
        private readonly FeedFunction feed;
        private readonly ReceiveFunction receive;
        private static string[] currentLibraryNames;
        internal static string RuntimeDirectory
        {
            get
            {
#if HORIZON_SERVER
                return NativeAudioStorage.GetDirectory(AppDomain.CurrentDomain.BaseDirectory, true, null);
#else
                return NativeAudioStorage.GetDirectory(AppDomain.CurrentDomain.BaseDirectory, false, ClientPaths.Current.SettingsDirectory);
#endif
            }
        }
        internal AudioModemKind Kind { get; private set; }
        internal int MaximumPayload { get; private set; }

        private NativeAudioCodec(AudioModemKind kind)
        {
            Kind = kind;
            string path = Extract(kind);
            module = LoadLibraryEx(path, IntPtr.Zero, 0x00000100U | 0x00000800U);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot load the bundled " + AudioModemCatalog.GetId(kind) + " audio modem.");
            try
            {
                create = Function<CreateFunction>("hc_create"); destroy = Function<DestroyFunction>("hc_destroy");
                encode = Function<EncodeFunction>("hc_encode"); feed = Function<FeedFunction>("hc_feed"); receive = Function<ReceiveFunction>("hc_receive");
                MaximumPayload = Function<MaximumFunction>("hc_max_payload")(0);
                if (MaximumPayload < AudioPacketWire.OverheadBytes + AudioModem.MaxPayloadBytes || MaximumPayload > AudioPacketWire.MaximumWireBytes)
                    throw new InvalidDataException("The bundled audio modem advertised an invalid frame capacity.");
                DiagnosticLog.Write("audio.native.loaded", "modem", AudioModemCatalog.GetId(kind), "path", path, "max_wire_bytes", MaximumPayload, "native_mode", 0);
            }
            catch { FreeLibrary(module); throw; }
        }

        internal static NativeAudioCodec Get(AudioModemKind kind)
        {
            if (kind != AudioModemKind.Quiet && kind != AudioModemKind.Aicodix) throw new ArgumentOutOfRangeException("kind");
            lock (LibrariesGate)
            {
                NativeAudioCodec codec;
                if (!Libraries.TryGetValue(kind, out codec)) { codec = new NativeAudioCodec(kind); Libraries.Add(kind, codec); }
                return codec;
            }
        }

        internal float[] Encode(byte[] message)
        {
            if (message == null || message.Length < 1 || message.Length > MaximumPayload) throw new ArgumentException("Invalid audio modem message.", "message");
            float[] samples;
            lock (encodingGate)
            {
                int required = encode(0, message, message.Length, null, 0);
                if (required < 1 || required > AudioModem.SampleRate * 30) throw new InvalidDataException("Audio modem encoder failed or exceeded the thirty-second frame limit: " + required + ".");
                // Explicit silence ensures a streaming decoder receives its final
                // lookahead even when WASAPI stops callbacks as playback ends.
                samples = new float[required + AudioModem.SampleRate / 2];
                int written = encode(0, message, message.Length, samples, required);
                if (written != required) throw new InvalidDataException("Audio modem encoder returned an inconsistent sample count: " + written + ".");
            }
            float peak = 0;
            foreach (float value in samples)
            {
                if (Single.IsNaN(value) || Single.IsInfinity(value)) throw new InvalidDataException("Audio modem produced non-finite PCM.");
                peak = Math.Max(peak, Math.Abs(value));
            }
            if (peak == 0) throw new InvalidDataException("Audio modem produced a silent data frame.");
            float gain = Math.Min(1, AudioModem.PeakAmplitude / peak);
            if (gain < 1) for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
            DiagnosticLog.Write("audio.native.encoded", "modem", AudioModemCatalog.GetId(Kind), "wire_bytes", message.Length,
                "samples", samples.Length, "duration_ms", samples.Length * 1000L / AudioModem.SampleRate, "gain", gain);
            return samples;
        }

        internal Decoder NewDecoder() { return new Decoder(this); }

        internal sealed class Decoder : IDisposable
        {
            private readonly NativeAudioCodec codec;
            private readonly byte[] message;
            private readonly object gate = new object();
            private IntPtr context;
            internal Decoder(NativeAudioCodec codec)
            {
                this.codec = codec; message = new byte[codec.MaximumPayload];
                context = codec.create(0, AudioModem.SampleRate);
                if (context == IntPtr.Zero) throw new InvalidOperationException("Cannot initialize the " + AudioModemCatalog.GetId(codec.Kind) + " audio decoder.");
            }
            internal byte[][] Feed(float[] samples)
            {
                if (samples == null || samples.Length > AudioModem.SampleRate * 10) throw new ArgumentException("Invalid audio callback.");
                var messages = new List<byte[]>();
                lock (gate)
                {
                    if (context == IntPtr.Zero) throw new ObjectDisposedException("Native audio decoder");
                    GCHandle pinned = GCHandle.Alloc(samples, GCHandleType.Pinned);
                    try
                    {
                        for (int offset = 0; offset < samples.Length; offset += 8192)
                        {
                            int status = codec.feed(context, IntPtr.Add(pinned.AddrOfPinnedObject(), offset * sizeof(float)), Math.Min(8192, samples.Length - offset));
                            if (status < 0) throw new InvalidDataException("Native audio decoder feed failed: " + status + ".");
                            for (int index = 0; index < 32; index++)
                            {
                                int length = codec.receive(context, message, message.Length);
                                if (length == 0) break;
                                if (length < 0 || length > message.Length) throw new InvalidDataException("Native audio decoder returned an invalid message length: " + length + ".");
                                byte[] copy = new byte[length]; Buffer.BlockCopy(message, 0, copy, 0, length); messages.Add(copy);
                            }
                        }
                    }
                    finally { pinned.Free(); }
                }
                return messages.ToArray();
            }
            public void Dispose() { lock (gate) { if (context != IntPtr.Zero) { codec.destroy(context); context = IntPtr.Zero; } } }
        }

        private T Function<T>(string name) where T : class
        {
            IntPtr pointer = GetProcAddress(module, name);
            if (pointer == IntPtr.Zero) throw new EntryPointNotFoundException("Bundled audio modem is missing " + name + ".");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
        }

        private static string Extract(AudioModemKind kind)
        {
            byte[] image = ReadLibrary(kind);
            if (currentLibraryNames == null)
            {
                var names = new List<string>();
                foreach (AudioModemKind bundled in new[] { AudioModemKind.Quiet, AudioModemKind.Aicodix })
                    names.Add(NativeAudioStorage.LibraryName(bundled, bundled == kind ? image : ReadLibrary(bundled)));
                currentLibraryNames = names.ToArray();
            }
            return NativeAudioStorage.ExtractImage(RuntimeDirectory, kind, image, currentLibraryNames,
                delegate(string path) { return GetModuleHandle(path) != IntPtr.Zero; });
        }

        private static byte[] ReadLibrary(AudioModemKind kind)
        {
            string id = AudioModemCatalog.GetId(kind);
            byte[] image;
            using (Stream input = typeof(NativeAudioCodec).Assembly.GetManifestResourceStream("HorizonClient.Audio." + id + ".dll"))
            {
                if (input == null) throw new FileNotFoundException("The " + id + " audio modem is missing from this EXE. Update both packages.");
                if (input.Length < 512 || input.Length > 32 * 1024 * 1024) throw new InvalidDataException("Bundled audio modem exceeds its size limit.");
                image = new byte[(int)input.Length]; int offset = 0;
                while (offset < image.Length) { int count = input.Read(image, offset, image.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
            }
            return image;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateFunction(int mode, int sampleRate);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyFunction(IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EncodeFunction(int mode, [In] byte[] data, int length, [Out] float[] samples, int capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FeedFunction(IntPtr context, IntPtr samples, int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReceiveFunction(IntPtr context, [Out] byte[] data, int capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MaximumFunction(int mode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string moduleName);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr module);
    }

    // Storage is isolated from loading/decoding so cleanup can be checked using
    // ordinary fixture files without loading native code or opening devices.
    internal static class NativeAudioStorage
    {
        internal static string GetDirectory(string executableDirectory, bool server, string clientSettingsDirectory)
        {
            string root = AbsoluteDirectory(executableDirectory, "executableDirectory");
            return server ? Path.Combine(root, "HorizonServer", "audio-native") :
                Path.Combine(AbsoluteDirectory(clientSettingsDirectory, "clientSettingsDirectory"), "audio-native");
        }

        internal static string LibraryName(AudioModemKind kind, byte[] image)
        {
            if (kind != AudioModemKind.Quiet && kind != AudioModemKind.Aicodix) throw new ArgumentOutOfRangeException("kind");
            if (image == null || image.Length < 512 || image.Length > 32 * 1024 * 1024) throw new ArgumentException("Invalid bundled audio library.", "image");
            string hash;
            using (SHA256 algorithm = SHA256.Create()) hash = BitConverter.ToString(algorithm.ComputeHash(image)).Replace("-", "").ToLowerInvariant();
            return "horizon-" + AudioModemCatalog.GetId(kind) + "-" + hash.Substring(0, 16) + ".dll";
        }

        internal static string ExtractImage(string directory, AudioModemKind kind, byte[] image, IEnumerable<string> currentNames, Func<string, bool> isLoaded)
        {
            if (currentNames == null) throw new ArgumentNullException("currentNames");
            if (isLoaded == null) throw new ArgumentNullException("isLoaded");
            directory = AbsoluteDirectory(directory, "directory");
            string name = LibraryName(kind, image), path = Path.Combine(directory, name);
            var keep = new HashSet<string>(currentNames, StringComparer.OrdinalIgnoreCase); keep.Add(name);
            if (HasReparseAncestor(directory)) throw new IOException("The audio library directory contains a redirection.");
            Directory.CreateDirectory(directory);
            if (!File.Exists(path))
            {
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(temporary, image);
                    try { File.Move(temporary, path); }
                    catch (IOException) { if (!File.Exists(path)) throw; }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                DiagnosticLog.Write("audio.native.extracted", "modem", AudioModemCatalog.GetId(kind), "path", path, "bytes", image.Length);
            }
            // A user-provided/relinked DLL under the current name is retained.
            CleanupStale(directory, keep, isLoaded);
            return path;
        }

        internal static void CleanupStale(string directory, IEnumerable<string> currentNames, Func<string, bool> isLoaded)
        {
            if (currentNames == null) throw new ArgumentNullException("currentNames");
            if (isLoaded == null) throw new ArgumentNullException("isLoaded");
            directory = AbsoluteDirectory(directory, "directory");
            var keep = new HashSet<string>(currentNames, StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!Directory.Exists(directory) || HasReparseAncestor(directory)) return;
                foreach (string candidate in Directory.GetFiles(directory, "horizon-*.dll", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(candidate);
                    if (!OwnedLibraryName(name) || keep.Contains(name)) continue;
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(candidate);
                        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0) continue;
                        if (isLoaded(candidate)) { DiagnosticLog.Write("audio.native.stale_retained", "path", candidate, "reason", "loaded"); continue; }
                        // The exclusive delete-on-close handle prevents another
                        // process from loading this file between checking and
                        // deletion; Windows also rejects mapped/locked images.
                        using (new FileStream(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                        DiagnosticLog.Write("audio.native.stale_removed", "path", candidate);
                    }
                    catch (IOException error) { DiagnosticLog.Write("audio.native.stale_retained", "path", candidate, "reason", error.GetType().Name); }
                    catch (UnauthorizedAccessException error) { DiagnosticLog.Write("audio.native.stale_retained", "path", candidate, "reason", error.GetType().Name); }
                    catch (SecurityException error) { DiagnosticLog.Write("audio.native.stale_retained", "path", candidate, "reason", error.GetType().Name); }
                }
            }
            catch (IOException error) { DiagnosticLog.Write("audio.native.cleanup_skipped", "directory", directory, "reason", error.GetType().Name); }
            catch (UnauthorizedAccessException error) { DiagnosticLog.Write("audio.native.cleanup_skipped", "directory", directory, "reason", error.GetType().Name); }
            catch (SecurityException error) { DiagnosticLog.Write("audio.native.cleanup_skipped", "directory", directory, "reason", error.GetType().Name); }
        }

        internal static bool OwnedLibraryName(string name)
        {
            if (name == null) return false;
            string prefix = name.StartsWith("horizon-quiet-", StringComparison.Ordinal) ? "horizon-quiet-" :
                name.StartsWith("horizon-aicodix-", StringComparison.Ordinal) ? "horizon-aicodix-" : null;
            if (prefix == null || name.Length != prefix.Length + 16 + 4 || !name.EndsWith(".dll", StringComparison.Ordinal)) return false;
            for (int index = prefix.Length; index < prefix.Length + 16; index++)
                if (!((name[index] >= '0' && name[index] <= '9') || (name[index] >= 'a' && name[index] <= 'f'))) return false;
            return true;
        }

        private static bool HasReparseAncestor(string directory)
        {
            for (string path = directory; !String.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            {
                try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return false;
        }

        private static string AbsoluteDirectory(string directory, string parameter)
        {
            if (String.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) throw new ArgumentException("An absolute directory is required.", parameter);
            string root = Path.GetPathRoot(directory);
            if (root.Length <= 1 || (root.Length == 2 && root[1] == ':')) throw new ArgumentException("An absolute directory is required.", parameter);
            return Path.GetFullPath(directory);
        }
    }
}
