using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using HorizonClient;

internal static class ThirdPartyMaterialsTests
{
    private static int checks;
    private static int Main()
    {
        string parent = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string fixture = Path.Combine(parent, "third-party-export-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            RuntimeHelpers.RunClassConstructor(typeof(ThirdPartyMaterials).TypeHandle);
            Check(!Directory.Exists(fixture), "Initializing the exporter creates no output directory");
            InvalidPathsAndMissingResources(fixture);
            ExactAndRepeatExports(Path.Combine(fixture, "client"), false);
            ExactAndRepeatExports(Path.Combine(fixture, "server"), true);
            ExistingCollisions(Path.Combine(fixture, "collision"));
            RedirectionChecks(Path.Combine(fixture, "redirections"));
            RacingCollision(Path.Combine(fixture, "race"));
            Console.WriteLine("Third-party material export checks passed: " + checks + ". Explicit isolated destinations and synthetic embedded streams only; no profiles, devices or automatic exports.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string resolved = Path.GetFullPath(fixture);
            if (String.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("third-party-export-test-", StringComparison.Ordinal) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static void InvalidPathsAndMissingResources(string root)
    {
        var resources = new Resources(false);
        foreach (string invalid in new[] { null, "", " ", "relative", @"C:relative", @"\drive-relative" })
            Throws<ArgumentException>(delegate { ThirdPartyMaterials.ExportFromResources(invalid, resources.Open); }, "Working-directory-dependent export locations are rejected");
        Check(resources.Opens == 0 && !Directory.Exists(root), "Invalid paths do not inspect resources or create any directory");
        string missing = Path.Combine(root, "missing-resource"); resources.Missing = "HorizonClient.MP4Modem.NOTICE";
        Throws<FileNotFoundException>(delegate { ThirdPartyMaterials.ExportFromResources(missing, resources.Open); }, "A missing required embedded material fails explicitly");
        Check(!Directory.Exists(missing) && resources.Disposals == 4, "Missing required materials leave no partial exports and close earlier streams");
        Directory.CreateDirectory(root); string blocked = Path.Combine(root, "regular-file"); File.WriteAllText(blocked, "preserved");
        resources = new Resources(false);
        Throws<IOException>(delegate { ThirdPartyMaterials.ExportFromResources(blocked, resources.Open); }, "A regular file cannot become the export directory");
        Check(File.ReadAllText(blocked) == "preserved" && resources.Opens == 0, "An invalid destination is preserved without reading any source material");
        string child = Path.Combine(blocked, "child");
        Throws<IOException>(delegate { ThirdPartyMaterials.ExportFromResources(child, resources.Open); }, "A file in a destination ancestor is rejected before directory creation");
    }

    private static void ExactAndRepeatExports(string root, bool server)
    {
        var resources = new Resources(server);
        string originalCwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetPathRoot(originalCwd);
            ThirdPartyMaterials.ExportFromResources(root, resources.Open);
        }
        finally { Environment.CurrentDirectory = originalCwd; }
        Check(Directory.GetFiles(root).Length == resources.Files.Count && Directory.GetDirectories(root).Length == 0,
            "An explicit destination contains only the requested material files");
        Check(resources.Opens == 6 && resources.Disposals == resources.Files.Count, "Every returned embedded stream is closed, including optional server materials");
        var timestamps = new Dictionary<string, DateTime>(); DateTime marker = new DateTime(2012, 3, 4, 5, 6, 8, DateTimeKind.Utc);
        foreach (KeyValuePair<string, byte[]> file in resources.Files)
        {
            string path = Path.Combine(root, file.Key);
            Check(Equal(File.ReadAllBytes(path), file.Value), "Every exported material matches its embedded bytes exactly");
            File.SetLastWriteTimeUtc(path, marker); timestamps.Add(path, File.GetLastWriteTimeUtc(path));
        }
        string readOnly = Path.Combine(root, "MP4Modem-LICENSE.txt"); File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try { ThirdPartyMaterials.ExportFromResources(root, resources.Open); }
        finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
        foreach (KeyValuePair<string, DateTime> timestamp in timestamps)
            Check(File.GetLastWriteTimeUtc(timestamp.Key) == timestamp.Value, "Identical existing files retain their timestamps and are never rewritten");
        Check(Directory.GetFiles(root).Length == resources.Files.Count && Directory.GetFiles(root, "*.tmp").Length == 0,
            "Repeating an export produces no duplicates or temporary leftovers");
    }

    private static void ExistingCollisions(string root)
    {
        Directory.CreateDirectory(root); var resources = new Resources(false);
        string collision = Path.Combine(root, "MP4Modem-NOTICE.txt"); File.WriteAllText(collision, "different user content");
        Throws<IOException>(delegate { ThirdPartyMaterials.ExportFromResources(root, resources.Open); }, "Different existing material content is never overwritten");
        Check(File.ReadAllText(collision) == "different user content" && Directory.GetFiles(root).Length == 1,
            "A collision on the last required material is detected before writing any earlier material");
        File.Delete(collision); Directory.CreateDirectory(collision);
        Throws<IOException>(delegate { ThirdPartyMaterials.ExportFromResources(root, resources.Open); }, "An existing directory cannot be overwritten as an export file");
        Check(Directory.Exists(collision) && Directory.GetFiles(root).Length == 0, "A file/directory collision leaves the destination unchanged");
    }

    private static void RedirectionChecks(string root)
    {
        Directory.CreateDirectory(root);
        string before = Path.Combine(root, "redirected-ancestor", "target"); var resources = new Resources(false);
        Func<string, FileAttributes> ancestor = delegate(string path)
        {
            if (String.Equals(path, Path.GetDirectoryName(before), StringComparison.OrdinalIgnoreCase)) return FileAttributes.Directory | FileAttributes.ReparsePoint;
            return File.GetAttributes(path);
        };
        Throws<IOException>(delegate { ThirdPartyMaterials.ExportFromResources(before, resources.Open, ancestor); }, "Existing reparse ancestors are rejected before creating output directories");
        Check(resources.Opens == 0 && !Directory.Exists(before), "A redirected ancestor prevents resource reads and directory writes");
        string after = Path.Combine(root, "redirected-after-create");
        Func<string, FileAttributes> afterCreate = delegate(string path)
        {
            FileAttributes value = File.GetAttributes(path);
            return String.Equals(path, after, StringComparison.OrdinalIgnoreCase) ? value | FileAttributes.ReparsePoint : value;
        };
        Throws<IOException>(delegate { ThirdPartyMaterials.ExportFromResources(after, resources.Open, afterCreate); }, "Directory components are checked again after creating the destination");
        Check(Directory.Exists(after) && Directory.GetFiles(after).Length == 0, "A newly redirected destination receives no exported content");
        string fileRoot = Path.Combine(root, "redirected-file"); Directory.CreateDirectory(fileRoot);
        string file = Path.Combine(fileRoot, "Audio-Licenses.zip"); File.WriteAllText(file, "preserved redirected file");
        Func<string, FileAttributes> redirectedFile = delegate(string path)
        {
            FileAttributes value = File.GetAttributes(path);
            return String.Equals(path, file, StringComparison.OrdinalIgnoreCase) ? value | FileAttributes.ReparsePoint : value;
        };
        Throws<IOException>(delegate { ThirdPartyMaterials.ExportFromResources(fileRoot, resources.Open, redirectedFile); }, "Reparse export files are rejected before reading or replacing them");
        Check(File.ReadAllText(file) == "preserved redirected file", "A redirected existing file retains its original contents");
        string temporaryRoot = Path.Combine(root, "redirected-temporary");
        Func<string, FileAttributes> redirectedTemporary = delegate(string path)
        {
            if (Path.GetFileName(path).StartsWith(".third-party-", StringComparison.Ordinal)) return FileAttributes.ReparsePoint;
            return File.GetAttributes(path);
        };
        Throws<IOException>(delegate { ThirdPartyMaterials.ExportFromResources(temporaryRoot, resources.Open, redirectedTemporary); }, "Temporary target components are validated immediately before opening a write handle");
        Check(Directory.Exists(temporaryRoot) && Directory.GetFiles(temporaryRoot).Length == 0, "A redirected temporary path is never written");
    }

    private static void RacingCollision(string root)
    {
        var resources = new Resources(false); bool raced = false;
        string collision = Path.Combine(root, "Audio-Licenses.zip");
        Func<string, FileAttributes> attributes = delegate(string path)
        {
            if (!raced && Path.GetFileName(path).StartsWith(".third-party-", StringComparison.Ordinal))
            { raced = true; File.WriteAllText(collision, "created by a competing writer"); }
            return File.GetAttributes(path);
        };
        Throws<IOException>(delegate { ThirdPartyMaterials.ExportFromResources(root, resources.Open, attributes); }, "A competing writer cannot be overwritten after preflight");
        Check(raced && File.ReadAllText(collision) == "created by a competing writer" && Directory.GetFiles(root).Length == 1,
            "A late collision preserves the competing file and removes the exporter's temporary file");
    }

    private sealed class Resources
    {
        internal readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
        private readonly Dictionary<string, byte[]> embedded = new Dictionary<string, byte[]>();
        internal int Opens, Disposals;
        internal string Missing;
        internal Resources(bool server)
        {
            Add("HorizonClient.Audio.Audio-Licenses.zip", "Audio-Licenses.zip", 1);
            Add("HorizonClient.Audio.quiet-source.zip", "quiet-source.zip", 2);
            Add("HorizonClient.Audio.quiet-relink.zip", "quiet-relink.zip", 3);
            Add("HorizonClient.MP4Modem.LICENSE", "MP4Modem-LICENSE.txt", 4);
            Add("HorizonClient.MP4Modem.NOTICE", "MP4Modem-NOTICE.txt", 5);
            if (server) Add("HorizonServer.ThirdPartyNotices.zip", "ThirdPartyNotices.zip", 6);
        }
        private void Add(string resource, string file, int seed)
        {
            byte[] data = new byte[4099 + seed * 5001]; new Random(seed).NextBytes(data);
            embedded.Add(resource, data); Files.Add(file, data);
        }
        internal Stream Open(string resource)
        {
            Opens++; byte[] data;
            if (resource == Missing || !embedded.TryGetValue(resource, out data)) return null;
            return new TrackedStream(data, delegate { Disposals++; });
        }
    }
    private sealed class TrackedStream : MemoryStream
    {
        private readonly Action closed;
        internal TrackedStream(byte[] data, Action closed) : base(data, false) { this.closed = closed; }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) closed(); }
    }
    private static bool Equal(byte[] first, byte[] second)
    {
        if (first.Length != second.Length) return false;
        for (int index = 0; index < first.Length; index++) if (first[index] != second[index]) return false;
        return true;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static void Throws<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { checks++; return; } throw new Exception(message); }
}
