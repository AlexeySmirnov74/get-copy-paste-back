using System;
using System.Collections.Generic;
using HorizonClient;

internal static class FastReceiptBurstTests
{
    private static int checks;
    private static int Main()
    {
        try
        {
            Guid session = new Guid("11223344-5566-7788-9900-aabbccddeeff"), transfer = new Guid("10203040-5060-7080-9000-a0b0c0d0e0f0");
            byte[] wire = DeliveryReceipt.EncodeFast(session, transfer, 1455);
            AudioEncodedBlock encoded = AudioTransmissionEncoder.EncodeFastReceipt(wire);
            Check(encoded.Packets == 1 && encoded.WireBytes == wire.Length, "One logical protected control receipt retains its packet accounting.");
            Check(encoded.Samples.Length <= AudioModem.SampleRate * 3 / 5, "A protected receipt stays within600ms rather than native-fallback seconds.");
            foreach (int lostMs in new[] { 0, 50, 100, 200, 250, 280, 300 })
            {
                float[] damaged = (float[])encoded.Samples.Clone(); Array.Clear(damaged, 0, Math.Min(damaged.Length, lostMs * 48));
                Check(Verify(damaged, session, transfer, 1455) == 1, "Loss of first" + lostMs + "ms still produces exactly one verified completion.");
            }
            float[] internalLoss = (float[])encoded.Samples.Clone(); Array.Clear(internalLoss, 170 * 48, 15 * 48);
            Check(Verify(internalLoss, session, transfer, 1455) == 1, "A15ms payload erasure cannot force a multi-second fallback when a protected copy survives.");
            Check(Verify(encoded.Samples, Guid.NewGuid(), transfer, 1455) == 0, "Repeated foreign-session controls cannot confirm.");
            Check(Verify(encoded.Samples, session, Guid.NewGuid(), 1455) == 0, "Repeated old-transfer controls cannot confirm.");
            Check(Verify(encoded.Samples, session, transfer, 1456) == 0, "Repeated wrong-size controls cannot confirm.");
            float[] noise = new float[encoded.Samples.Length]; Random random = new Random(128);
            for (int sample = 0; sample < noise.Length; sample++) noise[sample] = (float)((random.NextDouble() - .5) * .2);
            Check(Verify(noise, session, transfer, 1455) == 0, "Noise cannot become completion.");
            try { AudioTransmissionEncoder.EncodeFastReceipt(new byte[20]); throw new Exception("Invalid CRC accepted."); }
            catch (ArgumentException) { checks++; }
            Console.WriteLine("Fast receipt burst checks passed: {0}; production PCM/multiplexer and once-only owner state, startup/payload erasure, exact identity and CRC. Duration={1:F3}ms; no devices.", checks, encoded.Samples.Length / 48.0);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static int Verify(float[] samples, Guid session, Guid transfer, int bytes)
    {
        int confirmed = 0; var state = new ForwardDeliveryState(); state.Expect(session, transfer, bytes, 7, 9);
        using (var decoder = new AudioReceiveMultiplexer(delegate { throw new Exception("A receipt cannot become a clipboard packet."); }, false,
            delegate(byte[] token) { DeliveryReceipt receipt; if (state.ConfirmFast(token, 7, 9, out receipt)) confirmed++; }))
        {
            int[] chunks = { 1, 257, 960, 1931, 480 };
            for (int offset = 0, part = 0; offset < samples.Length; part++)
            {
                int count = Math.Min(chunks[part % chunks.Length], samples.Length - offset);
                float[] block = new float[count]; Array.Copy(samples, offset, block, 0, count); decoder.Feed(block); offset += count;
            }
            decoder.Feed(new float[4800]);
        }
        return confirmed;
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
