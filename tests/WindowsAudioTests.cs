// Pure WindowsAudio checks. Never enumerates, opens, records, or plays audio devices.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HorizonClient;

internal static class WindowsAudioTests
{
    private static int checks;
    private static int Main()
    {
        try
        {
            CheckAbi();
            CheckPcm();
            CheckNativeFormats();
            CheckResampling();
            CheckSignalStatistics();
            CheckArgumentsWithoutOpeningDevices();
            Console.WriteLine("Windows audio checks passed: " + checks + " assertions; native ABI, PCM16/24/32/float downmix, format parsing, streaming resampling, clipping/cancellation and validation. No audio devices were enumerated, opened, captured, or played.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckAbi()
    {
        Equal(18, Marshal.SizeOf(typeof(AudioNative.WaveFormat)), "WAVEFORMATEX size");
        Equal(IntPtr.Size == 8 ? 48 : 32, Marshal.SizeOf(typeof(AudioNative.WaveHeader)), "WAVEHDR size");
        Equal(IntPtr.Size == 8 ? 24 : 16, (int)Marshal.OffsetOf(typeof(AudioNative.WaveHeader), "Flags"), "WAVEHDR flags offset");
        Equal(84, Marshal.SizeOf(typeof(AudioNative.WaveOutCaps)), "WAVEOUTCAPSW size");
        Equal(IntPtr.Size == 8 ? 24 : 16, Marshal.SizeOf(typeof(AudioNative.PropVariant)), "PROPVARIANT size");
        Equal(20, Marshal.SizeOf(typeof(AudioNative.PropertyKey)), "PROPERTYKEY size");
        // IID from the installed Windows SDK mmdeviceapi.h. This catches a
        // QueryInterface failure before any live endpoint needs to be opened.
        Equal(new Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), typeof(AudioNative.IMMDeviceCollection).GUID, "IMMDeviceCollection IID");
        Equal(new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), typeof(AudioNative.IAudioClient).GUID, "IAudioClient IID");
        Equal(new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), typeof(AudioNative.IAudioCaptureClient).GUID, "IAudioCaptureClient IID");
        string[] expected = { "Initialize", "GetBufferSize", "GetStreamLatency", "GetCurrentPadding", "IsFormatSupported", "GetMixFormat", "GetDevicePeriod", "Start", "Stop", "Reset", "SetEventHandle", "GetService" };
        MethodInfo[] methods = typeof(AudioNative.IAudioClient).GetMethods();
        Array.Sort(methods, delegate(MethodInfo first, MethodInfo second) { return first.MetadataToken.CompareTo(second.MetadataToken); });
        Equal(expected.Length, methods.Length, "IAudioClient vtable length");
        for (int index = 0; index < methods.Length; index++)
        {
            Equal(expected[index], methods[index].Name, "IAudioClient slot " + index);
            True((methods[index].MethodImplementationFlags & MethodImplAttributes.PreserveSig) != 0, "HRESULT is preserved");
        }
        Equal(5, typeof(AudioNative.IAudioCaptureClient).GetMethod("GetBuffer").GetParameters().Length, "GetBuffer native parameters");
    }

