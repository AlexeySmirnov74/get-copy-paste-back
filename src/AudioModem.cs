using System;
using System.IO;
using System.Text;

namespace HorizonClient
{
    // Experimental Bell-202-style binary FSK transport. Its rate and resilience
    // must be measured through the actual audio redirection deployment.
    public static class AudioModem
    {
        public const int SampleRate = 48000;
        public const int BaudRate = 1200;
        public const float PeakAmplitude = 0.2f;
        public const int MaxPayloadBytes = 256;
        internal const int SamplesPerBit = SampleRate / BaudRate;
        internal const int HeaderSize = 36;
        internal const int PreambleBits = 96;
        internal const int AcquisitionPreambleBits = 64;
        internal const int SyncBits = 32;
        internal const uint SyncWord = 0xDDAA96F3;
        internal const uint FastReceiptSyncWord = 0x2761CA5E;
        internal const uint CompactFileReportSyncWord = 0xB4925E31;
        internal const int FastReceiptWireSize = 20, FastReceiptTokenSize = 16;
        internal const int GuardSamples = SampleRate / 100;
        private const uint Magic = 0x31414348; // HCA1
        private static readonly uint[] CrcTable = MakeCrcTable();

        public static float[] Encode(Packet packet)
        {
            if (packet == null || packet.Profile != VideoProfile.Compatible ||
                !VisualProtocol.ValidMetadata(packet.Index, packet.Count, packet.TotalLength,
                    packet.Data, VideoProfile.Compatible, packet.ChunkSize))
                throw new ArgumentException("Audio packets require valid metadata and at most 256 bytes.", "packet");
            byte[] bytes = new byte[HeaderSize + packet.Data.Length + 4];
            WriteUInt32(bytes, 0, Magic);
            bytes[4] = 1;
            bytes[5] = 0;
            bytes[6] = (byte)packet.Data.Length;
            bytes[7] = (byte)(packet.Data.Length >> 8);
            Buffer.BlockCopy(packet.TransferId.ToByteArray(), 0, bytes, 8, 16);
            WriteUInt32(bytes, 24, (uint)packet.Index);
            WriteUInt32(bytes, 28, (uint)packet.Count);
            WriteUInt32(bytes, 32, (uint)packet.TotalLength);
            Buffer.BlockCopy(packet.Data, 0, bytes, HeaderSize, packet.Data.Length);
            WriteUInt32(bytes, HeaderSize + packet.Data.Length, Crc32(bytes, HeaderSize + packet.Data.Length));

            return EncodeFrame(bytes, SyncWord);
        }

        internal static float[] EncodeFastReceipt(byte[] wire)
        {
            if (ReadFastReceipt(wire, wire == null ? 0 : wire.Length) == null)
                throw new ArgumentException("Invalid compact receipt wire frame.", "wire");
            return EncodeFrame(wire, FastReceiptSyncWord);
        }

        internal static int CompactFileReportWireLength(byte header)
        { int count = header & 31; return (header & 0xe0) == 0xa0 && count <= 16 ? 36 + 3 * count : 0; }

        internal static byte[] ReadCompactFileReport(byte[] wire, int count)
        {
            if (wire == null || count < 36 || count > 84 || wire.Length < count || CompactFileReportWireLength(wire[0]) != count || Crc32(wire, count - 4) != ReadUInt32(wire, count - 4)) return null;
            byte[] result = new byte[count]; Buffer.BlockCopy(wire, 0, result, 0, count); return result;
        }

        internal static float[] EncodeCompactFileReport(byte[] wire)
        {
            if (ReadCompactFileReport(wire, wire == null ? 0 : wire.Length) == null) throw new ArgumentException("Invalid compact file report.", "wire");
            return EncodeFrame(wire, CompactFileReportSyncWord);
        }

        internal static byte[] ReadFastReceipt(byte[] wire, int count)
        {
            if (wire == null || count != FastReceiptWireSize || wire.Length < count ||
                Crc32(wire, FastReceiptTokenSize) != ReadUInt32(wire, FastReceiptTokenSize)) return null;
            byte[] token = new byte[FastReceiptTokenSize]; Buffer.BlockCopy(wire, 0, token, 0, token.Length); return token;
        }

