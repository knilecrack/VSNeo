using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// The render itself: one pass per layout, and the signature that skips it.
internal sealed partial class RelativeLineNumberMargin
{
    // What the last OnRender drew from; see OnLayoutChanged.
    private long _renderedSignature;

    /// <summary>
    /// A cheap fingerprint of everything OnRender reads: the first visible
    /// line, the buffer line count, the caret line, the viewport top and
    /// the last visible line's bottom (a reformat that wraps or unwraps a
    /// line moves everything below it).
    /// </summary>
    private long RenderSignature()
    {
        try
        {
            var lines = _view.TextViewLines;
            if (lines == null || lines.Count == 0)
            {
                return 0;
            }

            long h = lines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
            h = h * 31 + _view.TextSnapshot.LineCount;
            h = h * 31 + _view.Caret.Position.BufferPosition.GetContainingLine().LineNumber;
            h = h * 31 + (long)Math.Round(_view.ViewportTop);
            h = h * 31 + (long)Math.Round(lines.LastVisibleLine.Bottom);
            return h;
        }
        catch
        {
            return -1;   // unreadable mid-layout: never equal, so it repaints
        }
    }

    private void OnViewportHeightChanged(object sender, EventArgs e)
    {
        if (_active)
        {
            InvalidateVisual();
        }
    }

    // The cache holds zoom-scaled glyphs, so a zoom change needs a fresh
    // shaping pass and a fresh measure, not just a repaint.
    private void OnZoomLevelChanged(object sender, ZoomLevelChangedEventArgs e)
    {
        // Glyphs are keyed by size, so the new zoom shapes its own set and
        // zooming back finds the old one still cached.
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        using var perf = Infrastructure.Perf.Time("RelativeLineNumberMargin.OnRender");
        long started = Infrastructure.Perf.Now;
        base.OnRender(dc);

        if (_disposed || !_active)
        {
            return;
        }

        _shapedThisRender = 0;
        int drawn = 0;
        try
        {
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            var lines = _view.TextViewLines;
            if (lines == null || lines.Count == 0)
            {
                return;
            }

            var snapshot = _view.TextSnapshot;
            if (snapshot == null)
            {
                return;
            }

            // The caret can be at an invalid position while the view is being
            // laid out or closed; GetContainingLine throws in that state.
            var caretPosition = _view.Caret.Position.BufferPosition;
            if (caretPosition.Snapshot != snapshot)
            {
                return;
            }

            int caretLine = caretPosition.GetContainingLine().LineNumber;
            _lastCaretLine = caretLine;
            _renderedSignature = RenderSignature();
            bool caretAbsolute = CaretLineIsAbsolute();

            // line.Top is in text-view coordinates, which scroll with the
            // content; the margin's origin is the viewport's top. Without
            // this subtraction every number is drawn its full scroll offset
            // too low, ClipToBounds cuts the lot, and the margin appears to
            // render only the first screenful of the file.
            double viewportTop = _view.ViewportTop;

            // Editor zoom scales the text surface but not this margin, and the
            // line coordinates stay unzoomed. At 145% the numbers came out at
            // 1/1.45 of the real line pitch, drifting rows away from their code
            // the further they sat from the first visible line. Positions and
            // glyphs both have to be scaled by the zoom factor to track the text.
            double zoom = Math.Max(0.01, _view.ZoomLevel / 100.0);
            double fontSize = _fontSize * zoom;

            // Distances count laid-out lines, not buffer lines: a closed
            // fold is a single line in Vim, and the numbers shown here are
            // what the user types as j/k counts - with folds mirrored into
            // nvim, only visible-line distances land where they say. Find
            // the caret's index in the visible sequence first.
            int caretIndex = -1;
            int visibleCount = 0;
            // One entry per buffer line, not per wrapped row: with Visual
            // Studio's word wrap on, TextViewLines holds a row per wrap, and
            // counting those numbered a three-row line three times and
            // inflated every distance below it - 5j did not land where the
            // margin said.
            foreach (var line in lines)
            {
                if (line.VisibilityState != VisibilityState.FullyVisible
                    && line.VisibilityState != VisibilityState.PartiallyVisible)
                {
                    continue;
                }

                if (!line.IsFirstTextViewLineForSnapshotLine)
                {
                    continue;
                }

                if (line.Start.GetContainingLine().LineNumber == caretLine)
                {
                    caretIndex = visibleCount;
                }

                visibleCount++;
            }

            int index = -1;
            foreach (var line in lines)
            {
                if (line.VisibilityState != VisibilityState.FullyVisible
                    && line.VisibilityState != VisibilityState.PartiallyVisible)
                {
                    continue;
                }

                if (!line.IsFirstTextViewLineForSnapshotLine)
                {
                    continue;
                }

                index++;

                int lineNumber = line.Start.GetContainingLine().LineNumber;
                // A caret the layout has not caught up with yet (no visible
                // line carries it) falls back to raw buffer distance.
                int number = lineNumber == caretLine
                    ? (caretAbsolute ? lineNumber + 1 : 0)
                    : caretIndex >= 0
                        ? Math.Abs(index - caretIndex)
                        : Math.Abs(lineNumber - caretLine);

                bool isCaretLine = lineNumber == caretLine;
                var formatted = isCaretLine
                    ? CaretGlyphFor(number, fontSize)
                    : GlyphFor(number, fontSize);

                // Centered on the text, not the line: a line carrying a CodeLens
                // header is taller than its text, and Top/Height span the lot, so
                // centering on them floats the number up into the lens row.
                double y = (line.TextTop - viewportTop) * zoom
                           + (line.TextHeight * zoom - formatted.Height) / 2;
                if (y + formatted.Height < 0 || y > ActualHeight)
                {
                    continue;
                }

                // Vim left-aligns the cursor line's absolute number
                // ('number' with 'relativenumber', or Hybrid here);
                // everything else, the cursor line's 0 included, is
                // right-aligned.
                double x = isCaretLine && caretAbsolute
                    ? 4
                    : Math.Max(0, ActualWidth - formatted.Width - 4);
                dc.DrawText(formatted, new Point(x, y));
                drawn++;
            }
        }
        catch (Exception ex)
        {
            // A margin must never take the editor down with it.
            Log.Write("relative line number render failed", ex);
        }

        // Slow renders were logged at 110-167 ms with nothing to say why.
        // The detail tells shaping (new glyphs) from a big screen from a
        // pause that was not ours at all (garbage collection, VS work).
        double ms = Infrastructure.Perf.Ms(started, Infrastructure.Perf.Now);
        if (ms >= Infrastructure.Perf.SlowUiMs)
        {
            Log.Write("slow ui detail: RelativeLineNumberMargin drew " + drawn
                + " numbers, shaped " + _shapedThisRender + ", glyph cache " + Glyphs.Count);
        }
    }
}
