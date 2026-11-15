// Independent Windows 10 Media Foundation capture. No native runtime is bundled.
// ABI: mfobjects.h, mfidl.h and mfreadwrite.h (Windows SDK). All device/source
// calls and COM releases belong to the MTA owner; Dispose only signals and joins.
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;

namespace HorizonClient
{
    public sealed class MediaFoundationCapture : IReceiverCapture, IReceiverCaptureModes
    {
        private readonly string deviceId;
        private readonly bool configureHighestNative;
        private readonly CameraResolution requestedResolution;
        private readonly Guid captureId = Guid.NewGuid();
        private int failureSnapshot;
        private readonly Action<Bitmap> onFrame;
        private readonly AutoResetEvent wake = new AutoResetEvent(false), frameWake = new AutoResetEvent(false);
        private readonly ManualResetEvent opened = new ManualResetEvent(false), startDone = new ManualResetEvent(false);
        private readonly object frames = new object(), startGate = new object();
        private readonly SampleMailbox samples = new SampleMailbox(Marshal.AddRef);
        private readonly FrameBufferPool frameBuffers = new FrameBufferPool();
        private readonly ReaderCallback callback;
        private readonly Thread ownerThread, deliveryThread;
        private Exception openError, startError, pendingError;
        private ManagedFrame latest;
        private FrameLayout layout;
        private NativeMode selectedNative;
        private CameraResolution[] availableResolutions = new CameraResolution[0];
        private int disposed, started, startRequested;
        private long received, delivered, replaced, readCallbacks, callbackEvents, callbackFlushes, lastSummary = Stopwatch.GetTimestamp();
        public event Action<Exception> OnError;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public CameraResolution[] AvailableResolutions { get { return (CameraResolution[])availableResolutions.Clone(); } }
        public CameraResolution CurrentResolution { get { return Width > 0 && Height > 0 ? new CameraResolution(Width, Height) : null; } }

        internal MediaFoundationCapture(string id, Action<Bitmap> handler)
            : this(id, handler, true, null) { }

        internal MediaFoundationCapture(string id, Action<Bitmap> handler, bool configureHighestNative)
            : this(id, handler, configureHighestNative, null) { }

        internal MediaFoundationCapture(string id, Action<Bitmap> handler, CameraResolution requested)
            : this(id, handler, true, requested) { }

        private MediaFoundationCapture(string id, Action<Bitmap> handler, bool configureHighestNative, CameraResolution requested)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentException("Choose an exact camera moniker ID.", "id");
            if (handler == null) throw new ArgumentNullException("handler");
            deviceId = id; onFrame = handler;
            this.configureHighestNative = configureHighestNative;
            requestedResolution = requested;
            callback = new ReaderCallback(this);
            deliveryThread = new Thread(DeliverLoop) { IsBackground = true, Name = "MF camera frame delivery" };
            ownerThread = new Thread(OwnerLoop) { IsBackground = true, Name = "MF camera source owner" };
            ownerThread.SetApartmentState(ApartmentState.MTA);
            Write("mf.open.requested", "camera_id", id, "backend", "MediaFoundation", "native_format_policy", NativePolicy);
            deliveryThread.Start(); ownerThread.Start();
            if (!opened.WaitOne(15000))
            { Dispose(); throw new TimeoutException("Media Foundation did not finish opening the selected camera within 15 seconds."); }
            if (openError != null)
            { Exception error = openError; Dispose(); throw new InvalidOperationException("Media Foundation could not open the selected camera: " + error.Message, error); }
        }

        public void Start()
        {
            lock (startGate)
            {
                if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("MediaFoundationCapture");
                if (Volatile.Read(ref started) != 0) return;
                startDone.Reset(); startError = null;
                Interlocked.Exchange(ref startRequested, 1); wake.Set();
                if (!startDone.WaitOne(10000))
                { Dispose(); throw new TimeoutException("Media Foundation did not accept the first video read within 10 seconds."); }
                if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("MediaFoundationCapture");
                if (startError != null) throw new InvalidOperationException("Media Foundation could not start video: " + startError.Message, startError);
            }
        }

