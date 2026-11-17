using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace VSNeo_Extension.Editor;

// Follow-scrolls and amplified edge jumps, decided from Visual Studio geometry.
internal sealed partial class ViewportSynchronizer
{
    /// <summary>
    /// A follow-scroll - nvim moving its window a row or two because its
    /// cursor left it - is decided here, from Visual Studio's own layout,
    /// never applied as reported. Returns true when handled.
    ///
    /// nvim's window is sized in rows of LineHeight, but Visual Studio's
    /// lines are not always that tall: CodeLens puts a strip of space above
    /// every member, and other line transforms do the same. The view then
    /// shows fewer lines than nvim's window holds, and the two scroll at
    /// different moments. Holding j, Visual Studio scrolled its caret back
    /// into view line by line (EnsureVisible) while nvim, whose taller
    /// window still held its cursor, did nothing - then nvim's own edge
    /// scroll arrived with a topline far above the view, applied, and
    /// dragged it back up; the caret was off screen again, and it repeated.
    /// So the scroll bar bounced in C# files with CodeLens on.
    ///
    /// Recognised from nvim's side alone, so a mismatch cannot hide it: the
    /// topline stepped by a row or two since nvim's previous report, and
    /// the cursor now sits at the configured offset from that edge. That
    /// excludes &lt;C-e&gt;/&lt;C-y&gt;, which leave the cursor mid-window.
    /// Padding is checked against laid-out lines. If it is already satisfied,
    /// the view stays; otherwise Visual Studio positions the cursor line at
    /// the padded edge (or centres it when scrolloff is zero). Either way
    /// nvim hears the resulting topline at once, since the layout capture is
    /// starved while a key repeats.
    /// </summary>
    private bool HandleFollowScroll(IWpfTextView view, ITextViewLineCollection? lines,
                                    int nvimTop, int previousNvimTop)
    {
        if (previousNvimTop < 0 || lines == null || lines.Count == 0)
        {
            return false;
        }

        if (nvimTop == previousNvimTop)
        {
            return false;
        }

        var session = VSNeo_ExtensionPackage.Session;
        if (session == null)
        {
            return false;
        }

        var snapshot = view.TextSnapshot;
        int cursor = session.State.CursorLine;
        if (cursor < 0 || cursor >= snapshot.LineCount)
        {
            return false;
        }

        int nvimHeight = Volatile.Read(ref _sentHeight);
        if (nvimHeight <= 0)
        {
            nvimHeight = HeightInRows(view);
        }

        int padding = Math.Min(session.State.Scrolloff, Math.Max(0, (nvimHeight - 1) / 2));

        int low = Math.Min(Math.Min(previousNvimTop, nvimTop), cursor);
        int high = Math.Max(Math.Max(previousNvimTop, nvimTop), cursor);
        var folds = FoldsIn(view, low, high);

        int step = folds.RowsBetween(previousNvimTop, nvimTop);
        if (step == 0 || step < -2 || step > 2)
        {
            return false;
        }

        int rowsToCursor = folds.RowsBetween(nvimTop, cursor);
        bool follows = step > 0
            ? rowsToCursor >= nvimHeight - padding - 1
            : rowsToCursor <= padding;
        if (!follows)
        {
            return false;
        }

        int currentTop = lines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
        var cursorPoint = snapshot.GetLineFromLineNumber(cursor).Start;
        var cursorLine = lines.GetTextViewLineContainingBufferPosition(cursorPoint);
        int required = RequiredScrollPadding(view, cursor, padding, step > 0);
        if (HasScrollPadding(view, lines, cursorLine, required, step > 0))
        {
            Infrastructure.Log.Key("scroll: nvim top " + nvimTop + " follows cursor " + cursor
                + ", padding satisfied - view stays at " + currentTop);
            SendAmplifiedTopline(view, currentTop);
            return true;
        }

        SmoothScroller.For(view).Cancel();
        Volatile.Write(ref _captureStale, 1);
        Volatile.Write(ref _edgeJumpGuardUntil, unchecked(Environment.TickCount + EdgeJumpGuardMs));
        double offset = padding == 0
            ? Math.Max(0, (view.ViewportHeight - view.LineHeight) / 2)
            : step > 0
                ? Math.Max(0, view.ViewportHeight - (required + 1) * view.LineHeight)
                : required * view.LineHeight;
        view.DisplayTextLineContainingBufferPosition(cursorPoint, offset, ViewRelativePosition.Top);

        var after = view.TextViewLines;
        int newTop = after != null && after.Count > 0
            ? after.FirstVisibleLine.Start.GetContainingLine().LineNumber
            : currentTop;
        Infrastructure.Log.Key("scroll: nvim top " + nvimTop + " follows cursor " + cursor
            + " - padding restored, view top " + currentTop + " -> " + newTop);
        SendAmplifiedTopline(view, newTop);
        return true;
    }

