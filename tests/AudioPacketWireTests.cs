using System;
using System.Collections.Generic;
using HorizonClient;

internal static class AudioPacketWireTests
{
    private static int checks;
    private static void Check(bool value, string label) { checks++; if (!value) throw new Exception(label); }
    private static int Main()
    {
        try
        {
            foreach (AudioModemKind modem in new[] { AudioModemKind.Quiet, AudioModemKind.Aicodix })
            foreach (int capacity in new[] { 296, 1528, 4096 })
            foreach (int length in new[] { 1, 255, 256, 257, 1280, 1281, 8193 })
            {
                byte[] source = new byte[length]; new Random(length + capacity).NextBytes(source);
                Guid identity = Guid.NewGuid(); var groups = new List<Packet[]>();
                int count = (length + 255) / 256;
                for (int first = 0; first < count; )
                {
                    int packets; byte[] wire = AudioPacketWire.Encode(modem, identity, first, source, capacity, out packets);
                    Check(wire.Length <= capacity, "Wire respects native capacity.");
                    Packet[] decoded = AudioPacketWire.Decode(modem, wire);
                    Check(decoded != null && decoded.Length == packets, "Batch packet count.");
                    Check(AudioPacketWire.Decode(modem == AudioModemKind.Quiet ? AudioModemKind.Aicodix : AudioModemKind.Quiet, wire) == null, "Wrong modem tag rejected.");
                    byte[] corrupted = (byte[])wire.Clone(); corrupted[36] ^= 4;
                    Check(AudioPacketWire.Decode(modem, corrupted) == null, "Corrupt data rejected.");
                    for (int i = 0; i < packets; i++)
                    {
                        Packet packet = decoded[i]; int index = first + i;
                        Check(packet.TransferId == identity && packet.Index == index && packet.Count == count && packet.TotalLength == length &&
                            packet.ChunkSize == 256 && packet.Profile == VideoProfile.Compatible, "Existing transfer layout preserved.");
                        for (int j = 0; j < packet.Data.Length; j++) Check(packet.Data[j] == source[index * 256 + j], "Payload exact.");
                    }
                    groups.Add(decoded); first += packets;
                }
                // Reverse order and duplicates must work in the existing assembler.
                var assembler = new TransferAssembler(); byte[] completed = null;
                for (int group = groups.Count - 1; group >= 0; group--)
                    foreach (Packet packet in groups[group])
                    {
                        byte[] result = assembler.Add(packet); if (result != null) completed = result;
                        Check(assembler.Add(packet) == null, "Duplicate does not complete again.");
                    }
                Check(completed != null && completed.Length == source.Length, "Whole file assembled.");
                for (int i = 0; i < length; i++) Check(completed[i] == source[i], "Whole file exact.");
            }
            MalformedEnvelopes();
            Console.WriteLine("Audio packet wire checks passed: " + checks + ". Synthetic metadata/bytes only; no native library or filesystem."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void MalformedEnvelopes()
    {
        byte[] data = new byte[600]; int ignored;
        byte[] original = AudioPacketWire.Encode(AudioModemKind.Quiet, Guid.NewGuid(), 0, data, 4096, out ignored);
        foreach (int field in new[] { 0, 4, 5, 6, 24, 28, 32 })
        {
            byte[] wire = (byte[])original.Clone(); wire[field] = 255; RepairCrc(wire);
            Check(AudioPacketWire.Decode(AudioModemKind.Quiet, wire) == null, "Malformed metadata rejected after CRC: " + field);
        }
        byte[] emptyId = (byte[])original.Clone(); Array.Clear(emptyId, 8, 16); RepairCrc(emptyId);
        Check(AudioPacketWire.Decode(AudioModemKind.Quiet, emptyId) == null, "Empty GUID rejected.");
        byte[] shortBatch = new byte[40 + 255]; Buffer.BlockCopy(original, 0, shortBatch, 0, shortBatch.Length);
        shortBatch[6] = 255; shortBatch[7] = 0; RepairCrc(shortBatch);
        Check(AudioPacketWire.Decode(AudioModemKind.Quiet, shortBatch) == null, "Partial middle packet rejected.");
        Check(AudioPacketWire.Decode(AudioModemKind.Quiet, null) == null && AudioPacketWire.Decode(AudioModemKind.Quiet, new byte[39]) == null, "Short wire rejected.");
        try { AudioPacketWire.Encode(AudioModemKind.Quiet, Guid.NewGuid(), 0, data, 295, out ignored); Check(false, "Small capacity accepted."); } catch (ArgumentException) { checks++; }
        try { AudioPacketWire.Encode(AudioModemKind.Fsk, Guid.NewGuid(), 0, data, 4096, out ignored); Check(false, "FSK in native batch accepted."); } catch (ArgumentException) { checks++; }
    }
    // Independent bitwise CRC implementation validates parser bounds after the
    // envelope CRC passes, rather than merely testing the checksum rejection.
    private static void RepairCrc(byte[] wire)
    {
        uint crc = UInt32.MaxValue;
        for (int i = 0; i < wire.Length - 4; i++) { crc ^= wire[i]; for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320U; }
        crc = ~crc; for (int i = 0; i < 4; i++) wire[wire.Length - 4 + i] = (byte)(crc >> (i * 8));
    }
}
