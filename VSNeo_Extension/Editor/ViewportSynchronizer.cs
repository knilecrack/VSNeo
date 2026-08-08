using System;
using System.ComponentModel.Composition;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
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
        private IWpfTextView _view;              // UI thread only
        private System.Windows.Threading.Dispatcher _dispatcher;
        private Nvim.NvimStateHub _subscribedTo;
        private readonly Timer _debounce;

        // Captured on the UI thread during layout, sent from the timer. Reading view
        // state needs the UI thread; sending does not.
        private int _pendingHeight;
        private int _pendingWidth;
        private int _pendingTop;
        private int _pendingCaretLine;

        private int _sentHeight = -1;
        private int _sentWidth = -1;
        private int _sentTop = -1;

        /// <summary>
        /// Layout fires per scrolled line, and a flick of the wheel is dozens of
        /// them. Coalescing means one resize and one topline per gesture.
        /// </summary>
        private const int DebounceMs = 40;

        public ViewportSynchronizer()
        {
            _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void SetActiveView(IWpfTextView view)
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
            Capture();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => Capture();

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
            var dispatcher = _dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => ApplyScroll(topLine)));
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

            _pendingHeight = lines.Count;
            _pendingWidth = EstimateWidth(view);
            _pendingTop = lines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
            _pendingCaretLine = view.Caret.Position.BufferPosition
                                    .GetContainingLine().LineNumber;

            try { _debounce.Change(DebounceMs, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
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

            // An nvim window always contains its own cursor, so a topline that would
            // scroll the cursor out of view is simply refused - measured: asking for
            // topline 100 with the cursor on line 5 leaves it at 1. Visual Studio has
            // no such rule, and scrolling with the wheel there does not move the
            // caret, so the two genuinely cannot agree once you have scrolled the
            // caret off screen. Sending it anyway would be traffic nvim discards.
            int caret = Volatile.Read(ref _pendingCaretLine);
            bool caretVisible = caret >= top && caret < top + height;

            if (top != _sentTop && caretVisible)
            {
                _sentTop = top;

                // winrestview wants a 1-based line, and setting only topline leaves
                // the cursor where it is.
                Observe(session.RequestAsync(
                    "nvim_exec_lua",
                    "local t = ...; vim.fn.winrestview({ topline = t })",
                    new object[] { top + 1 }));
            }
        }

        /// <summary>
        /// A viewport update can reference a line nvim has not been sent yet, or a
        /// window that has gone away. Both are self-correcting on the next layout;
        /// what must not happen is an unobserved task fault.
        /// </summary>
        private static void Observe(System.Threading.Tasks.Task task) =>
             task.ContinueWith(
                t => { _ = t.Exception; },
                CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted,
                System.Threading.Tasks.TaskScheduler.Default);
    }
}
