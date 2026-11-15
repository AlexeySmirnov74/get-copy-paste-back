using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

// Inspects the production server EXE as data only. Never invokes its entry
// point, installer, signature verifier, native backend or any Windows device.
internal static class PackagedDriverTests
{
    private const string InstallerResource = "HorizonServer.DriverInstaller.exe";
    private const string InstallerSha256 = "38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A";
    private const string BackendSha256 = "A4E9387B23EF138F6957E5D87F079D2BDF41DA10F3E341F3CCFBBBE35FBCE63B";
    private static int assertions;

    private static int Main()
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Server.exe");
            Assembly package = Assembly.LoadFile(Path.GetFullPath(path));
            foreach (string name in new[] {
                "HorizonServer.runtime.usbbridge-camera.exe",
                "HorizonServer.scripts.Setup-Camera.ps1",
                "HorizonServer.scripts.Stop-Camera.ps1",
                "HorizonServer.scripts.Camera-Runtime.ps1",
                "HorizonServer.scripts.Repair-CameraDriver.ps1",
                "HorizonServer.defaults.ServerDefaults.json",
                InstallerResource,
                "HorizonServer.ThirdPartyNotices.zip" })
            {
                using (Stream resource = package.GetManifestResourceStream(name))
                    Check(resource != null && resource.Length > 0, "Embedded setup resource: " + name);
            }
            using (Stream installer = package.GetManifestResourceStream(InstallerResource))
            {
                Check(installer.Length == 26083632, "Complete pinned USBIP-WIN2 installer size");
                Check(Hash(installer, true) == InstallerSha256, "Complete pinned USBIP-WIN2 installer SHA-256");

                // The real VC_redist.x64.exe Burn manifest, inspected without
                // executing it, describes one attached payload container. The
                // complete container must exist, not merely a bootstrapper.
                // Its embedded x64 minimum/additional MSIs and cabinets have
                // Packaging="embedded" and no DownloadUrl attributes.
                CheckCabinet(installer, 6442442, 140070,
                    "8CA5B3FD5D7F169E904A0CB391589ED07E2E4976", "VC redistributable manifest cabinet");
                CheckCabinet(installer, 6592698, 18091661,
                    "A0C032F1194EF5BD77E767149B5A8777670F092A", "Complete attached VC redistributable payloads");
            }
            using (Stream backend = package.GetManifestResourceStream("HorizonServer.runtime.usbbridge-camera.exe"))
            {
                Check(backend.Length == 1134592, "Complete pinned camera backend size");
                Check(Hash(backend, true) == BackendSha256, "Complete pinned camera backend SHA-256");
            }
            using (Stream notices = package.GetManifestResourceStream("HorizonServer.ThirdPartyNotices.zip"))
            {
                Check(notices.ReadByte() == 80 && notices.ReadByte() == 75, "Embedded third-party notice archive");
            }
            Console.WriteLine("Packaged driver checks passed ({0} assertions): complete offline installer, VC redistributable payloads, camera backend and setup resources; no installation or devices.", assertions);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckCabinet(Stream installer, long offset, int length, string expectedSha1, string label)
    {
        Check(offset >= 0 && length >= 36 && offset <= installer.Length - length, label + " fits inside installer");
        installer.Position = offset;
        byte[] header = new byte[12];
        ReadExactly(installer, header, header.Length);
        Check(Encoding.ASCII.GetString(header, 0, 4) == "MSCF" && BitConverter.ToInt32(header, 8) == length,
            label + " cabinet header and declared length");
        installer.Position = offset;
        using (SHA1 hash = SHA1.Create())
        {
            byte[] buffer = new byte[8192];
            int remaining = length;
            while (remaining != 0)
            {
                int count = Math.Min(buffer.Length, remaining);
                ReadExactly(installer, buffer, count);
                hash.TransformBlock(buffer, 0, count, buffer, 0);
                remaining -= count;
            }
            hash.TransformFinalBlock(new byte[0], 0, 0);
            Check(Hex(hash.Hash) == expectedSha1, label + " matches the audited embedded cabinet");
        }
    }

    private static string Hash(Stream input, bool sha256)
    {
        input.Position = 0;
        using (HashAlgorithm hash = sha256 ? (HashAlgorithm)SHA256.Create() : SHA1.Create())
            return Hex(hash.ComputeHash(input));
    }

    private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", ""); }

    private static void ReadExactly(Stream input, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset != count)
        {
            int read = input.Read(buffer, offset, count - offset);
            if (read == 0) throw new EndOfStreamException("Truncated embedded installer cabinet.");
            offset += read;
        }
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }
}
