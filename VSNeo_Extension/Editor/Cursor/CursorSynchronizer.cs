using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
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
                Log.Write("caret push rejected (" + n + " so far)", ex.GetBaseException());
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
