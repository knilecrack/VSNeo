using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;
using CreationPolicy = System.ComponentModel.Composition.CreationPolicy;


namespace VSNeo_Extension.Editor;

// nvim's cursor report to the VS caret: the pending hop, the apply, and its latency.
internal sealed partial class CursorSynchronizer
{
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
    private int _lastEnabled;

    private void OnNvimCursorMoved(int line, int byteColumn)
    {
        // nvim can move its own window (a file-mark jump, a cross-file
        // <C-o>, :b, gf) while Visual Studio keeps showing its document.
        // Reports from that other buffer describe a file this view is not
        // showing - one once clamped a 454-line report onto a 68-line
        // document and read as a caret slam to end of file. Dropped until
        // the snap-back (TextViewCreationListener) restores the expected
        // buffer. The hub path and the expected path are both normalized
        // with GetFullPath.
        var expected = TextViewCreationListener.ExpectedNvimPath;
        if (expected != null)
        {
            var showing = _subscribedTo != null ? _subscribedTo.CurrentBufferPath : null;
            if (showing != null
                && !string.Equals(showing, expected, StringComparison.OrdinalIgnoreCase))
            {
                // The pending position belongs to the buffer that was on
                // screen; with nvim elsewhere it is stale, and the single
                // application allowed on the move into insert would slam the
                // caret onto it. Drop it with the report.
                Volatile.Write(ref _pending, -1);
                return;
            }
        }

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
            {
                Volatile.Write(ref _ignoreNvimCursorUntil, 0);
            }
            else
            {
                return;
            }
        }

        // This report's place on the wire; read inside the notification that
        // carried it, so it numbers exactly this push.
        long seq = VSNeo_ExtensionPackage.Session?.NotificationSeq ?? 0;

        Volatile.Write(ref _pending, packed);
        Volatile.Write(ref _pendingSeq, seq);
        Volatile.Write(ref _queuedTicks, Clock.ElapsedTicks);

        // The report an insert-mode correction was waiting for (see
        // ReapplyAfterEdit): the first one after the edit, while still fresh.
        long waitingFor = Interlocked.Read(ref _awaitReportAfter);
        if (waitingFor > 0 && seq > waitingFor
            && Interlocked.CompareExchange(ref _awaitReportAfter, 0, waitingFor) == waitingFor
            && unchecked(Environment.TickCount - Volatile.Read(ref _awaitDeadline)) < 0)
        {
            Volatile.Write(ref _applyOnceInInsert, 1);
        }

        if (Interlocked.Exchange(ref _applyScheduled, 1) == 1)
        {
            return;
        }

        var dispatcher = _dispatcher;
        if (dispatcher == null)
        {
            Volatile.Write(ref _applyScheduled, 0);
            return;
        }

