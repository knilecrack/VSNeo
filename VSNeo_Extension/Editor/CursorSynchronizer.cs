using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;
using CreationPolicy = System.ComponentModel.Composition.CreationPolicy;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Keeps the Visual Studio caret and the nvim cursor pointing at the same
    /// place, for whichever view currently has focus.
    ///
    /// nvim -> VS is the milestone 1 goal: motions have to move something visible
    /// or none of this feels real. VS -> nvim is here because without it the very
    /// first mouse click desyncs the two, and because insert mode is handled
    /// entirely by VS - on &lt;Esc&gt; nvim would otherwise resume from wherever the
    /// cursor sat when insert began.
    ///
    /// Both directions stay off the key path. Nothing here is consulted to decide
    /// swallow-vs-passthrough.
    /// </summary>
    [Export(typeof(CursorSynchronizer))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class CursorSynchronizer
    {
        private IWpfTextView _activeView;          // UI thread only
        private NvimStateHub _subscribedTo;        // UI thread only
        private bool _applying;                    // UI thread only
        private long _pending = -1;
        private int _applyScheduled;
        private long _lastPushed = -1;
        private Dispatcher _dispatcher;   // captured from the active view

        // How long a cursor position waits between nvim reporting it and the caret
        // actually moving - the UI thread hop every motion pays. Accumulated in
        // memory and reported in batches, because measuring per event with I/O
        // would cost more than the thing being measured.
        private static readonly System.Diagnostics.Stopwatch Clock =
            System.Diagnostics.Stopwatch.StartNew();
        private long _queuedTicks;
        private int _samples;
        private double _totalMs;
        private double _maxMs;

        /// <summary>Call on the UI thread when a view takes focus.</summary>
        public void SetActiveView(IWpfTextView view)
        {
            if (view == null || _activeView == view) return;

            var session = VSNeo_ExtensionPackage.Session;
            if (session == null) return;

            Detach();

            _activeView = view;
            _dispatcher = view.VisualElement.Dispatcher;
            view.Caret.PositionChanged += OnCaretPositionChanged;
            view.Closed += OnViewClosed;

            if (!ReferenceEquals(_subscribedTo, session.State))
            {
                if (_subscribedTo != null) _subscribedTo.CursorMoved -= OnNvimCursorMoved;
                session.State.CursorMoved += OnNvimCursorMoved;
                _subscribedTo = session.State;
            }
        }

        /// <summary>
        /// Called on the RPC read thread, once per nvim redraw cycle. Records the
        /// position and schedules at most one hop to the UI thread; a burst of
        /// motions collapses into a single caret move with the latest value.
        /// </summary>
        private void OnNvimCursorMoved(int line, int byteColumn)
        {
            Volatile.Write(ref _pending, ((long)line << 32) | (uint)byteColumn);
            Volatile.Write(ref _queuedTicks, Clock.ElapsedTicks);
            if (Interlocked.Exchange(ref _applyScheduled, 1) == 1) return;

            var dispatcher = _dispatcher;
            if (dispatcher == null)
            {
                Volatile.Write(ref _applyScheduled, 0);
                return;
            }

            // Posted at Input priority rather than through
            // JoinableTaskFactory.SwitchToMainThreadAsync. An unjoined JoinableTask
            // has nobody blocking on it, so it queues behind Visual Studio's own
            // background work - measured at 373 ms average and 2954 ms worst case,
            // which is what made every motion and every mode change feel broken.
            // Input priority puts the caret update ahead of that backlog, where a
            // response to a keystroke belongs.
            // VSTHRD001 recommends SwitchToMainThreadAsync precisely because it hides
            // the priority. Here the priority is the point, and it is measured: the
            // JTF route averaged 373 ms to deliver a caret move.
#pragma warning disable VSTHRD001
            dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() =>
                {
                    // Released before applying, so a position that arrives while we
                    // are mid-apply schedules a fresh pass instead of being dropped.
                    Volatile.Write(ref _applyScheduled, 0);
                    ApplyPending();
                }));
#pragma warning restore VSTHRD001
        }

        private void ApplyPending()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            RecordHopLatency();

            var view = _activeView;
            if (view == null || view.IsClosed) return;

            // In insert mode Visual Studio owns the caret, and nvim's is a lagging
            // echo of it. Applying that echo back fights the typist: brace
            // completion inserts "()" and places the caret between them, but the
            // span carrying that text and the caret push are separate async
            // messages, so for a moment nvim's copy is short a character or two. It
            // clamps its cursor to the text it actually has, reports the clamped
            // position in the next redraw, and we would move the caret onto whatever
            // character sits there. nvim only drives the caret in the modes it owns.
            var session = VSNeo_ExtensionPackage.Session;
            var mode = session == null ? VimMode.Unknown : session.State.Mode;
            if (mode == VimMode.Insert || mode == VimMode.Replace) return;

            long packed = Volatile.Read(ref _pending);
            if (packed < 0) return;

            int line = (int)(packed >> 32);
            int byteColumn = (int)(uint)packed;

            var snapshot = view.TextSnapshot;
            if (snapshot.LineCount == 0) return;

            // The mirror can lag a keystroke behind on a large file. Clamping keeps
            // a stale position from throwing rather than just landing imprecisely.
            if (line >= snapshot.LineCount) line = snapshot.LineCount - 1;

            var snapshotLine = snapshot.GetLineFromLineNumber(line);
            int column = ColumnMapper.ByteToChar(snapshotLine.GetText(), byteColumn);
            if (column > snapshotLine.Length) column = snapshotLine.Length;

            var target = snapshotLine.Start + column;
            if (view.Caret.Position.BufferPosition == target) return;

            _applying = true;
            try
            {
                view.Caret.MoveTo(target);
                view.Caret.EnsureVisible();
            }
            finally
            {
                _applying = false;
            }
        }

        /// <summary>
        /// Reports every 25 motions rather than every one. If this reads in single
        /// milliseconds the hop is not the problem and the lag is elsewhere; if it
        /// reads in tens or hundreds, the UI thread is busy and the caret is queued
        /// behind whatever else Visual Studio is doing.
        /// </summary>
        private void RecordHopLatency()
        {
            long queued = Volatile.Read(ref _queuedTicks);
            if (queued == 0) return;

            double ms = (Clock.ElapsedTicks - queued)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            _totalMs += ms;
            if (ms > _maxMs) _maxMs = ms;

            if (++_samples < 25) return;

            Infrastructure.Log.Write(string.Format(
                "cursor hop over {0} motions: avg {1:F1} ms, max {2:F1} ms",
                _samples, _totalMs / _samples, _maxMs));

            _samples = 0;
            _totalMs = 0;
            _maxMs = 0;
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            if (_applying) return; // our own move, coming back around
            PushCaret(e.NewPosition.BufferPosition);
        }

        /// <summary>
        /// Push the active view's caret into nvim. UI thread.
        ///
        /// <paramref name="force"/> bypasses the dedupe, for the one case where it
        /// would do harm: leaving insert mode. Escape makes nvim move the cursor one
        /// column left, and it computes that from its own copy - which, after a burst
        /// of typing VS handled alone, may be a character behind. The dedupe would
        /// suppress the correcting push precisely because we "already sent" that
        /// position, even though nvim clamped it on arrival and is somewhere else.
        /// </summary>
        public void SyncCaretToNvim(bool force = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = _activeView;
            if (view == null || view.IsClosed) return;
            PushCaret(view.Caret.Position.BufferPosition, force);
        }

        private void PushCaret(Microsoft.VisualStudio.Text.SnapshotPoint point, bool force = false)
        {
            var session = VSNeo_ExtensionPackage.Session;
            if (session == null || !session.IsReady) return;

            // Moving nvim's cursor mid-command-line would disturb the prompt.
            if (session.State.Mode == VimMode.CmdLine) return;

            var line = point.GetContainingLine();
            int byteColumn = ColumnMapper.CharToByte(
                line.GetText(), point.Position - line.Start.Position);

            // PositionChanged fires for layouts, scrolls and word wrap, not only for
            // real cursor movement, and it fires for the caret landing back where it
            // already was. Without this every scroll became a burst of RPC that nvim
            // answered with a redraw, which moved the caret, which fired again.
            long packed = ((long)line.LineNumber << 32) | (uint)byteColumn;
            if (Interlocked.Exchange(ref _lastPushed, packed) == packed && !force) return;

            // nvim_win_set_cursor wants a 1-based row and a 0-based byte column.
            var pos = new object[] { line.LineNumber + 1, byteColumn };
            Observe(session.RequestAsync("nvim_win_set_cursor", 0, pos));
        }

        /// <summary>
        /// The mirror is one-way and can lag, so nvim will occasionally reject a
        /// position as out of range. That is expected and self-correcting on the
        /// next redraw; what we must not do is leave the task unobserved.
        /// </summary>
        private static int _pushFailures;

        private static void Observe(Task task)
        {
            _ = task.ContinueWith(
                t =>
                {
                    // Swallowing these entirely hid a real signal: a steady stream of
                    // rejections means nvim's buffer no longer matches VS's, and every
                    // motion after that is computed against the wrong text. Logged in
                    // powers of two so a genuine desync is loud without a stuck cursor
                    // filling the file.
                    int n = Interlocked.Increment(ref _pushFailures);
                    if ((n & (n - 1)) == 0)
                        Infrastructure.Log.Write(
                            "caret push rejected (" + n + " so far)", t.Exception?.GetBaseException());
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            if (ReferenceEquals(sender, _activeView)) Detach();
        }

        private void Detach()
        {
            if (_activeView == null) return;
            _activeView.Caret.PositionChanged -= OnCaretPositionChanged;
            _activeView.Closed -= OnViewClosed;
            _activeView = null;
        }
    }
}
