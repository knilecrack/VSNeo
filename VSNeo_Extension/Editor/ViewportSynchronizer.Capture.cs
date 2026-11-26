using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using VSNeo_Extension.Infrastructure;

namespace VSNeo_Extension.Editor;

// Visual Studio to nvim: what the view looks like now, and the debounced send.
internal sealed partial class ViewportSynchronizer
{
    /// <summary>
    /// LayoutChanged fires on scrolls, when the caret may still be where the
    /// last key left it. Keeping the pending caret current with the caret
    /// itself is what lets Flush detect a mid-flight clamp (see _sentClamp)
    /// and undo it once the jump's caret move lands.
    /// </summary>
    private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var point = e.NewPosition.BufferPosition;
        var line = point.GetContainingLine();
        _pendingCaretLine = line.LineNumber;
        _pendingCaretCol = ColumnMapper.CharToByte(line, point.Position - line.Start.Position);

        // Caret events fire mid-layout (TextViewLines throws then) and right
        // after an edit, before relayout (the lines are on an older snapshot
        // and the lookup throws). Either way the layout that follows
        // captures visibility itself, so skip the read; a throw here also
        // used to skip the debounce restart below.
        var view = _view;
        if (view != null && !view.IsClosed && !view.InLayout)
        {
            try
            {
                var lines = view.TextViewLines;
                if (lines != null && lines.Count > 0
                    && lines.FormattedSpan.Snapshot == point.Snapshot)
                {
                    var caretViewLine = lines.GetTextViewLineContainingBufferPosition(point);
                    _pendingCaretVisible = caretViewLine != null
                        && caretViewLine.VisibilityState != VisibilityState.Hidden ? 1 : 0;
                }
            }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
        }