    private static void CheckPcm()
    {
        AudioFormat pcm16 = new AudioFormat(48000, 2, 16, 4, false);
        float[] mono = pcm16.ToMono(Shorts(16384, -8192, -32768, -32768, 32767, 32767), 3);
        Near(0.125, mono[0], 1e-7, "stereo16 average");
        Near(-1, mono[1], 1e-7, "signed16 minimum");
        Near(32767.0 / 32768, mono[2], 1e-7, "signed16 maximum");
        AudioFormat pcm24 = new AudioFormat(44100, 1, 24, 3, false);
        mono = pcm24.ToMono(new byte[] { 0, 0, 128, 255, 255, 127, 0, 0, 64 }, 3);
        Near(-1, mono[0], 1e-7, "signed24 minimum");
        Near(8388607.0 / 8388608, mono[1], 1e-7, "signed24 maximum");
        Near(0.5, mono[2], 1e-7, "signed24 half");
        AudioFormat pcm32 = new AudioFormat(96000, 1, 32, 4, false);
        mono = pcm32.ToMono(Ints(Int32.MinValue, Int32.MaxValue, 0x40000000), 3);
        Near(-1, mono[0], 1e-7, "signed32 minimum");
        Near(1, mono[1], 1e-7, "signed32 maximum");
        Near(0.5, mono[2], 1e-7, "left aligned PCM24 in PCM32 container");
        AudioFormat floats = new AudioFormat(48000, 2, 32, 8, true);
        mono = floats.ToMono(Floats(0.5f, -0.25f, Single.NaN, 0.5f, Single.PositiveInfinity, Single.NegativeInfinity, 2, -2), 4);
        Near(0.125, mono[0], 1e-7, "stereo float average");
        Near(0.25, mono[1], 1e-7, "NaN sanitization");
        Near(0, mono[2], 1e-7, "infinity sanitization");
        Near(0, mono[3], 1e-7, "float clipping");
        AudioFormat padded = new AudioFormat(48000, 1, 16, 4, false);
        mono = padded.ToMono(new byte[] { 0, 64, 77, 88, 0, 192, 77, 88 }, 2);
        Near(0.5, mono[0], 1e-7, "padded frame stride");
        Near(-0.5, mono[1], 1e-7, "padded second frame");
        byte[] output = AudioSamples.ToPcm16(new float[] { 0, 0.25f, -0.25f, 5, -5 });
        Equal((short)0, BitConverter.ToInt16(output, 0), "playback zero");
        Equal((short)8192, BitConverter.ToInt16(output, 2), "playback amplitude bound");
        Equal((short)-8192, BitConverter.ToInt16(output, 4), "playback negative amplitude bound");
        Equal(BitConverter.ToInt16(output, 2), BitConverter.ToInt16(output, 6), "positive overrange bounded");
        Equal(BitConverter.ToInt16(output, 4), BitConverter.ToInt16(output, 8), "negative overrange bounded");
    }

    private static void CheckNativeFormats()
    {
        using (AudioMemory memory = new AudioMemory(40))
        {
            byte[] bytes = new byte[40];
            Set16(bytes, 0, 0xfffe); Set16(bytes, 2, 2); Set32(bytes, 4, 44100);
            Set32(bytes, 8, 352800); Set16(bytes, 12, 8); Set16(bytes, 14, 32);
            Set16(bytes, 16, 22); Set16(bytes, 18, 32); Set32(bytes, 20, 3);
            Buffer.BlockCopy(new Guid("00000003-0000-0010-8000-00aa00389b71").ToByteArray(), 0, bytes, 24, 16);
            Marshal.Copy(bytes, 0, memory.DangerousGetHandle(), bytes.Length);
            AudioFormat parsed = AudioFormat.FromNative(memory.DangerousGetHandle());
            True(parsed.IsFloat && parsed.Bits == 32 && parsed.Channels == 2 && parsed.SampleRate == 44100, "extensible float mix format");
            Set16(bytes, 18, 24);
            Buffer.BlockCopy(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray(), 0, bytes, 24, 16);
            Marshal.Copy(bytes, 0, memory.DangerousGetHandle(), bytes.Length);
            parsed = AudioFormat.FromNative(memory.DangerousGetHandle());
            True(!parsed.IsFloat && parsed.Bits == 32, "extensible PCM24 in32 mix format");
            Set16(bytes, 16, 21); Marshal.Copy(bytes, 0, memory.DangerousGetHandle(), bytes.Length);
            Throws<InvalidDataException>(delegate { AudioFormat.FromNative(memory.DangerousGetHandle()); }, "short extensible format");
            Set16(bytes, 0, 1); Set16(bytes, 2, 1); Set16(bytes, 12, 2); Set16(bytes, 14, 16); Set16(bytes, 16, 0);
            Marshal.Copy(bytes, 0, memory.DangerousGetHandle(), bytes.Length);
            parsed = AudioFormat.FromNative(memory.DangerousGetHandle());
            True(!parsed.IsFloat && parsed.Bits == 16 && parsed.Channels == 1, "plain PCM16 format");
            Set16(bytes, 0, 2); Marshal.Copy(bytes, 0, memory.DangerousGetHandle(), bytes.Length);
            Throws<NotSupportedException>(delegate { AudioFormat.FromNative(memory.DangerousGetHandle()); }, "compressed mix format rejected");
        }
    }

