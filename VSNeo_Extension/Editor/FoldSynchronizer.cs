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

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Mirrors Visual Studio's outlining regions into nvim as manual folds, and
    /// the closed/open state in both directions.
    ///
    /// The invariant: nvim's manual fold set is Visual Studio's outlining region
    /// set, with matching closed state. Region boundaries always come from Visual
    /// Studio (they are the language service's); either side can flip the closed
    /// state. Visual Studio -&gt; nvim is event-driven (RegionsCollapsed /
    /// RegionsExpanded, plus a full push when a document is shown). nvim -&gt;
    /// Visual Studio is poll-driven: the companion has no fold-changed event, so
    /// it diffs actual fold state against the last agreed state on every state
    /// push and reports the actual closed set, which is reconciled here. Each
    /// side updates its agreed copy when applying a change, so the answering
    /// event from the other side compares equal and no-ops.
    ///
    /// With real folds, counts treat a closed fold as one line, zj/zk/[z/]z work
    /// natively, and 'foldopen' auto-opens (search, %, marks) flow back into
    /// Visual Studio's outlining. zf creates a real outlining region through
    /// UserFoldTagger rather than an nvim-only fold; zd removes user folds and
    /// merely expands language folds. Nothing here runs on the key path.
    /// </summary>
    [Export(typeof(FoldSynchronizer))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class FoldSynchronizer
    {
        // Null only when MEF could not satisfy the imports; fold mirroring then
        // silently degrades to the SnapOutOfCollapsedRegion-only behavior.
        [Import]
        private IOutliningManagerService? _outliningManagerService = null;

        [Import]
        private ITextDocumentFactoryService? _documentFactory = null;

        [Import]
        private UserFoldStore? _userFolds = null;

        private IWpfTextView? _view;            // UI thread only
        private IOutliningManager? _outlining;  // UI thread only
        private Dispatcher? _dispatcher;
        private NvimStateHub? _subscribedTo;

        // Set while Visual Studio's outlining is being changed to match nvim's
        // report; the RegionsCollapsed/Expanded those changes raise are not
        // news and must not be sent back.
        private bool _applyingNvimChanges;      // UI thread only

        public void SetActiveView(IWpfTextView? view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (ReferenceEquals(_view, view)) return;

            if (_outlining != null)
            {
                _outlining.RegionsCollapsed -= OnRegionsCollapsed;
                _outlining.RegionsExpanded -= OnRegionsExpanded;
                _outlining = null;
            }
            _view = view;
            if (view == null) return;

            _dispatcher = view.VisualElement.Dispatcher;

            var session = VSNeo_ExtensionPackage.Session;
            if (session != null && !ReferenceEquals(_subscribedTo, session.State))
            {
                if (_subscribedTo != null)
                {
                    _subscribedTo.FoldsChanged -= OnNvimFoldsChanged;
                    _subscribedTo.FoldCreateRequested -= OnFoldCreateRequested;
                }
                session.State.FoldsChanged += OnNvimFoldsChanged;
                session.State.FoldCreateRequested += OnFoldCreateRequested;
                _subscribedTo = session.State;
            }

            if (_outliningManagerService != null)
            {
                _outlining = _outliningManagerService.GetOutliningManager(view);
                _outlining.RegionsCollapsed += OnRegionsCollapsed;
                _outlining.RegionsExpanded += OnRegionsExpanded;
            }

            view.Closed += (s, e) => { if (ReferenceEquals(_view, view)) SetActiveView(null); };
        }

        /// <summary>
        /// Full region push, called once nvim's window actually shows this
        /// document. Manual folds are window-local in nvim and do not survive a
        /// buffer switch, so this runs on every document switch, not just the
        /// first attach. UI thread.
        /// </summary>
        public void SyncNow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var view = _view;
            var outlining = _outlining;
            var session = VSNeo_ExtensionPackage.Session;
            if (view == null || view.IsClosed || outlining == null) return;
            if (session == null || !session.IsReady) return;

            var path = PathOf(view);
            if (path == null) return;

            var snapshot = view.TextSnapshot;
            var flat = new List<object>();
            foreach (var region in outlining.GetAllRegions(
                         new SnapshotSpan(snapshot, 0, snapshot.Length)))
            {
                if (!TryGetFoldLines(region, snapshot, out int start, out int end)) continue;
                flat.Add(start);
                flat.Add(end);
                flat.Add(region.IsCollapsed);
            }

            Send(session, "vsneo.folds_set(...)", path, flat.ToArray());
        }

        private void OnRegionsCollapsed(object sender, RegionsCollapsedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_applyingNvimChanges) return;

            var view = _view;
            var session = VSNeo_ExtensionPackage.Session;
            if (view == null || view.IsClosed || session == null || !session.IsReady) return;

            var path = PathOf(view);
            if (path == null) return;

            var snapshot = view.TextSnapshot;
            foreach (var region in e.CollapsedRegions)
            {
                if (!TryGetFoldLines(region, snapshot, out int start, out int end)) continue;
                Send(session, "vsneo.fold_closed(...)", path, start, end);
            }
        }

        private void OnRegionsExpanded(object sender, RegionsExpandedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_applyingNvimChanges) return;

            var view = _view;
            var session = VSNeo_ExtensionPackage.Session;
            if (view == null || view.IsClosed || session == null || !session.IsReady) return;

            var path = PathOf(view);
            if (path == null) return;

            // The regions are going away entirely (outlining stopped), not just
            // opening - incremental opens would leave nvim folds behind.
            if (e.RemovalPending)
            {
                SyncNow();
                return;
            }

            var snapshot = view.TextSnapshot;
            foreach (var region in e.ExpandedRegions)
            {
                if (!TryGetFoldLines(region, snapshot, out int start, out _)) continue;
                Send(session, "vsneo.fold_opened(...)", path, start);
            }
        }

        /// <summary>
        /// nvim's actual fold set changed (a z-command, or a 'foldopen'
        /// auto-open): flat [start, end, closed] triples - 1-based lines, closed
        /// as 0/1 - covering every fold that still exists there. Called on the
        /// RPC read thread; fold changes are rare enough that no coalescing is
        /// needed, but the outlining work itself needs the UI thread.
        /// </summary>
        private void OnNvimFoldsChanged(int[] foldTriples)
        {
            var dispatcher = _dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            // Input priority, same reasoning as the scroll apply: this answers a
            // keystroke (zo/zc/zM), and an unjoined SwitchToMainThreadAsync queues
            // behind Visual Studio's background work.
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    ApplyNvimFolds(foldTriples);
                }));