        private static float[] EncodeFrame(byte[] bytes, uint sync)
        {
            float[] pcm = new float[(PreambleBits + SyncBits + bytes.Length * 10) * SamplesPerBit + GuardSamples * 2];
            int position = GuardSamples;
            double phase = 0;
            for (int bit = 0; bit < PreambleBits; bit++) WriteSymbol(pcm, ref position, ref phase, (bit & 1) != 0);
            for (int bit = SyncBits - 1; bit >= 0; bit--) WriteSymbol(pcm, ref position, ref phase, ((sync >> bit) & 1) != 0);
            foreach (byte value in bytes)
            {
                WriteSymbol(pcm, ref position, ref phase, false);
                for (int bit = 0; bit < 8; bit++) WriteSymbol(pcm, ref position, ref phase, (value & (1 << bit)) != 0);
                WriteSymbol(pcm, ref position, ref phase, true);
            }
            // Gentle packet edges reduce clicks; the acquisition sequence uses
            // the later, unattenuated part of the alternating preamble.
            for (int sample = 0; sample < SamplesPerBit; sample++)
            {
                float gain = (float)sample / SamplesPerBit;
                pcm[GuardSamples + sample] *= gain;
                pcm[position - 1 - sample] *= gain;
            }
            return pcm;
        }

        private static void WriteSymbol(float[] samples, ref int position, ref double phase, bool one)
        {
            double step = 2 * Math.PI * (one ? 1200 : 2200) / SampleRate;
            for (int sample = 0; sample < SamplesPerBit; sample++)
            {
                samples[position++] = (float)(PeakAmplitude * Math.Sin(phase));
                phase += step;
                if (phase >= 2 * Math.PI) phase -= 2 * Math.PI;
            }
        }

        // This creates an ordinary 16-bit mono 48-kHz WAV without playing it.
        public static void WritePcm16Wave(Stream destination, float[] samples)
        {
            if (destination == null || !destination.CanWrite) throw new ArgumentException("A writable stream is required.", "destination");
            if (samples == null) throw new ArgumentNullException("samples");
            if (samples.LongLength > (UInt32.MaxValue - 36L) / 2) throw new ArgumentException("PCM exceeds the WAV size limit.", "samples");
            using (BinaryWriter writer = new BinaryWriter(destination, Encoding.ASCII, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write((uint)(36L + samples.LongLength * 2));
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write((uint)16); writer.Write((ushort)1); writer.Write((ushort)1);
                writer.Write((uint)SampleRate); writer.Write((uint)(SampleRate * 2));
                writer.Write((ushort)2); writer.Write((ushort)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write((uint)(samples.LongLength * 2));
                foreach (float value in samples)
                {
                    float finite = Single.IsNaN(value) || Single.IsInfinity(value) ? 0 : value;
                    writer.Write((short)Math.Round(Math.Max(-1, Math.Min(1, finite)) * 32767));
                }
            }
        }

        internal static bool ValidHeader(byte[] bytes, out int length)
        {
            length = 0;
            if (bytes == null || bytes.Length < HeaderSize || ReadUInt32(bytes, 0) != Magic || bytes[4] != 1 || bytes[5] != 0) return false;
            length = bytes[6] | bytes[7] << 8;
            if (length > MaxPayloadBytes) return false;
            return VisualProtocol.ValidMetadata((int)ReadUInt32(bytes, 24), (int)ReadUInt32(bytes, 28),
                (int)ReadUInt32(bytes, 32), new byte[length], VideoProfile.Compatible, MaxPayloadBytes);
        }

        internal static Packet ReadPacket(byte[] bytes, int count)
        {
            int length;
            if (!ValidHeader(bytes, out length) || count != HeaderSize + length + 4 ||
                Crc32(bytes, HeaderSize + length) != ReadUInt32(bytes, HeaderSize + length)) return null;
            byte[] id = new byte[16];
            byte[] payload = new byte[length];
            Buffer.BlockCopy(bytes, 8, id, 0, id.Length);
            Buffer.BlockCopy(bytes, HeaderSize, payload, 0, length);
            return new Packet { TransferId = new Guid(id), Index = (int)ReadUInt32(bytes, 24), Count = (int)ReadUInt32(bytes, 28),
                TotalLength = (int)ReadUInt32(bytes, 32), Data = payload, ChunkSize = MaxPayloadBytes, Profile = VideoProfile.Compatible };
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            for (int index = 0; index < 4; index++) bytes[offset + index] = (byte)(value >> (index * 8));
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
        }

        private static uint Crc32(byte[] bytes, int length)
        {
            uint crc = UInt32.MaxValue;
            for (int index = 0; index < length; index++) crc = CrcTable[(crc ^ bytes[index]) & 255] ^ (crc >> 8);
            return crc ^ UInt32.MaxValue;
        }

        private static uint[] MakeCrcTable()
        {
            uint[] table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320U ^ (value >> 1) : value >> 1;
                table[index] = value;
            }
            return table;
        }
    }

