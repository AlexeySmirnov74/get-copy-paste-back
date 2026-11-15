using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HorizonClient;

// Embedded codec and production receive workers, entirely synthetic PCM.
internal static class NativeAudioRecoveryTests
{
    private static int checks;
    private static int Main()
    {
        try
        {
            foreach (int kind in new[] { 0, 1, 2 }) RecoverAfterControl(kind);
            InvalidControlDoesNotReset();
            Console.WriteLine("Native interrupted-frame recovery checks passed: " + checks + ". No audio devices were opened.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void RecoverAfterControl(int kind)
    {
        bool compact = kind == 1;
        Guid transfer = Guid.NewGuid();
        byte[] bytes = new byte[12 * 256]; new Random(713).NextBytes(bytes);
        float[] first = NativeFrame(transfer, bytes, 0), broken = NativeFrame(transfer, bytes, 4), next = NativeFrame(transfer, bytes, 8);
        Array.Resize(ref broken, broken.Length / 2);
        byte[] wire = Control(compact);
        float[] control = compact ? AudioModem.EncodeCompactFileReport(wire) : AudioModem.EncodeFastReceipt(wire);
        Guid controlTransfer = kind == 2 ? Guid.NewGuid() : Guid.Empty;
        if (kind == 2) control = AudioModem.Encode(new Packet { TransferId = controlTransfer, Index = 0, Count = 1, TotalLength = 1,
            Data = new byte[] { 121 }, ChunkSize = 256, Profile = VideoProfile.Compatible });
        Quantize(control);
        float[] interrupted = Join(broken, control, next);
        Check(interrupted.Length < AudioModem.SampleRate * 10, "Interrupted frame, FSK control and one next native frame fit the bounded queue.");
        object gate = new object(); var found = new HashSet<int>(); Exception failure = null; int controls = 0; bool closed = false;
        using (var firstDone = new ManualResetEvent(false))
        using (var nextDone = new ManualResetEvent(false))
        {
            Action<byte[]> onControl = delegate(byte[] value) { lock (gate) { if (!closed) controls++; } };
            var receiver = new AudioReceiveMultiplexer(delegate(Packet packet)
            {
                lock (gate)
                {
                    if (closed) return;
                    try
                    {
                        if (packet.TransferId == controlTransfer)
                        { Check(kind == 2 && packet.Data.Length == 1 && packet.Data[0] == 121, "Ordinary FSK control remains exact."); controls++; return; }
                        Check(packet.TransferId == transfer && (packet.Index < 4 || packet.Index >= 8), "Only intact native frames are published.");
                        Check(found.Add(packet.Index), "Intact native packet is delivered once.");
                        for (int i = 0; i < packet.Data.Length; i++) Check(packet.Data[i] == bytes[packet.Index * 256 + i], "Recovered packet bytes are exact.");
                        if (found.Contains(3)) firstDone.Set();
                        if (found.Contains(11)) nextDone.Set();
                    }
                    catch (Exception error) { failure = error; firstDone.Set(); nextDone.Set(); }
                }
            }, true, compact ? null : onControl, compact ? onControl : null);
            try
            {
                Feed(receiver, first, 617);
                Check(firstDone.WaitOne(15000), "The first native frame is decoded before interruption.");
                // A deliberately large callback exercises ordering when a
                // single input block contains both the FSK control and native data.
                receiver.Feed(interrupted);
                Check(nextDone.WaitOne(15000), "The FIRST clean native frame after a verified interrupting control is recovered.");
                lock (gate)
                {
                    if (failure != null) throw new InvalidOperationException("Native recovery callback failed.", failure);
                    Check(controls == 1 && found.Count == 8, "One control and both intact native frames survive an interrupted frame.");
                }
            }
            finally { receiver.Dispose(); lock (gate) closed = true; }
        }
    }

    private static void InvalidControlDoesNotReset()
    {
        byte[] wire = Control(false); wire[wire.Length - 1] ^= 1;
        MethodInfo encode = typeof(AudioModem).GetMethod("EncodeFrame", BindingFlags.Static | BindingFlags.NonPublic);
        float[] invalid = (float[])encode.Invoke(null, new object[] { wire, AudioModem.FastReceiptSyncWord });
        Guid transfer = Guid.NewGuid(); byte[] bytes = new byte[256]; new Random(121).NextBytes(bytes);
        int controls = 0;
        using (var done = new ManualResetEvent(false))
        using (var receiver = new AudioReceiveMultiplexer(delegate(Packet packet) { if (packet.TransferId == transfer) done.Set(); }, true,
            delegate(byte[] value) { Interlocked.Increment(ref controls); }))
        {
            receiver.Feed(Join(invalid, NativeFrame(transfer, bytes, 0)));
            Check(done.WaitOne(15000), "Native frame following a corrupt FSK candidate remains decodable.");
            Check(controls == 0, "A corrupt FSK frame is never recognized as a control.");
            Array workers = (Array)typeof(AudioReceiveMultiplexer).GetField("workers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(receiver);
            foreach (object worker in workers)
                Check((long)worker.GetType().GetField("resets", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(worker) == 0, "Unverified FSK does not reset either native decoder.");
        }
    }

    private static float[] NativeFrame(Guid transfer, byte[] bytes, int first)
    {
        int packets; byte[] wire = AudioPacketWire.Encode(AudioModemKind.Quiet, transfer, first, bytes, AudioPacketWire.OverheadBytes + 4 * 256, out packets);
        float[] wave = NativeAudioCodec.Get(AudioModemKind.Quiet).Encode(wire); Quantize(wave); return wave;
    }
    private static byte[] Control(bool compact)
    {
        byte[] wire = new byte[compact ? 36 : 20]; new Random(compact ? 314 : 317).NextBytes(wire);
        if (compact) wire[0] = 0xa0;
        uint crc = UInt32.MaxValue;
        for (int i = 0; i < wire.Length - 4; i++) { crc ^= wire[i]; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320U : 0); }
        crc ^= UInt32.MaxValue;
        for (int i = 0; i < 4; i++) wire[wire.Length - 4 + i] = (byte)(crc >> (8 * i));
        return wire;
    }
    private static void Quantize(float[] wave) { for (int i = 0; i < wave.Length; i++) wave[i] = (float)(Math.Round(wave[i] * .67 * Int16.MaxValue) / Int16.MaxValue); }
    private static float[] Join(params float[][] waves)
    {
        int length = 0; foreach (float[] wave in waves) length += wave.Length;
        var result = new float[length]; int at = 0;
        foreach (float[] wave in waves) { Array.Copy(wave, 0, result, at, wave.Length); at += wave.Length; }
        return result;
    }
    private static void Feed(AudioReceiveMultiplexer receiver, float[] wave, int chunk)
    {
        for (int at = 0; at < wave.Length; at += chunk)
        { int count = Math.Min(chunk, wave.Length - at); var part = new float[count]; Array.Copy(wave, at, part, 0, count); receiver.Feed(part); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); Interlocked.Increment(ref checks); }
}
