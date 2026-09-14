using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using CreationPolicy = System.ComponentModel.Composition.CreationPolicy;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Marker for folds the user created with zf, so FoldSynchronizer can tell
    /// them apart from the language service's regions: zd on a user fold removes
    /// the region, zd on a language fold only expands it.
    /// </summary>
    internal sealed class UserFoldRegionTag : IOutliningRegionTag
    {
        public UserFoldRegionTag(object collapsedForm)
        {
            CollapsedForm = collapsedForm;
            CollapsedHintForm = collapsedForm;
        }

        public bool IsDefaultCollapsed => false;
        public bool IsImplementation => false;
        public object CollapsedForm { get; }
        public object CollapsedHintForm { get; }
    }

    /// <summary>
    /// The in-memory store behind user folds (zf). One list per ITextBuffer,
    /// held as tracking spans so folds ride buffer edits. Session-scoped by
    /// design: nvim's manual folds survive neither a buffer switch nor a
    /// reload, and these match - Visual Studio recreating the ITextBuffer for
    /// a reopened document drops them, which the next fold sync reconciles.
    /// </summary>
    [Export(typeof(UserFoldStore))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class UserFoldStore
    {
        /// <summary>Raised on the UI thread when any buffer's folds change.</summary>
        public event EventHandler<ITextBuffer>? Changed;

        /// <summary>UI thread. The span covers whole lines, start of first to end of last.</summary>
        public void AddFold(ITextBuffer buffer, int startLine, int endLine)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var snapshot = buffer.CurrentSnapshot;
            if (snapshot.LineCount == 0) return;

            if (startLine < 0) startLine = 0;
            if (endLine >= snapshot.LineCount) endLine = snapshot.LineCount - 1;
            if (endLine < startLine) endLine = startLine;

            var span = new SnapshotSpan(
                snapshot.GetLineFromLineNumber(startLine).Start,
                snapshot.GetLineFromLineNumber(endLine).End);

            FoldListOf(buffer).Add(snapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeInclusive));
            Changed?.Invoke(this, buffer);
        }

        /// <summary>UI thread. False when no user fold starts on that line.</summary>
        public bool RemoveFold(ITextBuffer buffer, int startLine)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var folds = FoldListOf(buffer);
            var snapshot = buffer.CurrentSnapshot;
            for (int i = 0; i < folds.Count; i++)
            {
                if (folds[i].GetStartPoint(snapshot).GetContainingLine().LineNumber != startLine)
                    continue;
                folds.RemoveAt(i);
                Changed?.Invoke(this, buffer);
                return true;
            }
            return false;
        }

        /// <summary>Any thread. Empty list when the buffer has no user folds.</summary>
        public IReadOnlyList<ITrackingSpan> FoldsOf(ITextBuffer buffer) =>
            buffer.Properties.TryGetProperty(typeof(UserFoldStore), out List<ITrackingSpan> folds)
                ? folds
                : Array.Empty<ITrackingSpan>();

        private static List<ITrackingSpan> FoldListOf(ITextBuffer buffer) =>
            buffer.Properties.GetOrCreateSingletonProperty(
                typeof(UserFoldStore), () => new List<ITrackingSpan>());
    }

    [Export(typeof(ITaggerProvider))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [TagType(typeof(IOutliningRegionTag))]
    internal sealed class UserFoldTaggerProvider : ITaggerProvider
    {
        [Import]
        internal UserFoldStore Store { get; set; } = null!;

        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag =>
            // A hard cast, not 'as': the provider is only ever queried for
            // IOutliningRegionTag, and a silent null would cost the folds.
            (ITagger<T>)(object)buffer.Properties.GetOrCreateSingletonProperty(
                typeof(UserFoldTagger),
                () => new UserFoldTagger(buffer, Store));
    }

    internal sealed class UserFoldTagger : ITagger<IOutliningRegionTag>, IDisposable
    {
        private readonly ITextBuffer _buffer;
        private readonly UserFoldStore _store;

        public UserFoldTagger(ITextBuffer buffer, UserFoldStore store)
        {
            _buffer = buffer;
            _store = store;
            _store.Changed += OnStoreChanged;
        }

        public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

        private void OnStoreChanged(object sender, ITextBuffer buffer)
        {
            if (!ReferenceEquals(buffer, _buffer)) return;

            // User folds are few; re-tagging the whole snapshot beats diffing.
            var snapshot = _buffer.CurrentSnapshot;
            TagsChanged?.Invoke(this,
                new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }

        public IEnumerable<ITagSpan<IOutliningRegionTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            var folds = _store.FoldsOf(_buffer);
            if (folds.Count == 0 || spans.Count == 0) yield break;

            var snapshot = spans[0].Snapshot;
            foreach (var fold in folds)
            {
                SnapshotSpan span;
                try { span = fold.GetSpan(snapshot); }
                catch { continue; }   // the tracking span outlived this snapshot

                foreach (var asked in spans)
                {
                    if (!span.IntersectsWith(asked)) continue;

                    // What the collapsed region shows in the "..." box: the
                    // fold's first line, like a language fold's header.
                    string firstLine = span.Start.GetContainingLine().GetText().Trim();
                    yield return new TagSpan<IOutliningRegionTag>(
                        span, new UserFoldRegionTag(firstLine.Length > 0 ? firstLine : "..."));
                    break;
                }
            }
        }

        public void Dispose() => _store.Changed -= OnStoreChanged;
    }
}
