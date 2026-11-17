using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using VSNeo_Extension.Infrastructure;

namespace VSNeo_Extension.Editor;

// Collapsed-region row arithmetic, and whether nvim can show a topline at all.
internal sealed partial class ViewportSynchronizer
{
    /// <summary>
    /// The collapsed regions touching lines [first, last], as screen-row
    /// arithmetic (see FoldRows). UI thread. Empty without an outlining
    /// manager, or when nothing is collapsed.
    /// </summary>
    private FoldRows FoldsIn(IWpfTextView view, int first, int last)
    {
        var outlining = _outlining;
        if (outlining == null)
        {
            return FoldRows.None;
        }

        var snapshot = view.TextSnapshot;
        if (snapshot.LineCount == 0)
        {
            return FoldRows.None;
        }

        first = Math.Max(0, first);
        last = Math.Min(snapshot.LineCount - 1, last);
        if (last < first)
        {
            return FoldRows.None;
        }

        try
        {
            var span = new SnapshotSpan(
                snapshot.GetLineFromLineNumber(first).Start,
                snapshot.GetLineFromLineNumber(last).EndIncludingLineBreak);

            List<KeyValuePair<int, int>>? regions = null;
            foreach (var region in outlining.GetCollapsedRegions(span))
            {
                var extent = region.Extent.GetSpan(snapshot);
                int start = extent.Start.GetContainingLine().LineNumber;
                var endLine = extent.End.GetContainingLine();
                int end = endLine.LineNumber;
                // An extent ending at the very start of a line hides
                // nothing of that line.
                if (end > start && extent.End.Position == endLine.Start.Position)
                {
                    end--;
                }

                regions ??= new List<KeyValuePair<int, int>>();
                regions.Add(new KeyValuePair<int, int>(start, end));
            }

            return regions == null ? FoldRows.None : FoldRows.FromRegions(regions);
        }
        catch (Exception ex)
        {
            // Layout arithmetic only; the line-count behavior is the fallback.
            Log.Write("fold row count failed", ex);
            return FoldRows.None;
        }
    }

    /// <summary>
    /// Whether nvim can show <paramref name="top"/> as its topline: nvim
    /// clamps any topline that would leave rows of its window below the
    /// end of the file. Counted in screen rows from the topline to the last
    /// line, so a collapsed region near the end counts as one row, as it
    /// does in nvim. UI thread.
    /// </summary>
    private bool IsPastNvimEnd(IWpfTextView view, int top, int height)
    {
        int lastLine = view.TextSnapshot.LineCount - 1;
        if (lastLine - top + 1 < height)
        {
            return true;   // short of rows even unfolded
        }

        // Far from the end the answer is plainly "no"; the bound keeps the
        // region query off the capture path for most of a large file.
        if (lastLine - top > height * 50)
        {
            return false;
        }

        int rows = FoldsIn(view, top, lastLine).RowsBetween(top, lastLine) + 1;
        return rows < height;
    }
}