        private void OwnerLoop()
        {
            bool mfStarted = false, ready = false;
            MediaFoundationNative.IMFAttributes attributes = null, readerAttributes = null;
            MediaFoundationNative.IMFActivate activation = null;
            MediaFoundationNative.IMFSourceReader reader = null;
            MediaFoundationNative.IMFMediaSource source = null;
            object sourceObject = null;
            try
            {
                string path = ResolveDevicePath(deviceId);
                Write("mf.source.identity", "camera_id", deviceId, "device_path", path);
                Trace("MFStartup", delegate { return MediaFoundationNative.MFStartup(0x00020070, 0); }); mfStarted = true;
                Trace("MFCreateAttributes.enumeration", delegate { return MediaFoundationNative.MFCreateAttributes(out attributes, 1); });
                Trace("attributes.source_type", delegate { return attributes.SetGUID(ref MediaFoundationNative.SourceType, ref MediaFoundationNative.VideoSource); });
                activation = FindActivation(attributes, path);
                Guid sourceIid = typeof(MediaFoundationNative.IMFMediaSource).GUID;
                Trace("IMFActivate.ActivateObject", delegate { return activation.ActivateObject(ref sourceIid, out sourceObject); });
                source = (MediaFoundationNative.IMFMediaSource)sourceObject;
                Trace("MFCreateAttributes.reader", delegate { return MediaFoundationNative.MFCreateAttributes(out readerAttributes, 2); });
                Trace("attributes.async_callback", delegate { return readerAttributes.SetUnknown(ref MediaFoundationNative.AsyncCallback, callback); });
                Trace("attributes.video_processing", delegate { return readerAttributes.SetUINT32(ref MediaFoundationNative.VideoProcessing, 1); });
                Trace("MFCreateSourceReaderFromMediaSource", delegate { return MediaFoundationNative.MFCreateSourceReaderFromMediaSource(source, readerAttributes, out reader); });
                Trace("reader.disable_other_streams", delegate { return reader.SetStreamSelection(-2, false); });
                Trace("reader.enable_first_video_stream", delegate { return reader.SetStreamSelection(-4, true); });
                if (configureHighestNative) ConfigureNativeModes(reader);
                else
                {
                    // Preserve the proven 0.6.10 path as an independent fallback:
                    // no native selection, no requested size/rate and no scaling.
                    // The driver chooses its size, which ReadLayout still bounds.
                    Write("mf.native.driver_default", "reason", "compatibility_backend_without_native_format_selection");
                    ReadDefaultCatalog(reader);
                    ConfigureRgb32(reader, null);
                    ReadLayout(reader);
                }
                ready = true; opened.Set();
                bool outstanding = false;
                while (Volatile.Read(ref disposed) == 0)
                {
                    if (Interlocked.Exchange(ref startRequested, 0) != 0)
                    {
                        try { Trace("reader.ReadSample.first", delegate { return reader.ReadSample(-4, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }); outstanding = true; Interlocked.Exchange(ref started, 1); }
                        catch (Exception error) { startError = error; Report(error, "read_first"); break; }
                        finally { startDone.Set(); }
                    }
                    NativeSample sample = samples.Take();
                    if (sample != null)
                    {
                        outstanding = false;
                        try
                        {
                            long callbacks = Interlocked.Increment(ref readCallbacks);
                            if (callbacks == 1 || sample.Status < 0 || sample.Flags != 0)
                                Write("mf.read.callback", "camera_id", deviceId, "callback", callbacks, "hresult", Hex(sample.Status),
                                    "stream_flags", "0x" + sample.Flags.ToString("X8"), "timestamp_100ns", sample.Timestamp, "has_sample", sample.Pointer != IntPtr.Zero);
                            if (sample.Status < 0) throw new COMException("Media Foundation video callback reported a streaming error.", sample.Status);
                            if ((sample.Flags & 1) != 0) throw new COMException("Media Foundation signalled a source-reader error.", unchecked((int)0x80004005));
                            if ((sample.Flags & 2) != 0) throw new InvalidOperationException("The camera's Media Foundation stream ended.");
                            if ((sample.Flags & 0x30) != 0)
                            {
                                if (selectedNative != null) VerifyNativeReadback(reader, selectedNative, "stream_type_changed");
                                ReadLayout(reader);
                            }
                            if (sample.Pointer != IntPtr.Zero) CopySample(sample);
                            else if (sample.Flags != 0) Write("mf.read.flags", "camera_id", deviceId, "flags", "0x" + sample.Flags.ToString("X8"), "timestamp_100ns", sample.Timestamp, "has_sample", false);
                        }
                        finally { if (sample.Pointer != IntPtr.Zero) Marshal.Release(sample.Pointer); }
                    }
                    if (Volatile.Read(ref started) != 0 && !outstanding && Volatile.Read(ref disposed) == 0)
                    {
                        int result = reader.ReadSample(-4, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                        if (result < 0) { Write("mf.call", "stage", "reader.ReadSample.next", "hresult", Hex(result)); Marshal.ThrowExceptionForHR(result); }
                        outstanding = true;
                    }
                    LogSummary();
                    wake.WaitOne(100);
                }
            }
            catch (Exception error)
            {
                CaptureFailure();
                if (!ready) openError = error;
                else Report(error, "owner");
                LogError("mf.open.failed", error, "camera_id", deviceId, "opened", ready);
            }
            finally
            {
                // Shutdown never runs on the caller's STA or on an MF callback.
                if (reader != null) TryClose("reader.Flush", delegate { return reader.Flush(-2); });
                if (source != null) TryClose("source.Shutdown", delegate { return source.Shutdown(); });
                NativeSample pending = samples.Close();
                if (pending != null && pending.Pointer != IntPtr.Zero) Marshal.Release(pending.Pointer);
                Release(reader); Release(sourceObject); Release(activation); Release(readerAttributes); Release(attributes);
                GC.KeepAlive(callback);
                if (mfStarted) TryClose("MFShutdown", MediaFoundationNative.MFShutdown);
                Write("mf.close", "camera_id", deviceId, "samples", Interlocked.Read(ref received), "delivered_frames", Interlocked.Read(ref delivered), "replaced_frames", Interlocked.Read(ref replaced), "unexpected_callbacks", samples.Dropped);
                opened.Set(); startDone.Set();
            }
        }

        private MediaFoundationNative.IMFActivate FindActivation(MediaFoundationNative.IMFAttributes attributes, string selectedPath)
        {
            IntPtr array = IntPtr.Zero; uint count = 0;
            MediaFoundationNative.IMFActivate selected = null;
            DeviceIdentity selectedIdentity = ReadIdentityOrNull(selectedPath, true);
            try
            {
                Trace("MFEnumDeviceSources", delegate { return MediaFoundationNative.MFEnumDeviceSources(attributes, out array, out count); });
                if (count > 4096 || (count != 0 && array == IntPtr.Zero)) throw new InvalidOperationException("Invalid Media Foundation device enumeration bounds.");
                for (uint i = 0; i < count; i++)
                {
                    IntPtr pointer = Marshal.ReadIntPtr(array, checked((int)i * IntPtr.Size));
                    MediaFoundationNative.IMFActivate item = null;
                    try
                    {
                        if (pointer == IntPtr.Zero) continue;
                        item = (MediaFoundationNative.IMFActivate)Marshal.GetObjectForIUnknown(pointer);
                        IntPtr text = IntPtr.Zero; uint length = 0; string path;
                        try
                        {
                            int hr = item.GetAllocatedString(ref MediaFoundationNative.VideoSymbolicLink, out text, out length);
                            Write("mf.call", "stage", "activation.GetAllocatedString.symbolic_link", "hresult", Hex(hr), "index", i, "length", length);
                            if (hr < 0 || length > 32768 || text == IntPtr.Zero) continue;
                            path = Marshal.PtrToStringUni(text, checked((int)length));
                        }
                        finally { if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text); }
                        DeviceIdentity candidateIdentity = null;
                        string reason;
                        bool needsIdentity = !SameDevicePath(selectedPath, path) &&
                            ((selectedIdentity != null && SameDevicePath(selectedIdentity.VideoAlias, path)) || EquivalentCameraInterfaceUnit(selectedPath, path));
                        if (needsIdentity) candidateIdentity = ReadIdentityOrNull(path, false);
                        bool match = MatchCameraIdentity(selectedPath, path, selectedIdentity, candidateIdentity, out reason);
                        Write("mf.source.mapping", "directshow_path", selectedPath, "candidate_path", path,
                            "selected_instance", selectedIdentity == null ? null : selectedIdentity.Instance,
                            "candidate_instance", candidateIdentity == null ? null : candidateIdentity.Instance,
                            "video_camera_alias", selectedIdentity == null ? null : selectedIdentity.VideoAlias, "selected", match, "reason", reason);
                        Write("mf.source.candidate", "symbolic_link", path, "selected", match, "index", i, "candidates", count);
                        if (match)
                        {
                            if (selected != null) throw new InvalidOperationException("More than one Media Foundation source has the selected camera's exact device path.");
                            selected = item; item = null;
                        }
                    }
                    finally { Release(item); }
                }
                if (selected == null) throw new InvalidOperationException("The exact selected DirectShow DevicePath is not available through Media Foundation.");
                MediaFoundationNative.IMFActivate result = selected; selected = null;
                return result;
            }
            finally
            {
                Release(selected);
                if (array != IntPtr.Zero)
                {
                    if (count <= 4096) for (uint i = 0; i < count; i++)
                    { IntPtr pointer = Marshal.ReadIntPtr(array, checked((int)i * IntPtr.Size)); if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
                    Marshal.FreeCoTaskMem(array);
                }
            }
        }

        private string ResolveDevicePath(string selectedId)
        {
            string result = null; bool found = false;
            DirectShow.WithVideoMonikers(delegate(IMoniker moniker)
            {
                string id; moniker.GetDisplayName(null, null, out id);
                if (!String.Equals(id, selectedId, StringComparison.Ordinal)) return false;
                found = true; object bag = null;
                try
                {
                    Guid iid = typeof(DirectShow.IPropertyBag).GUID; moniker.BindToStorage(null, null, ref iid, out bag);
                    object path = null;
                    Trace("moniker.Read.DevicePath", delegate { return ((DirectShow.IPropertyBag)bag).Read("DevicePath", out path, IntPtr.Zero); });
                    // The property is an opaque path. Never manufacture it from a display name.
                    result = path as string;
                }
                finally { Release(bag); }
                return true;
            });
            if (!found || String.IsNullOrWhiteSpace(result) || result.Length > 32768)
                throw new InvalidOperationException("The selected camera does not expose an exact DevicePath for Media Foundation.");
            return result;
        }

        internal static bool SameDevicePath(string selected, string candidate)
        { return !String.IsNullOrWhiteSpace(selected) && !String.IsNullOrWhiteSpace(candidate) && String.Equals(selected, candidate, StringComparison.OrdinalIgnoreCase); }

        internal sealed class DeviceIdentity
        {
            internal string Instance, VideoAlias;
        }

        internal static bool MatchCameraIdentity(string selectedPath, string candidatePath, DeviceIdentity selected, DeviceIdentity candidate, out string reason)
        {
            if (SameDevicePath(selectedPath, candidatePath)) { reason = "exact_symbolic_link"; return true; }
            bool hasAlias = selected != null && !String.IsNullOrEmpty(selected.VideoAlias);
            if (hasAlias && !SameDevicePath(selected.VideoAlias, candidatePath))
            { reason = "not_selected_windows_interface_alias"; return false; }
            if (!hasAlias && !EquivalentCameraInterfaceUnit(selectedPath, candidatePath))
            { reason = "different_interface_unit_reference_or_category"; return false; }
            if (selected == null || candidate == null || !SameDevicePath(selected.Instance, candidate.Instance))
            { reason = "pnp_instance_not_verified_or_different"; return false; }
            reason = hasAlias ? "windows_interface_alias_and_same_pnp_instance" : "same_pnp_instance_and_exact_camera_interface_unit";
            return true;
        }

        // The OS alias API is authoritative. This bounded fallback is used only
        // when it cannot supply an alias, and still requires verified PnP IDs.
        // Keep every character identifying the interface unit and reference;
        // the common vmwvwebcam parent does not identify one of its cameras.
        internal static bool EquivalentCameraInterfaceUnit(string selected, string candidate)
        {
            string selectedUnit, candidateUnit, selectedReference, candidateReference; Guid selectedCategory, candidateCategory;
            if (!SplitCameraInterface(selected, out selectedUnit, out selectedCategory, out selectedReference) ||
                !SplitCameraInterface(candidate, out candidateUnit, out candidateCategory, out candidateReference)) return false;
            return selectedCategory != candidateCategory && SameDevicePath(selectedUnit, candidateUnit) &&
                String.Equals(selectedReference, candidateReference, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SplitCameraInterface(string path, out string unit, out Guid category, out string reference)
        {
            unit = reference = null; category = Guid.Empty;
            if (String.IsNullOrEmpty(path) || !path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.Length > 32768) return false;
            int start = path.LastIndexOf("#{", StringComparison.Ordinal);
            if (start < 4 || start + 39 > path.Length || path[start + 38] != '}') return false;
            if (!Guid.TryParseExact(path.Substring(start + 1, 38), "B", out category) ||
                (category != MediaFoundationNative.CaptureCategory && category != MediaFoundationNative.VideoCameraCategory)) return false;
            unit = path.Substring(0, start); reference = path.Substring(start + 39);
            return reference.Length == 0 || reference[0] == '\\';
        }

        private DeviceIdentity ReadIdentityOrNull(string path, bool findAlias)
        {
            IntPtr set = IntPtr.Zero;
            try
            {
                set = MediaFoundationNative.SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
                int error = set == IntPtr.Zero || set == new IntPtr(-1) ? Marshal.GetLastWin32Error() : 0;
                if ((set == IntPtr.Zero || set == new IntPtr(-1)) && error == 0) error = 87;
                Write("mf.identity.call", "stage", "SetupDiCreateDeviceInfoList", "win32_error", error, "hresult", Hex(error == 0 ? 0 : unchecked((int)(0x80070000u | (uint)error))));
                if (error != 0) throw new Win32Exception(error);
                var item = new MediaFoundationNative.DeviceInterfaceData { Size = (uint)Marshal.SizeOf(typeof(MediaFoundationNative.DeviceInterfaceData)) };
                bool openedInterface = MediaFoundationNative.SetupDiOpenDeviceInterface(set, path, 0, ref item);
                CheckIdentityCall("SetupDiOpenDeviceInterface", openedInterface);
                MediaFoundationNative.DeviceInfoData device;
                string verifiedPath = ReadInterfaceDetail(set, ref item, out device);
                if (!SameDevicePath(path, verifiedPath)) throw new InvalidOperationException("SetupAPI returned a different camera interface path.");
                string instance = ReadInstanceId(set, ref device);
                var identity = new DeviceIdentity { Instance = instance };
                if (findAlias)
                {
                    var alias = new MediaFoundationNative.DeviceInterfaceData { Size = (uint)Marshal.SizeOf(typeof(MediaFoundationNative.DeviceInterfaceData)) };
                    bool aliased = MediaFoundationNative.SetupDiGetDeviceInterfaceAlias(set, ref item, ref MediaFoundationNative.VideoCameraCategory, ref alias);
                    int aliasError = aliased ? 0 : Marshal.GetLastWin32Error();
                    Write("mf.identity.call", "stage", "SetupDiGetDeviceInterfaceAlias.video_camera", "win32_error", aliasError,
                        "hresult", Hex(aliasError == 0 ? 0 : unchecked((int)(0x80070000u | (uint)aliasError))));
                    if (aliased)
                    {
                        MediaFoundationNative.DeviceInfoData aliasDevice;
                        string aliasPath = ReadInterfaceDetail(set, ref alias, out aliasDevice);
                        string aliasInstance = ReadInstanceId(set, ref aliasDevice);
                        if (!SameDevicePath(instance, aliasInstance)) throw new InvalidOperationException("Windows camera interface alias belongs to a different PnP instance.");
                        identity.VideoAlias = aliasPath;
                    }
                }
                Write("mf.source.instance_identity", "symbolic_link", path, "pnp_instance", identity.Instance, "video_camera_alias", identity.VideoAlias);
                return identity;
            }
            catch (Exception error) { LogError("mf.source.instance_identity_failed", error, "symbolic_link", path); return null; }
            finally
            {
                if (set != IntPtr.Zero && set != new IntPtr(-1))
                {
                    bool destroyed = MediaFoundationNative.SetupDiDestroyDeviceInfoList(set);
                    int error = destroyed ? 0 : Marshal.GetLastWin32Error();
                    Write("mf.identity.call", "stage", "SetupDiDestroyDeviceInfoList", "win32_error", error);
                }
            }
        }

        private void CheckIdentityCall(string stage, bool success)
        {
            int error = success ? 0 : Marshal.GetLastWin32Error();
            Write("mf.identity.call", "stage", stage, "win32_error", error, "hresult", Hex(error == 0 ? 0 : unchecked((int)(0x80070000u | (uint)error))));
            if (!success) throw new Win32Exception(error);
        }

        private string ReadInterfaceDetail(IntPtr set, ref MediaFoundationNative.DeviceInterfaceData item, out MediaFoundationNative.DeviceInfoData device)
        {
            device = new MediaFoundationNative.DeviceInfoData { Size = (uint)Marshal.SizeOf(typeof(MediaFoundationNative.DeviceInfoData)) };
            uint required;
            bool probe = MediaFoundationNative.SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out required, ref device);
            int error = probe ? 0 : Marshal.GetLastWin32Error();
            Write("mf.identity.call", "stage", "SetupDiGetDeviceInterfaceDetail.size", "win32_error", error, "required_bytes", required);
            if ((!probe && error != 122) || required < 8 || required > 65536) throw new Win32Exception(error == 0 ? 87 : error);
            uint capacity = required;
            IntPtr buffer = Marshal.AllocCoTaskMem(checked((int)capacity));
            try
            {
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                device.Size = (uint)Marshal.SizeOf(typeof(MediaFoundationNative.DeviceInfoData));
                bool read = MediaFoundationNative.SetupDiGetDeviceInterfaceDetail(set, ref item, buffer, capacity, out required, ref device);
                CheckIdentityCall("SetupDiGetDeviceInterfaceDetail.read", read);
                if (required < 8 || required > capacity) throw new InvalidOperationException("Invalid SetupAPI camera interface detail length.");
                string path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4), checked((int)(required - 4) / 2));
                int terminator = path.IndexOf('\0');
                return terminator < 0 ? path : path.Substring(0, terminator);
            }
            finally { Marshal.FreeCoTaskMem(buffer); }
        }

        private string ReadInstanceId(IntPtr set, ref MediaFoundationNative.DeviceInfoData device)
        {
            var value = new StringBuilder(2048); uint required;
            bool read = MediaFoundationNative.SetupDiGetDeviceInstanceId(set, ref device, value, (uint)value.Capacity, out required);
            CheckIdentityCall("SetupDiGetDeviceInstanceId", read);
            if (value.Length == 0 || required > value.Capacity) throw new InvalidOperationException("Invalid camera PnP instance identity.");
            return value.ToString();
        }

        private void ConfigureNativeModes(MediaFoundationNative.IMFSourceReader reader)
        {
            const uint limit = 512;
            var types = new Dictionary<uint, MediaFoundationNative.IMFMediaType>();
            var modes = new List<NativeMode>();
            try
            {
                for (uint index = 0; index < limit; index++)
                {
                    MediaFoundationNative.IMFMediaType type = null;
                    try
                    {
                        int hr = reader.GetNativeMediaType(-4, index, out type);
                        Write("mf.call", "stage", "reader.GetNativeMediaType", "index", index, "hresult", Hex(hr));
                        if (hr < 0)
                        {
                            if (hr != unchecked((int)0xC00D36B9)) Check(hr, "reader.GetNativeMediaType");
                            break;
                        }
                        NativeMode mode = DescribeNative(type, index, "advertised");
                        modes.Add(mode); types.Add(index, type); type = null;
                    }
                    finally { Release(type); }
                    if (index + 1 == limit) Write("mf.media_type.native_limit", "maximum_types", limit);
                }
                SetAvailableResolutions(modes);
                if (requestedResolution != null)
                {
                    bool offered = false; foreach (NativeMode mode in modes) if (mode.Eligible && mode.Width == requestedResolution.Width && mode.Height == requestedResolution.Height) { offered = true; break; }
                    Write("mf.native.requested_resolution", "width", requestedResolution.Width, "height", requestedResolution.Height, "offered", offered,
                        "fallback", offered ? "next_advertised_mode_if_rejected" : "highest_advertised_mode");
                }
                selectedNative = TryConfigureNativeModes(modes, delegate(NativeMode mode)
                {
                    if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("MediaFoundationCapture");
                    selectedNative = mode;
                    Write("mf.native.selection_attempt", "index", mode.Index, "width", mode.Width, "height", mode.Height, "subtype", mode.Subtype,
                        "rate_numerator", mode.RateNumerator, "rate_denominator", mode.RateDenominator, "policy", NativePolicy);
                    MediaFoundationNative.IMFSourceReaderEx extended = reader as MediaFoundationNative.IMFSourceReaderEx;
                    if (extended != null)
                    {
                        uint flags = 0;
                        Trace("reader.SetNativeMediaType", delegate { return extended.SetNativeMediaType(-4, types[mode.Index], out flags); });
                        Write("mf.native.selection_flags", "index", mode.Index, "stream_flags", "0x" + flags.ToString("X8"));
                    }
                    else Trace("reader.SetCurrentMediaType.native", delegate { return reader.SetCurrentMediaType(-4, IntPtr.Zero, types[mode.Index]); });
                    VerifyNativeReadback(reader, mode, "after_native_set");
                    ConfigureRgb32(reader, mode);
                    // Verify again: output negotiation must not silently revert
                    // the source to 320p and upscale through a video processor.
                    VerifyNativeReadback(reader, mode, "after_rgb32_conversion");
                    ReadLayout(reader);
                }, delegate(NativeMode mode, Exception error)
                {
                    LogError("mf.native.selection_rejected", error, "index", mode.Index, "width", mode.Width, "height", mode.Height, "subtype", mode.Subtype);
                }, requestedResolution);
                Write("mf.native.selected", "index", selectedNative.Index, "width", selectedNative.Width, "height", selectedNative.Height,
                    "subtype", selectedNative.Subtype, "rate_numerator", selectedNative.RateNumerator, "rate_denominator", selectedNative.RateDenominator);
            }
            finally { foreach (MediaFoundationNative.IMFMediaType type in types.Values) Release(type); }
        }

        private void ReadDefaultCatalog(MediaFoundationNative.IMFSourceReader reader)
        {
            var modes = new List<NativeMode>();
            for (uint index = 0; index < 512; index++)
            {
                MediaFoundationNative.IMFMediaType type = null;
                try
                {
                    int hr = reader.GetNativeMediaType(-4, index, out type);
                    Write("mf.call", "stage", "reader.GetNativeMediaType.catalog", "index", index, "hresult", Hex(hr));
                    if (hr < 0) break;
                    modes.Add(DescribeNative(type, index, "advertised"));
                }
                finally { Release(type); }
            }
            SetAvailableResolutions(modes);
        }

        private void SetAvailableResolutions(IEnumerable<NativeMode> modes)
        {
            var resolutions = new List<CameraResolution>();
            foreach (NativeMode mode in modes) if (mode.Eligible) resolutions.Add(new CameraResolution(mode.Width, mode.Height));
            availableResolutions = CameraResolution.Distinct(resolutions);
            foreach (CameraResolution mode in availableResolutions) Write("mf.mode.available", "width", mode.Width, "height", mode.Height);
        }

        private NativeMode DescribeNative(MediaFoundationNative.IMFMediaType type, uint index, string stage)
        {
            Guid major = Guid.Empty, subtype = Guid.Empty; ulong size = 0, rate = 0;
            int majorHr = type.GetGUID(ref MediaFoundationNative.MajorType, out major);
            int subHr = type.GetGUID(ref MediaFoundationNative.Subtype, out subtype);
            int sizeHr = type.GetUINT64(ref MediaFoundationNative.FrameSize, out size);
            int rateHr = type.GetUINT64(ref MediaFoundationNative.FrameRate, out rate);
            var result = new NativeMode { Index = index, Major = major, Subtype = subtype,
                Width = sizeHr < 0 || (size >> 32) > Int32.MaxValue ? 0 : (int)(size >> 32),
                Height = sizeHr < 0 || (size & 0xffffffff) > Int32.MaxValue ? 0 : (int)(size & 0xffffffff),
                RateNumerator = rateHr < 0 ? 0 : (uint)(rate >> 32), RateDenominator = rateHr < 0 ? 0 : (uint)(rate & 0xffffffff) };
            Write(stage == "advertised" ? "mf.media_type.native" : "mf.native.readback", "stage", stage, "index", index,
                "major", major, "major_hresult", Hex(majorHr), "subtype", subtype, "subtype_hresult", Hex(subHr), "width", result.Width, "height", result.Height,
                "size_hresult", Hex(sizeHr), "rate_numerator", result.RateNumerator, "rate_denominator", result.RateDenominator, "rate_hresult", Hex(rateHr));
            return result;
        }

        private void VerifyNativeReadback(MediaFoundationNative.IMFSourceReader reader, NativeMode expected, string stage)
        {
            MediaFoundationNative.IMFMediaType actual = null;
            try
            {
                Trace("reader.GetNativeMediaType.current." + stage, delegate { return reader.GetNativeMediaType(-4, UInt32.MaxValue, out actual); });
                ValidateNativeReadback(expected, DescribeNative(actual, UInt32.MaxValue, stage));
            }
            finally { Release(actual); }
        }

        private void ConfigureRgb32(MediaFoundationNative.IMFSourceReader reader, NativeMode mode)
        {
            MediaFoundationNative.IMFMediaType requested = null;
            try
            {
                Trace("MFCreateMediaType.RGB32", delegate { return MediaFoundationNative.MFCreateMediaType(out requested); });
                ConfigureRgb32Attributes(requested, mode, Trace);
                Trace(mode == null ? "reader.SetCurrentMediaType.RGB32.driver_default" : "reader.SetCurrentMediaType.RGB32", delegate { return reader.SetCurrentMediaType(-4, IntPtr.Zero, requested); });
            }
            finally { Release(requested); }
        }

        internal static void ConfigureRgb32Attributes(MediaFoundationNative.IMFMediaType requested, NativeMode mode, Action<string, Func<int>> invoke)
        {
            invoke("type.major_video", delegate { return requested.SetGUID(ref MediaFoundationNative.MajorType, ref MediaFoundationNative.Video); });
            invoke("type.subtype_RGB32", delegate { return requested.SetGUID(ref MediaFoundationNative.Subtype, ref MediaFoundationNative.Rgb32); });
            if (mode != null)
            {
                ulong size = ((ulong)mode.Width << 32) | (uint)mode.Height;
                invoke("type.exact_native_frame_size", delegate { return requested.SetUINT64(ref MediaFoundationNative.FrameSize, size); });
                if (mode.RateNumerator != 0 && mode.RateDenominator != 0)
                {
                    ulong rate = ((ulong)mode.RateNumerator << 32) | mode.RateDenominator;
                    invoke("type.native_frame_rate", delegate { return requested.SetUINT64(ref MediaFoundationNative.FrameRate, rate); });
                }
            }
        }

        internal sealed class NativeMode
        {
            internal uint Index, RateNumerator, RateDenominator;
            internal int Width, Height;
            internal Guid Major, Subtype;
            internal bool Eligible { get { return Major == MediaFoundationNative.Video && Subtype != Guid.Empty && CameraResolution.IsSupportedDimensions(Width, Height); } }
        }

        internal static NativeMode TryConfigureNativeModes(IEnumerable<NativeMode> advertised, Action<NativeMode> apply, Action<NativeMode, Exception> rejected)
        { return TryConfigureNativeModes(advertised, apply, rejected, null); }

        internal static NativeMode TryConfigureNativeModes(IEnumerable<NativeMode> advertised, Action<NativeMode> apply, Action<NativeMode, Exception> rejected, CameraResolution requested)
        {
            var modes = new List<NativeMode>();
            foreach (NativeMode mode in advertised)
                if (mode != null && mode.Eligible && (!CameraResolution.IsHighResolution(mode.Width, mode.Height) ||
                    requested != null && requested.Width == mode.Width && requested.Height == mode.Height)) modes.Add(mode);
            modes.Sort(delegate(NativeMode first, NativeMode second)
            {
                bool firstRequested = requested != null && first.Width == requested.Width && first.Height == requested.Height;
                bool secondRequested = requested != null && second.Width == requested.Width && second.Height == requested.Height;
                if (firstRequested != secondRequested) return firstRequested ? -1 : 1;
                int area = ((long)second.Width * second.Height).CompareTo((long)first.Width * first.Height);
                if (area != 0) return area;
                bool firstYuy2 = first.Subtype == MediaFoundationNative.Yuy2, secondYuy2 = second.Subtype == MediaFoundationNative.Yuy2;
                if (firstYuy2 != secondYuy2) return firstYuy2 ? -1 : 1;
                decimal firstFps = first.RateDenominator == 0 ? 0 : (decimal)first.RateNumerator / first.RateDenominator;
                decimal secondFps = second.RateDenominator == 0 ? 0 : (decimal)second.RateNumerator / second.RateDenominator;
                int rate = secondFps.CompareTo(firstFps);
                return rate != 0 ? rate : first.Index.CompareTo(second.Index);
            });
            Exception last = null;
            foreach (NativeMode mode in modes)
            {
                try { apply(mode); return mode; }
                catch (COMException error) { last = error; rejected(mode, error); }
                catch (NotSupportedException error) { last = error; rejected(mode, error); }
            }
            throw new NotSupportedException("No supported advertised camera mode could be captured without scaling; high resolution requires an exact explicit request.", last);
        }

        internal static void ValidateNativeReadback(NativeMode expected, NativeMode actual)
        {
            if (actual == null || actual.Major != MediaFoundationNative.Video || actual.Subtype != expected.Subtype || actual.Width != expected.Width || actual.Height != expected.Height)
                throw new NotSupportedException("Media Foundation changed the selected native camera format; scaling or a substituted native mode is not allowed.");
        }

        internal static void ValidateOutputSize(NativeMode expected, int width, int height)
        {
            if (expected != null && (width != expected.Width || height != expected.Height))
                throw new NotSupportedException("Media Foundation RGB32 output does not match the selected native camera size; scaling is not allowed.");
        }

        private void ReadLayout(MediaFoundationNative.IMFSourceReader reader)
        {
            MediaFoundationNative.IMFMediaType type = null;
            try
            {
                Trace("reader.GetCurrentMediaType", delegate { return reader.GetCurrentMediaType(-4, out type); });
                Guid major = Guid.Empty, subtype = Guid.Empty; ulong dimensions = 0; uint strideValue = 0;
                Trace("type.GetGUID.major", delegate { return type.GetGUID(ref MediaFoundationNative.MajorType, out major); });
                Trace("type.GetGUID.subtype", delegate { return type.GetGUID(ref MediaFoundationNative.Subtype, out subtype); });
                Trace("type.GetUINT64.frame_size", delegate { return type.GetUINT64(ref MediaFoundationNative.FrameSize, out dimensions); });
                int width = checked((int)(dimensions >> 32)), height = checked((int)(dimensions & 0xffffffff));
                int result = type.GetUINT32(ref MediaFoundationNative.DefaultStride, out strideValue), stride = unchecked((int)strideValue);
                Write("mf.call", "stage", "type.GetUINT32.default_stride", "hresult", Hex(result), "stride", stride);
                if (result < 0)
                    Trace("MFGetStrideForBitmapInfoHeader", delegate { return MediaFoundationNative.MFGetStrideForBitmapInfoHeader(0x16, checked((uint)width), out stride); });
                if (major != MediaFoundationNative.Video || subtype != MediaFoundationNative.Rgb32)
                    throw new NotSupportedException("Media Foundation did not negotiate uncompressed RGB32 video.");
                ValidateOutputSize(selectedNative, width, height);
                if (CameraResolution.IsHighResolution(width, height) &&
                    (requestedResolution == null || requestedResolution.Width != width || requestedResolution.Height != height))
                    throw new NotSupportedException("High-resolution Media Foundation output requires its exact explicit native request.");
                layout = new FrameLayout(width, height, stride);
                Width = width; Height = height;
                Write("mf.media_type", "camera_id", deviceId, "major", major, "subtype", subtype, "width", width, "height", height,
                    "stride", stride, "minimum_bytes", layout.MinimumBytes, "native_format_policy", NativePolicy, "output_format", "RGB32",
                    "selected_native_index", selectedNative == null ? -1 : (long)selectedNative.Index);
            }
            finally { Release(type); }
        }

        private void CopySample(NativeSample value)
        {
            MediaFoundationNative.IMFSample sample = null; MediaFoundationNative.IMFMediaBuffer buffer = null; bool locked = false;
            IntPtr bytes = IntPtr.Zero; uint maximum = 0, length = 0;
            byte[] copy = null; ManagedFrame owned = null;
            try
            {
                sample = (MediaFoundationNative.IMFSample)Marshal.GetObjectForIUnknown(value.Pointer);
                Check(sample.ConvertToContiguousBuffer(out buffer), "sample.ConvertToContiguousBuffer");
                Check(buffer.Lock(out bytes, out maximum, out length), "buffer.Lock"); locked = true;
                if (bytes == IntPtr.Zero || length > maximum || length > 32 * 1024 * 1024 || length < layout.MinimumBytes)
                    throw new InvalidOperationException("Media Foundation returned an invalid or truncated RGB32 video buffer.");
                copy = frameBuffers.Rent(layout.MinimumBytes); Marshal.Copy(bytes, copy, 0, copy.Length);
                long number = Interlocked.Increment(ref received);
                if (number == 1 || value.Flags != 0)
                    Write(number == 1 ? "mf.frame.first" : "mf.read.flags", "camera_id", deviceId, "sample", number, "callback_hresult", Hex(value.Status),
                        "flags", "0x" + value.Flags.ToString("X8"), "timestamp_100ns", value.Timestamp, "sample_bytes", length, "maximum_bytes", maximum,
                        "copied_bytes", copy.Length, "width", layout.Width, "height", layout.Height, "stride", layout.Stride);
                owned = new ManagedFrame(frameBuffers, copy, layout); copy = null;
                lock (frames)
                {
                    if (Volatile.Read(ref disposed) == 0)
                    {
                        if (latest != null) { Interlocked.Increment(ref replaced); latest.Dispose(); }
                        latest = owned; owned = null;
                    }
                }
                frameWake.Set();
            }
            finally
            {
                if (owned != null) owned.Dispose();
                if (copy != null) frameBuffers.Return(copy);
                if (locked) { int hr = buffer.Unlock(); if (hr < 0) Write("mf.call", "stage", "buffer.Unlock", "hresult", Hex(hr)); }
                Release(buffer); Release(sample);
            }
        }

        private void DeliverLoop()
        {
            while (true)
            {
                frameWake.WaitOne(100);
                ManagedFrame frame; Exception error;
                lock (frames) { frame = latest; latest = null; error = pendingError; pendingError = null; }
                if (error != null)
                {
                    Action<Exception> handler = OnError;
                    if (handler != null) try { handler(error); } catch (Exception nested) { LogError("mf.error.handler_failed", nested, "camera_id", deviceId); }
                }
                if (Volatile.Read(ref disposed) != 0) { if (frame != null) frame.Dispose(); return; }
                if (frame == null) continue;
                try { frame.Deliver(onFrame); Interlocked.Increment(ref delivered); }
                catch (Exception failure) { Report(failure, "frame_delivery"); }
            }
        }

        private void Report(Exception error, string stage)
        {
            CaptureFailure();
            LogError("mf.error", error, "camera_id", deviceId, "stage", stage, "samples", Interlocked.Read(ref received));
            lock (frames) { if (pendingError == null) pendingError = error; }
            frameWake.Set();
        }

        private void LogSummary()
        {
            long now = Stopwatch.GetTimestamp(), previous = Interlocked.Read(ref lastSummary);
            if (now - previous < 5 * Stopwatch.Frequency) return;
            Interlocked.Exchange(ref lastSummary, now);
            Write("mf.read.summary", "camera_id", deviceId, "samples", Interlocked.Read(ref received), "delivered_frames", Interlocked.Read(ref delivered),
                "replaced_frames", Interlocked.Read(ref replaced), "callback_events", Interlocked.Read(ref callbackEvents), "flush_callbacks", Interlocked.Read(ref callbackFlushes), "unexpected_callbacks", samples.Dropped,
                "frame_buffer_allocations", frameBuffers.Allocations, "frame_buffer_reuses", frameBuffers.Reuses,
                "retained_frame_buffers", frameBuffers.RetainedCount, "retained_frame_buffer_bytes", frameBuffers.RetainedBytes);
        }

        private static string Hex(int hr) { return "0x" + hr.ToString("X8"); }
        private string NativePolicy { get { return requestedResolution != null ? "requested_exact_then_highest_advertised" : configureHighestNative ? "highest_advertised_up_to_1080p" : "driver_default_compatibility"; } }
        private void CaptureFailure()
        {
            if (Interlocked.Exchange(ref failureSnapshot, 1) != 0) return;
            CaptureEnvironmentDiagnostics.Capture(deviceId, "mediafoundation_failure");
            HorizonCameraDiagnostics.Capture(deviceId, "mediafoundation_failure");
        }
        private object[] Context(object[] fields)
        {
            object[] result = new object[fields.Length + 4];
            result[0] = "capture_id"; result[1] = captureId; result[2] = "selected_camera_id"; result[3] = deviceId;
            Array.Copy(fields, 0, result, 4, fields.Length); return result;
        }
        private void Write(string name, params object[] fields) { DiagnosticLog.Write(name, Context(fields)); }
        private void LogError(string name, Exception error, params object[] fields) { DiagnosticLog.Error(name, error, Context(fields)); }
        private static void Check(int hr, string stage)
        { if (hr < 0) throw new COMException("Media Foundation call failed: " + stage + ".", hr); }
        private void Trace(string stage, Func<int> operation)
        {
            long before = Stopwatch.GetTimestamp();
            int hr;
            try { hr = operation(); }
            catch (Exception error) { LogError("mf.call.failed", error, "stage", stage, "elapsed_ms", (Stopwatch.GetTimestamp() - before) * 1000.0 / Stopwatch.Frequency); throw; }
            Write("mf.call", "stage", stage, "hresult", Hex(hr), "elapsed_ms", (Stopwatch.GetTimestamp() - before) * 1000.0 / Stopwatch.Frequency);
            Check(hr, stage);
        }
        private void TryClose(string stage, Func<int> operation)
        { try { Trace(stage, operation); } catch (Exception error) { LogError("mf.close.failed", error, "stage", stage); } }
        private static void Release(object value)
        { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Write("mf.close.requested", "camera_id", deviceId);
            frameBuffers.Close();
            lock (frames) { if (latest != null) { latest.Dispose(); latest = null; } }
            wake.Set(); frameWake.Set();
            var budget = Stopwatch.StartNew();
            if (Thread.CurrentThread != ownerThread && ownerThread.IsAlive) ownerThread.Join(2000);
            int remaining = Math.Max(0, 3000 - (int)budget.ElapsedMilliseconds);
            if (Thread.CurrentThread != deliveryThread && deliveryThread.IsAlive) deliveryThread.Join(remaining);
            Write("mf.close.wait_complete", "camera_id", deviceId, "owner_alive", ownerThread.IsAlive, "delivery_alive", deliveryThread.IsAlive,
                "late_read_callbacks", samples.Late, "unexpected_callbacks", samples.Dropped, "elapsed_ms", budget.ElapsedMilliseconds);
            // Wait handles remain valid for a late native callback/blocked driver.
            // Their SafeHandles are finalized when reader callbacks release this instance.
        }

        internal sealed class FrameLayout
        {
            internal readonly int Width, Height, Stride, MinimumBytes;
            internal FrameLayout(int width, int height, int stride)
            {
                if (!CameraResolution.IsSupportedDimensions(width, height)) throw new NotSupportedException("Unsupported camera dimensions; high-resolution modes are limited to2560x1440/3840x2160.");
                long pitch = Math.Abs((long)stride), row = checked(width * 4);
                long needed = checked(pitch * (height - 1) + row);
                if (pitch < row || pitch > 32 * 1024 * 1024 || needed > 32 * 1024 * 1024) throw new NotSupportedException("Invalid Media Foundation RGB32 stride.");
                Width = width; Height = height; Stride = stride; MinimumBytes = checked((int)needed);
            }
            internal Bitmap ToBitmap(byte[] bytes)
            {
                if (bytes == null || bytes.Length < MinimumBytes) throw new ArgumentException("Truncated RGB32 video frame.", "bytes");
                Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
                try
                {
                    BitmapData target = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        int pitch = checked((int)Math.Abs((long)Stride));
                        for (int y = 0; y < Height; y++)
                        {
                            int sourceRow = Stride > 0 ? y : Height - y - 1;
                            Marshal.Copy(bytes, checked(sourceRow * pitch), IntPtr.Add(target.Scan0, checked(y * target.Stride)), checked(Width * 4));
                        }
                    }
                    finally { bitmap.UnlockBits(target); }
                    return bitmap;
                }
                catch { bitmap.Dispose(); throw; }
            }
        }
        // Only managed snapshots are recycled. Native sample lifetime, the MTA
        // copy and the delivery-thread bitmap/callback lifetime stay unchanged.
        internal sealed class ManagedFrame : IDisposable
        {
            private readonly FrameBufferPool pool;
            internal readonly FrameLayout Layout;
            private byte[] bytes;
            internal ManagedFrame(FrameBufferPool owner, byte[] data, FrameLayout layout)
            { pool = owner; bytes = data; Layout = layout; }
            internal void Deliver(Action<Bitmap> handler)
            {
                try { using (Bitmap bitmap = Layout.ToBitmap(bytes)) handler(bitmap); }
                finally { Dispose(); }
            }
            public void Dispose()
            { byte[] previous = Interlocked.Exchange(ref bytes, null); if (previous != null) pool.Return(previous); }
        }

