// How nvim is launched: its arguments and the config directory it reads. Pure,
// so the rules are testable without a process.

namespace VSNeo_Extension.Nvim;

using System;
using System.Collections.Generic;
using System.IO;

internal static class NvimLaunch
{
    /// <summary>
    /// NVIM_APPNAME for the child: stdpath('config') is %LOCALAPPDATA%\vsneo
    /// and stdpath('data') %LOCALAPPDATA%\vsneo-data, apart from the user's
    /// terminal Neovim, whose plugins are written for a UI nothing draws here.
    /// </summary>
    public const string DefaultAppName = "vsneo";

    public static string Arguments(string pipeName)
    {
        // -u NORC: the user's init is not sourced by nvim's startup but by the
        // companion (see the user configuration section of vsneo.lua), after
        // its defaults and API exist, so the config can call vsneo.* and its
        // mappings win. The config directory is still on the runtimepath, so
        // its lua/, plugin/ and after/ work as in any Neovim.
        // -i NONE keeps the default shada file out: every Visual Studio
        // instance runs its own nvim, and a shared shada leaks registers,
        // marks, jumplist and history between them (nvim merges it on
        // focus changes, so a yank in one instance surfaces in another's
        // registers popup).
        return string.Join(" ", new[]
        {
            "--headless", "-u", "NORC", "-i", "NONE",
            // Before anything of the user's runs: plugin/ files and plugin
            // specs guard on it, as they do on vim.g.vscode elsewhere.
            "--cmd", "\"let g:vsneo=1\"",
            // The pre-config-directory plugin root: the standard packages
            // layout under ~/.vsneo (pack/<group>/start/<name>, or opt/<name>
            // for :packadd). packpath must be set with --cmd, which runs
            // before startup's packloadall: afterwards both packloadall and
            // :packadd ignore *start* directories, which is verified
            // behavior, not a quirk to code around.
            "--cmd", "\"exe 'set packpath+=' . fnameescape(expand('~/.vsneo'))\"",
            "--listen", @"\\.\pipe\" + pipeName,
        });
    }

    /// <summary>
    /// The environment that puts nvim's config directory where the setting
    /// says. Empty: the default app name, in nvim's standard place. A
    /// directory parent\leaf becomes XDG_CONFIG_HOME=parent and
    /// NVIM_APPNAME=leaf, which is the only way nvim takes a config location
    /// - so %LOCALAPPDATA%\nvim means the user's own Neovim config, plugins
    /// included. A path with no parent (a drive root) cannot be expressed
    /// and falls back to the default.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment(string? configDirectory)
    {
        var env = new Dictionary<string, string> { ["NVIM_APPNAME"] = DefaultAppName };
        if (TrySplit(configDirectory, out string parent, out string leaf))
        {
            env["XDG_CONFIG_HOME"] = parent;
            env["NVIM_APPNAME"] = leaf;
        }

        return env;
    }

    /// <summary>The directory nvim will read, for the diagnostics report.</summary>
    public static string ConfigDirectory(string? configDirectory, Func<string, string?> environment)
    {
        if (TrySplit(configDirectory, out string parent, out string leaf))
        {
            return Path.Combine(parent, leaf);
        }

        string? root = environment("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = environment("LOCALAPPDATA");
        }

        return Path.Combine(root ?? string.Empty, DefaultAppName);
    }

    private static bool TrySplit(string? configDirectory, out string parent, out string leaf)
    {
        parent = leaf = string.Empty;
        string path = (configDirectory ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0)
        {
            return false;
        }

        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)
                   + path.Substring(1);
        }

        try
        {
            path = Path.GetFullPath(System.Environment.ExpandEnvironmentVariables(path))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? directory = Path.GetDirectoryName(path);
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(directory) || name.Length == 0)
            {
                return false;
            }

            parent = directory!;
            leaf = name;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException
                                   || ex is PathTooLongException)
        {
            return false;
        }
    }
}
