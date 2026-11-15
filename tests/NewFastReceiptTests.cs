using System;
using System.Collections.Generic;
using HorizonClient;

internal static class NewFastReceiptTests
{
    private static int checks;
    private static int Main()
    {
        try
        {
            TokensAndState(); WireAndPcm(); ImpairmentsAndResync();
            Console.WriteLine("Compact receipt checks passed: " + checks + ". Synthetic PCM only; no audio device, camera or clipboard was opened.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void TokensAndState()
    {
        Guid session = Guid.NewGuid(), transfer = Guid.NewGuid(); int bytes = 10240;
        byte[] token = DeliveryReceipt.FastToken(session, transfer, bytes);
        Check(token.Length == 16, "Correlation token contains 128 bits.");
        Check(!DeliveryReceipt.SameToken(token, DeliveryReceipt.FastToken(Guid.NewGuid(), transfer, bytes)), "Session changes the token.");
        Check(!DeliveryReceipt.SameToken(token, DeliveryReceipt.FastToken(session, Guid.NewGuid(), bytes)), "Transfer changes the token.");
        Check(!DeliveryReceipt.SameToken(token, DeliveryReceipt.FastToken(session, transfer, bytes + 1)), "Encoded size changes the token.");
        var state = new ForwardDeliveryState(); DeliveryReceipt receipt;
        Check(!state.ConfirmFast(token, 1, 1, out receipt) && receipt == null, "No pending transfer rejects compact ACK.");
        state.Expect(session, transfer, bytes, 7, 9);
        Check(!state.ConfirmFast(token, 8, 9, out receipt), "An unprocessed newer clipboard command invalidates the old generation.");
        Check(!state.ConfirmFast(token, 7, 10, out receipt), "A changed native clipboard sequence invalidates the old ACK.");
        Check(!state.ConfirmFast(new byte[1], 7, 9, out receipt), "A single byte cannot acknowledge arbitrary data.");
        byte[] wrong = DeliveryReceipt.FastToken(Guid.NewGuid(), transfer, bytes);
        Check(!state.ConfirmFast(wrong, 7, 9, out receipt), "A valid token from another session cannot finish this copy.");
        Check(state.ConfirmFast(token, 7, 9, out receipt) && receipt.Session == session && receipt.Transfer == transfer && receipt.EncodedBytes == bytes,
            "An exact compact ACK reconstructs the current receipt once.");
        Check(!state.ConfirmFast(token, 7, 9, out receipt), "Repeated PCM cannot signal delivery twice.");
        Check(!state.Confirm(new DeliveryReceipt { Session = session, Transfer = transfer, EncodedBytes = bytes }, 7, 9), "A delayed full receipt cannot repeat compact confirmation.");
        Guid replacement = Guid.NewGuid(); state.Expect(session, replacement, bytes, 8, 10);
        Check(!state.ConfirmFast(token, 8, 10, out receipt), "A later copy of the same bytes requires its new transfer token.");
        Check(state.Confirm(new DeliveryReceipt { Session = session, Transfer = replacement, EncodedBytes = bytes }, 8, 10), "Legacy ACK remains accepted.");
        Check(!state.ConfirmFast(DeliveryReceipt.FastToken(session, replacement, bytes), 8, 10, out receipt), "Compact and legacy receipts share once-only state.");
    }

    private static void WireAndPcm()
    {
        Guid session = Guid.NewGuid(), transfer = Guid.NewGuid();
        byte[] wire = DeliveryReceipt.EncodeFast(session, transfer, 100);
        byte[] token;
        Check(wire.Length == 20 && DeliveryReceipt.TryDecodeFast(wire, out token), "Wire frame is only 16 token bytes plus CRC32.");
        for (int index = 0; index < wire.Length; index++)
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] corrupt = (byte[])wire.Clone(); corrupt[index] ^= (byte)(1 << bit);
                Check(!DeliveryReceipt.TryDecodeFast(corrupt, out token), "Every wire single-bit corruption is rejected.");
            }
        for (int length = 0; length <= 21; length++)
            if (length != 20) Check(!DeliveryReceipt.TryDecodeFast(new byte[length], out token), "Exact compact wire length is required.");
        float[] fast = AudioModem.EncodeFastReceipt(wire);
        byte[] full = DeliveryReceipt.Encode(session, transfer, 100);
        var packet = new Packet { TransferId = Guid.NewGuid(), Index = 0, Count = 1, TotalLength = full.Length,
            Data = full, ChunkSize = 256, Profile = VideoProfile.Compatible };
        float[] slow = AudioModem.Encode(packet);
        Check(fast.Length == 14080, "Compact waveform has exactly 293.333ms PCM and no bulk header.");
        Check(slow.Length == 42880 && fast.Length * 3 < slow.Length, "Compact first ACK is over three times shorter than legacy FSK first ACK.");
        int regular; List<byte[]> received = Decode(Join(slow, fast, slow, fast), out regular);
        Check(regular == 2 && received.Count == 2, "Distinct sync words allow alternating legacy data and compact ACKs.");
        foreach (byte[] result in received) Check(DeliveryReceipt.SameToken(result, DeliveryReceipt.FastToken(session, transfer, 100)), "Demodulated token matches the publication.");
        Console.WriteLine("ACK PCM: fast={0:F3}ms, legacy_first={1:F3}ms, legacy_three={2:F3}ms; compact wire={3}bytes.",
            fast.Length * 1000.0 / 48000, slow.Length * 1000.0 / 48000, slow.Length * 3000.0 / 48000, wire.Length);
    }

    private static void ImpairmentsAndResync()
    {
        Guid session = Guid.NewGuid(), transfer = Guid.NewGuid();
        byte[] wire = DeliveryReceipt.EncodeFast(session, transfer, 102400);
        float[] clean = AudioModem.EncodeFastReceipt(wire);
        foreach (double clock in new[] { 0.997, 1.0, 1.003 })
        {
            float[] impaired = Resample(clean, clock); var random = new Random(1307);
            for (int i = 0; i < impaired.Length; i++)
                impaired[i] = (float)(Math.Round((impaired[i] * 0.13 + (random.NextDouble() * 2 - 1) * 0.003) * 32767) / 32767);
            int regular; List<byte[]> got = Decode(Join(new float[391], impaired, new float[937]), out regular);
            Check(regular == 0 && got.Count == 1 && DeliveryReceipt.SameToken(got[0], DeliveryReceipt.FastToken(session, transfer, 102400)),
                "Compact ACK survives gain/noise/PCM16 and clock drift " + clock);
        }
        float[] converted = Resample(Resample(clean, 44100.0 / 48000), 48000.0 / 44100);
        int ignored; List<byte[]> resampled = Decode(Join(new float[133], converted, new float[700]), out ignored);
        Check(resampled.Count == 1, "Compact ACK survives 48kHz to44.1kHz to48kHz resampling.");
        float[] damaged = (float[])clean.Clone();
        int at = AudioModem.GuardSamples + (AudioModem.PreambleBits + AudioModem.SyncBits + 5 * 10 + 4) * AudioModem.SamplesPerBit;
        bool original = (wire[5] & (1 << 3)) != 0; double frequency = original ? 2200 : 1200;
        for (int i = 0; i < AudioModem.SamplesPerBit; i++) damaged[at + i] = (float)(AudioModem.PeakAmplitude * Math.Sin(2 * Math.PI * frequency * i / AudioModem.SampleRate));
        List<byte[]> recovered = Decode(Join(damaged, clean), out ignored);
        Check(recovered.Count == 1, "A changed PCM data bit is CRC-rejected and the next ACK resynchronizes.");
        float[] truncated = new float[clean.Length - 211];
        Array.Copy(clean, 0, truncated, 0, 6500); Array.Copy(clean, 6711, truncated, 6500, clean.Length - 6711);
        recovered = Decode(Join(truncated, clean), out ignored);
        Check(recovered.Count == 1, "A dropped audio segment cannot prevent the following compact ACK.");
        var oldPacket = new Packet { TransferId = Guid.NewGuid(), Index = 0, Count = 1, TotalLength = 256,
            Data = new byte[256], ChunkSize = 256, Profile = VideoProfile.Compatible };
        float[] interrupted = AudioModem.Encode(oldPacket);
        int stopAt = AudioModem.GuardSamples + (AudioModem.PreambleBits + AudioModem.SyncBits + (AudioModem.HeaderSize + 11) * 10) * AudioModem.SamplesPerBit;
        float[] prefix = new float[stopAt]; Array.Copy(interrupted, prefix, prefix.Length);
        recovered = Decode(Join(prefix, clean), out ignored);
        Check(recovered.Count == 1 && ignored == 0, "A preempted valid legacy header with missing bulk body does not swallow the following compact ACK.");
        float[] noise = new float[48000]; var rng = new Random(1900);
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)((rng.NextDouble() * 2 - 1) * 0.1);
        recovered = Decode(noise, out ignored);
        Check(recovered.Count == 0 && ignored == 0, "Noise never becomes an acknowledged publication.");
    }

