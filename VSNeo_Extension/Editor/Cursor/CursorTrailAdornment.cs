using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

/// <summary>
/// Neovide-style cursor trail: when the caret jumps, a quad smears from
/// where it was to where it is, its leading edge arriving first and its
/// trailing edge catching up.
///
/// Visual Studio's own caret is never touched - it moves instantly, as it
/// always did, and stays authoritative. The trail is a translucent overlay
/// drawn beneath it that exists only while it is moving and disappears on
/// arrival. Nothing here is on the key path: the caret event only raises a
/// flag and asks for frames, and the frames themselves (CompositionTarget
/// .Rendering) are subscribed only while something is animating.
///
/// The animation is Neovide's: each corner of the cursor quad is its own
/// critically damped spring toward the matching corner of the destination.
/// Corners facing the direction of travel get a short animation, trailing
/// ones the full length, and the difference is the smear. Configured from
/// ~/.vsneorc with Neovide's names under a vsneo_ prefix
/// (vsneo_cursor_animation_length, vsneo_cursor_trail_size,
/// vsneo_cursor_animate_in_insert_mode); off when Windows animations are.
/// </summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class CursorTrailAdornmentProvider : IWpfTextViewCreationListener
{
    [Export(typeof(AdornmentLayerDefinition))]
    [Name("VSNeoCursorTrail")]
    [Order(After = PredefinedAdornmentLayers.Text, Before = PredefinedAdornmentLayers.Caret)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    // MEF populates this through the Export above; nothing in code assigns it.
    internal AdornmentLayerDefinition _layerDefinition = null!;

    [Import]
    internal IEditorFormatMapService _formatMapService = null!;

    // Bookkeeping only (see the TextViewCreated invariant): the adornment
    // subscribes to view events and defers everything else - the format
    // map included - to the first frame it actually draws.
    public void TextViewCreated(IWpfTextView textView)
    {
        textView.Properties.GetOrCreateSingletonProperty(
            () => new CursorTrailAdornment(textView, _formatMapService));
    }
}


internal sealed partial class CursorTrailAdornment
{
    // Where the quad is and how it moves. Corners are ordered top-left,
    // top-right, bottom-right, bottom-left, viewport-relative (see
    // TrailMotion).
    private readonly TrailMotion _motion = new();

    private readonly IWpfTextView _view;
    private readonly IEditorFormatMapService _formatMapService;
    private IAdornmentLayer? _layer;
    private Polygon? _shape;
    private Effects.CursorEffectHost? _effects;
    private IEditorFormatMap? _formatMap;

    private bool _caretMoved;    // a real move since the last frame
    private bool _rendering;     // subscribed to CompositionTarget.Rendering
    private bool _closed;
    private TimeSpan _lastFrame;

    // Effect triggers other than a jump, queued here and fired from the
    // next frame, where the cursor cell is readable: a typed character,
    // the editor gaining focus, a Vim mode change.
    private bool _typedPending;
    private bool _focusPending;
    private bool _modePending;
    private VimMode _modeFrom;
    private VimMode _modeTo;
    private VimMode _lastMode;
    private NvimStateHub? _subscribedTo;
    private bool _readyHooked;

    // Set from the focus events and read on the RPC thread: a view without
    // focus posts nothing per mode change (it drew no effect for one
    // anyway, but queued a UI-thread item ahead of the caret's to find
    // that out). Focus gain resyncs _lastMode so the first change after it
    // starts from the right mode.
    private volatile bool _focused;

    public CursorTrailAdornment(IWpfTextView view, IEditorFormatMapService formatMapService)
    {
        _view = view;
        _formatMapService = formatMapService;
        _focused = view.HasAggregateFocus;

        view.Caret.PositionChanged += OnCaretMoved;
        view.LayoutChanged += OnLayoutChanged;
        view.LostAggregateFocus += OnLostFocus;
        view.GotAggregateFocus += OnGotFocus;
        view.Closed += OnClosed;
        Subscribe();
    }

    // ---- mode changes, for the effects that react to them ----------------

    private void Subscribe()
    {
        // Posted from the ready broadcast: the view can close before the
        // post runs, and subscribing then held it for the whole session.
        if (_closed)
        {
            return;
        }

        if (!_readyHooked)
        {
            VSNeo_ExtensionPackage.SessionReadyChanged += OnSessionReady;
            _readyHooked = true;
        }

        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || ReferenceEquals(_subscribedTo, session.State))
        {
            return;
        }

        if (_subscribedTo != null)
        {
            _subscribedTo.ModeChanged -= OnModeChanged;
        }

        session.State.ModeChanged += OnModeChanged;
        _subscribedTo = session.State;
        _lastMode = session.State.Mode;
    }

    private void OnSessionReady(bool ready) => Post(Subscribe);

    // RPC thread.
    private void OnModeChanged(VimMode mode)
    {
        if (_focused)
        {
            Post(() => ApplyModeChange(mode));
        }
    }

    private void ApplyModeChange(VimMode mode)
    {
        var from = _lastMode;
        _lastMode = mode;
        if (from == mode || _closed || !_view.HasAggregateFocus)
        {
            return;
        }

        var state = State;
        if (!Enabled(state) || !EffectsConfigured(state!))
        {
            return;
        }

        if (!_modePending)
        {
            _modeFrom = from;   // a burst keeps its first origin
        }

        _modeTo = mode;
        _modePending = true;
        StartFrames();
    }

    private void Post(Action action)
    {
        if (_closed)
        {
            return;
        }

        var dispatcher = _view.VisualElement.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }
#pragma warning disable VSTHRD001
        _ = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, action);
