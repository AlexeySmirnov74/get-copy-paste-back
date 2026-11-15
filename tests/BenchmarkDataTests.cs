using System;
using System.Security.Cryptography;
using HorizonClient;

internal static class BenchmarkDataTests
{
    private static int checks;
    public static int Main()
    {
        try
        {
            Golden(0UL, 0, "afcd1d7b39a820e2f465b9a16a9e786e4f450980185dc406ec814c72a8b88bf89b74a8516a89391beaa27e740c9fcb53e132451fbe9a822c3cab16c93a1384c5");
            Golden(1UL, 0, "c15c0289ec2d0a9167ec8e65a18debbe5e5532fbeea293f80bc942ee9086c171");
            Golden(0x0123456789abcdefUL, 5, "387a1593d0a1349b5273d5becc6d992eb7902fec67464c3319d4a208809314");
            Golden(UInt64.MaxValue, (1L << 40) + 3, "134d3a079f9355a6df698c77aaee1b9e71f5220d780c93e509018d3de0b73550");
            Golden(0x0123456789abcdefUL, Int64.MaxValue - 15, "38ab00db2fb9c2c3c17c4911877f5b");
            Partitions(); InvalidRanges(); StreamOneHundredMiB();
            Console.WriteLine("Benchmark data checks passed ({0} assertions; independent SplitMix64 goldens and 100 MiB streamed/verified without files).", checks); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Golden(ulong seed, long offset, string hex)
    {
        byte[] data = new byte[hex.Length / 2]; BenchmarkData.Fill(seed, offset, data, 0, data.Length);
        Check(Hex(data) == hex, "Independent BigInt oracle matches exact little-endian bytes");
        Check(BenchmarkData.Verify(seed, offset, data), "Golden range verifies");
        data[data.Length / 2] ^= 0x40;
        Check(!BenchmarkData.Verify(seed, offset, data), "Corrupted golden range is rejected");
    }

    private static void Partitions()
    {
        var random = new Random(61367);
        for (int pass = 0; pass < 100; pass++)
        {
            ulong seed = unchecked((ulong)random.Next() << 40) ^ (ulong)random.Next();
            long start = ((long)random.Next() << 11) + random.Next(0, 8);
            byte[] whole = new byte[random.Next(1, 65536)], partitioned = new byte[whole.Length];
            BenchmarkData.Fill(seed, start, whole, 0, whole.Length);
            int offset = 0;
            while (offset < whole.Length) { int count = Math.Min(random.Next(1, 3000), whole.Length - offset); BenchmarkData.Fill(seed, start + offset, partitioned, offset, count); offset += count; }
            Check(Hex(whole) == Hex(partitioned), "Arbitrary partitions reproduce the same file range");
            Check(BenchmarkData.Verify(seed, start, whole), "Partitioned range verifies");
            Check(!BenchmarkData.Verify(seed ^ 0x12345678UL, start, whole), "Wrong seed is rejected");
            Check(!BenchmarkData.Verify(seed, start + 1, whole), "Wrong byte offset is rejected");
        }
        byte[] guarded = new byte[31]; for (int i = 0; i < guarded.Length; i++) guarded[i] = 0xa5;
        BenchmarkData.Fill(1, 7, guarded, 3, 19);
        for (int i = 0; i < guarded.Length; i++) if (i < 3 || i >= 22) Check(guarded[i] == 0xa5, "Only the requested target slice changes");
        Check(BenchmarkData.Verify(0, Int64.MaxValue, new byte[0]), "Empty range at the signed offset limit is valid");
    }

    private static void InvalidRanges()
    {
        byte[] data = new byte[8];
        Throws(delegate { BenchmarkData.Fill(0, 0, null, 0, 0); });
        Throws(delegate { BenchmarkData.Verify(0, 0, null); });
        Throws(delegate { BenchmarkData.Fill(0, -1, data, 0, 1); });
        Throws(delegate { BenchmarkData.Verify(0, -1, data); });
        Throws(delegate { BenchmarkData.Fill(0, 0, data, -1, 1); });
        Throws(delegate { BenchmarkData.Fill(0, 0, data, 9, 0); });
        Throws(delegate { BenchmarkData.Fill(0, 0, data, 0, -1); });
        Throws(delegate { BenchmarkData.Fill(0, 0, data, 7, 2); });
        Throws(delegate { BenchmarkData.Fill(0, 0, data, Int32.MaxValue, Int32.MaxValue); });
        Throws(delegate { BenchmarkData.Fill(0, Int64.MaxValue, data, 0, 1); });
        Throws(delegate { BenchmarkData.Verify(0, Int64.MaxValue - 1, data); });
    }

    private static void StreamOneHundredMiB()
    {
        const long total = 100L * 1024 * 1024; const ulong seed = 0x0123456789abcdefUL;
        byte[] buffer = new byte[65521];
        using (SHA256 hash = SHA256.Create())
        {
            for (long offset = 0; offset < total; )
            {
                int count = (int)Math.Min(buffer.Length, total - offset);
                byte[] chunk = count == buffer.Length ? buffer : new byte[count];
                BenchmarkData.Fill(seed, offset, chunk, 0, count);
                Check(BenchmarkData.Verify(seed, offset, chunk), "Every streamed range verifies at its exact file offset");
                hash.TransformBlock(chunk, 0, count, chunk, 0);
                if ((offset & 15) == 0)
                {
                    chunk[count / 2] ^= 0x80; Check(!BenchmarkData.Verify(seed, offset, chunk), "Streamed corruption is detected"); chunk[count / 2] ^= 0x80;
                }
                offset += count;
            }
            hash.TransformFinalBlock(new byte[0], 0, 0);
            // Generated independently using Node BigInt arithmetic and SHA-256.
            Check(Hex(hash.Hash) == "8972ccac82e21930ad21ed3c431844f2f7444771ccc478818bb898412a06df87", "Entire 100 MiB stream matches an independent oracle");
        }
    }
    private static string Hex(byte[] data) { return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant(); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static void Throws(Action action) { try { action(); } catch (ArgumentException) { checks++; return; } throw new Exception("Invalid arguments were accepted."); }
}
