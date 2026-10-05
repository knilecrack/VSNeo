// Seeky picker embedded in NeoVS — the list pickers only VSNeo can feed (Telescope's buffers,
// oldfiles, marks, registers, diagnostics, keymaps, command_history, search_history). Not in
// upstream SeekyVS: it cannot reach nvim.

namespace VSNeo.Seeky;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

/// <summary>
/// Rows for the page's list modes: where they come from, how a query filters them, and what
/// Enter does with the ones that are not files.
/// </summary>
/// <remarks>
/// Visual Studio sources (buffers, diagnostics) are read on the UI thread when the picker is
/// shown; nvim sources are one <c>vsneo.seeky_source</c> round trip on the first search. Either
/// way the list is fixed for the show and filtered per keystroke.
/// </remarks>
internal static class SeekyLists
{
    /// <summary>Modes whose rows are files: Enter opens them. The rest are nvim actions.</summary>
    private static readonly HashSet<string> FileModes = new() { "buffers", "oldfiles", "diagnostics" };

    private static readonly HashSet<string> NvimModes =
        new() { "marks", "registers", "keymaps", "command_history", "search_history" };

    internal static bool IsListMode(string mode) => FileModes.Contains(mode) || NvimModes.Contains(mode);

    internal static bool IsNvimMode(string mode) => NvimModes.Contains(mode);

    /// <summary>One list row. <see cref="Name"/> is matched and highlighted, and is what a pick
    /// acts on; <see cref="Path"/> is workspace-relative or absolute.</summary>
    internal sealed record Row(
        string Name, string Kind, string Detail, string? Path = null, int Line = 0, int Col = 0,
        string? Preview = null);

    /// <summary>
    /// Open documents, most recently focused first (the recent-files order; documents it has
    /// never seen go last). The active one is marked, as Telescope's '%'. UI thread only.
    /// </summary>
    internal static List<Row> CaptureBuffers(Func<string, string> relative)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var rows = new List<(Row Row, int Rank)>();
        try
        {
            if (Package.GetGlobalService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte)
            {
                return new List<Row>();
            }

            // Capped: All() runs File.Exists on every entry, here on the UI thread. Open documents
            // beyond the hundred most recent files sort last.
            List<string> recent = RecentFiles.All(100);
            string? active = dte.ActiveDocument?.FullName;
            foreach (EnvDTE.Document document in dte.Documents)
            {
                string fullName = document.FullName;
                if (string.IsNullOrEmpty(fullName))
                {
                    continue;
                }

                int rank = recent.FindIndex(p => string.Equals(p, fullName, StringComparison.OrdinalIgnoreCase));
                string kind = string.Equals(fullName, active, StringComparison.OrdinalIgnoreCase) ? "current"
                    : document.Saved ? string.Empty : "modified";
                string path = relative(fullName);
                rows.Add((new Row(path, kind, string.Empty, path), rank < 0 ? int.MaxValue : rank));
            }
        }
        catch (Exception ex)
        {
            SeekyLog.Error("Listing the open documents failed", ex);
        }

