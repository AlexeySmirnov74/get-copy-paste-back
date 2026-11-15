using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

// Loads the production EXE, without invoking its entry point, from a fresh
// directory containing no runtime DLLs. Only its embedded resources may supply
// the modems/notices. A child process gives each EXE an isolated base directory.
internal static class PackagedAudioTests
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--child") return CheckPackage(args[1]);
            string build = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string name in new[] { "Client.exe", "Server.exe" })
            {
                string folder = Path.Combine(build, "packaged-audio-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
                string helper = Path.Combine(folder, "PackageCheck.exe"), package = Path.Combine(folder, name);
                File.Copy(typeof(PackagedAudioTests).Assembly.Location, helper); File.Copy(Path.Combine(build, name), package);
                var start = new ProcessStartInfo(helper, "--child " + name) { WorkingDirectory = folder, UseShellExecute = false,
                    CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process process = Process.Start(start))
                {
                    if (!process.WaitForExit(30000)) { process.Kill(); throw new Exception("Packaged audio check timed out."); }
                    Console.Write(process.StandardOutput.ReadToEnd()); string error = process.StandardError.ReadToEnd();
                    if (process.ExitCode != 0) throw new Exception(name + " packaged check failed: " + error);
                }
            }
            Console.WriteLine("Packaged audio checks passed: isolated PCM roundtrips, server/client DLL storage, no automatic notices and explicit third-party export from each EXE; no audio devices.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static int CheckPackage(string name)
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        bool server = name == "Server.exe";
        string localProfile = Path.Combine(root, "isolated-local-profile"), roamingProfile = Path.Combine(root, "isolated-roaming-profile");
        string audioDirectory = server ? Path.Combine(root, "HorizonServer", "audio-native") : Path.Combine(localProfile, "HorizonClient", "Settings", "audio-native");
        if (Directory.Exists(audioDirectory)) throw new Exception("Package fixture is not clean.");
        Assembly assembly = Assembly.LoadFile(Path.Combine(root, name));
        Type codecType = assembly.GetType("HorizonClient.NativeAudioCodec", true), kindType = assembly.GetType("HorizonClient.AudioModemKind", true);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
        if (!server)
        {
            // Match client startup ordering without probing a real user profile
            // or invoking Program.Main, clipboard listeners or audio devices.
            Type pathsType = assembly.GetType("HorizonClient.ClientPaths", true);
            object paths = pathsType.GetField("Current", flags).GetValue(null);
            pathsType.GetMethod("ConfigureSettingsStore", flags).Invoke(paths, new object[] { localProfile, roamingProfile });
        }
        int usedModems = 0;
        foreach (string modem in new[] { "Quiet", "Aicodix" })
        {
            object codec = codecType.GetMethod("Get", flags).Invoke(null, new[] { Enum.Parse(kindType, modem) });
            usedModems++;
            if (Directory.GetFiles(audioDirectory).Length != usedModems) throw new Exception("Only used modem DLLs may be materialized.");
            byte[] data = new byte[257]; new Random(709).NextBytes(data);
            float[] samples = (float[])codecType.GetMethod("Encode", flags).Invoke(codec, new object[] { data });
            object decoder = codecType.GetMethod("NewDecoder", flags).Invoke(codec, null); bool found = false;
            try
            {
                MethodInfo feed = decoder.GetType().GetMethod("Feed", flags);
                for (int offset = 0; offset < samples.Length; offset += 960)
                {
                    float[] chunk = new float[Math.Min(960, samples.Length - offset)]; Array.Copy(samples, offset, chunk, 0, chunk.Length);
                    foreach (byte[] message in (byte[][])feed.Invoke(decoder, new object[] { chunk }))
                    {
                        if (message.Length != data.Length) throw new Exception("Package decoded incorrect length.");
                        for (int i = 0; i < data.Length; i++) if (message[i] != data[i]) throw new Exception("Package decoded incorrect bytes.");
                        found = true;
                    }
                }
            }
            finally { ((IDisposable)decoder).Dispose(); }
            if (!found) throw new Exception(name + " could not roundtrip with " + modem + ".");
        }
        if (Directory.GetFiles(audioDirectory, "*.dll").Length != 2) throw new Exception("Both DLLs were not extracted.");
        if (Directory.Exists(Path.Combine(root, "audio-native"))) throw new Exception("Neither package may create the old root audio folder when client profile settings are writable.");
        if (!server && Directory.Exists(roamingProfile)) throw new Exception("Client audio must use the already selected LocalAppData settings store without probing Roaming again.");
        if (Directory.Exists(Path.Combine(root, "licenses"))) throw new Exception("Audio startup must not export third-party materials.");
        string notices = Path.Combine(root, "explicit-materials");
        Type materials = assembly.GetType("HorizonClient.ThirdPartyMaterials", true);
        materials.GetMethod("Export", flags).Invoke(null, new object[] { notices });
        foreach (string archive in new[] { "Audio-Licenses.zip", "quiet-source.zip", "quiet-relink.zip" })
        {
            byte[] header = new byte[4]; using (FileStream input = File.OpenRead(Path.Combine(notices, archive))) if (input.Read(header, 0, 4) != 4) throw new Exception("Empty archive.");
            if (header[0] != 80 || header[1] != 75) throw new Exception("Invalid notice/source archive.");
        }
        if (!File.Exists(Path.Combine(notices, "MP4Modem-LICENSE.txt")) || !File.Exists(Path.Combine(notices, "MP4Modem-NOTICE.txt"))) throw new Exception("Explicit export lacks the visual modem materials.");
        if (File.Exists(Path.Combine(notices, "ThirdPartyNotices.zip")) != server) throw new Exception("Server-only materials were not scoped to their package.");
        materials.GetMethod("Export", flags).Invoke(null, new object[] { notices });
        if (Directory.GetFiles(root, "*.ps1", SearchOption.AllDirectories).Length != 0 || Directory.Exists(Path.Combine(root, "licenses"))) throw new Exception("Package checks must not create scripts or automatic licenses.");
        Console.WriteLine(name + " single-EXE package: Quiet+aicodix PCM roundtrip and portable extraction PASS");
        return 0;
    }
}
