// The snapshot a Document Outline or Current File show reads: text as Visual Studio has
// it, unsaved edits included.


using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.Text;

namespace VSNeo.Seeky;
/// <summary>
/// The editor behind a Document Outline or Current File show: the document, the 1-based caret
/// line, and the snapshot both search. Split and classified once, lazily, on the first search
/// that asks — that runs on the thread pool, and a snapshot is safe to read there.
/// </summary>
internal sealed class EditorSnapshot
{
    // Preview cap, as for files read from disk: the preview is a glance, not an editor.
    private const int MaxPreviewLines = 2000;

    private readonly ITextSnapshot snapshot;
    private readonly Lazy<IReadOnlyList<string>> lines;
    private readonly Lazy<IReadOnlyList<SymbolOutline.Entry>> outline;

    internal EditorSnapshot(string documentPath, int caretLine, ITextSnapshot snapshot)
    {
        DocumentPath = documentPath;
        CaretLine = caretLine;
        this.snapshot = snapshot;
        lines = new Lazy<IReadOnlyList<string>>(ReadLines, LazyThreadSafetyMode.ExecutionAndPublication);
        outline = new Lazy<IReadOnlyList<SymbolOutline.Entry>>(
            () => SymbolOutline.ClassifyAll(documentPath, Lines),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal string DocumentPath { get; }

    internal int CaretLine { get; }

    /// <summary>The snapshot's lines, split once and shared by every search of this show.</summary>
    internal IReadOnlyList<string> Lines => lines.Value;

    internal IReadOnlyList<SymbolOutline.Entry> Outline => outline.Value;

    /// <summary>
    /// The UTF-16 column of the declaration's name on its line, or -1 when it is not there.
    /// Matched as a whole identifier: a name can also be a substring of the modifiers ahead of
    /// it ("in" inside "internal").
    /// </summary>
    /// <summary>The declaration line's text from the captured snapshot, or null out of range.</summary>
    internal string? LineText(int line) =>
        line < 1 || line > snapshot.LineCount ? null : snapshot.GetLineFromLineNumber(line - 1).GetText();

    internal int NameColumn(SymbolOutline.Entry entry)
    {
        if (entry.Line < 1 || entry.Line > snapshot.LineCount)
        {
            return -1;
        }

        string text = snapshot.GetLineFromLineNumber(entry.Line - 1).GetText();
        int from = 0;
        while (from <= text.Length - entry.Name.Length)
        {
            int at = text.IndexOf(entry.Name, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return -1;
            }

            int end = at + entry.Name.Length;
            bool startsWord = at == 0 || !IsIdentifierChar(text[at - 1]);
            bool endsWord = end == text.Length || !IsIdentifierChar(text[end]);
            if (startsWord && endsWord)
            {
                return at;
            }

            from = at + 1;
        }

        return -1;
    }

    /// <summary>The snapshot's text for the preview pane, capped like a file read from disk.</summary>
    internal string PreviewText()
    {
        int lines = Math.Min(snapshot.LineCount, MaxPreviewLines);
        int end = snapshot.GetLineFromLineNumber(lines - 1).EndIncludingLineBreak.Position;
        // Line breaks as the file has them, as HandlePreviewAsync sends a file read from disk.
        return snapshot.GetText(0, end);
    }

    private IReadOnlyList<string> ReadLines()
    {
        var lines = new List<string>(snapshot.LineCount);
        foreach (ITextSnapshotLine line in snapshot.Lines)
        {
            lines.Add(line.GetText());
        }

        return lines;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