#pragma warning restore VSTHRD001
    }

    private void OnGotFocus(object sender, EventArgs e)
    {
        _focused = true;
        if (_closed)
        {
            return;
        }

        var state = State;
        if (state != null)
        {
            _lastMode = state.Mode;
        }

        if (!Enabled(state) || !EffectsConfigured(state!))
        {
            return;
        }

        _focusPending = true;
        StartFrames();
    }

    private static NvimStateHub? State => VSNeo_ExtensionPackage.Session?.State;

    /// <summary>
    /// Off with no session (plain Visual Studio behaves as plain Visual
    /// Studio), with neither a trail length nor a VFX mode, and when
    /// Windows animations are off - "Show animations in Windows" is an
    /// accessibility switch, and a sliding cursor is exactly the motion it
    /// exists to stop.
    /// </summary>
    private static bool Enabled(NvimStateHub? state) =>
        state != null
        && (state.CursorAnimationMs > 0 || EffectsConfigured(state))
        && SystemParameters.ClientAreaAnimation;

    // Parsed once per distinct string: the hub hands back the same
    // instance until the companion pushes new settings.
    private static string? s_parsedModesText;
    private static bool s_parsedAny;

    /// <summary>Does vsneo_cursor_vfx_mode name any registered effect?</summary>
    private static bool EffectsConfigured(NvimStateHub state)
    {
        var text = state.CursorVfxModes;
        if (!ReferenceEquals(text, s_parsedModesText))
        {
            s_parsedAny = Effects.CursorEffectRegistry.ParseNames(text).Count > 0;
            s_parsedModesText = text;
        }

        return s_parsedAny;
    }

    private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        var state = State;
        if (!Enabled(state) || !_view.HasAggregateFocus)
        {
            // Costs nothing while off: no frames, just forget the old
            // spot, so the first move after turning the trail on (or
            // focusing the view) snaps instead of animating from it.
            _motion.Forget();
            _caretMoved = false;
            Settle(hide: true);
            _effects?.Clear();
            StopFrames();
            return;
        }

        // PositionChanged also fires when an edit elsewhere re-snapshots
        // the caret without moving it. Only a new line or column is a move.
        var oldPoint = e.OldPosition.BufferPosition;
        var newPoint = e.NewPosition.BufferPosition;
        var oldLine = oldPoint.GetContainingLine();
        var newLine = newPoint.GetContainingLine();
        bool sameLine = oldLine.LineNumber == newLine.LineNumber;

        // A typed character: insert/replace mode, the caret one or two
        // columns further along the same line. Queued for the effects
        // (sparks) whether or not the trail animates in insert mode.
        if (sameLine && (state!.Mode == VimMode.Insert || state.Mode == VimMode.Replace)
            && EffectsConfigured(state))
        {
            int advance = (newPoint.Position - newLine.Start.Position)
                          - (oldPoint.Position - oldLine.Start.Position);
            if (advance >= 1 && advance <= 2)
            {
                _typedPending = true;
                StartFrames();
            }
        }

        if (sameLine && oldPoint.Position - oldLine.Start.Position
                        == newPoint.Position - newLine.Start.Position)
        {
            RequestSnap();
            return;
        }

        // Visual mode animates toward VisualBlockCaretAdornment's block
        // (see TryGetCaretCorners), not the caret at the selection's
        // exclusive end. The command line never moves the text caret.
        var mode = state!.Mode;
        if (mode == VimMode.CmdLine)
        {
            RequestSnap();
            return;
        }

        if ((mode == VimMode.Insert || mode == VimMode.Replace)
            && !state.CursorAnimateInInsert)
        {
            RequestSnap();
            return;
        }

        _caretMoved = true;
        StartFrames();
    }

    /// <summary>
    /// The per-view instance the creation listener made, if any.
    /// </summary>
    public static CursorTrailAdornment? For(ITextView view) =>
        view.Properties.TryGetProperty(typeof(CursorTrailAdornment), out CursorTrailAdornment t)
            ? t
            : null;

    /// <summary>
    /// VisualBlockCaretAdornment moved the visual-mode cursor block. UI
    /// thread, from inside CursorSynchronizer's selection apply - the block
    /// is already in place, so the next frame aims at it.
    /// </summary>
    public void OnVisualCursorMoved()
    {
        if (_closed || !_motion.HasPosition)
        {
            return;
        }

        var state = State;
        if (!Enabled(state) || !_view.HasAggregateFocus)
        {
            return;
        }

        if (state!.Mode != VimMode.Visual)
        {
            return;
        }

        _caretMoved = true;
        StartFrames();
    }

    /// <summary>
    /// A scroll or an edit moves the caret on screen without the caret
    /// moving. Visual Studio scrolls instantly, so a cursor sliding after
    /// the text would look detached from it: the trail snaps instead -
    /// unless this layout is the scroll half of a real jump, in which case
    /// _caretMoved is already set and the frame animates.
    /// </summary>
    private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
    {
        using var perf = Infrastructure.Perf.Time("CursorTrailAdornment.OnLayoutChanged");
        if (_closed || !_motion.HasPosition)
        {
            return;
        }

        RequestSnap();
    }

    private void OnLostFocus(object sender, EventArgs e)
    {
        _focused = false;
        _caretMoved = false;
        Settle(hide: true);
        _effects?.Clear();
        StopFrames();
    }

    private void RequestSnap()
    {
        if (_caretMoved)
        {
            return; // a pending animation already covers it
        }

        StartFrames();
    }

    private void OnClosed(object sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        StopFrames();

        _view.Caret.PositionChanged -= OnCaretMoved;
        _view.LayoutChanged -= OnLayoutChanged;
        _view.LostAggregateFocus -= OnLostFocus;
        _view.GotAggregateFocus -= OnGotFocus;
        _view.Closed -= OnClosed;
        if (_formatMap != null)
        {
            _formatMap.FormatMappingChanged -= OnFormatMappingChanged;
        }

        if (_readyHooked)
        {
            VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;
        }

        if (_subscribedTo != null)
        {
            _subscribedTo.ModeChanged -= OnModeChanged;
        }
    }
}
