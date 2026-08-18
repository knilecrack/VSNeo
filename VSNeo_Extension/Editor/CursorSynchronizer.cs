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
        private IWpfTextView? _activeView;         // UI thread only; null again after Detach
        private NvimStateHub _subscribedTo = null!; // UI thread only; bound by SetActiveView
        private bool _applying;                    // UI thread only
        private long _pending = -1;
        private int _applyScheduled;
        private long _lastPushed = -1;

        // Visual Studio initiated this buffer switch (Go To Definition landing in
        // another file, a tab activation). nvim answers nvim_win_set_buf by
        // restoring the view it last had for that buffer, and the companion's
        // BufEnter push reports that cursor as though it were news. Applying it
        // yanks the caret off the navigation target Visual Studio just put it on -
        // which is why gd opened the right file at the wrong line. For the settle
        // window the caret here is authoritative: any report that is not an echo
        // of the pushed caret is dropped. A matching report ends the window early;
        // the deadline ends it when nvim clamped or rejected the push and no echo
        // can match. Environment.TickCount deadline, compared wrap-safe; 0 = live.
        private int _ignoreNvimCursorUntil;

        // The stale BufEnter state arrives within a redraw or two of the switch,
        // so the window only has to outlive that - not user input.
        private const int SwitchSettleMs = 500;
        private Dispatcher _dispatcher = null!;   // captured from the active view
        private int _applyOnceInInsert;   // lets the move into insert through

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
            ThreadHelper.ThrowIfNotOnUIThread();

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
                if (_subscribedTo != null)
                {
                    _subscribedTo.CursorMoved -= OnNvimCursorMoved;
                    _subscribedTo.ModeChanged -= OnModeChanged;
                }
                session.State.CursorMoved += OnNvimCursorMoved;
                session.State.ModeChanged += OnModeChanged;
                _subscribedTo = session.State;
            }

            ApplyCaretShape(session.State.Mode);
        }

        /// <summary>
        /// Called on the RPC read thread, once per nvim redraw cycle. Records the
        /// position and schedules at most one hop to the UI thread; a burst of
        /// motions collapses into a single caret move with the latest value.
        /// </summary>
        /// <summary>Called on the RPC read thread when nvim's mode changes.</summary>
        private void OnModeChanged(VimMode mode)
        {
            if (mode == VimMode.Insert || mode == VimMode.Replace)
                Volatile.Write(ref _applyOnceInInsert, 1);

            var dispatcher = _dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            _ = dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                // Runs on the UI thread via the dispatcher hop; the analyzer cannot
                // prove that from inside the lambda, so assert it.
                ThreadHelper.ThrowIfNotOnUIThread();

                ApplyCaretShape(mode);

                // Redraw the selection on the mode change itself, not only when the
                // cursor moves. Pressing v sets the anchor and leaves the cursor
                // exactly where it is, so CursorMoved never fires and nothing would
                // appear selected until the first motion. Leaving visual is the same
                // in reverse: the selection has to be cleared.
                ApplyPending(measure: false);
            }));