    // Feed normalized mono samples at 48 kHz on one consumer thread. Arbitrary
    // callback boundaries are supported; synchronization never uses them.
    public sealed class AudioDecoder
    {
        private const int Capacity = AudioModem.SampleRate * 4;
        private const int WindowSamples = 36;
        private readonly float[] ring = new float[Capacity];
        private readonly ToneBasis mark = new ToneBasis(1200);
        private readonly ToneBasis space = new ToneBasis(2200);
        private long first;
        private long end;
        private long search;
        private Frame frame;
        private bool feeding;
        private long inputSamples, nonfiniteSamples, signalSamples, preambleMatches, syncFailures, syncCandidates, byteFailures, headerFailures, crcFailures, overruns, decodedPackets, decodedReceipts, decodedFileReports;
        private double signalSquares, peak, lastPeriod, lastSyncScore;
        public event Action<Packet> PacketReceived;
        internal event Action<byte[]> FastReceiptReceived;
        internal event Action<byte[]> CompactFileReportReceived;

        internal AudioDecoderDiagnostics GetDiagnostics()
        {
            return new AudioDecoderDiagnostics { InputSamples = inputSamples, NonfiniteSamples = nonfiniteSamples, NonzeroSamples = signalSamples,
                Peak = peak, SignalSquares = signalSquares, Rms = inputSamples == 0 ? 0 : Math.Sqrt(signalSquares / inputSamples), PreambleMatches = preambleMatches, SyncFailures = syncFailures, SyncCandidates = syncCandidates,
                ByteFailures = byteFailures, HeaderFailures = headerFailures, CrcFailures = crcFailures, Overruns = overruns,
                Packets = decodedPackets, FastReceipts = decodedReceipts, FileReports = decodedFileReports, BufferedSamples = BufferedSampleCount,
                FrameActive = frame != null, FrameBytes = frame == null ? 0 : frame.Count, ExpectedBytes = frame == null ? 0 : frame.ExpectedBytes,
                LastPeriod = lastPeriod, LastSyncScore = lastSyncScore };
        }

        internal int BufferedSampleCount { get { return (int)(end - first); } }

        public void Reset()
        {
            if (feeding) throw new InvalidOperationException("The decoder cannot be reset during Feed.");
            first = end = search = 0;
            frame = null;
        }

