using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;

namespace VSNeo_Extension.Editor;

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
internal sealed partial class ViewportSynchronizer
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

    // Set when note_viewport answered 'deferred' (replace, visual,
    // operator-pending or command-line mode, where moving nvim's window
    // could drag its cursor). The topline then counts as unsent, and the
    // return to Normal or Insert flushes it - nothing else would: a wheel
    // scroll in R left nvim's window behind, and H/M/L and nvim's next
    // scroll report worked from the old one.
    private int _deferred;

    // Set when an nvim scroll is applied, cleared by the next Capture. The
    // captured view state waits DebounceMs before Flush sends it, and a
    // capture taken just before nvim jumped (the pending 'g' of gg
    // appearing in the old message margin was enough of a layout) describes the
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
    /// small nvim-to-VS scrolls are suspended. Deliberate multi-line
    /// scrolls still apply, so zz and jumps are not swallowed at the
    /// file-edge boundary.
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

    // RPC thread only, where OnNvimScrolled runs.
    private Action? _applyScrollAction;

    public ViewportSynchronizer()
    {
        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void SetActiveView(IWpfTextView? view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_view == view)
        {
            return;
        }

        if (_view != null)
        {
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
        }

        _view = view;
        if (view == null)
        {
            return;
        }

        _dispatcher = view.VisualElement.Dispatcher;
        try
        { _outlining = _outliningService?.GetOutliningManager(view); }
        catch { _outlining = null; }

        var session = VSNeo_ExtensionPackage.Session;
        if (session != null && !ReferenceEquals(_subscribedTo, session.State))
        {
            if (_subscribedTo != null)
            {
                _subscribedTo.ViewportScrolled -= OnNvimScrolled;
                _subscribedTo.ModeChanged -= OnNvimModeChanged;
            }

            session.State.ViewportScrolled += OnNvimScrolled;
            session.State.ModeChanged += OnNvimModeChanged;
            _subscribedTo = session.State;
        }

        view.LayoutChanged += OnLayoutChanged;
        view.Caret.PositionChanged += OnCaretPositionChanged;

        // Once per view, not per activation: A -> B -> A used to add another
        // Closed handler each time and never remove one.
        if (!view.Properties.ContainsProperty(typeof(ViewportSynchronizer)))
        {
            view.Properties[typeof(ViewportSynchronizer)] = true;
            view.Closed += (s, e) => { if (ReferenceEquals(_view, view)) { SetActiveView(null); } };
        }

        // A new document is a new viewport even at identical dimensions.
        _sentHeight = _sentWidth = _sentTop = -1;
        _lastNvimTop = -1;
        Volatile.Write(ref _sentClamp, 0);
        lock (_sentEchoes)
        {
            _sentEchoes.Clear();
        }

        Volatile.Write(ref _syncSuspended, 0);
        Capture();
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
