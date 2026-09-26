using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Draws Vim's hlsearch matches behind the text.
    ///
    /// nvim computes the matches and sends their positions; this margin turns them
    /// into background rectangles in the Visual Studio editor. It deliberately does
    /// not reimplement Vim's regex engine in C#.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class SearchHighlightAdornmentProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("VSNeoSearchHighlight")]
        [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
        [TextViewRole(PredefinedTextViewRoles.Document)]
        // MEF populates this through the Export above; nothing in code assigns it.
        internal AdornmentLayerDefinition LayerDefinition = null!;

        public void TextViewCreated(IWpfTextView textView)
        {
            textView.Properties.GetOrCreateSingletonProperty(
                () => new SearchHighlightAdornment(textView));
        }
    }

    internal sealed class SearchHighlightAdornment
    {
        private const string LayerName = "VSNeoSearchHighlight";

        private readonly IAdornmentLayer _layer;
        private readonly IWpfTextView _view;
        // Assigned by BuildBrushes(), which the constructor calls; the compiler
        // cannot see through the method call.
        private Brush _searchBrush = null!;
        private Brush _currentBrush = null!;
        private bool _disposed;

        // Hub events arrive on the RPC read thread and reach every open view;
        // only the focused one has anything to draw. Tracked here (set from the
        // view's focus events) so unfocused views bail before paying a
        // dispatcher hop per event. volatile: read on the RPC thread.
        private volatile bool _focused;

        public SearchHighlightAdornment(IWpfTextView view)
        {
            _view = view;
            _layer = view.GetAdornmentLayer(LayerName);
            _focused = view.HasAggregateFocus;

            BuildBrushes();

            Subscribe();
            view.LayoutChanged += OnLayoutChanged;
            view.GotAggregateFocus += OnGotFocus;
            view.LostAggregateFocus += OnLostFocus;
            view.Closed += OnClosed;
        }

        /// <summary>
        /// Colors come from nvim's own highlight groups, so ':hi Search
        /// guibg=...' in ~/.vsneorc really changes what Visual Studio draws.
        /// The fallbacks keep Vim's look: translucent yellow for Search, a
        /// stronger orange for the current match (CurSearch/IncSearch).
        /// </summary>
        private void BuildBrushes()
        {
            var state = VSNeo_ExtensionPackage.Session?.State;
            _searchBrush = MakeBrush(state == null ? -1 : state.SearchColor, 0x50, 0xFFD700);
            _currentBrush = MakeBrush(state == null ? -1 : state.CurrentMatchColor, 0x70, 0xFF9E40);
        }

        private static Brush MakeBrush(int rgb, byte fallbackAlpha, int fallbackRgb)
        {
            // nvim's bg is opaque; on text it has to stay translucent. 0xB0 reads
            // as the group color without drowning the foreground.
            var brush = rgb >= 0
                ? new SolidColorBrush(Color.FromArgb(0xB0,
                    (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb))
                : new SolidColorBrush(Color.FromArgb(fallbackAlpha,
                    (byte)(fallbackRgb >> 16), (byte)(fallbackRgb >> 8), (byte)fallbackRgb));
            brush.Freeze();
            return brush;
        }

        // Null until Subscribe() finds a live session; checked at every use.
        private NvimStateHub? _subscribedTo;
        private int _readyHooked;

        private void Subscribe()
        {
            var session = VSNeo_ExtensionPackage.Session;
            if (session == null)
            {
                // Created with the startup document, before the package loaded:
                // wait for the ready broadcast rather than staying deaf.
                if (Interlocked.Exchange(ref _readyHooked, 1) == 0)
                    VSNeo_ExtensionPackage.SessionReadyChanged += OnSessionReady;
                return;
            }
            if (ReferenceEquals(_subscribedTo, session.State)) return;

            if (_subscribedTo != null)
            {
                _subscribedTo.SearchMatchesChanged -= OnMatchesChanged;
                _subscribedTo.HighlightsChanged -= OnHighlightsChanged;
                _subscribedTo.CursorMoved -= OnCursorMoved;
            }
            session.State.SearchMatchesChanged += OnMatchesChanged;
            session.State.HighlightsChanged += OnHighlightsChanged;
            session.State.CursorMoved += OnCursorMoved;
            _subscribedTo = session.State;
        }

        private void OnSessionReady(bool ready)
        {
            if (!ready) return;
            var dispatcher = _view.VisualElement.Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() =>
                {
                    BuildBrushes();
                    Subscribe();
                    BeginRedraw();
                }));
#pragma warning restore VSTHRD001
        }

        private void OnMatchesChanged() => BeginRedraw();

        // Identity of the match drawn with the CurSearch brush, (-1, -1) when
        // none is. A cursor move is the only event that used to force a full
        // rebuild where nothing but - maybe - one brush changes; moves that
        // leave the current match untouched (j/k past matches on other lines)
        // now skip the rebuild entirely.
        private int _drawnCurrentLine = -1;
        private int _drawnCurrentStart = -1;

        private void OnCursorMoved(int line, int byteColumn)
        {
            if (!_focused) return;

            var session = VSNeo_ExtensionPackage.Session;
            var matches = session?.State.SearchMatches;

            int curLine = -1, curStart = -1;
            if (matches != null)
            {
                // Same "on the match" rule as Redraw, including the inclusive
                // end while a / or ? cmdline is open.
                bool searchTyping = session!.State.CmdLinePrefix == "/"
                    || session.State.CmdLinePrefix == "?";
                foreach (var m in matches)
                {
                    if (m.Line < line) continue;
                    if (m.Line > line) break; // matches arrive sorted by line
                    if (m.StartByte <= byteColumn
                        && (byteColumn < m.EndByte || (searchTyping && byteColumn == m.EndByte)))
                    {
                        curLine = m.Line;
                        curStart = m.StartByte;
                        break;
                    }
                }
            }

            if (curLine == _drawnCurrentLine && curStart == _drawnCurrentStart) return;
            BeginRedraw();
        }

        private void OnHighlightsChanged()
        {
            BuildBrushes();
            BeginRedraw();
        }

        // Runs on the UI thread (view focus events), so Redraw directly.
        // Focus loss clears the layer; focus gain draws the current matches.
        // The flag is set from the event itself, not re-read from
        // HasAggregateFocus: that property is not reliable inside the events,
        // and a stale false there silenced the highlights for good.
        private void OnGotFocus(object sender, EventArgs e)
        {
            _focused = true;
            Redraw();
        }

        private void OnLostFocus(object sender, EventArgs e)
        {
            _focused = false;
            Redraw();
        }

        private int _redrawPending;

        private void BeginRedraw()
        {
            // Unfocused views have nothing to draw; the focus handler repaints
            // on the way back in.
            if (!_focused) return;

            var dispatcher = _view.VisualElement.Dispatcher;
            if (dispatcher == null) return;

            // One queued redraw at a time: holding j with matches on screen
            // used to stack one full rebuild per cursor move.
            if (Interlocked.Exchange(ref _redrawPending, 1) == 1) return;

#pragma warning disable VSTHRD001
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() =>
                {
                    Volatile.Write(ref _redrawPending, 0);
                    Redraw();
                }));
