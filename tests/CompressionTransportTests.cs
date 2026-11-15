// Complete byte/bitmap/PCM transports with synthetic snapshots and fixture files.
// No clipboard API, camera, audio endpoint or real publication is used.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

internal static class CompressionTransportTests
{
    private static int checks;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static int Main()
    {
        string fixture = Path.GetFullPath(Path.Combine("build", "compression-transport-" + Guid.NewGuid().ToString("N")));
        try
        {
            var elapsed = Stopwatch.StartNew();
            ClipboardSnapshot formats = Formats(); string description;
            byte[] encoded = ClipboardPayload.Encode(formats, out description);
            Check(Encoding.ASCII.GetString(encoded, 0, 8) == "HCBPAY01", "Compression keeps the legacy payload envelope understood by both peers.");
            Check(encoded.Length < formats.Formats[DataFormats.UnicodeText].Length, "The shared payload compresses repetitive content before either transport.");
            foreach (VideoProfile profile in VisualProtocol.GetProfiles()) Compare(formats, ClipboardPayload.Decode(ThroughVideo(encoded, profile)));
            foreach (AudioModemKind modem in new[] { AudioModemKind.Fsk, AudioModemKind.Quiet, AudioModemKind.Aicodix })
                Compare(formats, ClipboardPayload.Decode(ThroughAudio(encoded, modem)));

            string folder = Path.Combine(fixture, "folder"); Directory.CreateDirectory(Path.Combine(folder, "empty"));
            byte[] contents = Utf8.GetBytes(new string('F', 2048) + " Файл\r\n");
            File.WriteAllBytes(Path.Combine(folder, "данные.txt"), contents);
            var files = new ClipboardSnapshot { Paths = new[] { folder } };
            byte[] fileWire = ClipboardPayload.Encode(files, out description);
            CompareFiles(ClipboardPayload.Decode(ThroughVideo(fileWire, VideoProfile.HighDefinitionRobust)), contents);
            CompareFiles(ClipboardPayload.Decode(ThroughAudio(fileWire, AudioModemKind.Fsk)), contents);
            Console.WriteLine("Compression transport checks passed: {0}; {1}ms, all five video profiles and FSK/Quiet/aicodix PCM. Synthetic snapshots/files only.", checks, elapsed.ElapsedMilliseconds);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string build = Path.GetFullPath("build") + Path.DirectorySeparatorChar;
            if (Directory.Exists(fixture) && fixture.StartsWith(build, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(fixture).StartsWith("compression-transport-")) Directory.Delete(fixture, true);
        }
    }

    private static ClipboardSnapshot Formats()
    {
        var snapshot = new ClipboardSnapshot();
        snapshot.Formats.Add(DataFormats.UnicodeText, Utf8.GetBytes(new string('T', 4096) + " Пример текста — clipboard\r\n"));
        snapshot.Formats.Add(DataFormats.Html, Utf8.GetBytes("<html><p>" + new string('H', 1024) + "</p></html>"));
        snapshot.Formats.Add(DataFormats.Rtf, Utf8.GetBytes(@"{\rtf1\ansi " + new string('R', 1024) + "}"));
        using (var image = new Bitmap(8, 8))
        using (var png = new MemoryStream())
        {
            using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.ForestGreen);
            image.Save(png, ImageFormat.Png); snapshot.Formats.Add("PNG", png.ToArray());
        }
        return snapshot;
    }

    private static byte[] ThroughVideo(byte[] bytes, VideoProfile profile)
    {
        Guid id = Guid.NewGuid(); int chunk = VisualProtocol.GetChunkSize(profile);
        int count = (bytes.Length + chunk - 1) / chunk; var assembler = new TransferAssembler(); byte[] result = null;
        for (int first = 0; first < count; first += VisualProtocol.GetPacketsPerFrame(profile))
        {
            using (Bitmap bitmap = VisualProtocol.EncodePacketFrame(id, first, bytes, profile))
            {
                Packet[] packets = VisualProtocol.DecodePackets(bitmap, profile);
                Check(packets != null && packets.Length != 0, "A compressed payload frame is decoded in " + profile);
                foreach (Packet packet in packets)
                {
                    result = assembler.Add(packet) ?? result;
                    Check(assembler.Add(packet) == null, "A repeated video packet cannot duplicate the payload.");
                }
            }
        }
        Equal(bytes, result, "Video preserves the selected compressed envelope exactly."); return result;
    }