    private int RequiredScrollPadding(IWpfTextView view, int cursor, int padding, bool below)
    {
        int last = view.TextSnapshot.LineCount - 1;
        int available = below
            ? FoldsIn(view, cursor, last).RowsBetween(cursor, last)
            : FoldsIn(view, 0, cursor).RowsBetween(0, cursor);
        return Math.Min(padding, Math.Max(0, available));
    }

    private static bool HasScrollPadding(IWpfTextView view, ITextViewLineCollection lines,
        ITextViewLine? cursorLine, int required, bool below)
    {
        if (cursorLine == null || cursorLine.VisibilityState == VisibilityState.Hidden
            || cursorLine.TextTop < view.ViewportTop || cursorLine.TextBottom > view.ViewportBottom)
        {
            return false;
        }

        int cursorIndex = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (ReferenceEquals(lines[i], cursorLine))
            { cursorIndex = i; break; }
        }

        if (cursorIndex < 0)
        {
            return false;
        }

        int visible = 0;
        int lineIndex = cursorIndex + (below ? 1 : -1);
        while (lineIndex >= 0 && lineIndex < lines.Count && visible < required)
        {
            var line = lines[lineIndex];
            if (line.TextTop < view.ViewportTop || line.TextBottom > view.ViewportBottom)
            {
                break;
            }

            if (line.VisibilityState != VisibilityState.Hidden)
            {
                visible++;
            }

            lineIndex += below ? 1 : -1;
        }

