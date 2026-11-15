using System;
using System.Drawing;
using HorizonClient;

internal static class ColorModeIntegrationTests
{
    private static int checks;
    public static int Main()
    {
        try
        {
            int[] sizes = { 3072, 12288, 6144, 24576 };
            for (int n = 0; n < sizes.Length; n++)
            {
                VideoProfile profile = (VideoProfile)(12 + n);
                Check(VisualProtocol.IsDefinedProfile(profile) && VisualProtocol.IsOfferedProfile(profile), "Experimental wire ID must be defined and offered: " + profile);
                Check(VisualProtocol.GetChunkSize(profile) == sizes[n] && VisualProtocol.GetPacketsPerFrame(profile) == 1, "Payload capacity matches selected color mode.");
                Check(VisualProtocol.GetWidth(profile) == (n < 2 ? 1280 : 1920) && VisualProtocol.GetHeight(profile) == (n < 2 ? 720 : 1080), "Color geometry is independent of camera source geometry.");
                byte[] bytes = new byte[sizes[n] * 2 + 17]; new Random(317 + n).NextBytes(bytes);
                Guid id = Guid.NewGuid(); var assembler = new TransferAssembler(); byte[] complete = null;
                for (int index = 2; index >= 0; index--)
                using (Bitmap frame = VisualProtocol.EncodePacketFrame(id, index, bytes, profile))
                {
                    VideoProfile hint = index == 1 ? VideoProfile.Fast : profile;
                    Packet[] decoded = VisualProtocol.DecodePackets(frame, hint);
                    Check(decoded.Length == 1 && decoded[0].Profile == profile && decoded[0].TransferId == id && decoded[0].Index == index, "Color packets survive dispatch, wrong hint fallback and transfer binding.");
                    byte[] output = assembler.Add(decoded[0]); if (output != null) complete = output;
                    Check(assembler.Add(decoded[0]) == null, "Duplicate packet never emits a second completion.");
                }
                Equal(bytes, complete, "Out-of-order color transfer reconstructs exact bytes.");
                byte[] control = { 19, 33, 71 };
                using (Bitmap frame = VisualProtocol.EncodeFrame(id, 0, 1, control.Length, control))
                {
                    Packet[] decoded = VisualProtocol.DecodePackets(frame, profile);
                    Check(decoded.Length == 1 && decoded[0].Profile == VideoProfile.Compatible, "A preferred color mode must not hide control frames.");
                    Equal(control, decoded[0].Data, "Control bytes stay unchanged.");
                }
                BenchmarkRoundTrip(profile);
            }
            Check(VisualProtocol.GetAllProfiles().Length == 16, "Existing twelve IDs remain alongside four appended color IDs.");
            Console.WriteLine("Color mode integration passed: {0}; dispatch, hints, assembly, controls and verified benchmark feedback, synthetic only.", checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void BenchmarkRoundTrip(VideoProfile profile)
    {
        var command = new FileBenchmarkCommand { Session = Guid.NewGuid(), Test = Guid.NewGuid(), Profile = profile,
            FileSize = 10240, Seed = 0x8123456789abcdefUL, Phase = FileBenchmarkPhase.Start, CompactReports = true };
        byte[] wire = FileBenchmarkProtocol.Command(command); FileBenchmarkCommand parsed;
        Check(FileBenchmarkProtocol.TryCommand(new Packet { TransferId = command.Session, Index = 0, Count = 1, TotalLength = wire.Length, Data = wire }, out parsed)
            && parsed.Profile == profile, "Benchmark command preserves experimental profile ID.");
        var receiver = new FileBenchmarkReceiver(parsed); receiver.Start(0);
        byte[] data = new byte[10240]; BenchmarkData.Fill(command.Seed, 0, data, 0, data.Length);
        int chunk = VisualProtocol.GetChunkSize(profile), count = (data.Length + chunk - 1) / chunk;
        for (int index = 0; index < count; index++)
        using (Bitmap frame = VisualProtocol.EncodePacketFrame(FileBenchmarkProtocol.SegmentTransferId(command.Test, 0), index, data, profile))
        {
            Packet[] packets = VisualProtocol.DecodePackets(frame, profile);
            Check(packets.Length == 1 && receiver.Accept(packets[0], 100 + index * 34), "The benchmark verifies actual decoded color packet bytes.");
        }
        FileBenchmarkReport report = receiver.Report(500, VisualProtocol.GetWidth(profile), VisualProtocol.GetHeight(profile), count), decoded;
        Check(report.Status == FileBenchmarkStatus.Completed && report.ReceivedBytes == 10240, "Only a full verified color test passes.");
        Check(FileBenchmarkProtocol.TryReport(FileBenchmarkProtocol.Report(report), command.Session, out decoded) && decoded.Profile == profile
            && decoded.ReceivedBytes == 10240, "Legacy audio report preserves profile and verified byte count.");
        Check(CompactFileBenchmarkReport.TryDecode(CompactFileBenchmarkReport.Encode(command, report), command, out decoded)
            && decoded.Status == FileBenchmarkStatus.Completed && decoded.ReceivedBytes == 10240, "Compact audio report also confirms complete color benchmark.");
    }
    private static void Equal(byte[] expected, byte[] actual, string message)
    {
        Check(actual != null && actual.Length == expected.Length, message);
        for (int i = 0; i < expected.Length; i++) if (actual[i] != expected[i]) throw new Exception(message + " Byte " + i);
    }
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
}
