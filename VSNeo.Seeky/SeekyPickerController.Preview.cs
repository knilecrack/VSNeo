// Preview, open, quickfix and the Visual Studio command a row can run.

namespace VSNeo.Seeky;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

internal static partial class SeekyPickerController
{
    private static async Task HandlePreviewAsync(string path, int? line, bool isBinary, bool isDirectory)
    {
        try
        {
            // Document Outline and Current File preview the snapshot they searched, not the
            // file on disk: their line numbers came from the editor's text, unsaved edits
            // included. Every other mode's line numbers are fff's, from the file on disk.
            EditorSnapshot? editor = editorSnapshot;
            if (editor is not null && !isDirectory && lastSearchMode is "path" or "buffer"
                && string.Equals(path, RelativeToWorkspace(editor.DocumentPath), StringComparison.OrdinalIgnoreCase))
            {
                PostJson(new { type = "preview", path, content = editor.PreviewText(), line });
                return;
            }

            string? workspace = workspaceDir;
            if (workspace is null)
            {
                return;
            }

            // Directories: list their entries (dirs first, then files) instead of file content.
            if (isDirectory)
            {
                string absoluteDir = Path.Combine(workspace, path);
                if (!Directory.Exists(absoluteDir))
                {
                    return;
                }

                var listing = Directory.GetDirectories(absoluteDir).Select(d => Path.GetFileName(d) + "/")
                    .Concat(Directory.GetFiles(absoluteDir).Select(Path.GetFileName)!)
                    .Take(300);
                PostJson(new { type = "preview", path, content = string.Join("\n", listing) });
                return;
            }

            // Never read binary files — the page shows a neutral note instead.
            if (isBinary)
            {
                PostJson(new { type = "preview", path, binary = true });
                return;
            }

            string absolutePath = Path.Combine(workspace, path);

            // Cap at ~200KB / 2000 lines — the preview is a glance, not an editor.
            const int maxBytes = 200 * 1024;
            const int maxLines = 2000;
            var buffer = new byte[maxBytes];
            int read;
            using (var stream = new FileStream(
                absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, maxBytes, FileOptions.SequentialScan))
            {
                read = await stream.ReadAsync(buffer, 0, maxBytes);
            }

            string text = Encoding.UTF8.GetString(buffer, 0, read);
            string[] lines = text.Split('\n');
            if (lines.Length > maxLines)
            {
                text = string.Join("\n", lines.Take(maxLines));
            }

            PostJson(new { type = "preview", path, content = text, line });
        }
        catch (Exception ex)
        {
            SeekyLog.Error($"Preview of '{path}' failed", ex);
            PostStatus("preview failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Opens a picked file at the match line and records the pick for frecency learning. UI
    /// thread only — document activation and the caret move are VS work; the extension's own
    /// focus/caret sync carries it from there.
    /// </summary>
    /// <param name="col">
    /// 1-based column from a "Foo.cs:42:9" Find Files query; wins over the remembered jump
    /// column. Null for every other row.
    /// </param>
    /// <param name="split">
    /// "vertical" (Ctrl+V) or "horizontal" (Ctrl+X), Telescope's open variants - the same
    /// commands as :vsp and :sp. Null opens in place.
    /// </param>
    private static void HandleOpen(string? path, int? line, int? col, string? split)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            // A rooted path (a Document Outline of a file outside the workspace) needs no root.
            if (path is null || (workspaceDir is null && !Path.IsPathRooted(path)))
            {
                return;
            }

            string absolutePath = workspaceDir is null ? path : Path.Combine(workspaceDir, path);
            SeekyLog.Info($"Open: '{absolutePath}' line {line} col {col} split {split}");

            // A pick lands in normal mode, as Telescope's does: the picker may have opened from
            // insert or visual, and carrying that into the new document is never what was meant.
            // Sent ahead of the open, so it reaches the buffer the mode belongs to.
            SeekyHost.Current?.EnsureNormalMode();

            // Frecency learning: record the pick (best-effort; never blocks the open). Only for fff's
            // own modes: the others' queries rank within one file or one list. fff
            // canonicalizes the path, so it must be absolute — a workspace-relative path
            // resolves against devenv's CWD and fails with os error 3.
            if (lastSearchMode is "files" or "mixed" or "grep" or "git" or "symbols")
            {
                string trackedQuery = lastSearchQuery;
                _ = Task.Run(() => TrackPickAsync(trackedQuery, absolutePath));
            }

            // The overload that hands back the frame and view: focus goes to the opened
            // document explicitly, since the picker's hide does not restore it anywhere.
            VsShellUtilities.OpenDocument(
                ServiceProvider.GlobalProvider, absolutePath, Guid.Empty,
                out _, out _, out IVsWindowFrame? frame, out IVsTextView? openedView);
            int showResult = frame?.Show() ?? VSConstants.S_OK;
            bool documentOpened = ErrorHandler.Succeeded(showResult);

            // fff line numbers are 1-based, and so are DTE's. With a known column the caret
            // lands on the match or the symbol name; without one on the first non-blank, as
            // Vim's :N does — column 1 in an indented block is nowhere anyone meant to go.
            // CursorSynchronizer carries the caret to nvim from here.
            // Its own try: a line past the end (the file shrank since the search) throws a
            // COMException, and that must not skip the focus hand-off below.
            try
            {
                if (line is int lineNumber && lineNumber > 0
                    && Package.GetGlobalService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte)
                {
                    EnvDTE.Document? active = dte.ActiveDocument;
                    bool correctDocument = documentOpened && active != null
                        && string.Equals(Path.GetFullPath(active.FullName), Path.GetFullPath(absolutePath),
                                         StringComparison.OrdinalIgnoreCase);
                    if (!correctDocument)
                    {
                        SeekyLog.Info($"Open: not moving the caret because '{absolutePath}' is not active");
                    }
                    else if (active != null && active.Selection is EnvDTE.TextSelection selection)
                    {
                        if (col is int queryCol && queryCol > 0)
                        {
                            selection.MoveToLineAndOffset(lineNumber, queryCol, false);
                        }
                        else if (jumpColumns.TryGetValue(JumpKey(path, lineNumber), out int column) && column >= 0)
                        {
                            // LineCharOffset: 1-based, one per UTF-16 char (a tab counts as one).
                            selection.MoveToLineAndOffset(lineNumber, column + 1, false);
                        }
                        else
                        {
                            selection.GotoLine(lineNumber, false);
                            selection.StartOfLine(EnvDTE.vsStartOfLineOptions.vsStartOfLineOptionsFirstText, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SeekyLog.Error($"Caret move to line {line} of '{path}' failed", ex);
            }

            IWpfTextView? focusView = openedView is null ? null : WpfViewOf(openedView);
            string? splitCommand = split switch
            {
                "vertical" => "Window.NewVerticalTabGroup",
                "horizontal" => "Window.Split",
                _ => null,
            };
            if (splitCommand is not null)
            {
                // After the caret move: both commands carry the active document's position
                // into the new pane, which then has focus - so nothing is refocused here.
                RunVsCommand(splitCommand);
                focusView = null;
            }

            FocusEditor(focusView);
        }
        catch (Exception ex)
        {
            SeekyLog.Error($"Open of '{path}' failed", ex);
            PostStatus("open failed: " + ex.Message);
        }
    }

    /// <summary>
    /// A DTE command, logged rather than thrown when it is unavailable - New Vertical Tab Group
    /// is disabled for a document that is already alone in its group.
    /// </summary>
    private static void RunVsCommand(string command)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (Package.GetGlobalService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte)
            {
                dte.ExecuteCommand(command, string.Empty);
            }
        }
        catch (Exception ex)
        {
            SeekyLog.Error($"{command} failed", ex);
        }
    }

    /// <summary>
    /// Exports rows into nvim's quickfix list: replace the current list, append to it, or push
    /// a new list onto the quickfix stack. Replace/new jumps to the first entry; append leaves
    /// the editor where it is. Columns come from <see cref="jumpColumns"/>: the page's rows carry
    /// fff's byte column, or none. UI thread (a page message).
    /// </summary>
    private static void HandleQuickfix(JsonElement message)
    {
        ISeekyHost? host = SeekyHost.Current;
        if (host is not { NvimReady: true })
        {
            PostStatus("quickfix needs nvim, which is not running");
            return;
        }

        if (!message.TryGetProperty("items", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var entries = new List<object>();
        foreach (JsonElement row in rows.EnumerateArray())
        {
            string? path = GetString(row, "path");
            int line = GetInt(row, "line") ?? 1;
            if (path is null || (workspaceDir is null && !Path.IsPathRooted(path)))
            {
                continue;
            }

            string key = JumpKey(path, line);
            int column = 1;
            if (jumpColumns.TryGetValue(key, out int c) && c >= 0)
            {
                // setqflist's col is a byte index; c counts UTF-16 chars. Without the line
                // text (outline rows) the char count stands, right for ASCII lines.
                column = jumpTexts.TryGetValue(key, out string? lineText) && c <= lineText.Length
                    ? Encoding.UTF8.GetByteCount(lineText.Substring(0, c)) + 1
                    : c + 1;
            }

            entries.Add(new Dictionary<string, object>
            {
                ["filename"] = workspaceDir is null ? path : Path.Combine(workspaceDir, path),
                ["lnum"] = line,
                ["col"] = column,
                ["text"] = GetString(row, "text") ?? string.Empty,
            });
        }

        string title = "Seeky: " + (GetString(message, "title") ?? string.Empty);
        string action = SeekyQuickfixAction.Parse(GetString(message, "action"));
        SeekyLog.Info($"Quickfix: {action} {entries.Count} entries ({title})");
        var what = new Dictionary<string, object> { ["items"] = entries };
        if (action != SeekyQuickfixAction.Append)
        {
            what["title"] = title;
            if (action == SeekyQuickfixAction.NewList)
            {
                what["nr"] = "$";
            }
        }

        _ = host.RequestAsync(
            "nvim_call_function",
            "setqflist",
            new object[]
            {
                Array.Empty<object>(),
                action == SeekyQuickfixAction.Append ? "a" : " ",
                what,
            });
        HidePopup(restoreEditorFocus: true);

        if (entries.Count > 0)
        {
            host.EnsureNormalMode();
            if (action != SeekyQuickfixAction.Append)
            {
                // Same RPC channel, so this runs after setqflist and VS follows the buffer switch.
                _ = host.RequestAsync("nvim_command", "cfirst");
            }
        }
    }
}
