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
internal sealed partial class CursorSynchronizer
{
    // Null only when MEF could not satisfy the import; fold skipping then
    // silently degrades to the pre-fold-aware behavior.
    [Import]
    private IOutliningManagerService? _outliningManagerService = null;

    // The manager is per-view and lives as long as the view; resolved when
    // the view becomes active, so the fold snap does not pay a service
    // lookup per caret move. UI thread only.
    private IOutliningManager? _outliningManager;

    // Null only when MEF could not satisfy the import; visual mode then
    // keeps the delimiter highlight beside its selection (cosmetic).
    [Import]
    private IEditorFormatMapService? _formatMapService = null;

    private IWpfTextView? _activeView;         // UI thread only; null again after Detach
    private NvimStateHub _subscribedTo = null!; // UI thread only; bound by SetActiveView
    private bool _applying;                    // UI thread only
    private bool _mouseDown;                   // UI thread only; left button held on the view

    /// <summary>
    /// UI thread. True only while the left button is genuinely held. The
    /// mouse-up event can be lost when a drag ends outside the view - the
    /// capture during a drag is Visual Studio's, not ours, and stealing it
    /// would break the editor's own drag-scrolling - so the button state,
    /// not the event pair, is ground truth.
    /// </summary>
    private bool MouseIsDown
    {
        get
        {
            if (_mouseDown && Mouse.LeftButton != MouseButtonState.Pressed)
            {
                _mouseDown = false;
            }

            return _mouseDown;
        }
    }

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

    // Wire position (NvimSession.NotificationSeq) of the cursor report in
    // _pending. RPC thread writes, UI thread reads.
    private long _pendingSeq;

    // An insert-mode correction waiting for nvim's cursor report after an
    // accepted edit (see ReapplyAfterEdit): the edit's wire position, 0 when
    // nothing waits, and a TickCount deadline after which it is dropped.
    private long _awaitReportAfter;
    private int _awaitDeadline;

    // How long a waiting correction stays valid. nvim's report follows its
    // edit within a redraw; later than this, the typist has moved on and the
    // report would only drag the caret back.
    private const int AwaitReportMs = 250;

    // Values displaced by the brace-match suppression, per format and key, so
    // leaving visual mode restores the view exactly as it was. UI thread only.
    //private readonly List<(string Format, string Key, bool Existed, object? Value)> _braceMatchSaved = new();
    private readonly List<BraceMatchSaved> _braceMatchSaved = [];

    // The names VS's delimiter highlight is known by. The format name is not
    // part of any public contract and both casings have shipped, so every
    // candidate is overridden - keys set on a name nothing reads are simply
    // never resolved.
    private static readonly string[] BraceMatchFormatNames =
    {
        "Brace Matching (Rectangle)",
        "brace matching (Rectangle)",
        "Brace Matching (Highlight)",
        "brace matching (Highlight)",
    };

    // Fill and border both go transparent: the rectangle variant draws its
    // border from the foreground, the highlight variant from the background,
    // and the rendering layer may consult either the color or the brush key.
    private static readonly KeyValuePair<string, object>[] BraceMatchOverrides =
    {
        new("BackgroundColor", Colors.Transparent),
        new("BackgroundBrush", Brushes.Transparent),
        new("ForegroundColor", Colors.Transparent),
        new("BorderBrush", Brushes.Transparent),
    };

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

