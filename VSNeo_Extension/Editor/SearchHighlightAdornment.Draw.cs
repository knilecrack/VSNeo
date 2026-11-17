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

namespace VSNeo_Extension.Editor;

// One layout pass: which matches are on screen, and where each is drawn.
internal sealed partial class SearchHighlightAdornment
{
    // Identity of the match drawn with the CurSearch brush, (-1, -1) when
    // none is. A cursor move is the only event that used to force a full
    // rebuild where nothing but - maybe - one brush changes; moves that
    // leave the current match untouched (j/k past matches on other lines)
    // now skip the rebuild entirely.
    private int _drawnCurrentLine = -1;
    private int _drawnCurrentStart = -1;

    // Where that match sits in the match list: the n in the [n/N] chip.
    private int _drawnCurrentIndex = -1;

    /// <summary>Full rebuild: new matches, a moved current match, focus, colors.</summary>
    private void Redraw()
    {
        using var perf = Infrastructure.Perf.Time("SearchHighlightAdornment.Redraw");
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!TryGetDrawState(out var session, out var matches))
            {
                // Nothing to draw. RemoveAllAdornments on an empty layer is
                // still work, and with no active search this branch runs on
                // every cursor move.
                if (_layer.Elements.Count > 0)
                {
                    _layer.RemoveAllAdornments();
                }

                _drawnCurrentLine = _drawnCurrentStart = _drawnCurrentIndex = -1;
                _countShown = false;
                return;
            }

            _layer.RemoveAllAdornments();
            _drawnCurrentLine = _drawnCurrentStart = _drawnCurrentIndex = -1;

            var lines = _view.TextViewLines;
            if (lines == null)
            {
                return;
            }

            foreach (var line in lines)
            {
                DrawLine(line, session, matches);
            }

            ShowCount(session, matches);
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
        return current != null && matches.Count > 0 && _focused;
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
            if (match.Line > lastLine)
            {
                break;
            }

            if (match.Line >= snapshot.LineCount)
            {
                break;
            }

            var line = snapshot.GetLineFromLineNumber(match.Line);

            int startCol = ColumnMapper.ByteToChar(line, match.StartByte);
            int endCol = ColumnMapper.ByteToChar(line, match.EndByte);

            if (startCol > line.Length)
            {
                startCol = line.Length;
            }

            if (endCol > line.Length)
            {
                endCol = line.Length;
            }

            if (endCol < startCol)
            {
                endCol = startCol;
            }

            var whole = new SnapshotSpan(line.Start + startCol, line.Start + endCol);
            var piece = whole.Overlap(extent);
            if (piece == null)
            {
                continue;
            }

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

            if (geometry == null)
            {
                continue;
            }

            bool isCurrent = match.Line == cursorLine
                && match.StartByte <= cursorCol
                && (cursorCol < match.EndByte || (searchTyping && cursorCol == match.EndByte));
            if (isCurrent)
            {
                _drawnCurrentLine = match.Line;
                _drawnCurrentStart = match.StartByte;
                _drawnCurrentIndex = i;
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
            if (matches[mid].Line < line)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
