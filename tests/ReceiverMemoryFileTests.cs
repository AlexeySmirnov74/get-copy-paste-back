using System;
using System.IO;
using HorizonClient;

// Exercises production receiver preparation without opening a clipboard or window.
internal static class ReceiverMemoryFileTests
{
    private static int Main()
    {
        string parent = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        string testRoot = Path.Combine(parent, "receiver-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            string receiveRoot = Path.Combine(testRoot, "Received");
            DecodedPayload payload = new DecodedPayload { IsFileDrop = true };
            payload.Files.Add(new PayloadFile { Path = "folder", IsDirectory = true, Data = new byte[0] });
            payload.Files.Add(new PayloadFile { Path = "folder\\test.bin", Data = new byte[] { 1, 2, 3 } });
            ReceiverServices services = new ReceiverServices();
            Guid transfer = Guid.NewGuid();
            services.PreparePayload(payload, receiveRoot, transfer);
            if (Directory.Exists(receiveRoot) || payload.PreparedFileDirectory != null)
                throw new Exception("Client file preparation must keep received files in memory without creating Received or a staging directory.");
            services.PreparePayload(payload, receiveRoot, transfer);
            if (Directory.GetFileSystemEntries(testRoot).Length != 0)
                throw new Exception("A preparation retry must not create intermediate files or folders.");
            Console.WriteLine("Receiver memory-file preparation passed: folders, retry, no Received or disk staging; no system clipboard access.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string absolute = Path.GetFullPath(testRoot);
            if (absolute.StartsWith(parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(absolute).StartsWith("receiver-memory-", StringComparison.Ordinal)
                && Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
    }
}
