using System;
using System.Collections.Generic;
using System.IO;

namespace HorizonClient
{
    // Resolves portable server paths only. Construction never reads, extracts,
    // migrates or creates files, and never examines a camera's private key.
    internal sealed class ServerPaths
    {
        internal static readonly ServerPaths Current = new ServerPaths(AppDomain.CurrentDomain.BaseDirectory);
        private readonly Func<string, FileAttributes> readAttributes;
        internal string BaseDirectory { get; private set; }
        internal string Root { get { return Path.Combine(BaseDirectory, "HorizonServer"); } }
        internal string DataDirectory { get { return Path.Combine(Root, "data"); } }
        internal string DebugLog { get { return Path.Combine(DataDirectory, "server.debug.log"); } }
        internal string SetupDebugLog { get { return Path.Combine(DataDirectory, "setup.debug.log"); } }
        internal string DebugFlag { get { return Path.Combine(DataDirectory, "server.debug.enabled"); } }
        internal string LanguageSetting { get { return Path.Combine(DataDirectory, "ui.language"); } }
        internal string SettingsFile { get { return Path.Combine(DataDirectory, "server.settings.xml"); } }
        internal string ReceivedRoot { get { return Path.Combine(Root, "Received"); } }

        internal ServerPaths(string executableDirectory) : this(executableDirectory, File.GetAttributes) { }

        internal ServerPaths(string executableDirectory, Func<string, FileAttributes> readAttributes)
        {
            if (readAttributes == null) throw new ArgumentNullException("readAttributes");
            if (String.IsNullOrWhiteSpace(executableDirectory) || !Path.IsPathRooted(executableDirectory))
                throw new ArgumentException("An absolute executable directory is required.", "executableDirectory");
            string root = Path.GetPathRoot(executableDirectory);
            if (root.Length <= 1 || (root.Length == 2 && root[1] == ':'))
                throw new ArgumentException("An absolute executable directory is required.", "executableDirectory");
            string full = Path.GetFullPath(executableDirectory); root = Path.GetPathRoot(full);
            while (full.Length > root.Length && (full[full.Length - 1] == Path.DirectorySeparatorChar || full[full.Length - 1] == Path.AltDirectorySeparatorChar))
                full = full.Substring(0, full.Length - 1);
            BaseDirectory = full;
            this.readAttributes = readAttributes;
        }

        // Elevated helpers must validate ancestors before creating directories
        // or opening a log. GetAttributes also catches dangling reparse points;
        // Exists would conceal access errors and some broken redirections.
        internal void ValidateHelperStorage()
        {
            ValidateOutputPath(SetupDebugLog, readAttributes);
        }

        internal static void ValidateOutputPath(string path)
        {
            ValidateOutputPath(path, File.GetAttributes);
        }

        internal static void ValidateOutputPath(string path, Func<string, FileAttributes> readAttributes)
        {
            if (readAttributes == null) throw new ArgumentNullException("readAttributes");
            path = Path.GetFullPath(path);
            var components = new List<string>();
            for (string component = path; !String.IsNullOrEmpty(component); component = Path.GetDirectoryName(component))
                components.Add(component);
            for (int index = components.Count - 1; index >= 0; index--)
            {
                FileAttributes attributes;
                try { attributes = readAttributes(components[index]); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The server helper storage contains a redirection: " + components[index]);
                if (index > 0 && (attributes & FileAttributes.Directory) == 0)
                    throw new IOException("The server helper storage ancestor is not a directory: " + components[index]);
                if (index == 0 && (attributes & FileAttributes.Directory) != 0)
                    throw new IOException("The server helper log path is a directory: " + components[index]);
            }
        }
    }
}
