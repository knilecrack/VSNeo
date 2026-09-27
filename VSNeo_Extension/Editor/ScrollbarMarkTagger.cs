using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Search matches and nvim marks as ticks on Visual Studio's vertical
    /// scrollbar, next to its own error, change and caret marks - where the
    /// hits are in a long file, at a glance.
    ///
    /// Visual Studio draws an <see cref="OverviewMarkTag"/> as a tick on the
    /// scrollbar ("Show marks" in Tools &gt; Options &gt; Text Editor &gt; All
    /// Languages &gt; Scroll Bars), colored by the EditorFormatDefinition its
    /// name points at - so both colors are also in Fonts and Colors.
    ///
    /// Nothing here asks nvim anything. Search matches are the list the
    /// highlights already use (vsneo_search_matches, one tick per matching
    /// line), shown only for the document nvim is searching in; marks come
    /// from vsneo_marks, which the companion sends when a file's marks change.
    /// </summary>
    [Export(typeof(ITaggerProvider))]
    [ContentType("text")]
    [TagType(typeof(OverviewMarkTag))]
    internal sealed class ScrollbarMarkTaggerProvider : ITaggerProvider
    {
        public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            // Only documents: a file path is what matches the buffer to nvim's.
            if (!buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)
                || string.IsNullOrEmpty(document.FilePath))
                return null;

            // One tagger per buffer, shared by every tag aggregator that asks;
            // each ask takes a reference, and each aggregator disposes its own.
            return buffer.Properties.GetOrCreateSingletonProperty(
                () => new ScrollbarMarkTagger(buffer, document)).AddRef() as ITagger<T>;
        }
    }

    internal sealed class ScrollbarMarkTagger : ITagger<OverviewMarkTag>, IDisposable
    {
        internal const string SearchMarkName = "VSNeo Scrollbar Search Match";
        internal const string MarkName = "VSNeo Scrollbar Mark";

        private static readonly OverviewMarkTag SearchTag = new OverviewMarkTag(SearchMarkName);
        private static readonly OverviewMarkTag MarkTag = new OverviewMarkTag(MarkName);

        private readonly ITextBuffer _buffer;
        private readonly ITextDocument _document;

        // What the last GetTags built, reused while nothing it came from changed:
        // the scrollbar asks for the whole buffer on every repaint.
        private ITextSnapshot? _builtFor;
        private object? _builtMatches;
        private object? _builtMarks;
        private bool _builtCurrent;
        private List<ITagSpan<OverviewMarkTag>> _built = new List<ITagSpan<OverviewMarkTag>>();

        // Whether the last build drew search ticks: when the search moves to
        // another document, this one must clear its own.
        private volatile bool _hadSearch;
        private int _changePending;
        private int _readyHooked;
        private int _refs;
        private NvimStateHub? _subscribedTo;

        public ScrollbarMarkTagger(ITextBuffer buffer, ITextDocument document)
        {
            _buffer = buffer;
            _document = document;
            Subscribe();
        }

        public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

        internal ScrollbarMarkTagger AddRef()
        {
            Interlocked.Increment(ref _refs);
            return this;
        }

        /// <summary>
        /// A tag aggregator let go (its view closed). The last one unhooks from
        /// the hub - which would otherwise keep a closed document's tagger alive
        /// for the whole session - and drops it from the buffer, so a reopened
        /// document starts fresh.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Decrement(ref _refs) > 0) return;

            if (_subscribedTo != null)
            {
                _subscribedTo.SearchMatchesChanged -= OnSearchMatchesChanged;
                _subscribedTo.BufferSwitched -= OnBufferSwitched;
                _subscribedTo.MarksChanged -= OnMarksChanged;
                _subscribedTo = null;
            }
            if (Interlocked.Exchange(ref _readyHooked, 0) == 1)
                VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;
            _buffer.Properties.RemoveProperty(typeof(ScrollbarMarkTagger));
        }

        private string? Path => NvimStateHub.NormalizePath(_document.FilePath);

        private void Subscribe()
        {
            var session = VSNeo_ExtensionPackage.Session;
            if (session == null)
            {
                // Created before the package finished loading: hook up when
                // the session is ready rather than staying deaf.
                if (Interlocked.Exchange(ref _readyHooked, 1) == 0)
                    VSNeo_ExtensionPackage.SessionReadyChanged += OnSessionReady;
                return;
            }
            if (ReferenceEquals(_subscribedTo, session.State)) return;

            if (_subscribedTo != null)
            {
                _subscribedTo.SearchMatchesChanged -= OnSearchMatchesChanged;
                _subscribedTo.BufferSwitched -= OnBufferSwitched;
                _subscribedTo.MarksChanged -= OnMarksChanged;
            }
            session.State.SearchMatchesChanged += OnSearchMatchesChanged;
            session.State.BufferSwitched += OnBufferSwitched;
            session.State.MarksChanged += OnMarksChanged;
            _subscribedTo = session.State;
        }

        private void OnSessionReady(bool ready)
        {
            if (!ready) return;
            Subscribe();
            RaiseChanged();
        }

        // Hub events arrive on the RPC read thread; each only raises
        // TagsChanged (on the UI thread) when it concerns this document.
        private void OnSearchMatchesChanged()
        {
            if (_hadSearch || IsCurrent(VSNeo_ExtensionPackage.Session?.State)) RaiseChanged();
        }

        private void OnBufferSwitched(string path)
        {
            if (_hadSearch || string.Equals(path, Path, StringComparison.OrdinalIgnoreCase)) RaiseChanged();
        }

        private void OnMarksChanged(string path)
        {
            if (string.Equals(path, Path, StringComparison.OrdinalIgnoreCase)) RaiseChanged();
        }

        private bool IsCurrent(NvimStateHub? state) =>
            state != null && string.Equals(state.CurrentBufferPath, Path, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// One TagsChanged over the whole buffer, raised on the UI thread and
        /// coalesced: a burst of hub events costs the scrollbar one repaint.
        /// </summary>
        private void RaiseChanged()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (Interlocked.Exchange(ref _changePending, 1) == 1) return;

#pragma warning disable VSTHRD001
            _ = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                Volatile.Write(ref _changePending, 0);
                var snapshot = _buffer.CurrentSnapshot;
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
            }));
