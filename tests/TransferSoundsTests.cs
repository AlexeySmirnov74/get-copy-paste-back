// Pure WAV checks: never starts the notification worker or opens audio devices.
using System;
using System.Text;
using HorizonClient;

internal static class TransferSoundsTests
{
    private static int checks;

    private static int Main()
    {
        try
        {
            byte[] accepted = TransferSounds.BuildWave(TransferSoundKind.Accepted);
            byte[] delivered = TransferSounds.BuildWave(TransferSoundKind.Delivered);
            CheckWave(accepted, 80);
            CheckWave(delivered, 200);
            short[] first = Samples(accepted), second = Samples(delivered);
            True(Power(first, 0, first.Length, 600) > Power(first, 0, first.Length, 800) * 100, "accepted tone is 600 Hz");
            int note = (22050 * 90 + 500) / 1000, gap = (22050 * 20 + 500) / 1000;
            True(Power(second, 0, note, 1800) > Power(second, 0, note, 600) * 100, "delivered first tone is high 1800 Hz, distinct from the low accepted cue");
            True(Power(second, note + gap, note, 2400) > Power(second, note + gap, note, 600) * 100, "delivered second tone is high 2400 Hz, distinct from the low accepted cue");
            True(Power(second, 0, note, 1800) > Power(second, 0, note, 2400) * 100, "the first high note remains distinct");
            True(Power(second, note + gap, note, 2400) > Power(second, note + gap, note, 1800) * 100, "the second high note remains distinct");
            bool silent = true;
            for (int index = note; index < note + gap; index++) silent &= second[index] == 0;
            True(silent, "delivered cue has a silent 20 ms gap");
            True(second[note - 1] == 0 && second[note + gap] == 0, "interior note edges fade to zero");
            bool rejected = false;
            try { TransferSounds.BuildWave((TransferSoundKind)99); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            True(rejected, "unknown cue rejected before audio access");
            Console.WriteLine("Transfer sound checks passed: " + checks + " assertions; PCM WAV, short distinct tones, bounded amplitude, silence and click-free edges. No audio devices opened or played.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckWave(byte[] wave, int milliseconds)
    {
        Equal("RIFF", Encoding.ASCII.GetString(wave, 0, 4), "RIFF signature");
        Equal(wave.Length - 8, BitConverter.ToInt32(wave, 4), "RIFF byte length");
        Equal("WAVEfmt ", Encoding.ASCII.GetString(wave, 8, 8), "WAV format signature");
        Equal(16, BitConverter.ToInt32(wave, 16), "PCM format length");
        Equal((short)1, BitConverter.ToInt16(wave, 20), "PCM encoding");
        Equal((short)1, BitConverter.ToInt16(wave, 22), "one channel");
        Equal(22050, BitConverter.ToInt32(wave, 24), "sample rate");
        Equal(44100, BitConverter.ToInt32(wave, 28), "byte rate");
        Equal((short)2, BitConverter.ToInt16(wave, 32), "block alignment");
        Equal((short)16, BitConverter.ToInt16(wave, 34), "PCM width");
        Equal("data", Encoding.ASCII.GetString(wave, 36, 4), "data signature");
        Equal(wave.Length - 44, BitConverter.ToInt32(wave, 40), "data length");
        short[] samples = Samples(wave);
        True(Math.Abs(samples.Length * 1000.0 / 22050 - milliseconds) < 0.1, "short expected duration");
        int peak = 0;
        double energy = 0;
        foreach (short sample in samples) { peak = Math.Max(peak, Math.Abs((int)sample)); energy += (double)sample * sample; }
        True(peak >= 3900 && peak <= 3933, "modest peak amplitude");
        True(energy / samples.Length > 1000000, "audible synthesized signal");
        Equal((short)0, samples[0], "silent leading edge");
        Equal((short)0, samples[samples.Length - 1], "silent trailing edge");
    }

    private static short[] Samples(byte[] wave)
    {
        short[] result = new short[(wave.Length - 44) / 2];
        for (int index = 0; index < result.Length; index++) result[index] = BitConverter.ToInt16(wave, 44 + index * 2);
        return result;
    }

    private static double Power(short[] samples, int offset, int count, double frequency)
    {
        double real = 0, imaginary = 0;
        for (int index = 0; index < count; index++)
        {
            double phase = 2 * Math.PI * frequency * index / 22050;
            real += samples[offset + index] * Math.Cos(phase);
            imaginary += samples[offset + index] * Math.Sin(phase);
        }
        return real * real + imaginary * imaginary;
    }

    private static void Equal<T>(T expected, T actual, string message) { True(Object.Equals(expected, actual), message); }
    private static void True(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
