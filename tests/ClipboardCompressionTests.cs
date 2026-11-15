using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using HorizonClient;

// Synthetic inputs only: no clipboard, user directories, devices or publication.
internal static class ClipboardCompressionTests
{
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HCBPAY01");
    private static int checks;

    private static int Main()
    {
        string parent = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string fixture = Path.Combine(parent, "clipboard-compression-" + Guid.NewGuid().ToString("N"));
        try
        {
            TextAndImageCompatibility();
            Directory.CreateDirectory(fixture);
            MixedFiles(fixture);
            MalformedEnvelopes();
            Measurements();
            FinalWireSizeBoundary();
            Console.WriteLine("Clipboard compression checks passed: " + checks + ". Synthetic data and dedicated test files only.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string absolute = Path.GetFullPath(fixture);
            if (Path.GetDirectoryName(absolute).Equals(parent, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(absolute).StartsWith("clipboard-compression-", StringComparison.Ordinal) && Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
    }

    private static void TextAndImageCompatibility()
    {
        var formatted = new ClipboardSnapshot();
        formatted.Formats.Add(DataFormats.UnicodeText, RepeatedUnicode(1024 * 1024));
        formatted.Formats.Add(DataFormats.Html, Utf8.GetBytes(Repeat("<p lang='ru'>Привет 日本語 😀 &amp; clipboard</p>\r\n", 3000)));
        formatted.Formats.Add(DataFormats.Rtf, Utf8.GetBytes(Repeat(@"{\rtf1\ansi\b Unicode \u1040?\b0 and repeated formatting}\par ", 3000)));
        byte[] raw = FormatBody(formatted);
        byte[] encoded = VerifyFormats(formatted, "Unicode/HTML/RTF");
        Check(encoded.Length < raw.Length / 20, "Repeated Unicode and formatting must save substantial transmission bytes.");
        Check(encoded.Length <= Envelope(raw, CompressionLevel.Fastest).Length, "Optimal text compression must not regress the legacy Fastest fixture.");

        var image = new ClipboardSnapshot(); image.Formats.Add("PNG", NoisePng(192, 128, 791));
        encoded = VerifyFormats(image, "noisy PNG image"); raw = FormatBody(image);
        Check(encoded.Length <= Envelope(raw, CompressionLevel.NoCompression).Length, "Already-compressed image overhead must remain bounded by the framework's stored DEFLATE stream.");
        using (var stream = new MemoryStream(ClipboardPayload.Decode(encoded).Formats["PNG"], false))
        using (var decoded = Image.FromStream(stream, true, true))
            Check(decoded.Width == 192 && decoded.Height == 128, "The preserved PNG remains a valid image.");
        VerifyFormats(new ClipboardSnapshot { Formats = { { DataFormats.UnicodeText, new byte[0] } } }, "empty text");
    }

    private static byte[] VerifyFormats(ClipboardSnapshot snapshot, string label)
    {
        byte[] raw = FormatBody(snapshot); string description;
        byte[] current = ClipboardPayload.Encode(snapshot, out description);
        Equal(raw, LegacyReadBody(current), label + " new envelope remains readable by the legacy DEFLATE/SHA contract");
        CheckFormats(snapshot, ClipboardPayload.Decode(current), label + " current decoder");
        CheckFormats(snapshot, ClipboardPayload.Decode(Envelope(raw, CompressionLevel.Fastest)), label + " old Fastest envelope");
        Check(!String.IsNullOrEmpty(description), label + " description survives compression");
        return current;
    }

    private static void MixedFiles(string fixture)
    {
        string root = Path.Combine(fixture, "Смешанный набор 日本語");
        Directory.CreateDirectory(Path.Combine(root, "empty")); Directory.CreateDirectory(Path.Combine(root, "подпапка"));
        var expected = new List<PayloadFile>();
        string prefix = Path.GetFileName(root);
        expected.Add(new PayloadFile { Path = prefix, IsDirectory = true, Data = new byte[0] });
        expected.Add(new PayloadFile { Path = prefix + "\\empty", IsDirectory = true, Data = new byte[0] });
        expected.Add(new PayloadFile { Path = prefix + "\\подпапка", IsDirectory = true, Data = new byte[0] });
        AddFile(root, prefix, expected, "readme.txt", RepeatedUnicode(256 * 1024));
        AddFile(root, prefix, expected, "zero.bin", new byte[0]);
        AddFile(root, prefix, expected, "документ.html", Utf8.GetBytes(Repeat("<b>Смешанный файл 😀</b>\n", 3000)));
        AddFile(root, prefix, expected, "изображение.png", NoisePng(128, 96, 125));
        AddFile(root, prefix, expected, "случайные.bin", RandomBytes(256 * 1024, 244));
        AddFile(root, prefix, expected, "архив.zip", ZipBytes());
        AddFile(root, prefix, expected, "подпапка\\ユニコード.txt", Utf8.GetBytes("Файл с Unicode-именем\r\n日本語\t😀"));
        expected.Sort(delegate(PayloadFile left, PayloadFile right) { return StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path); });
        byte[] raw = FileBody(expected); string description;
        byte[] encoded = ClipboardPayload.Encode(new ClipboardSnapshot { Paths = new[] { root } }, out description);
        Equal(raw, LegacyReadBody(encoded), "Mixed files preserve the original body schema for old receivers");
        foreach (byte[] envelope in new[] { encoded, Envelope(raw, CompressionLevel.Fastest) })
        {
            DecodedPayload decoded = ClipboardPayload.Decode(envelope);
            Check(decoded.IsFileDrop && decoded.Files.Count == expected.Count && decoded.Formats.Count == 0, "Mixed files and directories retain their types/count");
            var byName = new Dictionary<string, PayloadFile>(StringComparer.Ordinal);
            foreach (PayloadFile entry in decoded.Files) byName.Add(entry.Path, entry);
            foreach (PayloadFile entry in expected)
            {
                Check(byName.ContainsKey(entry.Path) && byName[entry.Path].IsDirectory == entry.IsDirectory, "Unicode relative path and directory flags survive");
                Equal(entry.Data, byName[entry.Path].Data, "Mixed file bytes, including ZIP/random/empty entries");
            }
        }
        Check(encoded.Length <= Envelope(raw, CompressionLevel.Fastest).Length, "Mixed compressible/already-compressed files do not regress the old Fastest fixture.");

        // Incompressible standalone file is the important stored-block fallback.
        string randomPath = Path.Combine(fixture, "already-compressed.bin"); byte[] random = RandomBytes(1024 * 1024, 914);
        File.WriteAllBytes(randomPath, random);
        byte[] randomBody = FileBody(new[] { new PayloadFile { Path = Path.GetFileName(randomPath), Data = random } });
        byte[] randomEncoded = ClipboardPayload.Encode(new ClipboardSnapshot { Paths = new[] { randomPath } }, out description);
        Equal(randomBody, LegacyReadBody(randomEncoded), "Incompressible new stream remains a valid legacy envelope");
        Equal(random, ClipboardPayload.Decode(randomEncoded).Files[0].Data, "Incompressible file round-trip");
        int storedBound = Envelope(randomBody, CompressionLevel.NoCompression).Length;
        Check(randomEncoded.Length <= storedBound, "Auto mode must avoid unbounded compression expansion for random files.");
    }

    private static void MalformedEnvelopes()
    {
        var snapshot = new ClipboardSnapshot(); snapshot.Formats.Add(DataFormats.UnicodeText, RepeatedUnicode(4096));
        byte[] raw = FormatBody(snapshot), valid = Envelope(raw, CompressionLevel.Fastest);
        byte[] copy = (byte[])valid.Clone(); copy[8] ^= 0x80; Reject(copy, "SHA mismatch");
        copy = (byte[])valid.Clone(); copy[7] = (byte)'9'; Reject(copy, "Unknown envelope version");
        foreach (int length in new[] { -1, 0, 8, ClipboardPayload.MaxBytes + 1, raw.Length - 1, raw.Length + 1 })
        { copy = (byte[])valid.Clone(); SetLength(copy, length); Reject(copy, "Invalid declared body length"); }
        copy = new byte[valid.Length / 2]; Buffer.BlockCopy(valid, 0, copy, 0, copy.Length); Reject(copy, "Truncated compressed body");
        copy = (byte[])raw.Clone(); copy[0] = 2; Reject(Envelope(copy, CompressionLevel.Fastest), "Unknown content version with a valid SHA");
        copy = new byte[raw.Length + 1]; Buffer.BlockCopy(raw, 0, copy, 0, raw.Length); Reject(Envelope(copy, CompressionLevel.Fastest), "Unexpected trailing body byte with a valid SHA");

        byte[] stored = StoredEnvelope(raw);
        Equal(raw, LegacyReadBody(stored), "Independent stored-block fixture follows the old DEFLATE contract");
        CheckFormats(snapshot, ClipboardPayload.Decode(stored), "Independent stored-block decoder compatibility");
        copy = (byte[])stored.Clone(); copy[47] ^= 1; Reject(copy, "Stored LEN/NLEN mismatch");
        copy = (byte[])stored.Clone(); copy[49 + raw.Length / 2] ^= 1; Reject(copy, "Corrupted stored literal is caught by SHA");
        copy = (byte[])stored.Clone(); SetLength(copy, raw.Length - 1); Reject(copy, "Stored data exceeds declared raw length");
        copy = (byte[])stored.Clone(); SetLength(copy, raw.Length + 1); Reject(copy, "Stored data ends before declared raw length");
    }

    private static void Measurements()
    {
        Console.WriteLine("Compression benchmark: full Encode wall/process CPU time; old Fastest uses the independent same body/envelope layout, two in-memory repetitions. Process CPU has coarse Windows timer resolution.");
        var warmup = new ClipboardSnapshot(); warmup.Formats.Add(DataFormats.UnicodeText, RepeatedUnicode(10240)); string ignored;
        ClipboardPayload.Encode(warmup, out ignored); LegacyEncode(warmup);
        int[] sizes = { 10 * 1024, 1024 * 1024, 4 * 1024 * 1024 };
        for (int index = 0; index < sizes.Length; index++)
        {
            var text = new ClipboardSnapshot(); text.Formats.Add(DataFormats.UnicodeText, RepeatedUnicode(sizes[index]));
            Measure("Unicode " + sizes[index], text);
            var image = new ClipboardSnapshot(); image.Formats.Add("PNG", NoisePng(index == 0 ? 64 : index == 1 ? 512 : 1024, index == 0 ? 48 : index == 1 ? 512 : 1024, 200 + index));
            Measure("already-compressed PNG " + image.Formats["PNG"].Length, image);
        }
    }

    private static void FinalWireSizeBoundary()
    {
        // Version/count, string prefix + "PNG", and item length occupy17 bytes.
        // These binary format containers are never published as real images.
        var snapshot = new ClipboardSnapshot();
        snapshot.Formats.Add("PNG", RandomBytes(ClipboardPayload.MaxBytes - 17, 6802));
        Check(FormatBody(snapshot).Length == ClipboardPayload.MaxBytes, "Random boundary fixture has an exactly32MiB legal raw body.");
        try
        {
            string description; ClipboardPayload.Encode(snapshot, out description);
            throw new Exception("Incompressible32MiB raw body incorrectly bypassed the final envelope/wire limit.");
        }
        catch (InvalidDataException) { checks++; }

        snapshot.Formats["PNG"] = RepeatedUnicode(ClipboardPayload.MaxBytes - 17);
        Check(FormatBody(snapshot).Length == ClipboardPayload.MaxBytes, "Compressible boundary fixture also has an exactly32MiB legal raw body.");
        string summary; byte[] encoded = ClipboardPayload.Encode(snapshot, out summary);
        Check(encoded.Length <= ClipboardPayload.MaxBytes && encoded.Length < ClipboardPayload.MaxBytes / 50, "A compressible maximum raw body remains accepted within the final wire limit.");
        Equal(snapshot.Formats["PNG"], ClipboardPayload.Decode(encoded).Formats["PNG"], "Maximum compressible raw body restores every byte");
    }

    private static void Measure(string label, ClipboardSnapshot snapshot)
    {
        byte[] raw = FormatBody(snapshot), current = null, legacy = null; string description;
        double legacyMs, currentMs, legacyCpu, currentCpu;
        using (Process process = Process.GetCurrentProcess())
        {
            TimeSpan cpu = process.TotalProcessorTime; var watch = Stopwatch.StartNew();
            for (int pass = 0; pass < 2; pass++) legacy = LegacyEncode(snapshot);
            legacyMs = watch.Elapsed.TotalMilliseconds / 2; legacyCpu = (process.TotalProcessorTime - cpu).TotalMilliseconds / 2;
            cpu = process.TotalProcessorTime; watch.Restart();
            for (int pass = 0; pass < 2; pass++) current = ClipboardPayload.Encode(snapshot, out description);
            currentMs = watch.Elapsed.TotalMilliseconds / 2; currentCpu = (process.TotalProcessorTime - cpu).TotalMilliseconds / 2;
        }
        Equal(raw, LegacyReadBody(current), "Measured envelope compatibility " + label);
        Console.WriteLine("{0}: raw={1}, Fastest={2}, auto={3}, auto/raw={4:F4}, Fastest_ms={5:F2}, auto_ms={6:F2}, Fastest_cpu_ms={7:F2}, auto_cpu_ms={8:F2}", label, raw.Length, legacy.Length, current.Length, (double)current.Length / raw.Length, legacyMs, currentMs, legacyCpu, currentCpu);
    }

    private static byte[] LegacyEncode(ClipboardSnapshot snapshot) { return Envelope(FormatBody(snapshot), CompressionLevel.Fastest); }
    private static byte[] FormatBody(ClipboardSnapshot snapshot)
    {
        return Body(delegate(BinaryWriter writer) {
            writer.Write(1); writer.Write(false); writer.Write(snapshot.Formats.Count);
            foreach (KeyValuePair<string, byte[]> format in snapshot.Formats) { writer.Write(format.Key); writer.Write(format.Value.Length); writer.Write(format.Value); }
        });
    }
    private static byte[] FileBody(IEnumerable<PayloadFile> source)
    {
        var entries = new List<PayloadFile>(source);
        return Body(delegate(BinaryWriter writer) {
            writer.Write(1); writer.Write(true); writer.Write(entries.Count);
            foreach (PayloadFile entry in entries) { writer.Write(entry.Path); writer.Write(entry.IsDirectory); writer.Write(entry.Data.Length); writer.Write(entry.Data); }
        });
    }
    private static byte[] Body(Action<BinaryWriter> write)
    {
        using (var stream = new MemoryStream()) { using (var writer = new BinaryWriter(stream, Utf8, true)) write(writer); return stream.ToArray(); }
    }
    private static byte[] Envelope(byte[] raw, CompressionLevel level)
    {
        using (var stream = new MemoryStream())
        {
            WriteHeader(stream, raw);
            using (var compressed = new DeflateStream(stream, level, true)) compressed.Write(raw, 0, raw.Length);
            return stream.ToArray();
        }
    }
    private static byte[] StoredEnvelope(byte[] raw)
    {
        using (var stream = new MemoryStream())
        {
            WriteHeader(stream, raw);
            for (int offset = 0; offset < raw.Length; )
            {
                int count = Math.Min(65535, raw.Length - offset); bool last = offset + count == raw.Length;
                stream.WriteByte(last ? (byte)1 : (byte)0); stream.WriteByte((byte)count); stream.WriteByte((byte)(count >> 8));
                int complement = count ^ 65535; stream.WriteByte((byte)complement); stream.WriteByte((byte)(complement >> 8));
                stream.Write(raw, offset, count); offset += count;
            }
            return stream.ToArray();
        }
    }
    private static void WriteHeader(Stream stream, byte[] raw)
    {
        stream.Write(Magic, 0, Magic.Length);
        using (SHA256 sha = SHA256.Create()) { byte[] hash = sha.ComputeHash(raw); stream.Write(hash, 0, hash.Length); }
        byte[] length = BitConverter.GetBytes(raw.Length); stream.Write(length, 0, length.Length);
    }
    private static byte[] LegacyReadBody(byte[] envelope)
    {
        Check(envelope.Length >= 45 && Encoding.ASCII.GetString(envelope, 0, 8) == "HCBPAY01", "Envelope magic stays backward compatible");
        byte[] raw;
        using (var compressed = new MemoryStream(envelope, 44, envelope.Length - 44, false))
        using (var inflater = new DeflateStream(compressed, CompressionMode.Decompress))
        using (var result = new MemoryStream()) { inflater.CopyTo(result); raw = result.ToArray(); }
        Check(raw.Length == BitConverter.ToInt32(envelope, 40), "Legacy decoder verifies declared body length");
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(raw);
            for (int index = 0; index < hash.Length; index++) if (hash[index] != envelope[8 + index]) throw new Exception("Legacy SHA check failed");
            checks++;
        }
        return raw;
    }

    private static byte[] NoisePng(int width, int height, int seed)
    {
        byte[] pixels = RandomBytes(width * height * 4, seed);
        for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        using (var image = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        using (var stream = new MemoryStream())
        {
            BitmapData data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { for (int row = 0; row < height; row++) Marshal.Copy(pixels, row * width * 4, IntPtr.Add(data.Scan0, row * data.Stride), width * 4); }
            finally { image.UnlockBits(data); }
            image.Save(stream, ImageFormat.Png); return stream.ToArray();
        }
    }
    private static byte[] ZipBytes()
    {
        using (var stream = new MemoryStream())
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                using (Stream entry = archive.CreateEntry("данные.bin", CompressionLevel.Optimal).Open()) { byte[] random = RandomBytes(128 * 1024, 818); entry.Write(random, 0, random.Length); }
                using (Stream entry = archive.CreateEntry("readme.txt", CompressionLevel.Optimal).Open()) { byte[] text = RepeatedUnicode(32768); entry.Write(text, 0, text.Length); }
            }
            return stream.ToArray();
        }
    }
    private static void AddFile(string root, string prefix, List<PayloadFile> entries, string relative, byte[] bytes)
    { File.WriteAllBytes(Path.Combine(root, relative), bytes); entries.Add(new PayloadFile { Path = prefix + "\\" + relative, Data = bytes }); }
    private static byte[] RandomBytes(int count, int seed) { byte[] bytes = new byte[count]; new Random(seed).NextBytes(bytes); return bytes; }
    private static byte[] RepeatedUnicode(int count)
    {
        byte[] text = Utf8.GetBytes("Clipboard буфер обмена 日本語 😀 Ελληνικά\r\n"); byte[] result = new byte[count]; int offset = 0;
        while (offset + text.Length <= result.Length) { Buffer.BlockCopy(text, 0, result, offset, text.Length); offset += text.Length; }
        while (offset < result.Length) result[offset++] = (byte)' '; return result;
    }
    private static string Repeat(string value, int count) { var result = new StringBuilder(value.Length * count); for (int index = 0; index < count; index++) result.Append(value); return result.ToString(); }
    private static void SetLength(byte[] envelope, int length) { Buffer.BlockCopy(BitConverter.GetBytes(length), 0, envelope, 40, 4); }
    private static void CheckFormats(ClipboardSnapshot expected, DecodedPayload actual, string label)
    {
        Check(!actual.IsFileDrop && actual.Files.Count == 0 && actual.Formats.Count == expected.Formats.Count, label + " format structure");
        foreach (KeyValuePair<string, byte[]> format in expected.Formats) Equal(format.Value, actual.Formats[format.Key], label + " exact " + format.Key);
    }
    private static void Equal(byte[] expected, byte[] actual, string label)
    { Check(actual != null && expected.Length == actual.Length, label + " length"); for (int index = 0; index < expected.Length; index++) if (expected[index] != actual[index]) throw new Exception(label + " byte " + index); checks++; }
    private static void Reject(byte[] envelope, string label)
    {
        try { ClipboardPayload.Decode(envelope); }
        catch (InvalidDataException) { checks++; return; }
        catch (EndOfStreamException) { checks++; return; }
        catch (DecoderFallbackException) { checks++; return; }
        throw new Exception("Invalid envelope accepted: " + label);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
}