        return rows.OrderBy(r => r.Rank).Select(r => r.Row).ToList();
    }

    /// <summary>The Error List's entries, errors first, in the list's own order. UI thread only.</summary>
    internal static List<Row> CaptureDiagnostics(Func<string, string> relative)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var rows = new List<(Row Row, int Severity)>();
        try
        {
            if (Package.GetGlobalService(typeof(EnvDTE.DTE)) is not EnvDTE80.DTE2 dte)
            {
                return new List<Row>();
            }

            EnvDTE80.ErrorItems items = dte.ToolWindows.ErrorList.ErrorItems;
            for (int i = 1; i <= items.Count; i++)
            {
                EnvDTE80.ErrorItem item = items.Item(i);
                if (string.IsNullOrEmpty(item.FileName))
                {
                    continue; // a build message with no place to jump to
                }

                (string kind, int severity) = item.ErrorLevel switch
                {
                    EnvDTE80.vsBuildErrorLevel.vsBuildErrorLevelHigh => ("error", 0),
                    EnvDTE80.vsBuildErrorLevel.vsBuildErrorLevelMedium => ("warning", 1),
                    _ => ("message", 2),
                };
                string path = relative(item.FileName);
                rows.Add((new Row(item.Description, kind, $"{path}:{item.Line}", path, item.Line, item.Column), severity));
            }
        }
        catch (Exception ex)
        {
            SeekyLog.Error("Reading the Error List failed", ex);
        }

        return rows.OrderBy(r => r.Severity).Select(r => r.Row).ToList();
    }

    /// <summary>Every recent file that still exists, inside the workspace or not.</summary>
    internal static List<Row> Oldfiles(Func<string, string> relative) =>
        RecentFiles.All(500)
            .Select(full => relative(full))
            .Select(path => new Row(path, string.Empty, string.Empty, path))
            .ToList();

    /// <summary>
    /// One <c>vsneo.seeky_source(mode)</c> round trip. Empty, with a reason, when nvim is not up.
    /// Any thread: the RPC client serializes its own writes.
    /// </summary>
    internal static async Task<List<Row>> NvimRowsAsync(string mode, Func<string, string> relative, Action<string> status)
    {
        ISeekyHost? host = SeekyHost.Current;
        if (host is not { NvimReady: true })
        {
            status("this picker needs nvim, which is not running");
            return new List<Row>();
        }

        object? result = await host.RequestAsync(
            "nvim_exec_lua", "return vsneo.seeky_source(...)", new object[] { mode });

        var rows = new List<Row>();
        if (result is not object[] list)
        {
            return rows;
        }

        string kind = mode switch
        {
            "marks" => "mark",
            "registers" => "register",
            _ => string.Empty,
        };
        foreach (object item in list)
        {
            if (item is not IDictionary<string, object?> row || Str(row, "name") is not string name)
            {
                continue;
            }

            string? path = Str(row, "path");
            int line = Int(row, "line");
            string text = Str(row, "text") ?? string.Empty;
            string? relativePath = string.IsNullOrEmpty(path) ? null : relative(path!.Replace('/', Path.DirectorySeparatorChar));

            // Marks show where they point; the line's text rides in the preview's highlight.
            string detail = mode == "marks" && relativePath is not null ? $"{relativePath}:{line}  {text}" : text;
            rows.Add(new Row(name, kind, detail, relativePath, line, Int(row, "col"), Str(row, "preview")));
        }

        return rows;
    }

    /// <summary>
    /// Page items for <paramref name="rows"/> under <paramref name="query"/>: all of them in
    /// order on an empty query, else fuzzy matches on the name (highlighted) ahead of matches on
    /// the detail. Best first; the page shows index 0 next to the prompt.
    /// </summary>
    internal static List<object> Filter(string mode, IReadOnlyList<Row> rows, string query, int max)
    {
        IEnumerable<(Row Row, int[][] Ranges)> picked;
        if (query.Length == 0)
        {
            picked = rows.Select(r => (r, Array.Empty<int[]>()));
        }
        else
        {
            var scored = new List<(Row Row, int Score, int Order, int[][] Ranges)>();
            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                if (FuzzyMatcher.TryMatch(query, row.Name, out int score, out (int Start, int End)[] ranges))
                {
                    scored.Add((row, score, i, ranges.Select(r => new[] { r.Start, r.End }).ToArray()));
                }
                else if (FuzzyMatcher.TryMatch(query, row.Detail, out int detailScore, out _))
                {
                    // Below every name match: the name is what the picker is about.
                    scored.Add((row, detailScore - 100_000, i, Array.Empty<int[]>()));
                }
            }

            picked = scored
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Order)
                .Select(s => (s.Row, s.Ranges));
        }

        bool pick = IsNvimMode(mode);
        return picked
            .Take(max)
            .Select(p => (object)new
            {
                name = p.Row.Name,
                kind = p.Row.Kind,
                detail = p.Row.Detail,
                nameRanges = p.Ranges,
                path = p.Row.Path,
                line = p.Row.Line > 0 ? p.Row.Line : (int?)null,
                col = p.Row.Col > 0 ? p.Row.Col : (int?)null,
                preview = p.Row.Preview,
                pick,
            })
            .ToList();
    }

    /// <summary>
    /// Enter on an nvim row, as keys typed into nvim, so it behaves exactly as typing them would:
    /// ` for a mark (Vim's ` semantics, and a mark in another file opens it in Visual Studio
    /// through the follow logic), "xp for a register, the keys of a mapping, the command or the
    /// search run again. Normal mode first. UI thread (a page message).
    /// </summary>
    internal static void Pick(string mode, string name)
    {
        ISeekyHost? host = SeekyHost.Current;
        if (host is not { NvimReady: true } || name.Length == 0)
        {
            return;
        }

        string? keys = mode switch
        {
            "marks" => "`" + EscapeKeys(name),
            "registers" => "\"" + EscapeKeys(name) + "p",
            "keymaps" => name, // already key notation (keytrans)
            "command_history" => ":" + EscapeKeys(name) + "<CR>",
            "search_history" => "/" + EscapeKeys(name) + "<CR>",
            _ => null,
        };
        if (keys is null)
        {
            return;
        }

        SeekyLog.Info($"Pick ({mode}): '{name}'");

        // The picker may have opened from insert or visual; neither a jump nor a typed
        // command means to carry that mode along.
        host.EnsureNormalMode();
        host.Input(keys);
    }

    // nvim_input reads key notation: a literal '<' must be <lt>.
    private static string EscapeKeys(string text) => text.Replace("<", "<lt>");

    private static string? Str(IDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out object? value) && value is not null ? AsString(value) : null;

    // msgpack strings arrive as string, or as raw bytes for a bin-typed value.
    private static string AsString(object value) =>
        value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : value.ToString() ?? string.Empty;

    private static int Int(IDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out object? value) || value is null)
        {
            return 0;
        }

        try
        {
            return Convert.ToInt32(value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }
}
