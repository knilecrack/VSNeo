// The workspace, the active document and the editor snapshot captured on show.

namespace VSNeo.Seeky;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

internal static partial class SeekyPickerController
{
    /// <summary>
    /// Re-resolves the workspace directory on every popup show (the user may have opened a
    /// different solution or folder since the last one) and snapshots the active document for
    /// current_file deprioritization. UI thread only — DTE is apartment-bound, which is exactly
    /// why background searches receive these values instead of asking for them.
    /// </summary>
    private static void RefreshWorkspace()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string? resolved = ResolveWorkspaceDir();
        SeekyLog.Info($"Workspace: resolved '{resolved ?? "(none)"}' (was '{workspaceDir ?? "(none)"}')");
        if (!string.Equals(resolved, workspaceDir, StringComparison.OrdinalIgnoreCase))
        {
            // The engine keys its symbol index by workspace, so a new root never serves the
            // old set.
            workspaceDir = resolved;
        }

        activeDocumentRelative = ResolveActiveDocumentRelative();
    }

    /// <summary>
    /// Snapshots the active editor for Document Outline: its document path, caret line, and
    /// text. Classification happens later, on the thread pool — a snapshot is immutable, so it
    /// can leave the UI thread. Null when no text editor is active; the picker then opens with
    /// a status line rather than silently not at all.
    /// </summary>
    private static EditorSnapshot? CaptureEditorSnapshot(IWpfTextView? view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (view is null)
            {
                return null;
            }

            ITextSnapshot snapshot = view.TextBuffer.CurrentSnapshot;
            string documentPath = view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)
                ? document.FilePath
                : string.Empty;

            // Mapped down to the document buffer: a projection view (Razor, say) reports its
            // caret in the view's buffer, which is not the one being outlined.
            SnapshotPoint? caret = view.Caret.Position.Point.GetPoint(view.TextBuffer, PositionAffinity.Successor);
            int caretLine = caret.HasValue ? caret.Value.GetContainingLine().LineNumber + 1 : 1;
            return new EditorSnapshot(documentPath, caretLine, snapshot);
        }
        catch (Exception ex)
        {
            SeekyLog.Error("Capturing the document outline failed", ex);
            return null;
        }
    }

    /// <summary>
    /// The active editor's search term for Grep Word Under Cursor: the selection's first line
    /// if there is a selection (a Vim visual selection is Visual Studio's selection too), else
    /// the identifier at the caret. Null when there is no editor or no term; the picker then
    /// opens with an empty prompt. UI thread only.
    /// </summary>
    internal static string? EditorSearchTerm()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        IWpfTextView? view = ActiveWpfTextView();
        if (view is null)
        {
            return null;
        }

        // A grep query is one line; a stray whole-file selection should not push a file
        // through fff.
        const int MaxLength = 200;
        string term;
        if (!view.Selection.IsEmpty)
        {
            term = view.Selection.StreamSelectionSpan.GetText();
            int lineBreak = term.IndexOfAny(['\r', '\n']);
            if (lineBreak >= 0)
            {
                term = term.Substring(0, lineBreak);
            }
        }
        else
        {
            // Expands both ways, so a caret just past a word's end (where Vim's block cursor
            // sits on the last letter's right edge) still picks the word up.
            ITextSnapshotLine line = view.Caret.Position.BufferPosition.GetContainingLine();
            string text = line.GetText();
            int column = view.Caret.Position.BufferPosition.Position - line.Start.Position;
            int start = column;
            while (start > 0 && IsWordChar(text[start - 1]))
            {
                start--;
            }

            int end = column;
            while (end < text.Length && IsWordChar(text[end]))
            {
                end++;
            }

            term = text.Substring(start, end - start);
        }

        term = term.Trim();
        if (term.Length > MaxLength)
        {
            term = term.Substring(0, MaxLength);
        }

        return term.Length == 0 ? null : term;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The active editor's WPF view, or null when no text editor is active. UI thread only.</summary>
    private static IWpfTextView? ActiveWpfTextView()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (!(Package.GetGlobalService(typeof(SVsTextManager)) is IVsTextManager textManager)
                || ErrorHandler.Failed(textManager.GetActiveView(1, null, out IVsTextView vsView))
                || vsView is null)
            {
                return null;
            }

            return WpfViewOf(vsView);
        }
        catch (Exception ex)
        {
            SeekyLog.Error("Active view query failed", ex);
            return null;
        }
    }

    private static IWpfTextView? WpfViewOf(IVsTextView vsView) =>
        Package.GetGlobalService(typeof(SComponentModel)) is IComponentModel model
            ? model.GetService<IVsEditorAdaptersFactoryService>()?.GetWpfTextView(vsView)
            : null;

    /// <summary>
    /// The open solution's directory; falls back to the active document's directory; then null.
    /// UI thread only.
    /// </summary>
    private static string? ResolveWorkspaceDir()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (Package.GetGlobalService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte)
            {
                string? solutionFile = dte.Solution?.FullName;
                if (solutionFile is not null && solutionFile.Length != 0)
                {
                    string? solutionDir = Path.GetDirectoryName(solutionFile);
                    if (!string.IsNullOrEmpty(solutionDir))
                    {
                        return solutionDir;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SeekyLog.Error("Solution query failed; trying active document", ex);
        }

        try
        {
            if (Package.GetGlobalService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte)
            {
                string? documentPath = dte.ActiveDocument?.FullName;
                if (documentPath is not null && documentPath.Length != 0)
                {
                    return Path.GetDirectoryName(documentPath);
                }
            }
        }
        catch (Exception ex)
        {
            SeekyLog.Error("Active document query failed", ex);
        }

        return null;
    }

    /// <summary>
    /// The VS active document relative to the workspace, or null when there is no document, no
    /// workspace, or the file sits outside the root. UI thread only.
    /// </summary>
    private static string? ResolveActiveDocumentRelative()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (workspaceDir is null
                || !(Package.GetGlobalService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte))
            {
                return null;
            }

            string? fullPath = dte.ActiveDocument?.FullName;
            if (fullPath is null || fullPath.Length == 0)
            {
                return null;
            }

            // RelativeToWorkspace passes an outside-the-root path through unchanged.
            string relative = RelativeToWorkspace(fullPath);
            return string.Equals(relative, fullPath, StringComparison.OrdinalIgnoreCase) ? null : relative;
        }
        catch (Exception ex)
        {
            SeekyLog.Error("Active document path query failed", ex);
            return null;
        }
    }

    /// <summary>
    /// <paramref name="fullPath"/> relative to the workspace ('/' separators), or the full path
    /// unchanged when there is no workspace or the file sits outside it — Path.Combine in the
    /// preview/open handlers passes rooted paths through, so an absolute path still works.
    /// </summary>
    private static string RelativeToWorkspace(string fullPath)
    {
        if (workspaceDir is null || fullPath.Length == 0)
        {
            return fullPath;
        }

        string prefix = workspaceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? fullPath.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/')
            : fullPath;
    }

    // ------------------------------------------------------------------ fff backend (thread pool)
}
