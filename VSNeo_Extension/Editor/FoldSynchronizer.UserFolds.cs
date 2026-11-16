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

// A fold created in nvim (zf) as a real VS region, and reopening one we made.
internal sealed partial class FoldSynchronizer
{
    /// <summary>
    /// zf never creates an nvim-only fold: the range becomes a real
    /// outlining region through UserFoldTagger, and the RegionsCollapsed
    /// event it raises round-trips back into nvim as the manual fold.
    /// </summary>
    private void CreateUserFold(string path, int start, int end)
    {
        var view = _view;
        var outlining = _outlining;
        if (view == null || view.IsClosed || outlining == null || _userFolds == null)
        {
            return;
        }

        // zf belongs to the document on screen; anything else is a
        // mid-switch straggler and self-heals on the next sync.
        var expected = PathOf(view);
        if (expected == null
            || !string.Equals(NormalizePath(path), NormalizePath(expected),
                              StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _userFolds.AddFold(view.TextBuffer, start - 1, end - 1);

        // The region materializes when the tagger's TagsChanged is
        // processed, which is not synchronous in every host; retry once at
        // Background priority before giving up.
        if (CollapseUserFold(view, outlining, start))
        {
            return;
        }

#pragma warning disable VSTHRD001
        _ = view.VisualElement.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_outlining != null && _view != null
                    && !CollapseUserFold(_view, _outlining, start))
                {
                    Infrastructure.Log.Write("user fold region never materialized at line " + start);
                }
            }));
#pragma warning restore VSTHRD001
    }

    private static bool CollapseUserFold(IWpfTextView view, IOutliningManager outlining, int start)
    {
        var snapshot = view.TextSnapshot;
        if (start < 1 || start > snapshot.LineCount)
        {
            return false;
        }

        var line = snapshot.GetLineFromLineNumber(start - 1);
        foreach (var region in outlining.GetAllRegions(line.Extent))
        {
            if (!(region.Tag is UserFoldRegionTag))
            {
                continue;
            }

            if (!TryGetFoldLines(region, snapshot, out int regionStart, out _))
            {
                continue;
            }

            if (regionStart != start)
            {
                continue;
            }

            if (!region.IsCollapsed)
            {
                outlining.TryCollapse(region);
            }

            return true;
        }

        return false;
    }
}
