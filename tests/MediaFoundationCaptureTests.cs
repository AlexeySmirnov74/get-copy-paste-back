// No camera enumeration, camera opening, audio, clipboard, GUI or disk I/O.
// Native ABI checks create only in-memory MF attributes/media types/samples/buffers.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HorizonClient;

internal static class MediaFoundationCaptureTests
{
    private static int checks;
    private static int Main()
    {
        try
        {
            Identity(); CameraInterfaceAliases(); NativeModeSelection(); FrameRowsAndBounds(); FrameBufferReuseOwnership(); SnapshotReuseBenchmark(); MailboxOwnership(); CallbackNativeAbi(); NativeMemoryObjects(); InterfaceContracts();
            Console.WriteLine("Media Foundation capture tests passed: " + checks + " assertions; exact identity, RGB32 signed stride/padding/bounds, bounded callback ownership, native CCW ABI and in-memory MF COM vtables. No camera opened/enumerated.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    private static void HResult(int hr, string reason) { Check(hr >= 0, reason + ": 0x" + hr.ToString("X8")); }
    private static void Reject(Action operation, string reason)
    {
        try { operation(); }
        catch (ArgumentException) { Check(true, reason); return; }
        catch (NotSupportedException) { Check(true, reason); return; }
        throw new Exception(reason);
    }
    private static void Identity()
    {
        string a = @"\\?\vmwhzbus#vmwvwebcam#opaque-one";
        Check(MediaFoundationCapture.SameDevicePath(a, a.ToUpperInvariant()), "Windows symbolic links compare exact characters without case.");
        Check(!MediaFoundationCapture.SameDevicePath(a, "opaque-one"), "Friendly-name/substring matching is forbidden.");
        Check(!MediaFoundationCapture.SameDevicePath(a, a + "-two"), "A prefix must not select a different camera.");
        Check(!MediaFoundationCapture.SameDevicePath(a, "@device:pnp:" + a), "A manufactured display-name prefix is not a device path.");
        Check(!MediaFoundationCapture.SameDevicePath(null, a) && !MediaFoundationCapture.SameDevicePath("", ""), "Missing identity fails closed.");
    }
    private static void CameraInterfaceAliases()
    {
        // Exact 0.6.9 production fixture: three VMware cameras, with one selected
        // unit exposed through different DirectShow and MF interface categories.
        string head = @"\\?\vmwhzbus#vmwvwebcam#1&79f5d87&0&vmwhzbus1_";
        string capture = "#{65e8773d-8f56-11d0-a3b9-00a0c9223196}";
        string video = "#{e5323777-f976-4f5b-9b55-b94699c46e44}";
        string selectedUnit = head + "{7749f2cd-4723-f9dc-2e2a-d3629e6ee95b}_1";
        string selectedPath = selectedUnit + capture + @"\global";
        string[] candidates = {
            head + "{574fe729-d412-426c-b16c-9fdf775b36c3}_1" + video + @"\global",
            head + "{3973eae3-d4dd-bd4f-be4c-adf6910a0c4e}_1" + video + @"\global",
            selectedUnit + video + @"\global" };
        string pnp = @"VMWHZBUS\VMWVWEBCAM\EXACT-INSTANCE";
        var source = new MediaFoundationCapture.DeviceIdentity { Instance = pnp, VideoAlias = candidates[2] };
        var target = new MediaFoundationCapture.DeviceIdentity { Instance = pnp };
        string reason;
        for (int index = 0; index < candidates.Length; index++)
        {
            Check(MediaFoundationCapture.MatchCameraIdentity(selectedPath, candidates[index], source, target, out reason) == (index == 2), "Windows alias selects only the chosen unit even if other units share a parent instance: " + index);
            Check(MediaFoundationCapture.MatchCameraIdentity(selectedPath, candidates[index], new MediaFoundationCapture.DeviceIdentity { Instance = pnp }, target, out reason) == (index == 2), "Verified-instance fallback preserves the complete unit identity: " + index);
        }
        Check(!MediaFoundationCapture.SameDevicePath(selectedPath, candidates[2]), "Raw full symbolic links differ in the production fixture.");
        Check(!MediaFoundationCapture.MatchCameraIdentity(selectedPath, candidates[2], source, new MediaFoundationCapture.DeviceIdentity { Instance = pnp + "-other" }, out reason), "Different PnP instances cannot alias.");
        Check(!MediaFoundationCapture.MatchCameraIdentity(selectedPath, candidates[2], source, null, out reason), "Missing native PnP verification fails closed.");
        Check(!MediaFoundationCapture.MatchCameraIdentity(selectedPath, candidates[2] + "-other", source, target, out reason), "Different reference strings cannot alias.");
        string unknown = selectedUnit + "#{00000000-0000-0000-0000-000000000001}" + @"\global";
        Check(!MediaFoundationCapture.MatchCameraIdentity(selectedPath, unknown, new MediaFoundationCapture.DeviceIdentity { Instance = pnp }, target, out reason), "Unrecognised interface categories are not stripped.");
        Check(!MediaFoundationCapture.EquivalentCameraInterfaceUnit(selectedPath, selectedUnit + video + @"\other"), "Fallback preserves the entire interface reference.");
        Check(!MediaFoundationCapture.EquivalentCameraInterfaceUnit(selectedPath, selectedPath + "suffix"), "A malformed category/reference suffix is not accepted.");
        Check(MediaFoundationCapture.MatchCameraIdentity(selectedPath, candidates[2], source, target, out reason) && reason == "windows_interface_alias_and_same_pnp_instance", "Primary decision records the Windows alias evidence.");
        Check(Marshal.SizeOf(typeof(MediaFoundationNative.DeviceInterfaceData)) == (IntPtr.Size == 8 ? 32 : 28) && Marshal.SizeOf(typeof(MediaFoundationNative.DeviceInfoData)) == (IntPtr.Size == 8 ? 32 : 28), "SetupAPI metadata structures retain native pointer alignment.");
    }
    private static void FrameRowsAndBounds()
    {
        byte[] top = { 0, 0, 255, 99, 0, 255, 0, 77, 91, 92, 93, 94 };
        byte[] bottom = { 255, 255, 255, 55, 255, 0, 0, 33, 81, 82, 83, 84 };
        byte[] data = new byte[20];
        Buffer.BlockCopy(top, 0, data, 0, 8); Buffer.BlockCopy(bottom, 0, data, 12, 8);
        var positive = new MediaFoundationCapture.FrameLayout(2, 2, 12);
        Check(positive.MinimumBytes == 20, "Last-row padding is not required.");
        using (Bitmap image = positive.ToBitmap(data))
        {
            Check(image.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb(), "MF positive stride starts at top row.");
            Check(image.GetPixel(1, 0).ToArgb() == Color.Lime.ToArgb(), "MF RGB32 uses BGR byte order and ignores X.");
            Check(image.GetPixel(0, 1).ToArgb() == Color.White.ToArgb(), "Padded source row maps to second destination row.");
            Check(image.GetPixel(1, 1).ToArgb() == Color.Blue.ToArgb(), "Source padding is not treated as pixels.");
        }
        Buffer.BlockCopy(bottom, 0, data, 0, 8); Buffer.BlockCopy(top, 0, data, 12, 8);
        using (Bitmap image = new MediaFoundationCapture.FrameLayout(2, 2, -12).ToBitmap(data))
        {
            Check(image.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb() && image.GetPixel(0, 1).ToArgb() == Color.White.ToArgb(), "Negative MF stride reverses physical contiguous rows.");
        }
        Check(new MediaFoundationCapture.FrameLayout(3840, 2160, 15360).MinimumBytes == 33177600, "Manual4K RGB32 fits the unchanged32MiB snapshot bound.");
        Check(new MediaFoundationCapture.FrameLayout(2560, 1440, -10240).MinimumBytes == 14745600, "Manual1440p accepts signed RGB32 stride.");
        Reject(delegate { new MediaFoundationCapture.FrameLayout(7680, 4320, 30720); }, "8K remains rejected before allocation.");
        Reject(delegate { new MediaFoundationCapture.FrameLayout(2560, 1600, 10240); }, "Unapproved high-resolution shapes are rejected before allocation.");
        Reject(delegate { new MediaFoundationCapture.FrameLayout(2, 2, 7); }, "Short RGB32 pitch rejected.");
        Reject(delegate { new MediaFoundationCapture.FrameLayout(1, 1, Int32.MinValue); }, "Int32.MinValue stride rejected without abs overflow.");
        Reject(delegate { new MediaFoundationCapture.FrameLayout(1920, 1080, Int32.MaxValue); }, "Huge native padding rejected.");
        Reject(delegate { positive.ToBitmap(new byte[19]); }, "Truncated last row rejected.");
        Reject(delegate { positive.ToBitmap(null); }, "Null sample rejected.");
        Check(new MediaFoundationCapture.FrameLayout(1920, 1080, 7680).MinimumBytes == 8294400, "Default1080p RGB32 snapshot size remains unchanged.");
    }
    private static void FrameBufferReuseOwnership()
    {
        var pool = new MediaFoundationCapture.FrameBufferPool();
        var layout = new MediaFoundationCapture.FrameLayout(2, 2, 8);
        byte[] active = pool.Rent(16); active[2] = 255;
        var delivering = new MediaFoundationCapture.ManagedFrame(pool, active, layout);
        Exception callbackError = null;
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            var worker = new Thread(delegate()
            {
                try
                {
                    delivering.Deliver(delegate(Bitmap frame)
                    {
                        entered.Set();
                        if (!release.WaitOne(2000)) throw new Exception("Synthetic callback release timed out.");
                        if (frame.GetPixel(0, 0).ToArgb() != Color.Red.ToArgb()) throw new Exception("Active callback frame was overwritten.");
                    });
                }
                catch (Exception error) { callbackError = error; }
            });
            worker.Start();
            try
            {
                Check(entered.WaitOne(2000), "Synthetic frame callback starts without opening a camera.");
                Check(pool.RetainedCount == 0, "A callback-owned snapshot cannot return to the pool early.");
                byte[] pending = pool.Rent(16);
                var replaced = new MediaFoundationCapture.ManagedFrame(pool, pending, layout);
                replaced.Dispose(); replaced.Dispose();
                Check(pool.RetainedCount == 1, "Replacing an undelivered pending frame returns its snapshot exactly once.");
                byte[] next = pool.Rent(16);
                Check(Object.ReferenceEquals(next, pending) && !Object.ReferenceEquals(next, active), "Only the replaced pending frame is reusable while a callback is busy.");
                next[1] = 255;
                new MediaFoundationCapture.ManagedFrame(pool, next, layout).Dispose();
                Check(active[2] == 255 && active[1] == 0, "Producer reuse cannot modify the callback-owned source snapshot.");
            }
            finally { release.Set(); Check(worker.Join(2500), "Synthetic callback thread finishes within a bounded time."); }
            Check(callbackError == null && pool.RetainedCount == 2 && pool.Allocations == 2, "Delivery finally returns its snapshot after callback completion without another allocation.");
        }
        byte[] failing = pool.Rent(16);
        try
        {
            new MediaFoundationCapture.ManagedFrame(pool, failing, layout).Deliver(delegate { throw new InvalidOperationException("Synthetic callback failure."); });
            throw new Exception("The callback exception must propagate.");
        }
        catch (InvalidOperationException) { Check(pool.RetainedCount == 2, "A throwing callback returns its snapshot through the production delivery finally."); }
        byte[] old = pool.Rent(16), changed = pool.Rent(32);
        Check(pool.RetainedCount == 0 && pool.RetainedBytes == 0, "A changed frame layout evicts cached snapshots of the old exact size.");
        pool.Return(old);
        Check(pool.RetainedCount == 0, "A late old-layout return cannot pollute the current layout pool.");
        pool.Return(changed);
        Check(Object.ReferenceEquals(changed, pool.Rent(32)), "Only a snapshot of the active exact layout is reused.");
        pool.Return(changed); pool.Close(); pool.Return(changed);
        Check(pool.RetainedCount == 0 && pool.RetainedBytes == 0, "Disposal clears cached references and ignores late in-flight returns.");
        byte[] late = pool.Rent(32); pool.Return(late);
        Check(!Object.ReferenceEquals(late, changed) && pool.RetainedCount == 0, "An already-running producer cannot revive a closed pool.");
        var closing = new MediaFoundationCapture.FrameBufferPool();
        byte[] callbackOwned = closing.Rent(16);
        new MediaFoundationCapture.ManagedFrame(closing, callbackOwned, layout).Deliver(delegate
        {
            closing.Close();
            byte[] concurrentProducer = closing.Rent(16);
            Check(!Object.ReferenceEquals(callbackOwned, concurrentProducer), "Disposal from an active callback never lends its still-owned snapshot to the producer.");
            closing.Return(concurrentProducer);
        });
        Check(closing.RetainedCount == 0 && closing.RetainedBytes == 0, "A callback's finally return after disposal leaves the pool closed and empty.");
        var bounded = new MediaFoundationCapture.FrameBufferPool();
        byte[][] small = { bounded.Rent(16), bounded.Rent(16), bounded.Rent(16), bounded.Rent(16) };
        foreach (byte[] bytes in small) bounded.Return(bytes);
        Check(bounded.RetainedCount == 3 && bounded.RetainedBytes == 48, "At most three snapshots remain cached.");
        const int bytesPerFrame = 12 * 1024 * 1024;
        byte[][] large = { bounded.Rent(bytesPerFrame), bounded.Rent(bytesPerFrame), bounded.Rent(bytesPerFrame) };
        foreach (byte[] bytes in large) bounded.Return(bytes);
        Check(bounded.RetainedCount == 2 && bounded.RetainedBytes <= 32 * 1024 * 1024, "A padded layout cannot retain more than the aggregate32MiB budget.");
        Reject(delegate { bounded.Rent(32 * 1024 * 1024 + 1); }, "Over-budget snapshot requests are rejected before allocation.");
        bounded.Close();
    }

    private static void SnapshotReuseBenchmark()
    {
        var layout = new MediaFoundationCapture.FrameLayout(1920, 1080, 7680);
        IntPtr source = Marshal.AllocHGlobal(layout.MinimumBytes);
        try
        {
            Marshal.Copy(new byte[layout.MinimumBytes], 0, source, layout.MinimumBytes);
            foreach (bool pooled in new[] { false, true })
            {
                var buffers = new MediaFoundationCapture.FrameBufferPool();
                const int frames = 24;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
                var clock = Stopwatch.StartNew();
                for (int i = 0; i < frames; i++)
                {
                    byte[] snapshot = pooled ? buffers.Rent(layout.MinimumBytes) : new byte[layout.MinimumBytes];
                    Marshal.Copy(source, snapshot, 0, snapshot.Length);
                    if (pooled) new MediaFoundationCapture.ManagedFrame(buffers, snapshot, layout).Deliver(delegate { });
                    else using (Bitmap bitmap = layout.ToBitmap(snapshot)) { }
                }
                clock.Stop();
                Check(!pooled || buffers.Allocations == 1 && buffers.Reuses == frames - 1,
                    "The real native-copy/bitmap delivery path reuses one snapshot across consecutive full-HD frames.");
                Console.WriteLine(String.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "MF1920x1080 native snapshot {0}: mean_ms={1:F3}; gen0/1/2={2}/{3}/{4}; managed_snapshot_allocations={5}; frames={6}.",
                    pooled ? "reused" : "new_per_frame", clock.Elapsed.TotalMilliseconds / frames,
                    GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2,
                    pooled ? buffers.Allocations : frames, frames));
                buffers.Close();
            }
        }
        finally { Marshal.FreeHGlobal(source); }
    }

    private static MediaFoundationCapture.NativeMode Mode(uint index, int width, int height, Guid subtype, uint numerator, uint denominator)
    {
        return new MediaFoundationCapture.NativeMode { Index = index, Width = width, Height = height, Major = MediaFoundationNative.Video, Subtype = subtype,
            RateNumerator = numerator, RateDenominator = denominator };
    }
    private static void NativeModeSelection()
    {
        Guid rgb24 = new Guid("00000014-0000-0010-8000-00aa00389b71");
        var low = Mode(0, 320, 240, rgb24, 30, 1);
        var hd = Mode(1, 1280, 720, rgb24, 30, 1);
        var full = Mode(2, 1920, 1080, rgb24, 30, 1);
        var beyond = Mode(3, 3840, 2160, rgb24, 30, 1);
        int attempts = 0, rejected = 0;
        Action<MediaFoundationCapture.NativeMode, Exception> failure = delegate { rejected++; };
        MediaFoundationCapture.NativeMode result = MediaFoundationCapture.TryConfigureNativeModes(new[] { low, hd }, delegate { attempts++; }, failure);
        Check(result == hd && attempts == 1, "Actual 0.6.10 fixture selects advertised 720p instead of first/default320p.");
        result = MediaFoundationCapture.TryConfigureNativeModes(new[] { low, hd }, delegate { }, failure, new CameraResolution(320, 240));
        Check(result == low, "A manual lower resolution chooses that exact advertised native mode before the maximum.");
        result = MediaFoundationCapture.TryConfigureNativeModes(new[] { low, hd }, delegate { }, failure, new CameraResolution(1920, 1080));
        Check(result == hd, "A disappeared manual resolution falls back to the real maximum without manufacturing1080p.");
        attempts = 0;
        result = MediaFoundationCapture.TryConfigureNativeModes(new[] { low, hd }, delegate(MediaFoundationCapture.NativeMode mode) { attempts++; if (mode == low) throw new COMException("Synthetic manual mode rejection", unchecked((int)0xC00D36B4)); }, failure, new CameraResolution(320, 240));
        Check(result == hd && attempts == 2, "A rejected requested native mode tries another advertised mode once.");
        result = hd;
        Check(result.Width == 1280 && result.Height == 720 && result.Subtype == rgb24, "Missing 1080p or YUY2 is never fabricated.");
        result = MediaFoundationCapture.TryConfigureNativeModes(new[] { beyond, low, hd, full }, delegate { }, failure);
        Check(result == full, "1080p is selected when actually advertised, while4K is excluded.");
        result = MediaFoundationCapture.TryConfigureNativeModes(new[] { beyond, low, hd, full }, delegate { }, failure, new CameraResolution(3840, 2160));
        Check(result == beyond, "An exact explicit4K request selects only the actually advertised native4K mode.");
        var qhd = Mode(10, 2560, 1440, rgb24, 30, 1);
        result = MediaFoundationCapture.TryConfigureNativeModes(new[] { beyond, qhd, full }, delegate { }, failure, new CameraResolution(2560, 1440));
        Check(result == qhd, "An explicit1440p request never upgrades to offered4K.");
        result = MediaFoundationCapture.TryConfigureNativeModes(new[] { beyond, qhd, full }, delegate { }, failure);
        Check(result == full, "Offering1440p/4K does not change automatic1080p defaults.");
        result = MediaFoundationCapture.TryConfigureNativeModes(new[] { qhd, full }, delegate { }, failure, new CameraResolution(3840, 2160));
        Check(result == full, "An unavailable4K request falls back to a real bounded mode without silently selecting another high-resolution mode.");
        var yuy2 = Mode(4, 1280, 720, MediaFoundationNative.Yuy2, 15, 1);
        var rgbFast = Mode(5, 1280, 720, rgb24, 60, 1);
        result = MediaFoundationCapture.TryConfigureNativeModes(new[] { rgbFast, hd, yuy2 }, delegate { }, failure);
        Check(result == yuy2, "At one geometry an advertised YUY2 mode has priority.");
        result = MediaFoundationCapture.TryConfigureNativeModes(new[] { yuy2, full }, delegate { }, failure);
        Check(result == full, "Maximum advertised resolution takes priority over lower-resolution YUY2.");
        var yuy2Fast = Mode(6, 1280, 720, MediaFoundationNative.Yuy2, 60, 1);
        result = MediaFoundationCapture.TryConfigureNativeModes(new[] { yuy2, yuy2Fast }, delegate { }, failure);
        Check(result == yuy2Fast, "Frame rate breaks ties within the same geometry and native subtype.");
        var ntsc = Mode(7, 1280, 720, rgb24, 30000, 1001);
        result = MediaFoundationCapture.TryConfigureNativeModes(new[] { ntsc, hd }, delegate { }, failure);
        Check(result == hd, "Native rational frame rates are compared without rounding to the same integer.");
        attempts = rejected = 0;
        result = MediaFoundationCapture.TryConfigureNativeModes(new[] { low, hd, full }, delegate(MediaFoundationCapture.NativeMode mode)
        {
            attempts++;
            if (mode == full) throw new COMException("Synthetic source rejection.", unchecked((int)0xC00D36B4));
        }, failure);
        Check(result == hd && attempts == 2 && rejected == 1, "Rejected native SetMediaType falls back to the next real mode once.");
        attempts = rejected = 0;
        result = MediaFoundationCapture.TryConfigureNativeModes(new[] { low, hd, full }, delegate(MediaFoundationCapture.NativeMode mode)
        {
            attempts++;
            MediaFoundationCapture.ValidateOutputSize(mode, mode == full ? 320 : mode.Width, mode == full ? 240 : mode.Height);
        }, failure);
        Check(result == hd && attempts == 2 && rejected == 1, "Wrong RGB32 output geometry cannot be accepted as a successful high-resolution mode.");
        attempts = rejected = 0;
        result = MediaFoundationCapture.TryConfigureNativeModes(new[] { low, hd, full }, delegate(MediaFoundationCapture.NativeMode mode)
        {
            attempts++;
            MediaFoundationCapture.ValidateNativeReadback(mode, mode == full ? low : mode);
        }, failure);
        Check(result == hd && attempts == 2 && rejected == 1, "A source reverting to320p during RGB conversion is rejected before any upscaled frames.");
        Reject(delegate { MediaFoundationCapture.ValidateOutputSize(hd, 1920, 1080); }, "Output upscale is forbidden.");
        Reject(delegate { MediaFoundationCapture.ValidateOutputSize(hd, 320, 240); }, "Output downscale is forbidden.");
        Reject(delegate { MediaFoundationCapture.ValidateNativeReadback(hd, yuy2); }, "Native subtype substitution is detected.");
        MediaFoundationCapture.ValidateNativeReadback(hd, hd); MediaFoundationCapture.ValidateOutputSize(hd, 1280, 720);
        Check(true, "An exact advertised native/output pair is accepted.");
        attempts = 0;
        Reject(delegate { MediaFoundationCapture.TryConfigureNativeModes(new[] { beyond, Mode(9, 1920, 1200, rgb24, 30, 1) }, delegate { attempts++; }, failure); }, "No eligible mode fails explicitly.");
        Check(attempts == 0, "Out-of-bounds modes never reach native configuration.");
        try
        {
            MediaFoundationCapture.TryConfigureNativeModes(new[] { hd }, delegate { throw new ObjectDisposedException("Synthetic owner cancellation"); }, failure);
            throw new Exception("Owner cancellation must stop negotiation.");
        }
        catch (ObjectDisposedException) { Check(true, "Disposal cancels negotiation instead of retrying more camera formats."); }
    }
    private static void MailboxOwnership()
    {
        int retained = 0;
        var box = new MediaFoundationCapture.SampleMailbox(delegate(IntPtr pointer) { retained++; return retained; });
        Check(box.Post(0, 0x20, 1234567890123L, new IntPtr(42)), "A pending sample can be retained.");
        Check(retained == 1, "Borrowed callback pointer obtains one owner reference.");
        Check(!box.Post(0, 0, 2, new IntPtr(43)) && retained == 1 && box.Dropped == 1, "Mailbox is bounded and rejected sample is not retained.");
        MediaFoundationCapture.NativeSample sample = box.Take();
        Check(sample.Pointer == new IntPtr(42) && sample.Timestamp == 1234567890123L && sample.Flags == 0x20, "Owner obtains complete native metadata.");
        Check(box.Take() == null, "Owner can consume each retained reference once.");
        Check(box.Post(unchecked((int)0x80070005), 1, 8, IntPtr.Zero) && retained == 1, "Error callbacks do not retain a null sample.");
        sample = box.Close();
        Check(sample != null && sample.Status == unchecked((int)0x80070005), "Shutdown drains the pending error.");
        Check(!box.Post(0, 0, 0, new IntPtr(44)) && retained == 1, "Late native callback after close cannot acquire a reference.");
        Check(box.Close() == null, "Mailbox shutdown is idempotent.");
        var failing = new MediaFoundationCapture.SampleMailbox(delegate { throw new COMException("Synthetic AddRef failure.", unchecked((int)0x80004005)); });
        try { failing.Post(0, 0, 0, new IntPtr(1)); throw new Exception("AddRef failure must propagate."); }
        catch (COMException) { Check(failing.Take() == null, "Failed retain never publishes an unowned native pointer."); }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReadCallback(IntPtr self, int status, uint stream, uint flags, long timestamp, IntPtr sample);
    private static void CallbackNativeAbi()
    {
        int retains = 0, notifications = 0, errors = 0;
        var box = new MediaFoundationCapture.SampleMailbox(delegate { retains++; return 1; });
        var callback = new MediaFoundationCapture.ReaderCallback(box, delegate { notifications++; }, delegate { errors++; }, delegate { }, delegate { });
        IntPtr unknown = IntPtr.Zero, callbackPointer = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(callback);
            Guid iid = typeof(IMediaFoundationReaderCallback).GUID;
            HResult(Marshal.QueryInterface(unknown, ref iid, out callbackPointer), "Callback exposes the exact native IMFSourceReaderCallback IID.");
            IntPtr table = Marshal.ReadIntPtr(callbackPointer);
            var call = (ReadCallback)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, 3 * IntPtr.Size), typeof(ReadCallback));
            HResult(call(callbackPointer, 0, 7, 0x100, 1234567890123L, new IntPtr(999)), "Native OnReadSample ABI invocation.");
            var item = box.Take();
            Check(item != null && item.Timestamp == 1234567890123L && item.Flags == 0x100 && item.Pointer == new IntPtr(999), "Native callback preserves DWORD/64-bit timestamp/pointer positions.");
            Check(retains == 1 && notifications == 1 && errors == 0, "Native callback only retains and signals.");
            box.Close();
            HResult(call(callbackPointer, 0, 0, 0, 0, new IntPtr(1000)), "A late callback remains valid after mailbox shutdown.");
            Check(retains == 1 && notifications == 1, "Late callbacks cannot retain or wake disposed owner.");
            GC.KeepAlive(callback);
        }
        finally { if (callbackPointer != IntPtr.Zero) Marshal.Release(callbackPointer); if (unknown != IntPtr.Zero) Marshal.Release(unknown); }
    }
    private static void NativeMemoryObjects()
    {
        bool started = false;
        MediaFoundationNative.IMFAttributes attributes = null;
        MediaFoundationNative.IMFMediaType type = null, defaultType = null;
        MediaFoundationNative.IMFSample sample = null;
        MediaFoundationNative.IMFMediaBuffer buffer = null, contiguous = null;
        try
        {
            HResult(MediaFoundationNative.MFStartup(0x00020070, 0), "MFStartup without devices."); started = true;
            HResult(MediaFoundationNative.MFCreateAttributes(out attributes, 2), "Create in-memory attributes.");
            HResult(attributes.SetGUID(ref MediaFoundationNative.SourceType, ref MediaFoundationNative.VideoSource), "Native IMFAttributes SetGUID slot.");
            Guid value;
            HResult(attributes.GetGUID(ref MediaFoundationNative.SourceType, out value), "Native IMFAttributes GetGUID slot.");
            Check(value == MediaFoundationNative.VideoSource, "Native GUID attribute roundtrip.");
            ulong encoded = ((ulong)1280 << 32) | 720;
            HResult(attributes.SetUINT64(ref MediaFoundationNative.FrameSize, encoded), "Native UINT64 Set slot.");
            ulong decoded;
            HResult(attributes.GetUINT64(ref MediaFoundationNative.FrameSize, out decoded), "Native UINT64 Get slot.");
            Check(decoded == encoded, "Native 64-bit frame size roundtrip.");
            HResult(MediaFoundationNative.MFCreateMediaType(out type), "Create in-memory media type.");
            HResult(type.SetGUID(ref MediaFoundationNative.MajorType, ref MediaFoundationNative.Video), "Flattened media-type attribute prefix.");
            HResult(type.GetMajorType(out value), "Derived media-type GetMajorType slot.");
            Check(value == MediaFoundationNative.Video, "Derived vtable uses native IMFAttributes+GetMajorType layout.");
            Action<string, Func<int>> configure = delegate(string stage, Func<int> operation) { HResult(operation(), stage); };
            MediaFoundationCapture.ConfigureRgb32Attributes(type, Mode(1, 1280, 720, MediaFoundationNative.Yuy2, 30, 1), configure);
            HResult(type.GetUINT64(ref MediaFoundationNative.FrameSize, out decoded), "Highest-mode output explicitly requests the selected native size.");
            Check(decoded == (((ulong)1280 << 32) | 720), "Highest-native mode cannot fall back to an unspecified320p output request.");
            HResult(MediaFoundationNative.MFCreateMediaType(out defaultType), "Create independent driver-default compatibility request.");
            MediaFoundationCapture.ConfigureRgb32Attributes(defaultType, null, configure);
            HResult(defaultType.GetGUID(ref MediaFoundationNative.Subtype, out value), "Compatibility mode still requests decoded RGB32.");
            Check(value == MediaFoundationNative.Rgb32, "Both backends deliver the same supported RGB32 buffer format.");
            Check(defaultType.GetUINT64(ref MediaFoundationNative.FrameSize, out decoded) < 0 && defaultType.GetUINT64(ref MediaFoundationNative.FrameRate, out decoded) < 0,
                "Compatibility request preserves0.6.10: no native frame size or rate attributes are forced.");
            HResult(MediaFoundationNative.MFCreateSample(out sample), "Create in-memory media sample.");
            HResult(sample.SetSampleTime(9876543210123L), "Derived sample SetSampleTime slot.");
            long time;
            HResult(sample.GetSampleTime(out time), "Derived sample GetSampleTime slot.");
            Check(time == 9876543210123L, "Sample timestamp is a 64-bit native value.");
            HResult(MediaFoundationNative.MFCreateMemoryBuffer(16, out buffer), "Create in-memory media buffer.");
            IntPtr data; uint capacity, length;
            HResult(buffer.Lock(out data, out capacity, out length), "Native buffer Lock slots and DWORD out values.");
            try { Check(capacity >= 16 && length == 0, "Native buffer capacity/current length are correctly marshalled."); Marshal.WriteInt64(data, 1234567); }
            finally { HResult(buffer.Unlock(), "Native buffer Unlock."); }
            HResult(buffer.SetCurrentLength(8), "Native buffer length setter.");
            HResult(sample.AddBuffer(buffer), "Derived sample AddBuffer slot.");
            HResult(sample.ConvertToContiguousBuffer(out contiguous), "Derived sample ConvertToContiguousBuffer slot.");
            HResult(contiguous.Lock(out data, out capacity, out length), "Native contiguous buffer Lock.");
            try { Check(length == 8 && Marshal.ReadInt64(data) == 1234567, "Sample buffer transfer preserves bytes without any camera."); }
            finally { HResult(contiguous.Unlock(), "Native contiguous buffer Unlock."); }
            uint total;
            HResult(sample.GetTotalLength(out total), "Derived sample GetTotalLength slot.");
            Check(total == 8, "Sample tail methods retain native slot order.");
        }
        finally
        {
            Release(contiguous); Release(buffer); Release(sample); Release(defaultType); Release(type); Release(attributes);
            if (started) HResult(MediaFoundationNative.MFShutdown(), "Balanced MFShutdown.");
        }
    }
    private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private static void InterfaceContracts()
    {
        MethodInfo[] reader = typeof(MediaFoundationNative.IMFSourceReader).GetMethods();
        Check(reader.Length == 10 && reader[6].Name == "ReadSample", "SourceReader has ten native methods with ReadSample slot9 including IUnknown.");
        ParameterInfo[] args = reader[6].GetParameters();
        Check(args.Length == 6 && args[2].ParameterType == typeof(IntPtr) && args[3].ParameterType == typeof(IntPtr) && args[4].ParameterType == typeof(IntPtr) && args[5].ParameterType == typeof(IntPtr), "Async ReadSample can pass native NULL for every out pointer.");
        Check(typeof(MediaFoundationNative.IMFAttributes).GetMethods().Length == 30, "Complete IMFAttributes prefix.");
        Check(typeof(MediaFoundationNative.IMFActivate).GetMethods().Length == 33, "Activate flattens 30+3 native methods.");
        Check(typeof(MediaFoundationNative.IMFMediaType).GetMethods().Length == 35, "MediaType flattens 30+5 native methods.");
        Check(typeof(MediaFoundationNative.IMFSample).GetMethods().Length == 44, "Sample flattens 30+14 native methods.");
        Check(typeof(MediaFoundationNative.IMFMediaSource).GetMethods().Length == 10 && typeof(MediaFoundationNative.IMFMediaSource).GetMethods()[9].Name == "Shutdown", "MediaSource includes inherited EventGenerator slots.");
        Check(typeof(MediaFoundationNative.IMFMediaType).GetMethod("GetRepresentation").GetParameters()[0].ParameterType == typeof(Guid), "Representation GUID is passed by value, unlike REFGUID attributes.");
        Check(typeof(MediaFoundationNative.IMFMediaBuffer).GetMethod("Lock").GetParameters()[1].ParameterType == typeof(uint).MakeByRefType(), "Buffer lengths are DWORD outputs.");
        MethodInfo[] extended = typeof(MediaFoundationNative.IMFSourceReaderEx).GetMethods();
        Check(extended.Length == 14 && extended[10].Name == "SetNativeMediaType" && extended[11].Name == "AddTransformForStream" && extended[12].Name == "RemoveAllTransformsForStream" && extended[13].Name == "GetTransformForStream", "SourceReaderEx repeats the10-method prefix before its4native extension slots.");
        for (int index = 0; index < reader.Length; index++) Check(extended[index].Name == reader[index].Name, "SourceReaderEx inherited method order: " + index);
        Check(extended[10].GetParameters()[2].ParameterType == typeof(uint).MakeByRefType(), "SetNativeMediaType uses a DWORD output for stream flags.");
    }
}