#pragma warning restore VSTHRD001
        }

        /// <summary>
        /// A block caret in the modes where Vim has one, a thin caret in insert.
        ///
        /// This is not decoration, it is what makes the cursor model legible. Vim's
        /// normal-mode cursor sits *on* a character and cannot go past the last one,
        /// so the rightmost position on a line is the final character, not the empty
        /// space after it. Visual Studio's caret sits *between* characters, so nvim
        /// reporting "on the last character" drew a thin line to the left of it -
        /// looking permanently one column short, and making the end of the line
        /// impossible to reach. Same position, different convention.
        ///
        /// Overwrite mode is how the VS editor draws a block, and it covers the
        /// character the caret is on, which is exactly Vim's cursor. It also changes
        /// what typing does, which is harmless here only because normal mode does not
        /// let keystrokes through to the editor - and it is switched off before
        /// insert mode, where they do.
        /// </summary>
        private void ApplyCaretShape(VimMode mode)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var view = _activeView;
            if (view == null || view.IsClosed) return;

            bool block = mode != VimMode.Insert
                      && mode != VimMode.Replace
                      && mode != VimMode.CmdLine
                      && mode != VimMode.Unknown;

            try
            {
                view.Options.SetOptionValue(DefaultTextViewOptions.OverwriteModeId, block);
            }
            catch
            {
                // Caret shape is cosmetic; never let it break the session.
            }
        }

        /// <summary>
        /// A Visual Studio-initiated buffer switch is about to point nvim's window
        /// at another document. Until nvim echoes the caret this view already has,
        /// its cursor reports describe where that buffer was left, not a motion.
        /// </summary>
        public void BeginBufferSwitch()
        {
            // A position queued from the previous view must not apply to this one.
            Volatile.Write(ref _pending, -1);
            Volatile.Write(ref _ignoreNvimCursorUntil,
                unchecked(Environment.TickCount + SwitchSettleMs));
        }

        private void OnNvimCursorMoved(int line, int byteColumn)
        {
            long packed = ((long)line << 32) | (uint)byteColumn;

            int ignoreUntil = Volatile.Read(ref _ignoreNvimCursorUntil);
            if (ignoreUntil != 0)
            {
                // The report that ends the distrust early: nvim echoing the caret
                // Visual Studio pushed after the switch. Anything else inside the
                // window is the buffer's old cursor being reported as though it
                // were news. The subtraction is deliberate: it stays correct when
                // TickCount wraps.
                if (packed == Interlocked.Read(ref _lastPushed)
                    || unchecked(Environment.TickCount - ignoreUntil) >= 0)
                    Volatile.Write(ref _ignoreNvimCursorUntil, 0);
                else
                    return;
            }

            Volatile.Write(ref _pending, packed);
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
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() =>
                {
                    // Runs on the UI thread via the dispatcher hop; the analyzer
                    // cannot prove that from inside the lambda, so assert it.
                    ThreadHelper.ThrowIfNotOnUIThread();

                    // Released before applying, so a position that arrives while we
                    // are mid-apply schedules a fresh pass instead of being dropped.
                    Volatile.Write(ref _applyScheduled, 0);
                    ApplyPending();
                }));
