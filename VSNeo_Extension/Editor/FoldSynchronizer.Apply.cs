using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using VSNeo_Extension.Nvim;
using CreationPolicy = System.ComponentModel.Composition.CreationPolicy;

namespace VSNeo_Extension.Editor;

// nvim's fold list onto Visual Studio's outlining manager.
internal sealed partial class FoldSynchronizer
{
    private void ApplyNvimFolds(string path, int[] foldTriples)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var view = _view;
        var outlining = _outlining;
        if (view == null || view.IsClosed || outlining == null)
        {
            return;
        }

        // The report was captured for nvim's then-current buffer. A queued
        // UI callback can run after a switch, when it no longer describes
        // this view's regions.
        var expected = PathOf(view);
        if (expected == null
            || !string.Equals(NormalizePath(path), NormalizePath(expected),
                              StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var snapshot = view.TextSnapshot;

        // nvim's folds by start line - the same line Visual Studio was told
        // the fold starts on, so the report keys directly into the regions.
        var nvimFolds = new Dictionary<int, bool>();
        for (int i = 0; i + 2 < foldTriples.Length; i += 3)
        {
            nvimFolds[foldTriples[i]] = foldTriples[i + 2] != 0;
        }

        _applyingNvimChanges = true;
        try
        {
            foreach (var region in outlining.GetAllRegions(
                         new SnapshotSpan(snapshot, 0, snapshot.Length)))
            {
                if (!TryGetFoldLines(region, snapshot, out int start, out _))
                {
                    continue;
                }

                if (nvimFolds.TryGetValue(start, out bool closed))
                {
                    if (closed && !region.IsCollapsed)
                    {
                        outlining.TryCollapse(region);
                    }
                    else if (!closed && region is ICollapsed collapsedRegion)
                    {
                        outlining.Expand(collapsedRegion);
                    }

                    continue;
                }

                // Missing from nvim's list: the fold was deleted there (zd).
                // The semantics split by origin - a user fold's region is
                // removed; a language fold only expands and stays
                // collapsible, so Visual Studio's outlining margin keeps it.
                if (region.Tag is UserFoldRegionTag)
                {
                    _userFolds?.RemoveFold(view.TextBuffer, start - 1);
                }
                else if (region is ICollapsed collapsed)
                {
                    outlining.Expand(collapsed);
                }
            }
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Write("could not apply nvim's fold state", ex);
        }
        finally
        {
            _applyingNvimChanges = false;
        }
    }

    /// <summary>
    /// The 1-based line range nvim's fold spans for a region. nvim folds are
    /// line-granular while Visual Studio extents can start mid-header-line;
    /// both sides still agree that the fold's first line is the header the
    /// cursor rests on. Used for both directions, so the keys always match.
    /// </summary>
    private static bool TryGetFoldLines(
        ICollapsible region, ITextSnapshot snapshot, out int start, out int end)
    {
        start = end = 0;

        SnapshotSpan extent;
        try
        { extent = region.Extent.GetSpan(snapshot); }
        catch { return false; }   // the extent outlived this snapshot; the next event resends

        int startLine = extent.Start.GetContainingLine().LineNumber;
        int endLine = extent.End.GetContainingLine().LineNumber;

        // An extent ending exactly at a line break hides nothing of that
        // line; nvim's line-granular fold must stop one line earlier.
        if (endLine > startLine && extent.End == extent.End.GetContainingLine().Start)
        {
            endLine--;
        }

        if (endLine < startLine)
        {
            return false;
        }

        start = startLine + 1;
        end = endLine + 1;
        return true;
    }
}