        internal sealed class FrameBufferPool
        {
            internal const int MaximumRetainedBytes = 32 * 1024 * 1024, MaximumRetainedCount = 3;
            private readonly object gate = new object();
            private readonly List<byte[]> available = new List<byte[]>();
            private int activeLength, retainedBytes;
            private long allocations, reuses;
            private bool closed;
            internal long Allocations { get { lock (gate) return allocations; } }
            internal long Reuses { get { lock (gate) return reuses; } }
            internal int RetainedCount { get { lock (gate) return available.Count; } }
            internal int RetainedBytes { get { lock (gate) return retainedBytes; } }
            internal byte[] Rent(int length)
            {
                if (length < 1 || length > MaximumRetainedBytes) throw new ArgumentOutOfRangeException("length");
                lock (gate)
                {
                    if (activeLength != length) { available.Clear(); retainedBytes = 0; activeLength = length; }
                    if (!closed && available.Count > 0)
                    {
                        int index = available.Count - 1; byte[] found = available[index];
                        available.RemoveAt(index); retainedBytes -= found.Length; reuses++; return found;
                    }
                    allocations++;
                }
                return new byte[length];
            }
            internal void Return(byte[] data)
            {
                if (data == null) return;
                lock (gate)
                {
                    if (closed || data.Length != activeLength || available.Count >= MaximumRetainedCount ||
                        data.Length > MaximumRetainedBytes - retainedBytes || available.Contains(data)) return;
                    available.Add(data); retainedBytes += data.Length;
                }
            }
            internal void Close()
            { lock (gate) { closed = true; available.Clear(); retainedBytes = 0; } }
        }
        internal sealed class NativeSample { internal int Status; internal uint Flags; internal long Timestamp; internal IntPtr Pointer; }
        internal sealed class SampleMailbox
        {
            private readonly object gate = new object();
            private readonly Func<IntPtr, int> retain;
            private bool accepting = true;
            private NativeSample pending;
            private long dropped, late;
            internal SampleMailbox(Func<IntPtr, int> retainSample) { retain = retainSample; }
            internal long Dropped { get { lock (gate) return dropped; } }
            internal long Late { get { lock (gate) return late; } }
            internal bool Post(int status, uint flags, long timestamp, IntPtr pointer)
            {
                lock (gate)
                {
                    if (!accepting) { late++; return false; }
                    if (pending != null) { dropped++; return false; }
                    // OnReadSample owns only a borrowed pointer. This one AddRef
                    // transfers its lifetime to the owner, which releases it there.
                    if (pointer != IntPtr.Zero) retain(pointer);
                    pending = new NativeSample { Status = status, Flags = flags, Timestamp = timestamp, Pointer = pointer };
                    return true;
                }
            }
            internal NativeSample Take() { lock (gate) { NativeSample result = pending; pending = null; return result; } }
            internal NativeSample Close() { lock (gate) { accepting = false; NativeSample result = pending; pending = null; return result; } }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class ReaderCallback : IMediaFoundationReaderCallback
        {
            private readonly SampleMailbox mailbox;
            private readonly Action signal, flushed, mediaEvent;
            private readonly Action<Exception> failed;
            internal ReaderCallback(MediaFoundationCapture owner)
                : this(owner.samples, delegate { owner.wake.Set(); }, delegate(Exception error) { owner.Report(error, "callback_retain"); },
                    delegate { Interlocked.Increment(ref owner.callbackFlushes); }, delegate { Interlocked.Increment(ref owner.callbackEvents); }) { }
            internal ReaderCallback(SampleMailbox target, Action notify, Action<Exception> failure, Action flush, Action onEvent)
            { mailbox = target; signal = notify; failed = failure; flushed = flush; mediaEvent = onEvent; }
            public int OnReadSample(int status, uint stream, uint flags, long timestamp, IntPtr sample)
            {
                try { if (mailbox.Post(status, flags, timestamp, sample)) signal(); return 0; }
                catch (Exception error) { failed(error); return error.HResult; }
            }
            public int OnFlush(uint stream) { flushed(); signal(); return 0; }
            public int OnEvent(uint stream, IntPtr value) { mediaEvent(); signal(); return 0; }
        }
    }