        // Posted at KeyResponse priority rather than through
        // JoinableTaskFactory.SwitchToMainThreadAsync. An unjoined JoinableTask
        // has nobody blocking on it, so it queues behind Visual Studio's own
        // background work - measured at 373 ms average and 2954 ms worst case,
        // which is what made every motion and every mode change feel broken.
        // Input priority fixed that backlog but still sat below Render and
        // Normal: the caret waited out every layout pass and Visual Studio's
        // foreground work, 25-60 ms p50 (see UiPriority). A response to a
        // keystroke goes first.
        // VSTHRD001 recommends SwitchToMainThreadAsync precisely because it hides
        // the priority. Here the priority is the point, and it is measured.
#pragma warning disable VSTHRD001
        // The delegate is allocated once: this hop runs per caret move.
        var apply = _applyPendingAction ??= new Action(() =>
        {
            // Runs on the UI thread via the dispatcher hop; the analyzer
            // cannot prove that from inside the lambda, so assert it.
            ThreadHelper.ThrowIfNotOnUIThread();

            // Released before applying, so a position that arrives while we
            // are mid-apply schedules a fresh pass instead of being dropped.
            Volatile.Write(ref _applyScheduled, 0);
            ApplyPending();
        });
        _ = dispatcher.BeginInvoke(Infrastructure.UiPriority.KeyResponse, apply);
#pragma warning restore VSTHRD001
    }

    // RPC thread only, where OnNvimCursorMoved runs.
    private Action? _applyPendingAction;

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
    public void ReapplyAfterEdit(long editSeq = 0)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // In insert mode the correction must use nvim's cursor from *after*
        // this edit. nvim sends the edit first and the cursor push after it,
        // and the drain can run before that push has arrived - then _pending
        // still holds the cursor from before the edit. Applying it was the
        // "typed pr, the caret jumped back onto the r" bug: while keys are
        // routed through nvim (see HasUnappliedRemoteEdits) every typed letter
        // is such an edit, and each correction put the caret one letter back.
        // So when the report after the edit has not arrived, wait for it (for
        // a moment - see AwaitReportMs) instead of applying the stale one.
        // Not in replace mode: an edit that leaves nvim's cursor where it
        // was (<Del>) sends no report after it, so the wait never ended and
        // the caret stayed on the next line. The caret follows every report
        // there anyway (no apply-once gate), so applying the current one now
        // costs at most a letter's lag until the next report lands.
        var mode = VSNeo_ExtensionPackage.Session?.State.Mode ?? VimMode.Unknown;
        if (mode == VimMode.Insert
            && editSeq > 0 && Volatile.Read(ref _pendingSeq) <= editSeq)
        {
            Volatile.Write(ref _awaitDeadline, unchecked(Environment.TickCount + AwaitReportMs));
            Interlocked.Exchange(ref _awaitReportAfter, editSeq);
            return;
        }

        // An accepted nvim edit can land while Visual Studio owns the caret:
        // cw deletes its word *as* it enters insert, and characters typed in
        // the few milliseconds before the mode push arrives are fed to nvim
        // and come back through the mirror. Applying such an edit displaces
        // the caret - text removed back to the line start leaves Visual
        // Studio's caret at the start of the *next* line - and the usual
        // exactly one
        // application, same as the move into insert.
        // cursor after its own edit is authoritative, so allow exactly one
        // application, same as the move into insert.
        Volatile.Write(ref _applyOnceInInsert, 1);

        ApplyPending(measure: false);
    }

    private void ApplyPending(bool measure = true)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        using (Infrastructure.Perf.Time("CursorSynchronizer.ApplyPending"))
        {
            ApplyPendingCore(measure);
        }

        // Only nvim-driven moves close a key's round trip; a correction after
        // an edit is not the answer to a keystroke.
        if (measure)
        {
            Infrastructure.Perf.CaretLanded();
        }
    }

    private void ApplyPendingCore(bool measure)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // Only real nvim-driven moves belong in the latency figure; a correction
        // after an edit would report the time since some unrelated motion.
        if (measure)
        {
            RecordHopLatency();
        }

        var view = _activeView;
        if (view == null || view.IsClosed)
        {
            return;
        }

        // Disabled, the caret is plain Visual Studio's (OnSettingsChanged).
        if (!VSNeoSettings.Enabled)
        {
            return;
        }

        // While the mouse is down the user owns the caret. An nvim redraw
        // landing mid-drag would snap the caret back and - in normal mode -
        // clear the selection being drawn, which is exactly what made
        // mouse selection impossible.
        if (MouseIsDown)
        {
            return;
        }

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
        // Not in replace mode: every key there goes to nvim (R, gR - see the
        // key processor), so nvim's cursor leads and the caret follows it.
        var session = VSNeo_ExtensionPackage.Session;
        var mode = session == null ? VimMode.Unknown : session.State.Mode;
        if (mode == VimMode.Insert
            && Interlocked.Exchange(ref _applyOnceInInsert, 0) == 0)
        {
            return;
        }

        // Even the one allowed application stands down while text typed here
        // is still on its way to nvim: its cursor then describes a buffer
        // missing the latest keystrokes, and applying it would move the
        // caret back over them. The caret here is the typist's and is right.
        if ((mode == VimMode.Insert || mode == VimMode.Replace)
            && view.TextBuffer.Properties.TryGetProperty(typeof(BufferMirror), out BufferMirror mirror)
            && mirror.HasLocalEditsInFlight)
        {
            return;
        }

        long packed = Volatile.Read(ref _pending);
        if (packed < 0)
        {
            return;
        }

        int line = (int)(packed >> 32);
        int byteColumn = (int)(uint)packed;

        var snapshot = view.TextSnapshot;
        if (snapshot.LineCount == 0)
        {
            return;
        }

        // The mirror can lag a keystroke behind on a large file. Clamping keeps
        // a stale position from throwing rather than just landing imprecisely.
        if (line >= snapshot.LineCount)
        {
            line = snapshot.LineCount - 1;
        }

        var snapshotLine = snapshot.GetLineFromLineNumber(line);
        int column = ColumnMapper.ByteToChar(snapshotLine, byteColumn);
        if (column > snapshotLine.Length)
        {
            column = snapshotLine.Length;
        }

        var target = snapshotLine.Start + column;

        // A collapsed outlining region is invisible to nvim: the mirrored
        // buffer holds every line, so nvim's cursor walks the hidden ones one
        // by one and the caret disappears into the fold. Snap the target the
        // way Vim treats a closed fold - as one line.
        target = SnapOutOfCollapsedRegion(view, target, out bool snapped);

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

                // Not while a smooth scroll is under way: it is headed for
                // nvim's window, which always contains nvim's cursor, and
                // an instant EnsureVisible mid-flight would yank the view
                // to the caret and then back into the animation.
                if (!SmoothScroller.IsAnimating(view))
                {
                    view.Caret.EnsureVisible();
                }
            }

            // nvim still believes its cursor is inside the fold. Sending the
            // snapped position back is what lets one j cross the whole region:
            // the next motion continues from past it. Forced - the dedupe
            // cannot tell a correction apart from a repeat. Not a loop: the
            // echo reports the position the caret already holds, which the
            // equality check above absorbs. PushCaret refuses the command
            // line itself, so 'incsearch' is undisturbed.
            if (snapped)
            {
                PushCaret(target, force: true);
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
    /// If <paramref name="target"/> lies strictly inside a collapsed outlining
    /// region, snap it to the nearest visible edge in the direction of travel:
    /// just past the region when moving down, onto the fold's header line when
    /// moving up. Returns the target unchanged when there is no outlining
    /// manager, no collapsed region, or the position is already visible.
    /// </summary>
    private Microsoft.VisualStudio.Text.SnapshotPoint SnapOutOfCollapsedRegion(
        IWpfTextView view,
        Microsoft.VisualStudio.Text.SnapshotPoint target,
        out bool snapped)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        snapped = false;

        var outlining = _outliningManager;
        if (outlining == null)
        {
            return target;
        }

        bool movingDown = view.Caret.Position.BufferPosition <= target;

        // The motion's column, kept across the snap the way Vim keeps the
        // cursor column over a closed fold. Computed on the first snap:
        // with no collapsed region under the target (nearly every move)
        // the conversion below never runs.
        int byteColumn = -1;

        // The re-check skips nested or directly adjacent collapsed regions in
        // one motion. Four is a bound against a pathological layout, not a
        // real case.
        for (int guard = 0; guard < 4; guard++)
        {
            var line = target.GetContainingLine();
            Microsoft.VisualStudio.Text.SnapshotSpan? hidden = null;
            foreach (var region in outlining.GetCollapsedRegions(
                         new Microsoft.VisualStudio.Text.SnapshotSpan(target, line.End)))
            {
                // Strictly inside the hidden span: a caret on the fold header
                // or exactly on a boundary is visible already and stays put.
                var extent = region.Extent.GetSpan(target.Snapshot);
                if (extent.Start < target && target < extent.End)
                {
                    hidden = extent;
                    break;
                }
            }

            if (hidden == null)
            {
                return target;
            }

            if (byteColumn < 0)
            {
                var targetLine = target.GetContainingLine();
                byteColumn = ColumnMapper.CharToByte(
                    targetLine, target.Position - targetLine.Start.Position);
            }

            snapped = true;
            var extentSpan = hidden.Value;
            var landingLine = (movingDown ? extentSpan.End : extentSpan.Start).GetContainingLine();
            int column = ColumnMapper.ByteToChar(landingLine, byteColumn);
            if (column > landingLine.Length)
            {
                column = landingLine.Length;
            }

            target = landingLine.Start + column;

            // An extent that ends mid-line can leave the column-preserved
            // target inside the hidden span; land exactly on the boundary.
            if (extentSpan.Start < target && target < extentSpan.End)
            {
                target = movingDown ? extentSpan.End : extentSpan.Start;
            }
        }

        return target;
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
        if (queued == 0)
        {
            return;
        }

        double ms = (Clock.ElapsedTicks - queued)
                    * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        _totalMs += ms;
        if (ms > _maxMs)
        {
            _maxMs = ms;
        }

        if (++_samples < 25)
        {
            return;
        }

        Infrastructure.Log.Write(string.Format(
            "cursor hop over {0} motions: avg {1:F1} ms, max {2:F1} ms",
            _samples, _totalMs / _samples, _maxMs));

        _samples = 0;
        _totalMs = 0;
        _maxMs = 0;
    }
}