    private static List<byte[]> Decode(float[] samples, out int regular)
    {
        int ordinary = 0; var output = new List<byte[]>(); var decoder = new AudioDecoder();
        decoder.PacketReceived += delegate { ordinary++; };
        decoder.FastReceiptReceived += delegate(byte[] token) { output.Add(token); };
        int[] chunks = { 1, 257, 960, 4096, 313 };
        for (int offset = 0, step = 0; offset < samples.Length; step++)
        {
            int count = Math.Min(chunks[step % chunks.Length], samples.Length - offset);
            float[] block = new float[count]; Array.Copy(samples, offset, block, 0, count); offset += count; decoder.Feed(block);
        }
        regular = ordinary; return output;
    }
    private static float[] Resample(float[] source, double rate)
    {
        float[] result = new float[(int)Math.Floor(source.Length * rate)];
        for (int i = 0; i < result.Length; i++)
        {
            double at = i / rate; int low = (int)at; double part = at - low;
            result[i] = (float)(source[low] * (1 - part) + source[Math.Min(source.Length - 1, low + 1)] * part);
        }
        return result;
    }
    private static float[] Join(params float[][] pieces)
    {
        int length = 0; foreach (float[] piece in pieces) length += piece.Length;
        float[] result = new float[length]; int at = 0;
        foreach (float[] piece in pieces) { Array.Copy(piece, 0, result, at, piece.Length); at += piece.Length; }
        return result;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
}
