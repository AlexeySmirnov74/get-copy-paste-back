// DirectShow capture for Windows 10 / .NET Framework 4.7. No third-party assemblies.
// ABI references: strmif.h, control.h, qedit.h; AM_MEDIA_TYPE uses native packing.
// https://learn.microsoft.com/windows/win32/directshow/using-the-sample-grabber
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace HorizonClient
{
    // Immutable dimensions from one exact capture source, never a guessed mode.
    public sealed class CameraResolution
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        internal CameraResolution(int width, int height)
        {
            if (!IsSupportedDimensions(width, height)) throw new ArgumentOutOfRangeException("width", "Camera modes support up to1080p plus explicit2560x1440/3840x2160.");
            Width = width; Height = height;
        }
        internal static bool IsHighResolution(int width, int height)
        { return width == 2560 && height == 1440 || width == 3840 && height == 2160; }
        internal static bool IsSupportedDimensions(int width, int height)
        { return width > 0 && height > 0 && (width <= 1920 && height <= 1080 || IsHighResolution(width, height)); }
        internal bool Matches(CameraResolution other) { return other != null && Width == other.Width && Height == other.Height; }
        public override string ToString() { return Width + " × " + Height; }
        internal static CameraResolution[] Distinct(IEnumerable<CameraResolution> modes)
        {
            var result = new List<CameraResolution>();
            foreach (CameraResolution mode in modes)
            {
                if (mode == null) continue;
                bool exists = false; foreach (CameraResolution prior in result) if (mode.Matches(prior)) { exists = true; break; }
                if (!exists) result.Add(mode);
            }
            result.Sort(delegate(CameraResolution a, CameraResolution b) { int area = ((long)b.Width * b.Height).CompareTo((long)a.Width * a.Height); return area != 0 ? area : b.Width.CompareTo(a.Width); });
            return result.ToArray();
        }
    }

    internal interface IReceiverCaptureModes
    {
        CameraResolution[] AvailableResolutions { get; }
        CameraResolution CurrentResolution { get; }
    }

    internal enum CameraNativeFormatPolicy { DriverDefault, ExactSize, HighestOffered }

    // Identifies an asynchronous error reported by the running video graph.
    // Opening a device or changing its format can fail with the same HRESULT;
    // those errors must not be confused with a stream failing before frame one.
    internal sealed class CameraStreamException : COMException
    {
        internal readonly int EventCode;
        internal CameraStreamException(int eventCode, int errorCode)
            : base("The camera graph reported a streaming error.", errorCode) { EventCode = eventCode; }
    }

    internal sealed class CameraFormatException : COMException
    {
        internal readonly string Stage;
        internal readonly int RequestedWidth, RequestedHeight;
        internal CameraFormatException(string stage, int width, int height, int errorCode)
            : base("The camera did not accept the requested " + width + "x" + height + " format (" + stage + ").", errorCode)
        { Stage = stage; RequestedWidth = width; RequestedHeight = height; }
    }

    public sealed class CameraDevice
    {
        public string Name { get; private set; }
        public string Id { get; private set; }

        internal CameraDevice(string name, string id)
        {
            Name = name;
            Id = id;
        }

        public override string ToString() { return Name; }
    }

    public static class CameraDevices
    {
        /// <summary>Enumerates video inputs without opening any camera. An empty category is normal.</summary>
        public static List<CameraDevice> List()
        {
            List<CameraDevice> result = new List<CameraDevice>();
            DirectShow.WithVideoMonikers(delegate(IMoniker moniker)
            {
                string id;
                moniker.GetDisplayName(null, null, out id);
                object bag = null;
                string name = id;
                try
                {
                    Guid bagId = typeof(DirectShow.IPropertyBag).GUID;
                    moniker.BindToStorage(null, null, ref bagId, out bag);
                    object value;
                    if (((DirectShow.IPropertyBag)bag).Read("FriendlyName", out value, IntPtr.Zero) >= 0 && value != null)
                        name = Convert.ToString(value);
                }
                catch (COMException) { /* The moniker ID is still a usable label. */ }
                finally { DirectShow.Release(bag); }
                result.Add(new CameraDevice(name, id));
                return false;
            });
            return result;
        }
    }

    /// <summary>
    /// Captures the exact DirectShow moniker returned by CameraDevices.List().
    /// RGB24 conversion is negotiated by the graph. Public width/height specify exact native dimensions;
    /// the internal HighestOffered policy instead bounds selection to actual advertised dimensions.
    /// The selected native dimensions must be accepted and verified. An unavailable or changed
    /// format fails explicitly so the owner can choose a bounded compatibility mode.
    /// Automatic selection remains bounded to1920x1080. Explicit1440p/4K requests use only
    /// their actual advertised dimensions. Width/Height expose the negotiated size.
    /// onFrame runs serially on a background thread. Its detached Bitmap is valid only during that call;
    /// clone it to retain it. At most one latest frame waits while a handler is busy.
    /// Dispose prevents new deliveries and waits at most three seconds in total. An already executing
    /// handler may finish later; its Bitmap stays valid until it returns. A stalled camera driver keeps
    /// its COM objects on the background owner thread until the driver returns, avoiding unsafe release.
    /// </summary>
    public sealed class CameraCapture : IDisposable, IReceiverCaptureModes
    {
        private const int OpenTimeoutMilliseconds = 15000;
        private const int StartTimeoutMilliseconds = 10000;
        private readonly string deviceId;
        private readonly Action<Bitmap> onFrame;
        private readonly int preferredWidth;
        private readonly int preferredHeight;
        private readonly CameraNativeFormatPolicy nativeFormatPolicy;
        private readonly DirectShowCaptureDiagnostics trace;
        private readonly object frameGate = new object();
        private readonly object startGate = new object();
        private readonly AutoResetEvent graphWake = new AutoResetEvent(false);
        private readonly AutoResetEvent frameWake = new AutoResetEvent(false);
        private readonly ManualResetEvent graphReady = new ManualResetEvent(false);
        private readonly ManualResetEvent startDone = new ManualResetEvent(false);
        private Thread graphThread;
        private Thread frameThread;
        private Exception openError;
        private Exception startError;
        private Exception lastError;
        private Exception pendingError;
        private Frame pendingFrame;
        private FrameFormat format;
        private CameraResolution[] availableResolutions = new CameraResolution[0];
        public CameraResolution[] AvailableResolutions { get { return (CameraResolution[])availableResolutions.Clone(); } }
        public CameraResolution CurrentResolution { get { return Width > 0 && Height > 0 ? new CameraResolution(Width, Height) : null; } }
        private int disposed;
        private int started;
        private int startRequested;
        private int copyingFrame;
        private int badFrameReported;
        private int handlesClosed;
        private long diagnosticLast = Stopwatch.GetTimestamp();
        private long diagnosticSamples, diagnosticBytes, diagnosticBusy, diagnosticReplaced, diagnosticDelivered, diagnosticHandlerTicks, diagnosticMaximumTicks;
        private int diagnosticFirstSample;
        private long diagnosticLifetimeSamples;

        /// <summary>Raised on the frame delivery thread; a handler must return promptly.</summary>
        public event Action<Exception> OnError;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public Exception LastError { get { return Interlocked.CompareExchange(ref lastError, null, null); } }

        public CameraCapture(string deviceId, Action<Bitmap> onFrame, int width = 640, int height = 480)
            : this(deviceId, onFrame, width, height, true) { }

        internal CameraCapture(string deviceId, Action<Bitmap> onFrame, int width, int height, bool configureNativeFormat)
            : this(deviceId, onFrame, width, height, configureNativeFormat ? CameraNativeFormatPolicy.ExactSize : CameraNativeFormatPolicy.DriverDefault) { }

        internal CameraCapture(string deviceId, Action<Bitmap> onFrame, int width, int height, CameraNativeFormatPolicy nativeFormatPolicy)
        {
            if (String.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("Choose a camera moniker ID.", "deviceId");
            if (onFrame == null) throw new ArgumentNullException("onFrame");
            if (!CameraResolution.IsSupportedDimensions(width, height)) throw new ArgumentOutOfRangeException("width");
            if (nativeFormatPolicy < CameraNativeFormatPolicy.DriverDefault || nativeFormatPolicy > CameraNativeFormatPolicy.HighestOffered)
                throw new ArgumentOutOfRangeException("nativeFormatPolicy");
            this.deviceId = deviceId;
            this.onFrame = onFrame;
            preferredWidth = width;
            preferredHeight = height;
            this.nativeFormatPolicy = nativeFormatPolicy;
            trace = new DirectShowCaptureDiagnostics(deviceId);
            DiagnosticLog.Write("capture.open.requested", "camera_id", deviceId, "preferred_width", width, "preferred_height", height,
                "native_format_policy", nativeFormatPolicy == CameraNativeFormatPolicy.HighestOffered ? "highest_offered_bounded" :
                    nativeFormatPolicy == CameraNativeFormatPolicy.ExactSize ? "preferred" : "driver_default", "capture_id", trace.CaptureId,
                "trace_schema", 4);
            graphThread = new Thread(GraphLoop);
            graphThread.Name = "DirectShow graph owner";
            graphThread.IsBackground = true;
            graphThread.SetApartmentState(ApartmentState.MTA);
            graphThread.Start();
            if (!graphReady.WaitOne(OpenTimeoutMilliseconds))
            {
                Dispose();
                DiagnosticLog.Write("capture.open.timeout", "camera_id", deviceId);
                throw new TimeoutException("The camera driver did not finish opening within 15 seconds.");
            }
            if (openError != null)
            {
                Exception error = openError;
                Dispose();
                throw new InvalidOperationException("Unable to open the selected camera: " + error.Message, error);
            }
            frameThread = new Thread(FrameLoop);
            frameThread.Name = "Camera frame delivery";
            frameThread.IsBackground = true;
            frameThread.Start();
        }

        public void Start()
        {
            lock (startGate)
            {
                ThrowIfDisposed();
                if (Volatile.Read(ref started) != 0) return;
                startDone.Reset();
                startError = null;
                Interlocked.Exchange(ref startRequested, 1);
                graphWake.Set();
                if (!startDone.WaitOne(StartTimeoutMilliseconds))
                {
                    Dispose();
                    throw new TimeoutException("The camera driver did not start within 10 seconds.");
                }
                ThrowIfDisposed();
                if (startError != null)
                    throw new InvalidOperationException("Unable to start the selected camera: " + startError.Message, startError);
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("CameraCapture");
        }

        private void GraphLoop()
        {
            object graphObject = null;
            object builderObject = null;
            object sourceObject = null;
            object grabberObject = null;
            object rendererObject = null;
            DirectShow.ISampleGrabber grabber = null;
            DirectShow.IMediaControl control = null;
            GrabberCallback callback = null;
            bool ready = false;
            Size selectedSize = new Size(preferredWidth, preferredHeight);
            try
            {
                graphObject = trace.Create(DirectShow.FilterGraph);
                DirectShow.IGraphBuilder graph = (DirectShow.IGraphBuilder)graphObject;
                control = (DirectShow.IMediaControl)graphObject;
                DirectShow.IMediaEvent mediaEvent = (DirectShow.IMediaEvent)graphObject;
                builderObject = trace.Create(DirectShow.CaptureGraphBuilder2);
                DirectShow.ICaptureGraphBuilder2 builder = (DirectShow.ICaptureGraphBuilder2)builderObject;
                DirectShow.Check(trace.Call("ICaptureGraphBuilder2.SetFiltergraph", delegate { return builder.SetFiltergraph(graph); }), "Set capture graph");

                // Match the opaque moniker exactly, never a partial friendly name or first device.
                DirectShow.WithVideoMonikers(delegate(IMoniker moniker)
                {
                    string id;
                    moniker.GetDisplayName(null, null, out id);
                    if (!String.Equals(id, deviceId, StringComparison.Ordinal)) return false;
                    Guid filterId = typeof(DirectShow.IBaseFilter).GUID;
                    trace.Call("IMoniker.BindToObject(selected camera)", delegate { moniker.BindToObject(null, null, ref filterId, out sourceObject); return 0; });
                    return true;
                });
                if (sourceObject == null) throw new InvalidOperationException("The selected camera is no longer present.");
                DirectShow.IBaseFilter source = (DirectShow.IBaseFilter)sourceObject;
                DirectShow.Check(trace.Call("IGraphBuilder.AddFilter(camera)", delegate { return graph.AddFilter(source, "Selected camera"); }), "Add camera");
                trace.Capabilities(builder, source);
                availableResolutions = ReadSourceResolutions(builder, source);
                if (nativeFormatPolicy != CameraNativeFormatPolicy.DriverDefault)
                    selectedSize = TrySetNativeSize(builder, source, preferredWidth, preferredHeight,
                        nativeFormatPolicy == CameraNativeFormatPolicy.HighestOffered, trace);
                else DiagnosticLog.Write("capture.format.driver_default", "camera_id", deviceId,
                    "reason", "compatible_stream_retry_without_SetFormat");

                grabberObject = trace.Create(DirectShow.SampleGrabber);
                grabber = (DirectShow.ISampleGrabber)grabberObject;
                DirectShow.AmMediaType request = new DirectShow.AmMediaType();
                request.MajorType = DirectShow.MediaTypeVideo;
                request.SubType = DirectShow.MediaSubtypeRgb24;
                request.FormatType = DirectShow.FormatVideoInfo;
                DirectShow.Check(trace.Call("ISampleGrabber.SetMediaType(RGB24)", delegate { return grabber.SetMediaType(ref request); }), "Request uncompressed RGB24");
                DirectShow.Check(trace.Call("ISampleGrabber.SetOneShot(false)", delegate { return grabber.SetOneShot(false); }), "Set continuous capture");
                DirectShow.Check(trace.Call("ISampleGrabber.SetBufferSamples(true)", delegate { return grabber.SetBufferSamples(true); }), "Enable sample buffers");
                DirectShow.Check(trace.Call("IGraphBuilder.AddFilter(grabber)", delegate { return graph.AddFilter((DirectShow.IBaseFilter)grabberObject, "RGB24 frame grabber"); }), "Add sample grabber");

                rendererObject = trace.Create(DirectShow.NullRenderer);
                DirectShow.Check(trace.Call("IGraphBuilder.AddFilter(renderer)", delegate { return graph.AddFilter((DirectShow.IBaseFilter)rendererObject, "Null renderer"); }), "Add null renderer");
                Guid category = DirectShow.PinCategoryCapture;
                Guid mediaType = DirectShow.MediaTypeVideo;
                int connectionResult = trace.Call("ICaptureGraphBuilder2.RenderStream(capture)", delegate { return builder.RenderStream(ref category, ref mediaType, source,
                    (DirectShow.IBaseFilter)grabberObject, (DirectShow.IBaseFilter)rendererObject); });
                if (connectionResult < 0) throw new CameraFormatException("connect", selectedSize.Width, selectedSize.Height, connectionResult);

                DirectShow.AmMediaType connected = new DirectShow.AmMediaType();
                try
                {
                    DirectShow.Check(trace.Call("ISampleGrabber.GetConnectedMediaType", delegate { return grabber.GetConnectedMediaType(out connected); }), "Read connected video format");
                    trace.MediaType("grabber_connected", connected, -1, -1, "after_connect");
                    if (connected.MajorType != DirectShow.MediaTypeVideo || connected.SubType != DirectShow.MediaSubtypeRgb24)
                        throw new NotSupportedException("The graph did not negotiate uncompressed RGB24.");
                    format = FrameFormat.FromMediaType(connected);
                    Width = format.Width;
                    Height = format.Height;
                    ValidateConnectedDimensions(nativeFormatPolicy, selectedSize, new Size(Width, Height));
                    if (!CameraResolution.IsSupportedDimensions(Width, Height) || CameraResolution.IsHighResolution(Width, Height) &&
                        (nativeFormatPolicy == CameraNativeFormatPolicy.DriverDefault || Width != preferredWidth || Height != preferredHeight))
                        throw new NotSupportedException("High-resolution capture requires an exact explicitly requested2560x1440/3840x2160 mode.");
                    long average = connected.FormatPointer != IntPtr.Zero && connected.FormatSize >= 48 ? Marshal.ReadInt64(connected.FormatPointer, 40) : 0;
                    DiagnosticLog.Write("capture.format.connected", "camera_id", deviceId, "width", Width, "height", Height, "subtype", connected.SubType, "format", connected.FormatType, "fps", average > 0 ? 10000000.0 / average : 0, "stride", format.Stride, "frame_bytes", format.BufferSize, "bottom_up", format.BottomUp);
                }
                finally { DirectShow.FreeMediaType(ref connected); }
                callback = new GrabberCallback(this);
                DirectShow.Check(trace.Call("ISampleGrabber.SetCallback(BufferCB)", delegate { return grabber.SetCallback(callback, 1); }), "Register frame callback");
                trace.Snapshot(graph, "before_run");
                ready = true;
                graphReady.Set();

                while (Volatile.Read(ref disposed) == 0)
                {
                    if (Interlocked.Exchange(ref startRequested, 0) != 0)
                    {
                        try
                        {
                            int runResult = trace.Call("IMediaControl.Run", delegate { return control.Run(); });
                            DirectShow.Check(runResult, "Run capture graph");
                            Interlocked.Exchange(ref started, 1);
                            int state = 0; int stateResult = trace.Call("IMediaControl.GetState(0)", delegate { return control.GetState(0, out state); });
                            DiagnosticLog.Write("capture.graph.run_accepted", "camera_id", deviceId, "width", Width, "height", Height,
                                "run_hresult", "0x" + runResult.ToString("X8"), "state_hresult", "0x" + stateResult.ToString("X8"), "state", state,
                                "transition_pending", runResult != 0 || stateResult != 0 || state != 2, "awaiting_first_sample", Interlocked.Read(ref diagnosticLifetimeSamples) == 0,
                                "capture_id", trace.CaptureId);
                            trace.Snapshot(graph, "after_run");
                        }
                        catch (Exception error) { startError = error; ReportError(error); }
                        finally { startDone.Set(); }
                    }
                    PollEvents(mediaEvent, graph);
                    LogCaptureSummary(false);
                    graphWake.WaitOne(100);
                }
            }
            catch (Exception error)
            {
                DiagnosticLog.Error("capture.graph.failed", error, "camera_id", deviceId, "ready", ready, "capture_id", trace.CaptureId);
                trace.Snapshot(graphObject as DirectShow.IGraphBuilder, "open_failure");
                CaptureEnvironmentDiagnostics.Capture(deviceId, "directshow_open_failure");
                HorizonCameraDiagnostics.Capture(deviceId, "directshow_open_failure");
                if (!ready) openError = error;
                else ReportError(error);
            }
            finally
            {
                // Every COM reference remains on this owner thread, including during a slow Stop.
                // BufferCB never touches a COM object and queued frames own their managed bytes.
                if (control != null)
                {
                    try { DirectShow.Check(trace.Call("IMediaControl.Stop", delegate { return control.Stop(); }), "Stop capture graph"); }
                    catch (Exception error) { ReportError(error); }
                }
                if (grabber != null)
                {
                    try { trace.Call("ISampleGrabber.SetCallback(null)", delegate { return grabber.SetCallback(null, 1); }); }
                    catch (COMException) { }
                }
                DirectShow.Release(builderObject);
                DirectShow.Release(graphObject);
                DirectShow.Release(rendererObject);
                DirectShow.Release(grabberObject);
                DirectShow.Release(sourceObject);
                GC.KeepAlive(callback);
                LogCaptureSummary(true);
                DiagnosticLog.Write("capture.graph.closed", "camera_id", deviceId, "capture_id", trace.CaptureId,
                    "lifetime_callbacks", Interlocked.Read(ref diagnosticLifetimeSamples));
                if (!ready) graphReady.Set();
                if (startError == null && Volatile.Read(ref started) == 0)
                    startError = openError ?? new ObjectDisposedException("CameraCapture");
                startDone.Set();
            }
        }

        private static Size TrySetNativeSize(DirectShow.ICaptureGraphBuilder2 builder,
            DirectShow.IBaseFilter source, int width, int height, bool highestOffered, DirectShowCaptureDiagnostics trace)
        {
            object configObject = null;
            try
            {
                Guid category = DirectShow.PinCategoryCapture;
                Guid media = DirectShow.MediaTypeVideo;
                Guid configId = typeof(DirectShow.IAMStreamConfig).GUID;
                int result = trace.Call("ICaptureGraphBuilder2.FindInterface(IAMStreamConfig)", delegate { return builder.FindInterface(ref category, ref media, source, ref configId, out configObject); });
                if (result < 0) throw new CameraFormatException("query_stream_config", width, height, result);
                DirectShow.IAMStreamConfig config = configObject as DirectShow.IAMStreamConfig;
                if (config == null) throw new CameraFormatException("query_stream_config", width, height, unchecked((int)0x80004002));
                return ConfigureNativeFormatCore(config, width, height, highestOffered, trace);
            }
            finally
            {
                if (configObject != null && Marshal.IsComObject(configObject)) Marshal.ReleaseComObject(configObject);
            }
        }

        private CameraResolution[] ReadSourceResolutions(DirectShow.ICaptureGraphBuilder2 builder, DirectShow.IBaseFilter source)
        {
            object config = null;
            try
            {
                Guid category = DirectShow.PinCategoryCapture, media = DirectShow.MediaTypeVideo, iid = typeof(DirectShow.IAMStreamConfig).GUID;
                DirectShow.Check(builder.FindInterface(ref category, ref media, source, ref iid, out config), "Read source resolution catalog");
                CameraResolution[] modes = EnumerateResolutions((DirectShow.IAMStreamConfig)config);
                foreach (CameraResolution mode in modes) DiagnosticLog.Write("capture.mode.available", "camera_id", deviceId, "width", mode.Width, "height", mode.Height);
                return modes;
            }
            catch (Exception error) { DiagnosticLog.Error("capture.modes.enumeration_failed", error, "camera_id", deviceId); return new CameraResolution[0]; }
            finally { if (config != null && Marshal.IsComObject(config)) Marshal.ReleaseComObject(config); }
        }

        internal static CameraResolution[] EnumerateResolutions(DirectShow.IAMStreamConfig config)
        {
            int count, size; DirectShow.Check(config.GetNumberOfCapabilities(out count, out size), "Read resolution capability count");
            if (count < 0 || count > 4096 || size < 1 || size > 1048576) throw new NotSupportedException("Invalid camera capability bounds.");
            IntPtr caps = Marshal.AllocCoTaskMem(size);
            var modes = new List<CameraResolution>();
            try
            {
                for (int index = 0; index < count; index++)
                {
                    IntPtr pointer = IntPtr.Zero;
                    try
                    {
                        if (config.GetStreamCaps(index, out pointer, caps) < 0 || pointer == IntPtr.Zero) continue;
                        var type = (DirectShow.AmMediaType)Marshal.PtrToStructure(pointer, typeof(DirectShow.AmMediaType));
                        DirectShow.BitmapInfoHeader bitmap;
                        if (type.MajorType != DirectShow.MediaTypeVideo || !DirectShow.TryGetBitmapHeader(type, out bitmap) || bitmap.Height == Int32.MinValue) continue;
                        int height = Math.Abs(bitmap.Height);
                        if (CameraResolution.IsSupportedDimensions(bitmap.Width, height)) modes.Add(new CameraResolution(bitmap.Width, height));
                    }
                    finally { DirectShow.FreeMediaTypePointer(pointer); }
                }
            }
            finally { Marshal.FreeCoTaskMem(caps); }
            return CameraResolution.Distinct(modes);
        }

        // Separate from COM activation so exact negotiation and driver readback
        // are verifiable with fake capabilities, without opening a camera.
        internal static void ConfigureNativeFormat(DirectShow.IAMStreamConfig config, int width, int height, DirectShowCaptureDiagnostics trace = null)
        { ConfigureNativeFormatCore(config, width, height, false, trace); }

        internal static Size ConfigureHighestNativeFormat(DirectShow.IAMStreamConfig config, int maximumWidth, int maximumHeight, DirectShowCaptureDiagnostics trace = null)
        { return ConfigureNativeFormatCore(config, maximumWidth, maximumHeight, true, trace); }

        internal static void ValidateConnectedDimensions(CameraNativeFormatPolicy policy, Size selected, Size connected)
        {
            if (policy != CameraNativeFormatPolicy.DriverDefault && connected != selected)
                throw new CameraFormatException("connected_size_changed", selected.Width, selected.Height, unchecked((int)0x8004022A));
        }

        private static Size ConfigureNativeFormatCore(DirectShow.IAMStreamConfig config, int width, int height, bool highestOffered, DirectShowCaptureDiagnostics trace)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (!CameraResolution.IsSupportedDimensions(width, height)) throw new ArgumentOutOfRangeException("width");
            IntPtr caps = IntPtr.Zero, selected = IntPtr.Zero, actual = IntPtr.Zero;
            long selectedArea = 0;
            int selectedPreference = -1;
            try
            {
                int count = 0, capsSize = 0;
                int result = trace == null ? config.GetNumberOfCapabilities(out count, out capsSize) :
                    trace.Call("IAMStreamConfig.GetNumberOfCapabilities", delegate { return config.GetNumberOfCapabilities(out count, out capsSize); });
                if (result < 0) throw new CameraFormatException("enumerate_capabilities", width, height, result);
                if (count <= 0 || count > 4096 || capsSize <= 0 || capsSize > 1048576)
                    throw new CameraFormatException("invalid_capability_bounds", width, height, unchecked((int)0x80070057));
                caps = Marshal.AllocCoTaskMem(capsSize);
                for (int index = 0; index < count; index++)
                {
                    IntPtr typePointer = IntPtr.Zero;
                    try
                    {
                        if (config.GetStreamCaps(index, out typePointer, caps) < 0 || typePointer == IntPtr.Zero) continue;
                        DirectShow.AmMediaType type = (DirectShow.AmMediaType)Marshal.PtrToStructure(typePointer, typeof(DirectShow.AmMediaType));
                        DirectShow.BitmapInfoHeader bitmap;
                        if (type.MajorType != DirectShow.MediaTypeVideo || !DirectShow.TryGetBitmapHeader(type, out bitmap) ||
                            bitmap.Width < 1 || bitmap.Height == 0 || bitmap.Height == Int32.MinValue) continue;
                        int candidateHeight = Math.Abs(bitmap.Height);
                        if (!CameraResolution.IsSupportedDimensions(bitmap.Width, candidateHeight) ||
                            CameraResolution.IsHighResolution(bitmap.Width, candidateHeight) && (bitmap.Width != width || candidateHeight != height) ||
                            (highestOffered ? bitmap.Width > width || candidateHeight > height : bitmap.Width != width || candidateHeight != height)) continue;
                        bool rgb = type.SubType == DirectShow.MediaSubtypeRgb24;
                        long area = (long)bitmap.Width * candidateHeight;
                        // RenderStream still has to prove conversion into RGB24. A failed
                        // YUY2 graph is retired before the separate driver-default retry.
                        int preference = type.SubType == DirectShow.MediaSubtypeYuy2 ? 2 : rgb ? 1 : 0;
                        if (selected == IntPtr.Zero || (highestOffered ? area > selectedArea || area == selectedArea && preference > selectedPreference : rgb))
                        {
                            DirectShow.FreeMediaTypePointer(selected);
                            selected = typePointer;
                            selectedArea = area;
                            selectedPreference = preference;
                            typePointer = IntPtr.Zero;
                            if (!highestOffered && rgb) break;
                        }
                    }
                    finally { DirectShow.FreeMediaTypePointer(typePointer); }
                }
                if (selected == IntPtr.Zero)
                {
                    DiagnosticLog.Write("capture.format.source_unavailable", "reason", highestOffered ? "no_offered_mode_within_bounds" : "exact_requested_mode_unavailable", "preferred_width", width, "preferred_height", height);
                    throw new CameraFormatException(highestOffered ? "bounded_mode_unavailable" : "exact_mode_unavailable", width, height, unchecked((int)0x8004022A));
                }
                DirectShow.AmMediaType chosen = (DirectShow.AmMediaType)Marshal.PtrToStructure(selected, typeof(DirectShow.AmMediaType));
                DirectShow.BitmapInfoHeader chosenBitmap;
                if (!DirectShow.TryGetBitmapHeader(chosen, out chosenBitmap)) throw new CameraFormatException("selected_mode_invalid", width, height, unchecked((int)0x8004022A));
                width = chosenBitmap.Width; height = Math.Abs(chosenBitmap.Height);
                long average = chosen.FormatPointer != IntPtr.Zero && chosen.FormatSize >= 48 ? Marshal.ReadInt64(chosen.FormatPointer, 40) : 0;
                result = trace == null ? config.SetFormat(ref chosen) : trace.Call("IAMStreamConfig.SetFormat", delegate { return config.SetFormat(ref chosen); });
                DiagnosticLog.Write("capture.format.source_selected", "native_format_policy", highestOffered ? "highest_offered_bounded" : "exact", "width", width, "height", height, "subtype", chosen.SubType, "format", chosen.FormatType,
                    "fps", average > 0 ? 10000000.0 / average : 0, "set_format_hresult", "0x" + result.ToString("X8"));
                if (result < 0) throw new CameraFormatException("set_format", width, height, result);
                result = trace == null ? config.GetFormat(out actual) : trace.Call("IAMStreamConfig.GetFormat(readback)", delegate { return config.GetFormat(out actual); });
                if (result < 0) throw new CameraFormatException("get_format", width, height, result);
                if (actual == IntPtr.Zero) throw new CameraFormatException("get_format_empty", width, height, unchecked((int)0x8004022A));
                DirectShow.AmMediaType actualType = (DirectShow.AmMediaType)Marshal.PtrToStructure(actual, typeof(DirectShow.AmMediaType));
                DirectShow.BitmapInfoHeader actualBitmap;
                bool readable = DirectShow.TryGetBitmapHeader(actualType, out actualBitmap);
                int actualHeight = actualBitmap.Height == Int32.MinValue ? 0 : Math.Abs(actualBitmap.Height);
                long actualAverage = actualType.FormatPointer != IntPtr.Zero && actualType.FormatSize >= 48 ? Marshal.ReadInt64(actualType.FormatPointer, 40) : 0;
                DiagnosticLog.Write("capture.format.source_actual", "requested_width", width, "requested_height", height,
                    "width", readable ? actualBitmap.Width : 0, "height", actualHeight, "subtype", actualType.SubType, "format", actualType.FormatType,
                    "fps", actualAverage > 0 ? 10000000.0 / actualAverage : 0, "get_format_hresult", "0x" + result.ToString("X8"));
                if (!readable || actualType.MajorType != DirectShow.MediaTypeVideo || actualBitmap.Width != width || actualHeight != height)
                    throw new CameraFormatException("driver_changed_dimensions", width, height, unchecked((int)0x8004022A));
                return new Size(width, height);
            }
            finally
            {
                DirectShow.FreeMediaTypePointer(selected);
                DirectShow.FreeMediaTypePointer(actual);
                if (caps != IntPtr.Zero) Marshal.FreeCoTaskMem(caps);
            }
        }

        private void PollEvents(DirectShow.IMediaEvent mediaEvent, DirectShow.IGraphBuilder graph)
        {
            int code;
            IntPtr first, second;
            // Drain only a bounded number per iteration to keep shutdown responsive.
            for (int index = 0; index < 32 && mediaEvent.GetEvent(out code, out first, out second, 0) == 0; index++)
            {
                try
                {
                    bool failed = code == 3 || code == 8 || code == 0x45;
                    int eventError = unchecked((int)first.ToInt64());
                    DiagnosticLog.Write("capture.graph.event.raw", "capture_id", trace.CaptureId, "camera_id", deviceId,
                        "event_code", code, "event_name", DirectShowCaptureDiagnostics.EventName(code),
                        "param1", "0x" + first.ToInt64().ToString("X"), "param2", "0x" + second.ToInt64().ToString("X"),
                        "error_description", failed ? DirectShowCaptureDiagnostics.Describe(eventError) : null,
                        "lifetime_callbacks", Interlocked.Read(ref diagnosticLifetimeSamples));
                    if (code == 3 || code == 8 || code == 0x45) // EC_ERRORABORT / EC_ERROR_STILLPLAYING / EC_ERRORABORTEX
                    {
                        int streamError = unchecked((int)first.ToInt64());
                        DiagnosticLog.Write("capture.graph.event", "camera_id", deviceId, "event_code", code,
                            "stream_hresult", "0x" + streamError.ToString("X8"), "started", Volatile.Read(ref started) != 0,
                            "sample_callbacks", Interlocked.Read(ref diagnosticSamples), "delivered_frames", Interlocked.Read(ref diagnosticDelivered));
                        trace.Snapshot(graph, "event_" + DirectShowCaptureDiagnostics.EventName(code));
                        CaptureEnvironmentDiagnostics.Capture(deviceId, "directshow_stream_failure");
                        HorizonCameraDiagnostics.Capture(deviceId, "directshow_stream_failure");
                        ReportError(new CameraStreamException(code, streamError));
                    }
                    else if (code == 0x1f && second == IntPtr.Zero) // EC_DEVICE_LOST, removed
                        ReportError(new InvalidOperationException("The selected camera was disconnected."));
                }
                finally
                {
                    int freeHr = mediaEvent.FreeEventParams(code, first, second);
                    DiagnosticLog.Write("capture.graph.event.freed", "capture_id", trace.CaptureId, "event_code", code,
                        "hresult", DirectShowCaptureDiagnostics.Hex(freeHr));
                }
            }
        }

        private void ReceiveBuffer(IntPtr buffer, int length, double sampleTime)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            bool diagnostics = DiagnosticLog.IsEnabled;
            if (diagnostics)
            {
                long sample = Interlocked.Increment(ref diagnosticLifetimeSamples);
                Interlocked.Increment(ref diagnosticSamples); Interlocked.Add(ref diagnosticBytes, length);
                if (sample <= 8) DiagnosticLog.Write("capture.sample.raw", "capture_id", trace.CaptureId, "sample_number", sample,
                    "sample_time_seconds", sampleTime, "buffer_bytes", length, "buffer_is_null", buffer == IntPtr.Zero,
                    "native_thread", DirectShowCaptureDiagnostics.NativeThreadId);
            }
            if (Interlocked.CompareExchange(ref copyingFrame, 1, 0) != 0) { if (diagnostics) Interlocked.Increment(ref diagnosticBusy); return; }
            try
            {
                FrameFormat currentFormat = format;
                if (currentFormat == null) return;
                if (buffer == IntPtr.Zero || length < currentFormat.BufferSize)
                    throw new InvalidOperationException("The camera delivered an incomplete RGB24 frame.");
                if (diagnostics && Interlocked.Exchange(ref diagnosticFirstSample, 1) == 0)
                    DiagnosticLog.Write("capture.sample.first", "camera_id", deviceId, "callback_bytes", length, "expected_bytes", currentFormat.BufferSize, "width", currentFormat.Width, "height", currentFormat.Height);
                byte[] bytes = new byte[currentFormat.BufferSize];
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                lock (frameGate)
                {
                    if (Volatile.Read(ref disposed) != 0) return;
                    if (diagnostics && pendingFrame != null) Interlocked.Increment(ref diagnosticReplaced);
                    pendingFrame = new Frame(bytes, currentFormat);
                }
                Interlocked.Exchange(ref badFrameReported, 0);
                frameWake.Set();
            }
            catch (Exception error)
            {
                if (Interlocked.Exchange(ref badFrameReported, 1) == 0) ReportError(error);
            }
            finally { Interlocked.Exchange(ref copyingFrame, 0); }
        }

        private void FrameLoop()
        {
            while (Volatile.Read(ref disposed) == 0)
            {
                frameWake.WaitOne();
                if (Volatile.Read(ref disposed) != 0) break;
                Exception error = Interlocked.Exchange(ref pendingError, null);
                if (error != null) DeliverError(error);
                Frame frame;
                lock (frameGate)
                {
                    frame = pendingFrame;
                    pendingFrame = null;
                }
                if (frame == null || Volatile.Read(ref disposed) != 0) continue;
                try
                {
                    using (Bitmap bitmap = frame.ToBitmap())
                    {
                        if (Volatile.Read(ref disposed) == 0)
                        {
                            long began = Stopwatch.GetTimestamp();
                            onFrame(bitmap);
                            if (DiagnosticLog.IsEnabled)
                            {
                                long elapsed = Stopwatch.GetTimestamp() - began;
                                Interlocked.Increment(ref diagnosticDelivered); Interlocked.Add(ref diagnosticHandlerTicks, elapsed);
                                long previous;
                                do { previous = Interlocked.Read(ref diagnosticMaximumTicks); if (elapsed <= previous) break; } while (Interlocked.CompareExchange(ref diagnosticMaximumTicks, elapsed, previous) != previous);
                            }
                        }
                    }
                }
                catch (Exception frameError) { ReportError(frameError); }
            }
        }

        private void ReportError(Exception error)
        {
            DiagnosticLog.Error("capture.error", error, "camera_id", deviceId);
            Interlocked.Exchange(ref lastError, error);
            if (Volatile.Read(ref disposed) != 0) return;
            Interlocked.Exchange(ref pendingError, error);
            frameWake.Set();
        }

        private void DeliverError(Exception error)
        {
            Action<Exception> handlers = OnError;
            if (handlers == null) return;
            foreach (Action<Exception> handler in handlers.GetInvocationList())
            {
                if (Volatile.Read(ref disposed) != 0) return;
                try { handler(error); }
                catch (Exception) { /* Error handlers cannot tear down the capture worker. */ }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (frameGate) { pendingFrame = null; }
            graphWake.Set();
            frameWake.Set();
            startDone.Set();
            // The graph may be stuck in a third-party driver; do not abort it or release its COM
            // pointers from another thread. A user callback may likewise be in arbitrary code.
            if (graphThread != null && Thread.CurrentThread != graphThread) graphThread.Join(2000);
            if (frameThread != null && Thread.CurrentThread != frameThread) frameThread.Join(1000);
            DiagnosticLog.Write("capture.disposed", "camera_id", deviceId, "graph_thread_alive", graphThread != null && graphThread.IsAlive, "delivery_thread_alive", frameThread != null && frameThread.IsAlive);
            TryCloseHandles();
        }

        private void LogCaptureSummary(bool force)
        {
            if (!DiagnosticLog.IsEnabled) return;
            long now = Stopwatch.GetTimestamp(), elapsed = now - diagnosticLast;
            if (!force && elapsed < Stopwatch.Frequency) return;
            diagnosticLast = now;
            long delivered = Interlocked.Exchange(ref diagnosticDelivered, 0), ticks = Interlocked.Exchange(ref diagnosticHandlerTicks, 0);
            DiagnosticLog.Write("capture.summary", "camera_id", deviceId, "width", Width, "height", Height, "interval_ms", elapsed * 1000.0 / Stopwatch.Frequency,
                "sample_callbacks", Interlocked.Exchange(ref diagnosticSamples, 0), "sample_bytes", Interlocked.Exchange(ref diagnosticBytes, 0),
                "copy_busy_drops", Interlocked.Exchange(ref diagnosticBusy, 0), "latest_frame_replacements", Interlocked.Exchange(ref diagnosticReplaced, 0),
                "delivered_frames", delivered, "handler_mean_ms", delivered == 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency / delivered,
                "handler_max_ms", Interlocked.Exchange(ref diagnosticMaximumTicks, 0) * 1000.0 / Stopwatch.Frequency, "final", force);
        }

        private void TryCloseHandles()
        {
            if ((graphThread != null && graphThread.IsAlive) || (frameThread != null && frameThread.IsAlive)) return;
            if (!Monitor.TryEnter(startGate)) return;
            try
            {
                if (Interlocked.Exchange(ref handlesClosed, 1) != 0) return;
                graphWake.Close();
                frameWake.Close();
                graphReady.Close();
                startDone.Close();
            }
            finally { Monitor.Exit(startGate); }
        }

        // A CCW interface must be publicly visible, even though its implementation is private.
        [ComVisible(true), Guid("0579154A-2B53-4994-B0D0-E773148EFF85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ISampleGrabberCallback
        {
            [PreserveSig] int SampleCB(double sampleTime, IntPtr sample);
            [PreserveSig] int BufferCB(double sampleTime, IntPtr buffer, int length);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class GrabberCallback : ISampleGrabberCallback
        {
            private readonly CameraCapture capture;
            internal GrabberCallback(CameraCapture capture) { this.capture = capture; }
            public int SampleCB(double sampleTime, IntPtr sample) { return 0; }
            public int BufferCB(double sampleTime, IntPtr buffer, int length)
            {
                capture.ReceiveBuffer(buffer, length, sampleTime);
                return 0;
            }
        }

        internal sealed class FrameFormat
        {
            internal readonly int Width;
            internal readonly int Height;
            internal readonly int Stride;
            internal readonly int BufferSize;
            internal readonly bool BottomUp;

            internal FrameFormat(int width, int height, int imageSize)
            {
                if (width < 1 || width > 32768 || height == 0 || height == Int32.MinValue || Math.Abs(height) > 32768)
                    throw new NotSupportedException("The camera supplied invalid video dimensions.");
                Width = width;
                Height = Math.Abs(height);
                BottomUp = height > 0;
                Stride = checked((width * 3 + 3) & ~3);
                // Some RGB sources advertise a larger pitch in biSizeImage.
                if (imageSize > 0 && imageSize % Height == 0 && imageSize / Height >= Stride && (imageSize / Height) % 4 == 0)
                    Stride = imageSize / Height;
                BufferSize = checked(Stride * Height);
                if (BufferSize > 256 * 1024 * 1024) throw new NotSupportedException("The camera frame exceeds 256 MiB.");
            }

            internal static FrameFormat FromMediaType(DirectShow.AmMediaType type)
            {
                DirectShow.BitmapInfoHeader bitmap;
                if (!DirectShow.TryGetBitmapHeader(type, out bitmap) || bitmap.Size < 40 || bitmap.Planes != 1 ||
                    bitmap.BitCount != 24 || bitmap.Compression != 0)
                    throw new NotSupportedException("The camera must negotiate a valid uncompressed RGB24 VIDEOINFOHEADER.");
                return new FrameFormat(bitmap.Width, bitmap.Height, bitmap.ImageSize);
            }
        }

        internal sealed class Frame
        {
            private readonly byte[] bytes;
            private readonly FrameFormat format;
            internal Frame(byte[] bytes, FrameFormat format) { this.bytes = bytes; this.format = format; }

            internal Bitmap ToBitmap()
            {
                Bitmap bitmap = new Bitmap(format.Width, format.Height, PixelFormat.Format24bppRgb);
                try
                {
                    BitmapData data = bitmap.LockBits(new Rectangle(0, 0, format.Width, format.Height),
                        ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                    try
                    {
                        for (int y = 0; y < format.Height; y++)
                        {
                            int sourceY = format.BottomUp ? format.Height - 1 - y : y;
                            Marshal.Copy(bytes, sourceY * format.Stride, IntPtr.Add(data.Scan0, y * data.Stride), format.Width * 3);
                        }
                    }
                    finally { bitmap.UnlockBits(data); }
                    return bitmap;
                }
                catch { bitmap.Dispose(); throw; }
            }
        }
    }

    internal static class DirectShow
    {
        internal static readonly Guid SystemDeviceEnum = new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
        internal static readonly Guid VideoInputDeviceCategory = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
        internal static readonly Guid FilterGraph = new Guid("E436EBB3-524F-11CE-9F53-0020AF0BA770");
        internal static readonly Guid CaptureGraphBuilder2 = new Guid("BF87B6E1-8C27-11D0-B3F0-00AA003761C5");
        internal static readonly Guid SampleGrabber = new Guid("C1F400A0-3F08-11D3-9F0B-006008039E37");
        internal static readonly Guid NullRenderer = new Guid("C1F400A4-3F08-11D3-9F0B-006008039E37");
        internal static readonly Guid MediaTypeVideo = new Guid("73646976-0000-0010-8000-00AA00389B71");
        internal static readonly Guid MediaSubtypeRgb24 = new Guid("E436EB7D-524F-11CE-9F53-0020AF0BA770");
        internal static readonly Guid MediaSubtypeYuy2 = new Guid("32595559-0000-0010-8000-00AA00389B71");
        internal static readonly Guid FormatVideoInfo = new Guid("05589F80-C356-11CE-BF01-00AA0055595A");
        internal static readonly Guid FormatVideoInfo2 = new Guid("F72A76A0-EB0A-11D0-ACE4-0000C0CC16BA");
        internal static readonly Guid PinCategoryCapture = new Guid("FB6C4281-0353-11D1-905F-0000C0CC16BA");

        internal static object Create(Guid classId) { return Activator.CreateInstance(Type.GetTypeFromCLSID(classId, true)); }
        internal static void Check(int result, string operation)
        {
            if (result < 0) throw new COMException(operation + " failed (0x" + result.ToString("X8") + ").", result);
        }
        internal static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
        internal static void WithVideoMonikers(Func<IMoniker, bool> visit)
        {
            object deviceEnum = null;
            IEnumMoniker enumerator = null;
            try
            {
                deviceEnum = Create(SystemDeviceEnum);
                Guid category = VideoInputDeviceCategory;
                int result = ((ICreateDevEnum)deviceEnum).CreateClassEnumerator(ref category, out enumerator, 0);
                Check(result, "Enumerate cameras");
                if (result != 0 || enumerator == null) return;
                IMoniker[] monikers = new IMoniker[1];
                while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
                {
                    try { if (visit(monikers[0])) break; }
                    finally { Release(monikers[0]); monikers[0] = null; }
                }
            }
            finally { Release(enumerator); Release(deviceEnum); }
        }

        internal static bool TryGetBitmapHeader(AmMediaType type, out BitmapInfoHeader bitmap)
        {
            bitmap = new BitmapInfoHeader();
            int offset;
            if (type.FormatType == FormatVideoInfo) offset = 48;
            else if (type.FormatType == FormatVideoInfo2) offset = 72;
            else return false;
            if (type.FormatPointer == IntPtr.Zero || type.FormatSize < offset + 40) return false;
            bitmap = (BitmapInfoHeader)Marshal.PtrToStructure(IntPtr.Add(type.FormatPointer, offset), typeof(BitmapInfoHeader));
            return true;
        }

        internal static void FreeMediaType(ref AmMediaType type)
        {
            if (type.FormatPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(type.FormatPointer);
            if (type.Unknown != IntPtr.Zero) Marshal.Release(type.Unknown);
            type.FormatPointer = IntPtr.Zero;
            type.Unknown = IntPtr.Zero;
            type.FormatSize = 0;
        }

        internal static void FreeMediaTypePointer(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return;
            AmMediaType type = (AmMediaType)Marshal.PtrToStructure(pointer, typeof(AmMediaType));
            FreeMediaType(ref type);
            Marshal.FreeCoTaskMem(pointer);
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AmMediaType
        {
            internal Guid MajorType;
            internal Guid SubType;
            [MarshalAs(UnmanagedType.Bool)] internal bool FixedSizeSamples;
            [MarshalAs(UnmanagedType.Bool)] internal bool TemporalCompression;
            internal int SampleSize;
            internal Guid FormatType;
            internal IntPtr Unknown;
            internal int FormatSize;
            internal IntPtr FormatPointer;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfoHeader
        {
            internal int Size;
            internal int Width;
            internal int Height;
            internal short Planes;
            internal short BitCount;
            internal int Compression;
            internal int ImageSize;
            internal int XPelsPerMeter;
            internal int YPelsPerMeter;
            internal int ColorsUsed;
            internal int ColorsImportant;
        }

        [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ICreateDevEnum
        {
            [PreserveSig] int CreateClassEnumerator([In] ref Guid category, out IEnumMoniker enumerator, int flags);
        }

        [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPropertyBag
        {
            [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string property,
                [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr errorLog);
            [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string property,
                [MarshalAs(UnmanagedType.Struct)] ref object value);
        }

        // Methods inherited from IPersist and IMediaFilter are repeated in native vtable order.
        [ComImport, Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IBaseFilter
        {
            [PreserveSig] int GetClassID(out Guid classId);
            [PreserveSig] int Stop();
            [PreserveSig] int Pause();
            [PreserveSig] int Run(long start);
            [PreserveSig] int GetState(int timeout, out int state);
            [PreserveSig] int SetSyncSource(IntPtr clock);
            [PreserveSig] int GetSyncSource(out IntPtr clock);
            [PreserveSig] int EnumPins(out IntPtr enumerator);
            [PreserveSig] int FindPin([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr pin);
            [PreserveSig] int QueryFilterInfo(IntPtr info);
            [PreserveSig] int JoinFilterGraph([MarshalAs(UnmanagedType.Interface)] IGraphBuilder graph, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int QueryVendorInfo([MarshalAs(UnmanagedType.LPWStr)] out string vendor);
        }

        [ComImport, Guid("56A868A9-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IGraphBuilder
        {
            [PreserveSig] int AddFilter([MarshalAs(UnmanagedType.Interface)] IBaseFilter filter, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int RemoveFilter([MarshalAs(UnmanagedType.Interface)] IBaseFilter filter);
            [PreserveSig] int EnumFilters(out IntPtr enumerator);
            [PreserveSig] int FindFilterByName([MarshalAs(UnmanagedType.LPWStr)] string name, out IBaseFilter filter);
            [PreserveSig] int ConnectDirect(IntPtr output, IntPtr input, IntPtr mediaType);
            [PreserveSig] int Reconnect(IntPtr pin);
            [PreserveSig] int Disconnect(IntPtr pin);
            [PreserveSig] int SetDefaultSyncSource();
            [PreserveSig] int Connect(IntPtr output, IntPtr input);
            [PreserveSig] int Render(IntPtr pin);
            [PreserveSig] int RenderFile([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.LPWStr)] string playList);
            [PreserveSig] int AddSourceFilter([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.LPWStr)] string name, out IBaseFilter filter);
            [PreserveSig] int SetLogFile(IntPtr file);
            [PreserveSig] int Abort();
            [PreserveSig] int ShouldOperationContinue();
        }

        [ComImport, Guid("93E5A4E0-2D50-11D2-ABFA-00A0C9C6E38D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ICaptureGraphBuilder2
        {
            [PreserveSig] int SetFiltergraph([MarshalAs(UnmanagedType.Interface)] IGraphBuilder graph);
            [PreserveSig] int GetFiltergraph(out IGraphBuilder graph);
            [PreserveSig] int SetOutputFileName([In] ref Guid type, [MarshalAs(UnmanagedType.LPWStr)] string file, out IBaseFilter filter, out IntPtr sink);
            [PreserveSig] int FindInterface([In] ref Guid category, [In] ref Guid mediaType, [MarshalAs(UnmanagedType.Interface)] IBaseFilter filter,
                [In] ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object result);
            [PreserveSig] int RenderStream([In] ref Guid category, [In] ref Guid mediaType,
                [MarshalAs(UnmanagedType.IUnknown)] object source, [MarshalAs(UnmanagedType.Interface)] IBaseFilter intermediate,
                [MarshalAs(UnmanagedType.Interface)] IBaseFilter sink);
            [PreserveSig] int ControlStream([In] ref Guid category, [In] ref Guid mediaType,
                [MarshalAs(UnmanagedType.Interface)] IBaseFilter filter, IntPtr start, IntPtr stop, short startCookie, short stopCookie);
            [PreserveSig] int AllocCapFile([MarshalAs(UnmanagedType.LPWStr)] string file, long size);
            [PreserveSig] int CopyCaptureFile([MarshalAs(UnmanagedType.LPWStr)] string oldFile, [MarshalAs(UnmanagedType.LPWStr)] string newFile,
                [MarshalAs(UnmanagedType.Bool)] bool allowEscape, IntPtr progress);
            [PreserveSig] int FindPin([MarshalAs(UnmanagedType.IUnknown)] object source, int direction, [In] ref Guid category,
                [In] ref Guid mediaType, [MarshalAs(UnmanagedType.Bool)] bool unconnected, int index, out IntPtr pin);
        }

        [ComImport, Guid("6B652FFF-11FE-4FCE-92AD-0266B5D7C78F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ISampleGrabber
        {
            [PreserveSig] int SetOneShot([MarshalAs(UnmanagedType.Bool)] bool oneShot);
            [PreserveSig] int SetMediaType([In] ref AmMediaType type);
            [PreserveSig] int GetConnectedMediaType(out AmMediaType type);
            [PreserveSig] int SetBufferSamples([MarshalAs(UnmanagedType.Bool)] bool bufferSamples);
            [PreserveSig] int GetCurrentBuffer(ref int size, IntPtr buffer);
            [PreserveSig] int GetCurrentSample(out IntPtr sample);
            [PreserveSig] int SetCallback([MarshalAs(UnmanagedType.Interface)] CameraCapture.ISampleGrabberCallback callback, int method);
        }

        [ComImport, Guid("C6E13340-30AC-11D0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAMStreamConfig
        {
            [PreserveSig] int SetFormat([In] ref AmMediaType type);
            [PreserveSig] int GetFormat(out IntPtr type);
            [PreserveSig] int GetNumberOfCapabilities(out int count, out int size);
            [PreserveSig] int GetStreamCaps(int index, out IntPtr type, IntPtr caps);
        }

        [ComImport, Guid("56A868B1-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface IMediaControl
        {
            [PreserveSig] int Run();
            [PreserveSig] int Pause();
            [PreserveSig] int Stop();
            [PreserveSig] int GetState(int timeout, out int state);
            [PreserveSig] int RenderFile([MarshalAs(UnmanagedType.BStr)] string file);
            [PreserveSig] int AddSourceFilter([MarshalAs(UnmanagedType.BStr)] string file, [MarshalAs(UnmanagedType.IDispatch)] out object filter);
            [PreserveSig] int get_FilterCollection([MarshalAs(UnmanagedType.IDispatch)] out object collection);
            [PreserveSig] int get_RegFilterCollection([MarshalAs(UnmanagedType.IDispatch)] out object collection);
            [PreserveSig] int StopWhenReady();
        }

        [ComImport, Guid("56A868B6-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface IMediaEvent
        {
            [PreserveSig] int GetEventHandle(out IntPtr handle);
            [PreserveSig] int GetEvent(out int code, out IntPtr first, out IntPtr second, int timeout);
            [PreserveSig] int WaitForCompletion(int timeout, out int code);
            [PreserveSig] int CancelDefaultHandling(int code);
            [PreserveSig] int RestoreDefaultHandling(int code);
            [PreserveSig] int FreeEventParams(int code, IntPtr first, IntPtr second);
        }
    }
}
