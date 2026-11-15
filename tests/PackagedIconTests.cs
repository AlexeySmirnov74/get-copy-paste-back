using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Reads EXE resources only. LOAD_LIBRARY_AS_DATAFILE never executes the app's
// entry point, resolves imports or starts a tray/window, device or clipboard.
internal static class PackagedIconTests
{
    private static int checks;
    private static int Main(string[] args)
    {
        try
        {
            string build = AppDomain.CurrentDomain.BaseDirectory;
            byte[] source = File.ReadAllBytes(Path.GetFullPath(Path.Combine(build, "..", "assets", "HorizonClient.ico")));
            if (args.Length == 2 && args[0] == "--verify") CheckPackage(Path.GetFullPath(args[1]), source);
            else
            {
                if (args.Length != 0) throw new ArgumentException("Unexpected icon-test arguments.");
                foreach (string name in new[] { "Server.exe", "Client.exe" }) CheckPackage(Path.Combine(build, name), source);
            }
            Console.WriteLine("Packaged icon checks passed: " + checks + ". Actual EXE native icon frames and embedded tray resource match the source; offline resource reads only.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckPackage(string path, byte[] expected)
    {
        Assembly assembly = Assembly.LoadFile(path); // No entry-point invocation.
        bool server = assembly.GetType("HorizonClient.ServerForm") != null;
        var product = (AssemblyProductAttribute)Attribute.GetCustomAttribute(assembly, typeof(AssemblyProductAttribute));
        var title = (AssemblyTitleAttribute)Attribute.GetCustomAttribute(assembly, typeof(AssemblyTitleAttribute));
        Check(product != null && product.Product == "Get Copy-Paste Back", "The executable advertises the new product identity.");
        Check(title != null && title.Title == (server ? "Get Copy-Paste Back — Server" : "Get Copy-Paste Back — Client"), "The executable description identifies its branded server/client role.");
        Check(assembly.GetName().Name == (server ? "Server" : "Client"), "The renamed file is compiled with the matching assembly identity.");
        Type catalog = assembly.GetType("HorizonClient.CameraNames");
        Check(catalog != null, "Both distributed executables must include the shared discovery catalog.");
        var catalogNames = (System.Collections.IList)catalog.GetProperty("Names", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null, null);
        Check(catalogNames.Count == 100 && catalogNames.Contains("Elgato Facecam MK.2") && !catalogNames.Contains("Chicony USB2.0 Camera"),
            "The compiled discovery catalog includes the server's suggested camera identities and excludes the observed unrelated Chicony.");
        byte[] tray;
        using (Stream stream = assembly.GetManifestResourceStream("HorizonClient.Tray.ico"))
        {
            Check(stream != null && stream.Length <= 4 * 1024 * 1024, "The built EXE must contain its bounded tray icon resource.");
            using (var memory = new MemoryStream()) { stream.CopyTo(memory); tray = memory.ToArray(); }
        }
        Same(tray, expected, "The built tray icon must match the canonical source ICO.");
        byte[] executable = NativeIcon(path, expected);
        foreach (int size in new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 })
        using (var trayBytes = new MemoryStream(tray, false))
        using (var exeBytes = new MemoryStream(executable, false))
        using (var trayIcon = new Icon(trayBytes, new Size(size, size)))
        using (var exeIcon = new Icon(exeBytes, new Size(size, size)))
        using (Bitmap trayBitmap = trayIcon.ToBitmap())
        using (Bitmap exeBitmap = exeIcon.ToBitmap())
        {
            Check(trayBitmap.Size == exeBitmap.Size, "Tray and executable select the same icon size at each supported DPI target.");
            bool equal = true;
            for (int y = 0; equal && y < trayBitmap.Height; y++) for (int x = 0; x < trayBitmap.Width; x++)
                if (trayBitmap.GetPixel(x, y).ToArgb() != exeBitmap.GetPixel(x, y).ToArgb()) { equal = false; break; }
            Check(equal, "Tray and native EXE icons must have identical rendered ARGB pixels at " + size + "px.");
        }
        // The client's real loader is side-effect free and clones the embedded
        // icon before closing its stream. Check that exact loader as well.
        Type receiver = assembly.GetType("HorizonClient.ReceiverContext");
        if (receiver != null)
        {
            MethodInfo loader = receiver.GetMethod("LoadTrayIcon", BindingFlags.Static | BindingFlags.NonPublic);
            using (Icon actual = (Icon)loader.Invoke(null, null))
            using (var bytes = new MemoryStream(expected, false))
            using (var icon = new Icon(bytes, actual.Size))
            using (Bitmap actualPixels = actual.ToBitmap())
            using (Bitmap sourcePixels = icon.ToBitmap())
            {
                bool equal = actualPixels.Size == sourcePixels.Size;
                for (int y = 0; equal && y < actualPixels.Height; y++) for (int x = 0; x < actualPixels.Width; x++)
                    if (actualPixels.GetPixel(x, y).ToArgb() != sourcePixels.GetPixel(x, y).ToArgb()) { equal = false; break; }
                Check(equal, "The actual client tray loader keeps the same icon after the resource stream closes.");
            }
        }
        Console.WriteLine(Path.GetFileName(path) + ": all ten native/tray icon sizes match (16 through 256px). No application was started.");
    }

    private static byte[] NativeIcon(string path, byte[] expected)
    {
        using (DataModule module = LoadLibraryExW(path, IntPtr.Zero, 0x00000002U)) // AS_DATAFILE
        {
            if (module.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var groups = new List<byte[]>(); Exception failure = null;
            ResourceNameCallback callback = delegate(IntPtr ignoredModule, IntPtr type, IntPtr name, IntPtr ignoredParameter)
            {
                try { groups.Add(Resource(module, type, name)); return true; }
                catch (Exception error) { failure = error; return false; }
            };
            bool enumerated = EnumResourceNamesW(module, new IntPtr(14), callback, IntPtr.Zero); // RT_GROUP_ICON
            GC.KeepAlive(callback);
            if (failure != null) throw failure;
            if (!enumerated) throw new Win32Exception(Marshal.GetLastWin32Error());
            Check(groups.Count == 1, "The EXE has exactly one application icon group.");
            byte[] group = groups[0];
            Check(group.Length >= 6 && U16(group, 0) == 0 && U16(group, 2) == 1, "The native icon group has a valid ICONDIR header.");
            int count = U16(group, 4);
            Check(count == U16(expected, 4) && group.Length == 6 + count * 14, "Native EXE and source ICO contain the same complete frame set.");
            var images = new List<byte[]>(); int length = 6 + count * 16;
            for (int index = 0; index < count; index++)
            {
                int resourceOffset = 6 + index * 14, icoOffset = 6 + index * 16;
                for (int field = 0; field < 12; field++) Check(group[resourceOffset + field] == expected[icoOffset + field], "Native icon frame dimensions, planes, bit depth and byte count match the tray ICO.");
                byte[] image = Resource(module, new IntPtr(3), new IntPtr(U16(group, resourceOffset + 12))); // RT_ICON
                int offset = checked((int)U32(expected, icoOffset + 12)), size = checked((int)U32(expected, icoOffset + 8));
                Check(offset >= 0 && size >= 0 && offset <= expected.Length - size && image.Length == size, "The icon image has its exact bounded size.");
                for (int pixel = 0; pixel < size; pixel++) if (image[pixel] != expected[offset + pixel]) throw new Exception("Native EXE icon bytes differ from the tray source at frame " + index + ".");
                checks++; images.Add(image); length = checked(length + image.Length);
            }
            var reconstructed = new byte[length]; Buffer.BlockCopy(expected, 0, reconstructed, 0, 6 + count * 16);
            int position = 6 + count * 16;
            for (int index = 0; index < count; index++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(position), 0, reconstructed, 6 + index * 16 + 12, 4);
                Buffer.BlockCopy(images[index], 0, reconstructed, position, images[index].Length); position += images[index].Length;
            }
            return reconstructed;
        }
    }

    private static byte[] Resource(DataModule module, IntPtr type, IntPtr name)
    {
        IntPtr information = FindResourceW(module, name, type);
        if (information == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        uint size = SizeofResource(module, information);
        Check(size > 0 && size <= 4 * 1024 * 1024, "Icon resources are bounded before allocating or copying.");
        IntPtr data = LoadResource(module, information), pointer = data == IntPtr.Zero ? IntPtr.Zero : LockResource(data);
        if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new byte[(int)size]; Marshal.Copy(pointer, result, 0, result.Length); return result;
        // Resource memory belongs to module; only FreeLibrary releases it.
    }
    private static ushort U16(byte[] bytes, int offset) { return BitConverter.ToUInt16(bytes, offset); }
    private static uint U32(byte[] bytes, int offset) { return BitConverter.ToUInt32(bytes, offset); }
    private static void Same(byte[] actual, byte[] expected, string message)
    { Check(actual.Length == expected.Length, message); for (int index = 0; index < actual.Length; index++) if (actual[index] != expected[index]) throw new Exception(message); checks++; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }

    private sealed class DataModule : SafeHandleZeroOrMinusOneIsInvalid
    {
        private DataModule() : base(true) { }
        protected override bool ReleaseHandle() { return FreeLibrary(handle); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)] private delegate bool ResourceNameCallback(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)] private static extern DataModule LoadLibraryExW(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumResourceNamesW(DataModule module, IntPtr type, ResourceNameCallback callback, IntPtr parameter);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] private static extern IntPtr FindResourceW(DataModule module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] private static extern uint SizeofResource(DataModule module, IntPtr information);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] private static extern IntPtr LoadResource(DataModule module, IntPtr information);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr module);
}