    public CursorSynchronizer()
    {
        _lastEnabled = VSNeoSettings.Enabled ? 1 : 0;
        VSNeoSettings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        bool enabled = VSNeoSettings.Enabled;
        bool wasEnabled = Interlocked.Exchange(ref _lastEnabled, enabled ? 1 : 0) != 0;
        bool enableTransition = !wasEnabled && enabled;
        bool disableTransition = wasEnabled && !enabled;
        var dispatcher = _dispatcher;
        if (dispatcher == null)
        {
            return;
        }
#pragma warning disable VSTHRD001
        _ = dispatcher.BeginInvoke(UiPriority.KeyResponse, new Action(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (enabled != VSNeoSettings.Enabled)
            {
                return;
            }

            var mode = enabled
                ? VSNeo_ExtensionPackage.Session?.State.Mode ?? VimMode.Insert
                : VimMode.Insert;
            ApplyCaretShape(mode);
            UpdateBraceMatchSuppression(mode);
            // Off, neither direction runs (PushCaret, ApplyPendingCore,
            // OnMouseLeftUp), so the caret is plain Visual Studio's. Back
            // on, Visual Studio's caret is the current one: send it to nvim
            // rather than apply nvim's report from before the switch -
            // except in visual mode, where nvim owns the cursor and a
            // pushed caret would extend the selection to it: redraw nvim's
            // selection instead (the switch-off cleared it).
            if (disableTransition)
            {
                _activeView?.Selection.Clear();
            }
            else if (enableTransition)
            {
                if (mode == VimMode.Visual)
                {
                    ApplyPending(measure: false);
                }
                else
                {
                    var session = VSNeo_ExtensionPackage.Session;
                    if (mode == VimMode.OperatorPending)
                    {
                        session?.Input("<Esc>");
                    }

                    // The normal key path intentionally does not echo
                    // Visual Studio caret moves while Replace owns the
                    // cursor. Re-enabling is a handoff, so send the caret
                    // after any pending operator has been cancelled.
                    var view = _activeView;
                    if (view != null && !view.IsClosed)
                    {
                        PushCaret(view.Caret.Position.BufferPosition, force: true);
                    }
                }
            }
        }));
#pragma warning restore VSTHRD001
    }

    /// <summary>
    /// The shared instance, for the package's command handlers (MEF owns
    /// the part; the package is not composed). Set on the first activation.
    /// </summary>
    internal static CursorSynchronizer? Instance { get; private set; }

    /// <summary>The focused document view this synchronizer follows. UI thread only.</summary>
    internal IWpfTextView? ActiveView
    {
        get { ThreadHelper.ThrowIfNotOnUIThread(); return _activeView; }
    }

    /// <summary>
    /// UI thread. Runs work that moves Visual Studio's caret as bookkeeping,
    /// not as a motion - a range action selecting its range and collapsing
    /// it - without telling nvim, then puts the caret back on nvim's last
    /// reported cursor. Echoed, those moves reached nvim after it had moved
    /// on (a macro's gccj, a quick .j): the range start came back, and the
    /// next j started a line too high.
    /// </summary>
    internal void WithoutEcho(Action work)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        bool was = _applying;
        _applying = true;
        try
        { work(); }
        finally { _applying = was; }

        ApplyPending(measure: false);
    }

    /// <summary>Call on the UI thread when a view takes focus.</summary>
    public void SetActiveView(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Instance = this;

        if (view == null || _activeView == view)
        {
            return;
        }

        var session = VSNeo_ExtensionPackage.Session;
        if (session == null)
        {
            return;
        }

        Detach();

        _activeView = view;
        _dispatcher = view.VisualElement.Dispatcher;
        _outliningManager = _outliningManagerService?.GetOutliningManager(view);
        view.Caret.PositionChanged += OnCaretPositionChanged;
        view.VisualElement.PreviewMouseLeftButtonDown += OnMouseLeftDown;
        view.VisualElement.PreviewMouseLeftButtonUp += OnMouseLeftUp;
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
        UpdateBraceMatchSuppression(session.State.Mode);
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
        {
            Volatile.Write(ref _applyOnceInInsert, 1);
        }

        var dispatcher = _dispatcher;
        if (dispatcher == null)
        {
            return;
        }

#pragma warning disable VSTHRD001
        _ = dispatcher.BeginInvoke(Infrastructure.UiPriority.KeyResponse, new Action(() =>
        {
            // Runs on the UI thread via the dispatcher hop; the analyzer cannot
            // prove that from inside the lambda, so assert it.
            ThreadHelper.ThrowIfNotOnUIThread();

            ApplyCaretShape(mode);
            UpdateBraceMatchSuppression(mode);

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
    ///
    /// Visual mode is the exception: the caret sits at the selection's
    /// exclusive end, where Visual Studio renders no block at all, so the
    /// overwrite block is off there and VisualBlockCaretAdornment draws the
    /// block over nvim's cursor character instead.
    /// </summary>
    private void ApplyCaretShape(VimMode mode)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var view = _activeView;
        if (view == null || view.IsClosed)
        {
            return;
        }

        bool block = VSNeoSettings.Enabled && mode is (VimMode.Normal
                  or VimMode.OperatorPending
                  or VimMode.Terminal);

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
    /// Suppresses Visual Studio's automatic delimiter highlighting for the
    /// active view while visual mode is active, and restores it elsewhere.
    ///
    /// The caret in visual mode sits on the character under nvim's cursor
    /// (ApplySelection stops the span there so the block marks the inclusive
    /// end). When that character is a bracket, delimiter highlighting boxes
    /// it and its match - a rectangle fighting the block for the same cell,
    /// plus a second box somewhere outside the selection. Neither helps
    /// read the selection, so the highlight goes while visual mode is on.
    ///
    /// Done through the per-view format map so the user's global Fonts and
    /// Colors settings are never touched.
    /// </summary>
    private void UpdateBraceMatchSuppression(VimMode mode)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (mode != VimMode.Visual)
        {
            RestoreBraceMatchFormats();
            return;
        }

        if (_braceMatchSaved.Count != 0)
        {
            return;   // already suppressed
        }

        var view = _activeView;
        if (view == null || view.IsClosed || _formatMapService == null)
        {
            return;
        }

        try
        {
            var formatMap = _formatMapService.GetEditorFormatMap(view);
            formatMap.BeginBatchUpdate();
            try
            {
                foreach (var name in BraceMatchFormatNames)
                {
                    var properties = formatMap.GetProperties(name);
                    foreach (var pair in BraceMatchOverrides)
                    {
                        bool existed = properties.Contains(pair.Key);
                        _braceMatchSaved.Add(new BraceMatchSaved(name, pair.Key, existed, existed ? properties[pair.Key] : null));
                        properties[pair.Key] = pair.Value;
                    }
                }
            }
            finally
            {
                formatMap.EndBatchUpdate();
            }
        }
        catch
        {
            // Cosmetic only; never let it break the session.
            _braceMatchSaved.Clear();
        }
    }

    private void RestoreBraceMatchFormats()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_braceMatchSaved.Count == 0)
        {
            return;
        }

        var saved = _braceMatchSaved.ToArray();
        _braceMatchSaved.Clear();

        var view = _activeView;
        if (view == null || view.IsClosed || _formatMapService == null)
        {
            return;
        }

        try
        {
            var formatMap = _formatMapService.GetEditorFormatMap(view);
            formatMap.BeginBatchUpdate();
            try
            {
                foreach (var braceMatchSaved in saved)
                {
                    // Format, not Key: the properties belong to the format
                    // definition ("brace matching"), and looking them up by
                    // the property name created junk entries named
                    // "BackgroundColor" while the real colors stayed suppressed.
                    var properties = formatMap.GetProperties(braceMatchSaved.Format);
                    if (braceMatchSaved.Existed)
                    {
                        properties[braceMatchSaved.Key] = braceMatchSaved.Value!;
                    }
                    else
                    {
                        properties.Remove(braceMatchSaved.Key);
                    }
                }
            }
            finally
            {
                formatMap.EndBatchUpdate();
            }
        }
        catch
        {
            // Cosmetic only; never let it break the session.
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
        if (view == null || view.IsClosed)
        {
            return;
        }
        // Replace mode typed through nvim, so the caret is the one lagging.
        if (VSNeo_ExtensionPackage.Session?.State.Mode == VimMode.Replace)
        {
            return;
        }

        PushCaret(view.Caret.Position.BufferPosition, force);
    }

    /// <summary>
    /// A claimed insert-mode mapping may move nvim's cursor without
    /// leaving insert (imap &lt;C-l&gt; &lt;Right&gt;). Allow the next push
    /// through the Visual-Studio-owns-the-caret rule - the same exception
    /// the move into insert gets. One shot: the flag is consumed by the
    /// next apply either way, so a mapping that leaves insert (where the
    /// mode change sets it itself) is unaffected.
    /// </summary>
    public void AllowNextInsertApply() => Volatile.Write(ref _applyOnceInInsert, 1);

    private void PushCaret(Microsoft.VisualStudio.Text.SnapshotPoint point, bool force = false)
    {
        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || !session.IsReady || !VSNeoSettings.Enabled)
        {
            return;
        }

        // A mouse drag moves the caret continuously; the mouse-up handler
        // sends the finished selection (or the clicked position), so the
        // frames along the way are noise - and each echoed redraw would be
        // due for suppression in ApplyPending anyway.
        if (MouseIsDown)
        {
            return;
        }

        // Moving nvim's cursor mid-command-line would disturb the prompt.
        if (session.State.Mode == VimMode.CmdLine)
        {
            return;
        }

        var line = point.GetContainingLine();
        int byteColumn = ColumnMapper.CharToByte(
            line, point.Position - line.Start.Position);

        // PositionChanged fires for layouts, scrolls and word wrap, not only for
        // real cursor movement, and it fires for the caret landing back where it
        // already was. Without this every scroll became a burst of RPC that nvim
        // answered with a redraw, which moved the caret, which fired again.
        long packed = ((long)line.LineNumber << 32) | (uint)byteColumn;
        if (Interlocked.Exchange(ref _lastPushed, packed) == packed && !force)
        {
            return;
        }

        // Routed through the companion rather than raw nvim_win_set_cursor:
        // the API scrolls nvim's window to reveal the cursor when the pushed
        // caret sits outside it, and the WinScrolled report of that
        // transitional scroll used to reach us before note_viewport's
        // winrestview with the real topline - the view yanked to nvim's
        // minimal scroll on every VS-initiated jump (gd, Ctrl+-, a far
        // click), and the correction was then swallowed by the echo ring.
        // vsneo.set_cursor moves the cursor and reports the position, but
        // marks the push as carrying no scroll information. Same convention
        // as nvim_win_set_cursor: 1-based row, 0-based byte column.
        var pos = new object[] { line.LineNumber + 1, byteColumn };
        Observe(session.RequestAsync("nvim_exec_lua", "vsneo.set_cursor(...)", pos));
    }

    /// <summary>
    /// The mirror is one-way and can lag, so nvim will occasionally reject a
    /// position as out of range. That is expected and self-correcting on the
    /// next redraw; what we must not do is leave the task unobserved.
    /// </summary>
    private static int s_pushFailures;

    private static void Observe(Task task) => _ = ObserveAsync(task);

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            // An RPC reply from NvimRpcClient: completed on the thread pool,
            // never joined from the UI thread, so VSTHRD003's deadlock has
            // no path here.
#pragma warning disable VSTHRD003
            await task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        catch (Exception ex)
        {
            // Swallowing these entirely hid a real signal: a steady stream of
            // rejections means nvim's buffer no longer matches VS's, and every
            // motion after that is computed against the wrong text. Logged in
            // powers of two so a genuine desync is loud without a stuck cursor
            // filling the file.
            int n = Interlocked.Increment(ref s_pushFailures);
            if ((n & (n - 1)) == 0)
            {
                Infrastructure.Log.Write("caret push rejected (" + n + " so far)", ex.GetBaseException());
            }
        }
    }

    private void OnViewClosed(object sender, EventArgs e)
    {
        // The view's Closed event fires on the UI thread.
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ReferenceEquals(sender, _activeView))
        {
            Detach();
        }
    }

    private void Detach()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_activeView == null)
        {
            return;
        }

        // Leave the view as we found it. Overwrite mode is ours only while nvim
        // is driving this view, and a stray block caret on a detached view would
        // also mean stray overwrite typing - as would a view left in box
        // selection, which behaves like a permanently held Alt.
        try
        { _activeView.Options.SetOptionValue(DefaultTextViewOptions.OverwriteModeId, false); }
        catch { }

        RestoreBraceMatchFormats();
        VisualBlockCaretAdornment.For(_activeView)?.Hide();
        ResetSelection(_activeView);
        _activeView.Caret.PositionChanged -= OnCaretPositionChanged;
        _activeView.VisualElement.PreviewMouseLeftButtonDown -= OnMouseLeftDown;
        _activeView.VisualElement.PreviewMouseLeftButtonUp -= OnMouseLeftUp;
        _activeView.Closed -= OnViewClosed;
        _activeView = null;
        _outliningManager = null;
        _mouseDown = false;
    }

}

public readonly struct BraceMatchSaved(string format, string key, bool existed, object? value)
{
    public string Format { get; } = format;
    public string Key { get; } = key;
    public bool Existed { get; } = existed;
    public object? Value { get; } = value;
}
