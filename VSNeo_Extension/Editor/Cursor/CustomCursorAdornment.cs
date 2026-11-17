using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

/// <summary>
/// A cursor drawn by VSNeo instead of Visual Studio: per-mode shapes
/// (block, hollow block, bar, underline) and VS Code's blink styles,
/// including "expand", which squeezes the cursor to its vertical centre and
/// back. Opt-in from ~/.vsneorc (vsneo_cursor_style / vsneo_cursor_blinking);
/// with neither set this class does nothing and Visual Studio's caret is
/// untouched.
///
/// When on, Visual Studio's caret is hidden (ITextCaret.IsHidden) - it
/// still exists, moves and owns every position, it is just not painted -
/// and a shape in an adornment layer is drawn over the caret's cell. Every
/// path that turns the feature off, loses the session or fails puts the
/// real caret back first: a hidden caret with nothing drawn in its place
/// is the one outcome this must never leave behind.
///
/// Without any of those settings it still steps in at one kind of
/// position: normal and operator-pending mode with the caret at the end of
/// a line - an empty line, or past the last character. Visual Studio's
/// block caret is overwrite mode, which covers the next character, and
/// with no character there it shrinks to a bar: insert mode's shape,
/// shown in normal mode, and pressing Escape to "leave insert" did
/// nothing. There the real caret is hidden and a one-column block drawn
/// in its place, blinking like the caret; everywhere else Visual Studio's
/// caret is untouched, as before.
///
/// Visual mode draws nothing: VisualBlockCaretAdornment already marks
/// Vim's cursor there, and Visual Studio's caret (now hidden) sat at the
/// selection's exclusive end, one character off it.
///
/// Cost: shape updates ride the caret and layout events; blinking is a
/// half-second DispatcherTimer (like Visual Studio's own caret) or, for the
/// smooth styles, a WPF animation that runs ten seconds after the cursor
/// stops and then holds solid - VS Code's own power-saving rule.
/// </summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class CustomCursorAdornmentProvider : IWpfTextViewCreationListener
{
    [Export(typeof(AdornmentLayerDefinition))]
    [Name("VSNeoCursor")]
    [Order(After = "VSNeoCursorTrail", Before = PredefinedAdornmentLayers.Caret)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    // MEF populates this through the Export above; nothing in code assigns it.
    internal AdornmentLayerDefinition _layerDefinition = null!;

    [Import]
    internal IEditorFormatMapService _formatMapService = null!;

    // Bookkeeping only (see the TextViewCreated invariant): subscriptions,
    // nothing resolved, nothing hidden until a session says so.
    public void TextViewCreated(IWpfTextView textView)
    {
        textView.Properties.GetOrCreateSingletonProperty(
            () => new CustomCursorAdornment(textView, _formatMapService));
    }
}

internal sealed partial class CustomCursorAdornment
{
    private readonly IWpfTextView _view;
    private readonly IEditorFormatMapService _formatMapService;
    private IAdornmentLayer? _layer;
    private IEditorFormatMap? _formatMap;
    private Rectangle? _shape;
    private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
    private DispatcherTimer? _blinkDelay;   // solid for a moment after each move
    private DispatcherTimer? _blinkTimer;   // the on/off blink itself

    private NvimStateHub? _subscribedTo;
    private bool _readyHooked;
    private bool _active;     // we hid Visual Studio's caret
    private bool _failed;     // tripped: this view keeps the real caret for good
    private bool _closed;
    private Rect _drawn = Rect.Empty;   // view coordinates, empty when nothing is shown
    private Color _brushColor;
    private SolidColorBrush? _solid;
    private SolidColorBrush? _translucent;
    private DropShadowEffect? _glow;
    private Color _currentColor;

    // Set from the focus events themselves (HasAggregateFocus is not
    // reliable inside them) and read on the RPC thread: a view without
    // focus posts nothing per mode change - every open document used to
    // queue its own UI-thread item for each one, ahead of the caret's.
    // Focus gain catches up through Update, which reads the live mode.
    private volatile bool _focused;

    public CustomCursorAdornment(IWpfTextView view, IEditorFormatMapService formatMapService)
    {
        _view = view;
        _formatMapService = formatMapService;
        _focused = view.HasAggregateFocus;

        view.Caret.PositionChanged += OnCaretMoved;
        view.LayoutChanged += OnLayoutChanged;
        view.GotAggregateFocus += OnGotFocus;
        view.LostAggregateFocus += OnLostFocus;
        view.Closed += OnClosed;

        Subscribe();
    }

    /// <summary>The per-view instance, if the creation listener made one.</summary>
    public static CustomCursorAdornment? For(ITextView view) =>
        view.Properties.TryGetProperty(typeof(CustomCursorAdornment), out CustomCursorAdornment c)
            ? c
            : null;

    /// <summary>
    /// The shape currently on screen, in view coordinates - the cursor trail
    /// aims at this, so a hollow block or an underline gets a trail of its
    /// own shape rather than the hidden caret's.
    /// </summary>
    public bool TryGetDrawnRect(out Rect rect)
    {
        rect = _drawn;
        return _active && !_drawn.IsEmpty;
    }

    /// <summary>
    /// The color the cursor is drawn in for the current mode, while the
    /// custom cursor is on - the trail and the particles take theirs from
    /// here, so a neon cursor leaves a neon trail.
    /// </summary>
    public Color? CurrentColor => _active ? _currentColor : (Color?)null;

    // ---- session wiring -------------------------------------------------

    private void Subscribe()
    {
        // Posted from the ready broadcast: the view can close before the
        // post runs, and subscribing then held it for the whole session.
        if (_closed)
        {
            return;
        }

        var session = VSNeo_ExtensionPackage.Session;
        if (!_readyHooked)
        {
            // Also the "session went away" signal, which must restore the
            // real caret - so hooked for the view's whole life.
            VSNeo_ExtensionPackage.SessionReadyChanged += OnSessionReady;
            _readyHooked = true;
        }

        if (session == null || ReferenceEquals(_subscribedTo, session.State))
        {
            return;
        }

        if (_subscribedTo != null)
        {
            _subscribedTo.ModeChanged -= OnModeChanged;
            _subscribedTo.CursorStyleChanged -= OnStyleChanged;
        }

        session.State.ModeChanged += OnModeChanged;
        session.State.CursorStyleChanged += OnStyleChanged;
        _subscribedTo = session.State;
    }

    // Hub and session events arrive on the RPC thread.
    private void OnSessionReady(bool ready) => Post(() => { Subscribe(); Update(restartBlink: true); });
    private void OnModeChanged(VimMode mode)
    {
        if (_focused)
        {
            Post(() => Update(restartBlink: true));
        }
    }
    // Focused views only, like OnModeChanged: a style push arrives for
    // every sourced script, and Update re-reads the style on focus gain.
    private void OnStyleChanged()
    {
        if (_focused)
        {
            Post(() => Update(restartBlink: true));
        }
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
        // Input priority, as everywhere else in the extension: a mode flip
        // that repaints half a second late reads as a wrong cursor.
        _ = dispatcher.BeginInvoke(DispatcherPriority.Input, action);
#pragma warning restore VSTHRD001
    }

    // ---- view events (UI thread) ----------------------------------------

    private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e) => Update(restartBlink: true);
    private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => Update(restartBlink: false);
    private void OnGotFocus(object sender, EventArgs e)
    {
        _focused = true;
        Update(restartBlink: true);
    }

    private void OnLostFocus(object sender, EventArgs e)
    {
        _focused = false;
        Update(restartBlink: true);
    }

    // ---- the one place that decides what is on screen -------------------

    private void Deactivate()
    {
        StopBlink();
        HideShape();
        if (!_active)
        {
            return;
        }

        _active = false;
        try
        { _view.Caret.IsHidden = false; }
        catch { /* the view is going away; nothing left to restore */ }
    }

    private void OnClosed(object sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }

        Deactivate();
        _closed = true;

        _view.Caret.PositionChanged -= OnCaretMoved;
        _view.LayoutChanged -= OnLayoutChanged;
        _view.GotAggregateFocus -= OnGotFocus;
        _view.LostAggregateFocus -= OnLostFocus;
        _view.Closed -= OnClosed;
        if (_readyHooked)
        {
            VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;
        }

        if (_subscribedTo != null)
        {
            _subscribedTo.ModeChanged -= OnModeChanged;
            _subscribedTo.CursorStyleChanged -= OnStyleChanged;
        }

        if (_formatMap != null)
        {
            _formatMap.FormatMappingChanged -= OnFormatMappingChanged;
        }
    }
}
