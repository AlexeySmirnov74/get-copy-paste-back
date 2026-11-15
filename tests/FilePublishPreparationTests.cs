// /define:FILE_PREPARATION_TEST_MAIN builds a standalone test with ClipboardPayload.cs.
// These tests prepare temporary files only; they never read or write the system clipboard.
using System;
using System.IO;
using HorizonClient;

internal static class FilePublishPreparationTests
{
    internal static void RunAll()
    {
        string parent = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        string root = Path.Combine(parent, "FilePreparationTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RetryUsesPreparedFiles(Path.Combine(root, "retry"));
            ExistingTransferGetsNewDirectory(Path.Combine(root, "collision"));
            FailedExtractionIsUncached(Path.Combine(root, "partial"));
            RootAndPathValidation(Path.Combine(root, "validation"));
            Console.WriteLine("File publication preparation tests passed. No clipboard accessed.");
        }
        finally
        {
            // Only remove this test's randomized child directory after verifying its absolute target.
            string target = Path.GetFullPath(root);
            string expectedParent = parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (target.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(target).StartsWith("FilePreparationTests-", StringComparison.Ordinal))
                Directory.Delete(target, true);
        }
    }

#if FILE_PREPARATION_TEST_MAIN
    private static int Main()
    {
        try { RunAll(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
#endif

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static DecodedPayload Example()
    {
        DecodedPayload payload = new DecodedPayload { IsFileDrop = true };
        payload.Files.Add(new PayloadFile { Path = "folder", IsDirectory = true, Data = new byte[0] });
        payload.Files.Add(new PayloadFile { Path = "folder\\child.bin", Data = new byte[] { 1, 2, 3 } });
        payload.Files.Add(new PayloadFile { Path = "note.txt", Data = new byte[] { 65 } });
        return payload;
    }

    private static void RetryUsesPreparedFiles(string root)
    {
        DecodedPayload payload = Example();
        Guid id = Guid.NewGuid();
        string firstDirectory, nextDirectory;
        string[] firstPaths = ClipboardPayload.PrepareFileDrop(payload, root, id, out firstDirectory);
        payload.Files[1].Data = new byte[] { 9 }; // Retrying must not write this replacement buffer.
        string[] nextPaths = ClipboardPayload.PrepareFileDrop(payload, root, id, out nextDirectory);
        Assert(firstDirectory == nextDirectory, "Retry must reuse this payload's fully prepared directory.");
        Assert(Directory.GetDirectories(root).Length == 1, "Retry must not create another extraction directory.");
        Assert(!Object.ReferenceEquals(firstPaths, nextPaths) && nextPaths.Length == 2, "Each retry rebuilds the top-level FileDrop list.");
        Assert(nextPaths[0] == Path.Combine(firstDirectory, "folder") && nextPaths[1] == Path.Combine(firstDirectory, "note.txt"), "Only top-level entries belong in FileDrop.");
        Assert(File.ReadAllBytes(Path.Combine(firstDirectory, "folder\\child.bin")).Length == 3, "Retry must not re-extract completed files.");
    }

    private static void ExistingTransferGetsNewDirectory(string root)
    {
        Guid id = Guid.NewGuid();
        string occupied = Path.Combine(root, id.ToString("N"));
        Directory.CreateDirectory(occupied);
        string sentinel = Path.Combine(occupied, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        string prepared;
        ClipboardPayload.PrepareFileDrop(Example(), root, id, out prepared);
        Assert(prepared != occupied && Path.GetFileName(prepared).StartsWith(id.ToString("N") + "-", StringComparison.Ordinal), "A previous transfer directory needs a new GUID suffix.");
        Assert(File.ReadAllText(sentinel) == "keep", "Existing directory contents must remain unchanged.");
        // A new decoded object after Stop/Start must also be able to receive this same transfer ID.
        string restarted;
        ClipboardPayload.PrepareFileDrop(Example(), root, id, out restarted);
        Assert(restarted != occupied && restarted != prepared, "A restarted receiver must not reuse an earlier object's extraction.");
    }

    private static void FailedExtractionIsUncached(string root)
    {
        DecodedPayload payload = new DecodedPayload { IsFileDrop = true };
        payload.Files.Add(new PayloadFile { Path = "duplicate.bin", Data = new byte[] { 7 } });
        payload.Files.Add(new PayloadFile { Path = "duplicate.bin", Data = new byte[] { 8 } });
        Guid id = Guid.NewGuid();
        bool failed = false;
        string prepared;
        try { ClipboardPayload.PrepareFileDrop(payload, root, id, out prepared); }
        catch (IOException) { failed = true; }
        Assert(failed && payload.PreparedFileDirectory == null, "Partial extraction must not commit the cache.");
        string partial = Path.Combine(root, id.ToString("N"));
        Assert(File.ReadAllBytes(Path.Combine(partial, "duplicate.bin"))[0] == 7, "Failed extraction must leave existing partial files alone.");
        payload.Files.RemoveAt(1);
        ClipboardPayload.PrepareFileDrop(payload, root, id, out prepared);
        Assert(prepared != partial, "A later preparation attempt must use a fresh directory after extraction failure.");
    }

    private static void RootAndPathValidation(string root)
    {
        DecodedPayload payload = Example();
        Guid id = Guid.NewGuid();
        string prepared;
        ClipboardPayload.PrepareFileDrop(payload, root, id, out prepared);
        bool rejected = false;
        try { ClipboardPayload.PrepareFileDrop(payload, root + "-other", id, out prepared); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && !Directory.Exists(root + "-other"), "A cached directory cannot migrate to another receiving root.");
        DecodedPayload traversal = new DecodedPayload { IsFileDrop = true };
        traversal.Files.Add(new PayloadFile { Path = "..\\escape.bin", Data = new byte[] { 1 } });
        rejected = false;
        try { ClipboardPayload.PrepareFileDrop(traversal, root, Guid.NewGuid(), out prepared); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected && traversal.PreparedFileDirectory == null, "Relative traversal is rejected before extraction.");
        Assert(Directory.GetDirectories(root).Length == 1, "Invalid paths must not create a transfer directory.");
    }
}