    [ComVisible(true), Guid("deec8d99-fa1d-4d82-84c2-2c8969944867"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMediaFoundationReaderCallback
    {
        [PreserveSig] int OnReadSample(int status, uint stream, uint flags, long timestamp, IntPtr sample);
        [PreserveSig] int OnFlush(uint stream);
        [PreserveSig] int OnEvent(uint stream, IntPtr mediaEvent);
    }

    internal static class MediaFoundationNative
    {
        internal static Guid SourceType = new Guid("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3"), VideoSource = new Guid("8ac3587a-4ae7-42d8-99e0-0a6013eef90f"), VideoSymbolicLink = new Guid("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
        internal static Guid AsyncCallback = new Guid("1e3dbeac-bb43-4c35-b507-cd644464c965"), VideoProcessing = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        internal static Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), FrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d"), DefaultStride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        internal static Guid FrameRate = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        internal static Guid CaptureCategory = new Guid("65e8773d-8f56-11d0-a3b9-00a0c9223196"), VideoCameraCategory = new Guid("e5323777-f976-4f5b-9b55-b94699c46e44");
        internal static Guid Video = new Guid("73646976-0000-0010-8000-00aa00389b71"), Rgb32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
        internal static Guid Yuy2 = new Guid("32595559-0000-0010-8000-00aa00389b71");

        [StructLayout(LayoutKind.Sequential)]
        internal struct DeviceInterfaceData { internal uint Size; internal Guid InterfaceClass; internal uint Flags; internal UIntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct DeviceInfoData { internal uint Size; internal Guid Class; internal uint DevInst; internal UIntPtr Reserved; }
        [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr parent);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInterfaceW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiOpenDeviceInterface(IntPtr set, string path, uint flags, ref DeviceInterfaceData data);
        [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceAlias(IntPtr set, ref DeviceInterfaceData data, ref Guid aliasClass, ref DeviceInterfaceData alias);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint bytes, out uint required, ref DeviceInfoData device);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfoData device, StringBuilder value, uint characters, out uint required);
        [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateSample(out IMFSample sample);
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMemoryBuffer(uint size, out IMFMediaBuffer buffer);
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFGetStrideForBitmapInfoHeader(uint format, uint width, out int stride);
        [DllImport("mf.dll", ExactSpelling = true)] internal static extern int MFEnumDeviceSources(IMFAttributes attributes, out IntPtr activateArray, out uint count);
        [DllImport("mfreadwrite.dll", ExactSpelling = true)] internal static extern int MFCreateSourceReaderFromMediaSource(IMFMediaSource source, IMFAttributes attributes, out IMFSourceReader reader);

        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFAttributes { 
            [PreserveSig] int GetItem([In] ref Guid key, IntPtr value);
            [PreserveSig] int GetItemType([In] ref Guid key, out int type);
            [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool same);
            [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes other, int match, [MarshalAs(UnmanagedType.Bool)] out bool same);
            [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
            [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
            [PreserveSig] int GetDouble([In] ref Guid key, out double value);
            [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
            [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
            [PreserveSig] int GetString([In] ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint capacity, out uint length);
            [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
            [PreserveSig] int GetBlobSize([In] ref Guid key, out uint size);
            [PreserveSig] int GetBlob([In] ref Guid key, IntPtr buffer, uint capacity, out uint size);
            [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
            [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid iid, out IntPtr value);
            [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
            [PreserveSig] int DeleteItem([In] ref Guid key);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
            [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
            [PreserveSig] int SetDouble([In] ref Guid key, double value);
            [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
            [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int SetBlob([In] ref Guid key, IntPtr buffer, uint size);
            [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
            [PreserveSig] int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes destination); }
        // Flatten inherited IMFAttributes slots explicitly: CLR COM inheritance
        // declarations must not shift the native vtable of derived interfaces.
        [ComImport, Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFActivate
        { 
            [PreserveSig] int GetItem([In] ref Guid key, IntPtr value);
            [PreserveSig] int GetItemType([In] ref Guid key, out int type);
            [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool same);
            [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes other, int match, [MarshalAs(UnmanagedType.Bool)] out bool same);
            [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
            [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
            [PreserveSig] int GetDouble([In] ref Guid key, out double value);
            [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
            [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
            [PreserveSig] int GetString([In] ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint capacity, out uint length);
            [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
            [PreserveSig] int GetBlobSize([In] ref Guid key, out uint size);
            [PreserveSig] int GetBlob([In] ref Guid key, IntPtr buffer, uint capacity, out uint size);
            [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
            [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid iid, out IntPtr value);
            [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
            [PreserveSig] int DeleteItem([In] ref Guid key);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
            [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
            [PreserveSig] int SetDouble([In] ref Guid key, double value);
            [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
            [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int SetBlob([In] ref Guid key, IntPtr buffer, uint size);
            [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
            [PreserveSig] int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes destination);
            [PreserveSig] int ActivateObject([In] ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
            [PreserveSig] int ShutdownObject();
            [PreserveSig] int DetachObject();
        }
        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFMediaType
        { 
            [PreserveSig] int GetItem([In] ref Guid key, IntPtr value);
            [PreserveSig] int GetItemType([In] ref Guid key, out int type);
            [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool same);
            [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes other, int match, [MarshalAs(UnmanagedType.Bool)] out bool same);
            [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
            [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
            [PreserveSig] int GetDouble([In] ref Guid key, out double value);
            [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
            [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
            [PreserveSig] int GetString([In] ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint capacity, out uint length);
            [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
            [PreserveSig] int GetBlobSize([In] ref Guid key, out uint size);
            [PreserveSig] int GetBlob([In] ref Guid key, IntPtr buffer, uint capacity, out uint size);
            [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
            [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid iid, out IntPtr value);
            [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
            [PreserveSig] int DeleteItem([In] ref Guid key);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
            [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
            [PreserveSig] int SetDouble([In] ref Guid key, double value);
            [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
            [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int SetBlob([In] ref Guid key, IntPtr buffer, uint size);
            [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
            [PreserveSig] int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes destination);
            [PreserveSig] int GetMajorType(out Guid type);
            [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool compressed);
            [PreserveSig] int IsEqual(IMFMediaType other, out uint flags);
            [PreserveSig] int GetRepresentation(Guid representation, out IntPtr value);
            [PreserveSig] int FreeRepresentation(Guid representation, IntPtr value);
        }
        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFSample
        { 
            [PreserveSig] int GetItem([In] ref Guid key, IntPtr value);
            [PreserveSig] int GetItemType([In] ref Guid key, out int type);
            [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool same);
            [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes other, int match, [MarshalAs(UnmanagedType.Bool)] out bool same);
            [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
            [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
            [PreserveSig] int GetDouble([In] ref Guid key, out double value);
            [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
            [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
            [PreserveSig] int GetString([In] ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint capacity, out uint length);
            [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
            [PreserveSig] int GetBlobSize([In] ref Guid key, out uint size);
            [PreserveSig] int GetBlob([In] ref Guid key, IntPtr buffer, uint capacity, out uint size);
            [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
            [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid iid, out IntPtr value);
            [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
            [PreserveSig] int DeleteItem([In] ref Guid key);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
            [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
            [PreserveSig] int SetDouble([In] ref Guid key, double value);
            [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
            [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int SetBlob([In] ref Guid key, IntPtr buffer, uint size);
            [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
            [PreserveSig] int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes destination);
            [PreserveSig] int GetSampleFlags(out uint flags);
            [PreserveSig] int SetSampleFlags(uint flags);
            [PreserveSig] int GetSampleTime(out long time);
            [PreserveSig] int SetSampleTime(long time);
            [PreserveSig] int GetSampleDuration(out long duration);
            [PreserveSig] int SetSampleDuration(long duration);
            [PreserveSig] int GetBufferCount(out uint count);
            [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
            [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
            [PreserveSig] int RemoveBufferByIndex(uint index);
            [PreserveSig] int RemoveAllBuffers();
            [PreserveSig] int GetTotalLength(out uint length);
            [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
        }
        [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr data, out uint maximumLength, out uint currentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out uint length);
            [PreserveSig] int SetCurrentLength(uint length);
            [PreserveSig] int GetMaxLength(out uint length);
        }
        [ComImport, Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFMediaSource
        {
            [PreserveSig] int GetEvent(uint flags, out IntPtr mediaEvent);
            [PreserveSig] int BeginGetEvent(IntPtr callback, IntPtr state);
            [PreserveSig] int EndGetEvent(IntPtr result, out IntPtr mediaEvent);
            [PreserveSig] int QueueEvent(uint type, [In] ref Guid extendedType, int status, IntPtr value);
            [PreserveSig] int GetCharacteristics(out uint flags);
            [PreserveSig] int CreatePresentationDescriptor(out IntPtr descriptor);
            [PreserveSig] int Start(IntPtr descriptor, [In] ref Guid timeFormat, IntPtr startPosition);
            [PreserveSig] int Stop();
            [PreserveSig] int Pause();
            [PreserveSig] int Shutdown();
        }
        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] out bool selected);
            [PreserveSig] int SetStreamSelection(int stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
            [PreserveSig] int GetNativeMediaType(int stream, uint index, out IMFMediaType type);
            [PreserveSig] int GetCurrentMediaType(int stream, out IMFMediaType type);
            [PreserveSig] int SetCurrentMediaType(int stream, IntPtr reserved, IMFMediaType type);
            [PreserveSig] int SetCurrentPosition([In] ref Guid timeFormat, IntPtr position);
            [PreserveSig] int ReadSample(int stream, uint flags, IntPtr actualStream, IntPtr streamFlags, IntPtr timestamp, IntPtr sample);
            [PreserveSig] int Flush(int stream);
            [PreserveSig] int GetServiceForStream(int stream, [In] ref Guid service, [In] ref Guid iid, out IntPtr value);
            [PreserveSig] int GetPresentationAttribute(int stream, [In] ref Guid key, IntPtr value);
        }
        [ComImport, Guid("7b981cf0-560e-4116-9875-b099895f23d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFSourceReaderEx
        {
            [PreserveSig] int GetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] out bool selected);
            [PreserveSig] int SetStreamSelection(int stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
            [PreserveSig] int GetNativeMediaType(int stream, uint index, out IMFMediaType type);
            [PreserveSig] int GetCurrentMediaType(int stream, out IMFMediaType type);
            [PreserveSig] int SetCurrentMediaType(int stream, IntPtr reserved, IMFMediaType type);
            [PreserveSig] int SetCurrentPosition([In] ref Guid timeFormat, IntPtr position);
            [PreserveSig] int ReadSample(int stream, uint flags, IntPtr actualStream, IntPtr streamFlags, IntPtr timestamp, IntPtr sample);
            [PreserveSig] int Flush(int stream);
            [PreserveSig] int GetServiceForStream(int stream, [In] ref Guid service, [In] ref Guid iid, out IntPtr value);
            [PreserveSig] int GetPresentationAttribute(int stream, [In] ref Guid key, IntPtr value);
            [PreserveSig] int SetNativeMediaType(int stream, IMFMediaType type, out uint flags);
            [PreserveSig] int AddTransformForStream(int stream, IntPtr transform);
            [PreserveSig] int RemoveAllTransformsForStream(int stream);
            [PreserveSig] int GetTransformForStream(int stream, uint index, IntPtr category, out IntPtr transform);
        }
    }
}
