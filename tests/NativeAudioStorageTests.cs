using System;
using System.Collections.Generic;
using System.IO;
using HorizonClient;

// Only ordinary files under a unique fixture beside this test EXE are used.
// No native DLL is loaded and no audio/camera device is enumerated or opened.
internal static class NativeAudioStorageTests
{
    private static int checks;
    private static int Main()
    {
        string parent = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string fixture = Path.Combine(parent, "native-audio-storage-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            ClientSelectedSettingsPath(Path.Combine(fixture, "settings"));
            PortablePaths();
            LazyExtractionAndRelinking(Path.Combine(fixture, "lazy"));
            CurrentVersionsAndLoadedLibraries(Path.Combine(fixture, "versions"));
            ForeignLockedAndNestedFiles(Path.Combine(fixture, "ownership"));
            UnwritableStorageHasNoFallback(Path.Combine(fixture, "blocked"));
            Console.WriteLine("Native audio storage checks passed: " + checks + ". Isolated portable fixture only; no DLL loading, devices or user-directory access.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string resolved = Path.GetFullPath(fixture);
            if (String.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("native-audio-storage-test-", StringComparison.Ordinal) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static void ClientSelectedSettingsPath(string root)
    {
#if !HORIZON_SERVER
        string local = Path.Combine(root, "local-profile"), roaming = Path.Combine(root, "roaming-unused");
        ClientPaths.Current.ConfigureSettingsStore(local, roaming);
        string expected = Path.Combine(local, "HorizonClient", "Settings", "audio-native");
        Check(NativeAudioCodec.RuntimeDirectory == expected, "Client audio libraries follow the selected LocalAppData settings store rather than the EXE folder");
        Check(!Directory.Exists(expected), "Selecting client settings does not eagerly extract the audio library directory");
        byte[] image = Image(11);
        string extracted = NativeAudioStorage.ExtractImage(NativeAudioCodec.RuntimeDirectory, AudioModemKind.Quiet, image, new string[0], NotLoaded);
        Check(FileBytesEqual(extracted, image) && Path.GetDirectoryName(extracted) == expected, "Client codec extraction writes only below the selected settings store");
        Check(!Directory.Exists(roaming), "Native audio does not select a second profile store after settings choose a writable local directory");
        string blockedLocal = Path.Combine(root, "blocked-local"); File.WriteAllText(blockedLocal, "keep local blocker");
        ClientPaths.Current.ConfigureSettingsStore(blockedLocal, roaming);
        expected = Path.Combine(roaming, "HorizonClient", "Settings", "audio-native");
        Check(NativeAudioCodec.RuntimeDirectory == expected, "Native audio follows the Roaming settings fallback when LocalAppData is unwritable");
        extracted = NativeAudioStorage.ExtractImage(NativeAudioCodec.RuntimeDirectory, AudioModemKind.Quiet, image, new string[0], NotLoaded);
        Check(FileBytesEqual(extracted, image) && File.ReadAllText(blockedLocal) == "keep local blocker", "Roaming extraction does not alter the failed LocalAppData candidate");
#else
        Check(NativeAudioCodec.RuntimeDirectory == Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HorizonServer", "audio-native"), "A server build keeps its portable audio library location");
#endif
    }

    private static void PortablePaths()
    {
        const string baseDirectory = @"C:\Portable folder\Bridge";
        const string settingsDirectory = @"C:\Profile folder\HorizonClient\Settings";
        Check(NativeAudioStorage.GetDirectory(baseDirectory, false, settingsDirectory) == Path.Combine(settingsDirectory, "audio-native"), "Client native storage resolves beneath its explicit selected settings directory");
        Check(NativeAudioStorage.GetDirectory(baseDirectory, false, baseDirectory) == Path.Combine(baseDirectory, "audio-native"), "The executable folder is used only when it is the selected settings fallback");
        Check(NativeAudioStorage.GetDirectory(baseDirectory, true, null) == Path.Combine(baseDirectory, "HorizonServer", "audio-native"), "Server native storage stays under its portable HorizonServer directory without a client settings dependency");
#if HORIZON_SERVER
        Check(NativeAudioCodec.RuntimeDirectory == NativeAudioStorage.GetDirectory(AppDomain.CurrentDomain.BaseDirectory, true, null), "A server build selects the server storage branch");
#else
        Check(NativeAudioCodec.RuntimeDirectory == Path.Combine(ClientPaths.Current.SettingsDirectory, "audio-native"), "A client build selects the configured settings storage branch");
#endif
        foreach (string invalid in new[] { "", "relative", @"C:relative", @"\drive-relative" })
        {
            Throws<ArgumentException>(delegate { NativeAudioStorage.GetDirectory(invalid, false, settingsDirectory); }, "Native storage never resolves a working-directory-dependent executable base");
            Throws<ArgumentException>(delegate { NativeAudioStorage.GetDirectory(baseDirectory, false, invalid); }, "Native storage never resolves a working-directory-dependent settings base");
        }
        string expected = NativeAudioStorage.GetDirectory(baseDirectory, true, null), previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetPathRoot(previous);
            Check(NativeAudioStorage.GetDirectory(baseDirectory, true, null) == expected, "Changing working directory cannot redirect an absolute native storage path");
            Check(NativeAudioStorage.GetDirectory(baseDirectory, false, settingsDirectory) == Path.Combine(settingsDirectory, "audio-native"), "Changing working directory cannot redirect the selected client settings store");
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    private static void LazyExtractionAndRelinking(string root)
    {
        byte[] quiet = Image(19), aicodix = Image(23);
        string[] current = { NativeAudioStorage.LibraryName(AudioModemKind.Quiet, quiet), NativeAudioStorage.LibraryName(AudioModemKind.Aicodix, aicodix) };
        Check(!Directory.Exists(root), "Computing bundled file names creates no directory");
        string first = NativeAudioStorage.ExtractImage(root, AudioModemKind.Quiet, quiet, current, NotLoaded);
        Check(Path.GetFileName(first) == current[0] && FileBytesEqual(first, quiet), "The selected codec is atomically extracted under its content-derived name");
        Check(Directory.GetFiles(root).Length == 1 && Directory.GetDirectories(root).Length == 0 && !File.Exists(Path.Combine(root, current[1])),
            "Selecting one codec extracts exactly one DLL and no inactive codec, licenses, source archive or temporary file");
        byte[] relinked = { 7, 11, 29, 39 };
        File.WriteAllBytes(first, relinked);
        string restored = NativeAudioStorage.ExtractImage(root, AudioModemKind.Quiet, quiet, current, NotLoaded);
        Check(restored == first && FileBytesEqual(first, relinked), "An existing user-relinked library under the current name is never overwritten");
        string second = NativeAudioStorage.ExtractImage(root, AudioModemKind.Aicodix, aicodix, current, NotLoaded);
        Check(FileBytesEqual(second, aicodix) && Directory.GetFiles(root).Length == 2, "Switching codecs extracts only the second necessary DLL");
        Check(NativeAudioStorage.LibraryName(AudioModemKind.Quiet, quiet) != NativeAudioStorage.LibraryName(AudioModemKind.Quiet, aicodix),
            "A changed codec image receives a different stable file name");
        Throws<ArgumentOutOfRangeException>(delegate { NativeAudioStorage.LibraryName(AudioModemKind.Fsk, quiet); }, "FSK never extracts a native DLL");
        Throws<ArgumentException>(delegate { NativeAudioStorage.LibraryName(AudioModemKind.Quiet, new byte[511]); }, "Invalid native images are rejected before storage changes");
    }

    private static void CurrentVersionsAndLoadedLibraries(string root)
    {
        byte[] oldQuiet = Image(41), oldAicodix = Image(43), quiet = Image(47), aicodix = Image(53);
        string[] old = { NativeAudioStorage.LibraryName(AudioModemKind.Quiet, oldQuiet), NativeAudioStorage.LibraryName(AudioModemKind.Aicodix, oldAicodix) };
        string[] current = { NativeAudioStorage.LibraryName(AudioModemKind.Quiet, quiet), NativeAudioStorage.LibraryName(AudioModemKind.Aicodix, aicodix) };
        string loaded = NativeAudioStorage.ExtractImage(root, AudioModemKind.Quiet, oldQuiet, old, NotLoaded);
        string unused = NativeAudioStorage.ExtractImage(root, AudioModemKind.Aicodix, oldAicodix, old, NotLoaded);
        Func<string, bool> retained = delegate(string path) { return String.Equals(path, loaded, StringComparison.OrdinalIgnoreCase); };
        NativeAudioStorage.ExtractImage(root, AudioModemKind.Quiet, quiet, current, retained);
        Check(File.Exists(loaded) && !File.Exists(unused), "Upgrade cleanup retains a loaded old library and removes an unused obsolete codec");
        Check(File.Exists(Path.Combine(root, current[0])) && !File.Exists(Path.Combine(root, current[1])), "Cleanup preserves lazy extraction of the inactive current codec");
        NativeAudioStorage.ExtractImage(root, AudioModemKind.Aicodix, aicodix, current, retained);
        Check(Directory.GetFiles(root, "*.dll").Length == 3 && FileBytesEqual(loaded, oldQuiet), "Loading the other codec cannot delete or overwrite an active old module");
        NativeAudioStorage.CleanupStale(root, current, NotLoaded);
        Check(!File.Exists(loaded) && Directory.GetFiles(root, "*.dll").Length == 2, "Once the old module is inactive only the two current codec DLLs remain");
        NativeAudioStorage.CleanupStale(root, current, NotLoaded);
        Check(FileBytesEqual(Path.Combine(root, current[0]), quiet) && FileBytesEqual(Path.Combine(root, current[1]), aicodix), "Repeated cleanup retains both current libraries unchanged");
    }

    private static void ForeignLockedAndNestedFiles(string root)
    {
        Directory.CreateDirectory(root);
        string[] foreign = { "horizon-quiet.dll", "horizon-quiet-123.dll", "horizon-quiet-0123456789abcdef0123456789abcdef.dll",
            "horizon-quiet-0123456789abcdeg.dll", "horizon-other-0123456789abcdef.dll", "unrelated.dll", "quiet-source.zip", "quiet-relink.zip",
            "horizon-quiet-0123456789abcdef.dll.backup", "horizon-quiet-0123456789abcdef.dll.incomplete.tmp" };
        foreach (string name in foreign) File.WriteAllText(Path.Combine(root, name), "preserve " + name);
        string stale = Path.Combine(root, "horizon-quiet-1111111111111111.dll"), locked = Path.Combine(root, "horizon-aicodix-2222222222222222.dll"),
            readOnly = Path.Combine(root, "horizon-quiet-3333333333333333.dll");
        File.WriteAllText(stale, "stale"); File.WriteAllText(locked, "locked"); File.WriteAllText(readOnly, "readonly");
        string nested = Path.Combine(root, "nested"); Directory.CreateDirectory(nested);
        string nestedOwned = Path.Combine(nested, "horizon-quiet-4444444444444444.dll"); File.WriteAllText(nestedOwned, "nested library");
        string sibling = Path.Combine(Path.GetDirectoryName(root), "horizon-aicodix-5555555555555555.dll"); File.WriteAllText(sibling, "outside native directory");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            using (FileStream held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
                NativeAudioStorage.CleanupStale(root, new string[0], NotLoaded);
            Check(!File.Exists(stale), "A recognized inactive old codec is removed");
            Check(File.ReadAllText(locked) == "locked" && File.ReadAllText(readOnly) == "readonly", "Locked and read-only old libraries remain untouched");
            foreach (string name in foreign) Check(File.ReadAllText(Path.Combine(root, name)) == "preserve " + name, "Foreign files and nonmatching historical names are preserved");
            Check(File.ReadAllText(nestedOwned) == "nested library" && File.ReadAllText(sibling) == "outside native directory", "Cleanup never recurses or touches an adjacent directory");
        }
        finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
        NativeAudioStorage.CleanupStale(root, new string[0], NotLoaded);
        Check(!File.Exists(locked) && !File.Exists(readOnly), "Previously locked recognized libraries become removable after they are released");
        Check(NativeAudioStorage.OwnedLibraryName("horizon-quiet-0123456789abcdef.dll") && NativeAudioStorage.OwnedLibraryName("horizon-aicodix-0123456789abcdef.dll"), "Only the two exact generated codec name families are recognized");
        foreach (string name in foreign) Check(!NativeAudioStorage.OwnedLibraryName(name), "Foreign names cannot become cleanup candidates");
    }

    private static void UnwritableStorageHasNoFallback(string root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(root)); File.WriteAllText(root, "not a directory");
        byte[] image = Image(71);
        Throws<IOException>(delegate { NativeAudioStorage.ExtractImage(root, AudioModemKind.Quiet, image, new string[0], NotLoaded); }, "Unwritable native storage fails without silently changing the selected settings store or using temporary directories");
        Check(File.ReadAllText(root) == "not a directory", "Failed extraction leaves the blocked path unchanged");
    }

    private static bool NotLoaded(string path) { return false; }
    private static byte[] Image(int seed) { byte[] image = new byte[2048]; new Random(seed).NextBytes(image); return image; }
    private static bool FileBytesEqual(string path, byte[] expected)
    {
        byte[] actual = File.ReadAllBytes(path); if (actual.Length != expected.Length) return false;
        for (int index = 0; index < actual.Length; index++) if (actual[index] != expected[index]) return false;
        return true;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static void Throws<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { checks++; return; } throw new Exception(message); }
}
