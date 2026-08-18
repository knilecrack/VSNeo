using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

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
        private int _pendingLineCount;

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

        /// <summary>
        /// A Visual Studio-initiated buffer switch is about to point nvim's window
        /// at another document. Until it settles, nvim's scroll reports describe
        /// where that buffer was left, not a scroll.
        /// </summary>
        public void BeginBufferSwitch() =>
            Volatile.Write(ref _ignoreNvimScrollUntil, unchecked(Environment.TickCount + 500));

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

            if (_view != null) _view.LayoutChanged -= OnLayoutChanged;
            _view = view;
            if (view == null) return;

            _dispatcher = view.VisualElement.Dispatcher;

            var session = VSNeo_ExtensionPackage.Session;
            if (session != null && !ReferenceEquals(_subscribedTo, session.State))
            {
                if (_subscribedTo != null) _subscribedTo.ViewportScrolled -= OnNvimScrolled;
                session.State.ViewportScrolled += OnNvimScrolled;
                _subscribedTo = session.State;
            }

            view.LayoutChanged += OnLayoutChanged;
            view.Closed += (s, e) => { if (ReferenceEquals(_view, view)) SetActiveView(null); };

            // A new document is a new viewport even at identical dimensions.
            _sentHeight = _sentWidth = _sentTop = -1;
            lock (_sentEchoes) _sentEchoes.Clear();
            Volatile.Write(ref _syncSuspended, 0);
            Capture();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            // LayoutChanged is raised on the UI thread; Capture reads view state.
            ThreadHelper.ThrowIfNotOnUIThread();
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
        private void OnNvimScrolled(int topLine)
        {
            // Inside a Visual Studio-initiated buffer switch this report is the
            // buffer's old topline, not a scroll; applying it would drag the view
            // off the navigation target. The subtraction stays correct when
            // TickCount wraps.
            int ignoreUntil = Volatile.Read(ref _ignoreNvimScrollUntil);
            if (ignoreUntil != 0 && unchecked(Environment.TickCount - ignoreUntil) < 0) return;

            var dispatcher = _dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            // Fire-and-forget: nothing meaningful to do with the DispatcherOperation,
            // and the callback asserts the thread the analyzer cannot prove here.
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    ApplyScroll(topLine);
                }));
#pragma warning restore VSTHRD001
        }

        private void ApplyScroll(int topLine)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var view = _view;
            if (view == null || view.IsClosed) return;

            var snapshot = view.TextSnapshot;
            if (snapshot.LineCount == 0) return;
            if (topLine < 0) topLine = 0;
            if (topLine >= snapshot.LineCount) topLine = snapshot.LineCount - 1;

            var lines = view.TextViewLines;
            if (lines != null && lines.Count > 0
                && lines.FirstVisibleLine.Start.GetContainingLine().LineNumber == topLine)
                return;   // already there

            // The view is somewhere nvim's window cannot be - scrolled past
            // the end of the file, or with the caret off screen. nvim's
            // topline reports there are the snap-back.
            if (Volatile.Read(ref _syncSuspended) == 1) return;

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

            // Record it as sent before scrolling. The scroll raises LayoutChanged,
            // which captures this very topline and would push it straight back to
            // nvim - a round trip for something nvim just told us.
            _sentTop = topLine;

            var start = snapshot.GetLineFromLineNumber(topLine).Start;
            view.DisplayTextLineContainingBufferPosition(start, 0.0, ViewRelativePosition.Top);
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
                caretLine.GetText(), caretPos.Position - caretLine.Start.Position);
            _pendingLineCount = view.TextSnapshot.LineCount;

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
            // (flagged synthetic, so the caret here is not dragged along).
            int caret = Volatile.Read(ref _pendingCaretLine);
            int caretCol = Volatile.Read(ref _pendingCaretCol);
            int lineCount = Volatile.Read(ref _pendingLineCount);
            bool pastEnd = top > Math.Max(0, lineCount - height);

            // Logged on transitions only - this runs on a timer and the state
            // flips once per scroll gesture.
            int suspended = pastEnd ? 1 : 0;
            if (Interlocked.Exchange(ref _syncSuspended, suspended) != suspended)
                Infrastructure.Log.Write(suspended == 1
                    ? "view scrolled past end of file - nvim scroll sync suspended"
                    : "view back in nvim's range - nvim scroll sync resumed");

            if (top != _sentTop && !pastEnd)
            {
                _sentTop = top;

                lock (_sentEchoes)
                {
                    long cutoff = DateTime.UtcNow.Ticks - EchoWindowTicks;
                    _sentEchoes.RemoveAll(e => e.Value < cutoff);
                    _sentEchoes.Add(new KeyValuePair<int, long>(top, DateTime.UtcNow.Ticks));
                }

                // All lines 1-based, the column a 0-based byte offset.
                Observe(session.RequestAsync(
                    "nvim_exec_lua",
                    "vsneo.note_viewport(...)",
                    new object[] { top + 1, height, caret + 1, caretCol }));
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
