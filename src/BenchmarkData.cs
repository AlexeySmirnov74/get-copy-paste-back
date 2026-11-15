using System;

namespace HorizonClient
{
    // Seekable synthetic data. The seed and file offset reproduce any byte range
    // without allocating, retaining, or reading a complete benchmark file.
    internal static class BenchmarkData
    {
        internal static void Fill(ulong seed, long fileOffset, byte[] target, int targetOffset, int count)
        {
            if (target == null) throw new ArgumentNullException("target");
            if (targetOffset < 0 || targetOffset > target.Length) throw new ArgumentOutOfRangeException("targetOffset");
            if (count < 0 || count > target.Length - targetOffset) throw new ArgumentOutOfRangeException("count");
            CheckRange(fileOffset, count);
            int end = targetOffset + count, within = (int)(fileOffset & 7); ulong block = (ulong)(fileOffset >> 3);
            while (targetOffset < end)
            {
                ulong value = Mix(seed, block++);
                int take = Math.Min(8 - within, end - targetOffset);
                for (int i = 0; i < take; i++) target[targetOffset++] = (byte)((value >> ((within + i) * 8)) & 255);
                within = 0;
            }
        }

        internal static bool Verify(ulong seed, long fileOffset, byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            CheckRange(fileOffset, data.Length);
            int offset = 0, within = (int)(fileOffset & 7); ulong block = (ulong)(fileOffset >> 3);
            while (offset < data.Length)
            {
                ulong value = Mix(seed, block++); int take = Math.Min(8 - within, data.Length - offset);
                for (int i = 0; i < take; i++) if (data[offset++] != (byte)((value >> ((within + i) * 8)) & 255)) return false;
                within = 0;
            }
            return true;
        }

        private static void CheckRange(long offset, int count)
        {
            if (offset < 0 || offset > Int64.MaxValue - count) throw new ArgumentOutOfRangeException("fileOffset");
        }
        private static ulong Mix(ulong seed, ulong block)
        {
            unchecked
            {
                ulong value = seed + (block + 1) * 0x9E3779B97F4A7C15UL;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                return value ^ (value >> 31);
            }
        }
    }
}