#pragma warning restore VSTHRD001
        }

        /// <summary>
        /// Layout changes are incremental. Adornments are TextRelative, so the
        /// layer itself moves them on scroll and drops the ones on lines it
        /// reformats; only NewOrReformattedLines need drawing. This used to be a
        /// full rebuild - RemoveAllAdornments and a fresh Image per visible
        /// match - on every layout, and typing lays out the current line on
        /// every keystroke.
        /// </summary>
        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_disposed) return;

            try
            {
                if (!TryGetDrawState(out var session, out var matches))
                {
                    if (_layer.Elements.Count > 0) _layer.RemoveAllAdornments();
                    _drawnCurrentLine = _drawnCurrentStart = -1;
                    return;
                }

                foreach (var line in e.NewOrReformattedLines)
                {
                    // A reformatted line lost its adornments, the current
                    // match's included; DrawLine records it again if it is
                    // still there.
                    if (_drawnCurrentLine >= 0 && LineHolds(line, _drawnCurrentLine))
                        _drawnCurrentLine = _drawnCurrentStart = -1;

                    DrawLine(line, session, matches);
                }
            }
            catch (Exception ex)
            {
                // An adornment must never take the editor down with it.
                Infrastructure.Log.Write("search highlight layout update failed", ex);
            }
        }

        /// <summary>Full rebuild: new matches, a moved current match, focus, colors.</summary>
        private void Redraw()
        {
            if (_disposed) return;

            try
            {
                if (!TryGetDrawState(out var session, out var matches))
                {
                    // Nothing to draw. RemoveAllAdornments on an empty layer is
                    // still work, and with no active search this branch runs on
                    // every cursor move.
                    if (_layer.Elements.Count > 0) _layer.RemoveAllAdornments();
                    _drawnCurrentLine = _drawnCurrentStart = -1;
                    return;
                }

                _layer.RemoveAllAdornments();
                _drawnCurrentLine = _drawnCurrentStart = -1;

                var lines = _view.TextViewLines;
                if (lines == null) return;

                foreach (var line in lines)
                    DrawLine(line, session, matches);
            }
            catch (Exception ex)
            {
                // An adornment must never take the editor down with it.
                Infrastructure.Log.Write("search highlight redraw failed", ex);
            }
        }

        private bool TryGetDrawState(out NvimSession session, out IReadOnlyList<SearchMatch> matches)
        {
            var current = VSNeo_ExtensionPackage.Session;
            // Only read when this returns true, and that requires non-null.
            session = current!;
            matches = current?.State.SearchMatches ?? Array.Empty<SearchMatch>();
            return current != null && matches.Count > 0 && _view.HasAggregateFocus;
        }

        private static bool LineHolds(ITextViewLine line, int lineNumber) =>
            line.Start.GetContainingLine().LineNumber <= lineNumber
            && lineNumber <= line.End.GetContainingLine().LineNumber;

        /// <summary>
        /// Draws the part of every match that falls on one formatted line.
        /// Per view line rather than per match, so a match that word-wrap splits
        /// across two view lines is two adornments, each owned by its own line -
        /// reformatting one of them then removes and redraws exactly its half.
        /// </summary>
        private void DrawLine(ITextViewLine viewLine, NvimSession session, IReadOnlyList<SearchMatch> matches)
        {
            var snapshot = viewLine.Snapshot;
            var extent = viewLine.ExtentIncludingLineBreak;

            int firstLine = viewLine.Start.GetContainingLine().LineNumber;
            int lastLine = viewLine.End.GetContainingLine().LineNumber;

            // The match under the cursor gets the CurSearch/IncSearch brush.
            // Two positions count as "on the match": after <CR> and on n/N
            // the cursor sits at the match START, but while the search is
            // being typed incsearch parks it one past the last character
            // (measured: byte col == EndByte for /f, /fo, ...), so the end
            // comparison is inclusive only while a / or ? cmdline is open.
            int cursorLine = session.State.CursorLine;
            int cursorCol = session.State.CursorColumnByte;
            bool searchTyping = session.State.CmdLinePrefix == "/"
                || session.State.CmdLinePrefix == "?";

            // Matches arrive sorted by line: jump straight to this line's.
            for (int i = LowerBound(matches, firstLine); i < matches.Count; i++)
            {
                var match = matches[i];
                if (match.Line > lastLine) break;
                if (match.Line >= snapshot.LineCount) break;

                var line = snapshot.GetLineFromLineNumber(match.Line);

                int startCol = ColumnMapper.ByteToChar(line, match.StartByte);
                int endCol = ColumnMapper.ByteToChar(line, match.EndByte);

                if (startCol > line.Length) startCol = line.Length;
                if (endCol > line.Length) endCol = line.Length;
                if (endCol < startCol) endCol = startCol;

                var whole = new SnapshotSpan(line.Start + startCol, line.Start + endCol);
                var piece = whole.Overlap(extent);
                if (piece == null) continue;
                var span = piece.Value;

                Geometry geometry;
                try
                {
                    geometry = _view.TextViewLines.GetMarkerGeometry(span);
                }
                catch (Exception)
                {
                    // GetMarkerGeometry throws while the view is mid-layout.
                    continue;
                }

                if (geometry == null) continue;

                bool isCurrent = match.Line == cursorLine
                    && match.StartByte <= cursorCol
                    && (cursorCol < match.EndByte || (searchTyping && cursorCol == match.EndByte));
                if (isCurrent)
                {
                    _drawnCurrentLine = match.Line;
                    _drawnCurrentStart = match.StartByte;
                }
                var brush = isCurrent ? _currentBrush : _searchBrush;

                var image = new Image
                {
                    Source = new DrawingImage(new GeometryDrawing(brush, null, geometry)),
                    Width = geometry.Bounds.Width,
                    Height = geometry.Bounds.Height,
                };

                Canvas.SetLeft(image, geometry.Bounds.Left);
                Canvas.SetTop(image, geometry.Bounds.Top);

                // TextRelative: the layer repositions it on scroll and removes
                // it when this span's line is reformatted or leaves the view.
                _layer.AddAdornment(
                    AdornmentPositioningBehavior.TextRelative, span, null, image, null);
            }
        }

        /// <summary>First index whose Line is at least <paramref name="line"/>.</summary>
        private static int LowerBound(IReadOnlyList<SearchMatch> matches, int line)
        {
            int lo = 0, hi = matches.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (matches[mid].Line < line) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_disposed) return;
            _disposed = true;

            if (_subscribedTo != null)
            {
                _subscribedTo.SearchMatchesChanged -= OnMatchesChanged;
                _subscribedTo.HighlightsChanged -= OnHighlightsChanged;
                _subscribedTo.CursorMoved -= OnCursorMoved;
            }
            if (Interlocked.Exchange(ref _readyHooked, 0) == 1)
                VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;

            _view.LayoutChanged -= OnLayoutChanged;
            _view.GotAggregateFocus -= OnGotFocus;
            _view.LostAggregateFocus -= OnLostFocus;
            _view.Closed -= OnClosed;
        }
    }
}
