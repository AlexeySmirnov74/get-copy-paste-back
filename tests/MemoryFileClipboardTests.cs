using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;
using OleDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

// Synthetic COM/native-memory fixtures only: no clipboard publication, disk
// writes, audio/camera devices, hooks, services or user profile access.
internal static class MemoryFileClipboardTests
{
    private static int checks;
    [STAThread]
    private static int Main()
    {
        try
        {
            DescriptorsAndSnapshot(); IndependentNativeStreams(); NegotiationAndEnumeration();
            ComVisibilityAndOwnership(); FeedbackOwnership(); ValidationAndLimits(); ConcurrentPreparation();
            Console.WriteLine("Memory-only client file checks passed: " + checks + ". Native OLE buffers/streams only; no clipboard access or file writes.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static DecodedPayload Files()
    {
        var payload = new DecodedPayload { IsFileDrop = true };
        payload.Files.Add(new PayloadFile { Path = "Папка", IsDirectory = true, Data = new byte[0] });
        payload.Files.Add(new PayloadFile { Path = "Папка\\данные😀.bin", Data = new byte[] { 2, 7, 19, 31 } });
        payload.Files.Add(new PayloadFile { Path = "empty.txt", Data = new byte[0] });
        payload.Files.Add(new PayloadFile { Path = "emptyfolder", IsDirectory = true, Data = new byte[0] });
        return payload;
    }
    private static FORMATETC Format(string name, TYMED medium, int index)
    { return new FORMATETC { cfFormat = unchecked((short)DataFormats.GetFormat(name).Id), dwAspect = DVASPECT.DVASPECT_CONTENT, tymed = medium, lindex = index }; }
    private static STGMEDIUM Get(OleDataObject data, string name, TYMED medium, int index)
    { var format = Format(name, medium, index); STGMEDIUM result; data.GetData(ref format, out result); return result; }

    private static void DescriptorsAndSnapshot()
    {
        DecodedPayload payload = Files();
        MemoryFileDataObject data = MemoryFileClipboard.Prepare(payload);
        Check(Object.ReferenceEquals(data, MemoryFileClipboard.Prepare(payload)), "Repeated busy-clipboard preparation reuses one bounded snapshot");
        Check(payload.PreparedFileDirectory == null && payload.PreparedFileRoot == null, "Virtual preparation never creates a staged file-root cache");
        payload.Files[1].Data[0] = 255; payload.Files[1].Path = "changed.bin"; payload.Files.Clear();
        STGMEDIUM medium = Get(data, "FileGroupDescriptorW", TYMED.TYMED_HGLOBAL, -1);
        try
        {
            Check(medium.tymed == TYMED.TYMED_HGLOBAL && medium.pUnkForRelease == null, "Caller owns the returned descriptor global memory");
            byte[] bytes = GlobalBytes(medium.unionmember, 4 + 4 * 592);
            Check(BitConverter.ToInt32(bytes, 0) == 4, "Descriptor count includes files and directories");
            string[] names = { "Папка", "Папка\\данные😀.bin", "empty.txt", "emptyfolder" };
            int[] lengths = { 0, 4, 0, 0 }, attributes = { 0x10, 0x80, 0x80, 0x10 };
            for (int index = 0; index < 4; index++)
            {
                int offset = 4 + index * 592;
                Check(unchecked((uint)BitConverter.ToInt32(bytes, offset)) == 0x80000044U, "Descriptors mark Unicode, attributes and exact sizes");
                Check(BitConverter.ToInt32(bytes, offset + 36) == attributes[index], "File and directory attributes retain their snapshot values");
                Check(BitConverter.ToInt32(bytes, offset + 64) == 0 && BitConverter.ToInt32(bytes, offset + 68) == lengths[index], "Descriptor file sizes use the documented high/low DWORD positions");
                Check(Encoding.Unicode.GetString(bytes, offset + 72, 520).TrimEnd('\0') == names[index], "Names use UTF-16 including Unicode surrogate pairs and relative folder paths");
            }
        }
        finally { ReleaseStgMedium(ref medium); }
        medium = Get(data, "FileContents", TYMED.TYMED_HGLOBAL, 1);
        try { Same(GlobalBytes(medium.unionmember, 4), new byte[] { 2, 7, 19, 31 }, "Prepared content cannot be changed through the original decoded payload array"); }
        finally { ReleaseStgMedium(ref medium); }
        foreach (string name in new[] { "Preferred DropEffect", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
        {
            medium = Get(data, name, TYMED.TYMED_HGLOBAL, -1);
            try { Check(BitConverter.ToInt32(GlobalBytes(medium.unionmember, 4), 0) == (name == "Preferred DropEffect" ? 1 : 0), "Copy is preferred; optional Windows history/cloud hints are disabled"); }
            finally { ReleaseStgMedium(ref medium); }
        }
    }

    private static void IndependentNativeStreams()
    {
        MemoryFileDataObject data = MemoryFileClipboard.Prepare(Files());
        STGMEDIUM first = Get(data, "FileContents", TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL, 1), second = Get(data, "FileContents", TYMED.TYMED_ISTREAM, 1);
        IStream a = null, b = null, clone = null;
        try
        {
            Check(first.tymed == TYMED.TYMED_ISTREAM && first.pUnkForRelease == null && first.unionmember != second.unionmember, "Repeated requests prefer independent native streams owned by their callers");
            a = (IStream)Marshal.GetObjectForIUnknown(first.unionmember); b = (IStream)Marshal.GetObjectForIUnknown(second.unionmember);
            Same(Read(a, 2), new byte[] { 2, 7 }, "A native memory stream starts at position zero");
            Same(Read(b, 20), new byte[] { 2, 7, 19, 31 }, "Reading one returned stream cannot advance a second stream");
            Check(Read(b, 1).Length == 0, "End of stream returns zero bytes");
            a.Clone(out clone);
            Same(Read(clone, 10), new byte[] { 19, 31 }, "Windows 10 native stream Clone preserves current seek position");
            Same(Read(a, 10), new byte[] { 19, 31 }, "Clone reads cannot change the original stream seek position");
            a.Seek(1, 0, IntPtr.Zero); Same(Read(a, 2), new byte[] { 7, 19 }, "Native file contents support seeking");
            System.Runtime.InteropServices.ComTypes.STATSTG stat;
            a.Stat(out stat, 1); Check(stat.cbSize == 4, "Native stream length matches its descriptor");
            ReleaseStgMedium(ref first); first = new STGMEDIUM();
            GC.Collect(); GC.WaitForPendingFinalizers();
            a.Seek(0, 0, IntPtr.Zero); Same(Read(a, 4), new byte[] { 2, 7, 19, 31 }, "A retained native IStream reference remains valid after the STGMEDIUM owner releases its reference");
        }
        finally
        {
            if (clone != null) Marshal.ReleaseComObject(clone);
            if (a != null) Marshal.ReleaseComObject(a);
            if (b != null) Marshal.ReleaseComObject(b);
            if (first.tymed != TYMED.TYMED_NULL) ReleaseStgMedium(ref first);
            ReleaseStgMedium(ref second);
        }
        foreach (int index in new[] { 0, 2, 3 })
        {
            STGMEDIUM medium = Get(data, "FileContents", TYMED.TYMED_ISTREAM, index);
            IStream empty = null;
            try { empty = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember); Check(Read(empty, 1).Length == 0, "Empty files and directory contents provide valid empty native streams"); }
            finally { if (empty != null) Marshal.ReleaseComObject(empty); ReleaseStgMedium(ref medium); }
        }
    }

    private static void NegotiationAndEnumeration()
    {
        MemoryFileDataObject data = MemoryFileClipboard.Prepare(Files());
        var format = Format("FileContents", TYMED.TYMED_ISTREAM, 1);
        Check(data.QueryGetData(ref format) == 0, "Valid indexed file contents are discoverable");
        foreach (int index in new[] { -2, -1, 4, Int32.MaxValue })
        {
            format.lindex = index;
            Check(data.QueryGetData(ref format) == unchecked((int)0x80040068), "Content indices outside the descriptor array fail with DV_E_LINDEX");
            ThrowsHr(delegate { STGMEDIUM medium; data.GetData(ref format, out medium); }, unchecked((int)0x80040068), "GetData preserves invalid index errors without allocating media");
        }
        format = Format("FileGroupDescriptorW", TYMED.TYMED_HGLOBAL, 0);
        Check(data.QueryGetData(ref format) == unchecked((int)0x80040068), "Global descriptor data requires lindex -1");
        format = Format("FileContents", TYMED.TYMED_FILE, 0);
        Check(data.QueryGetData(ref format) == unchecked((int)0x80040069), "Disk-file media are never advertised or accepted");
        format = Format("FileGroupDescriptorW", TYMED.TYMED_ISTREAM, -1);
        Check(data.QueryGetData(ref format) == unchecked((int)0x80040069), "Descriptors require HGLOBAL media");
        format = Format("FileContents", TYMED.TYMED_ISTREAM, 1); format.dwAspect = DVASPECT.DVASPECT_ICON;
        Check(data.QueryGetData(ref format) == unchecked((int)0x8004006B), "Unsupported aspects are rejected");
        format.dwAspect = DVASPECT.DVASPECT_CONTENT; format.ptd = new IntPtr(1);
        Check(data.QueryGetData(ref format) == unchecked((int)0x80040065), "Target-device-specific rendering cannot dereference an untrusted device pointer");
        format = Format("FileDrop", TYMED.TYMED_HGLOBAL, -1);
        Check(data.QueryGetData(ref format) == unchecked((int)0x80040064), "The virtual clipboard does not claim existing disk-file paths");
        IEnumFORMATETC enumerator = data.EnumFormatEtc(DATADIR.DATADIR_GET), clone;
        var one = new FORMATETC[1];
        Check(enumerator.Next(1, one, null) == 0 && one[0].cfFormat == Format("FileGroupDescriptorW", TYMED.TYMED_HGLOBAL, -1).cfFormat, "Enum Next allows a null fetched count for one requested item");
        enumerator.Clone(out clone);
        Check(enumerator.Skip(1) == 0 && clone.Next(1, one, null) == 0 && one[0].cfFormat == Format("FileContents", TYMED.TYMED_ISTREAM, 0).cfFormat, "Format enumerator clones preserve independent positions");
        Check(enumerator.Reset() == 0, "Format enumeration can restart");
        var all = new FORMATETC[8]; var fetched = new int[1];
        Check(enumerator.Next(8, all, fetched) == 1 && fetched[0] == 5, "Enumeration returns the five available virtual-file/control formats with S_FALSE at the end");
        for (int index = 0; index < fetched[0]; index++)
        {
            Check(data.QueryGetData(ref all[index]) == 0, "Every enumerated format can be retrieved as advertised");
            Check((all[index].tymed & TYMED.TYMED_FILE) == 0 && all[index].cfFormat != 15, "No enumerated format refers to staged paths or TYMED_FILE");
        }
        Check(enumerator.Skip(Int32.MaxValue) == 1 && enumerator.Next(1, one, null) == 1, "Skipping beyond the format count stays at the end without integer overflow");
        Check(enumerator.Next(2, one, null) == unchecked((int)0x80070057), "Invalid enumeration output buffers are rejected");
        ThrowsHr(delegate { data.EnumFormatEtc(DATADIR.DATADIR_SET); }, unchecked((int)0x80004001), "SET enumeration is not offered");
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NativeGetData(IntPtr self, ref FORMATETC format, out STGMEDIUM medium);
    private static void ComVisibilityAndOwnership()
    {
        MemoryFileDataObject data = MemoryFileClipboard.Prepare(Files());
        IntPtr pointer = Marshal.GetComInterfaceForObject(data, typeof(OleDataObject));
        try
        {
            data = null; GC.Collect(); GC.WaitForPendingFinalizers();
            OleDataObject retained = (OleDataObject)Marshal.GetObjectForIUnknown(pointer);
            STGMEDIUM medium = Get(retained, "FileContents", TYMED.TYMED_HGLOBAL, 1);
            try { Same(GlobalBytes(medium.unionmember, 4), new byte[] { 2, 7, 19, 31 }, "COM AddRef retains the managed data object across collection without a static owner"); }
            finally { ReleaseStgMedium(ref medium); }
            IntPtr vtable = Marshal.ReadIntPtr(pointer), method = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
            var invoke = (NativeGetData)Marshal.GetDelegateForFunctionPointer(method, typeof(NativeGetData));
            var format = Format("FileContents", TYMED.TYMED_HGLOBAL, 1);
            Check(invoke(pointer, ref format, out medium) == 0, "A native IDataObject vtable call can retrieve the medium despite assembly-default ComVisible(false)");
            try { Same(GlobalBytes(medium.unionmember, 4), new byte[] { 2, 7, 19, 31 }, "Native GetData marshals the standard STGMEDIUM layout and ownership correctly"); }
            finally { ReleaseStgMedium(ref medium); }
            IEnumFORMATETC formats = retained.EnumFormatEtc(DATADIR.DATADIR_GET);
            IntPtr enumPointer = Marshal.GetComInterfaceForObject(formats, typeof(IEnumFORMATETC));
            try { Check(enumPointer != IntPtr.Zero, "The format enumerator is also explicitly COM-visible"); }
            finally { Marshal.Release(enumPointer); }
            for (int index = 0; index < 200; index++)
            {
                medium = Get(retained, "FileContents", index % 2 == 0 ? TYMED.TYMED_HGLOBAL : TYMED.TYMED_ISTREAM, 1);
                Check(medium.unionmember != IntPtr.Zero, "Repeated paste requests each receive an owned native medium");
                ReleaseStgMedium(ref medium);
            }
        }
        finally { Marshal.Release(pointer); }
    }

    private static void FeedbackOwnership()
    {
        MemoryFileDataObject data = MemoryFileClipboard.Prepare(Files());
        var format = Format("Performed DropEffect", TYMED.TYMED_HGLOBAL, -1);
        STGMEDIUM medium = Get(data, "Preferred DropEffect", TYMED.TYMED_HGLOBAL, -1);
        try
        {
            data.SetData(ref format, ref medium, false);
            Check(BitConverter.ToInt32(GlobalBytes(medium.unionmember, 4), 0) == 1, "Successful feedback with release=false leaves ownership with the caller");
            var unsupported = Format("Unrelated feedback", TYMED.TYMED_HGLOBAL, -1);
            ThrowsHr(delegate { data.SetData(ref unsupported, ref medium, true); }, unchecked((int)0x80004001), "Unsupported SetData never consumes its medium even when release was requested");
            Check(BitConverter.ToInt32(GlobalBytes(medium.unionmember, 4), 0) == 1, "Rejected feedback medium remains valid for its caller");
            IntPtr address = GlobalLock(medium.unionmember);
            try { Marshal.WriteInt32(address, 2); } finally { GlobalUnlock(medium.unionmember); }
            ThrowsHr(delegate { data.SetData(ref format, ref medium, true); }, unchecked((int)0x80004001), "Move feedback is rejected rather than deleting received source bytes");
            address = GlobalLock(medium.unionmember); try { Marshal.WriteInt32(address, 1); } finally { GlobalUnlock(medium.unionmember); }
            data.SetData(ref format, ref medium, true);
            Check(medium.tymed == TYMED.TYMED_NULL && medium.unionmember == IntPtr.Zero, "Successful feedback with release=true frees exactly the transferred medium");
        }
        finally { if (medium.tymed != TYMED.TYMED_NULL) ReleaseStgMedium(ref medium); }
    }

    private static void ValidationAndLimits()
    {
        Check(MemoryFileClipboard.Prepare(new DecodedPayload()) == null, "Text/image preparation remains on the existing clipboard path");
        Throws<ArgumentNullException>(delegate { MemoryFileClipboard.Prepare(null); }, "Null payload is rejected");
        Throws<InvalidDataException>(delegate { MemoryFileClipboard.Prepare(new DecodedPayload { IsFileDrop = true }); }, "An empty file transfer is rejected");
        foreach (string invalid in new[] { "", "..\\escape.bin", "C:\\absolute.bin", "slash/name", "CON.txt", "trailing. ", new String('x', 221) })
            Reject(new PayloadFile { Path = invalid, Data = new byte[0] }, "Unsafe virtual names are rejected before descriptor allocation");
        Reject(null, "Null file entries are rejected");
        Reject(new PayloadFile { Path = "file.bin", Data = null }, "Missing file content is rejected");
        Reject(new PayloadFile { Path = "directory", IsDirectory = true, Data = new byte[] { 1 } }, "Directory entries cannot contain bytes");
        var duplicate = new DecodedPayload { IsFileDrop = true };
        duplicate.Files.Add(new PayloadFile { Path = "A.bin", Data = new byte[0] }); duplicate.Files.Add(new PayloadFile { Path = "a.bin", Data = new byte[0] });
        Throws<InvalidDataException>(delegate { MemoryFileClipboard.Prepare(duplicate); }, "Case-insensitive duplicate names are rejected");
        Reject(new PayloadFile { Path = "missing\\file.bin", Data = new byte[0] }, "A child requires its declared parent directory");
        var badParent = new DecodedPayload { IsFileDrop = true };
        badParent.Files.Add(new PayloadFile { Path = "parent", Data = new byte[0] }); badParent.Files.Add(new PayloadFile { Path = "parent\\child", Data = new byte[0] });
        Throws<InvalidDataException>(delegate { MemoryFileClipboard.Prepare(badParent); }, "A file cannot serve as another entry's parent directory");
        var maximum = new DecodedPayload { IsFileDrop = true };
        for (int index = 0; index < ClipboardPayload.MaxEntries; index++) maximum.Files.Add(new PayloadFile { Path = "file" + index, Data = new byte[0] });
        Check(MemoryFileClipboard.Prepare(maximum) != null, "The existing 4096-entry boundary remains supported");
        maximum = new DecodedPayload { IsFileDrop = true };
        for (int index = 0; index <= ClipboardPayload.MaxEntries; index++) maximum.Files.Add(new PayloadFile { Path = "file" + index, Data = new byte[0] });
        Throws<InvalidDataException>(delegate { MemoryFileClipboard.Prepare(maximum); }, "More than 4096 entries fail before snapshot allocation");
        Reject(new PayloadFile { Path = "b", Data = new byte[ClipboardPayload.MaxBytes] }, "The 32 MiB bound includes serialized metadata rather than only file data");
        var boundary = new DecodedPayload { IsFileDrop = true };
        boundary.Files.Add(new PayloadFile { Path = "b", Data = new byte[ClipboardPayload.MaxBytes - 16] });
        Check(MemoryFileClipboard.Prepare(boundary) != null, "A one-byte name plus header metadata and data exactly fitting 32 MiB is accepted");
        var nested = new DecodedPayload { IsFileDrop = true }; string path = "a";
        for (int index = 0; index <= 65; index++) { nested.Files.Add(new PayloadFile { Path = path, IsDirectory = true, Data = new byte[0] }); path += "\\a"; }
        Throws<InvalidDataException>(delegate { MemoryFileClipboard.Prepare(nested); }, "Folder nesting remains bounded to the sender's maximum depth");
    }

    private static void ConcurrentPreparation()
    {
        DecodedPayload payload = Files(); var result = new MemoryFileDataObject[8]; var threads = new Thread[8];
        for (int index = 0; index < threads.Length; index++) { int slot = index; threads[index] = new Thread(new ThreadStart(delegate { result[slot] = MemoryFileClipboard.Prepare(payload); })); threads[index].Start(); }
        foreach (Thread thread in threads) Check(thread.Join(5000), "Concurrent preparation finishes without waiting for clipboard access");
        for (int index = 1; index < result.Length; index++) Check(Object.ReferenceEquals(result[0], result[index]), "Concurrent preparations retain exactly one content snapshot");
    }

    private static void Reject(PayloadFile file, string message)
    {
        var payload = new DecodedPayload { IsFileDrop = true }; payload.Files.Add(file);
        Throws<InvalidDataException>(delegate { MemoryFileClipboard.Prepare(payload); }, message);
        Check(payload.PreparedMemoryFiles == null && payload.PreparedFileDirectory == null, "Rejected virtual content commits neither a memory provider nor disk preparation state");
    }
    private static byte[] GlobalBytes(IntPtr handle, int count)
    {
        Check(GlobalSize(handle).ToUInt64() >= (ulong)count, "Returned global memory is large enough for the advertised bytes");
        IntPtr pointer = GlobalLock(handle); Check(pointer != IntPtr.Zero, "Returned HGLOBAL can be locked by its receiver");
        try { var bytes = new byte[count]; if (count != 0) Marshal.Copy(pointer, bytes, 0, count); return bytes; }
        finally { GlobalUnlock(handle); }
    }
    private static byte[] Read(IStream stream, int count)
    {
        IntPtr countPointer = Marshal.AllocCoTaskMem(4);
        try
        {
            var bytes = new byte[count]; Marshal.WriteInt32(countPointer, 0); stream.Read(bytes, count, countPointer);
            int read = Marshal.ReadInt32(countPointer); Check(read >= 0 && read <= count, "Native stream reports a valid read count");
            var result = new byte[read]; Array.Copy(bytes, result, read); return result;
        }
        finally { Marshal.FreeCoTaskMem(countPointer); }
    }
    private static void Same(byte[] actual, byte[] expected, string message)
    { Check(actual.Length == expected.Length, message); for (int index = 0; index < expected.Length; index++) Check(actual[index] == expected[index], message); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static void Throws<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { checks++; return; } throw new Exception(message); }
    private static void ThrowsHr(Action action, int expected, string message) { try { action(); } catch (Exception error) { if (error.HResult == expected) { checks++; return; } throw; } throw new Exception(message); }
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern UIntPtr GlobalSize(IntPtr handle);
}
