using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HorizonClient;

// Uses the same embedded libraries and managed transport as the portable EXEs.
// All audio is synthetic PCM; this test never enumerates or opens a device.
internal static class NativeAudioIntegrationTests
{
    private static int checks;

    private static int Main()
    {
        try
        {
            Check(AudioModem.SampleRate == 48000, "Native integration uses mono 48000 Hz PCM.");
            foreach (AudioModemKind kind in new[] { AudioModemKind.Quiet, AudioModemKind.Aicodix })
                foreach (int length in new[] { 1, 256, 257, 8193 }) NativeTransfer(kind, length);
            AutomaticDetection();
            Console.WriteLine("Native audio integration checks passed: " + checks + ". PCM16/0.67 gain, native wire batches, complete transfer assembly, and FSK/Quiet/aicodix automatic detection. No audio devices were opened.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void NativeTransfer(AudioModemKind kind, int length)
    {
        NativeAudioCodec codec = NativeAudioCodec.Get(kind);
        Guid transfer = Guid.NewGuid();
        byte[] source = Bytes(length, 901 + length);
        int count = (length + AudioModem.MaxPayloadBytes - 1) / AudioModem.MaxPayloadBytes;
        var assembler = new TransferAssembler();
        byte[] completed = null;
        long samples = 0;
        int batches = 0, receivedPackets = 0, completions = 0;
        Stopwatch cpu = Stopwatch.StartNew();
        using (NativeAudioCodec.Decoder decoder = codec.NewDecoder())
        {
            for (int first = 0; first < count;)
            {
                AudioEncodedBlock encoded = AudioTransmissionEncoder.Encode(kind, transfer, first, source);
                Check(encoded.Packets > 0 && encoded.Packets <= count - first, "Native encoder produces a bounded packet batch.");
                Check(encoded.WireBytes <= codec.MaximumPayload, "Batch fits the advertised native payload capacity.");
                Quantize(encoded.Samples);
                samples += encoded.Samples.Length; batches++;
                var packets = new List<Packet>();
                // These capture blocks deliberately cross the wrapper's 8192
                // sample native-call boundary as well as packet/symbol edges.
                int[] chunks = { 1, 127, 8192, 313, 48000, 17, 2048 };
                for (int offset = 0, chunk = 0; offset < encoded.Samples.Length;)
                {
                    int take = Math.Min(chunks[chunk++ % chunks.Length], encoded.Samples.Length - offset);
                    float[] piece = Slice(encoded.Samples, offset, take); offset += take;
                    foreach (byte[] wire in decoder.Feed(piece))
                    {
                        Packet[] decoded = AudioPacketWire.Decode(kind, wire);
                        Check(decoded != null, "Native decoded wire passes the Horizon batch CRC and metadata checks.");
                        packets.AddRange(decoded);
                    }
                }
                Check(packets.Count == encoded.Packets, "Every native batch produces exactly its declared packet count, using only the encoded PCM and its included tail.");
                for (int index = 0; index < packets.Count; index++)
                {
                    SamePacket(Expected(transfer, source, first + index), packets[index]);
                    byte[] result = assembler.Add(packets[index]);
                    if (result != null) { completed = result; completions++; }
                }
                receivedPackets += packets.Count; first += encoded.Packets;
            }
            Check(decoder.Feed(new float[0]).Length == 0, "No extra decoded messages remain after draining a transfer.");
        }
        cpu.Stop();
        Check(receivedPackets == count && completions == 1, "The complete logical transfer is assembled once from all packet batches.");
        Equal(source, completed, "Assembled native transfer preserves every byte, including trailing zeros.");
        double seconds = samples / (double)AudioModem.SampleRate;
        Console.WriteLine("{0}: payload_bytes={1}, packets={2}, native_batches={3}, samples={4}, audio_seconds={5:F6}, net_payload_bit_s={6:F2}, encode_decode_ms={7}; includes all managed 0.5-second tails",
            AudioModemCatalog.GetId(kind), length, count, batches, samples, seconds, 8.0 * length / seconds, cpu.ElapsedMilliseconds);
    }

    private static void AutomaticDetection()
    {
        AudioModemKind[] kinds = { AudioModemKind.Fsk, AudioModemKind.Quiet, AudioModemKind.Aicodix };
        var expected = new Dictionary<Guid, Packet>();
        var payloads = new Dictionary<Guid, byte[]>();
        var waves = new List<float[]>();
        long totalSamples = 0;
        foreach (AudioModemKind kind in kinds)
        {
            Guid id = Guid.NewGuid();
            byte[] control = Bytes(84, 4810 + (int)kind);
            expected.Add(id, Expected(id, control, 0)); payloads.Add(id, control);
            AudioEncodedBlock encoded = AudioTransmissionEncoder.Encode(kind, id, 0, control);
            Check(encoded.Packets == 1, "An 84-byte control message stays in one logical packet on each modem.");
            Quantize(encoded.Samples); waves.Add(encoded.Samples); totalSamples += encoded.Samples.Length;
        }
        Check(totalSamples < AudioModem.SampleRate * 10, "The combined three-modem stream fits within the bounded ten-second worker queues.");
        object gate = new object();
        var received = new HashSet<Guid>();
        Exception failure = null;
        bool callbacksClosed = false;
        int lateCallbacks = 0, callbacks = 0;
        using (var complete = new ManualResetEvent(false))
        {
            var receiver = new AudioReceiveMultiplexer(delegate(Packet packet)
            {
                lock (gate)
                {
                    // The wait handle outlives receiver disposal. Keep a guard
                    // in the callback so a late native callback is reported
                    // without touching a disposed synchronization primitive.
                    if (callbacksClosed) { lateCallbacks++; return; }
                    try
                    {
                        callbacks++;
                        Packet wanted;
                        Check(packet != null && expected.TryGetValue(packet.TransferId, out wanted), "Automatic detection emits only the three intended transfer IDs.");
                        wanted = expected[packet.TransferId]; SamePacket(wanted, packet);
                        Check(received.Add(packet.TransferId), "Automatic detection emits each control frame once.");
                        Equal(payloads[packet.TransferId], new TransferAssembler().Add(packet), "Automatically detected control packet reassembles to the complete payload.");
                        if (received.Count == kinds.Length) complete.Set();
                    }
                    catch (Exception error) { failure = error; complete.Set(); }
                }
            }, true);
            try
            {
                foreach (float[] wave in waves)
                    for (int offset = 0; offset < wave.Length; offset += 960)
                        receiver.Feed(Slice(wave, offset, Math.Min(960, wave.Length - offset)));
                Check(complete.WaitOne(15000), "Both native workers and the FSK path auto-detect the combined PCM stream within fifteen seconds.");
                lock (gate)
                {
                    if (failure != null) throw new InvalidOperationException("Automatic detection callback failed.", failure);
                    Check(received.Count == kinds.Length && callbacks == kinds.Length, "One receiver accepts FSK, Quiet and aicodix without a selected receive modem.");
                }
            }
            finally { receiver.Dispose(); }
            lock (gate) { callbacksClosed = true; }
            // Disposal is idempotent, and later capture callbacks are ignored.
            receiver.Dispose();
            foreach (float[] wave in waves) receiver.Feed(wave);
            lock (gate) { Check(lateCallbacks == 0 && callbacks == kinds.Length, "Disposed receiver ignores subsequent capture callbacks."); }
        }
        Console.WriteLine("Automatic detection: FSK + Quiet + aicodix, three distinct 84-byte messages, {0} samples ({1:F6} seconds), complete payloads PASS",
            totalSamples, totalSamples / (double)AudioModem.SampleRate);
    }

    private static Packet Expected(Guid transfer, byte[] source, int index)
    {
        int offset = index * AudioModem.MaxPayloadBytes;
        int length = Math.Min(AudioModem.MaxPayloadBytes, source.Length - offset);
        var data = new byte[length]; Buffer.BlockCopy(source, offset, data, 0, length);
        return new Packet { TransferId = transfer, Index = index, Count = (source.Length + AudioModem.MaxPayloadBytes - 1) / AudioModem.MaxPayloadBytes,
            TotalLength = source.Length, Data = data, ChunkSize = AudioModem.MaxPayloadBytes, Profile = VideoProfile.Compatible };
    }

    private static void SamePacket(Packet expected, Packet actual)
    {
        Check(actual != null && expected.TransferId == actual.TransferId && expected.Index == actual.Index && expected.Count == actual.Count &&
            expected.TotalLength == actual.TotalLength && expected.ChunkSize == actual.ChunkSize && expected.Profile == actual.Profile,
            "Native audio preserves all packet identity, layout and transfer metadata.");
        Equal(expected.Data, actual.Data, "Native audio preserves each packet's exact binary data.");
        Check(VisualProtocol.ValidMetadata(actual.Index, actual.Count, actual.TotalLength, actual.Data, actual.Profile, actual.ChunkSize), "Decoded packet remains valid for the existing transfer assembler.");
    }

    private static byte[] Bytes(int length, int seed)
    {
        byte[] data = new byte[length]; new Random(seed).NextBytes(data);
        data[0] = 0; data[length - 1] = 0;
        return data;
    }
    private static float[] Slice(float[] samples, int offset, int count) { var result = new float[count]; Array.Copy(samples, offset, result, 0, count); return result; }
    private static void Quantize(float[] samples)
    {
        Check(samples != null && samples.Length > 0, "Encoder returns a nonempty PCM frame.");
        float peak = 0;
        for (int index = 0; index < samples.Length; index++)
        {
            float value = samples[index];
            if (Single.IsNaN(value) || Single.IsInfinity(value) || Math.Abs(value) > AudioModem.PeakAmplitude + 0.0001f)
                throw new InvalidOperationException("Encoder returned invalid or unclamped PCM.");
            peak = Math.Max(peak, Math.Abs(value));
            samples[index] = (float)(Math.Round(value * 0.67 * Int16.MaxValue) / Int16.MaxValue);
        }
        Check(peak > 0, "Encoded PCM contains an audible-band data waveform before attenuation.");
    }
    private static void Equal(byte[] expected, byte[] actual, string message)
    {
        Check(actual != null && actual.Length == expected.Length, message + " Length.");
        for (int index = 0; index < expected.Length; index++) if (expected[index] != actual[index]) throw new InvalidOperationException(message + " Byte " + index + ".");
        Interlocked.Increment(ref checks);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); Interlocked.Increment(ref checks); }
}
