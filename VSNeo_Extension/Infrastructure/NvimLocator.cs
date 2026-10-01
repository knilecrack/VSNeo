using System;
using System.Collections.Generic;
using System.IO;

namespace VSNeo_Extension.Infrastructure
{
    /// <summary>
    /// Where nvim.exe is. A VSIX cannot check or install anything at install
    /// time (the installer runs no extension code and its prerequisites can
    /// only name Visual Studio components), so the check happens when the
    /// package loads, and this is the lookup it uses. Order: the explicit
    /// override, then PATH, then the places the usual installers put it -
    /// which matters right after an install, because a PATH edit does not
    /// reach a Visual Studio that is already running.
    ///
    /// Pure (no Visual Studio types), so the wire test links it and looks in
    /// the same places the extension does.
    /// </summary>
    internal static class NvimLocator
    {
        public const string PathVariable = "VSNEO_NVIM_PATH";

        /// <summary>The oldest nvim the companion script is written for.</summary>
        public static readonly Version MinimumVersion = new Version(0, 9, 0);

        /// <summary>winget's package id for Neovim.</summary>
        public const string WingetId = "Neovim.Neovim";

        public static string? Find()
        {
            var env = Environment.GetEnvironmentVariable(PathVariable);
            if (!string.IsNullOrEmpty(env))
            {
                // A directory is accepted too; people point at the install dir.
                if (File.Exists(env)) return env;
                var inDir = Path.Combine(env!, "nvim.exe");
                if (File.Exists(inDir)) return inDir;
                var inBin = Path.Combine(env!, "bin", "nvim.exe");
                if (File.Exists(inBin)) return inBin;
            }

            var onPath = FindOnPath("nvim.exe");
            if (onPath != null) return onPath;

            foreach (var candidate in WellKnownLocations())
                if (File.Exists(candidate)) return candidate;

            return null;
        }

        /// <summary>
        /// Where the installers put nvim.exe: winget / the MSI (Program Files),
        /// the per-user MSI, scoop, chocolatey.
        /// </summary>
        public static IEnumerable<string> WellKnownLocations()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (programFiles.Length > 0) yield return Path.Combine(programFiles, "Neovim", "bin", "nvim.exe");
            if (localAppData.Length > 0) yield return Path.Combine(localAppData, "Programs", "Neovim", "bin", "nvim.exe");
            if (programFilesX86.Length > 0) yield return Path.Combine(programFilesX86, "Neovim", "bin", "nvim.exe");
            if (profile.Length > 0)
            {
                yield return Path.Combine(profile, "scoop", "apps", "neovim", "current", "bin", "nvim.exe");
                yield return Path.Combine(profile, "scoop", "shims", "nvim.exe");
            }
            yield return @"C:\tools\neovim\nvim-win64\bin\nvim.exe";   // chocolatey
        }

        /// <summary>winget.exe, when the App Installer is present.</summary>
        public static string? FindWinget()
        {
            var onPath = FindOnPath("winget.exe");
            if (onPath != null) return onPath;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (localAppData.Length > 0)
            {
                var apps = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
                if (File.Exists(apps)) return apps;
            }
            return null;
        }

        private static string? FindOnPath(string file)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;

            foreach (var dir in path!.Split(Path.PathSeparator))
            {
                if (dir.Length == 0) continue;
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), file);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // A PATH entry with characters Path.Combine rejects: skip it.
                }
            }
            return null;
        }
    }
}
