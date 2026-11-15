using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace HorizonClient
{
    // An opt-in bitstream probe. There is deliberately no RGB, decoder, encoder,
    // RenderStream/Intelligent Connect, or fallback to a different camera.
    public sealed class H264SeiCapture : IDisposable
    {
        private const int MaximumQueuedBytes = 32 * 1024 * 1024;
        private const int MaximumQueuedSamples = 8;
        private readonly string deviceId;
        private readonly Action<byte[]> onSample;
        private readonly object queueGate = new object();
        private readonly Queue<byte[]> queue = new Queue<byte[]>();
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private readonly AutoResetEvent sampleReady = new AutoResetEvent(false);
        private Thread graphThread, sampleThread;
        private Exception openError, lastError;
        private int started, disposed, queuedBytes, graphFinished = 1, deliveryFinished = 1, startInProgress, handlesClosed;
        private long droppedSamples;

        public event Action<Exception> OnError;
        public H264NalFormat NalFormat { get; private set; }
        public int AvccLengthSize { get; private set; }
        public Guid ConnectedSubtype { get; private set; }
        public string ExposedFormats { get; private set; }
        public long DroppedSamples { get { return Interlocked.Read(ref droppedSamples); } }
        public Exception LastError { get { return Interlocked.CompareExchange(ref lastError, null, null); } }

        public H264SeiCapture(string deviceId, Action<byte[]> onSample)
        {
            if (String.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("An exact camera moniker is required.", "deviceId");
            if (onSample == null) throw new ArgumentNullException("onSample");
            this.deviceId = deviceId; this.onSample = onSample; AvccLengthSize = 4; ExposedFormats = "";
        }

        public void Start()
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("H264SeiCapture");
            if (Interlocked.CompareExchange(ref started, 1, 0) != 0) return;
            Interlocked.Exchange(ref startInProgress, 1);
            try
            {
                if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("H264SeiCapture");
                graphFinished = deliveryFinished = 0;
                sampleThread = new Thread(DeliverSamples) { IsBackground = true, Name = "H.264 SEI sample delivery" };
                sampleThread.Start();
                graphThread = new Thread(GraphLoop) { IsBackground = true, Name = "H.264 SEI graph owner" };
                graphThread.SetApartmentState(ApartmentState.MTA); graphThread.Start();
                if (!ready.WaitOne(15000)) { Dispose(); throw new TimeoutException("The native H.264 camera probe did not open within 15 seconds."); }
                if (openError != null) { Exception error = openError; Dispose(); throw new NotSupportedException(error.Message, error); }
                if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("H264SeiCapture");
            }
            finally { Interlocked.Exchange(ref startInProgress, 0); CloseHandlesIfFinished(); }
        }

        private void GraphLoop()
        {
            object graphObject = null, builderObject = null, sourceObject = null, grabberObject = null, rendererObject = null, configObject = null;
            DirectShow.IMediaControl control = null; DirectShow.ISampleGrabber grabber = null; Callback callback = null;
            IntPtr nativeType = IntPtr.Zero, sourcePin = IntPtr.Zero, grabberInput = IntPtr.Zero, grabberOutput = IntPtr.Zero, rendererInput = IntPtr.Zero;
            bool opened = false;
            try
            {
                graphObject = DirectShow.Create(DirectShow.FilterGraph); var graph = (DirectShow.IGraphBuilder)graphObject; control = (DirectShow.IMediaControl)graphObject;
                builderObject = DirectShow.Create(DirectShow.CaptureGraphBuilder2); var builder = (DirectShow.ICaptureGraphBuilder2)builderObject;
                DirectShow.Check(builder.SetFiltergraph(graph), "Set native H.264 graph");
                DirectShow.WithVideoMonikers(delegate(IMoniker moniker)
                {
                    string id; moniker.GetDisplayName(null, null, out id); if (!String.Equals(deviceId, id, StringComparison.Ordinal)) return false;
                    Guid filterId = typeof(DirectShow.IBaseFilter).GUID; moniker.BindToObject(null, null, ref filterId, out sourceObject); return true;
                });
                if (sourceObject == null) throw new NotSupportedException("The selected camera is not present.");
                var source = (DirectShow.IBaseFilter)sourceObject; DirectShow.Check(graph.AddFilter(source, "Native H.264 camera"), "Add exact camera");
                Guid category = DirectShow.PinCategoryCapture, video = DirectShow.MediaTypeVideo, configId = typeof(DirectShow.IAMStreamConfig).GUID;
                DirectShow.Check(builder.FindInterface(ref category, ref video, source, ref configId, out configObject), "Query camera native formats");
                var config = (DirectShow.IAMStreamConfig)configObject;
                nativeType = SelectNativeType(config);
                var selected = (DirectShow.AmMediaType)Marshal.PtrToStructure(nativeType, typeof(DirectShow.AmMediaType));
                DirectShow.Check(config.SetFormat(ref selected), "Select native H.264 format");
                DirectShow.Check(builder.FindPin(sourceObject, 1, ref category, ref video, true, 0, out sourcePin), "Find native camera capture output");

                grabberObject = DirectShow.Create(DirectShow.SampleGrabber); grabber = (DirectShow.ISampleGrabber)grabberObject;
                DirectShow.Check(grabber.SetMediaType(ref selected), "Require native H.264 sample type");
                DirectShow.Check(grabber.SetOneShot(false), "Set continuous native samples");
                DirectShow.Check(grabber.SetBufferSamples(false), "Disable extra native sample buffering");
                DirectShow.Check(graph.AddFilter((DirectShow.IBaseFilter)grabberObject, "Encoded H.264 samples"), "Add H.264 sample grabber");
                rendererObject = DirectShow.Create(DirectShow.NullRenderer); DirectShow.Check(graph.AddFilter((DirectShow.IBaseFilter)rendererObject, "Encoded H.264 sink"), "Add null sink");
                grabberInput = FindFilterPin((DirectShow.IBaseFilter)grabberObject, 0); grabberOutput = FindFilterPin((DirectShow.IBaseFilter)grabberObject, 1); rendererInput = FindFilterPin((DirectShow.IBaseFilter)rendererObject, 0);
                DirectShow.Check(graph.ConnectDirect(sourcePin, grabberInput, nativeType), "Connect native H.264 directly without decoder");
                DirectShow.Check(graph.ConnectDirect(grabberOutput, rendererInput, nativeType), "Connect encoded samples directly to sink");
                DirectShow.AmMediaType connected = new DirectShow.AmMediaType();
                try
                {
                    DirectShow.Check(grabber.GetConnectedMediaType(out connected), "Verify native H.264 connection");
                    H264NalFormat format; int lengthSize;
                    if (connected.MajorType != DirectShow.MediaTypeVideo || connected.SubType != selected.SubType || !TryGetNalFormat(connected, out format, out lengthSize))
                        throw new NotSupportedException("The camera did not expose the requested unchanged H.264 bitstream.");
                    ConnectedSubtype = connected.SubType; NalFormat = format; AvccLengthSize = lengthSize;
                }
                finally { DirectShow.FreeMediaType(ref connected); }
                callback = new Callback(this); DirectShow.Check(grabber.SetCallback(callback, 1), "Observe encoded camera samples");
                if (Volatile.Read(ref disposed) != 0) return;
                DirectShow.Check(control.Run(), "Start native H.264 camera probe"); opened = true; ready.Set();
                var mediaEvent = (DirectShow.IMediaEvent)graphObject;
                while (!stop.WaitOne(100))
                {
                    int code; IntPtr first, second;
                    for (int events = 0; events < 32 && mediaEvent.GetEvent(out code, out first, out second, 0) == 0; events++)
                    {
                        try { if (code == 3 || code == 8 || code == 0x45 || (code == 0x1F && second == IntPtr.Zero)) throw new InvalidOperationException("The encoded camera stream ended or disconnected."); }
                        finally { mediaEvent.FreeEventParams(code, first, second); }
                    }
                }
            }
            catch (Exception error) { if (!opened) openError = error; else ReportError(error); }
            finally
            {
                if (control != null) try { control.Stop(); } catch (COMException) { }
                if (grabber != null) try { grabber.SetCallback(null, 1); } catch (COMException) { }
                foreach (IntPtr pin in new[] { sourcePin, grabberInput, grabberOutput, rendererInput }) if (pin != IntPtr.Zero) Marshal.Release(pin);
                if (nativeType != IntPtr.Zero) DirectShow.FreeMediaTypePointer(nativeType);
                // FindInterface may return the same RCW as the source filter.
                if (configObject != null && Marshal.IsComObject(configObject)) Marshal.ReleaseComObject(configObject);
                DirectShow.Release(builderObject); DirectShow.Release(graphObject); DirectShow.Release(rendererObject); DirectShow.Release(grabberObject); DirectShow.Release(sourceObject);
                GC.KeepAlive(callback); ready.Set(); Interlocked.Exchange(ref graphFinished, 1); CloseHandlesIfFinished();
            }
        }

        private IntPtr SelectNativeType(DirectShow.IAMStreamConfig config)
        {
            int count, size; DirectShow.Check(config.GetNumberOfCapabilities(out count, out size), "Enumerate native camera formats");
            if (count < 1 || count > 4096 || size < 1 || size > 1024 * 1024) throw new NotSupportedException("The camera returned invalid native format capabilities.");
            IntPtr caps = Marshal.AllocCoTaskMem(size), chosen = IntPtr.Zero; var descriptions = new List<string>(); bool unsupportedH264 = false, unsupportedDimensions = false;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr pointer = IntPtr.Zero;
                    try
                    {
                        if (config.GetStreamCaps(i, out pointer, caps) < 0 || pointer == IntPtr.Zero) continue;
                        var type = (DirectShow.AmMediaType)Marshal.PtrToStructure(pointer, typeof(DirectShow.AmMediaType));
                        string description = type.SubType.ToString() + "/" + type.FormatType.ToString(); if (!descriptions.Contains(description) && descriptions.Count < 32) descriptions.Add(description);
                        H264NalFormat format; int lengthSize;
                        if (type.MajorType == DirectShow.MediaTypeVideo && IsH264Subtype(type.SubType))
                        {
                            if (TryGetNalFormat(type, out format, out lengthSize))
                            {
                                int width, height;
                                if (!TryGetClientDimensions(type, out width, out height))
                                {
                                    unsupportedDimensions = true;
                                    DiagnosticLog.Write("capture.h264.format.skipped", "camera_id", deviceId, "width", width, "height", height,
                                        "reason", "outside_1920x1080_or_unknown_dimensions", "subtype", type.SubType);
                                }
                                else if (chosen == IntPtr.Zero)
                                {
                                    DiagnosticLog.Write("capture.h264.format.selected", "camera_id", deviceId, "width", width, "height", height, "subtype", type.SubType);
                                    chosen = pointer; pointer = IntPtr.Zero;
                                }
                            }
                            else unsupportedH264 = true;
                        }
                    }
                    finally { if (pointer != IntPtr.Zero) DirectShow.FreeMediaTypePointer(pointer); }
                }
                ExposedFormats = String.Join(";", descriptions.ToArray());
                if (chosen == IntPtr.Zero) throw new NotSupportedException(unsupportedDimensions ? "Native H.264 capture requires a supported media header with dimensions at or below 1920x1080." : unsupportedH264 ? "Native H.264 exists, but its format is unsupported by the strict Sample Grabber probe (including VideoInfo2)." : "The selected camera does not expose native H.264. Encoded SEI is unavailable before RGB decoding.");
                IntPtr result = chosen; chosen = IntPtr.Zero; return result;
            }
            finally { Marshal.FreeCoTaskMem(caps); if (chosen != IntPtr.Zero) DirectShow.FreeMediaTypePointer(chosen); }
        }

        internal static bool TryGetClientDimensions(DirectShow.AmMediaType type, out int width, out int height)
        {
            // MPEG2VIDEOINFO starts with VIDEOINFOHEADER2. Reuse the existing
            // bounded header reader; dimensions are never inferred from SPS.
            if (type.FormatType == new Guid("E06D80E3-DB46-11CF-B4D1-00805F6CBBEA")) type.FormatType = DirectShow.FormatVideoInfo2;
            DirectShow.BitmapInfoHeader bitmap;
            width = height = 0;
            if (!DirectShow.TryGetBitmapHeader(type, out bitmap)) return false;
            width = bitmap.Width;
            if (bitmap.Height == Int32.MinValue) return false;
            height = Math.Abs(bitmap.Height);
            return bitmap.Size >= 40 && width >= 1 && width <= 1920 && height >= 1 && height <= 1080;
        }

        internal static bool IsH264Subtype(Guid subtype)
        {
            return subtype == FourCc("H264") || subtype == FourCc("h264") || subtype == FourCc("X264") || subtype == FourCc("x264") || subtype == FourCc("AVC1") || subtype == FourCc("avc1");
        }
        internal static bool TryGetNalFormat(DirectShow.AmMediaType type, out H264NalFormat format, out int lengthSize)
        {
            format = H264NalFormat.AnnexB; lengthSize = 4;
            if (type.MajorType != DirectShow.MediaTypeVideo || !IsH264Subtype(type.SubType) || type.FormatType == DirectShow.FormatVideoInfo2) return false;
            if (type.SubType == FourCc("AVC1") || type.SubType == FourCc("avc1"))
            {
                if (type.FormatType != new Guid("E06D80E3-DB46-11CF-B4D1-00805F6CBBEA") || type.FormatPointer == IntPtr.Zero || type.FormatSize < 132) return false;
                lengthSize = Marshal.ReadInt32(type.FormatPointer, 128); if (lengthSize != 1 && lengthSize != 2 && lengthSize != 4) return false; format = H264NalFormat.Avcc; return true;
            }
            return type.FormatType == Guid.Empty || type.FormatType == DirectShow.FormatVideoInfo || type.FormatType == new Guid("E06D80E3-DB46-11CF-B4D1-00805F6CBBEA");
        }
        private static Guid FourCc(string value) { return new Guid(((uint)value[0] | (uint)value[1] << 8 | (uint)value[2] << 16 | (uint)value[3] << 24).ToString("X8") + "-0000-0010-8000-00AA00389B71"); }

        private static IntPtr FindFilterPin(DirectShow.IBaseFilter filter, int direction)
        {
            IntPtr pointer; DirectShow.Check(filter.EnumPins(out pointer), "Enumerate H.264 graph pins"); object enumerator = null;
            try
            {
                enumerator = Marshal.GetObjectForIUnknown(pointer); var pins = new IPin[1];
                while (((IEnumPins)enumerator).Next(1, pins, IntPtr.Zero) == 0)
                {
                    try
                    {
                        int actual; DirectShow.Check(pins[0].QueryDirection(out actual), "Read pin direction");
                        if (actual == direction)
                        {
                            IntPtr unknown = Marshal.GetIUnknownForObject(pins[0]);
                            try { Guid pinId = typeof(IPin).GUID; IntPtr pin; DirectShow.Check(Marshal.QueryInterface(unknown, ref pinId, out pin), "Query native IPin"); return pin; }
                            finally { Marshal.Release(unknown); }
                        }
                    }
                    finally { DirectShow.Release(pins[0]); pins[0] = null; }
                }
                throw new NotSupportedException("The native H.264 graph has no matching pin.");
            }
            finally { DirectShow.Release(enumerator); if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
        }

        private void Receive(IntPtr buffer, int length)
        {
            if (Volatile.Read(ref disposed) != 0 || buffer == IntPtr.Zero || length < 1) return;
            if (length > H264Sei.MaximumSampleBytes) { Interlocked.Increment(ref droppedSamples); return; }
            byte[] sample = new byte[length]; Marshal.Copy(buffer, sample, 0, length);
            lock (queueGate)
            {
                if (Volatile.Read(ref disposed) != 0) return;
                while (queue.Count != 0 && (queue.Count >= MaximumQueuedSamples || queuedBytes + length > MaximumQueuedBytes)) { queuedBytes -= queue.Dequeue().Length; Interlocked.Increment(ref droppedSamples); }
                queue.Enqueue(sample); queuedBytes += sample.Length;
            }
            sampleReady.Set();
        }
        private void DeliverSamples()
        {
            try
            {
                while (Volatile.Read(ref disposed) == 0)
                {
                    byte[] sample = null; lock (queueGate) if (queue.Count != 0) { sample = queue.Dequeue(); queuedBytes -= sample.Length; }
                    if (sample == null) { sampleReady.WaitOne(100); continue; }
                    try { onSample(sample); } catch (Exception error) { ReportError(error); }
                }
            }
            finally { Interlocked.Exchange(ref deliveryFinished, 1); CloseHandlesIfFinished(); }
        }
        private void ReportError(Exception error) { Interlocked.Exchange(ref lastError, error); var handler = OnError; if (handler != null) try { handler(error); } catch { } }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return; stop.Set(); sampleReady.Set();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            foreach (Thread thread in new[] { graphThread, sampleThread }) if (thread != null && thread != Thread.CurrentThread && thread.IsAlive) thread.Join(Math.Max(0, 3000 - (int)elapsed.ElapsedMilliseconds));
            lock (queueGate) { queue.Clear(); queuedBytes = 0; } CloseHandlesIfFinished();
        }
        private void CloseHandlesIfFinished()
        {
            if (Volatile.Read(ref disposed) != 0 && Volatile.Read(ref graphFinished) != 0 && Volatile.Read(ref deliveryFinished) != 0 && Volatile.Read(ref startInProgress) == 0 && Interlocked.CompareExchange(ref handlesClosed, 1, 0) == 0) { stop.Dispose(); ready.Dispose(); sampleReady.Dispose(); }
        }
        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class Callback : CameraCapture.ISampleGrabberCallback
        {
            private readonly H264SeiCapture owner; internal Callback(H264SeiCapture owner) { this.owner = owner; }
            public int SampleCB(double time, IntPtr sample) { return 0; }
            public int BufferCB(double time, IntPtr buffer, int length) { try { owner.Receive(buffer, length); } catch (Exception error) { owner.ReportError(error); } return 0; }
        }
        [ComImport, Guid("56A86892-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumPins
        {
            [PreserveSig] int Next(int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IPin[] pins, IntPtr fetched);
            [PreserveSig] int Skip(int count); [PreserveSig] int Reset(); [PreserveSig] int Clone(out IEnumPins clone);
        }
        [ComImport, Guid("56A86891-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPin
        {
            [PreserveSig] int Connect(IntPtr receivePin, IntPtr mediaType); [PreserveSig] int ReceiveConnection(IntPtr connector, IntPtr mediaType);
            [PreserveSig] int Disconnect(); [PreserveSig] int ConnectedTo(out IntPtr pin); [PreserveSig] int ConnectionMediaType(out DirectShow.AmMediaType type);
            [PreserveSig] int QueryPinInfo(IntPtr info); [PreserveSig] int QueryDirection(out int direction);
        }
    }
}
