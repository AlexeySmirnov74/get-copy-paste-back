using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
#if H264_CAPTURE_TESTS
using System.Reflection;
using System.Runtime.InteropServices;
#endif
using HorizonClient;

internal static class H264SeiTests
{
    private static int assertions;
    private static readonly Random random = new Random(261113);
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1)
            {
                H264SeiMessage[] messages = H264Sei.ParseSample(File.ReadAllBytes(args[0]), H264NalFormat.AnnexB);
                foreach (H264SeiMessage message in messages) Console.WriteLine("UUID={0}; bytes={1}; probe={2}", message.Uuid, message.Payload.Length, H264Sei.IsProbe(message));
                return 0;
            }
            RoundTrips(); AnnexBFraming(); UnknownMessages(); Rejections(); ProbeIdentity(); Fuzz();
#if H264_CAPTURE_TESTS
            CapturePolicy();
#endif
            Console.WriteLine("H.264 SEI parser tests passed ({0} assertions).", assertions); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void RoundTrips()
    {
        foreach (int length in new[] { 0, 1, 238, 239, 240, 255, 256, 4096, H264Sei.MaximumUserDataBytes })
        {
            byte[] payload = new byte[length]; random.NextBytes(payload); Guid uuid = Guid.NewGuid();
            foreach (H264NalFormat format in new[] { H264NalFormat.AnnexB, H264NalFormat.Avcc, H264NalFormat.SingleNal })
            foreach (int prefix in format == H264NalFormat.Avcc ? new[] { 1, 2, 4 } : new[] { 4 })
            {
                byte[] encoded;
                try { encoded = H264Sei.CreateUserDataNal(uuid, payload, format, prefix); }
                catch (ArgumentException) { Check(format == H264NalFormat.Avcc && prefix < 4, "Only short AVC lengths can overflow"); continue; }
                H264SeiMessage[] messages = H264Sei.ParseSample(encoded, format, prefix);
                Check(messages.Length == 1, "One user-data message"); Check(messages[0].Uuid == uuid, "UUID round trip"); Equal(payload, messages[0].Payload, "SEI payload round trip");
            }
        }
        byte[] escapes = new byte[2048]; for (int i = 0; i < escapes.Length; i++) escapes[i] = (byte)(i % 4);
        for (int i = 0; i < escapes.Length - 6; i += 7) { escapes[i] = 0; escapes[i + 1] = 0; escapes[i + 2] = (byte)((i / 7) % 4); escapes[i + 3] = 0; escapes[i + 4] = 0; escapes[i + 5] = 0; }
        byte[] nal = H264Sei.CreateUserDataNal(Guid.Empty, escapes, H264NalFormat.AnnexB);
        Equal(escapes, H264Sei.ParseSample(nal, H264NalFormat.AnnexB)[0].Payload, "Emulation prevention preserves embedded start-code values");
    }

    private static void AnnexBFraming()
    {
        byte[] one = H264Sei.CreateUserDataNal(H264Sei.ProbeUuid, Encoding.ASCII.GetBytes(H264Sei.ProbeMarker), H264NalFormat.AnnexB);
        byte[] threeByteStart = new byte[one.Length - 1]; Buffer.BlockCopy(one, 1, threeByteStart, 0, threeByteStart.Length);
        Check(H264Sei.ParseSample(threeByteStart, H264NalFormat.AnnexB).Length == 1, "Three-byte Annex B start code");
        byte[] combined = Join(new byte[] { 0, 0, 0, 0, 0, 1, 9, 0xF0 }, one, new byte[] { 0, 0, 1, 0x67, 0x42, 0, 0 }, threeByteStart, new byte[] { 0, 0, 0 });
        Check(H264Sei.ParseSample(combined, H264NalFormat.AnnexB).Length == 2, "AUD, SPS, mixed start codes, and trailing zeros");
        byte[] avcc = H264Sei.CreateUserDataNal(H264Sei.ProbeUuid, new byte[] { 1, 2 }, H264NalFormat.Avcc);
        Check(H264Sei.ParseSample(Join(avcc, avcc), H264NalFormat.Avcc).Length == 2, "Multiple AVC NAL units");
        Check(H264Sei.ParseSample(Join(new byte[] { 7 }, one), H264NalFormat.AnnexB).Length == 0, "Garbage before Annex B stream rejected");
    }

    private static void UnknownMessages()
    {
        byte[] nal = H264Sei.CreateUserDataNal(H264Sei.ProbeUuid, new byte[] { 91 }, H264NalFormat.SingleNal);
        byte[] unknown = new byte[nal.Length + 3]; unknown[0] = 6; unknown[1] = 1; unknown[2] = 1; unknown[3] = 42; Buffer.BlockCopy(nal, 1, unknown, 4, nal.Length - 1);
        H264SeiMessage[] messages = H264Sei.ParseSample(unknown, H264NalFormat.SingleNal);
        Check(messages.Length == 1 && messages[0].Payload[0] == 91, "Unknown SEI types are skipped");
        byte[] duplicate = new byte[nal.Length * 2 - 2]; duplicate[0] = 6; Buffer.BlockCopy(nal, 1, duplicate, 1, nal.Length - 2); Buffer.BlockCopy(nal, 1, duplicate, nal.Length - 1, nal.Length - 1);
        Check(H264Sei.ParseSample(duplicate, H264NalFormat.SingleNal).Length == 2, "Multiple unregistered messages in one SEI NAL");
    }

    private static void Rejections()
    {
        byte[] nal = H264Sei.CreateUserDataNal(H264Sei.ProbeUuid, new byte[] { 1 }, H264NalFormat.SingleNal);
        byte[] truncated = new byte[nal.Length - 1]; Buffer.BlockCopy(nal, 0, truncated, 0, truncated.Length);
        Check(H264Sei.ParseSample(truncated, H264NalFormat.SingleNal).Length == 0, "Missing rbsp trailing bits rejected");
        byte[] badSize = (byte[])nal.Clone(); badSize[2] = 100;
        Check(H264Sei.ParseSample(badSize, H264NalFormat.SingleNal).Length == 0, "Oversized SEI payload declaration rejected");
        Check(H264Sei.ParseSample(new byte[] { 6, 5, 1, 42, 0x80 }, H264NalFormat.SingleNal).Length == 0, "Unregistered UUID shorter than 16 bytes rejected");
        Check(H264Sei.ParseSample(new byte[] { 6, 0, 0, 3, 4 }, H264NalFormat.SingleNal).Length == 0, "Invalid emulation prevention rejected");
        Check(H264Sei.ParseSample(new byte[] { 6, 0, 0, 3 }, H264NalFormat.SingleNal).Length == 0, "Truncated emulation prevention rejected");
        Check(H264Sei.ParseSample(new byte[] { 255, 255, 255, 255 }, H264NalFormat.Avcc).Length == 0, "Unbounded AVC NAL declaration rejected");
        Check(H264Sei.ParseSample(new byte[] { 0, 0, 0, 0 }, H264NalFormat.Avcc).Length == 0, "Zero AVC NAL rejected");
        Check(H264Sei.ParseSample(new byte[] { 0, 0, 0 }, H264NalFormat.Avcc).Length == 0, "Truncated AVC length rejected");
        byte[] avcc = H264Sei.CreateUserDataNal(H264Sei.ProbeUuid, new byte[] { 42 }, H264NalFormat.Avcc);
        Check(H264Sei.ParseSample(Join(avcc, new byte[] { 1 }), H264NalFormat.Avcc).Length == 0, "Malformed access unit cannot return partial AVC success");
        byte[] forbidden = (byte[])nal.Clone(); forbidden[0] |= 0x80;
        Check(H264Sei.ParseSample(forbidden, H264NalFormat.SingleNal).Length == 0, "Forbidden NAL bit rejected");
        Throws(delegate { H264Sei.ParseSample(null, H264NalFormat.AnnexB); }, "Null sample");
        Throws(delegate { H264Sei.ParseSample(new byte[0], H264NalFormat.Avcc, 3); }, "Unsupported AVC length size");
        Throws(delegate { H264Sei.ParseSample(new byte[0], (H264NalFormat)99); }, "Unknown framing");
        Throws(delegate { H264Sei.ParseSample(new byte[H264Sei.MaximumSampleBytes + 1], H264NalFormat.AnnexB); }, "Sample bound");
        Throws(delegate { H264Sei.CreateUserDataNal(Guid.Empty, new byte[H264Sei.MaximumUserDataBytes + 1], H264NalFormat.AnnexB); }, "User data bound");
    }

    private static void ProbeIdentity()
    {
        byte[] data = Encoding.ASCII.GetBytes(H264Sei.ProbeMarker);
        H264SeiMessage message = H264Sei.ParseSample(H264Sei.CreateUserDataNal(H264Sei.ProbeUuid, data, H264NalFormat.AnnexB), H264NalFormat.AnnexB)[0];
        Check(H264Sei.IsProbe(message), "Expected UUID and marker match");
        Check(!H264Sei.IsProbe(H264Sei.ParseSample(H264Sei.CreateUserDataNal(Guid.NewGuid(), data, H264NalFormat.AnnexB), H264NalFormat.AnnexB)[0]), "Other encoder metadata cannot establish probe success");
        Check(!H264Sei.IsProbe(H264Sei.ParseSample(H264Sei.CreateUserDataNal(H264Sei.ProbeUuid, Encoding.ASCII.GetBytes("HORIZON_SEI_PROBE_V10"), H264NalFormat.AnnexB), H264NalFormat.AnnexB)[0]), "Similar marker does not establish probe success");
        byte[] nal = H264Sei.CreateUserDataNal(H264Sei.ProbeUuid, data, H264NalFormat.SingleNal);
        Equal(new byte[] { 0x6B, 0x82, 0xD4, 0x3F, 0x4D, 0x4F, 0x4A, 0x2A, 0x99, 0x67, 0x0D, 0x62, 0x07, 0x61, 0xC0, 0x01 }, Slice(nal, 3, 16), "UUID uses standard wire byte order");
        Check(!H264Sei.IsProbe(null), "Null is not a successful probe");
    }

    private static void Fuzz()
    {
        for (int i = 0; i < 10000; i++)
        {
            byte[] sample = new byte[random.Next(0, 256)]; random.NextBytes(sample);
            foreach (H264NalFormat format in new[] { H264NalFormat.AnnexB, H264NalFormat.Avcc, H264NalFormat.SingleNal })
                foreach (H264SeiMessage message in H264Sei.ParseSample(sample, format)) Check(message.Payload.Length <= H264Sei.MaximumUserDataBytes, "Fuzz payload stays bounded");
        }
        assertions++;
    }
