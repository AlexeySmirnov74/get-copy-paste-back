using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace HorizonClient
{
    public sealed class ClipboardSnapshot
    {
        public readonly Dictionary<string, byte[]> Formats = new Dictionary<string, byte[]>();
        public string[] Paths;
    }

    public sealed class PayloadFile
    {
        public string Path;
        public bool IsDirectory;
        public byte[] Data;
    }

    public sealed class DecodedPayload
    {
        public readonly Dictionary<string, byte[]> Formats = new Dictionary<string, byte[]>();
        public readonly List<PayloadFile> Files = new List<PayloadFile>();
        public bool IsFileDrop;
        public string Description;

        // Local receiving state only. This cache is never part of the wire payload.
        internal readonly object FilePreparationLock = new object();
        internal string PreparedFileRoot;
        internal string PreparedFileDirectory;
        internal MemoryFileDataObject PreparedMemoryFiles;
    }

    // Only explicit Windows clipboard formats are serialized, never OLE objects.
    public static class ClipboardPayload
    {
        public const int MaxBytes = 32 * 1024 * 1024;
        public const int MaxEntries = 4096;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HCBPAY01");
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        // Must be called on the interactive STA thread.
        public static ClipboardSnapshot Capture()
        {
            IDataObject data = Clipboard.GetDataObject();
            if (data == null) return null;
            ClipboardSnapshot snapshot = new ClipboardSnapshot();
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                snapshot.Paths = data.GetData(DataFormats.FileDrop) as string[];
                return snapshot.Paths == null || snapshot.Paths.Length == 0 ? null : snapshot;
            }
            CopyText(data, snapshot, DataFormats.UnicodeText);
            CopyText(data, snapshot, DataFormats.Html);
            CopyText(data, snapshot, DataFormats.Rtf);
            if (data.GetDataPresent(DataFormats.Bitmap))
            {
                using (Image image = data.GetData(DataFormats.Bitmap) as Image)
                {
                    if (image != null)
                    {
                        if ((long)image.Width * image.Height > 32 * 1024 * 1024)
                            throw new InvalidDataException(UiText.Get("Изображение слишком большое.", "The image is too large."));
                        using (MemoryStream stream = new MemoryStream())
                        {
                            image.Save(stream, ImageFormat.Png);
                            if (stream.Length > MaxBytes) throw TooLarge();
                            snapshot.Formats.Add("PNG", stream.ToArray());
                        }
                    }
                }
            }
            return snapshot.Formats.Count == 0 ? null : snapshot;
        }

        private static void CopyText(IDataObject data, ClipboardSnapshot snapshot, string format)
        {
            if (!data.GetDataPresent(format)) return;
            string value = data.GetData(format) as string;
            if (value == null) return;
            if (Utf8.GetByteCount(value) > MaxBytes) throw TooLarge();
            snapshot.Formats.Add(format, Utf8.GetBytes(value));
        }

        public static byte[] Encode(ClipboardSnapshot snapshot, out string description)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            List<PayloadFile> files = new List<PayloadFile>();
            if (snapshot.Paths != null)
            {
                HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long byteCount = 0;
                foreach (string path in snapshot.Paths)
                {
                    string full = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
                    string root = System.IO.Path.GetFileName(full);
                    if (String.IsNullOrEmpty(root)) throw new InvalidDataException(UiText.Get("Копирование целого диска не поддерживается.", "Copying an entire drive is not supported."));
                    string unique = root;
                    for (int suffix = 2; !roots.Add(unique); suffix++)
                        unique = System.IO.Path.GetFileNameWithoutExtension(root) + " (" + suffix + ")" + System.IO.Path.GetExtension(root);
                    CollectFiles(full, unique, files, ref byteCount, 0);
                }
            }
            using (MemoryStream body = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(body, Utf8, true))
                {
                    writer.Write(1);
                    writer.Write(snapshot.Paths != null);
                    if (snapshot.Paths != null)
                    {
                        writer.Write(files.Count);
                        foreach (PayloadFile file in files)
                        {
                            writer.Write(file.Path);
                            writer.Write(file.IsDirectory);
                            writer.Write(file.Data.Length);
                            writer.Write(file.Data);
                            if (body.Length > MaxBytes) throw TooLarge();
                        }
                        description = UiText.Format("Файлы и папки: {0}", "Files and folders: {0}", snapshot.Paths.Length);
                    }
                    else
                    {
                        writer.Write(snapshot.Formats.Count);
                        foreach (KeyValuePair<string, byte[]> format in snapshot.Formats)
                        {
                            ValidateFormat(format.Key);
                            writer.Write(format.Key);
                            writer.Write(format.Value.Length);
                            writer.Write(format.Value);
                            if (body.Length > MaxBytes) throw TooLarge();
                        }
                        description = snapshot.Formats.ContainsKey("PNG") ? UiText.Get("Изображение", "Image") : UiText.Get("Текст", "Text");
                    }
                }
                // Reuse the serialized body buffer; copying it would retain a
                // second full uncompressed payload during compression.
                byte[] raw = body.GetBuffer();
                int rawLength = checked((int)body.Length);
                long compressionStarted = Stopwatch.GetTimestamp();
                using (MemoryStream result = new MemoryStream())
                {
                    result.Write(Magic, 0, Magic.Length);
                    using (SHA256 hash = SHA256.Create())
                    {
                        byte[] digest = hash.ComputeHash(raw, 0, rawLength);
                        result.Write(digest, 0, digest.Length);
                    }
                    result.Write(BitConverter.GetBytes(rawLength), 0, 4);
                    using (DeflateStream zip = new DeflateStream(result, CompressionLevel.Optimal, true))
                        zip.Write(raw, 0, rawLength);
                    MemoryStream stored = null;
                    try
                    {
                        MemoryStream selected = result;
                        string mode = "deflate-optimal";
                        if (result.Length - 44 >= rawLength)
                        {
                            // Already compressed/high-entropy contents can grow.
                            // Stored DEFLATE blocks retain the existing envelope
                            // and work with old clients, without a new wire codec.
                            int capacity = checked(44 + rawLength + (rawLength / 16384 + 1) * 5 + 512);
                            stored = new MemoryStream(capacity);
                            stored.Write(result.GetBuffer(), 0, 44);
                            using (DeflateStream zip = new DeflateStream(stored, CompressionLevel.NoCompression, true))
                                zip.Write(raw, 0, rawLength);
                            if (stored.Length < result.Length) { selected = stored; mode = "deflate-stored"; }
                        }
                        if (selected.Length > MaxBytes) throw TooLarge();
                        byte[] encoded = selected.ToArray();
                        DiagnosticLog.Write("payload.compression.encoded", "mode", mode, "raw_bytes", rawLength,
                            "payload_bytes", encoded.Length - 44, "wire_bytes", encoded.Length,
                            "saved_bytes", rawLength - (encoded.Length - 44),
                            "elapsed_ms", (Stopwatch.GetTimestamp() - compressionStarted) * (1000.0 / Stopwatch.Frequency));
                        return encoded;
                    }
                    finally { if (stored != null) stored.Dispose(); }
                }
            }
        }

        private static void CollectFiles(string full, string relative, List<PayloadFile> files, ref long total, int depth)
        {
            if (depth > 64 || files.Count >= MaxEntries) throw new InvalidDataException(UiText.Get("Слишком много файлов или вложенных папок.", "Too many files or nested folders."));
            ValidatePath(relative);
            FileAttributes attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(UiText.Get("Ссылки и точки подключения не поддерживаются: ", "Links and mount points are not supported: ") + System.IO.Path.GetFileName(full));
            bool directory = (attributes & FileAttributes.Directory) != 0;
            byte[] bytes = new byte[0];
            if (!directory)
            {
                using (FileStream input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length > MaxBytes || total + input.Length > MaxBytes) throw TooLarge();
                    bytes = ReadExactly(input, (int)input.Length);
                    if (input.ReadByte() != -1) throw new IOException(UiText.Get("Файл изменился во время чтения.", "The file changed while it was being read."));
                    total += bytes.Length;
                }
            }
            files.Add(new PayloadFile { Path = relative, IsDirectory = directory, Data = bytes });
            if (directory)
            {
                string[] children = Directory.GetFileSystemEntries(full);
                Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                foreach (string child in children)
                    CollectFiles(child, relative + "\\" + System.IO.Path.GetFileName(child), files, ref total, depth + 1);
            }
        }

        public static DecodedPayload Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 45 || bytes.Length > MaxBytes) throw new InvalidDataException(UiText.Get("Неверный размер передачи.", "Invalid transfer size."));
            for (int i = 0; i < Magic.Length; i++) if (bytes[i] != Magic[i]) throw new InvalidDataException(UiText.Get("Неверный формат передачи.", "Invalid transfer format."));
            int length = BitConverter.ToInt32(bytes, 40);
            if (length < 9 || length > MaxBytes) throw new InvalidDataException(UiText.Get("Неверный размер содержимого.", "Invalid content size."));
            byte[] raw;
            using (MemoryStream source = new MemoryStream(bytes, 44, bytes.Length - 44, false))
            using (DeflateStream zip = new DeflateStream(source, CompressionMode.Decompress))
            {
                raw = ReadExactly(zip, length);
                if (zip.ReadByte() != -1) throw new InvalidDataException(UiText.Get("Содержимое превышает заявленный размер.", "The content exceeds its declared size."));
            }
            using (SHA256 hash = SHA256.Create())
            {
                byte[] digest = hash.ComputeHash(raw);
                int difference = 0;
                for (int i = 0; i < digest.Length; i++) difference |= digest[i] ^ bytes[i + 8];
                if (difference != 0) throw new InvalidDataException(UiText.Get("Контрольная сумма SHA-256 не совпала.", "The SHA-256 checksum does not match."));
            }
            DecodedPayload payload = new DecodedPayload();
            using (MemoryStream stream = new MemoryStream(raw, false))
            using (BinaryReader reader = new BinaryReader(stream, Utf8))
            {
                if (reader.ReadInt32() != 1) throw new InvalidDataException(UiText.Get("Неизвестная версия содержимого.", "Unknown content version."));
                payload.IsFileDrop = reader.ReadBoolean();
                int count = reader.ReadInt32();
                if (count < 1 || count > (payload.IsFileDrop ? MaxEntries : 4)) throw new InvalidDataException(UiText.Get("Неверное число элементов.", "Invalid item count."));
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    string name = ReadName(reader);
                    if (!names.Add(name)) throw new InvalidDataException(UiText.Get("Повторяющееся имя элемента.", "Duplicate item name."));
                    bool directory = false;
                    if (payload.IsFileDrop) { ValidatePath(name); directory = reader.ReadBoolean(); }
                    else ValidateFormat(name);
                    int size = reader.ReadInt32();
                    if (size < 0 || size > stream.Length - stream.Position || (directory && size != 0)) throw new InvalidDataException(UiText.Get("Неверный размер элемента.", "Invalid item size."));
                    byte[] data = ReadExactly(stream, size);
                    if (payload.IsFileDrop) payload.Files.Add(new PayloadFile { Path = name, IsDirectory = directory, Data = data });
                    else payload.Formats.Add(name, data);
                }
                if (stream.Position != stream.Length) throw new InvalidDataException(UiText.Get("Лишние данные в передаче.", "Unexpected extra data in the transfer."));
            }
            if (payload.IsFileDrop)
            {
                Dictionary<string, bool> entries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (PayloadFile file in payload.Files) entries.Add(file.Path, file.IsDirectory);
                foreach (PayloadFile file in payload.Files)
                {
                    string parent = System.IO.Path.GetDirectoryName(file.Path);
                    while (!String.IsNullOrEmpty(parent))
                    {
                        bool isDirectory;
                        if (!entries.TryGetValue(parent, out isDirectory) || !isDirectory) throw new InvalidDataException(UiText.Get("Отсутствует родительская папка.", "The parent folder is missing."));
                        parent = System.IO.Path.GetDirectoryName(parent);
                    }
                }
                payload.Description = UiText.Format("Файлы и папки: {0}", "Files and folders: {0}", payload.Files.Count);
            }
            else payload.Description = payload.Formats.ContainsKey("PNG") ? UiText.Get("Изображение", "Image") : UiText.Get("Текст", "Text");
            return payload;
        }

        // Call on the receiving STA thread. FileDrop paths must remain on disk after exit.
        public static string Publish(DecodedPayload payload, string receivedRoot, Guid id)
        {
            DataObject data = new DataObject();
            Bitmap image = null;
            MemoryStream effect = null;
            string directory = null;
            try
            {
                if (payload.IsFileDrop)
                {
                    string[] paths = PrepareFileDrop(payload, receivedRoot, id, out directory);
                    StringCollection top = new StringCollection();
                    top.AddRange(paths);
                    data.SetFileDropList(top);
                    effect = new MemoryStream(BitConverter.GetBytes(1));
                    data.SetData("Preferred DropEffect", effect);
                }
                else
                {
                    foreach (KeyValuePair<string, byte[]> format in payload.Formats)
                    {
                        if (format.Key == "PNG")
                        {
                            using (MemoryStream stream = new MemoryStream(format.Value, false))
                            using (Image decoded = Image.FromStream(stream, true, true))
                            {
                                if ((long)decoded.Width * decoded.Height > 32 * 1024 * 1024) throw new InvalidDataException(UiText.Get("Изображение слишком большое.", "The image is too large."));
                                image = new Bitmap(decoded);
                            }
                            data.SetImage(image);
                        }
                        else data.SetData(format.Key, false, Utf8.GetString(format.Value));
                    }
                }
                Clipboard.SetDataObject(data, true, 10, 50);
                return directory;
            }
            finally
            {
                if (image != null) image.Dispose();
                if (effect != null) effect.Dispose();
            }
        }

        // Does not access the clipboard. A busy clipboard can retry publication of this same object
        // without extracting again. Failed extraction deliberately leaves its uncached directory alone.
        internal static string[] PrepareFileDrop(DecodedPayload payload, string receivedRoot, Guid id, out string directory)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            if (!payload.IsFileDrop || payload.Files.Count == 0) throw new InvalidDataException(UiText.Get("Нет файлов для подготовки.", "There are no files to prepare."));
            if (String.IsNullOrWhiteSpace(receivedRoot)) throw new ArgumentException(UiText.Get("Не задан каталог полученных файлов.", "The received files directory is not configured."), "receivedRoot");
            string root = System.IO.Path.GetFullPath(receivedRoot).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            lock (payload.FilePreparationLock)
            {
                if (payload.PreparedFileDirectory != null)
                {
                    if (!String.Equals(root, payload.PreparedFileRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(UiText.Get("Файлы уже подготовлены в другом каталоге получения.", "The files were already prepared in another receive directory."));
                    directory = payload.PreparedFileDirectory;
                }
                else
                {
                    // Validate every relative name before creating anything, even for programmatically
                    // built DecodedPayload instances that did not pass through Decode.
                    foreach (PayloadFile file in payload.Files)
                    {
                        if (file == null || file.Data == null) throw new InvalidDataException(UiText.Get("Неверные данные файла.", "Invalid file data."));
                        ValidatePath(file.Path);
                    }
                    Directory.CreateDirectory(root);
                    EnsurePlainDirectories(root, root);
                    string stem = id.ToString("N");
                    string candidate = System.IO.Path.Combine(root, stem);
                    while (!NativeCreateDirectory(candidate, IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 183 && error != 80)
                            throw new IOException(UiText.Get("Не удалось создать каталог полученной передачи.", "Could not create the received transfer directory."), new System.ComponentModel.Win32Exception(error));
                        candidate = System.IO.Path.Combine(root, stem + "-" + Guid.NewGuid().ToString("N"));
                    }
                    directory = candidate;
                    EnsurePlainDirectories(root, directory);
                    foreach (PayloadFile file in payload.Files)
                    {
                        string target = ContainedFilePath(directory, file.Path);
                        if (file.IsDirectory)
                        {
                            Directory.CreateDirectory(target);
                            EnsurePlainDirectories(root, target);
                        }
                        else
                        {
                            string parent = System.IO.Path.GetDirectoryName(target);
                            Directory.CreateDirectory(parent);
                            EnsurePlainDirectories(root, parent);
                            using (FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                output.Write(file.Data, 0, file.Data.Length);
                        }
                    }
                    // Commit only when all entries have been extracted successfully.
                    payload.PreparedFileRoot = root;
                    payload.PreparedFileDirectory = directory;
                }

                EnsurePlainDirectories(root, directory);
                List<string> top = new List<string>();
                foreach (PayloadFile file in payload.Files)
                {
                    ValidatePath(file.Path);
                    string target = ContainedFilePath(directory, file.Path);
                    EnsurePlainDirectories(root, file.IsDirectory ? target : System.IO.Path.GetDirectoryName(target));
                    FileAttributes attributes = File.GetAttributes(target);
                    if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                        ((attributes & FileAttributes.Directory) != 0) != file.IsDirectory)
                        throw new IOException(UiText.Get("Подготовленный файл или каталог был заменён.", "A prepared file or directory was replaced."));
                    if (file.Path.IndexOf('\\') < 0) top.Add(target);
                }
                if (top.Count == 0) throw new InvalidDataException(UiText.Get("Нет корневых элементов передачи.", "The transfer contains no root items."));
                return top.ToArray();
            }
        }

        private static string ContainedFilePath(string directory, string relative)
        {
            string prefix = System.IO.Path.GetFullPath(directory).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, relative));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(UiText.Get("Файл находится за пределами каталога передачи.", "The file is outside the transfer directory."));
            return target;
        }

        private static void EnsurePlainDirectories(string root, string directory)
        {
            string rootPath = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
            string current = System.IO.Path.GetFullPath(directory).TrimEnd('\\', '/');
            if (!String.Equals(rootPath, current, StringComparison.OrdinalIgnoreCase) &&
                !current.StartsWith(rootPath + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(UiText.Get("Каталог находится за пределами каталога получения.", "The directory is outside the receive directory."));
            while (true)
            {
                // Restore the separator for a drive root such as C:\\.
                string probe = current.Length == 2 && current[1] == ':' ? current + "\\" : current;
                FileAttributes attributes = File.GetAttributes(probe);
                if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(UiText.Get("Каталог получения не должен содержать ссылки или точки подключения.", "The receive directory must not contain links or mount points."));
                if (String.Equals(current, rootPath, StringComparison.OrdinalIgnoreCase)) break;
                current = System.IO.Path.GetDirectoryName(current);
                if (current == null) throw new InvalidDataException(UiText.Get("Неверный каталог получения.", "Invalid receive directory."));
                current = current.TrimEnd('\\', '/');
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeCreateDirectory(string path, IntPtr securityAttributes);

        private static string ReadName(BinaryReader reader)
        {
            // BinaryWriter strings use a seven-bit byte count. Bound it before allocation.
            int length = 0;
            int shift = 0;
            byte next;
            do
            {
                if (shift > 21) throw new InvalidDataException(UiText.Get("Имя слишком длинное.", "The name is too long."));
                next = reader.ReadByte();
                length |= (next & 127) << shift;
                shift += 7;
            } while ((next & 128) != 0);
            if (length < 1 || length > 4096) throw new InvalidDataException(UiText.Get("Неверная длина имени.", "Invalid name length."));
            return Utf8.GetString(ReadExactly(reader.BaseStream, length));
        }

        private static void ValidateFormat(string name)
        {
            if (name != DataFormats.UnicodeText && name != DataFormats.Html && name != DataFormats.Rtf && name != "PNG")
                throw new InvalidDataException(UiText.Get("Неподдерживаемый формат буфера.", "Unsupported clipboard format."));
        }

        internal static void ValidatePath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length > 220 || System.IO.Path.IsPathRooted(path) || path.IndexOf('/') >= 0)
                throw new InvalidDataException(UiText.Get("Недопустимое имя файла.", "Invalid file name."));
            foreach (string part in path.Split('\\'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException(UiText.Get("Недопустимое имя файла.", "Invalid file name."));
                string stem = part.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$" ||
                    (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".IndexOf(stem[3]) >= 0))
                    throw new InvalidDataException(UiText.Get("Зарезервированное имя файла.", "Reserved file name."));
            }
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int got = stream.Read(buffer, offset, count - offset);
                if (got == 0) throw new InvalidDataException(UiText.Get("Неполные данные.", "Incomplete data."));
                offset += got;
            }
            return buffer;
        }

        private static InvalidDataException TooLarge()
        {
            return new InvalidDataException(UiText.Get("Передача превышает 32 МиБ. Выберите меньше файлов.", "The transfer exceeds 32 MiB. Select fewer files."));
        }
    }
}
