using System;
using System.IO;
using System.Text;
using HorizonClient;

internal static class PortableClientTests
{
    private static int checks;
    [STAThread]
    public static int Main()
    {
        string testRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable-client-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            PurePaths();
            Directory.CreateDirectory(testRoot);
            ActualFiles(testRoot); UnwritableLocations(testRoot);
            SettingsStoreSelectionAndMigration(testRoot); SettingsStoreFallbacks(testRoot);
            Console.WriteLine("Portable client checks passed ({0} assertions; only a dedicated fixture beside the test EXE was written).", checks); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string resolved = Path.GetFullPath(testRoot), parent = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetDirectoryName(resolved).Equals(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("portable-client-test-", StringComparison.Ordinal) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static void PurePaths()
    {
        var paths = new ClientPaths(@"C:\Portable folder\HorizonClient\");
        Check(paths.BaseDirectory == @"C:\Portable folder\HorizonClient", "Portable base is the executable folder");
        Check(paths.SettingsDirectory == paths.BaseDirectory, "Construction keeps default settings beside the executable without profile I/O");
        Check(paths.CameraSelection == Path.Combine(paths.BaseDirectory, "camera.id"), "Camera choice is beside the executable");
        Check(paths.AutomaticCameraSetting == Path.Combine(paths.BaseDirectory, "camera.auto"), "Automatic camera preference follows the explicit settings store");
        Check(paths.AudioSelection == Path.Combine(paths.BaseDirectory, "audio.id"), "Audio choice is beside the executable");
        Check(paths.AudioModemSetting == Path.Combine(paths.BaseDirectory, "audio.modem"), "Modem choice is beside the executable");
        Check(paths.ErrorLog == Path.Combine(paths.BaseDirectory, "client.stderr.log"), "Failures are logged beside the executable");
        Check(paths.ReceivedRoot == Path.Combine(paths.BaseDirectory, "Received"), "The legacy receive-root path resolves without creating storage");
        Check(new ClientPaths(@"C:\").CameraSelection == @"C:\camera.id", "A drive root never becomes a drive-relative path");
        Throws<ArgumentException>(delegate { new ClientPaths(""); }, "Empty base rejected");
        Throws<ArgumentException>(delegate { new ClientPaths("relative"); }, "Working-directory-relative base rejected");
        Throws<ArgumentException>(delegate { new ClientPaths(@"C:relative"); }, "Drive-relative base rejected");
        Throws<ArgumentException>(delegate { new ClientPaths(@"\drive-relative"); }, "Current-drive-dependent base rejected");
        string expected = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), originalCwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetPathRoot(originalCwd);
            Check(ClientPaths.Current.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) == expected, "Launching from a different working directory does not redirect storage");
        }
        finally { Environment.CurrentDirectory = originalCwd; }
    }

    private static void ActualFiles(string root)
    {
        string portable = Path.Combine(root, "moved client folder");
        var paths = new ClientPaths(portable); var services = new ReceiverServices(paths);
        Check(!Directory.Exists(portable), "Paths and receiver services construction perform no I/O");
        Check(AudioModemSelection.Read(paths) == AudioModemKind.Aicodix && !Directory.Exists(portable), "Missing portable modem setting defaults to aicodix without creating a directory or loading a codec");
        Directory.CreateDirectory(portable);
        foreach (AudioModemKind kind in new[] { AudioModemKind.Fsk, AudioModemKind.Quiet, AudioModemKind.Aicodix })
        {
            AudioModemSelection.Write(paths, kind);
            Check(File.ReadAllText(paths.AudioModemSetting) == AudioModemCatalog.GetId(kind) && AudioModemSelection.Read(paths) == kind,
                "Every modem round-trips using its stable identifier beside the executable");
        }
        File.WriteAllText(paths.AudioModemSetting, "unknown-modem");
        Check(AudioModemSelection.Read(paths) == AudioModemKind.Aicodix && File.ReadAllText(paths.AudioModemSetting) == "unknown-modem",
            "Unknown modem setting falls back to aicodix without rewriting user files");
        File.WriteAllText(paths.AudioModemSetting, "  quiet\r\n");
        Check(AudioModemSelection.Read(paths) == AudioModemKind.Quiet, "Portable modem restore accepts surrounding whitespace");
        services.WriteSelection("opaque-camera-moniker");
        Check(services.ReadSelection() == "opaque-camera-moniker" && File.Exists(Path.Combine(portable, "camera.id")), "Production camera persistence uses the supplied executable folder");
        Check(services.ReceivedRoot == paths.ReceivedRoot && Object.ReferenceEquals(services.Paths, paths), "Receiver services retain the injected path layout without creating receive storage");
        Check(!Directory.Exists(Path.Combine(portable, "licenses")), "Normal client settings never extract bundled third-party materials");
        var payload = new DecodedPayload { IsFileDrop = true };
        payload.Files.Add(new PayloadFile { Path = "folder", IsDirectory = true, Data = new byte[0] });
        payload.Files.Add(new PayloadFile { Path = "folder\\fixture.bin", Data = new byte[] { 14, 18, 91 } });
        Guid transfer = Guid.NewGuid(); services.PreparePayload(payload, services.ReceivedRoot, transfer);
        Check(!Directory.Exists(paths.ReceivedRoot) && payload.PreparedMemoryFiles != null,
            "Received files are prepared in memory without a Received directory or system clipboard access");
        object prepared = payload.PreparedMemoryFiles;
        services.PreparePayload(payload, services.ReceivedRoot, transfer);
        Check(Object.ReferenceEquals(prepared, payload.PreparedMemoryFiles) && !Directory.Exists(paths.ReceivedRoot),
            "A receiver preparation retry reuses memory without creating disk copies");
        services.LogFailure(new IOException("fixture failure"));
        Check(File.ReadAllText(paths.ErrorLog).Contains("fixture failure"), "File transfer errors use the portable error log");
    }

    private static void UnwritableLocations(string root)
    {
        // A regular file in place of a directory reliably rejects child writes
        // without changing ACLs or relying on the test account's administrator status.
        string blocked = Path.Combine(root, "unwritable-executable-folder"); File.WriteAllText(blocked, "unchanged");
        var paths = new ClientPaths(blocked); var services = new ReceiverServices(paths);
        Throws<IOException>(delegate { services.WriteSelection("camera"); }, "Unwritable settings do not choose another storage location");
        Throws<IOException>(delegate { AudioModemSelection.Write(paths, AudioModemKind.Aicodix); }, "Unwritable modem setting never falls back to a profile or temporary directory");
        Check(AudioModemSelection.Read(paths) == AudioModemKind.Aicodix, "Unreadable modem setting selects the aicodix default without writing elsewhere");
        paths.TryLogFailure(new IOException("must not fall back"));
        Check(File.ReadAllText(blocked) == "unchanged", "Failed settings writes leave the blocked location unchanged");
        var fileDrop = new DecodedPayload { IsFileDrop = true }; fileDrop.Files.Add(new PayloadFile { Path = "fixture.txt", Data = Encoding.ASCII.GetBytes("fixture") });
        services.PreparePayload(fileDrop, services.ReceivedRoot, Guid.NewGuid());
        Check(fileDrop.PreparedMemoryFiles != null && File.ReadAllText(blocked) == "unchanged",
            "Memory-only file preparation works with an unwritable executable location and leaves its blocking file intact");
    }

    private static void SettingsStoreSelectionAndMigration(string root)
    {
        string portable = Path.Combine(root, "profile-settings client"), local = Path.Combine(root, "local-profile"), roaming = Path.Combine(root, "roaming-unused");
        Directory.CreateDirectory(portable);
        string legacyProfile = Path.Combine(local, "HorizonClient"); Directory.CreateDirectory(legacyProfile);
        File.WriteAllText(Path.Combine(legacyProfile, "audio.modem"), "quiet");
        string[] names = { "camera.id", "camera.auto", "audio.id", "audio.modem", "camera.resolution", "client.debug.enabled", "ui.language" };
        string[] values = { "camera-from-current-executable", "0", "waveout:3\r\nUnique Realtek name", "fsk", "1920x1080", "1", "en" };
        for (int index = 0; index < names.Length; index++) File.WriteAllText(Path.Combine(portable, names[index]), values[index]);
        File.WriteAllText(Path.Combine(portable, "unrelated.dat"), "must stay portable");
        File.WriteAllText(Path.Combine(portable, "client.debug.log"), "existing diagnostics");
        string readOnlySource = Path.Combine(portable, "audio.id");
        File.SetAttributes(readOnlySource, FileAttributes.ReadOnly);
        var paths = new ClientPaths(portable);
        try { paths.ConfigureSettingsStore(local, roaming); }
        finally { File.SetAttributes(readOnlySource, FileAttributes.Normal); }
        string expected = Path.Combine(local, "HorizonClient", "Settings");
        Check(paths.SettingsDirectory == expected && Directory.Exists(expected), "Explicit initialization selects writable LocalAppData/HorizonClient/Settings");
        Check(!Directory.Exists(roaming), "A writable local settings store never accesses the roaming fallback");
        Check(paths.CameraSelection == Path.Combine(expected, "camera.id") && paths.AudioSelection == Path.Combine(expected, "audio.id") && paths.AudioModemSetting == Path.Combine(expected, "audio.modem"), "Camera, output and modem choices use the configured store");
        Check(paths.CameraResolutionSetting == Path.Combine(expected, "camera.resolution") && paths.DebugLogSetting == Path.Combine(expected, "client.debug.enabled"), "Resolution and detailed-log flag are settings paths");
        Check(paths.LanguageSetting == Path.Combine(expected, "ui.language"), "Language follows the writable client settings store");
        Check(paths.AutomaticCameraSetting == Path.Combine(expected, "camera.auto"), "Automatic camera selection uses the configured profile settings directory");
        for (int index = 0; index < names.Length; index++)
            Check(File.Exists(Path.Combine(expected, names[index])) && File.ReadAllText(Path.Combine(expected, names[index])) == values[index] && File.ReadAllText(Path.Combine(portable, names[index])) == values[index], "Migration copies only a missing known setting and preserves the original: " + names[index]);
        Check((File.GetAttributes(paths.AudioSelection) & FileAttributes.ReadOnly) == 0, "A read-only legacy file is migrated as a writable setting");
        Check(!File.Exists(Path.Combine(expected, "unrelated.dat")) && !File.Exists(Path.Combine(expected, "client.debug.log")), "Migration does not move diagnostics or unrelated files into profile storage");
        Check(paths.BaseDirectory == portable && paths.ErrorLog == Path.Combine(portable, "client.stderr.log") && paths.DebugLog == Path.Combine(portable, "client.debug.log") && paths.ReceivedRoot == Path.Combine(portable, "Received"), "Logs and the unused legacy receive-root path remain portable after settings relocation");
        Check(AudioModemSelection.Read(paths) == AudioModemKind.Fsk, "Stale legacy AppData files outside the new Settings directory cannot override current portable choices");
        var services = new ReceiverServices(paths); services.WriteSelection("new-camera-choice");
        Check(services.ReadSelection() == "new-camera-choice" && File.ReadAllText(Path.Combine(portable, "camera.id")) == values[0], "Normal receiver writes update profile settings and leave the portable source untouched");
        AudioModemSelection.Write(paths, AudioModemKind.Aicodix);
        paths.ConfigureSettingsStore(local, roaming);
        Check(services.ReadSelection() == "new-camera-choice" && AudioModemSelection.Read(paths) == AudioModemKind.Aicodix, "Repeated initialization never overwrites an existing user setting with a migrated copy");
        Check(Directory.GetFiles(expected).Length == names.Length && Directory.GetFiles(expected, "*.tmp").Length == 0, "Writability probes and atomic migration leave no temporary files");
        UiText.SetLanguage(UiLanguage.Russian); UiText.Load(paths.LanguageSetting);
        Check(UiText.Language == UiLanguage.English, "A migrated portable English preference is restored from the selected client store");
        UiText.SetLanguage(UiLanguage.Russian); UiText.Save(paths.LanguageSetting);
        paths.ConfigureSettingsStore(local, roaming); UiText.SetLanguage(UiLanguage.English); UiText.Load(paths.LanguageSetting);
        Check(UiText.Language == UiLanguage.Russian && File.ReadAllText(Path.Combine(portable, "ui.language")) == "en", "An existing profile Russian preference wins over the new English default and an older portable English preference");

        string noPortable = Path.Combine(root, "no-legacy-settings"), cleanLocal = Path.Combine(root, "legacy-profile-only");
        Directory.CreateDirectory(Path.Combine(cleanLocal, "HorizonClient"));
        File.WriteAllText(Path.Combine(cleanLocal, "HorizonClient", "camera.id"), "stale-old-profile-camera");
        var clean = new ClientPaths(noPortable); clean.ConfigureSettingsStore(cleanLocal, null);
        Check(!File.Exists(clean.CameraSelection) && !Directory.Exists(noPortable), "A first new Settings directory does not import obsolete profile locations or create the executable directory");
        UiText.SetLanguage(UiLanguage.Russian); UiText.Load(clean.LanguageSetting);
        Check(UiText.Language == UiLanguage.English && !File.Exists(clean.LanguageSetting), "A fresh client settings store starts in English without writing a preference");
    }

    private static void SettingsStoreFallbacks(string root)
    {
        string blockedLocal = Path.Combine(root, "blocked-local-profile"), blockedRoaming = Path.Combine(root, "blocked-roaming-profile");
        File.WriteAllText(blockedLocal, "local unchanged"); File.WriteAllText(blockedRoaming, "roaming unchanged");
        string portable = Path.Combine(root, "fallback-executable"), roaming = Path.Combine(root, "writable-roaming");
        Directory.CreateDirectory(portable); File.WriteAllText(Path.Combine(portable, "camera.id"), "portable-camera");
        var paths = new ClientPaths(portable); paths.ConfigureSettingsStore(blockedLocal, roaming);
        Check(paths.SettingsDirectory == Path.Combine(roaming, "HorizonClient", "Settings") && File.ReadAllText(paths.CameraSelection) == "portable-camera", "Failed actual local write probe falls back to writable roaming storage and migrates known settings");
        Check(File.ReadAllText(blockedLocal) == "local unchanged", "A failed profile candidate is never replaced or removed");
        paths.ConfigureSettingsStore(blockedLocal, blockedRoaming);
        Check(paths.SettingsDirectory == portable && File.ReadAllText(paths.CameraSelection) == "portable-camera", "Two unwritable profile roots fall back to the executable directory");
        Check(Directory.GetFiles(portable, "*.tmp").Length == 0 && File.ReadAllText(blockedRoaming) == "roaming unchanged", "Portable fallback leaves no probes and does not alter a blocked roaming root");
        paths.ConfigureSettingsStore("relative-profile-root", @"\current-drive-dependent");
        Check(paths.SettingsDirectory == portable, "Invalid or relative profile roots cannot redirect storage through the working directory");
        var blocked = new ClientPaths(blockedLocal); blocked.ConfigureSettingsStore(blockedLocal, blockedRoaming);
        Check(blocked.SettingsDirectory == blockedLocal, "When every location rejects a write probe, the explicit executable fallback remains the reported store");
        UiText.SetLanguage(UiLanguage.Russian); UiText.Load(blocked.LanguageSetting);
        Check(UiText.Language == UiLanguage.English && File.ReadAllText(blockedLocal) == "local unchanged", "A fully unwritable client preference store still selects English without changing the blocked path");
        Throws<IOException>(delegate { new ReceiverServices(blocked).WriteSelection("camera"); }, "A fully unwritable configured store reports normal I/O failure without inventing another location");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static void Throws<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { checks++; return; } throw new Exception(message); }
}