#if H264_CAPTURE_TESTS
    private static void CapturePolicy()
    {
        foreach (string fourcc in new[] { "H264", "h264", "X264", "x264", "AVC1", "avc1" }) Check(H264SeiCapture.IsH264Subtype(FourCc(fourcc)), "Known H.264 subtype");
        Check(!H264SeiCapture.IsH264Subtype(DirectShow.MediaSubtypeRgb24), "RGB cannot establish encoded H.264 availability");
        var type = new DirectShow.AmMediaType { MajorType = DirectShow.MediaTypeVideo, SubType = FourCc("H264"), FormatType = DirectShow.FormatVideoInfo };
        H264NalFormat format; int prefix;
        Check(H264SeiCapture.TryGetNalFormat(type, out format, out prefix) && format == H264NalFormat.AnnexB, "Native H264 Annex B classification");
        type.FormatType = DirectShow.FormatVideoInfo2;
        Check(!H264SeiCapture.TryGetNalFormat(type, out format, out prefix), "Unsupported Sample Grabber VideoInfo2 fails explicitly");
        type.FormatType = new Guid("E06D80E3-DB46-11CF-B4D1-00805F6CBBEA"); type.SubType = FourCc("AVC1"); type.FormatSize = 132; type.FormatPointer = Marshal.AllocCoTaskMem(132);
        try
        {
            foreach (int size in new[] { 1, 2, 4 }) { Marshal.WriteInt32(type.FormatPointer, 128, size); Check(H264SeiCapture.TryGetNalFormat(type, out format, out prefix) && format == H264NalFormat.Avcc && prefix == size, "AVC length size read from MPEG2VIDEOINFO flags"); }
            Marshal.WriteInt32(type.FormatPointer, 128, 3); Check(!H264SeiCapture.TryGetNalFormat(type, out format, out prefix), "Invalid AVC length flags rejected");
        }
        finally { Marshal.FreeCoTaskMem(type.FormatPointer); }
        CaptureDimensions();
        // Construction/disposal must not enumerate or open any actual camera.
        using (var capture = new H264SeiCapture("synthetic-moniker-never-opened", delegate(byte[] sample) { })) { Check(capture.ConnectedSubtype == Guid.Empty && capture.DroppedSamples == 0, "Probe constructor performs no capture"); }
        Throws(delegate { new H264SeiCapture("", delegate(byte[] sample) { }); }, "Empty exact moniker rejected");
        Type callback = typeof(H264SeiCapture).GetNestedType("Callback", BindingFlags.NonPublic);
        Check(callback.GetCustomAttributes(typeof(ComVisibleAttribute), false).Length == 1, "Callback is visible to native COM");
    }
    private static void CaptureDimensions()
    {
        IntPtr memory = Marshal.AllocCoTaskMem(132);
        try
        {
            Marshal.Copy(new byte[132], 0, memory, 132);
            var type = new DirectShow.AmMediaType { MajorType = DirectShow.MediaTypeVideo, SubType = FourCc("H264"),
                FormatType = DirectShow.FormatVideoInfo, FormatPointer = memory, FormatSize = 88 };
            int width, height;
            foreach (int[] dimensions in new[] { new[] { 640, 480 }, new[] { 1920, 1080 }, new[] { 1920, -1080 } })
            {
                var bitmap = new DirectShow.BitmapInfoHeader { Size = 40, Width = dimensions[0], Height = dimensions[1], Planes = 1 };
                Marshal.StructureToPtr(bitmap, IntPtr.Add(memory, 48), false);
                Check(H264SeiCapture.TryGetClientDimensions(type, out width, out height) && width == dimensions[0] && height == Math.Abs(dimensions[1]),
                    "Bounded VIDEOINFO capture accepts the server probe and top-down 1080p metadata");
            }
            foreach (int[] dimensions in new[] { new[] { 3840, 2160 }, new[] { 7680, 4320 }, new[] { 0, 1080 }, new[] { 1920, Int32.MinValue } })
            {
                var bitmap = new DirectShow.BitmapInfoHeader { Size = 40, Width = dimensions[0], Height = dimensions[1], Planes = 1 };
                Marshal.StructureToPtr(bitmap, IntPtr.Add(memory, 48), false);
                Check(!H264SeiCapture.TryGetClientDimensions(type, out width, out height), "4K, 8K and invalid dimensions cannot be selected for the client probe");
            }
            type.FormatType = Guid.Empty;
            Check(!H264SeiCapture.TryGetClientDimensions(type, out width, out height), "A bitstream without readable dimensions cannot bypass the client resolution limit");
            type.FormatType = new Guid("E06D80E3-DB46-11CF-B4D1-00805F6CBBEA"); type.SubType = FourCc("AVC1"); type.FormatSize = 132;
            Marshal.StructureToPtr(new DirectShow.BitmapInfoHeader { Size = 40, Width = 1920, Height = 1080, Planes = 1 }, IntPtr.Add(memory, 72), false);
            Check(H264SeiCapture.TryGetClientDimensions(type, out width, out height) && width == 1920 && height == 1080, "AVC MPEG2VIDEOINFO reuses the bounded VIDEOINFOHEADER2 bitmap reader");
            Marshal.StructureToPtr(new DirectShow.BitmapInfoHeader { Size = 40, Width = 3840, Height = 2160, Planes = 1 }, IntPtr.Add(memory, 72), false);
            Check(!H264SeiCapture.TryGetClientDimensions(type, out width, out height), "AVC metadata cannot select a 4K native format");
            type.FormatSize = 111;
            Check(!H264SeiCapture.TryGetClientDimensions(type, out width, out height), "Truncated native format headers are rejected without reading outside the supplied block");
        }
        finally { Marshal.FreeCoTaskMem(memory); }
    }
    private static Guid FourCc(string value) { return new Guid(((uint)value[0] | (uint)value[1] << 8 | (uint)value[2] << 16 | (uint)value[3] << 24).ToString("X8") + "-0000-0010-8000-00AA00389B71"); }
#endif
    private static byte[] Slice(byte[] bytes, int offset, int length) { byte[] result = new byte[length]; Buffer.BlockCopy(bytes, offset, result, 0, length); return result; }
    private static byte[] Join(params byte[][] parts) { using (var output = new MemoryStream()) { foreach (byte[] part in parts) output.Write(part, 0, part.Length); return output.ToArray(); } }
    private static void Equal(byte[] expected, byte[] actual, string message) { Check(expected.Length == actual.Length, message); for (int i = 0; i < expected.Length; i++) if (expected[i] != actual[i]) throw new Exception(message); assertions++; }
    private static void Throws(Action action, string message) { try { action(); } catch (ArgumentException) { assertions++; return; } throw new Exception(message); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }
}
