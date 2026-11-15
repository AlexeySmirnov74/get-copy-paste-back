using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security;

namespace HorizonClient
{
    // Explicit, caller-directed export only. There are no startup hooks, default
    // output folders, profile lookups or runtime downloads in this component.
    internal static class ThirdPartyMaterials
    {
        private static readonly Material[] Materials = {
            new Material("HorizonClient.Audio.Audio-Licenses.zip", "Audio-Licenses.zip", false),
            new Material("HorizonClient.Audio.quiet-source.zip", "quiet-source.zip", false),
            new Material("HorizonClient.Audio.quiet-relink.zip", "quiet-relink.zip", false),
            new Material("HorizonClient.MP4Modem.LICENSE", "MP4Modem-LICENSE.txt", false),
            new Material("HorizonClient.MP4Modem.NOTICE", "MP4Modem-NOTICE.txt", false),
            new Material("HorizonServer.ThirdPartyNotices.zip", "ThirdPartyNotices.zip", true)
        };

        internal static void Export(string absoluteDirectory)
        {
            Assembly assembly = typeof(ThirdPartyMaterials).Assembly;
            ExportFromResources(absoluteDirectory, assembly.GetManifestResourceStream);
        }

        internal static void ExportFromResources(string absoluteDirectory, Func<string, Stream> openResource)
        {
            ExportFromResources(absoluteDirectory, openResource, File.GetAttributes);
        }

        // Attribute injection exercises redirection boundaries without creating
        // actual symlinks, changing permissions or requiring administrator rights.
        internal static void ExportFromResources(string absoluteDirectory, Func<string, Stream> openResource, Func<string, FileAttributes> attributes)
        {
            string directory = AbsoluteDirectory(absoluteDirectory);
            if (openResource == null) throw new ArgumentNullException("openResource");
            if (attributes == null) throw new ArgumentNullException("attributes");
            var storage = new ExportStorage(attributes);
            storage.ValidateDirectory(directory);
            var files = new List<ExportFile>();
            foreach (Material material in Materials)
            {
                byte[] bytes;
                using (Stream source = openResource(material.Resource))
                {
                    if (source == null)
                    {
                        if (material.Optional) continue;
                        throw new FileNotFoundException("This package does not contain required third-party materials.", material.Resource);
                    }
                    using (var buffer = new MemoryStream()) { source.CopyTo(buffer); bytes = buffer.ToArray(); }
                }
                files.Add(new ExportFile(Path.Combine(directory, material.FileName), bytes));
            }
            // Reject collisions before creating any material. Existing identical
            // exports are retained byte-for-byte, including their timestamps.
            foreach (ExportFile file in files) storage.ExistingMatches(file.Path, file.Bytes);
            storage.ValidateDirectory(directory);
            Directory.CreateDirectory(directory);
            storage.ValidateDirectory(directory);
            foreach (ExportFile file in files) storage.WriteAtomic(directory, file);
        }

        private static string AbsoluteDirectory(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) throw new ArgumentException("An absolute export directory is required.", "absoluteDirectory");
            string root = Path.GetPathRoot(directory);
            if (root.Length <= 1 || (root.Length == 2 && root[1] == ':')) throw new ArgumentException("An absolute export directory is required.", "absoluteDirectory");
            string full = Path.GetFullPath(directory);
            while (full.Length > Path.GetPathRoot(full).Length && (full[full.Length - 1] == Path.DirectorySeparatorChar || full[full.Length - 1] == Path.AltDirectorySeparatorChar))
                full = full.Substring(0, full.Length - 1);
            return full;
        }

        private sealed class ExportStorage
        {
            private readonly Func<string, FileAttributes> attributes;
            internal ExportStorage(Func<string, FileAttributes> attributes) { this.attributes = attributes; }

            internal void ValidateDirectory(string directory) { ValidateComponents(directory, true); }

            private void ValidateComponents(string target, bool directory)
            {
                var components = new List<string>();
                for (string component = target; !String.IsNullOrEmpty(component); component = Path.GetDirectoryName(component)) components.Add(component);
                for (int index = components.Count - 1; index >= 0; index--)
                {
                    FileAttributes? found = ReadAttributes(components[index]);
                    if (!found.HasValue) continue;
                    if ((found.Value & FileAttributes.ReparsePoint) != 0) throw new IOException("The export path contains a redirection: " + components[index]);
                    bool expectDirectory = index != 0 || directory;
                    if (expectDirectory != ((found.Value & FileAttributes.Directory) != 0))
                        throw new IOException("The export path has an incompatible existing file or directory: " + components[index]);
                }
            }

            private FileAttributes? ReadAttributes(string path)
            {
                try { return attributes(path); }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
            }

            internal bool ExistingMatches(string path, byte[] expected)
            {
                ValidateComponents(path, false);
                if (!ReadAttributes(path).HasValue) return false;
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length != expected.Length) throw Collision(path);
                    byte[] buffer = new byte[8192]; int offset = 0;
                    while (offset < expected.Length)
                    {
                        int count = input.Read(buffer, 0, Math.Min(buffer.Length, expected.Length - offset));
                        if (count == 0) throw Collision(path);
                        for (int index = 0; index < count; index++) if (buffer[index] != expected[offset + index]) throw Collision(path);
                        offset += count;
                    }
                    if (input.ReadByte() != -1) throw Collision(path);
                }
                return true;
            }

            internal void WriteAtomic(string directory, ExportFile file)
            {
                ValidateDirectory(directory);
                if (ExistingMatches(file.Path, file.Bytes)) return;
                string temporary = Path.Combine(directory, ".third-party-" + Guid.NewGuid().ToString("N") + ".tmp");
                bool created = false;
                try
                {
                    ValidateDirectory(directory);
                    ValidateComponents(temporary, false);
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        created = true;
                        output.Write(file.Bytes, 0, file.Bytes.Length);
                        output.Flush();
                    }
                    ValidateDirectory(directory);
                    if (ExistingMatches(file.Path, file.Bytes)) return;
                    try { File.Move(temporary, file.Path); }
                    catch (IOException) { if (!ExistingMatches(file.Path, file.Bytes)) throw; }
                }
                finally
                {
                    if (created)
                    {
                        try
                        {
                            ValidateComponents(temporary, false);
                            if (ReadAttributes(temporary).HasValue) File.Delete(temporary);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        catch (SecurityException) { }
                    }
                }
            }

            private static IOException Collision(string path) { return new IOException("Export would overwrite different existing content: " + path); }
        }

        private sealed class Material
        {
            internal readonly string Resource, FileName;
            internal readonly bool Optional;
            internal Material(string resource, string fileName, bool optional) { Resource = resource; FileName = fileName; Optional = optional; }
        }
        private sealed class ExportFile
        {
            internal readonly string Path;
            internal readonly byte[] Bytes;
            internal ExportFile(string path, byte[] bytes) { Path = path; Bytes = bytes; }
        }
    }
}
