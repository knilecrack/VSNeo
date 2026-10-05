// Seeky picker embedded in NeoVS — SeekyVS's RecentFiles (vs2026/SeekyVS/RecentFiles.cs),
// net472 spellings only. Same store file, so standalone SeekyVS and VSNeo share one list.

namespace VSNeo_Extension.Seeky;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Most-recently-used files, most recent first, as absolute paths — what Find Files lists on an
/// empty prompt. Fed by Seeky picks, every editor that takes focus
/// (TextViewCreationListener), and the file that is active when the picker opens.
/// </summary>
/// <remarks>
/// <para>
/// fff cannot supply this: its access frecency is only ever fed by <c>track_access</c>, which the
/// C API does not export — every access score it reports is 0.
/// </para>
/// <para>
/// One list for every solution, filtered to the current workspace when read, persisted to
/// <c>%LOCALAPPDATA%\SeekyVS\recent.json</c> on every touch that changes the head. Touches are
/// focus changes and picks, not keystrokes, so the write rate is a human one. Best-effort
/// throughout: a list that cannot be read or written costs the user history, never a search.
/// </para>
/// </remarks>
internal static class RecentFiles
{
    private const int MaxEntries = 500;

    private static readonly object Gate = new();

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SeekyVS",
        "recent.json");

    private static List<string>? entries;

    /// <summary>Moves <paramref name="fullPath"/> to the front of the list and persists it.</summary>
    internal static void Touch(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return;
        }

        string path;
        try
        {
            path = Path.GetFullPath(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        lock (Gate)
        {
            List<string> list = EnsureLoaded();
            if (list.Count > 0 && string.Equals(list[0], path, StringComparison.OrdinalIgnoreCase))
            {
                return; // already the most recent — nothing to write
            }

            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            if (list.Count > MaxEntries)
            {
                list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                File.WriteAllText(StorePath, JsonSerializer.Serialize(list));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SeekyLog.Error($"recent files: saving '{StorePath}' failed", ex);
            }
        }
    }

    /// <summary>
    /// Up to <paramref name="max"/> recent files under <paramref name="workspaceDir"/> that still
    /// exist, most recent first, as workspace-relative '/' paths. <paramref name="exclude"/> (the
    /// active document, workspace-relative) is left out so the top row is the previous file —
    /// Enter on an empty prompt flips between the last two.
    /// </summary>
    internal static List<string> InWorkspace(string workspaceDir, string? exclude, int max)
    {
        string[] snapshot;
        lock (Gate)
        {
            snapshot = EnsureLoaded().ToArray();
        }

        string prefix = workspaceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return snapshot
            .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => (Full: p, Relative: p.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/')))
            .Where(p => !string.Equals(p.Relative, exclude, StringComparison.OrdinalIgnoreCase) && File.Exists(p.Full))
            .Take(max)
            .Select(p => p.Relative)
            .ToList();
    }

    /// <summary>
    /// Up to <paramref name="max"/> recent files that still exist, anywhere, most recent first,
    /// as absolute paths (VSNeo's Recent Files picker and its buffer ordering).
    /// </summary>
    internal static List<string> All(int max)
    {
        string[] snapshot;
        lock (Gate)
        {
            snapshot = EnsureLoaded().ToArray();
        }

        return snapshot.Where(File.Exists).Take(max).ToList();
    }

    /// <summary>Caller must hold <see cref="Gate"/>.</summary>
    private static List<string> EnsureLoaded()
    {
        if (entries is not null)
        {
            return entries;
        }

        entries = new List<string>();
        try
        {
            if (File.Exists(StorePath))
            {
                entries = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePath)) ?? new List<string>();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            SeekyLog.Error($"recent files: unreadable '{StorePath}'", ex);
        }

        return entries;
    }
}