        public void Feed(float[] samples)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            if (feeding) throw new InvalidOperationException("AudioDecoder.Feed requires one consumer.");
            feeding = true;
            try
            {
                int source = 0;
                while (source < samples.Length)
                {
                    int count = Math.Min(4096, samples.Length - source);
                    if (end - first + count > Capacity)
                    {
                        overruns++;
                        // A damaged or abandoned candidate cannot retain an
                        // unbounded stream or displace an entire future frame.
                        frame = null;
                        first = Math.Max(first, end - Capacity / 2);
                        search = Math.Max(search, first);
                    }
                    for (int index = 0; index < count; index++)
                    {
                        float value = samples[source++];
                        inputSamples++;
                        if (Single.IsNaN(value) || Single.IsInfinity(value)) { nonfiniteSamples++; value = 0; }
                        if (value != 0) signalSamples++;
                        double absolute = Math.Abs((double)value); if (absolute > peak) peak = absolute;
                        signalSquares += value * (double)value;
                        ring[(int)(end++ % Capacity)] = value;
                    }
                    Process();
                }
            }
            finally { feeding = false; }
        }

        private void Process()
        {
            for (;;)
            {
                if (frame == null)
                {
                    if (!FindFrame()) return;
                }
                if (frame.NextByte + 10.2 * frame.Period + 4 >= end) return;
                byte value;
                double adjusted;
                if (!ReadByte(frame.NextByte, frame.Period, out value, out adjusted))
                {
                    byteFailures++;
                    Abandon();
                    continue;
                }
                frame.Bytes[frame.Count++] = value;
                // A slow phase loop follows residual clock-estimation error;
                // filtering prevents noisy byte decisions from shifting the
                // whole frame by their full local correction.
                frame.NextByte += 10 * frame.Period + (adjusted - frame.NextByte) * 0.35;
                if (frame.CompactFileReport && frame.Count == 1)
                {
                    frame.ExpectedBytes = AudioModem.CompactFileReportWireLength(value);
                    if (frame.ExpectedBytes == 0) { headerFailures++; Abandon(); continue; }
                }
                if (!frame.FastReceipt && !frame.CompactFileReport && frame.Count == AudioModem.HeaderSize)
                {
                    int length;
                    if (!AudioModem.ValidHeader(frame.Bytes, out length)) { headerFailures++; Abandon(); continue; }
                    frame.ExpectedBytes = AudioModem.HeaderSize + length + 4;
                }
                if (frame.ExpectedBytes != 0 && frame.Count == frame.ExpectedBytes)
                {
                    Packet packet = frame.FastReceipt || frame.CompactFileReport ? null : AudioModem.ReadPacket(frame.Bytes, frame.Count);
                    byte[] token = frame.FastReceipt ? AudioModem.ReadFastReceipt(frame.Bytes, frame.Count) : null;
                    byte[] fileReport = frame.CompactFileReport ? AudioModem.ReadCompactFileReport(frame.Bytes, frame.Count) : null;
                    if (packet == null && token == null && fileReport == null) crcFailures++;
                    search = (long)Math.Floor(frame.NextByte);
                    first = Math.Max(first, search - 8);
                    frame = null;
                    if (packet != null)
                    {
                        decodedPackets++;
                        Action<Packet> handler = PacketReceived;
                        if (handler != null) handler(packet);
                    }
                    if (token != null)
                    {
                        decodedReceipts++;
                        Action<byte[]> handler = FastReceiptReceived;
                        if (handler != null) handler(token);
                    }
                    if (fileReport != null)
                    {
                        decodedFileReports++;
                        Action<byte[]> handler = CompactFileReportReceived;
                        if (handler != null) handler(fileReport);
                    }
                }
            }
        }

        private void Abandon()
        {
            search = (long)Math.Floor(frame.Start + AudioModem.AcquisitionPreambleBits * frame.Period);
            first = Math.Max(first, search - 8);
            frame = null;
        }