#pragma warning restore VSTHRD001
        }

        public IEnumerable<ITagSpan<OverviewMarkTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0) yield break;

            List<ITagSpan<OverviewMarkTag>> tags;
            try
            {
                tags = Build(spans[0].Snapshot);
            }
            catch (Exception ex)
            {
                // A decoration must never take the editor down with it.
                Infrastructure.Log.Write("scrollbar marks failed", ex);
                yield break;
            }

            foreach (var tag in tags)
                if (spans.IntersectsWith(tag.Span)) yield return tag;
        }

        private List<ITagSpan<OverviewMarkTag>> Build(ITextSnapshot snapshot)
        {
            var state = VSNeo_ExtensionPackage.Session?.State;
            if (state == null) return new List<ITagSpan<OverviewMarkTag>>();

            bool current = IsCurrent(state);
            var matches = current ? state.SearchMatches : null;
            var marks = state.MarksFor(Path);

            if (ReferenceEquals(snapshot, _builtFor) && ReferenceEquals(matches, _builtMatches)
                && ReferenceEquals(marks, _builtMarks) && current == _builtCurrent)
                return _built;

            var tags = new List<ITagSpan<OverviewMarkTag>>();
            int lineCount = snapshot.LineCount;

            // One tick per matching line: the scrollbar cannot tell two
            // matches on a line apart, and the list arrives sorted by line.
            bool anySearch = false;
            if (matches != null)
            {
                int lastLine = -1;
                foreach (var match in matches)
                {
                    if (match.Line == lastLine || match.Line < 0 || match.Line >= lineCount) continue;
                    lastLine = match.Line;
                    tags.Add(new TagSpan<OverviewMarkTag>(snapshot.GetLineFromLineNumber(match.Line).Extent, SearchTag));
                    anySearch = true;
                }
            }

            foreach (var mark in marks)
            {
                if (mark.Line < 0 || mark.Line >= lineCount) continue;
                tags.Add(new TagSpan<OverviewMarkTag>(snapshot.GetLineFromLineNumber(mark.Line).Extent, MarkTag));
            }

            _hadSearch = anySearch;
            _builtFor = snapshot;
            _builtMatches = matches;
            _builtMarks = marks;
            _builtCurrent = current;
            _built = tags;
            return tags;
        }
    }

    /// <summary>The search-match tick's color: Vim's Search yellow. Editable in Fonts and Colors.</summary>
    [Export(typeof(EditorFormatDefinition))]
    [Name(ScrollbarMarkTagger.SearchMarkName)]
    [UserVisible(true)]
    internal sealed class ScrollbarSearchMarkFormat : EditorFormatDefinition
    {
        public ScrollbarSearchMarkFormat()
        {
            DisplayName = "VSNeo: search match (scrollbar)";
            ForegroundColor = Color.FromRgb(0xE0, 0xAF, 0x28);
            BackgroundColor = Color.FromRgb(0xE0, 0xAF, 0x28);
        }
    }

    /// <summary>The mark tick's color: a teal that stays apart from errors and changes. Editable in Fonts and Colors.</summary>
    [Export(typeof(EditorFormatDefinition))]
    [Name(ScrollbarMarkTagger.MarkName)]
    [UserVisible(true)]
    internal sealed class ScrollbarMarkFormat : EditorFormatDefinition
    {
        public ScrollbarMarkFormat()
        {
            DisplayName = "VSNeo: mark (scrollbar)";
            ForegroundColor = Color.FromRgb(0x2D, 0xC7, 0xC7);
            BackgroundColor = Color.FromRgb(0x2D, 0xC7, 0xC7);
        }
    }
}
