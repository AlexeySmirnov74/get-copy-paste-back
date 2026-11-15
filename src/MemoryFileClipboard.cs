using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using OleDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace HorizonClient
{
    // Virtual Shell files are rendered from memory only when the paste target
    // asks for them. No CF_HDROP paths, staging directory or temporary file.
    internal static class MemoryFileClipboard
    {
        internal static MemoryFileDataObject Prepare(DecodedPayload payload)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            if (!payload.IsFileDrop) return null;
            lock (payload.FilePreparationLock)
            {
                if (payload.PreparedMemoryFiles == null) payload.PreparedMemoryFiles = new MemoryFileDataObject(payload);
                return payload.PreparedMemoryFiles;
            }
        }

        internal static string Publish(DecodedPayload payload)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            if (!payload.IsFileDrop) return ClipboardPayload.Publish(payload, null, Guid.Empty);
            MemoryFileDataObject data = Prepare(payload);
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new ThreadStateException("The clipboard publisher must run on an STA thread.");
            Application.OleRequired();
            // OLE owns an AddRef after success and releases it on replacement.
            // Do not flush: the live data object serves streams without a cache.
            Marshal.ThrowExceptionForHR(OleSetClipboard(data));
            return null;
        }

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] OleDataObject data);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class MemoryFileDataObject : OleDataObject
    {
        private const int FormatError = unchecked((int)0x80040064), MediumError = unchecked((int)0x80040069),
            AspectError = unchecked((int)0x8004006B), IndexError = unchecked((int)0x80040068), NotImplemented = unchecked((int)0x80004001);
        private static readonly short Descriptor = FormatId("FileGroupDescriptorW"), Contents = FormatId("FileContents"),
            PreferredEffect = FormatId("Preferred DropEffect"), History = FormatId("CanIncludeInClipboardHistory"), Cloud = FormatId("CanUploadToCloudClipboard"),
            PerformedEffect = FormatId("Performed DropEffect"), LogicalEffect = FormatId("Logical Performed DropEffect"), PasteSucceeded = FormatId("Paste Succeeded");
        private readonly Entry[] entries;
        private readonly byte[] descriptors;
        private sealed class Entry { internal string Name; internal bool Directory; internal byte[] Data; }

        internal MemoryFileDataObject(DecodedPayload payload)
        {
            if (!payload.IsFileDrop || payload.Files.Count < 1 || payload.Files.Count > ClipboardPayload.MaxEntries)
                throw new InvalidDataException("Invalid virtual file count.");
            var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var files = new List<Entry>(payload.Files.Count);
            long serializedBytes = 9;
            var utf8 = new UTF8Encoding(false, true);
            foreach (PayloadFile file in payload.Files)
            {
                if (file == null || file.Data == null) throw new InvalidDataException("Invalid virtual file data.");
                ClipboardPayload.ValidatePath(file.Path);
                if (paths.ContainsKey(file.Path)) throw new InvalidDataException("Duplicate virtual file name.");
                if (file.IsDirectory && file.Data.Length != 0) throw new InvalidDataException("A virtual directory cannot contain file bytes.");
                int nameBytes = utf8.GetByteCount(file.Path), prefixBytes = 1;
                for (int remaining = nameBytes; remaining >= 128; remaining >>= 7) prefixBytes++;
                serializedBytes += prefixBytes + nameBytes + 5L + file.Data.Length;
                if (serializedBytes > ClipboardPayload.MaxBytes) throw new InvalidDataException("The virtual file payload exceeds its size limit.");
                paths.Add(file.Path, file.IsDirectory);
                files.Add(new Entry { Name = file.Path, Directory = file.IsDirectory, Data = file.Data });
            }
            foreach (Entry file in files)
            {
                int depth = 0;
                for (string parent = Path.GetDirectoryName(file.Name); !String.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                {
                    bool directory;
                    if (++depth > 64 || !paths.TryGetValue(parent, out directory) || !directory)
                        throw new InvalidDataException("A virtual file parent directory is missing or invalid.");
                }
            }
            entries = files.ToArray();
            foreach (Entry entry in entries) entry.Data = (byte[])entry.Data.Clone();
            // FILEGROUPDESCRIPTORW: DWORD count followed by 592-byte records.
            // Each FILEDESCRIPTORW name starts at offset 72 (260 UTF-16 chars).
            descriptors = new byte[checked(4 + entries.Length * 592)];
            WriteInt(descriptors, 0, entries.Length);
            for (int index = 0; index < entries.Length; index++)
            {
                Entry file = entries[index]; int offset = 4 + index * 592;
                WriteInt(descriptors, offset, unchecked((int)0x80000044)); // UNICODE | ATTRIBUTES | FILESIZE
                WriteInt(descriptors, offset + 36, file.Directory ? 0x10 : 0x80);
                WriteInt(descriptors, offset + 68, file.Data.Length);
                byte[] name = Encoding.Unicode.GetBytes(file.Name);
                Buffer.BlockCopy(name, 0, descriptors, offset + 72, name.Length);
            }
        }

        private static short FormatId(string name) { return unchecked((short)DataFormats.GetFormat(name).Id); }
        private static void WriteInt(byte[] bytes, int offset, int value) { Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
        private static FORMATETC Format(short id, TYMED medium, int index) { return new FORMATETC { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = medium }; }

        public int QueryGetData(ref FORMATETC format)
        {
            if (format.dwAspect != DVASPECT.DVASPECT_CONTENT) return AspectError;
            if (format.ptd != IntPtr.Zero) return unchecked((int)0x80040065); // DV_E_DVTARGETDEVICE
            if (format.cfFormat == Contents)
            {
                if (format.lindex < 0 || format.lindex >= entries.Length) return IndexError;
                return (format.tymed & (TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL)) != 0 ? 0 : MediumError;
            }
            if (format.cfFormat != Descriptor && format.cfFormat != PreferredEffect && format.cfFormat != History && format.cfFormat != Cloud) return FormatError;
            if (format.lindex != -1) return IndexError;
            return (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? 0 : MediumError;
        }

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = new STGMEDIUM();
            int result = QueryGetData(ref format);
            if (result != 0) Marshal.ThrowExceptionForHR(result);
            if (format.cfFormat == Contents && (format.tymed & TYMED.TYMED_ISTREAM) != 0)
            {
                byte[] bytes = entries[format.lindex].Data;
                IntPtr stream = SHCreateMemStream(bytes.Length == 0 ? null : bytes, (uint)bytes.Length);
                if (stream == IntPtr.Zero) throw new OutOfMemoryException();
                medium.tymed = TYMED.TYMED_ISTREAM; medium.unionmember = stream;
                return; // Caller owns native stream's initial reference.
            }
            byte[] image = format.cfFormat == Contents ? entries[format.lindex].Data : format.cfFormat == Descriptor ? descriptors :
                BitConverter.GetBytes(format.cfFormat == PreferredEffect ? 1 : 0);
            IntPtr handle = GlobalAlloc(0x0042U, new UIntPtr((uint)Math.Max(1, image.Length))); // MOVEABLE | ZEROINIT
            if (handle == IntPtr.Zero) throw new OutOfMemoryException();
            try
            {
                IntPtr pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
                try { if (image.Length != 0) Marshal.Copy(image, 0, pointer, image.Length); }
                finally { GlobalUnlock(handle); }
                medium.tymed = TYMED.TYMED_HGLOBAL; medium.unionmember = handle; handle = IntPtr.Zero;
            }
            finally { if (handle != IntPtr.Zero) GlobalFree(handle); }
        }

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
        {
            if (direction != DATADIR.DATADIR_GET) throw new COMException("Only retrieval formats are enumerated.", NotImplemented);
            return new MemoryFileFormatEnumerator(new[] { Format(Descriptor, TYMED.TYMED_HGLOBAL, -1),
                Format(Contents, TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL, 0), Format(PreferredEffect, TYMED.TYMED_HGLOBAL, -1),
                Format(History, TYMED.TYMED_HGLOBAL, -1), Format(Cloud, TYMED.TYMED_HGLOBAL, -1) }, 0);
        }

        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release)
        {
            // Shell copy feedback is optional and never requests deleting our
            // source. On failure ownership stays with the caller.
            if ((format.cfFormat != PerformedEffect && format.cfFormat != LogicalEffect && format.cfFormat != PasteSucceeded) ||
                format.dwAspect != DVASPECT.DVASPECT_CONTENT || format.lindex != -1 || format.ptd != IntPtr.Zero ||
                (format.tymed & TYMED.TYMED_HGLOBAL) == 0 || medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero ||
                GlobalSize(medium.unionmember).ToUInt64() < 4) throw new COMException("Unsupported Shell copy feedback.", NotImplemented);
            IntPtr pointer = GlobalLock(medium.unionmember);
            if (pointer == IntPtr.Zero) throw new COMException("Invalid Shell copy feedback.", NotImplemented);
            int effect;
            try { effect = Marshal.ReadInt32(pointer); }
            finally { GlobalUnlock(medium.unionmember); }
            if (effect != 0 && effect != 1) throw new COMException("Only copy feedback is supported.", NotImplemented);
            if (release) { ReleaseStgMedium(ref medium); medium = new STGMEDIUM(); }
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) { throw new COMException("Caller-owned media are not supported.", NotImplemented); }
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; output.ptd = IntPtr.Zero; return 0x00040130; }
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) { throw new COMException("Advisory connections are not supported.", unchecked((int)0x80040003)); }
        public int EnumDAdvise(out IEnumSTATDATA enumerator) { enumerator = null; return unchecked((int)0x80040003); }

        [DllImport("shlwapi.dll", ExactSpelling = true)] private static extern IntPtr SHCreateMemStream([In] byte[] initialBytes, uint count);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr handle);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr handle);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr handle);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr handle);
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class MemoryFileFormatEnumerator : IEnumFORMATETC
    {
        private readonly FORMATETC[] formats;
        private int position;
        internal MemoryFileFormatEnumerator(FORMATETC[] formats, int position) { this.formats = formats; this.position = position; }
        public int Next(int count, FORMATETC[] result, int[] fetched)
        {
            if (count < 0 || result == null || result.Length < count || (fetched == null && count != 1) || (fetched != null && fetched.Length < 1)) return unchecked((int)0x80070057);
            int copied = 0;
            while (copied < count && position < formats.Length) result[copied++] = formats[position++];
            if (fetched != null) fetched[0] = copied;
            return copied == count ? 0 : 1;
        }
        public int Skip(int count)
        {
            if (count < 0) return unchecked((int)0x80070057);
            int skipped = Math.Min(count, formats.Length - position); position += skipped;
            return skipped == count ? 0 : 1;
        }
        public int Reset() { position = 0; return 0; }
        public void Clone(out IEnumFORMATETC clone) { clone = new MemoryFileFormatEnumerator(formats, position); }
    }
}
