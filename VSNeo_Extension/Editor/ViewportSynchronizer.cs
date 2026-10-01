using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Outlining;
using VSNeo_Extension.Infrastructure;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Keeps nvim's idea of the window the same size, and looking at the same part
    /// of the file, as the editor you are actually reading.
    ///
    /// nvim_ui_attach was hardcoded to 200x60, and a surprising number of everyday
    /// motions are defined in terms of the window rather than the buffer. &lt;C-d&gt;
    /// scrolls half a window - thirty lines, whatever the real height. &lt;C-f&gt;
    /// scrolls a full one. H, M and L mean the top, middle and bottom *visible*
    /// line, and zz, zt and zb reposition the current line within the visible
    /// region. All of them were computing against a window nobody was looking at.
    ///
    /// Two things have to agree for those to work: the height, via
    /// nvim_ui_try_resize, and the first visible line, via winrestview. Height alone
    /// fixes the scroll amounts; the topline is what makes H, M, L and zz land where
    /// you can see.
    /// </summary>
    [Export(typeof(ViewportSynchronizer))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class ViewportSynchronizer
    {
        // All three are null until the first SetActiveView, and every read
        // below already null-checks for exactly that state.
        private IWpfTextView? _view;              // UI thread only
        private System.Windows.Threading.Dispatcher? _dispatcher;
        private Nvim.NvimStateHub? _subscribedTo;
        private readonly Timer _debounce;

        // Captured on the UI thread during layout, sent from the timer. Reading view
        // state needs the UI thread; sending does not.
        private int _pendingHeight;
        private int _pendingWidth;
        private int _pendingTop;
        private int _pendingCaretLine;
        private int _pendingCaretCol;
        private int _pendingCaretVisible;
        private int _pendingLineCount;
        private int _pendingPastEnd;

        // Null only when MEF could not satisfy the import; row counting then
        // treats every line as its own row, the pre-fold-aware behavior.
        [Import(AllowDefault = true)]
        private IOutliningManagerService? _outliningService = null;
        private IOutliningManager? _outlining;   // UI thread; per active view

        private int _sentHeight = -1;
        private int _sentWidth = -1;
        private int _sentTop = -1;

        // The same buffer switch CursorSynchronizer.BeginBufferSwitch covers, one
        // line further down: nvim's BufEnter push also carries the topline it last
        // had for the buffer, and applying it scrolls the navigation target off
        // screen. The echo ring cannot catch it - Visual Studio never sent that
        // value - so scroll reports are dropped outright until the switch has
        // settled. Environment.TickCount deadline, compared wrap-safe; 0 = live.
        private int _ignoreNvimScrollUntil;

        // Set when the last note_viewport sent carried a caret outside the window,
        // i.e. the companion clamped nvim's cursor to the window edge. The clamp
        // is by design while the caret is genuinely scrolled off screen, but the
        // pair can also be captured mid-flight: a far jump (%, gd) applies the
        // scroll before the caret move lands, and a Flush in between - forced by
        // a resize, which resets _sentTop - ships the new topline with the OLD
        // caret. nvim's cursor is yanked off the jump target, flagged synthetic so
        // the caret is never told, and the next % or motion then runs from the
        // wrong place. While this is set and the caret is back inside the window,
        // Flush re-sends so nvim's cursor rejoins the caret.
        private int _sentClamp;

        // Set when an nvim scroll is applied, cleared by the next Capture. The
        // captured view state waits DebounceMs before Flush sends it, and a
        // capture taken just before nvim jumped (the pending 'g' of gg
        // appearing in the message margin is enough of a layout) describes the
        // view nvim has since left. Applying the jump then moves _sentTop, so
        // Flush sees a topline change and ships the stale capture - and
        // note_viewport puts nvim's window AND cursor back where they were. A
        // smooth scroll makes it certain: layouts are not captured while it
        // animates, so nothing overwrites the stale capture before the timer
        // fires. Observed as 'gg' then a quick 'j' landing one line below the
        // pre-gg position. The capture after the scroll (the animation's final
        // layout) is fresh and clears this.
        private int _captureStale;

        /// <summary>
        /// A Visual Studio-initiated buffer switch is about to point nvim's window
        /// at another document. Until it settles, nvim's scroll reports describe
        /// where that buffer was left, not a scroll.
        /// </summary>
        public void BeginBufferSwitch() =>
            Volatile.Write(ref _ignoreNvimScrollUntil, unchecked(Environment.TickCount + 500));

        /// <summary>
        /// Environment.TickCount deadline, compared wrap-safe; 0 = live. After an
        /// edge scroll is amplified into a half-screen jump, nvim still has
        /// one-line scroll reports in flight from keystrokes it processed before
        /// our note_viewport arrived (holding j through the edge produces several).
        /// Each is stale the moment the jump lands, but none of them matches the
        /// echo ring - Visual Studio never sent those values - so without this
        /// window they apply as full scrolls and yank the view back, one line per
        /// queued report. Genuine nvim scrolls inside the window (zz right after
        /// the jump) are dropped too; that is rare and self-corrects on the next
        /// layout.
        /// </summary>
        private int _edgeJumpGuardUntil;

        // nvim's topline as last reported, echoes and guarded reports included:
        // the step between two reports is what identifies a follow-scroll.
        // UI thread; -1 until the first report for the active view.
        private int _lastNvimTop = -1;
        private const int EdgeJumpGuardMs = 250;

        /// <summary>
        /// Toplines we recently pushed to nvim, with timestamps. nvim answers
        /// every winrestview with a WinScrolled report of the same value, and
        /// during a long wheel gesture that echo arrives *after* Visual Studio
        /// has already scrolled on - applying it yanks the view backwards,
        /// which is the up-and-down fight of continuous scrolling. Values nvim
        /// produced on its own (zz, zt, zb, &lt;C-e&gt;) are never in this list,
        /// so matching here is exactly "this report is our own echo: drop it".
        /// </summary>
        private readonly List<KeyValuePair<int, long>> _sentEchoes =
            new List<KeyValuePair<int, long>>();
        private const long EchoWindowTicks = TimeSpan.TicksPerSecond * 2;

        /// <summary>
        /// The one state nvim's window cannot represent, where applying its
        /// topline reports is the snap-back: Visual Studio scrolled past the
        /// end of the file. nvim clamps its topline to lineCount - height, so
        /// a winrestview beyond that comes back as the clamp rather than what
        /// was sent - matching nothing in the echo ring - and applying it
        /// yanks the view back up on every wheel tick. While this is set,
        /// nvim-to-VS scroll is suspended. The cost: zz and friends do
        /// nothing visible until the view is back in range, which is the
        /// moment sync resumes.
        ///
        /// The caret scrolled off screen is NOT such a state: the companion
        /// clamps nvim's cursor into the window instead (see note_viewport in
        /// vsneo.lua), so the window keeps matching the viewport and H/M/L
        /// keep aiming at what is on screen.
        /// </summary>
        private int _syncSuspended;

        /// <summary>
        /// Layout fires per scrolled line, and a flick of the wheel is dozens of
        /// them. Coalescing means one resize and one topline per gesture.
        /// </summary>
        private const int DebounceMs = 40;

        public ViewportSynchronizer()
        {
            _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void SetActiveView(IWpfTextView? view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_view == view) return;

            if (_view != null)
            {
                _view.LayoutChanged -= OnLayoutChanged;
                _view.Caret.PositionChanged -= OnCaretPositionChanged;
            }
            _view = view;
            if (view == null) return;

            _dispatcher = view.VisualElement.Dispatcher;
            try { _outlining = _outliningService?.GetOutliningManager(view); }
            catch { _outlining = null; }

            var session = VSNeo_ExtensionPackage.Session;
            if (session != null && !ReferenceEquals(_subscribedTo, session.State))
            {
                if (_subscribedTo != null) _subscribedTo.ViewportScrolled -= OnNvimScrolled;
                session.State.ViewportScrolled += OnNvimScrolled;
                _subscribedTo = session.State;
            }

            view.LayoutChanged += OnLayoutChanged;
            view.Caret.PositionChanged += OnCaretPositionChanged;

            // Once per view, not per activation: A -> B -> A used to add another
            // Closed handler each time and never remove one.
            if (!view.Properties.ContainsProperty(typeof(ViewportSynchronizer)))
            {
                view.Properties[typeof(ViewportSynchronizer)] = true;
                view.Closed += (s, e) => { if (ReferenceEquals(_view, view)) SetActiveView(null); };
            }

            // A new document is a new viewport even at identical dimensions.
            _sentHeight = _sentWidth = _sentTop = -1;
            _lastNvimTop = -1;
            Volatile.Write(ref _sentClamp, 0);
            lock (_sentEchoes) _sentEchoes.Clear();
            Volatile.Write(ref _syncSuspended, 0);
            Capture();
        }

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

            try { _debounce.Change(DebounceMs, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            using var perf = Infrastructure.Perf.Time("ViewportSynchronizer.OnLayoutChanged");
            // LayoutChanged is raised on the UI thread; Capture reads view state.
            ThreadHelper.ThrowIfNotOnUIThread();

            // A smooth scroll lays out every frame. Its intermediate toplines
            // are not places the view is going to stay, and sending them would
            // move nvim's window (and clamp its cursor) through each one. The
            // animation's final, exact layout arrives with IsAnimating already
            // false and is captured like any other.
            if (_view != null && SmoothScroller.IsAnimating(_view)) return;

            Capture();
        }

        /// <summary>
        /// nvim scrolled; move Visual Studio to match. Called on the RPC read thread.
        ///
        /// This is the direction that was missing, and it is the whole of zz, zt, zb
        /// and the &lt;C-e&gt;/&lt;C-y&gt; pair: those commands move the window and
        /// deliberately leave the cursor alone, so a caret-only synchroniser sees
        /// nothing happen and the screen never moves.
        /// </summary>
        private int _pendingScrollTop;
        private int _scrollApplyScheduled;

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
                    return;
            }

            // Inside a Visual Studio-initiated buffer switch this report is the
            // buffer's old topline, not a scroll; applying it would drag the view
            // off the navigation target. The subtraction stays correct when
            // TickCount wraps.
            int ignoreUntil = Volatile.Read(ref _ignoreNvimScrollUntil);
            if (ignoreUntil != 0 && unchecked(Environment.TickCount - ignoreUntil) < 0) return;

            var dispatcher = _dispatcher;
            if (dispatcher == null) return;

            // Latest wins, one dispatcher hop per burst. nvim emits a report per
            // keystroke while a scroll key is held, and every applied report
            // costs a full view layout; only the last position can be visible.
            Volatile.Write(ref _pendingScrollTop, topLine);
            if (Interlocked.Exchange(ref _scrollApplyScheduled, 1) == 1) return;

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

        // RPC thread only, where OnNvimScrolled runs.
        private Action? _applyScrollAction;

        private void ApplyScroll(int topLine)
        {
            using var perf = Infrastructure.Perf.Time("ViewportSynchronizer.ApplyScroll");
            ThreadHelper.ThrowIfNotOnUIThread();

            var view = _view;
            if (view == null || view.IsClosed) return;

            var snapshot = view.TextSnapshot;
            if (snapshot.LineCount == 0) return;
            if (topLine < 0) topLine = 0;
            if (topLine >= snapshot.LineCount) topLine = snapshot.LineCount - 1;

            int previousNvimTop = _lastNvimTop;
            _lastNvimTop = topLine;

            // The view is somewhere nvim's window cannot be - scrolled past
            // the end of the file, or with the caret off screen. nvim's
            // topline reports there are the snap-back.
            if (Volatile.Read(ref _syncSuspended) == 1) return;

            // Stale one-line reports from before an amplified edge jump.
            int guardUntil = Volatile.Read(ref _edgeJumpGuardUntil);
            if (guardUntil != 0 && unchecked(Environment.TickCount - guardUntil) < 0) return;

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

            var lines = view.TextViewLines;
            int reported = topLine;

            // nvim scrolled only to keep its cursor in its own window: Visual
            // Studio decides that one from its real layout (see HandleFollowScroll).
            if (HandleFollowScroll(view, lines, topLine, previousNvimTop)) return;

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
                return;   // already there

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

            if (amplified) SendAmplifiedTopline(view, topLine);
        }

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
        /// the cursor now sits on the edge it moved toward (scrolloff is 0).
        /// That excludes &lt;C-e&gt;/&lt;C-y&gt;, which leave the cursor mid-window.
        /// Then: if the cursor's line is fully on screen, the view stays and
        /// nvim's window is moved to it; if not, the cursor is centred by
        /// Visual Studio (which lays out CodeLens and folds correctly) and nvim
        /// is sent the result. Either way nvim hears the topline at once, since
        /// the layout capture is starved while a key repeats.
        /// </summary>
        private bool HandleFollowScroll(IWpfTextView view, ITextViewLineCollection? lines,
                                        int nvimTop, int previousNvimTop)
        {
            if (previousNvimTop < 0 || lines == null || lines.Count == 0) return false;
            if (nvimTop == previousNvimTop) return false;

            var session = VSNeo_ExtensionPackage.Session;
            if (session == null) return false;
            var snapshot = view.TextSnapshot;
            int cursor = session.State.CursorLine;
            if (cursor < 0 || cursor >= snapshot.LineCount) return false;

            int nvimHeight = Volatile.Read(ref _sentHeight);
            if (nvimHeight <= 0) nvimHeight = HeightInRows(view);

            int low = Math.Min(Math.Min(previousNvimTop, nvimTop), cursor);
            int high = Math.Max(Math.Max(previousNvimTop, nvimTop), cursor);
            var folds = FoldsIn(view, low, high);

            int step = folds.RowsBetween(previousNvimTop, nvimTop);
            if (step == 0 || step < -2 || step > 2) return false;

            int rowsToCursor = folds.RowsBetween(nvimTop, cursor);
            bool follows = step > 0 ? rowsToCursor >= nvimHeight - 1 : rowsToCursor <= 0;
            if (!follows) return false;

            int currentTop = lines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
            var cursorPoint = snapshot.GetLineFromLineNumber(cursor).Start;
            var cursorLine = lines.GetTextViewLineContainingBufferPosition(cursorPoint);
            bool onScreen = cursorLine != null
                && cursorLine.VisibilityState != VisibilityState.Hidden
                && cursorLine.TextTop >= view.ViewportTop
                && cursorLine.TextBottom <= view.ViewportBottom;

            if (onScreen)
            {
                Infrastructure.Log.Key("scroll: nvim top " + nvimTop + " follows cursor " + cursor
                    + ", already on screen - view stays at " + currentTop);
                SendAmplifiedTopline(view, currentTop);
                return true;
            }

            // Off screen: centre it the way Visual Studio lays lines out.
            SmoothScroller.For(view).Cancel();
            Volatile.Write(ref _captureStale, 1);
            Volatile.Write(ref _edgeJumpGuardUntil, unchecked(Environment.TickCount + EdgeJumpGuardMs));
            view.DisplayTextLineContainingBufferPosition(
                cursorPoint, Math.Max(0, (view.ViewportHeight - view.LineHeight) / 2),
                ViewRelativePosition.Top);

            var after = view.TextViewLines;
            int newTop = after != null && after.Count > 0
                ? after.FirstVisibleLine.Start.GetContainingLine().LineNumber
                : currentTop;
            Infrastructure.Log.Key("scroll: nvim top " + nvimTop + " follows cursor " + cursor
                + " off screen - centred, view top " + currentTop + " -> " + newTop);
            SendAmplifiedTopline(view, newTop);
            return true;
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
            if (session == null || !session.IsReady) return;

            // Past the end of what nvim can show: Flush's pastEnd logic
            // suspends sync there, exactly as after a wheel scroll past the end.
            if (IsPastNvimEnd(view, topLine, HeightInRows(view))) return;

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
            if (lines == null || lines.Count == 0) return false;

            int currentTop = lines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
            if (topLine == currentTop) return false;

            var session = VSNeo_ExtensionPackage.Session;
            if (session == null) return false;

            // The hub's cursor, not the caret: the push that carried this scroll
            // also carried the cursor, but the caret here is only updated after
            // this method returns.
            int caret = session.State.CursorLine;
            if (caret < 0 || caret >= view.TextSnapshot.LineCount) return false;

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
            if (delta == 0 || delta < -2 || delta > 2) return false;

            int rowsFromTop = folds.RowsBetween(topLine, caret);
            bool atBottomEdge = delta > 0 && rowsFromTop >= height - 2;
            bool atTopEdge = delta < 0 && rowsFromTop <= 1;
            if (!atBottomEdge && !atTopEdge) return false;

            int centered = folds.LineRowsAbove(caret, height / 2);
            // Clamped to the last line, not lineCount - height: the jump may
            // deliberately scroll past nvim's range so the end of the file sits
            // mid-view. nvim cannot represent that topline, so nothing is sent
            // and Flush's pastEnd check suspends sync until the view returns.
            int maxTop = view.TextSnapshot.LineCount - 1;
            if (centered > maxTop) centered = maxTop;
            if (centered < 0) centered = 0;
            if (centered == currentTop) return false;   // clamped at a file edge

            Volatile.Write(ref _edgeJumpGuardUntil, unchecked(Environment.TickCount + EdgeJumpGuardMs));
            topLine = centered;
            return true;
        }

        /// <summary>
        /// The collapsed regions touching lines [first, last], as screen-row
        /// arithmetic (see FoldRows). UI thread. Empty without an outlining
        /// manager, or when nothing is collapsed.
        /// </summary>
        private FoldRows FoldsIn(IWpfTextView view, int first, int last)
        {
            var outlining = _outlining;
            if (outlining == null) return FoldRows.None;

            var snapshot = view.TextSnapshot;
            if (snapshot.LineCount == 0) return FoldRows.None;
            first = Math.Max(0, first);
            last = Math.Min(snapshot.LineCount - 1, last);
            if (last < first) return FoldRows.None;

            try
            {
                var span = new SnapshotSpan(
                    snapshot.GetLineFromLineNumber(first).Start,
                    snapshot.GetLineFromLineNumber(last).EndIncludingLineBreak);

                List<KeyValuePair<int, int>>? regions = null;
                foreach (var region in outlining.GetCollapsedRegions(span))
                {
                    var extent = region.Extent.GetSpan(snapshot);
                    int start = extent.Start.GetContainingLine().LineNumber;
                    var endLine = extent.End.GetContainingLine();
                    int end = endLine.LineNumber;
                    // An extent ending at the very start of a line hides
                    // nothing of that line.
                    if (end > start && extent.End.Position == endLine.Start.Position) end--;
                    regions ??= new List<KeyValuePair<int, int>>();
                    regions.Add(new KeyValuePair<int, int>(start, end));
                }
                return regions == null ? FoldRows.None : FoldRows.FromRegions(regions);
            }
            catch (Exception ex)
            {
                // Layout arithmetic only; the line-count behavior is the fallback.
                Infrastructure.Log.Write("fold row count failed", ex);
                return FoldRows.None;
            }
        }

        /// <summary>
        /// Whether nvim can show <paramref name="top"/> as its topline: nvim
        /// clamps any topline that would leave rows of its window below the
        /// end of the file. Counted in screen rows from the topline to the last
        /// line, so a collapsed region near the end counts as one row, as it
        /// does in nvim. UI thread.
        /// </summary>
        private bool IsPastNvimEnd(IWpfTextView view, int top, int height)
        {
            int lastLine = view.TextSnapshot.LineCount - 1;
            if (lastLine - top + 1 < height) return true;   // short of rows even unfolded

            // Far from the end the answer is plainly "no"; the bound keeps the
            // region query off the capture path for most of a large file.
            if (lastLine - top > height * 50) return false;

            int rows = FoldsIn(view, top, lastLine).RowsBetween(top, lastLine) + 1;
            return rows < height;
        }

        private void Capture()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var view = _view;
            if (view == null || view.IsClosed) return;

            var lines = view.TextViewLines;
            if (lines == null || lines.Count == 0) return;

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

            try { _debounce.Change(DebounceMs, Timeout.Infinite); }
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
                    return Math.Max(1, (int)(view.ViewportHeight / lineHeight));
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
                    return Math.Max(80, Math.Min(2000, (int)(view.ViewportWidth / charWidth)));
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
            if (session == null || !session.IsReady) return;

            // A capture from before nvim's last scroll would undo it; the
            // layout after the scroll captures again and flushes then.
            if (Volatile.Read(ref _captureStale) == 1) return;

            int height = Volatile.Read(ref _pendingHeight);
            int width = Volatile.Read(ref _pendingWidth);
            int top = Volatile.Read(ref _pendingTop);

            if (height <= 0) return;

            if (height != _sentHeight || width != _sentWidth)
            {
                _sentHeight = height;
                _sentWidth = width;

                // Resizing invalidates the topline nvim was holding.
                _sentTop = -1;

                Observe(session.RequestAsync("nvim_ui_try_resize", width, height));
                Infrastructure.Log.Write("viewport resized to " + width + "x" + height);
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
                Infrastructure.Log.Write(suspended == 1
                    ? "view scrolled past end of file - nvim scroll sync suspended"
                    : "view back in nvim's range - nvim scroll sync resumed");

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
                // first g (the showcmd margin appearing resizes the view) ran
                // after gg - and put the cursor and window back where they
                // were before it. Dropped, it is re-sent from fresh state.
                var hub = session.State;
                int knownLine = hub.CursorLine, knownCol = hub.CursorColumnByte;
                var request = session.RequestAsync(
                    "nvim_exec_lua",
                    "return vsneo.note_viewport(...)",
                    new object[] { top + 1, height, caret + 1, caretCol, caretInWindow,
                                   knownLine + 1, knownCol });
                _ = request.ContinueWith(t =>
                {
                    if (t.IsFaulted) { _ = t.Exception; return; }
                    if (!(t.Result is bool applied) || applied) return;

                    Infrastructure.Log.Key("viewport: stale note_viewport dropped by nvim - resending");
                    // Forget what was sent and flush again once the state
                    // settles; the next Flush reads the hub's fresh cursor.
                    _sentTop = -1;
                    try { _debounce.Change(DebounceMs, Timeout.Infinite); }
                    catch (ObjectDisposedException) { }
                }, CancellationToken.None,
                   System.Threading.Tasks.TaskContinuationOptions.None,
                   System.Threading.Tasks.TaskScheduler.Default);
            }
        }

        /// <summary>
        /// A viewport update can reference a line nvim has not been sent yet, or a
        /// window that has gone away. Both are self-correcting on the next layout;
        /// what must not happen is an unobserved task fault.
        /// </summary>
        private static void Observe(System.Threading.Tasks.Task task) =>
             _ = task.ContinueWith(
                t => { _ = t.Exception; },
                CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted,
                System.Threading.Tasks.TaskScheduler.Default);
    }
}