    private static void CheckResampling()
    {
        foreach (int rate in new int[] { 8000, 16000, 24000, 44100, 48000, 96000, 192000, 384000 })
        {
            float[] source = new float[rate + 37];
            for (int index = 0; index < source.Length; index++) source[index] = (float)(0.125 * Math.Sin(index * Math.PI * 2400 / rate) + 0.125 * Math.Sin(index * Math.PI * 4400 / rate));
            AudioResampler wholeConverter = new AudioResampler(rate);
            float[] whole = wholeConverter.Convert(source);
            int expected = (int)(((long)(source.Length - 1) * 48000 / rate) + 1);
            Equal(expected, whole.Length, "resampled length at " + rate);
            AudioResampler stream = new AudioResampler(rate);
            List<float> chunks = new List<float>();
            int position = 0, turn = 0;
            int[] sizes = { 1, 7, 241, 480, 3, 999 };
            while (position < source.Length)
            {
                int count = Math.Min(sizes[turn++ % sizes.Length], source.Length - position);
                float[] piece = new float[count];
                Array.Copy(source, position, piece, 0, count);
                chunks.AddRange(stream.Convert(piece));
                position += count;
            }
            Equal(whole.Length, chunks.Count, "chunked length at " + rate);
            double maximum = 0;
            for (int index = 0; index < whole.Length; index++) maximum = Math.Max(maximum, Math.Abs(whole[index] - chunks[index]));
            Near(0, maximum, 0, "chunking preserves all phase and sample values at " + rate);
            stream.Reset();
            float[] reset = stream.Convert(new float[] { 0.125f });
            Equal(1, reset.Length, "reset restarts phase");
            Near(0.125, reset[0], 0, "reset restarts previous sample");
        }
        AudioResampler ramp = new AudioResampler(24000);
        float[] first = ramp.Convert(new float[] { 0 });
        float[] second = ramp.Convert(new float[] { 1 });
        Equal(1, first.Length, "streaming first endpoint");
        Equal(2, second.Length, "streaming next interval");
        Near(0.5, second[0], 0, "interpolation across chunk boundary");
        Near(1, second[1], 0, "interpolation exact endpoint");
        Equal(0, ramp.Convert(new float[0]).Length, "empty chunks preserve state");
    }

    private static void CheckArgumentsWithoutOpeningDevices()
    {
        Throws<ArgumentException>(delegate { new AudioPlayer("not-a-waveout-id"); }, "invalid playback ID");
        Throws<ArgumentException>(delegate { new AudioPlayer("waveout:-1"); }, "negative playback ID");
        Throws<ArgumentException>(delegate { new AudioPlayer("waveout:65536"); }, "oversized playback ID");
        using (AudioPlayer player = new AudioPlayer("waveout:0"))
        {
            Throws<ArgumentNullException>(delegate { player.Play(null); }, "null playback samples");
            Throws<ArgumentOutOfRangeException>(delegate { player.Play(new float[0]); }, "empty playback samples");
            Throws<ArgumentException>(delegate { player.Play(new float[] { Single.NaN }); }, "NaN playback samples");
            Throws<ArgumentException>(delegate { player.Play(new float[] { Single.PositiveInfinity }); }, "infinite playback samples");
            using (CancellationTokenSource cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Throws<OperationCanceledException>(delegate { player.Play(new float[] { 0 }, cancelled.Token); }, "cancelled playback does not open audio");
            }
        }
        AudioPlayer disposedPlayer = new AudioPlayer();
        disposedPlayer.Dispose(); disposedPlayer.Dispose();
        Throws<ObjectDisposedException>(delegate { disposedPlayer.Play(new float[] { 0 }); }, "disposed player does not open audio");
        Throws<ArgumentNullException>(delegate { new AudioCapture(null, true); }, "missing capture callback");
        Throws<ArgumentException>(delegate { new AudioCapture(delegate { }, false); }, "no automatic physical microphone");
        Throws<ArgumentException>(delegate { new AudioCapture(delegate { }, true, " "); }, "empty explicit endpoint");
        using (AudioCapture loopback = new AudioCapture(delegate { }, true))
        {
            True(!loopback.IsRunning && loopback.DroppedChunks == 0 && loopback.Discontinuities == 0, "loopback constructor does not start capture");
        }
        AudioCapture selected = new AudioCapture(delegate { }, false, "test-selected-virtual-microphone");
        selected.Dispose(); selected.Dispose();
        Throws<ObjectDisposedException>(delegate { selected.Start(); }, "disposed capture does not open audio");
        Throws<ArgumentOutOfRangeException>(delegate { new AudioFormat(0, 2, 16, 4, false); }, "invalid source rate");
        Throws<ArgumentOutOfRangeException>(delegate { new AudioFormat(48000, 0, 16, 4, false); }, "invalid channel count");
        Throws<NotSupportedException>(delegate { new AudioFormat(48000, 2, 8, 2, false); }, "unsupported sample format");
        Throws<ArgumentOutOfRangeException>(delegate { new AudioFormat(48000, 2, 16, 2, false); }, "short block alignment");
        Throws<ArgumentOutOfRangeException>(delegate { new AudioFormat(48000, 2, 16, 4, false).ToMono(new byte[3], 1); }, "partial PCM frame");
        Throws<ArgumentOutOfRangeException>(delegate { new AudioResampler(0); }, "invalid resampler rate");
    }