        private bool FindFrame()
        {
            const int scanStep = 5;
            int sequenceBits = AudioModem.AcquisitionPreambleBits + AudioModem.SyncBits;
            while (search + sequenceBits * 40.4 + 8 < end)
            {
                if (QuickPreamble(search))
                {
                    preambleMatches++;
                    double bestScore = 0.55;
                    double bestPeriod = 40;
                    double bestStart = search;
                    uint bestSync = AudioModem.SyncWord;
                    for (int drift = -4; drift <= 4; drift++)
                    {
                        double period = 40 + drift * 0.1;
                        for (int candidate = 0; candidate < 3; candidate++)
                        {
                            uint sync = candidate == 0 ? AudioModem.SyncWord : candidate == 1 ? AudioModem.FastReceiptSyncWord : AudioModem.CompactFileReportSyncWord;
                            double score = SyncScore(search, period, sync);
                            if (score > bestScore) { bestScore = score; bestPeriod = period; bestSync = sync; }
                        }
                    }
                    if (bestScore > 0.55)
                    {
                        // The alternating continuous-phase waveform repeats
                        // every twelve symbols. Its autocorrelation estimates
                        // sample-clock rate without data or tone-phase bias.
                        bestPeriod = EstimatePeriod(search);
                        bestScore = 0;
                        for (int offset = -24; offset <= 24; offset++)
                        {
                            double start = search + offset * 0.5;
                            if (start < first) continue;
                            double score = SyncScore(start, bestPeriod, bestSync);
                            if (score > bestScore) { bestScore = score; bestStart = start; }
                        }
                        if (bestScore < 0.55) { syncFailures++; search += scanStep; continue; }
                        bool fast = bestSync == AudioModem.FastReceiptSyncWord;
                        syncCandidates++; lastPeriod = bestPeriod; lastSyncScore = bestScore;
                        frame = new Frame { Start = bestStart, Period = bestPeriod, NextByte = bestStart + sequenceBits * bestPeriod,
                            FastReceipt = fast, CompactFileReport = bestSync == AudioModem.CompactFileReportSyncWord, ExpectedBytes = fast ? AudioModem.FastReceiptWireSize : 0 };
                        first = Math.Max(first, (long)Math.Floor(bestStart) - 8);
                        return true;
                    }
                    syncFailures++;
                }
                search += scanStep;
            }
            first = Math.Max(first, search - 8);
            return false;
        }

