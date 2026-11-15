// Synthetic signal and packet fixtures only: no devices, files or clipboard.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HorizonClient;

internal static class Mp4ModemTests
{
    private static int checks;
    private static readonly Random Random = new Random(342719);

    public static int Main()
    {
        try
        {
            GoldenVectors(); ReedSolomonCorrection(); FecInterleaveCorrection();
            AdapterPacketsAndImages(); AdapterCorrectionAndRejection(); InvalidMetadata();
            Console.WriteLine("MP4Modem V2 adapter tests passed: " + checks + " assertions (upstream tag/basis/RS golden vectors, errors+erasures, interleave burst repair, packet CRC, JPEG, resize, tag majority and malformed input).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void GoldenVectors()
    {
        byte[] check = Encoding.ASCII.GetBytes("123456789");
        Check(Mp4ModemProtocol.Crc32(check, 0, check.Length) == 0xcbf43926, "IEEE CRC-32 check value");
        var tag = new Mp4ModemProtocol.CompactTag { Epoch = 7, Phase = 2, Flags = 0, SymbolId = 12345 };
        Check(Hex(Mp4ModemProtocol.PackTag(tag)) == "4d3202810007020000003039d8e867e3", "Upstream compact-tag golden bytes");
        var data = new byte[Mp4ModemProtocol.FrameDataBytes]; for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 37);
        byte[] frame = Mp4ModemProtocol.EncodeBasisFrame(tag, data);
        Check(frame.Length == 1382400, "I420 frame length matches upstream");
        Check(Hash(frame) == "678a62001e87d7b9c6bdffb99f8eb65d2c0fd6b41171442e79d31c89b71a49d3", "Upstream Python/TypeScript basis-frame golden SHA256");
        Mp4ModemProtocol.BasisFrame decoded = Mp4ModemProtocol.DecodeBasisFrame(frame);
        Equal(data, decoded.Data, "Basis demodulation recovers all1656 bytes");
        Check(decoded.Tag.Epoch == 7 && decoded.Tag.Phase == 2 && decoded.Tag.SymbolId == 12345 && decoded.MinimumPilotSeparation > 40, "Pilot calibration and compact tag match the golden frame");
        byte[] message = new byte[191]; for (int i = 0; i < message.Length; i++) message[i] = (byte)i;
        byte[] codeword = Mp4ModemReedSolomon.Encode(message, 64);
        Check(Hash(codeword) == "28a63541dcec4e03805a3a66c05fc5bc8efa595a4f339290ccedb7067a60be10", "Upstream Python/TypeScript RS255191 golden SHA256");
        Equal(message, Mp4ModemReedSolomon.Decode(codeword, 64), "Golden codeword decodes");
    }

    private static void ReedSolomonCorrection()
    {
        for (int trial = 0; trial < 15; trial++)
        {
            var message = new byte[191]; Random.NextBytes(message);
            byte[] damaged = Mp4ModemReedSolomon.Encode(message, 64);
            var positions = ShuffledPositions(255);
            int errors = trial == 0 ? 32 : trial + 1;
            for (int i = 0; i < errors; i++) damaged[positions[i]] ^= (byte)(17 + i);
            Equal(message, Mp4ModemReedSolomon.Decode(damaged, 64), "RS corrects " + errors + " unknown byte errors");
        }
        foreach (int[] counts in new[] { new[] { 20, 10 }, new[] { 64, 0 }, new[] { 30, 17 } })
        {
            var message = new byte[191]; Random.NextBytes(message);
            byte[] damaged = Mp4ModemReedSolomon.Encode(message, 64); int[] positions = ShuffledPositions(255);
            var erasures = new int[counts[0]];
            for (int i = 0; i < counts[0] + counts[1]; i++) damaged[positions[i]] ^= (byte)(1 + i);
            Array.Copy(positions, erasures, erasures.Length);
            Equal(message, Mp4ModemReedSolomon.Decode(damaged, 64, erasures), "RS errors+erasures at supported correction bounds");
        }
        Rejected(delegate { Mp4ModemReedSolomon.Encode(new byte[192], 64); }, "Oversized RS message rejects");
        Rejected(delegate { Mp4ModemReedSolomon.Decode(new byte[255], 64, new[] { -1 }); }, "Invalid erasure position rejects");
    }

    private static void FecInterleaveCorrection()
    {
        byte[] data = new byte[1122]; Random.NextBytes(data);
        byte[] coded = Mp4ModemProtocol.EncodeFec(data);
        Check(coded.Length == 1530, "Six RS blocks occupy1530 interleaved bytes");
        Equal(data, Mp4ModemProtocol.DecodeFec(coded, data.Length, null), "Column-interleaved FEC roundtrip");
        byte[] burst = (byte[])coded.Clone();
        for (int i = 0; i < 192; i++) burst[500 + i] ^= 0x69;
        Equal(data, Mp4ModemProtocol.DecodeFec(burst, data.Length, null), "Column interleave distributes192-byte burst into32 errors per codeword");
        var erasures = new HashSet<int>(); byte[] erased = (byte[])coded.Clone();
        for (int i = 0; i < 384; i++) { erased[i] = 0; erasures.Add(i); }
        Equal(data, Mp4ModemProtocol.DecodeFec(erased, data.Length, erasures), "Column interleave recovers384 known erasures at64 per block");
        byte[] excessive = (byte[])coded.Clone(); for (int i = 0; i < 198; i++) excessive[500 + i] ^= 0x69;
        Rejected(delegate { Mp4ModemProtocol.DecodeFec(excessive, data.Length, null); }, "33 unknown errors per block reject instead of delivering damaged data");
        Rejected(delegate { Mp4ModemProtocol.DecodeFec(new byte[254], 187, null); }, "Malformed encoded length rejects");
    }

    private static void AdapterPacketsAndImages()
    {
        foreach (int length in new[] { 0, 1, 83, 1082, 1083, 2164 + 77 })
        {
            byte[] all = new byte[length]; Random.NextBytes(all); Guid id = Guid.NewGuid();
            int count = Math.Max(1, (length + Mp4ModemProtocol.ChunkSize - 1) / Mp4ModemProtocol.ChunkSize);
            for (int index = 0; index < count; index++)
            {
                Packet expected = MakePacket(id, index, all);
                using (Bitmap frame = Mp4ModemProtocol.EncodeFrame(expected))
                {
                    Check(frame.Width == 1280 && frame.Height == 720, "Adapter frame has fixed upstream resolution");
                    EqualPacket(expected, Mp4ModemProtocol.DecodeFrame(frame), "Adapter packet roundtrip");
                }
            }
        }
        byte[] payload = new byte[1082]; Random.NextBytes(payload); Packet packet = MakePacket(Guid.NewGuid(), 0, payload);
        using (Bitmap frame = Mp4ModemProtocol.EncodeFrame(packet))
        {
            foreach (long quality in new long[] { 20, 50, 90 })
            using (Bitmap compressed = Jpeg(frame, quality)) EqualPacket(packet, Mp4ModemProtocol.DecodeFrame(compressed), "JPEG quality" + quality);
            foreach (Size size in new[] { new Size(320, 180), new Size(320, 240), new Size(640, 360), new Size(1920, 1080) })
            using (Bitmap scaled = Resize(frame, size.Width, size.Height)) EqualPacket(packet, Mp4ModemProtocol.DecodeFrame(scaled), "Centered16:9 resize" + size.Width);
            using (Bitmap letterbox = new Bitmap(1280, 800))
            {
                using (Graphics graphics = Graphics.FromImage(letterbox)) { graphics.Clear(Color.Black); graphics.DrawImageUnscaled(frame, 0, 40); }
                EqualPacket(packet, Mp4ModemProtocol.DecodeFrame(letterbox), "Centered letterbox crop");
            }
            using (Bitmap graded = (Bitmap)frame.Clone())
            {
                using (Graphics graphics = Graphics.FromImage(graded))
                using (var overlay = new SolidBrush(Color.FromArgb(60, Color.White))) graphics.FillRectangle(overlay, 0, 0, graded.Width, graded.Height);
                EqualPacket(packet, Mp4ModemProtocol.DecodeFrame(graded), "Pilots compensate brightness and contrast shift");
            }
        }
    }

    private static void AdapterCorrectionAndRejection()
    {
        byte[] payload = new byte[1082]; Random.NextBytes(payload); Packet packet = MakePacket(Guid.NewGuid(), 0, payload);
        using (Bitmap frame = Mp4ModemProtocol.EncodeFrame(packet))
        {
            // Recover the rendered basis payload independently from bitmap bytes
            // so subsequent damage alters coded symbols while retaining tags.
            Mp4ModemProtocol.BasisFrame original = Mp4ModemProtocol.DecodeBasisFrame(ReadBitmapI420(frame));
            byte[] damaged = (byte[])original.Data.Clone();
            for (int i = 0; i < 192; i++) damaged[500 + i] ^= 0x69;
            using (Bitmap corrected = Mp4ModemProtocol.LuminanceToBitmap(Mp4ModemProtocol.EncodeBasisFrame(original.Tag, damaged)))
                EqualPacket(packet, Mp4ModemProtocol.DecodeFrame(corrected), "Adapter repairs a192-byte burst despite stale outer-frame CRC");
            for (int i = 0; i < 6; i++) damaged[692 + i] ^= 0x69;
            using (Bitmap rejected = Mp4ModemProtocol.LuminanceToBitmap(Mp4ModemProtocol.EncodeBasisFrame(original.Tag, damaged)))
                Check(Mp4ModemProtocol.DecodeFrame(rejected) == null, "Adapter rejects corruption beyond RS correction capacity");
            original.Tag.SymbolId = 1;
            using (Bitmap wrongIndex = Mp4ModemProtocol.LuminanceToBitmap(Mp4ModemProtocol.EncodeBasisFrame(original.Tag, original.Data)))
                Check(Mp4ModemProtocol.DecodeFrame(wrongIndex) == null, "Tag symbol ID must match protected packet index");
            original.Tag.SymbolId = 0; original.Tag.Flags = 0;
            using (Bitmap upstreamData = Mp4ModemProtocol.LuminanceToBitmap(Mp4ModemProtocol.EncodeBasisFrame(original.Tag, original.Data)))
                Check(Mp4ModemProtocol.DecodeFrame(upstreamData) == null, "FullBundle data flags cannot masquerade as adapter frames");
            using (Bitmap oneTagDamaged = (Bitmap)frame.Clone())
            {
                using (Graphics graphics = Graphics.FromImage(oneTagDamaged))
                    for (int i = 0; i < 32; i++) { int block = Mp4ModemProtocol.HeaderIndices[i]; graphics.FillRectangle(Brushes.Black, block % 80 * 16, block / 80 * 16, 16, 16); }
                EqualPacket(packet, Mp4ModemProtocol.DecodeFrame(oneTagDamaged), "Majority tag voting survives one destroyed compact-tag copy");
            }
        }
        Check(Mp4ModemProtocol.DecodeFrame(null) == null, "Null input produces no packet");
        using (Bitmap empty = new Bitmap(1280, 720)) Check(Mp4ModemProtocol.DecodeFrame(empty) == null, "Untagged camera frame produces no packet");
    }

    private static void InvalidMetadata()
    {
        Rejected(delegate { using (Bitmap ignored = Mp4ModemProtocol.EncodeFrame((Packet)null)) { } }, "Null packet rejects");
        foreach (Packet packet in new[] {
            new Packet { Index = 1, Count = 1, TotalLength = 0, Data = new byte[0] },
            new Packet { Index = 0, Count = 2, TotalLength = 0, Data = new byte[0] },
            new Packet { Index = 0, Count = 1, TotalLength = 1, Data = new byte[0] },
            new Packet { Index = 0, Count = 1, TotalLength = -1, Data = new byte[0] },
            new Packet { Index = 0, Count = 1, TotalLength = Int32.MaxValue, Data = new byte[0] }
        }) Rejected(delegate { using (Bitmap ignored = Mp4ModemProtocol.EncodeFrame(packet)) { } }, "Inconsistent packet dimensions reject");
    }

    private static Packet MakePacket(Guid id, int index, byte[] all)
    {
        int count = Math.Max(1, (all.Length + 1081) / 1082), length = Math.Min(1082, all.Length - index * 1082);
        var chunk = new byte[length]; Buffer.BlockCopy(all, index * 1082, chunk, 0, length);
        return new Packet { TransferId = id, Index = index, Count = count, TotalLength = all.Length, Data = chunk, ChunkSize = 1082, Profile = (VideoProfile)11 };
    }
    private static int[] ShuffledPositions(int count)
    {
        var result = new int[count]; for (int i = 0; i < count; i++) result[i] = i;
        for (int i = count - 1; i > 0; i--) { int j = Random.Next(i + 1), old = result[i]; result[i] = result[j]; result[j] = old; } return result;
    }
    private static byte[] ReadBitmapI420(Bitmap frame)
    {
        var result = new byte[1280 * 720 * 3 / 2];
        for (int y = 0; y < 720; y++) for (int x = 0; x < 1280; x++) result[y * 1280 + x] = frame.GetPixel(x, y).R;
        for (int i = 1280 * 720; i < result.Length; i++) result[i] = 128;
        return result;
    }
    private static Bitmap Resize(Bitmap frame, int width, int height)
    {
        var output = new Bitmap(width, height); using (Graphics graphics = Graphics.FromImage(output)) { graphics.InterpolationMode = InterpolationMode.HighQualityBilinear; graphics.DrawImage(frame, 0, 0, width, height); } return output;
    }
    private static Bitmap Jpeg(Bitmap frame, long quality)
    {
        ImageCodecInfo codec = null; foreach (ImageCodecInfo item in ImageCodecInfo.GetImageEncoders()) if (item.MimeType == "image/jpeg") codec = item;
        using (var stream = new MemoryStream())
        using (var parameters = new EncoderParameters(1))
        {
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality); frame.Save(stream, codec, parameters); stream.Position = 0;
            using (var decoded = new Bitmap(stream)) return new Bitmap(decoded);
        }
    }
    private static string Hash(byte[] data) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(data)); }
    private static string Hex(byte[] data) { return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant(); }
    private static void EqualPacket(Packet expected, Packet actual, string message)
    {
        Check(actual != null && actual.TransferId == expected.TransferId && actual.Index == expected.Index && actual.Count == expected.Count && actual.TotalLength == expected.TotalLength && actual.ChunkSize == 1082 && (int)actual.Profile == 11, message + " metadata");
        Equal(expected.Data, actual.Data, message + " payload");
    }
    private static void Equal(byte[] expected, byte[] actual, string message) { Check(actual != null && Hex(expected) == Hex(actual), message); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static void Rejected(Action action, string message) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidDataException) { rejected = true; } Check(rejected, message); }
}