        try
        { _debounce.Change(DebounceMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
    {
        using var perf = Perf.Time("ViewportSynchronizer.OnLayoutChanged");
        // LayoutChanged is raised on the UI thread; Capture reads view state.
        ThreadHelper.ThrowIfNotOnUIThread();

        // A smooth scroll lays out every frame. Its intermediate toplines
        // are not places the view is going to stay, and sending them would
        // move nvim's window (and clamp its cursor) through each one. The
        // animation's final, exact layout arrives with IsAnimating already
        // false and is captured like any other.
        if (_view != null && SmoothScroller.IsAnimating(_view))
        {
            return;
        }

        Capture();
    }

    private void Capture()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var view = _view;
        if (view == null || view.IsClosed)
        {
            return;
        }

        var lines = view.TextViewLines;
        if (lines == null || lines.Count == 0)
        {
            return;
        }

        _pendingHeight = HeightInRows(view);
        _pendingWidth = EstimateWidth(view);
        _pendingTop = lines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
        var caretPos = view.Caret.Position.BufferPosition;
        var caretLine = caretPos.GetContainingLine();
        _pendingCaretLine = caretLine.LineNumber;
        _pendingCaretCol = ColumnMapper.CharToByte(
            caretLine, caretPos.Position - caretLine.Start.Position);
        // Visibility from the layout itself, not from line arithmetic: a
        // position scrolled out of the layout - or hidden inside a collapsed
        // outlining region - has no laid-out line. A fold compresses the
        // view, so topline + height arithmetic cannot say whether the caret
        // is on screen; this flag can, and it drives note_viewport's clamp.
        var caretViewLine = lines.GetTextViewLineContainingBufferPosition(caretPos);
        _pendingCaretVisible = caretViewLine != null
            && caretViewLine.VisibilityState != VisibilityState.Hidden ? 1 : 0;
        _pendingLineCount = view.TextSnapshot.LineCount;
        _pendingPastEnd = IsPastNvimEnd(view, _pendingTop, _pendingHeight) ? 1 : 0;
        Volatile.Write(ref _captureStale, 0);

        try
        { _debounce.Change(DebounceMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Rows that fit the viewport, from stable geometry rather than
    /// TextViewLines.Count. The count of laid-out lines includes the
    /// partially visible ones, so it flips by one as a scroll gesture
    /// alternates which edge cuts a line in half - and every flip sent a
    /// resize to nvim, which recomputed its window and reported a scroll
    /// back, which changed the layout, which flipped the count again.
    /// That loop is the lag and jump of holding j past the bottom of the
    /// view. ViewportHeight / LineHeight only changes when the window is
    /// genuinely resized.
    /// </summary>
    private static int HeightInRows(IWpfTextView view)
    {
        try
        {
            double lineHeight = view.LineHeight;
            if (lineHeight > 0.5)
            {
                return Math.Max(1, (int)(view.ViewportHeight / lineHeight));
            }
        }
        catch
        {
            // Fall through to the default below.
        }

        return 40;
    }

    /// <summary>
    /// Columns that fit across the viewport. Only approximately meaningful with a
    /// proportional font, and it matters far less than the height - nothing in
    /// the motions above is defined by width - but a grid narrower than the text
    /// would make nvim believe lines are wrapping when VS is not wrapping them.
    /// </summary>
    private static int EstimateWidth(IWpfTextView view)
    {
        try
        {
            double charWidth = view.FormattedLineSource?.ColumnWidth ?? 0;
            if (charWidth > 0.5)
            {
                return Math.Max(80, Math.Min(2000, (int)(view.ViewportWidth / charWidth)));
            }
        }
        catch
        {
            // Fall through to the default below.
        }

        return 200;
    }

    private void Flush()
    {
        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || !session.IsReady)
        {
            return;
        }

        // A capture from before nvim's last scroll would undo it; the
        // layout after the scroll captures again and flushes then.
        if (Volatile.Read(ref _captureStale) == 1)
        {
            return;
        }

        int height = Volatile.Read(ref _pendingHeight);
        int width = Volatile.Read(ref _pendingWidth);
        int top = Volatile.Read(ref _pendingTop);

        if (height <= 0)
        {
            return;
        }

        if (height != _sentHeight || width != _sentWidth)
        {
            _sentHeight = height;
            _sentWidth = width;

            // Resizing invalidates the topline nvim was holding.
            _sentTop = -1;

            // Ahead of the resize on the same stream: picks 'splitkeep' so a
            // cursor the smaller window would cover scrolls the window
            // rather than moving (see before_resize).
            Observe(session.RequestAsync("nvim_exec_lua", "vsneo.before_resize(...)", new object[] { height }));
            Observe(session.RequestAsync("nvim_ui_try_resize", width, height));
            Log.Write("viewport resized to " + width + "x" + height);
        }

        // The one state nvim's window cannot represent is the view
        // scrolled past the end of the file: nvim clamps its topline to
        // lineCount - height, and the report that comes back is the clamp,
        // not what was sent - it matches nothing in the echo ring, so
        // applying it yanks the view back up on every wheel tick. It is
        // not sent, and while it lasts ApplyScroll ignores nvim's reports.
        //
        // The caret scrolled off screen used to suspend sync too - an nvim
        // window always contains its own cursor, so the topline was
        // refused - which is what left H/M/L aiming at wherever you
        // scrolled from. Now the caret position goes along with the
        // topline and the companion clamps nvim's cursor into the window
        // (flagged synthetic, so the caret here is not dragged along) -
        // but only when the caret is genuinely off screen, as decided by
        // the captured visibility flag. Line arithmetic called a caret
        // below a collapsed outlining region "outside the window" while it
        // was plainly visible, and the manufactured clamp snapped the next
        // motion back to the window edge.
        int caret = Volatile.Read(ref _pendingCaretLine);
        int caretCol = Volatile.Read(ref _pendingCaretCol);
        // Counted in screen rows at capture time (IsPastNvimEnd): with a
        // collapsed region near the end of the file, "top > lineCount -
        // height" said a topline was showable when nvim, counting the fold
        // as one row, clamped it - and the clamp came back as a scroll on
        // every wheel tick. That was the flicker.
        bool pastEnd = Volatile.Read(ref _pendingPastEnd) == 1;

        // Visibility as captured from the laid-out lines; outside it,
        // note_viewport clamps nvim's cursor.
        bool caretInWindow = Volatile.Read(ref _pendingCaretVisible) == 1;

        // Logged on transitions only - this runs on a timer and the state
        // flips once per scroll gesture.
        int suspended = pastEnd ? 1 : 0;
        if (Interlocked.Exchange(ref _syncSuspended, suspended) != suspended)
        {
            Log.Write(suspended == 1
                ? "view scrolled past end of file - nvim scroll sync suspended"
                : "view back in nvim's range - nvim scroll sync resumed");
        }

        // topChanged is the steady case. The second disjunct undoes a clamp
        // sent with a mid-flight caret (_sentClamp): the jump's caret move
        // lands after the scroll, and unless the landing itself scrolled
        // the view nothing else would ever re-send - nvim's cursor stays on
        // the window edge it was clamped to, and the next % computes from
        // there.
        bool topChanged = top != _sentTop;
        bool rejoin = Volatile.Read(ref _sentClamp) == 1 && caretInWindow;

        if ((topChanged || rejoin) && !pastEnd)
        {
            _sentTop = top;
            Volatile.Write(ref _sentClamp, caretInWindow ? 0 : 1);

            if (topChanged)
            {
                lock (_sentEchoes)
                {
                    long cutoff = DateTime.UtcNow.Ticks - EchoWindowTicks;
                    _sentEchoes.RemoveAll(e => e.Value < cutoff);
                    _sentEchoes.Add(new KeyValuePair<int, long>(top, DateTime.UtcNow.Ticks));
                }
            }

            // All lines 1-based, the column a 0-based byte offset, the flag
            // whether the caret is on a laid-out (visible) line. The last two
            // are nvim's cursor as this side last heard it: the companion
            // drops the whole update when nvim's cursor has moved since,
            // because then keys ran after this view state was captured.
            // That happens whenever the request lands in the middle of a
            // key sequence: nvim does not service RPC while it waits for
            // the second key of gg, dw, "a, so a flush sent after the
            // first g (the old showcmd margin appearing resized the view) ran
            // after gg - and put the cursor and window back where they
            // were before it. Dropped, it is re-sent from fresh state.
            // nvim's topline goes along for zz/zt/zb, which scroll
            // without moving the cursor: queued behind the first z, this
            // ran after zz and put the old topline back.
            var hub = session.State;
            int knownLine = hub.CursorLine, knownCol = hub.CursorColumnByte;
            var request = session.RequestAsync(
                "nvim_exec_lua",
                "return vsneo.note_viewport(...)",
                new object[] { top + 1, height, caret + 1, caretCol, caretInWindow,
                               knownLine + 1, knownCol, hub.TopLine + 1 });
            _ = request.ContinueWith(t =>
            {
                if (t.IsFaulted)
                { _ = t.Exception; return; }

                if (t.Result is string answer && answer == "deferred")
                {
                    _sentTop = -1;
                    Volatile.Write(ref _deferred, 1);
                    // The mode may have ended while the answer was on the wire.
                    OnNvimModeChanged(hub.Mode);
                    return;
                }

                if (!(t.Result is bool applied) || applied)
                {
                    return;
                }

                Log.Key("viewport: stale note_viewport dropped by nvim - resending");
                // Forget what was sent and flush again once the state
                // settles; the next Flush reads the hub's fresh cursor.
                _sentTop = -1;
                try
                { _debounce.Change(DebounceMs, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }, CancellationToken.None,
               System.Threading.Tasks.TaskContinuationOptions.None,
               System.Threading.Tasks.TaskScheduler.Default);
        }
    }
}