#pragma warning restore VSTHRD001
        }

        /// <summary>zf in nvim. RPC read thread; the region work is UI thread.</summary>
        private void OnFoldCreateRequested(string path, int start, int end)
        {
            var dispatcher = _dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            // Input priority: this answers the zf keystroke directly.
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    CreateUserFold(path, start, end);
                }));
#pragma warning restore VSTHRD001
        }

        /// <summary>
        /// zf never creates an nvim-only fold: the range becomes a real
        /// outlining region through UserFoldTagger, and the RegionsCollapsed
        /// event it raises round-trips back into nvim as the manual fold.
        /// </summary>
        private void CreateUserFold(string path, int start, int end)
        {
            var view = _view;
            var outlining = _outlining;
            if (view == null || view.IsClosed || outlining == null || _userFolds == null) return;

            // zf belongs to the document on screen; anything else is a
            // mid-switch straggler and self-heals on the next sync.
            var expected = PathOf(view);
            if (expected == null
                || !string.Equals(NormalizePath(path), NormalizePath(expected),
                                  StringComparison.OrdinalIgnoreCase))
                return;

            _userFolds.AddFold(view.TextBuffer, start - 1, end - 1);

            // The region materializes when the tagger's TagsChanged is
            // processed, which is not synchronous in every host; retry once at
            // Background priority before giving up.
            if (CollapseUserFold(view, outlining, start)) return;

#pragma warning disable VSTHRD001
            _ = view.VisualElement.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (_outlining != null && _view != null
                        && !CollapseUserFold(_view, _outlining, start))
                        Infrastructure.Log.Write("user fold region never materialized at line " + start);
                }));
