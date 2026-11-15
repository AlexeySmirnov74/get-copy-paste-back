using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HorizonClient
{
    // Read-only graph inspection. Called only by the graph's COM owner thread.
    // No diagnostic method changes a format, filter state, pin or allocator.
    internal sealed class DirectShowCaptureDiagnostics
    {
        internal readonly Guid CaptureId = Guid.NewGuid();
        private readonly string cameraId;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private int operation;
        internal static uint NativeThreadId { get { return GetCurrentThreadId(); } }
        internal DirectShowCaptureDiagnostics(string cameraId) { this.cameraId = cameraId; }

        internal int Call(string name, Func<int> action)
        {
            if (!DiagnosticLog.IsEnabled) return action();
            int call = ++operation; long began = clock.ElapsedMilliseconds;
            DiagnosticLog.Write("capture.native.begin", "capture_id", CaptureId, "call_id", call, "operation", name,
                "camera_id", cameraId, "since_open_ms", began, "native_thread", GetCurrentThreadId());
            try
            {
                int result = action();
                DiagnosticLog.Write("capture.native.end", "capture_id", CaptureId, "call_id", call, "operation", name,
                    "hresult", Hex(result), "description", Describe(result), "duration_ms", clock.ElapsedMilliseconds - began);
                return result;
            }
            catch (Exception error)
            {
                DiagnosticLog.Error("capture.native.exception", error, "capture_id", CaptureId, "call_id", call,
                    "operation", name, "duration_ms", clock.ElapsedMilliseconds - began);
                throw;
            }
        }

        internal object Create(Guid clsid)
        {
            object instance = null;
            Call("CoCreateInstance " + clsid, delegate { instance = DirectShow.Create(clsid); return 0; });
            return instance;
        }

        internal void Snapshot(DirectShow.IGraphBuilder graph, string stage)
        {
            if (!DiagnosticLog.IsEnabled || graph == null) return;
            IntPtr pointer = IntPtr.Zero; object enumeratorObject = null;
            DiagnosticLog.Write("capture.graph.snapshot.begin", "capture_id", CaptureId, "camera_id", cameraId, "stage", stage);
            try
            {
                int hr = graph.EnumFilters(out pointer);
                DiagnosticLog.Write("capture.graph.enumerate", "capture_id", CaptureId, "stage", stage, "hresult", Hex(hr));
                if (hr < 0 || pointer == IntPtr.Zero) return;
                enumeratorObject = Marshal.GetObjectForIUnknown(pointer);
                IEnumFilters filters = (IEnumFilters)enumeratorObject;
                for (int index = 0; index < 32; index++)
                {
                    DirectShow.IBaseFilter filter;
                    hr = filters.Next(1, out filter, IntPtr.Zero);
                    if (hr != 0 || filter == null) break;
                    try { Filter(filter, stage, index); }
                    finally { ReleaseReference(filter); }
                }
            }
            catch (Exception error) { DiagnosticLog.Error("capture.graph.snapshot.failed", error, "capture_id", CaptureId, "stage", stage); }
            finally
            {
                ReleaseReference(enumeratorObject);
                if (pointer != IntPtr.Zero) Marshal.Release(pointer);
                DiagnosticLog.Write("capture.graph.snapshot.end", "capture_id", CaptureId, "stage", stage);
            }
        }

        private void Filter(DirectShow.IBaseFilter filter, string stage, int index)
        {
            Guid clsid; int state;
            int classHr = filter.GetClassID(out clsid), stateHr = filter.GetState(0, out state);
            string name = ""; IntPtr info = Marshal.AllocCoTaskMem(256 + IntPtr.Size);
            try
            {
                Marshal.Copy(new byte[256 + IntPtr.Size], 0, info, 256 + IntPtr.Size);
                if (filter.QueryFilterInfo(info) >= 0)
                {
                    name = Marshal.PtrToStringUni(info, 128).TrimEnd('\0');
                    IntPtr owner = Marshal.ReadIntPtr(info, 256);
                    if (owner != IntPtr.Zero) Marshal.Release(owner);
                }
            }
            finally { Marshal.FreeCoTaskMem(info); }
            DiagnosticLog.Write("capture.graph.filter", "capture_id", CaptureId, "stage", stage, "filter_index", index,
                "name", name, "class_id", clsid, "class_hresult", Hex(classHr), "state", state, "state_hresult", Hex(stateHr));
            if (stage == "before_run") FilterModule(clsid, index);
            IntPtr pointer = IntPtr.Zero; object enumeratorObject = null;
            try
            {
                int hr = filter.EnumPins(out pointer);
                if (hr < 0 || pointer == IntPtr.Zero) return;
                enumeratorObject = Marshal.GetObjectForIUnknown(pointer);
                IEnumPins pins = (IEnumPins)enumeratorObject;
                for (int pinIndex = 0; pinIndex < 32; pinIndex++)
                {
                    IPin pin;
                    if (pins.Next(1, out pin, IntPtr.Zero) != 0 || pin == null) break;
                    try { Pin(pin, stage, index, pinIndex); }
                    finally { ReleaseReference(pin); }
                }
            }
            finally { ReleaseReference(enumeratorObject); if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
        }

        private void FilterModule(Guid clsid, int filter)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\" + clsid.ToString("B") + @"\InprocServer32", false))
                {
                    string module = key == null ? null : key.GetValue(null, null) as string;
                    string apartment = key == null ? null : key.GetValue("ThreadingModel", null) as string;
                    string version = null;
                    if (!String.IsNullOrWhiteSpace(module))
                    {
                        string path = module.Trim('"');
                        string[] allowedRoots = { Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
                        foreach (string root in allowedRoots)
                            if (!String.IsNullOrEmpty(root) && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) &&
                                !path.Contains("..") && Path.IsPathRooted(path))
                            { version = FileVersionInfo.GetVersionInfo(path).FileVersion; break; }
                    }
                    DiagnosticLog.Write("capture.graph.filter.module", "capture_id", CaptureId, "filter_index", filter,
                        "class_id", clsid, "registered_module", module, "file_version", version, "threading_model", apartment,
                        "registry_view_bits", IntPtr.Size * 8);
                }
            }
            catch (Exception error) { DiagnosticLog.Write("capture.graph.filter.module_unavailable", "capture_id", CaptureId,
                "filter_index", filter, "exception_type", error.GetType().FullName, "hresult", Hex(error.HResult)); }
        }

        private void Pin(IPin pin, string stage, int filter, int index)
        {
            string id; int direction;
            int idHr = pin.QueryId(out id), directionHr = pin.QueryDirection(out direction);
            IPin peer = null;
            try
            {
                int connectedHr = pin.ConnectedTo(out peer);
                string peerId = null;
                if (connectedHr >= 0 && peer != null) peer.QueryId(out peerId);
                DiagnosticLog.Write("capture.graph.pin", "capture_id", CaptureId, "stage", stage, "filter_index", filter,
                    "pin_index", index, "pin_id", id, "id_hresult", Hex(idHr), "direction", direction,
                    "direction_hresult", Hex(directionHr), "connected_hresult", Hex(connectedHr), "peer_pin_id", peerId);
                DirectShow.AmMediaType type = new DirectShow.AmMediaType();
                try
                {
                    int mediaHr = pin.ConnectionMediaType(out type);
                    if (mediaHr >= 0) MediaType("pin_connection", type, filter, index, stage);
                }
                finally { DirectShow.FreeMediaType(ref type); }
            }
            finally { ReleaseReference(peer); }
        }

        internal void Capabilities(DirectShow.ICaptureGraphBuilder2 builder, DirectShow.IBaseFilter source)
        {
            if (!DiagnosticLog.IsEnabled) return;
            object configObject = null; IntPtr buffer = IntPtr.Zero, typePointer = IntPtr.Zero;
            try
            {
                Guid category = DirectShow.PinCategoryCapture, media = DirectShow.MediaTypeVideo, iid = typeof(DirectShow.IAMStreamConfig).GUID;
                int hr = builder.FindInterface(ref category, ref media, source, ref iid, out configObject);
                DiagnosticLog.Write("capture.source.config_interface", "capture_id", CaptureId, "hresult", Hex(hr));
                var config = configObject as DirectShow.IAMStreamConfig;
                if (hr < 0 || config == null) return;
                hr = config.GetFormat(out typePointer);
                DiagnosticLog.Write("capture.source.current_format", "capture_id", CaptureId, "hresult", Hex(hr));
                if (hr >= 0 && typePointer != IntPtr.Zero)
                    MediaType("source_default", (DirectShow.AmMediaType)Marshal.PtrToStructure(typePointer, typeof(DirectShow.AmMediaType)), -1, -1, "before_native_format_change");
                DirectShow.FreeMediaTypePointer(typePointer); typePointer = IntPtr.Zero;
                int count, size; hr = config.GetNumberOfCapabilities(out count, out size);
                DiagnosticLog.Write("capture.source.capabilities", "capture_id", CaptureId, "hresult", Hex(hr), "count", count, "struct_bytes", size, "maximum_logged", 128);
                if (hr < 0 || count <= 0 || count > 4096 || size <= 0 || size > 1048576) return;
                buffer = Marshal.AllocCoTaskMem(size);
                Marshal.Copy(new byte[size], 0, buffer, size);
                for (int index = 0; index < Math.Min(count, 128); index++)
                {
                    try
                    {
                        hr = config.GetStreamCaps(index, out typePointer, buffer);
                        DiagnosticLog.Write("capture.source.capability.result", "capture_id", CaptureId, "index", index, "hresult", Hex(hr));
                        if (hr < 0 || typePointer == IntPtr.Zero) continue;
                        MediaType("source_capability", (DirectShow.AmMediaType)Marshal.PtrToStructure(typePointer, typeof(DirectShow.AmMediaType)), -1, index, "before_native_format_change");
                        // VIDEO_STREAM_CONFIG_CAPS native offsets from strmif.h.
                        if (size == 128)
                            DiagnosticLog.Write("capture.source.capability.bounds", "capture_id", CaptureId, "index", index,
                                "min_width", Marshal.ReadInt32(buffer, 60), "min_height", Marshal.ReadInt32(buffer, 64),
                                "max_width", Marshal.ReadInt32(buffer, 68), "max_height", Marshal.ReadInt32(buffer, 72),
                                "width_step", Marshal.ReadInt32(buffer, 76), "height_step", Marshal.ReadInt32(buffer, 80),
                                "min_interval_100ns", Marshal.ReadInt64(buffer, 104), "max_interval_100ns", Marshal.ReadInt64(buffer, 112),
                                "min_bps", Marshal.ReadInt32(buffer, 120), "max_bps", Marshal.ReadInt32(buffer, 124));
                    }
                    finally { DirectShow.FreeMediaTypePointer(typePointer); typePointer = IntPtr.Zero; }
                }
            }
            catch (Exception error) { DiagnosticLog.Error("capture.source.capabilities.failed", error, "capture_id", CaptureId); }
            finally { DirectShow.FreeMediaTypePointer(typePointer); if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer); ReleaseReference(configObject); }
        }

        private static void ReleaseReference(object value)
        {
            // Enumeration may return an RCW already owned by the live graph.
            // Release only this acquired reference; never invalidate that RCW.
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        internal void MediaType(string kind, DirectShow.AmMediaType type, int filter, int index, string stage)
        {
            DirectShow.BitmapInfoHeader bitmap;
            bool valid = DirectShow.TryGetBitmapHeader(type, out bitmap);
            long interval = type.FormatPointer != IntPtr.Zero && type.FormatSize >= 48 &&
                (type.FormatType == DirectShow.FormatVideoInfo || type.FormatType == DirectShow.FormatVideoInfo2) ? Marshal.ReadInt64(type.FormatPointer, 40) : 0;
            DiagnosticLog.Write("capture.media_type", "capture_id", CaptureId, "kind", kind, "stage", stage, "filter_index", filter, "index", index,
                "major_type", type.MajorType, "subtype", type.SubType, "format_type", type.FormatType,
                "fixed_samples", type.FixedSizeSamples, "temporal_compression", type.TemporalCompression, "sample_bytes", type.SampleSize,
                "format_bytes", type.FormatSize, "valid_bitmap", valid, "width", bitmap.Width, "signed_height", bitmap.Height,
                "bits_per_pixel", bitmap.BitCount, "compression_fourcc", "0x" + bitmap.Compression.ToString("X8"),
                "image_bytes", bitmap.ImageSize, "frame_interval_100ns", interval, "fps", interval > 0 ? 10000000.0 / interval : 0);
        }

        internal static string Hex(int hr) { return "0x" + hr.ToString("X8"); }
        internal static string EventName(int code)
        {
            switch (code) {
                case 1: return "EC_COMPLETE"; case 2: return "EC_USERABORT"; case 3: return "EC_ERRORABORT";
                case 8: return "EC_ERROR_STILLPLAYING"; case 0x0d: return "EC_CLOCK_CHANGED";
                case 0x0e: return "EC_PAUSED"; case 0x1f: return "EC_DEVICE_LOST"; case 0x32: return "EC_STATE_CHANGE";
                case 0x45: return "EC_ERRORABORTEX"; default: return "event_0x" + code.ToString("X");
            }
        }
        internal static string Describe(int hr)
        {
            if (hr == 0) return "S_OK";
            if (hr == 1) return "S_FALSE";
            try { var text = new StringBuilder(512); if (AMGetErrorTextW(hr, text, text.Capacity) > 0) return text.ToString().Trim(); }
            catch (Exception) { }
            try { var text = new StringBuilder(512); if (FormatMessage(0x1200, IntPtr.Zero, unchecked((uint)hr), 0, text, text.Capacity, IntPtr.Zero) != 0) return text.ToString().Trim(); }
            catch (Exception) { }
            return "HRESULT " + Hex(hr);
        }
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("quartz.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int AMGetErrorTextW(int hr, StringBuilder text, int maximum);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int FormatMessage(int flags, IntPtr source, uint code, int language, StringBuilder text, int maximum, IntPtr arguments);

        [ComImport, Guid("56A86893-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IEnumFilters {
            [PreserveSig] int Next(int count, [MarshalAs(UnmanagedType.Interface)] out DirectShow.IBaseFilter filter, IntPtr fetched);
            [PreserveSig] int Skip(int count); [PreserveSig] int Reset(); [PreserveSig] int Clone(out IEnumFilters copy);
        }
        [ComImport, Guid("56A86892-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IEnumPins {
            [PreserveSig] int Next(int count, [MarshalAs(UnmanagedType.Interface)] out IPin pin, IntPtr fetched);
            [PreserveSig] int Skip(int count); [PreserveSig] int Reset(); [PreserveSig] int Clone(out IEnumPins copy);
        }
        [ComImport, Guid("56A86891-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPin {
            [PreserveSig] int Connect(IPin other, IntPtr type); [PreserveSig] int ReceiveConnection(IPin other, IntPtr type);
            [PreserveSig] int Disconnect(); [PreserveSig] int ConnectedTo(out IPin other);
            [PreserveSig] int ConnectionMediaType(out DirectShow.AmMediaType type); [PreserveSig] int QueryPinInfo(IntPtr info);
            [PreserveSig] int QueryDirection(out int direction); [PreserveSig] int QueryId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int QueryAccept(IntPtr type); [PreserveSig] int EnumMediaTypes(out IntPtr types);
            [PreserveSig] int QueryInternalConnections(IntPtr pins, ref int count); [PreserveSig] int EndOfStream();
            [PreserveSig] int BeginFlush(); [PreserveSig] int EndFlush(); [PreserveSig] int NewSegment(long start, long stop, double rate);
        }
    }
}