        return visible >= required;
    }

    /// <summary>
    /// Tells nvim the topline an amplified edge jump just scrolled to, now.
    ///
    /// It used to ride the layout capture, whose Flush waits DebounceMs
    /// after the last layout - and a held j lays out on every repeat,
    /// faster than that, so the flush never came until the key was let go
    /// (measured: 200-700 ms late). nvim's window meanwhile kept its own
    /// topline and went on scrolling a line per j; once the edge-jump
    /// guard lapsed those reports applied, dragging the view back up
    /// until the two met, then the next jump went down again.
    ///
    /// Only the topline: note_viewport also rejoins nvim's cursor to the
    /// caret, and mid-repeat the caret lags nvim's cursor by the keys in
    /// flight, so rejoining would step nvim back. vsneo.set_topline keeps
    /// the cursor and refuses a topline that would leave it off screen.
    /// Recorded as sent and as an echo, so neither the next capture nor
    /// nvim's WinScrolled report sends it round again. UI thread.
    /// </summary>
    private void SendAmplifiedTopline(IWpfTextView view, int topLine)
    {
        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || !session.IsReady)
        {
            return;
        }

        // Past the end of what nvim can show: Flush's pastEnd logic
        // suspends sync there, exactly as after a wheel scroll past the end.
        if (IsPastNvimEnd(view, topLine, HeightInRows(view)))
        {
            return;
        }

        _sentTop = topLine;
        lock (_sentEchoes)
        {
            long cutoff = DateTime.UtcNow.Ticks - EchoWindowTicks;
            _sentEchoes.RemoveAll(e => e.Value < cutoff);
            _sentEchoes.Add(new KeyValuePair<int, long>(topLine, DateTime.UtcNow.Ticks));
        }

        Infrastructure.Log.Key("scroll: amplified topline " + topLine + " sent to nvim");
        Observe(session.RequestAsync("nvim_exec_lua", "vsneo.set_topline(...)", new object[] { topLine + 1 }));
    }

    /// <summary>
    /// Turns nvim's one-line edge scroll into a half-screen jump and returns
    /// whether it did. Recognising the case: the window moved by a line or
    /// two (anything bigger is zz, &lt;C-d&gt; or a jump, applied exactly) and
    /// the cursor sits at the window edge in the scroll direction. The last
    /// condition is what excludes &lt;C-e&gt;/&lt;C-y&gt;, which scroll one line
    /// but leave the cursor mid-window - unless the cursor was already pinned
    /// to the edge, where one &lt;C-e&gt; centers it; that is accepted.
    ///
    /// The jump lands the caret mid-view. Near the end of the file it can
    /// scroll past where nvim's window can follow: Visual Studio lets the
    /// last line rise into the middle of the view (wheel scrolling does the
    /// same), and Flush's pastEnd logic suspends nvim scroll sync while the
    /// view sits there, exactly as after a wheel gesture past the end.
    /// </summary>
    private bool AmplifyEdgeScroll(IWpfTextView view, ITextViewLineCollection? lines, ref int topLine)
    {
        if (lines == null || lines.Count == 0)
        {
            return false;
        }

        int currentTop = lines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
        if (topLine == currentTop)
        {
            return false;
        }

        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || session.State.Scrolloff > 0)
        {
            return false;
        }

        // The hub's cursor, not the caret: the push that carried this scroll
        // also carried the cursor, but the caret here is only updated after
        // this method returns.
        int caret = session.State.CursorLine;
        if (caret < 0 || caret >= view.TextSnapshot.LineCount)
        {
            return false;
        }

        // All of it in screen rows: a collapsed region is one row, in
        // Visual Studio and in nvim alike. Counted in buffer lines, a fold
        // on screen made a caret mid-view look like one on the edge (and
        // the reverse), a scroll past a fold look like a jump, and the
        // "centered" topline land anywhere but the centre.
        int height = HeightInRows(view);
        int low = Math.Min(Math.Min(currentTop, topLine), caret);
        int high = Math.Max(Math.Max(currentTop, topLine), caret);
        var folds = FoldsIn(view, low - height, high);

        int delta = folds.RowsBetween(currentTop, topLine);
        if (delta == 0 || delta < -2 || delta > 2)
        {
            return false;
        }

        int rowsFromTop = folds.RowsBetween(topLine, caret);
        bool atBottomEdge = delta > 0 && rowsFromTop >= height - 2;
        bool atTopEdge = delta < 0 && rowsFromTop <= 1;
        if (!atBottomEdge && !atTopEdge)
        {
            return false;
        }

        int centered = folds.LineRowsAbove(caret, height / 2);
        // Clamped to the last line, not lineCount - height: the jump may
        // deliberately scroll past nvim's range so the end of the file sits
        // mid-view. nvim cannot represent that topline, so nothing is sent
        // and Flush's pastEnd check suspends sync until the view returns.
        int maxTop = view.TextSnapshot.LineCount - 1;
        if (centered > maxTop)
        {
            centered = maxTop;
        }

        if (centered < 0)
        {
            centered = 0;
        }

        if (centered == currentTop)
        {
            return false;   // clamped at a file edge
        }

        Volatile.Write(ref _edgeJumpGuardUntil, unchecked(Environment.TickCount + EdgeJumpGuardMs));
        topLine = centered;
        return true;
    }
}