    private static void CheckSignalStatistics()
    {
        AudioSignalStatistics whole = new AudioSignalStatistics();
        Equal(0L, whole.Samples, "empty diagnostic sample count");
        Near(0, whole.Peak, 0, "empty diagnostic peak");
        Near(0, whole.Rms, 0, "empty diagnostic RMS");
        float[] samples = new float[] { 0, 0.5f, -0.5f, 0.25f, 0, Single.NaN, Single.PositiveInfinity };
        whole.Observe(samples);
        Equal(7L, whole.Samples, "diagnostics count all supplied samples");
        Equal(3L, whole.NonZeroSamples, "diagnostics distinguish nonzero signal from silence");
        Equal(2L, whole.NonFiniteSamples, "diagnostics flag invalid values without retaining samples");
        Near(0.5, whole.Peak, 0, "diagnostic absolute peak");
        Near(Math.Sqrt(0.5625 / 7), whole.Rms, 1e-12, "diagnostic RMS sanitizes nonfinite values to zero");
        AudioSignalStatistics chunks = new AudioSignalStatistics();
        chunks.Observe(new float[] { 0, 0.5f, -0.5f });
        chunks.Observe(new float[] { 0.25f, 0, Single.NaN, Single.PositiveInfinity });
        Equal(whole.Samples, chunks.Samples, "diagnostic count independent of chunk boundaries");
        Near(whole.Rms, chunks.Rms, 0, "diagnostic RMS independent of chunk boundaries");
        whole.Reset();
        Equal(0L, whole.Samples, "interval diagnostic reset clears sample count");
        Equal(0L, whole.NonZeroSamples, "interval diagnostic reset clears signal count");
        Equal(0L, whole.NonFiniteSamples, "interval diagnostic reset clears invalid count");
        Near(0, whole.Peak, 0, "interval diagnostic reset clears peak");
        Near(0, whole.Rms, 0, "interval diagnostic reset clears energy");
        whole.Observe(new float[8]);
        Equal(8L, whole.Samples, "silent packets still contain real samples");
        Equal(0L, whole.NonZeroSamples, "silent packets have no nonzero signal");
        Near(0, whole.Rms, 0, "silent packet RMS");
        whole.Reset();
        whole.ObservePcm16(Shorts(0, 8192, -8192));
        Near(0.25, whole.Peak, 0, "playback diagnostics measure actual PCM amplitude");
        Near(Math.Sqrt(0.125 / 3), whole.Rms, 1e-12, "playback PCM RMS");
        whole.Reset();
        whole.ObservePcm16(AudioSamples.ToPcm16(new float[] { 1, -1 }));
        True(whole.Peak <= 0.25001, "playback metrics include the existing PCM clipping bound");
        Throws<ArgumentException>(delegate { whole.ObservePcm16(new byte[1]); }, "diagnostics reject partial PCM samples");
    }

    private static byte[] Shorts(params short[] values) { byte[] result = new byte[values.Length * 2]; Buffer.BlockCopy(values, 0, result, 0, result.Length); return result; }
    private static byte[] Ints(params int[] values) { byte[] result = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, result, 0, result.Length); return result; }
    private static byte[] Floats(params float[] values) { byte[] result = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, result, 0, result.Length); return result; }
    private static void Set16(byte[] bytes, int offset, ushort value) { bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8); }
    private static void Set32(byte[] bytes, int offset, uint value) { Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
    private static void True(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    private static void Equal<T>(T expected, T actual, string reason) { checks++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(reason + ": expected " + expected + ", got " + actual); }
    private static void Near(double expected, double actual, double tolerance, string reason) { checks++; if (Double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance) throw new Exception(reason + ": expected " + expected + ", got " + actual); }
    private static void Throws<T>(Action action, string reason) where T : Exception
    {
        checks++;
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ": " + reason);
    }
}