#pragma warning restore VSTHRD001
        }

        /// <summary>
        /// Put the caret back where nvim has it, after something else moved it.
        ///
        /// Applying an edit displaces the caret: replacing a whole line, line break
        /// included, leaves Visual Studio's caret at the start of the *next* line.
        /// Normally nvim's own cursor move corrects that on the way through - which
        /// is why J looked fine - but an operator like x or cw leaves the cursor
        /// exactly where it was, so nvim reports no movement, nothing corrects it,
        /// and the caret is left a line down.
        /// </summary>
        public void ReapplyAfterEdit()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // An accepted nvim edit can land while Visual Studio owns the caret:
            // <C-w> is claimed in insert mode and nvim does the deleting. Applying
            // that deletion displaces the caret - text removed back to the line
            // start leaves Visual Studio's caret at the start of the *next* line -
            // and the usual insert-mode refusal in ApplyPending would strand it
            // there. nvim's cursor after its own edit is authoritative, so allow
            // exactly one application, same as the move into insert.
            Volatile.Write(ref _applyOnceInInsert, 1);

            ApplyPending(measure: false);
        }

        private void ApplyPending(bool measure = true)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Only real nvim-driven moves belong in the latency figure; a correction
            // after an edit would report the time since some unrelated motion.
            if (measure) RecordHopLatency();

            var view = _activeView;
            if (view == null || view.IsClosed) return;

            // In insert mode Visual Studio owns the caret, and nvim's is a lagging
            // echo of it. Applying that echo back fights the typist: brace
            // completion inserts "()" and places the caret between them, but the
            // span carrying that text and the caret push are separate async
            // messages, so for a moment nvim's copy is short a character or two. It
            // clamps its cursor to the text it actually has, reports the clamped
            // position in the next redraw, and we would move the caret onto whatever
            // character sits there.
            //
            // The exception is the move *into* insert. A, I, o, O, cw and s are
            // normal-mode commands that reposition the cursor and only then change
            // mode, and that reposition is nvim's to make - refusing it is what left
            // A one column short of the end of the line. Exactly one application is
            // allowed per entry into insert; after that the typist owns the caret.
            var session = VSNeo_ExtensionPackage.Session;
            var mode = session == null ? VimMode.Unknown : session.State.Mode;
            if ((mode == VimMode.Insert || mode == VimMode.Replace)
                && Interlocked.Exchange(ref _applyOnceInInsert, 0) == 0)
                return;

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

            _applying = true;
            try
            {
                // The caret is often already where nvim says, and returning early on
                // that used to skip everything below - including resetting the
                // selection. Leaving a blockwise selection does not move the cursor,
                // so Ctrl+V left the view in box mode with no way back out, and the
                // editor stayed that way for good. The selection has to be reconciled
                // on every update, whether or not the caret moved.
                if (view.Caret.Position.BufferPosition != target)
                {
                    view.Caret.MoveTo(target);
                    view.Caret.EnsureVisible();
                }

                // Contained deliberately. This runs on the dispatcher with nothing
                // above it to catch anything, so an arithmetic slip here does not
                // just lose the selection - it escapes into Visual Studio's message
                // loop. Losing a highlight is recoverable; taking the editor down
                // over one is not.
                try
                {
                    ApplySelection(view, snapshot, target, mode, session);
                }
                catch (Exception ex)
                {
                    Infrastructure.Log.Write("could not draw the visual selection", ex);
                    ResetSelection(view);
                }
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

        /// <summary>
        /// Draws nvim's visual selection in Visual Studio.
        ///
        /// The mode was already tracked, so v and V worked - they simply could not be
        /// seen, because only the cursor was ever synchronised and a selection needs
        /// both ends.
        ///
        /// Two conventions have to be reconciled. Vim's selection *includes* the
        /// character under the cursor; Visual Studio's ends where it ends, so the far
        /// end moves one character outward - and which end depends on the direction
        /// the selection was made in. And the three flavours cover different regions
        /// from the same pair of positions: charwise runs between them, linewise
        /// takes whole lines, blockwise takes the rectangle they corner.
        /// </summary>
        private void ApplySelection(IWpfTextView view, Microsoft.VisualStudio.Text.ITextSnapshot snapshot,
                                    Microsoft.VisualStudio.Text.SnapshotPoint caret,
                                    VimMode mode, NvimSession? session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // A null session means nvim is gone; ApplyPending reports the mode as
            // Unknown then, so there cannot be a visual selection to draw - the
            // same outcome the guard below would produce, reached before touching
            // session.State.
            if (session == null)
            {
                ResetSelection(view);
                return;
            }

            var state = session.State;
            int anchorLine = state.VisualAnchorLine;

            if (mode != VimMode.Visual || anchorLine < 0)
            {
                ResetSelection(view);
                return;
            }

            var anchor = PointAt(snapshot, anchorLine, state.VisualAnchorColumn);

            // Per-line secondary selections belong only to the ragged blockwise-$
            // draw; any other visual state has to shed them first, or they linger
            // beside the new selection.
            bool ragged = state.VisualKind == '\x16' && state.VisualBlockToEol;
            if (!ragged && view is ITextView2 multiView
                && multiView.MultiSelectionBroker != null
                && multiView.MultiSelectionBroker.HasMultipleSelections)
                multiView.MultiSelectionBroker.ClearSecondarySelections();

            switch (state.VisualKind)
            {
                case 'V':   // linewise: whole lines, however far along each the ends are
                {
                    int first = Math.Min(anchor.GetContainingLine().LineNumber,
                                         caret.GetContainingLine().LineNumber);
                    int last = Math.Max(anchor.GetContainingLine().LineNumber,
                                        caret.GetContainingLine().LineNumber);

                    var start = snapshot.GetLineFromLineNumber(first).Start;
                    var end = last + 1 < snapshot.LineCount
                        ? snapshot.GetLineFromLineNumber(last + 1).Start
                        : new Microsoft.VisualStudio.Text.SnapshotPoint(snapshot, snapshot.Length);

                    view.Selection.Mode = TextSelectionMode.Stream;
                    view.Selection.Select(new Microsoft.VisualStudio.Text.SnapshotSpan(start, end), false);
                    break;
                }

                case '\x16':   // blockwise: the rectangle the two corners describe
                {
                    // $ in blockwise visual is not a rectangle at all: every line
                    // runs to its own end. Drawn as one selection per line.
                    if (state.VisualBlockToEol
                        && ApplyRaggedBlock(view, snapshot, anchor, caret))
                        break;

                    // Vim's block includes the column the cursor is on, so whichever
                    // corner is on the right has to reach one column further out.
                    // Extending the caret unconditionally is wrong the moment the
                    // block is drawn leftwards or upwards from its anchor: the
                    // rightmost column is then the anchor's, and the rectangle came up
                    // one short - missing precisely the column being pointed at.
                    //
                    // Compared by column within the line, not by absolute position:
                    // the two corners are on different lines, so their offsets say
                    // nothing about which is further right.
                    int anchorColumn = anchor - anchor.GetContainingLine().Start;
                    int caretColumn = caret - caret.GetContainingLine().Start;
                    bool leftwards = caretColumn < anchorColumn;

                    view.Selection.Mode = TextSelectionMode.Box;
                    view.Selection.Select(
                        new Microsoft.VisualStudio.Text.VirtualSnapshotPoint(
                            leftwards ? Extend(snapshot, anchor) : anchor),
                        new Microsoft.VisualStudio.Text.VirtualSnapshotPoint(
                            leftwards ? caret : Extend(snapshot, caret)));
                    break;
                }

                default:    // charwise
                {
                    view.Selection.Mode = TextSelectionMode.Stream;

                    // A SnapshotSpan is always start-then-end; direction is carried
                    // by the reversed flag, not by the order of the points. Building
                    // it the other way round throws, which is what o did - swapping
                    // the ends of a selection puts the anchor after the cursor, and
                    // the exception escaped into the dispatcher.
                    bool reversed = anchor > caret;

                    // Whichever end trails is the one that grows, so the character
                    // under the cursor is included either way.
                    var start = reversed ? caret : anchor;
                    var end = reversed ? Extend(snapshot, anchor) : Extend(snapshot, caret);

                    view.Selection.Select(
                        new Microsoft.VisualStudio.Text.SnapshotSpan(start, end), reversed);
                    break;
                }
            }
        }

        /// <summary>
        /// Draws blockwise-$: the block runs from its left edge to the end of
        /// each line, ragged, which no two-corner rectangle can express. The
        /// multi-selection broker can: one selection per line. Lines shorter
        /// than the block's left edge are not in the block, matching Vim.
        ///
        /// Returns false when the broker is unavailable, so the caller can fall
        /// back to the rectangle - approximately right beats not drawn.
        /// </summary>
        private static bool ApplyRaggedBlock(
            IWpfTextView view,
            Microsoft.VisualStudio.Text.ITextSnapshot snapshot,
            Microsoft.VisualStudio.Text.SnapshotPoint anchor,
            Microsoft.VisualStudio.Text.SnapshotPoint caret)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var broker = (view as ITextView2)?.MultiSelectionBroker;
            if (broker == null) return false;

            int anchorColumn = anchor - anchor.GetContainingLine().Start;
            int caretColumn = caret - caret.GetContainingLine().Start;
            int left = Math.Min(anchorColumn, caretColumn);

            int caretLine = caret.GetContainingLine().LineNumber;
            int first = Math.Min(anchor.GetContainingLine().LineNumber, caretLine);
            int last = Math.Max(anchor.GetContainingLine().LineNumber, caretLine);

            var selections = new System.Collections.Generic.List<Microsoft.VisualStudio.Text.Selection>(last - first + 1);
            Microsoft.VisualStudio.Text.Selection? primary = null;
            for (int i = first; i <= last; i++)
            {
                var line = snapshot.GetLineFromLineNumber(i);
                if (left > line.Length) continue;

                var selection = new Microsoft.VisualStudio.Text.Selection(
                    new Microsoft.VisualStudio.Text.SnapshotSpan(line.Start + left, line.End), false);
                selections.Add(selection);
                if (i == caretLine) primary = selection;
            }

            // Every line shorter than the left edge, or the caret sitting on a
            // skipped line: nothing sensible to draw, and the broker's state
            // must not outlive the block either way.
            if (selections.Count == 0)
            {
                broker.ClearSecondarySelections();
                view.Selection.Clear();
                return true;
            }

            view.Selection.Mode = TextSelectionMode.Stream;
            broker.SetSelectionRange(selections, primary ?? selections[selections.Count - 1]);
            return true;
        }

        /// <summary>
        /// Back to no selection and stream mode.
        ///
        /// Clearing the selection is not enough on its own: box mode is a property of
        /// the view that outlives the selection it was set for, and a view left in it
        /// behaves like a permanently held Alt - which is how Ctrl+V made the editor
        /// unusable rather than merely mis-highlighted.
        /// </summary>
        private static void ResetSelection(IWpfTextView view)
        {
            try
            {
                if (!view.Selection.IsEmpty) view.Selection.Clear();
                if (view.Selection.Mode != TextSelectionMode.Stream)
                    view.Selection.Mode = TextSelectionMode.Stream;

                // A ragged blockwise-$ block leaves per-line selections behind;
                // they are not part of Selection.Clear().
                if (view is ITextView2 view2
                    && view2.MultiSelectionBroker != null
                    && view2.MultiSelectionBroker.HasMultipleSelections)
                    view2.MultiSelectionBroker.ClearSecondarySelections();
            }
            catch
            {
                // Never let tidying up be the thing that throws.
            }
        }

        private static Microsoft.VisualStudio.Text.SnapshotPoint PointAt(
            Microsoft.VisualStudio.Text.ITextSnapshot snapshot, int line, int byteColumn)
        {
            if (line >= snapshot.LineCount) line = snapshot.LineCount - 1;
            if (line < 0) line = 0;

            var snapshotLine = snapshot.GetLineFromLineNumber(line);
            int column = ColumnMapper.ByteToChar(snapshotLine.GetText(), Math.Max(0, byteColumn));
            if (column > snapshotLine.Length) column = snapshotLine.Length;
            return snapshotLine.Start + column;
        }

        /// <summary>One character further on, without running off the line.</summary>
        private static Microsoft.VisualStudio.Text.SnapshotPoint Extend(
            Microsoft.VisualStudio.Text.ITextSnapshot snapshot,
            Microsoft.VisualStudio.Text.SnapshotPoint point)
        {
            var line = point.GetContainingLine();
            return point.Position < line.End.Position ? point + 1 : point;
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
                        // OnlyOnFaulted guarantees the task faulted, so Exception
                        // cannot be null here.
                        Infrastructure.Log.Write(
                            "caret push rejected (" + n + " so far)", t.Exception!.GetBaseException());
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

            // Leave the view as we found it. Overwrite mode is ours only while nvim
            // is driving this view, and a stray block caret on a detached view would
            // also mean stray overwrite typing - as would a view left in box
            // selection, which behaves like a permanently held Alt.
            try { _activeView.Options.SetOptionValue(DefaultTextViewOptions.OverwriteModeId, false); }
            catch { }
            ResetSelection(_activeView);
            _activeView.Caret.PositionChanged -= OnCaretPositionChanged;
            _activeView.Closed -= OnViewClosed;
            _activeView = null;
        }
    }
}