    private static byte[] ThroughAudio(byte[] encoded, AudioModemKind modem)
    {
        Guid session = Guid.NewGuid(), id = Guid.NewGuid(); byte[] wrapped = BridgeControl.WrapClipboard(session, encoded);
        var gate = new object(); var assembler = new TransferAssembler(); var indices = new HashSet<int>(); byte[] received = null;
        using (var mux = new AudioReceiveMultiplexer(delegate(Packet packet) {
            if (packet.TransferId != id) return;
            lock (gate) { indices.Add(packet.Index); received = assembler.Add(packet) ?? received; }
        }, modem != AudioModemKind.Fsk))
        {
            int count = (wrapped.Length + AudioModem.MaxPayloadBytes - 1) / AudioModem.MaxPayloadBytes;
            for (int first = 0; first < count; )
            {
                AudioEncodedBlock block = AudioTransmissionEncoder.Encode(modem, id, first, wrapped);
                for (int offset = 0; offset < block.Samples.Length; offset += 960)
                {
                    int length = Math.Min(960, block.Samples.Length - offset); var samples = new float[length];
                    for (int i = 0; i < length; i++) samples[i] = (float)(Math.Round(block.Samples[offset + i] * 0.67 * Int16.MaxValue) / Int16.MaxValue);
                    mux.Feed(samples);
                }
                int next = first + block.Packets; var wait = Stopwatch.StartNew();
                for (;;) { lock (gate) { if (indices.Count >= next) break; } if (wait.ElapsedMilliseconds >= 4000) throw new Exception("PCM packet timeout: " + modem); Thread.Sleep(1); }
                first = next;
            }
        }
        Equal(wrapped, received, "Audio preserves the complete session envelope in " + modem);
        byte[] payload;
        Check(BridgeControl.TryClipboard(received, session, out payload), "Audio payload unwraps only for its exact bridge session.");
        byte[] foreign; Check(!BridgeControl.TryClipboard(received, Guid.NewGuid(), out foreign), "Compression cannot weaken the reverse-session binding.");
        Equal(encoded, payload, "Session wrapping never recompresses or changes the payload bytes."); return payload;
    }

    private static void Compare(ClipboardSnapshot original, DecodedPayload actual)
    {
        Check(!actual.IsFileDrop && actual.Formats.Count == 4, "Text, HTML, RTF and PNG share one compressed payload in both directions.");
        foreach (KeyValuePair<string, byte[]> pair in original.Formats) Equal(pair.Value, actual.Formats[pair.Key], "Each clipboard format is byte-exact after decompression.");
    }
    private static void CompareFiles(DecodedPayload actual, byte[] expected)
    {
        Check(actual.IsFileDrop && actual.Files.Count == 3, "File-drop transport preserves the folder, empty subfolder and file.");
        var files = new Dictionary<string, PayloadFile>(); foreach (PayloadFile file in actual.Files) files.Add(file.Path, file);
        Check(files["folder"].IsDirectory && files["folder\\empty"].IsDirectory, "Directory metadata remains intact after decompression.");
        Equal(expected, files["folder\\данные.txt"].Data, "Nested file contents are byte-exact in video and reverse audio.");
    }
    private static void Equal(byte[] expected, byte[] actual, string message)
    {
        Check(actual != null && actual.Length == expected.Length, message);
        for (int i = 0; i < expected.Length; i++) if (expected[i] != actual[i]) throw new Exception(message + " at byte " + i);
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
