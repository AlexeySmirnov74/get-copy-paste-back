// Build this file with src/CameraCapture.cs and /define:CAPTURE_TEST_MAIN for a standalone test.
// No test binds to a camera or enables any physical camera capture.
using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using HorizonClient;

internal static class CaptureTests
{
    internal static void RunAll()
    {
        NativePacking();
        RgbOrientationAndPadding();
        MediaHeaderValidation();
        ExactNativeNegotiation();
        HighestNativeNegotiation();
        AvailableResolutionCatalog();
        CallbackComVisibility();
        EmptyGraphInterop();
        ArgumentValidation();
        Console.WriteLine("Capture tests passed (ABI, RGB rows/stride, format validation, COM callback, empty graph). No camera opened.");
    }

#if CAPTURE_TEST_MAIN
    private static int Main()
    {
        try { RunAll(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
#endif

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void NativePacking()
    {
        Assert(Marshal.SizeOf(typeof(DirectShow.BitmapInfoHeader)) == 40, "BITMAPINFOHEADER must be 40 bytes.");
        Assert(Marshal.SizeOf(typeof(DirectShow.AmMediaType)) == (IntPtr.Size == 8 ? 88 : 72), "AM_MEDIA_TYPE native size.");
        Assert(Marshal.OffsetOf(typeof(DirectShow.AmMediaType), "FormatType").ToInt32() == 44, "AM_MEDIA_TYPE GUID alignment.");
        Assert(Marshal.OffsetOf(typeof(DirectShow.AmMediaType), "Unknown").ToInt32() == (IntPtr.Size == 8 ? 64 : 60), "AM_MEDIA_TYPE IUnknown alignment.");
        Assert(Marshal.OffsetOf(typeof(DirectShow.AmMediaType), "FormatPointer").ToInt32() == (IntPtr.Size == 8 ? 80 : 68), "AM_MEDIA_TYPE format pointer alignment.");
    }

    private static void RgbOrientationAndPadding()
    {
        // Three pixels require 9 bytes, with 3 bytes of DIB padding per row.
        byte[] top = { 0, 0, 255, 0, 255, 0, 255, 0, 0, 91, 92, 93 };
        byte[] bottom = { 255, 255, 255, 0, 0, 0, 0, 255, 255, 81, 82, 83 };
        byte[] bytes = new byte[24];
        Buffer.BlockCopy(bottom, 0, bytes, 0, 12);
        Buffer.BlockCopy(top, 0, bytes, 12, 12);
        using (Bitmap bitmap = new CameraCapture.Frame(bytes, new CameraCapture.FrameFormat(3, 2, 24)).ToBitmap())
        {
            Assert(bitmap.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb(), "Bottom-up top row.");
            Assert(bitmap.GetPixel(2, 0).ToArgb() == Color.Blue.ToArgb(), "BGR to RGB pixel interpretation.");
            Assert(bitmap.GetPixel(0, 1).ToArgb() == Color.White.ToArgb(), "Bottom-up second row.");
            Assert(bitmap.GetPixel(2, 1).ToArgb() == Color.Yellow.ToArgb(), "Bottom-up right edge.");
        }
        // Negative height and an explicitly padded 16-byte native stride.
        byte[] widePitch = new byte[32];
        Buffer.BlockCopy(top, 0, widePitch, 0, 9);
        Buffer.BlockCopy(bottom, 0, widePitch, 16, 9);
        using (Bitmap bitmap = new CameraCapture.Frame(widePitch, new CameraCapture.FrameFormat(3, -2, 32)).ToBitmap())
        {
            Assert(bitmap.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb(), "Top-down first row.");
            Assert(bitmap.GetPixel(2, 1).ToArgb() == Color.Yellow.ToArgb(), "Native pitch respected.");
        }
    }

    private static void MediaHeaderValidation()
    {
        DirectShow.AmMediaType type = new DirectShow.AmMediaType();
        type.FormatType = DirectShow.FormatVideoInfo;
        type.FormatSize = 88;
        type.FormatPointer = Marshal.AllocCoTaskMem(88);
        try
        {
            byte[] zeros = new byte[88];
            Marshal.Copy(zeros, 0, type.FormatPointer, 88);
            DirectShow.BitmapInfoHeader header = new DirectShow.BitmapInfoHeader();
            header.Size = 40;
            header.Width = 3;
            header.Height = -2;
            header.Planes = 1;
            header.BitCount = 24;
            header.ImageSize = 24;
            Marshal.StructureToPtr(header, IntPtr.Add(type.FormatPointer, 48), false);
            CameraCapture.FrameFormat format = CameraCapture.FrameFormat.FromMediaType(type);
            Assert(format.Width == 3 && format.Height == 2 && format.Stride == 12 && !format.BottomUp, "VIDEOINFOHEADER fields.");
            type.FormatSize = 87;
            bool rejected = false;
            try { CameraCapture.FrameFormat.FromMediaType(type); }
            catch (NotSupportedException) { rejected = true; }
            Assert(rejected, "Reject truncated native format block before dereferencing.");
            type.FormatSize = 88;
            header.BitCount = 32;
            Marshal.StructureToPtr(header, IntPtr.Add(type.FormatPointer, 48), false);
            rejected = false;
            try { CameraCapture.FrameFormat.FromMediaType(type); }
            catch (NotSupportedException) { rejected = true; }
            Assert(rejected, "Reject non-RGB24 data.");
        }
        finally { DirectShow.FreeMediaType(ref type); }
        Assert(type.FormatPointer == IntPtr.Zero && type.FormatSize == 0, "Freed media format clears its pointer.");
    }

    private static void CallbackComVisibility()
    {
        Type callbackType = typeof(CameraCapture).GetNestedType("GrabberCallback", BindingFlags.NonPublic);
        object callback = Activator.CreateInstance(callbackType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { null }, null);
        IntPtr callbackInterface = Marshal.GetComInterfaceForObject(callback, typeof(CameraCapture.ISampleGrabberCallback));
        try
        {
            Guid id = typeof(CameraCapture.ISampleGrabberCallback).GUID;
            IntPtr queried;
            int result = Marshal.QueryInterface(callbackInterface, ref id, out queried);
            try { Assert(result == 0 && queried != IntPtr.Zero, "Managed callback must expose ISampleGrabberCB to native filters."); }
            finally { if (queried != IntPtr.Zero) Marshal.Release(queried); }
        }
        finally { Marshal.Release(callbackInterface); }
    }

    private static void ExactNativeNegotiation()
    {
        var config = new FakeStreamConfig(new NativeMode(1920, 1080, true), new NativeMode(1280, 720, false),
            new NativeMode(640, 480, true), new NativeMode(1280, -720, true));
        CameraCapture.ConfigureNativeFormat(config, 1280, 720);
        Assert(config.SetCalls == 1 && config.SetWidth == 1280 && config.SetHeight == 720 && config.SelectedSubtype == DirectShow.MediaSubtypeRgb24 && config.GetCalls == 1,
            "Exact requested mode wins over advertised1080p and its driver readback is verified.");
        config = new FakeStreamConfig(new NativeMode(1920, 1080, true), new NativeMode(640, 480, true));
        ExpectFormatFailure(config, "exact_mode_unavailable", unchecked((int)0x8004022A));
        Assert(config.SetCalls == 0 && config.GetCalls == 0, "Missing720p never silently continues with1080p or a nearest format.");
        CameraCapture.ConfigureNativeFormat(config, 640, 480);
        Assert(config.SetWidth == 640 && config.SetHeight == 480, "An explicit bounded640p retry negotiates exactly640x480.");
        config = new FakeStreamConfig(new NativeMode(1280, 720, true)) { SetError = unchecked((int)0x8004022A) };
        ExpectFormatFailure(config, "set_format", config.SetError);
        Assert(config.GetCalls == 0, "A failed SetFormat cannot be mistaken for a successful start using the default mode.");
        config = new FakeStreamConfig(new NativeMode(1280, 720, true)) { ActualWidth = 1920, ActualHeight = 1080 };
        ExpectFormatFailure(config, "driver_changed_dimensions", unchecked((int)0x8004022A));
        Assert(config.GetCalls == 1, "A driver returning1080p after accepting720p is detected before graph startup.");
        config = new FakeStreamConfig(new NativeMode(1280, 720, true)) { GetError = unchecked((int)0x80004005) };
        ExpectFormatFailure(config, "get_format", config.GetError);
        config = new FakeStreamConfig(new NativeMode(1280, 720, true)) { ActualEmpty = true };
        ExpectFormatFailure(config, "get_format_empty", unchecked((int)0x8004022A));
    }

    private static void ExpectFormatFailure(FakeStreamConfig config, string stage, int error)
    {
        try { CameraCapture.ConfigureNativeFormat(config, 1280, 720); }
        catch (CameraFormatException failure)
        {
            Assert(failure.Stage == stage && failure.HResult == error && failure.RequestedWidth == 1280 && failure.RequestedHeight == 720,
                "A format negotiation failure preserves its stage, requested size and original HRESULT.");
            return;
        }
        throw new Exception("Expected format negotiation failure at " + stage);
    }

    private static void HighestNativeNegotiation()
    {
        var config = new FakeStreamConfig(new NativeMode(320, 240, true), new NativeMode(1920, -1080, true),
            new NativeMode(2560, 1440, false), new NativeMode(1280, 720, false));
        Size selected = CameraCapture.ConfigureHighestNativeFormat(config, 1920, 1080);
        Assert(selected == new Size(1920, 1080) && config.SetWidth == 1920 && config.SetHeight == 1080 && config.SetCalls == 1 && config.GetCalls == 1,
            "Highest selection enumerates all real caps, accepts top-down1080p and excludes oversized modes without resizing.");
        config = new FakeStreamConfig(new NativeMode(640, 480, true));
        selected = CameraCapture.ConfigureHighestNativeFormat(config, 1920, 1080);
        Assert(selected == new Size(640, 480) && config.SetWidth == 640 && config.SetHeight == 480,
            "A640p-only source stays640p instead of fabricating720p or1080p.");
        config = new FakeStreamConfig(new NativeMode(1920, 1080, true), new NativeMode(1920, 1080, false), new NativeMode(1920, 1080, true));
        CameraCapture.ConfigureHighestNativeFormat(config, 1920, 1080);
        Assert(config.SelectedSubtype == DirectShow.MediaSubtypeYuy2,
            "Equal-size YUY2 is preferred, subject to the later real RGB24 RenderStream negotiation.");
        config = new FakeStreamConfig(new NativeMode(1280, 720, true), new NativeMode(1920, 1080, true));
        selected = CameraCapture.ConfigureHighestNativeFormat(config, 1280, 720);
        Assert(selected == new Size(1280, 720), "Caller bounds are respected even when1080p is offered.");
        config = new FakeStreamConfig(new NativeMode(1280, 1024, false), new NativeMode(1600, 900, true));
        selected = CameraCapture.ConfigureHighestNativeFormat(config, 1920, 1080);
        Assert(selected == new Size(1600, 900), "Pixel area wins over greater height or a smaller YUY2 mode.");
        config = new FakeStreamConfig(new NativeMode(2560, 1440, true), new NativeMode(0, 720, true), new NativeMode(1280, Int32.MinValue, true));
        try { CameraCapture.ConfigureHighestNativeFormat(config, 1920, 1080); throw new Exception("An unavailable bounded mode was accepted."); }
        catch (CameraFormatException error) { Assert(error.Stage == "bounded_mode_unavailable" && config.SetCalls == 0, "Only valid advertised sizes may reach SetFormat."); }
        config = new FakeStreamConfig(new NativeMode(1920, 1080, true)) { ActualWidth = 320, ActualHeight = 240 };
        try { CameraCapture.ConfigureHighestNativeFormat(config, 1920, 1080); throw new Exception("Driver-default readback replaced1080p silently."); }
        catch (CameraFormatException error) { Assert(error.Stage == "driver_changed_dimensions" && error.RequestedWidth == 1920 && error.RequestedHeight == 1080,
            "Native readback must prove the selected actual1080p dimensions."); }
        config = new FakeStreamConfig(new NativeMode(1920, 1080, false)) { SetError = unchecked((int)0x8004022A) };
        try { CameraCapture.ConfigureHighestNativeFormat(config, 1920, 1080); throw new Exception("Rejected highest mode was accepted."); }
        catch (CameraFormatException error) { Assert(error.Stage == "set_format" && config.SetCalls == 1 && config.GetCalls == 0,
            "A rejected highest mode ends this attempt so the retry policy can open a fresh driver-default graph."); }
        CameraCapture.ValidateConnectedDimensions(CameraNativeFormatPolicy.HighestOffered, new Size(1920, 1080), new Size(1920, 1080));
        CameraCapture.ValidateConnectedDimensions(CameraNativeFormatPolicy.DriverDefault, new Size(1920, 1080), new Size(320, 240));
        try { CameraCapture.ValidateConnectedDimensions(CameraNativeFormatPolicy.HighestOffered, new Size(1920, 1080), new Size(320, 240)); throw new Exception("Connected graph silently downscaled."); }
        catch (CameraFormatException error) { Assert(error.Stage == "connected_size_changed", "Connected RGB24 output also proves the actual selected size."); }
        Assert(config.SetCalls == 1, "Dimension validation never calls a camera or changes its source format.");
        var high = new FakeStreamConfig(new NativeMode(1920, 1080, true), new NativeMode(2560, 1440, false), new NativeMode(3840, 2160, true));
        Assert(CameraCapture.ConfigureHighestNativeFormat(high, 1920, 1080) == new Size(1920, 1080), "Default bounds never select a newly offered high-resolution mode.");
        Assert(CameraCapture.ConfigureHighestNativeFormat(high, 2560, 1440) == new Size(2560, 1440), "Explicit1440p bounds select its real native mode without upscaling.");
        Assert(CameraCapture.ConfigureHighestNativeFormat(high, 3840, 2160) == new Size(3840, 2160), "Explicit4K bounds and actual driver readback preserve the offered native mode.");
        var missing4K = new FakeStreamConfig(new NativeMode(1920, 1080, true), new NativeMode(2560, 1440, false));
        Assert(CameraCapture.ConfigureHighestNativeFormat(missing4K, 3840, 2160) == new Size(1920, 1080) && missing4K.SetWidth == 1920,
            "An absent4K request cannot SetFormat on an unrequested1440p mode before falling back to1080p.");
        foreach (Size bounds in new[] { new Size(0, 1080), new Size(1920, 0), new Size(7680, 4320) })
        {
            try { CameraCapture.ConfigureHighestNativeFormat(config, bounds.Width, bounds.Height); throw new Exception("Invalid highest bounds accepted."); }
            catch (ArgumentOutOfRangeException) { Assert(config.SetCalls == 1, "Invalid bounds are rejected before any stream-config call."); }
        }
    }

    private sealed class NativeMode
    {
        internal readonly int Width, Height;
        internal readonly Guid Subtype;
        internal NativeMode(int width, int height, bool rgb)
        { Width = width; Height = height; Subtype = rgb ? DirectShow.MediaSubtypeRgb24 : DirectShow.MediaSubtypeYuy2; }
    }

    private static void AvailableResolutionCatalog()
    {
        var config = new FakeStreamConfig(new NativeMode(320, 240, true), new NativeMode(1280, -720, true),
            new NativeMode(1280, 720, false), new NativeMode(1920, 1080, true), new NativeMode(3840, 2160, true));
        CameraResolution[] modes = CameraCapture.EnumerateResolutions(config);
        Assert(modes.Length == 4 && modes[0].Width == 3840 && modes[1].Width == 1920 && modes[2].Width == 1280 && modes[2].Height == 720 && modes[3].Width == 320, "Catalog exposes actual4K for manual selection without changing the default1080p selection bound.");
        Assert(config.SetCalls == 0 && config.GetCalls == 0, "Reading available resolutions never changes a camera's format.");
    }

    private sealed class FakeStreamConfig : DirectShow.IAMStreamConfig
    {
        private readonly NativeMode[] modes;
        internal int SetCalls, GetCalls, SetWidth, SetHeight, SetError, GetError, ActualWidth, ActualHeight;
        internal bool ActualEmpty;
        internal Guid SelectedSubtype;
        internal FakeStreamConfig(params NativeMode[] modes) { this.modes = modes; }
        public int GetNumberOfCapabilities(out int count, out int size) { count = modes.Length; size = 128; return 0; }
        public int GetStreamCaps(int index, out IntPtr type, IntPtr caps) { type = Allocate(modes[index].Width, modes[index].Height, modes[index].Subtype); return 0; }
        public int SetFormat(ref DirectShow.AmMediaType type)
        {
            SetCalls++; DirectShow.BitmapInfoHeader bitmap; DirectShow.TryGetBitmapHeader(type, out bitmap);
            SetWidth = bitmap.Width; SetHeight = Math.Abs(bitmap.Height); SelectedSubtype = type.SubType; return SetError;
        }
        public int GetFormat(out IntPtr type)
        {
            GetCalls++; type = IntPtr.Zero;
            if (GetError < 0 || ActualEmpty) return GetError;
            type = Allocate(ActualWidth == 0 ? SetWidth : ActualWidth, ActualHeight == 0 ? SetHeight : ActualHeight, SelectedSubtype); return 0;
        }
        private static IntPtr Allocate(int width, int height, Guid subtype)
        {
            IntPtr format = Marshal.AllocCoTaskMem(88);
            Marshal.Copy(new byte[88], 0, format, 88); Marshal.WriteInt64(format, 40, 333333);
            var bitmap = new DirectShow.BitmapInfoHeader { Size = 40, Width = width, Height = height, Planes = 1,
                BitCount = subtype == DirectShow.MediaSubtypeYuy2 ? (short)16 : (short)24,
                Compression = subtype == DirectShow.MediaSubtypeYuy2 ? 0x32595559 : 0 };
            Marshal.StructureToPtr(bitmap, IntPtr.Add(format, 48), false);
            var mediaType = new DirectShow.AmMediaType { MajorType = DirectShow.MediaTypeVideo, SubType = subtype,
                FormatType = DirectShow.FormatVideoInfo, FormatSize = 88, FormatPointer = format };
            IntPtr pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(DirectShow.AmMediaType)));
            Marshal.StructureToPtr(mediaType, pointer, false); return pointer;
        }
    }

    private static void EmptyGraphInterop()
    {
        object graph = DirectShow.Create(DirectShow.FilterGraph);
        object grabberObject = null;
        object builderObject = null;
        try
        {
            DirectShow.IMediaControl control = (DirectShow.IMediaControl)graph;
            DirectShow.Check(control.Run(), "Run empty graph");
            DirectShow.Check(control.Stop(), "Stop empty graph");
            int code;
            IntPtr first, second;
            DirectShow.IMediaEvent events = (DirectShow.IMediaEvent)graph;
            while (events.GetEvent(out code, out first, out second, 0) == 0)
                DirectShow.Check(events.FreeEventParams(code, first, second), "Free empty graph event");
            builderObject = DirectShow.Create(DirectShow.CaptureGraphBuilder2);
            DirectShow.Check(((DirectShow.ICaptureGraphBuilder2)builderObject).SetFiltergraph((DirectShow.IGraphBuilder)graph), "Capture builder ABI");
            grabberObject = DirectShow.Create(DirectShow.SampleGrabber);
            DirectShow.ISampleGrabber grabber = (DirectShow.ISampleGrabber)grabberObject;
            DirectShow.AmMediaType type = new DirectShow.AmMediaType();
            type.MajorType = DirectShow.MediaTypeVideo;
            type.SubType = DirectShow.MediaSubtypeRgb24;
            type.FormatType = DirectShow.FormatVideoInfo;
            DirectShow.Check(grabber.SetMediaType(ref type), "Sample grabber media type ABI");
            DirectShow.Check(grabber.SetOneShot(false), "Sample grabber one-shot ABI");
            DirectShow.Check(grabber.SetBufferSamples(true), "Sample grabber buffer ABI");
            DirectShow.Check(grabber.SetCallback(null, 1), "Sample grabber callback ABI");
            DirectShow.Check(((DirectShow.IGraphBuilder)graph).AddFilter((DirectShow.IBaseFilter)grabberObject, "Test grabber"), "Graph/filter ABI");
        }
        finally
        {
            DirectShow.Release(builderObject);
            DirectShow.Release(graph);
            DirectShow.Release(grabberObject);
        }
    }

    private static void ArgumentValidation()
    {
        bool rejected = false;
        try { new CameraCapture("", delegate(Bitmap frame) { }); }
        catch (ArgumentException) { rejected = true; }
        Assert(rejected, "An empty device ID must never fall back to a real camera.");
        rejected = false;
        try { new CameraCapture("not-a-camera", null); }
        catch (ArgumentNullException) { rejected = true; }
        Assert(rejected, "Null callback rejected before graph creation.");
        foreach (int[] dimensions in new[] { new[] { 2560, 1600 }, new[] { 7680, 4320 }, new[] { 1922, 1080 }, new[] { 1920, 1082 } })
        {
            rejected = false;
            try { new CameraCapture("not-a-camera", delegate(Bitmap frame) { }, dimensions[0], dimensions[1]); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "Unsupported high-resolution capture requests are rejected before opening any camera.");
        }
    }
}
