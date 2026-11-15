using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using HorizonClient;

internal static class AudioModemTests
{
    private static int checks;

    private static int Main()
    {
        try
        {
            BasicRoundTrips();
            ReassemblyAndRepeats();
            CorruptionAndResynchronization();
            GainNoiseAndDrift();
            SyntheticLossMatrix();
            InvalidInputsAndBounds();
            WaveFile();
            MeasureSyntheticRate();
            Console.WriteLine("Audio modem checks passed: " + checks + ". Synthetic PCM only; no microphone or speaker was opened.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void BasicRoundTrips()
    {
        foreach (int length in new[] { 0, 1, 31, 256 })
        {
            byte[] bytes = RandomBytes(length, 1900 + length);
            Packet packet = PacketFor(Guid.NewGuid(), bytes, 0);
            List<Packet> decoded = Decode(WithSilence(AudioModem.Encode(packet), 537, 937), true);
            Check(decoded.Count == 1, "Each clean audio packet must decode at arbitrary sample/chunk offsets: " + length);
            SamePacket(packet, decoded[0]);
        }
    }

    private static void ReassemblyAndRepeats()
    {
        byte[] bytes = RandomBytes(256 * 3 + 11, 339);
        Guid transfer = Guid.NewGuid();
        List<float[]> pieces = new List<float[]>();
        foreach (int index in new[] { 3, 1, 1, 0, 2, 2 }) pieces.Add(AudioModem.Encode(PacketFor(transfer, bytes, index)));
        List<Packet> decoded = Decode(Join(pieces.ToArray()), true);
        Check(decoded.Count == 6, "Repeated and out-of-order packets must remain decodable; received " + decoded.Count + ": " + String.Join(",", decoded.ConvertAll(delegate(Packet value) { return value.Index.ToString(); }).ToArray()));
        TransferAssembler assembler = new TransferAssembler();
        byte[] result = null;
        int completions = 0;
        foreach (Packet packet in decoded)
        {
            byte[] complete = assembler.Add(packet);
            if (complete != null) { result = complete; completions++; }
        }
        Equal(bytes, result, "Existing assembler must reconstruct audio transfer bytes.");
        Check(completions == 1, "Repeated audio packets must not re-publish a completed transfer.");
    }

    private static void CorruptionAndResynchronization()
    {
        Packet first = PacketFor(Guid.NewGuid(), RandomBytes(256, 9871), 0);
        Packet next = PacketFor(Guid.NewGuid(), RandomBytes(83, 9872), 0);
        float[] damaged = AudioModem.Encode(first);
        int byteIndex = AudioModem.HeaderSize + 17;
        int dataBit = 3;
        int location = AudioModem.GuardSamples + (AudioModem.PreambleBits + AudioModem.SyncBits + byteIndex * 10 + dataBit + 1) * AudioModem.SamplesPerBit;
        bool original = (first.Data[17] & (1 << dataBit)) != 0;
        int oppositeFrequency = original ? 2200 : 1200;
        for (int sample = 0; sample < AudioModem.SamplesPerBit; sample++)
            damaged[location + sample] = (float)(AudioModem.PeakAmplitude * Math.Sin(2 * Math.PI * oppositeFrequency * sample / AudioModem.SampleRate));
        List<Packet> decoded = Decode(Join(damaged, AudioModem.Encode(next)), true);
        Check(decoded.Count == 1, "CRC must reject a changed data bit and recover the next packet.");
        SamePacket(next, decoded[0]);

        float[] dropped = AudioModem.Encode(first);
        int cut = AudioModem.GuardSamples + 11000;
        float[] shortened = new float[dropped.Length - 211];
        Array.Copy(dropped, 0, shortened, 0, cut);
        Array.Copy(dropped, cut + 211, shortened, cut, dropped.Length - cut - 211);
        decoded = Decode(Join(shortened, AudioModem.Encode(next)), true);
        Check(decoded.Count == 1, "A dropped audio segment must not block recovery of the following packet.");
        SamePacket(next, decoded[0]);
    }

    private static void GainNoiseAndDrift()
    {
        Packet packet = PacketFor(Guid.NewGuid(), RandomBytes(256, 6134), 0);
        float[] clean = AudioModem.Encode(packet);
        foreach (double rate in new[] { 0.997, 1.0, 1.003 })
        {
            float[] distorted = Resample(clean, rate);
            Random random = new Random(3127);
            for (int sample = 0; sample < distorted.Length; sample++)
                distorted[sample] = (float)(distorted[sample] * 0.13 + (random.NextDouble() * 2 - 1) * 0.003);
            List<Packet> decoded = Decode(WithSilence(distorted, 391, 480), true);
            Check(decoded.Count == 1, "Audio must tolerate synthetic gain/noise and sample-rate drift " + rate);
            SamePacket(packet, decoded[0]);
        }

        // A reproducible 8-kHz/8-bit sample path is a synthetic stress case,
        // not a simulation or guarantee of any particular Horizon codec.
        float[] voiceRate = new float[(clean.Length + 5) / 6];
        for (int sample = 0; sample < voiceRate.Length; sample++)
            voiceRate[sample] = (float)(Math.Round(clean[Math.Min(clean.Length - 1, sample * 6)] * 110) / 110.0);
        List<Packet> lowQuality = Decode(WithSilence(Resample(voiceRate, 6), 47, 480), true);
        Check(lowQuality.Count == 1, "The packet must survive a synthetic 8-kHz 8-bit sample path.");
        SamePacket(packet, lowQuality[0]);
    }

    private static void InvalidInputsAndBounds()
    {
        Rejected<ArgumentException>(delegate { AudioModem.Encode(null); }, "Null packets must be rejected.");
        Packet invalid = PacketFor(Guid.NewGuid(), new byte[256], 0);
        invalid.Data = new byte[257];
        Rejected<ArgumentException>(delegate { AudioModem.Encode(invalid); }, "Audio payloads larger than 256 bytes must be rejected.");
        invalid.Data = new byte[256]; invalid.TotalLength = VisualProtocol.MaxTransferBytes + 1;
        Rejected<ArgumentException>(delegate { AudioModem.Encode(invalid); }, "Transfers beyond the 32-MiB bound must be rejected.");
        invalid.TotalLength = 256; invalid.Profile = VideoProfile.Dense;
        Rejected<ArgumentException>(delegate { AudioModem.Encode(invalid); }, "Audio uses the existing 256-byte assembler profile only.");

        AudioDecoder decoder = new AudioDecoder();
        int packets = 0;
        decoder.PacketReceived += delegate { packets++; };
        float[] sound = new float[AudioModem.SampleRate * 7];
        Random random = new Random(417);
        for (int sample = 0; sample < sound.Length; sample++)
            sound[sample] = (float)(0.15 * Math.Sin(2 * Math.PI * 1473 * sample / AudioModem.SampleRate) + (random.NextDouble() - 0.5) * 0.12);
        sound[10] = Single.NaN; sound[191] = Single.PositiveInfinity;
        decoder.Feed(sound);
        Check(packets == 0, "Ordinary tones/noise and invalid samples must not produce clipboard packets.");
        Check(decoder.BufferedSampleCount < AudioModem.SampleRate * 4, "An unrecognized stream must retain a bounded sample buffer.");
        decoder.Reset();
        Check(decoder.BufferedSampleCount == 0, "Reset must discard stream state.");
        Rejected<ArgumentNullException>(delegate { decoder.Feed(null); }, "Null PCM must be rejected.");
        decoder.Feed(new float[0]);
    }

    private static void SyntheticLossMatrix()
    {
        double[] noiseLevels = { 0.003, 0.015, 0.03 };
        foreach (double noise in noiseLevels)
        {
            const int trials = 12;
            int recovered = 0;
            for (int trial = 0; trial < trials; trial++)
            {
                byte[] identity = RandomBytes(16, 901 + trial);
                Packet packet = PacketFor(new Guid(identity), RandomBytes(256, 731 + trial), 0);
                double rate = trial % 3 == 0 ? 0.997 : trial % 3 == 1 ? 1.0 : 1.003;
                float[] samples = Resample(AudioModem.Encode(packet), rate);
                Random random = new Random(9123 + trial);
                for (int sample = 0; sample < samples.Length; sample++)
                    samples[sample] = (float)(samples[sample] * 0.13 + (random.NextDouble() * 2 - 1) * noise);
                List<Packet> decoded = Decode(WithSilence(samples, 173 + trial, 480), true);
                Check(decoded.Count <= 1, "Synthetic noise must not create additional packets.");
                if (decoded.Count == 1) { SamePacket(packet, decoded[0]); recovered++; }
            }
            double snr = 20 * Math.Log10((AudioModem.PeakAmplitude * 0.13 / Math.Sqrt(2)) / (noise / Math.Sqrt(3)));
            Console.WriteLine("SYNTHETIC ONLY: uniform noise, approximate SNR {0:F1} dB, drift -0.3/0/+0.3%: {1}/{2} verified packets, loss {3:F1}%.",
                snr, recovered, trials, (trials - recovered) * 100.0 / trials);
            if (noise == 0.003) Check(recovered == trials, "All fixed 20-dB synthetic channel cases must recover.");
        }
    }

    private static void WaveFile()
    {
        float[] samples = new[] { 0f, -1f, 1f, 5f, Single.NaN };
        using (MemoryStream stream = new MemoryStream())
        {
            AudioModem.WritePcm16Wave(stream, samples);
            Check(stream.CanWrite, "The WAV helper must leave its destination open.");
            byte[] wave = stream.ToArray();
            Check(wave.Length == 44 + samples.Length * 2, "PCM16 WAV must have a conventional 44-byte header.");
            Check(Encoding.ASCII.GetString(wave, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wave, 8, 4) == "WAVE", "WAV signature must be correct.");
            Check(BitConverter.ToInt32(wave, 24) == 48000 && BitConverter.ToInt16(wave, 22) == 1, "WAV must be mono 48 kHz.");
            Check(BitConverter.ToInt16(wave, 50) == 32767 && BitConverter.ToInt16(wave, 52) == 0, "WAV conversion must clamp peaks and reject non-finite samples.");
        }
    }

    private static void MeasureSyntheticRate()
    {
        Packet packet = PacketFor(Guid.NewGuid(), RandomBytes(256, 831), 0);
        Stopwatch timer = Stopwatch.StartNew();
        float[] samples = AudioModem.Encode(packet);
        List<Packet> decoded = Decode(samples, false);
        timer.Stop();
        Check(decoded.Count == 1, "Rate measurement must include verified packet decoding.");
        SamePacket(packet, decoded[0]);
        double seconds = (double)samples.Length / AudioModem.SampleRate;
        Console.WriteLine("SYNTHETIC ONLY: {0} verified payload bytes / {1:F3} encoded audio seconds = {2:F2} B/s ({3:F3} kbit/s); encode+decode CPU wall time {4:F3}s.",
            packet.Data.Length, seconds, packet.Data.Length / seconds, packet.Data.Length * 8 / seconds / 1000, timer.Elapsed.TotalSeconds);
    }

    private static List<Packet> Decode(float[] samples, bool split)
    {
        AudioDecoder decoder = new AudioDecoder();
        List<Packet> packets = new List<Packet>();
        decoder.PacketReceived += delegate(Packet packet) { packets.Add(packet); };
        if (!split) decoder.Feed(samples);
        else
        {
            int[] chunkSizes = { 1, 7, 43, 127, 479, 997, 4096, 13 };
            int position = 0, chunk = 0;
            while (position < samples.Length)
            {
                int count = Math.Min(chunkSizes[chunk++ % chunkSizes.Length], samples.Length - position);
                float[] part = new float[count];
                Array.Copy(samples, position, part, 0, count);
                decoder.Feed(part);
                position += count;
            }
        }
        return packets;
    }

    private static Packet PacketFor(Guid transfer, byte[] bytes, int index)
    {
        int count = Math.Max(1, (bytes.Length + 255) / 256);
        int length = Math.Min(256, bytes.Length - index * 256);
        byte[] data = new byte[length];
        Array.Copy(bytes, index * 256, data, 0, length);
        return new Packet { TransferId = transfer, Index = index, Count = count, TotalLength = bytes.Length, Data = data,
            Profile = VideoProfile.Compatible, ChunkSize = 256 };
    }

    private static float[] WithSilence(float[] samples, int before, int after)
    {
        float[] result = new float[before + samples.Length + after];
        Array.Copy(samples, 0, result, before, samples.Length);
        return result;
    }

    private static float[] Join(params float[][] segments)
    {
        int length = 0;
        foreach (float[] segment in segments) length += segment.Length;
        float[] result = new float[length];
        int position = 0;
        foreach (float[] segment in segments) { Array.Copy(segment, 0, result, position, segment.Length); position += segment.Length; }
        return result;
    }

    private static float[] Resample(float[] source, double rate)
    {
        float[] result = new float[(int)Math.Ceiling(source.Length * rate)];
        for (int sample = 0; sample < result.Length; sample++)
        {
            double position = sample / rate;
            int low = Math.Min(source.Length - 1, (int)position);
            int high = Math.Min(source.Length - 1, low + 1);
            double fraction = position - low;
            result[sample] = (float)(source[low] * (1 - fraction) + source[high] * fraction);
        }
        return result;
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static void SamePacket(Packet expected, Packet actual)
    {
        Check(actual != null && actual.TransferId == expected.TransferId && actual.Index == expected.Index && actual.Count == expected.Count && actual.TotalLength == expected.TotalLength,
            "Audio packet metadata must survive exactly.");
        Check(actual.Profile == VideoProfile.Compatible && actual.ChunkSize == 256, "Decoded packets must use the compatible assembler profile.");
        Equal(expected.Data, actual.Data, "Audio payload bytes must survive exactly.");
    }

    private static void Equal(byte[] expected, byte[] actual, string description)
    {
        Check(actual != null && actual.Length == expected.Length, description + " Length mismatch.");
        for (int index = 0; index < expected.Length; index++) if (actual[index] != expected[index]) throw new Exception(description + " Byte " + index);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        checks++;
    }

    private static void Rejected<T>(Action action, string description) where T : Exception
    {
        try { action(); }
        catch (T) { checks++; return; }
        throw new Exception(description);
    }
}
