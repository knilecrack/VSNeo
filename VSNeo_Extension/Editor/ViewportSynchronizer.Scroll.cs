using System;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;

namespace VSNeo_Extension.Editor;

// nvim to Visual Studio: a topline report, and the scroll that follows it.
internal sealed partial class ViewportSynchronizer
{
    /// <summary>Any thread; only a timer restart, and only after a deferral.</summary>
    private void OnNvimModeChanged(Nvim.VimMode mode)
    {
        if (mode != Nvim.VimMode.Normal && mode != Nvim.VimMode.Insert)
        {
            return;
        }

        if (Interlocked.Exchange(ref _deferred, 0) == 0)
        {
            return;
        }

        try
        { _debounce.Change(DebounceMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void OnNvimScrolled(int topLine)
    {
        // Same foreign-buffer guard as CursorSynchronizer: after an
        // nvim-initiated buffer switch (file mark, cross-file <C-o>, :b),
        // scroll reports describe the buffer nvim jumped to, not the
        // document on screen. Dropped until the snap-back lands.
        var expectedBuffer = TextViewCreationListener.ExpectedNvimPath;
        if (expectedBuffer != null)
        {
            var showing = _subscribedTo != null ? _subscribedTo.CurrentBufferPath : null;
            if (showing != null
                && !string.Equals(showing, expectedBuffer, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        // Inside a Visual Studio-initiated buffer switch this report is the
        // buffer's old topline, not a scroll; applying it would drag the view
        // off the navigation target. The subtraction stays correct when
        // TickCount wraps.
        int ignoreUntil = Volatile.Read(ref _ignoreNvimScrollUntil);
        if (ignoreUntil != 0 && unchecked(Environment.TickCount - ignoreUntil) < 0)
        {
            return;
        }

        var dispatcher = _dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        // Latest wins, one dispatcher hop per burst. nvim emits a report per
        // keystroke while a scroll key is held, and every applied report
        // costs a full view layout; only the last position can be visible.
        Volatile.Write(ref _pendingScrollTop, topLine);
        if (Interlocked.Exchange(ref _scrollApplyScheduled, 1) == 1)
        {
            return;
        }

#pragma warning disable VSTHRD001
        // Fire-and-forget: nothing meaningful to do with the DispatcherOperation,
        // and the callback asserts the thread the analyzer cannot prove here.
        // KeyResponse: <C-d>, zz and the scroll that follows j off the
        // screen edge are keystroke responses, like the caret (UiPriority).
        // The delegate is allocated once: a held scroll key hops per repeat.
        var apply = _applyScrollAction ??= new Action(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Volatile.Write(ref _scrollApplyScheduled, 0);
            ApplyScroll(Volatile.Read(ref _pendingScrollTop));
        });
        _ = dispatcher.BeginInvoke(Infrastructure.UiPriority.KeyResponse, apply);
#pragma warning restore VSTHRD001
    }

    private void ApplyScroll(int topLine)
    {
        using var perf = Infrastructure.Perf.Time("ViewportSynchronizer.ApplyScroll");
        ThreadHelper.ThrowIfNotOnUIThread();

        var view = _view;
        if (view == null || view.IsClosed)
        {
            return;
        }

        var snapshot = view.TextSnapshot;
        if (snapshot.LineCount == 0)
        {
            return;
        }

        if (topLine < 0)
        {
            topLine = 0;
        }

        if (topLine >= snapshot.LineCount)
        {
            topLine = snapshot.LineCount - 1;
        }

        int previousNvimTop = _lastNvimTop;
        _lastNvimTop = topLine;

        var lines = view.TextViewLines;

        // Past nvim's end, its small topline reports are the clamp snapping
        // the view back after a wheel tick. A larger step is a deliberate
        // scroll (zz, zt/zb away from the edge, or a jump), not that clamp.
        // In particular, a one-row showcmd resize can suspend sync while
        // the caret sits at the top of the last full window; zz must still
        // be able to move it back into nvim's range.
        if (Volatile.Read(ref _syncSuspended) == 1)
        {
            if (previousNvimTop < 0 || lines == null || lines.Count == 0)
            {
                return;
            }

            int low = Math.Min(previousNvimTop, topLine);
            int high = Math.Max(previousNvimTop, topLine);
            int delta = FoldsIn(view, low, high).RowsBetween(previousNvimTop, topLine);
            if (delta >= -2 && delta <= 2)
            {
                return;
            }
        }

        // Stale one-line reports from before an amplified edge jump.
        int guardUntil = Volatile.Read(ref _edgeJumpGuardUntil);
        if (guardUntil != 0 && unchecked(Environment.TickCount - guardUntil) < 0)
        {
            return;
        }

        // Our own echo. The value was sent to nvim a moment ago; Visual
        // Studio has since scrolled past it, and applying the stale copy
        // would drag the view back. Consumed so a later, genuine nvim
        // scroll to the same line is not also swallowed.
        lock (_sentEchoes)
        {
            for (int i = 0; i < _sentEchoes.Count; i++)
            {
                if (_sentEchoes[i].Key == topLine)
                {
                    _sentEchoes.RemoveAt(i);
                    return;
                }
            }
        }

        int reported = topLine;

        // nvim scrolled only to keep its cursor in its own window: Visual
        // Studio decides that one from its real layout (see HandleFollowScroll).
        if (HandleFollowScroll(view, lines, topLine, previousNvimTop))
        {
            return;
        }

        // A one-line scroll that just follows the caret off the edge is
        // turned into a half-screen jump, so holding j/k costs one scroll
        // per half window instead of one per line.
        bool amplified = AmplifyEdgeScroll(view, lines, ref topLine);

        Infrastructure.Log.Key("scroll: nvim top " + reported
            + ", view top " + (lines != null && lines.Count > 0
                ? lines.FirstVisibleLine.Start.GetContainingLine().LineNumber.ToString() : "?")
            + ", cursor " + (VSNeo_ExtensionPackage.Session?.State.CursorLine ?? -1)
            + (amplified ? " -> amplified to " + topLine : " -> applied"));

        // Mid-animation the first visible line is only a waypoint, so it
        // says nothing about whether this report is already satisfied.
        bool animating = SmoothScroller.IsAnimating(view);
        if (!animating && lines != null && lines.Count > 0
            && lines.FirstVisibleLine.Start.GetContainingLine().LineNumber == topLine)
        {
            return;   // already there
        }

        // Whatever capture is still waiting to be flushed predates this
        // scroll; see _captureStale. Set before scrolling, so the layout the
        // scroll raises (immediately, or when an animation ends) captures
        // fresh state and clears it.
        Volatile.Write(ref _captureStale, 1);

        if (!amplified)
        {
            // Record it as sent before scrolling. The scroll raises LayoutChanged,
            // which captures this very topline and would push it straight back to
            // nvim - a round trip for something nvim just told us.
            _sentTop = topLine;
        }
        // An amplified topline is ours, not nvim's, and nvim has to hear it
        // at once (SendAmplifiedTopline, below). Without it nvim's window
        // stays where its one-line scroll left it, and the next edge scroll
        // reports a topline far above the view, which applies as a jump
        // backwards: the scroll bar dropping and bouncing back up while j is
        // held.

        // nvim's own scrolls animate (Neovide's scroll animation). An
        // amplified edge jump does not: it follows a caret that is still
        // moving - holding j - and a chain of half-screen glides behind it
        // would lag the caret it exists to keep on screen.
        var scroller = SmoothScroller.For(view);
        if (amplified)
        {
            scroller.Cancel();
        }
        else if (scroller.ScrollTo(topLine))
        {
            return;
        }

        var start = snapshot.GetLineFromLineNumber(topLine).Start;
        view.DisplayTextLineContainingBufferPosition(start, 0.0, ViewRelativePosition.Top);

        if (amplified)
        {
            SendAmplifiedTopline(view, topLine);
        }
    }
}
