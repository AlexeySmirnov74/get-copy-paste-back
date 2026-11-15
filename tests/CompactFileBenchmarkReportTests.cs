using System;
using System.Collections.Generic;
using HorizonClient;

internal static class CompactFileBenchmarkReportTests
{
    private static int checks;
    private static int Main()
    {
        try { EnvelopesAndBinding(); PcmAndResynchronization(); Console.WriteLine("Compact benchmark report checks passed: " + checks + "; exact-command binding,24-bit missing indices, short PCM, legacy/ACK isolation and decoder diagnostics. No devices or I/O."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    private static FileBenchmarkCommand Command()
    { return new FileBenchmarkCommand { Session = Guid.NewGuid(), Test = Guid.NewGuid(), Profile = VideoProfile.Compact640, FileSize = 100L * 1024 * 1024, Seed = 123, MaximumRunMilliseconds = 180000, CompactReports = true }; }
    private static FileBenchmarkReport Report(FileBenchmarkCommand c)
    { return new FileBenchmarkReport { Session = c.Session, Test = c.Test, Profile = c.Profile, Status = FileBenchmarkStatus.Progress, ReceivedBytes = 1024, TotalBytes = c.FileSize, ElapsedMilliseconds = 12345, FrameWidth = 1920, FrameHeight = 1080, Frames = 224 }; }
    private static void EnvelopesAndBinding()
    {
        FileBenchmarkCommand c = Command(); FileBenchmarkReport r = Report(c), decoded;
        byte[] wire = CompactFileBenchmarkReport.Encode(c, r);
        Check(wire.Length == 36 && AudioModem.CompactFileReportWireLength(wire[0]) == wire.Length, "Zero-missing compact feedback is36bytes with independently identical framing bounds.");
        Check(CompactFileBenchmarkReport.TryDecode(wire, c, out decoded) && decoded.Session == c.Session && decoded.Test == c.Test && decoded.ReceivedBytes == 1024 && decoded.ElapsedMilliseconds == 12345 && decoded.FrameWidth == 1920 && decoded.FrameHeight == 1080 && decoded.Frames == 224, "All exact counters and geometry survive compact roundtrip.");
        FileBenchmarkCommand same = FileBenchmarkProtocol.Copy(c); same.Phase = FileBenchmarkPhase.Start; same.CompactReports = false;
        Check(CompactFileBenchmarkReport.TryDecode(wire, same, out decoded), "Announcement phase/feature alternation does not alter logical-test binding.");
        foreach (Action<FileBenchmarkCommand> mutate in new Action<FileBenchmarkCommand>[] { x => x.Session = Guid.NewGuid(), x => x.Test = Guid.NewGuid(), x => x.Profile = VideoProfile.Fast, x => x.FileSize--, x => x.Seed++, x => x.SegmentSize /= 2, x => x.MaximumRunMilliseconds++ })
        { FileBenchmarkCommand other = FileBenchmarkProtocol.Copy(c); mutate(other); Check(!CompactFileBenchmarkReport.TryDecode(wire, other, out decoded), "A changed command cannot accept a stale compact report."); }
        r.MissingSegment = 0; r.MissingIndices = new int[16]; for (int i = 0; i < 16; i++) r.MissingIndices[i] = 70000 + i;
        wire = CompactFileBenchmarkReport.Encode(c, r);
        Check(wire.Length == 84 && CompactFileBenchmarkReport.TryDecode(wire, c, out decoded) && decoded.MissingIndices[15] == 70015 && decoded.MissingSegment == 0, "All16 real indices above65535 survive without truncation in a32MiB segment.");
        byte[] damaged = (byte[])wire.Clone(); damaged[14] ^= 1;
        Check(!CompactFileBenchmarkReport.ValidWire(damaged) && !CompactFileBenchmarkReport.TryDecode(damaged, c, out decoded), "CRC rejects changed counters before command binding.");
        Check(!CompactFileBenchmarkReport.ValidWire(new byte[4096]) && AudioModem.CompactFileReportWireLength(0xff) == 0, "Unbounded length/count cannot reach sample allocation or a report handler.");
        r.MissingIndices = null; r.MissingSegment = -1; r.Status = FileBenchmarkStatus.Completed; r.ReceivedBytes = c.FileSize;
        wire = CompactFileBenchmarkReport.Encode(c, r);
        Check(wire.Length == 36 && CompactFileBenchmarkReport.TryDecode(wire, c, out decoded) && decoded.Status == FileBenchmarkStatus.Completed && decoded.ReceivedBytes == c.FileSize, "A compact completion still proves the exact complete logical file.");
        r.ReceivedBytes--;
        try { CompactFileBenchmarkReport.Encode(c, r); throw new Exception("Incomplete completion accepted."); } catch (ArgumentException) { checks++; }
    }
    private static void PcmAndResynchronization()
    {
        FileBenchmarkCommand c = Command(); byte[] wire = CompactFileBenchmarkReport.Encode(c, Report(c));
        float[] pcm = AudioModem.EncodeCompactFileReport(wire);
        Check(pcm.Length * 1000L / AudioModem.SampleRate < 440, "Base feedback occupies less than440ms rather than a1160ms ordinary report.");
        var decoder = new AudioDecoder(); int compact = 0, legacy = 0, ack = 0;
        decoder.CompactFileReportReceived += delegate(byte[] message) { FileBenchmarkReport result; Check(CompactFileBenchmarkReport.TryDecode(message, c, out result), "PCM frame validates against the active command."); compact++; };
        decoder.PacketReceived += delegate { legacy++; }; decoder.FastReceiptReceived += delegate { ack++; };
        Feed(decoder, pcm); Check(compact == 1 && legacy == 0 && ack == 0, "Special sync cannot dispatch a compact report as clipboard data or publicationACK.");
        float[] damaged = (float[])pcm.Clone(); int byteIndex = 14, bit = 2;
        int at = AudioModem.GuardSamples + (AudioModem.PreambleBits + AudioModem.SyncBits + byteIndex * 10 + bit + 1) * AudioModem.SamplesPerBit;
        int opposite = (wire[byteIndex] & (1 << bit)) == 0 ? 1200 : 2200;
        for (int i = 0; i < AudioModem.SamplesPerBit; i++) damaged[at + i] = (float)(AudioModem.PeakAmplitude * Math.Sin(2 * Math.PI * opposite * i / AudioModem.SampleRate));
        Feed(decoder, damaged); Feed(decoder, pcm);
        Check(compact == 2 && decoder.GetDiagnostics().CrcFailures + decoder.GetDiagnostics().ByteFailures > 0, "Damaged feedback is rejected diagnostically and the next frame re-synchronizes.");
        Packet p = new Packet { TransferId = Guid.NewGuid(), Index = 0, Count = 1, TotalLength = 1, Data = new byte[] { 19 }, ChunkSize = 256, Profile = VideoProfile.Compatible };
        Feed(decoder, AudioModem.Encode(p)); Feed(decoder, AudioModem.EncodeFastReceipt(DeliveryReceipt.EncodeFast(c.Session, c.Test, 100)));
        Check(legacy == 1 && ack == 1 && compact == 2, "Legacy clipboard packets and293ms fast publication receipts retain independent decoding.");
        AudioDecoderDiagnostics d = decoder.GetDiagnostics();
        Check(d.Packets == 1 && d.FastReceipts == 1 && d.FileReports == 2 && d.SyncCandidates >= 5 && d.NonzeroSamples > 0 && d.Peak > 0 && d.Rms > 0, "Decoder telemetry distinguishes actual signal, frame alignment, CRC/framing failures and accepted frame kinds.");
        Check(AudioModem.EncodeFastReceipt(DeliveryReceipt.EncodeFast(c.Session, c.Test, 100)).Length == 14080, "Fast receipt remains exactly the previously tested293ms waveform.");
    }
    private static void Feed(AudioDecoder decoder, float[] pcm)
    { for (int start = 0; start < pcm.Length; start += 997) { int n = Math.Min(997, pcm.Length - start); float[] piece = new float[n]; Array.Copy(pcm, start, piece, 0, n); decoder.Feed(piece); } }
}