#pragma warning restore VSTHRD001
        }

        private static bool CollapseUserFold(IWpfTextView view, IOutliningManager outlining, int start)
        {
            var snapshot = view.TextSnapshot;
            if (start < 1 || start > snapshot.LineCount) return false;

            var line = snapshot.GetLineFromLineNumber(start - 1);
            foreach (var region in outlining.GetAllRegions(line.Extent))
            {
                if (!(region.Tag is UserFoldRegionTag)) continue;
                if (!TryGetFoldLines(region, snapshot, out int regionStart, out _)) continue;
                if (regionStart != start) continue;

                if (!region.IsCollapsed) outlining.TryCollapse(region);
                return true;
            }
            return false;
        }

        private void ApplyNvimFolds(int[] foldTriples)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var view = _view;
            var outlining = _outlining;
            if (view == null || view.IsClosed || outlining == null) return;

            var snapshot = view.TextSnapshot;

            // nvim's folds by start line - the same line Visual Studio was told
            // the fold starts on, so the report keys directly into the regions.
            var nvimFolds = new Dictionary<int, bool>();
            for (int i = 0; i + 2 < foldTriples.Length; i += 3)
                nvimFolds[foldTriples[i]] = foldTriples[i + 2] != 0;

            _applyingNvimChanges = true;
            try
            {
                foreach (var region in outlining.GetAllRegions(
                             new SnapshotSpan(snapshot, 0, snapshot.Length)))
                {
                    if (!TryGetFoldLines(region, snapshot, out int start, out _)) continue;

                    if (nvimFolds.TryGetValue(start, out bool closed))
                    {
                        if (closed && !region.IsCollapsed)
                            outlining.TryCollapse(region);
                        else if (!closed && region is ICollapsed collapsedRegion)
                            outlining.Expand(collapsedRegion);
                        continue;
                    }

                    // Missing from nvim's list: the fold was deleted there (zd).
                    // The semantics split by origin - a user fold's region is
                    // removed; a language fold only expands and stays
                    // collapsible, so Visual Studio's outlining margin keeps it.
                    if (region.Tag is UserFoldRegionTag)
                        _userFolds?.RemoveFold(view.TextBuffer, start - 1);
                    else if (region is ICollapsed collapsed)
                        outlining.Expand(collapsed);
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
            try { extent = region.Extent.GetSpan(snapshot); }
            catch { return false; }   // the extent outlived this snapshot; the next event resends

            int startLine = extent.Start.GetContainingLine().LineNumber;
            int endLine = extent.End.GetContainingLine().LineNumber;

            // An extent ending exactly at a line break hides nothing of that
            // line; nvim's line-granular fold must stop one line earlier.
            if (endLine > startLine && extent.End == extent.End.GetContainingLine().Start)
                endLine--;

            if (endLine < startLine) return false;
            start = startLine + 1;
            end = endLine + 1;
            return true;
        }

        private static string NormalizePath(string path)
        {
            try { return System.IO.Path.GetFullPath(path); }
            catch { return path; }   // keep raw; the equality check just fails
        }

        private string? PathOf(IWpfTextView view) =>
            _documentFactory != null
            && _documentFactory.TryGetTextDocument(view.TextBuffer, out var document)
                ? document.FilePath
                : null;

        private static void Send(NvimSession session, string lua, params object[] args) =>
            Observe(session.RequestAsync("nvim_exec_lua", lua, args));

        /// <summary>
        /// Fold RPCs can reference a buffer nvim has not been sent yet; they
        /// self-correct on the next sync. What must not happen is an unobserved
        /// task fault.
        /// </summary>
        private static void Observe(System.Threading.Tasks.Task task) =>
            _ = task.ContinueWith(
                t => { _ = t.Exception; },
                System.Threading.CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted,
                System.Threading.Tasks.TaskScheduler.Default);
    }
}
