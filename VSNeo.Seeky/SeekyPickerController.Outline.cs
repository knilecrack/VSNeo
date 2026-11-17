// Outline and symbol rows, and where each row should put the caret.

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
    /// Document-outline rows for "path" mode. Empty query: the whole file, file-bottom-first —
    /// the page renders index 0 as the bottom row next to the prompt, so the file reads
    /// top-to-bottom on screen. Non-empty query: fuzzy name filter, best match at index 0.
    /// </summary>
    private static List<object> OutlineItems(
        IReadOnlyList<SymbolOutline.Entry> outline, string displayPath, string query, int maxResults)
    {
        if (query.Length == 0)
        {
            return outline
                .Reverse()
                .Select(e => OutlineItem(e, displayPath, Array.Empty<int[]>()))
                .ToList();
        }

        var scored = new List<(SymbolOutline.Entry Entry, int Score, (int Start, int End)[] Ranges)>();
        foreach (SymbolOutline.Entry entry in outline)
        {
            if (FuzzyMatcher.TryMatch(query, entry.Name, out int score, out (int Start, int End)[] ranges))
            {
                scored.Add((entry, score, ranges));
            }
        }

        scored.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Entry.Line.CompareTo(b.Entry.Line);
        });

        return scored
            .Take(maxResults)
            .Select(h => OutlineItem(h.Entry, displayPath, h.Ranges.Select(r => new[] { r.Start, r.End }).ToArray()))
            .ToList();
    }

    private static object OutlineItem(SymbolOutline.Entry entry, string displayPath, int[][] nameRanges) =>
        new
        {
            name = entry.Name,
            path = displayPath,
            line = entry.Line,
            depth = entry.Indent,
            kind = entry.Kind,
            nameRanges,
            ranges = Array.Empty<int[]>(),
            isDefinition = true,
        };

    /// <summary>
    /// The outline position of <paramref name="needle"/>, matched on declaration line (unique
    /// per outline), or -1.
    /// </summary>
    private static int IndexOfEntry(IReadOnlyList<SymbolOutline.Entry> outline, SymbolOutline.Entry needle)
    {
        for (int i = 0; i < outline.Count; i++)
        {
            if (outline[i].Line == needle.Line)
            {
                return i;
            }
        }

        return -1;
    }

    private static string JumpKey(string path, int line) => path + "\n" + line;
}