        private bool QuickPreamble(double start)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                double purity;
                double soft = Symbol(start + bit * 40, 40, out purity);
                if (purity < 0.35 || (((bit & 1) != 0) ? soft : -soft) < 0.2) return false;
            }
            return true;
        }

        private double EstimatePeriod(double start)
        {
            double best = 480;
            double score = Double.NegativeInfinity;
            for (int candidate = -24; candidate <= 24; candidate++)
            {
                double delay = 480 + candidate * 0.25;
                double correlation = RepeatedWaveScore(start + 120, delay);
                if (correlation > score) { score = correlation; best = delay; }
            }
            double center = best;
            for (int candidate = -25; candidate <= 25; candidate++)
            {
                double delay = center + candidate * 0.01;
                double correlation = RepeatedWaveScore(start + 120, delay);
                if (correlation > score) { score = correlation; best = delay; }
            }
            return best / 12;
        }

        private double RepeatedWaveScore(double start, double delay)
        {
            double product = 0, firstPower = 0, secondPower = 0;
            for (int index = 0; index < 1500; index++)
            {
                double one = Interpolate(start + index);
                double two = Interpolate(start + index + delay);
                product += one * two;
                firstPower += one * one;
                secondPower += two * two;
            }
            return firstPower * secondPower > 1e-15 ? product / Math.Sqrt(firstPower * secondPower) : 0;
        }

        private double Interpolate(double position)
        {
            long low = (long)Math.Floor(position);
            double fraction = position - low;
            return Sample(low) * (1 - fraction) + Sample(low + 1) * fraction;
        }

        private double SyncScore(double start, double period, uint sync)
        {
            double sum = 0;
            int wrong = 0;
            for (int bit = 0; bit < AudioModem.AcquisitionPreambleBits + AudioModem.SyncBits; bit++)
            {
                bool expected = bit < AudioModem.AcquisitionPreambleBits ? (bit & 1) != 0 :
                    ((sync >> (AudioModem.SyncBits - 1 - (bit - AudioModem.AcquisitionPreambleBits))) & 1) != 0;
                double purity;
                double soft = Symbol(start + bit * period, period, out purity);
                double signed = expected ? soft : -soft;
                if (signed < 0 || purity < 0.25) if (++wrong > 2) return 0;
                sum += signed * Math.Min(1, purity);
            }
            return sum / (AudioModem.AcquisitionPreambleBits + AudioModem.SyncBits);
        }

        private bool ReadByte(double predicted, double period, out byte value, out double adjusted)
        {
            value = 0;
            adjusted = predicted;
            double bestScore = Double.NegativeInfinity;
            for (int correction = -4; correction <= 4; correction++)
            {
                double position = predicted + correction;
                if (position < first) continue;
                byte candidate = 0;
                double score = 0;
                bool valid = true;
                for (int bit = 0; bit < 10; bit++)
                {
                    double purity;
                    double soft = Symbol(position + bit * period, period, out purity);
                    if (purity < 0.2 || (bit == 0 && soft > -0.12) || (bit == 9 && soft < 0.12)) { valid = false; break; }
                    if (bit > 0 && bit < 9 && soft >= 0) candidate |= (byte)(1 << (bit - 1));
                    score += Math.Abs(soft) * Math.Min(1, purity);
                }
                score -= Math.Abs(correction) * 0.015;
                if (valid && score > bestScore) { bestScore = score; value = candidate; adjusted = position; }
            }
            return bestScore > 3;
        }

        private double Symbol(double position, double period, out double purity)
        {
            double mc = 0, ms = 0, sc = 0, ss = 0, power = 0;
            for (int index = 0; index < WindowSamples; index++)
            {
                double at = position + (index + 2.5) * period / AudioModem.SamplesPerBit;
                long low = (long)Math.Floor(at);
                double fraction = at - low;
                double value = Sample(low) * (1 - fraction) + Sample(low + 1) * fraction;
                power += value * value;
                mc += value * mark.Cos[index]; ms += value * mark.Sin[index];
                sc += value * space.Cos[index]; ss += value * space.Sin[index];
            }
            double one = mark.Energy(mc, ms);
            double zero = space.Energy(sc, ss);
            purity = power > 1e-12 ? Math.Max(one, zero) / power : 0;
            return one + zero > 1e-12 ? (one - zero) / (one + zero) : 0;
        }

        private float Sample(long position)
        {
            return position < first || position >= end ? 0 : ring[(int)(position % Capacity)];
        }

        private sealed class Frame
        {
            internal double Start, Period, NextByte;
            internal readonly byte[] Bytes = new byte[AudioModem.HeaderSize + AudioModem.MaxPayloadBytes + 4];
            internal int Count, ExpectedBytes;
            internal bool FastReceipt, CompactFileReport;
        }

        private sealed class ToneBasis
        {
            internal readonly double[] Cos = new double[WindowSamples];
            internal readonly double[] Sin = new double[WindowSamples];
            private readonly double cc, ss, cs, determinant;

            internal ToneBasis(int frequency)
            {
                for (int index = 0; index < WindowSamples; index++)
                {
                    double angle = 2 * Math.PI * frequency * (index + 2.5) / AudioModem.SampleRate;
                    Cos[index] = Math.Cos(angle); Sin[index] = Math.Sin(angle);
                    cc += Cos[index] * Cos[index]; ss += Sin[index] * Sin[index]; cs += Cos[index] * Sin[index];
                }
                determinant = cc * ss - cs * cs;
            }

            internal double Energy(double cosine, double sine)
            {
                return Math.Max(0, (cosine * cosine * ss - 2 * cosine * sine * cs + sine * sine * cc) / determinant);
            }
        }
    }

    internal sealed class AudioDecoderDiagnostics
    {
        internal long InputSamples, NonfiniteSamples, NonzeroSamples, PreambleMatches, SyncFailures, SyncCandidates, ByteFailures, HeaderFailures, CrcFailures, Overruns, Packets, FastReceipts, FileReports;
        internal int BufferedSamples, FrameBytes, ExpectedBytes;
        internal bool FrameActive;
        internal double Peak, Rms, SignalSquares, LastPeriod, LastSyncScore;
    }
}
