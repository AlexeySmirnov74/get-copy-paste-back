using System;
using HorizonClient;

internal static class BridgeControlTests
{
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    public static int Main()
    {
        try
        {
            Guid session = Guid.NewGuid(), other = Guid.NewGuid(), video = Guid.NewGuid(), audio = Guid.NewGuid();
            Packet hello = BridgeControl.Single(session, BridgeControl.Hello(session, video, audio, 20));
            BridgeHello decoded;
            Check(BridgeControl.TryHello(hello, out decoded) && decoded.Session == session && decoded.VideoTest == video && decoded.AudioTest == audio && decoded.Seconds == 20, "A speed announcement identifies both channels and its receiving window.");
            hello.TransferId = other;
            Check(!BridgeControl.TryHello(hello, out decoded), "A mismatched envelope ID is rejected.");
            hello.TransferId = session; hello.Data[56] = 127;
            Check(!BridgeControl.TryHello(hello, out decoded), "Unbounded benchmark windows are rejected.");
            hello.Data = BridgeControl.Hello(session, video, Guid.Empty, 20);
            Check(!BridgeControl.TryHello(hello, out decoded), "A half-specified test is rejected.");
            hello.Data = BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0);
            Check(BridgeControl.TryHello(hello, out decoded), "A normal heartbeat does not start a speed test.");
            hello = BridgeControl.Single(session, BridgeControl.Hello(session, video, audio, 20, "4k webcam"));
            Check(BridgeControl.TryHello(hello, out decoded) && decoded.CameraName == "4k webcam", "The source camera name survives a Horizon virtual-webcam rename.");
            hello.Data[60]++;
            Check(!BridgeControl.TryHello(hello, out decoded), "Truncated camera names are rejected.");
            byte[] original = new byte[1000]; new Random(35).NextBytes(original);
            byte[] envelope = BridgeControl.WrapClipboard(session, original), unwrapped;
            Check(BridgeControl.TryClipboard(envelope, session, out unwrapped) && Equal(original, unwrapped), "Clipboard bytes survive framing.");
            Check(!BridgeControl.TryClipboard(envelope, other, out unwrapped), "Another server's audio is ignored.");
            Check(!BridgeControl.TryClipboard(new byte[24], session, out unwrapped), "An empty or truncated payload is rejected.");
            Check(!BridgeControl.TryClipboard(hello.Data, session, out unwrapped), "Commands cannot become clipboard data.");
            envelope[0] ^= 1;
            Check(!BridgeControl.TryClipboard(envelope, session, out unwrapped), "An invalid envelope signature is rejected.");
            BridgeReport report = new BridgeReport { Session = session, Test = video, VideoBytes = 4000000, AudioBytes = 1536, Milliseconds = 20000, MicrophoneFound = true }, parsed;
            byte[] encoded = BridgeControl.Report(report);
            Check(BridgeControl.TryReport(encoded, session, out parsed) && parsed.Test == video && parsed.VideoBytes == report.VideoBytes && parsed.AudioBytes == 1536 && parsed.MicrophoneFound, "Measured byte counts survive reporting.");
            Check(!BridgeControl.TryReport(encoded, other, out parsed), "A report must belong to the active server.");
            Check(!BridgeControl.TryClipboard(encoded, session, out unwrapped), "Reports cannot enter the clipboard.");
            encoded[56] = encoded[57] = encoded[58] = encoded[59] = 0;
            Check(!BridgeControl.TryReport(encoded, session, out parsed), "Zero-duration measurements are rejected.");
            Packet ack = BridgeControl.Single(session, BridgeControl.Acknowledge(session, video)); Guid acknowledged;
            Check(BridgeControl.TryAcknowledge(ack, out acknowledged) && acknowledged == video, "Acknowledgements refer to a specific completed transfer.");
            ack.TransferId = other;
            Check(!BridgeControl.TryAcknowledge(ack, out acknowledged), "A mismatched acknowledgement is rejected.");
            var counter = new SpeedCounter();
            Packet block = BridgeControl.Single(video, new byte[256]); counter.Add(block); counter.Add(block);
            Check(counter.Bytes == 256, "Repeated video or audio blocks do not inflate measured throughput.");
            block.Index = 1; counter.Add(block);
            Check(counter.Bytes == 512, "Distinct delivered blocks count as useful bytes.");
            block.TransferId = BridgeControl.BenchmarkEpoch(video, 1); block.Index = 0; counter.Add(block);
            Check(counter.Bytes == 768, "A new epoch counts unique data beyond one clipboard-sized transfer.");
            Check(BridgeControl.IsBenchmarkTransfer(block.TransferId, video) && !BridgeControl.IsBenchmarkTransfer(block.TransferId, other), "Synthetic benchmark epochs cannot become clipboard transfers.");
            report.VideoBytes = 400000000; report.FrameWidth = 3840; report.FrameHeight = 2160; report.Frames = 1500;
            report.ProfileBytes = new long[12]; report.ProfileMilliseconds = new int[12];
            report.ProfileBytes[11] = 123456; report.ProfileMilliseconds[11] = 4000;
            encoded = BridgeControl.Report(report);
            Check(encoded.Length <= 256 && BridgeControl.TryReport(encoded, session, out parsed) && parsed.VideoBytes == 400000000 && parsed.FrameWidth == 3840 && parsed.FrameHeight == 2160 && parsed.Frames == 1500 && parsed.ProfileBytes[11] == 123456 && parsed.ProfileMilliseconds[11] == 4000, "Sustained multi-profile results and negotiated camera size fit one audio packet.");
            encoded[77] = 127;
            Check(!BridgeControl.TryReport(encoded, session, out parsed), "Malformed profile reports are bounded.");
            CheckEpochsAndSustainedCounting(video, other);
            CheckFastReceiptNegotiation(session, other);
            Console.WriteLine("Bridge control checks passed: " + checks + "."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void CheckFastReceiptNegotiation(Guid session, Guid transfer)
    {
        BridgeHello decoded;
        var packet = BridgeControl.Single(session, BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0, "1080p camera", transfer, 1234, true));
        Check(BridgeControl.TryHello(packet, out decoded) && decoded.FastDeliveryReceipts && decoded.ForwardTransfer == transfer && decoded.ForwardEncodedBytes == 1234 && decoded.CameraName == "1080p camera", "Fast receipt capability binds the exact active forward transfer.");
        byte[] clipboard;
        Check(!BridgeControl.TryClipboard(packet.Data, session, out clipboard), "A capability announcement never becomes audio clipboard data.");
        packet.Data[81] |= 2;
        Check(!BridgeControl.TryHello(packet, out decoded), "Unknown capability bits are rejected.");
        packet = BridgeControl.Single(session, BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0, null, Guid.Empty, 0, true));
        Check(BridgeControl.TryHello(packet, out decoded) && decoded.FastDeliveryReceipts && decoded.ForwardTransfer == Guid.Empty, "Capability is available before the first clipboard copy.");
        packet.Data[76] = 1;
        Check(!BridgeControl.TryHello(packet, out decoded), "An empty transfer cannot carry a fake byte count.");
        packet = BridgeControl.Single(session, BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0, null, transfer, 1234));
        Check(BridgeControl.TryHello(packet, out decoded) && !decoded.FastDeliveryReceipts, "Old forward announcements retain the legacy audio receipt.");
        packet = BridgeControl.Single(session, BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0));
        Check(BridgeControl.TryHello(packet, out decoded) && !decoded.FastDeliveryReceipts, "Old ordinary heartbeats do not opt into a new codec.");
    }
    private static void CheckEpochsAndSustainedCounting(Guid video, Guid other)
    {
        var unique = new System.Collections.Generic.HashSet<Guid>();
        for (int i = 0; i < 256; i++)
        {
            Guid epoch = BridgeControl.BenchmarkEpoch(video, i);
            Check(unique.Add(epoch), "Each benchmark epoch has a distinct transfer ID.");
            Check(BridgeControl.IsBenchmarkTransfer(epoch, video), "Every epoch remains reserved as benchmark traffic.");
            Check(!BridgeControl.IsBenchmarkTransfer(epoch, other), "Epochs cannot be attributed to another test.");
        }
        byte[] ordinary = video.ToByteArray(); ordinary[12] ^= 0x35; ordinary[15] &= 0x7f;
        if (new Guid(ordinary) == video) ordinary[13] ^= 1;
        Check(!BridgeControl.IsBenchmarkTransfer(new Guid(ordinary), video), "A normal ID with the same prefix is not reserved without the epoch bit.");
        var counter = new SpeedCounter();
        var packet = new Packet { TransferId = BridgeControl.BenchmarkEpoch(video, 1), Profile = VideoProfile.HighDensity720, ChunkSize = 4096,
            Count = 8192, TotalLength = 32 * 1024 * 1024, Data = new byte[4096] };
        for (int epoch = 0; epoch < 2; epoch++)
        {
            packet.TransferId = BridgeControl.BenchmarkEpoch(video, epoch);
            for (int index = 0; index < packet.Count; index++)
            {
                packet.Index = index;
                Check(counter.Add(packet), "A unique delivered packet is accepted.");
                Check(!counter.Add(packet), "Retransmitted packets never inflate sustained throughput.");
            }
        }
        Check(counter.Bytes == 64L * 1024 * 1024, "Epochs support sustained measurements beyond one clipboard transfer limit.");
    }
    private static bool Equal(byte[] left, byte[] right) { if (left.Length != right.Length) return false; for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false; return true; }
}
