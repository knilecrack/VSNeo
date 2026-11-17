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

// The [3/17] counter on the current match.
internal sealed partial class SearchHighlightAdornment
{
    // Whether the [n/N] chip is on screen, so a mode change knows it has
    // something to take down.
    private bool _countShown;

    // Tags the chip so it can be replaced without touching the highlights.
    private static readonly object CountTag = new object();

    /// <summary>(Re)draws the [n/N] chip for the current match, if there is one.</summary>
    private void ShowCount(NvimSession session, IReadOnlyList<SearchMatch> matches)
    {
        _layer.RemoveAdornmentsByTag(CountTag);

        // The list here is the scanned window; the companion's searchcount
        // figures place it in the buffer. Without them (an older companion)
        // the window-local numbers are all there is.
        var state = session.State;
        int index = _drawnCurrentIndex;
        int total = matches.Count;
        bool incomplete = false;
        if (state.SearchTotal >= 0)
        {
            index += state.SearchMatchesBefore;
            total = state.SearchTotal;
            incomplete = state.SearchTotalIncomplete;
        }

        var snapshot = _view.TextSnapshot;
        _countShown = _drawnCurrentIndex >= 0
            && snapshot != null
            && _drawnCurrentLine < snapshot.LineCount
            && state.SearchCountEnabled
            && state.Mode != VimMode.Insert
            && state.Mode != VimMode.Replace
            && DrawCount(snapshot, _drawnCurrentLine, index, total, incomplete);
    }

    /// <summary>The companion stops listing matches at this many (max_matches in vsneo.lua).</summary>
    private const int MatchCap = 5000;

    /// <summary>
    /// The [current/total] chip, a couple of columns past the end of the
    /// match's line - where nvim-hlslens puts its virtual text, so it never
    /// covers code. Styled as a Search highlight with the editor's own font
    /// and text color. False when the line has no laid-out view line.
    /// </summary>
    private bool DrawCount(ITextSnapshot snapshot, int lineNumber, int index, int total, bool incomplete)
    {
        var line = snapshot.GetLineFromLineNumber(lineNumber);
        var viewLine = _view.TextViewLines.GetTextViewLineContainingBufferPosition(line.End);
        if (viewLine == null)
        {
            return false;
        }

        var props = _view.FormattedLineSource?.DefaultTextProperties;
        var text = new TextBlock
        {
            // At the cap the companion stopped counting: the total is a floor.
            Text = "[" + (index + 1) + "/" + total + (incomplete || total >= MatchCap ? "+" : "") + "]",
            Foreground = props?.ForegroundBrush ?? Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (props != null)
        {
            text.FontFamily = props.Typeface.FontFamily;
            text.FontSize = props.FontRenderingEmSize * 0.9;
        }

        var chip = new Border
        {
            Background = _searchBrush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0, 4, 0),
            Height = viewLine.TextHeight,
            Child = text,
            IsHitTestVisible = false,
        };

        double column = _view.FormattedLineSource?.ColumnWidth ?? 7;
        Canvas.SetLeft(chip, viewLine.TextRight + (2 * column));
        Canvas.SetTop(chip, viewLine.TextTop);
        // TextRelative on its view line, like the highlights: it scrolls
        // with the text and the layer drops it when the line is reformatted.
        _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, viewLine.Extent, CountTag, chip, null);
        return true;
    }
}
