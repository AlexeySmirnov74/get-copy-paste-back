using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using HorizonClient;

// Headless tests: no access to the system clipboard, cameras, or user files.
internal static class PayloadTests
{
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private static int assertions;

    public static int Main()
    {
        string testRoot = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        string directory = Path.Combine(testRoot, "payload-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            TextFormatsThroughVideo();
            FilesThroughVideo(directory);
            IntegrityAndBounds();
            ParserValidation();
            Console.WriteLine("Payload/integration tests passed ({0} assertions).", assertions);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            string fullDirectory = Path.GetFullPath(directory);
            string expectedPrefix = testRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullDirectory.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullDirectory).StartsWith("payload-tests-", StringComparison.Ordinal)
                && Directory.Exists(fullDirectory))
                Directory.Delete(fullDirectory, true);
        }
    }

    private static void TextFormatsThroughVideo()
    {
        ClipboardSnapshot snapshot = new ClipboardSnapshot();
        snapshot.Formats.Add(DataFormats.UnicodeText, Utf8.GetBytes("Буфер обмена\r\n日本語 😀\tΣ"));
        snapshot.Formats.Add(DataFormats.Html, Utf8.GetBytes("<html><body><b>Привет</b> &amp; 🌍</body></html>"));
        snapshot.Formats.Add(DataFormats.Rtf, Utf8.GetBytes(@"{\rtf1\ansi Hello \u1040?\b clipboard\b0}"));
        using (Bitmap bitmap = new Bitmap(24, 16))
        using (MemoryStream png = new MemoryStream())
        {
            Random random = new Random(752);
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                    bitmap.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
            bitmap.Save(png, ImageFormat.Png);
            snapshot.Formats.Add("PNG", png.ToArray());
        }
        string description;
        byte[] envelope = ClipboardPayload.Encode(snapshot, out description);
        Check(!String.IsNullOrEmpty(description), "Encoded text description");
        foreach (VideoProfile profile in new VideoProfile[] {
            VideoProfile.Compatible, VideoProfile.Fast, VideoProfile.Dense })
        {
            DecodedPayload decoded = ClipboardPayload.Decode(ThroughVideo(envelope, profile));
            Check(!decoded.IsFileDrop && decoded.Formats.Count == 4 && decoded.Files.Count == 0,
                "All four allowed formats preserved through " + profile);
            foreach (KeyValuePair<string, byte[]> format in snapshot.Formats)
                Equal(format.Value, decoded.Formats[format.Key], "Preserved " + format.Key + " through " + profile);
        }
    }

    private static void FilesThroughVideo(string directory)
    {
        string left = Path.Combine(directory, "left");
        string right = Path.Combine(directory, "right");
        Directory.CreateDirectory(Path.Combine(left, "package", "empty"));
        Directory.CreateDirectory(Path.Combine(left, "package", "sub"));
        Directory.CreateDirectory(Path.Combine(right, "package", "empty"));
        byte[] binary = new byte[719];
        new Random(29).NextBytes(binary);
        File.WriteAllBytes(Path.Combine(left, "common.txt"), Utf8.GetBytes("первый файл"));
        File.WriteAllBytes(Path.Combine(right, "common.txt"), new byte[0]);
        File.WriteAllBytes(Path.Combine(left, "package", "sub", "данные.bin"), binary);
        File.WriteAllBytes(Path.Combine(right, "package", "readme.txt"), Utf8.GetBytes("second directory"));
        ClipboardSnapshot snapshot = new ClipboardSnapshot { Paths = new string[] {
            Path.Combine(left, "common.txt"), Path.Combine(right, "common.txt"),
            Path.Combine(left, "package"), Path.Combine(right, "package") } };
        string description;
        byte[] encoded = ClipboardPayload.Encode(snapshot, out description);
        DecodedPayload decoded = ClipboardPayload.Decode(ThroughVideo(encoded));
        Check(decoded.IsFileDrop && decoded.Formats.Count == 0 && decoded.Files.Count == 9,
            "Files and directories preserved");
        Dictionary<string, PayloadFile> files = new Dictionary<string, PayloadFile>();
        foreach (PayloadFile file in decoded.Files) files.Add(file.Path, file);
        Equal(Utf8.GetBytes("первый файл"), files["common.txt"].Data, "First duplicate root");
        Equal(new byte[0], files["common (2).txt"].Data, "Second duplicate root and zero-byte file");
        Check(files["package"].IsDirectory && files["package\\empty"].IsDirectory,
            "Directory and empty directory");
        Check(files["package (2)"].IsDirectory && files["package (2)\\empty"].IsDirectory,
            "Duplicate directory roots renamed");
        Equal(binary, files["package\\sub\\данные.bin"].Data, "Nested binary file bytes");
        Equal(Utf8.GetBytes("second directory"), files["package (2)\\readme.txt"].Data,
            "Contents of renamed root");
        Check(!String.IsNullOrEmpty(description) && !String.IsNullOrEmpty(decoded.Description),
            "File descriptions");
    }

    private static byte[] ThroughVideo(byte[] envelope)
    {
        return ThroughVideo(envelope, VideoProfile.Compatible);
    }

    private static byte[] ThroughVideo(byte[] envelope, VideoProfile profile)
    {
        Guid id = Guid.NewGuid();
        int chunkSize = VisualProtocol.GetChunkSize(profile);
        int count = (envelope.Length + chunkSize - 1) / chunkSize;
        TransferAssembler assembler = new TransferAssembler();
        byte[] result = null;
        // Reverse order and a duplicate model camera frame drops/reordering.
        for (int i = count - 1; i >= 0; i--)
        {
            int length = Math.Min(chunkSize, envelope.Length - i * chunkSize);
            byte[] chunk = new byte[length];
            Buffer.BlockCopy(envelope, i * chunkSize, chunk, 0, length);
            using (Bitmap frame = VisualProtocol.EncodeFrame(id, i, count, envelope.Length, chunk, profile))
            using (Bitmap video = JpegVideo(frame, profile))
            {
                Packet packet = VisualProtocol.DecodeFrame(video);
                Check(packet != null && packet.Index == i, "JPEG transport packet");
                result = assembler.Add(packet) ?? result;
                Check(assembler.Add(packet) == null, "Duplicate camera frame");
            }
        }
        Equal(envelope, result, "Complete compressed envelope over visual channel");
        return result;
    }

    private static Bitmap JpegVideo(Bitmap image, VideoProfile profile)
    {
        int width = profile == VideoProfile.Compatible ? 320 : 1280;
        int height = profile == VideoProfile.Compatible ? 240 : 720;
        using (Bitmap scaled = new Bitmap(width, height))
        using (MemoryStream stream = new MemoryStream())
        using (EncoderParameters parameters = new EncoderParameters(1))
        {
            using (Graphics graphics = Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, new Rectangle(0, 0, width, height));
            }
            ImageCodecInfo jpeg = null;
            foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
                if (codec.MimeType == "image/jpeg") jpeg = codec;
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,
                profile == VideoProfile.Compatible ? 40L : 50L);
            scaled.Save(stream, jpeg, parameters);
            stream.Position = 0;
            using (Bitmap decoded = new Bitmap(stream)) return new Bitmap(decoded);
        }
    }

    private static void IntegrityAndBounds()
    {
        byte[] valid = Envelope(FormatBody(DataFormats.UnicodeText, Utf8.GetBytes("integrity")));
        byte[] tampered = (byte[])valid.Clone();
        tampered[8] ^= 1;
        Reject(tampered, "SHA-256 mismatch");
        tampered = (byte[])valid.Clone();
        tampered[0] ^= 1;
        Reject(tampered, "Bad magic");
        Reject(null, "Null envelope");
        Reject(new byte[44], "Truncated envelope");
        tampered = (byte[])valid.Clone();
        WriteInt32(tampered, 40, -1);
        Reject(tampered, "Negative declared decompressed length");
        WriteInt32(tampered, 40, ClipboardPayload.MaxBytes + 1);
        Reject(tampered, "Declared size greater than 32 MiB");
        Check(ClipboardPayload.MaxBytes == VisualProtocol.MaxTransferBytes
            && ClipboardPayload.MaxBytes == 32 * 1024 * 1024, "Envelope and transport share 32 MiB bound");

        byte[] expansion = new byte[256 * 1024];
        byte[] bomb = Envelope(expansion);
        WriteInt32(bomb, 40, 9);
        Reject(bomb, "Deflate expansion beyond declared size");
        WriteInt32(bomb, 40, ClipboardPayload.MaxBytes + 1);
        Reject(bomb, "Compressed bomb declares too much memory");
        tampered = (byte[])valid.Clone();
        WriteInt32(tampered, 40, 128);
        Reject(tampered, "Premature decompressed end");
        Reject(new byte[ClipboardPayload.MaxBytes + 1], "Envelope larger than 32 MiB");

        // No video loop for the size boundary. Metadata itself consumes space,
        // so a format containing exactly 32 MiB cannot fit in the raw body.
        ClipboardSnapshot large = new ClipboardSnapshot();
        large.Formats.Add(DataFormats.UnicodeText, new byte[ClipboardPayload.MaxBytes]);
        ExpectReject(delegate { string description; ClipboardPayload.Encode(large, out description); },
            "Raw contents plus metadata exceed 32 MiB");
    }

    private static void ParserValidation()
    {
        Reject(Envelope(FormatBody("UnapprovedFormat", new byte[0])), "Unapproved clipboard format");
        Reject(Envelope(FormatBody("", new byte[0])), "Empty format name");
        Reject(Envelope(Body(delegate(BinaryWriter writer) {
            writer.Write(1); writer.Write(false); writer.Write(5);
        })), "Too many clipboard formats");
        Reject(Envelope(Body(delegate(BinaryWriter writer) {
            writer.Write(1); writer.Write(true); writer.Write(ClipboardPayload.MaxEntries + 1);
        })), "Too many file entries");
        Reject(Envelope(Body(delegate(BinaryWriter writer) {
            writer.Write(1); writer.Write(false); writer.Write(1);
            writer.Write(DataFormats.UnicodeText); writer.Write(Int32.MaxValue);
        })), "Huge item size before allocation");
        Reject(Envelope(Body(delegate(BinaryWriter writer) {
            writer.Write(1); writer.Write(false); writer.Write(1);
            writer.Write((byte)2); writer.Write((byte)0xc3); writer.Write((byte)0x28);
            writer.Write(0);
        })), "Malformed UTF-8 item name with valid SHA");
        Reject(Envelope(Body(delegate(BinaryWriter writer) {
            writer.Write(1); writer.Write(false); writer.Write(1);
            for (int i = 0; i < 5; i++) writer.Write((byte)0xff);
        })), "Unbounded string length prefix with valid SHA");
        Reject(Envelope(Body(delegate(BinaryWriter writer) {
            writer.Write(1); writer.Write(false); writer.Write(1);
            writer.Write(new string('x', 4097)); writer.Write(0);
        })), "Oversized string with valid SHA");
        Reject(Envelope(Body(delegate(BinaryWriter writer) {
            writer.Write(1); writer.Write(false); writer.Write(2);
            for (int i = 0; i < 2; i++) { writer.Write(DataFormats.UnicodeText); writer.Write(0); }
        })), "Duplicate clipboard format");

        string[] invalidPaths = new string[] { "..\\escape.txt", "../escape.txt", "C:\\escape.txt",
            "\\\\server\\share", ".\\escape.txt", "folder\\..\\escape.txt", "safe.txt:stream",
            "bad.\\x", "bad \\x", "CON.txt", "NUL", "COM1.log", "LPT9", "CONIN$", "COM¹", "" };
        foreach (string path in invalidPaths)
            Reject(Envelope(FileBody(new PayloadFile { Path = path, IsDirectory = false, Data = new byte[0] })),
                "Unsafe file path " + path);
        Reject(Envelope(FileBody(new PayloadFile { Path = "missing\\child", Data = new byte[0] })),
            "Missing parent directory");
        Reject(Envelope(FileBody(new PayloadFile { Path = "root", Data = new byte[0] },
            new PayloadFile { Path = "root\\child", Data = new byte[0] })), "Parent is a regular file");
        Reject(Envelope(FileBody(new PayloadFile { Path = "Same.txt", Data = new byte[0] },
            new PayloadFile { Path = "same.txt", Data = new byte[0] })), "Case-insensitive duplicate path");
        Reject(Envelope(FileBody(new PayloadFile { Path = "directory", IsDirectory = true, Data = new byte[] { 1 } })),
            "Directory with nonempty data");
        byte[] raw = FormatBody(DataFormats.UnicodeText, new byte[0]);
        Array.Resize(ref raw, raw.Length + 1);
        Reject(Envelope(raw), "Trailing raw bytes");
    }

    private static byte[] FormatBody(string name, byte[] data)
    {
        return Body(delegate(BinaryWriter writer) {
            writer.Write(1); writer.Write(false); writer.Write(1);
            writer.Write(name); writer.Write(data.Length); writer.Write(data);
        });
    }

    private static byte[] FileBody(params PayloadFile[] files)
    {
        return Body(delegate(BinaryWriter writer) {
            writer.Write(1); writer.Write(true); writer.Write(files.Length);
            foreach (PayloadFile file in files)
            {
                writer.Write(file.Path); writer.Write(file.IsDirectory);
                writer.Write(file.Data.Length); writer.Write(file.Data);
            }
        });
    }

    private static byte[] Body(Action<BinaryWriter> write)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            using (BinaryWriter writer = new BinaryWriter(stream, Utf8, true)) write(writer);
            return stream.ToArray();
        }
    }

    private static byte[] Envelope(byte[] raw)
    {
        using (MemoryStream result = new MemoryStream())
        {
            byte[] magic = Encoding.ASCII.GetBytes("HCBPAY01");
            result.Write(magic, 0, magic.Length);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(raw);
                result.Write(hash, 0, hash.Length);
            }
            result.Write(BitConverter.GetBytes(raw.Length), 0, 4);
            using (DeflateStream zip = new DeflateStream(result, CompressionLevel.Fastest, true))
                zip.Write(raw, 0, raw.Length);
            return result.ToArray();
        }
    }

    private static void WriteInt32(byte[] bytes, int offset, int value)
    {
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4);
    }

    private static void Reject(byte[] envelope, string message)
    {
        ExpectReject(delegate { ClipboardPayload.Decode(envelope); }, message);
    }

    private static void ExpectReject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { assertions++; return; }
        catch (EndOfStreamException) { assertions++; return; }
        catch (DecoderFallbackException) { assertions++; return; }
        throw new Exception("Accepted invalid input: " + message);
    }

    private static void Equal(byte[] expected, byte[] actual, string message)
    {
        Check(actual != null && expected.Length == actual.Length, message + " length");
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != actual[i]) throw new Exception(message + " byte " + i);
        assertions++;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
    }
}
