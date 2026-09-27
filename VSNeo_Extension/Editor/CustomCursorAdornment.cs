using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>VS Code's editor.cursorStyle values.</summary>
    internal enum CursorShape { Block, BlockOutline, Line, LineThin, Underline, UnderlineThin }

    /// <summary>VS Code's editor.cursorBlinking values.</summary>
    internal enum CursorBlinking { Blink, Smooth, Phase, Expand, Solid }

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
        internal AdornmentLayerDefinition LayerDefinition = null!;

        [Import]
        internal IEditorFormatMapService FormatMapService = null!;

        // Bookkeeping only (see the TextViewCreated invariant): subscriptions,
        // nothing resolved, nothing hidden until a session says so.
        public void TextViewCreated(IWpfTextView textView)
        {
            textView.Properties.GetOrCreateSingletonProperty(
                () => new CustomCursorAdornment(textView, FormatMapService));
        }
    }

    internal sealed class CustomCursorAdornment
    {
        private const string LayerName = "VSNeoCursor";

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

        public CustomCursorAdornment(IWpfTextView view, IEditorFormatMapService formatMapService)
        {
            _view = view;
            _formatMapService = formatMapService;

            view.Caret.PositionChanged += OnCaretMoved;
            view.LayoutChanged += OnLayoutChanged;
            view.GotAggregateFocus += OnFocusChanged;
            view.LostAggregateFocus += OnFocusChanged;
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
            var session = VSNeo_ExtensionPackage.Session;
            if (!_readyHooked)
            {
                // Also the "session went away" signal, which must restore the
                // real caret - so hooked for the view's whole life.
                VSNeo_ExtensionPackage.SessionReadyChanged += OnSessionReady;
                _readyHooked = true;
            }
            if (session == null || ReferenceEquals(_subscribedTo, session.State)) return;

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
        private void OnModeChanged(VimMode mode) => Post(() => Update(restartBlink: true));
        private void OnStyleChanged() => Post(() => Update(restartBlink: true));

        private void Post(Action action)
        {
            if (_closed) return;
            var dispatcher = _view.VisualElement.Dispatcher;
            if (dispatcher == null) return;
#pragma warning disable VSTHRD001
            // Input priority, as everywhere else in the extension: a mode flip
            // that repaints half a second late reads as a wrong cursor.
            _ = dispatcher.BeginInvoke(DispatcherPriority.Input, action);
#pragma warning restore VSTHRD001
        }

        // ---- view events (UI thread) ----------------------------------------

        private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e) => Update(restartBlink: true);
        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => Update(restartBlink: false);
        private void OnFocusChanged(object sender, EventArgs e) => Update(restartBlink: true);

        // ---- the one place that decides what is on screen -------------------

        private void Update(bool restartBlink)
        {
            if (_closed) return;

            try
            {
                var session = VSNeo_ExtensionPackage.Session;
                var state = session?.State;
                if (_failed || session == null || !session.IsReady || state == null
                    || !state.CursorStyleEnabled)
                {
                    Deactivate();
                    return;
                }

                // Asserted every time, not only on activation: nothing in the
                // editor is known to unhide the caret, but if anything does,
                // the next event puts it back rather than showing two cursors.
                if (!_view.Caret.IsHidden) _view.Caret.IsHidden = true;
                _active = true;
                EnsureFormatMap();
                _currentColor = ColorFor(state.Mode, state);

                if (!_view.HasAggregateFocus || state.Mode == VimMode.Visual)
                {
                    // Unfocused: Visual Studio draws no caret there either.
                    // Visual: VisualBlockCaretAdornment is the cursor.
                    HideShape();
                    return;
                }

                // Mid-layout the caret's line is not readable; LayoutChanged
                // follows and calls back in.
                if (_view.InLayout) return;

                if (!TryGetCell(out var cell, out double caretLeft))
                {
                    HideShape();
                    return;
                }

                var shape = ShapeFor(state.Mode, state);
                // The glow is a blur; on a software renderer that is a CPU
                // convolution on every repaint of the cursor (every blink
                // frame included).
                int glow = state.CursorGlow > 0
                           && Infrastructure.RenderTier.ReduceEffects(_view.VisualElement)
                    ? 0
                    : state.CursorGlow;
                Place(shape, cell, caretLeft, _currentColor, glow);
                if (restartBlink) RestartBlink(state);
            }
            catch (Exception ex)
            {
                // Trip for this view: the real caret back, permanently. A
                // cursor that throws on every caret move is worse than a plain
                // one.
                _failed = true;
                Infrastructure.Log.Write("custom cursor failed; restoring Visual Studio's caret", ex);
                Deactivate();
            }
        }

        private void Deactivate()
        {
            StopBlink();
            HideShape();
            if (!_active) return;
            _active = false;
            try { _view.Caret.IsHidden = false; }
            catch { /* the view is going away; nothing left to restore */ }
        }

        /// <summary>
        /// The caret's character cell in view coordinates: the text line's
        /// height, and the width of the character under the caret (a tab is
        /// wide, an end-of-line or virtual-space position uses one column).
        /// </summary>
        private bool TryGetCell(out Rect cell, out double caretLeft)
        {
            cell = Rect.Empty;
            var caret = _view.Caret;
            caretLeft = caret.Left;

            ITextViewLine line;
            try { line = caret.ContainingTextViewLine; }
            catch (InvalidOperationException) { return false; }

            if (line == null || line.VisibilityState == VisibilityState.Unattached
                || line.VisibilityState == VisibilityState.Hidden)
                return false;

            double columnWidth = _view.FormattedLineSource?.ColumnWidth ?? 8;
            double left = caret.Left;
            double width = columnWidth;

            var position = caret.Position.BufferPosition;
            if (!caret.InVirtualSpace && position.Position < line.End.Position)
            {
                var bounds = line.GetCharacterBounds(position);
                if (bounds.Width > 0)
                {
                    left = bounds.Left;
                    width = bounds.Width;
                }
            }

            double top = line.TextTop;
            double height = line.TextHeight;
            if (double.IsNaN(left) || double.IsNaN(top) || height <= 0) return false;

            cell = new Rect(left, top, Math.Max(width, 1), height);
            return true;
        }

        private void Place(CursorShape kind, Rect cell, double caretLeft, Color color, int glow)
        {
            var shape = EnsureShape();
            if (_solid == null || color != _brushColor)
            {
                // Rebuilt only when the theme's caret color changes, not per
                // keystroke.
                _brushColor = color;
                _solid = Frozen(Color.FromRgb(color.R, color.G, color.B));
                _translucent = Frozen(Color.FromArgb(0xA0, color.R, color.G, color.B));
            }
            var solid = _solid;

            // Bars sit between characters (the caret's own x); blocks and
            // underlines cover the character.
            double underline = Math.Max(2, Math.Round(cell.Height * 0.12));
            Rect rect;
            shape.Fill = solid;
            shape.Stroke = null;
            shape.StrokeThickness = 0;

            switch (kind)
            {
                case CursorShape.BlockOutline:
                    rect = cell;
                    shape.Fill = null;
                    shape.Stroke = solid;
                    shape.StrokeThickness = 1.5;
                    break;
                case CursorShape.Line:
                    rect = new Rect(caretLeft, cell.Top, 2, cell.Height);
                    break;
                case CursorShape.LineThin:
                    rect = new Rect(caretLeft, cell.Top, 1, cell.Height);
                    break;
                case CursorShape.Underline:
                    rect = new Rect(cell.Left, cell.Bottom - underline, cell.Width, underline);
                    break;
                case CursorShape.UnderlineThin:
                    rect = new Rect(cell.Left, cell.Bottom - 1, cell.Width, 1);
                    break;
                default:
                    // Translucent, so the character under it stays readable -
                    // the editor cannot re-draw that glyph inverted for us.
                    rect = cell;
                    shape.Fill = _translucent;
                    break;
            }

            // Neon: a zero-depth drop shadow is a soft halo in the cursor's
            // own color. One small element, so the blur is cheap even while
            // a blink animation redraws it.
            if (glow > 0)
            {
                if (_glow == null || _glow.Color != color || _glow.BlurRadius != glow)
                    _glow = new DropShadowEffect
                    {
                        Color = color,
                        BlurRadius = glow,
                        ShadowDepth = 0,
                        Opacity = 0.95,
                        RenderingBias = RenderingBias.Performance,
                    };
                shape.Effect = _glow;
            }
            else if (shape.Effect != null)
            {
                shape.Effect = null;
            }

            Canvas.SetLeft(shape, rect.Left);
            Canvas.SetTop(shape, rect.Top);
            shape.Width = rect.Width;
            shape.Height = rect.Height;
            // "expand" squeezes toward the shape's own vertical centre.
            _scale.CenterY = rect.Height / 2;
            shape.Visibility = Visibility.Visible;
            _drawn = rect;
        }

        private void HideShape()
        {
            _drawn = Rect.Empty;
            if (_shape != null) _shape.Visibility = Visibility.Collapsed;
        }

        private Rectangle EnsureShape()
        {
            if (_shape != null) return _shape;

            _layer ??= _view.GetAdornmentLayer(LayerName);
            EnsureFormatMap();

            _shape = new Rectangle
            {
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
                RenderTransform = _scale,
                Visibility = Visibility.Collapsed,
            };
            _layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, _shape, null);
            return _shape;
        }

        private void EnsureFormatMap()
        {
            if (_formatMap != null) return;
            _formatMap = _formatMapService.GetEditorFormatMap(_view);
            _formatMap.FormatMappingChanged += OnFormatMappingChanged;
        }

        private void OnFormatMappingChanged(object sender, FormatItemsEventArgs e) => Update(restartBlink: false);

        /// <summary>
        /// vsneo_cursor_color for this mode, or the theme's caret color when
        /// the rc left it unset (-1 on the wire).
        /// </summary>
        private Color ColorFor(VimMode mode, NvimStateHub state)
        {
            var colors = state.CursorColors;
            int index = ModeIndex(mode);
            int rgb = index < colors.Length ? colors[index] : -1;
            if (rgb < 0) return ReadCaretColor(_formatMap);
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        // ---- blinking ---------------------------------------------------------

        /// <summary>
        /// Solid while moving, blinking once it rests: every caret move stops
        /// the blink and re-arms it one blink interval later, as Visual Studio
        /// and VS Code both do.
        /// </summary>
        private void RestartBlink(NvimStateHub state)
        {
            StopBlink();

            var blinking = BlinkingOf(state);
            int interval = CaretBlinkMs();
            if (blinking == CursorBlinking.Solid || interval <= 0) return;

            _blinkDelay ??= new DispatcherTimer(DispatcherPriority.Normal, _view.VisualElement.Dispatcher);
            _blinkDelay.Tick -= OnBlinkDelay;
            _blinkDelay.Tick += OnBlinkDelay;
            _blinkDelay.Interval = TimeSpan.FromMilliseconds(interval);
            _blinkDelay.Start();
        }

        private void OnBlinkDelay(object sender, EventArgs e)
        {
            _blinkDelay?.Stop();
            if (_closed || _shape == null || _shape.Visibility != Visibility.Visible) return;

            var state = VSNeo_ExtensionPackage.Session?.State;
            if (state == null) return;
            var blinking = BlinkingOf(state);

            // The smooth styles are motion; with Windows animations off they
            // fall back to a plain blink, which is what the system caret does.
            // Software rendering too: ten seconds at 60 fps of a repainted
            // cursor is CPU work there, and a frame stream over Remote Desktop.
            if (blinking != CursorBlinking.Blink
                && (!SystemParameters.ClientAreaAnimation
                    || Infrastructure.RenderTier.ReduceEffects(_view.VisualElement)))
                blinking = CursorBlinking.Blink;

            switch (blinking)
            {
                case CursorBlinking.Blink:
                    _blinkTimer ??= new DispatcherTimer(DispatcherPriority.Normal, _view.VisualElement.Dispatcher);
                    _blinkTimer.Tick -= OnBlinkTick;
                    _blinkTimer.Tick += OnBlinkTick;
                    _blinkTimer.Interval = TimeSpan.FromMilliseconds(CaretBlinkMs());
                    _shape.Opacity = 0;
                    _blinkTimer.Start();
                    break;

                case CursorBlinking.Smooth:
                    // VS Code: opacity 1 for 0-20%, 0 from 60%, ease-in-out.
                    _shape.BeginAnimation(UIElement.OpacityProperty, Pulse(0.2, 0.6));
                    break;

                case CursorBlinking.Phase:
                    // VS Code: opacity 1 for 0-20%, 0 from 90%.
                    _shape.BeginAnimation(UIElement.OpacityProperty, Pulse(0.2, 0.9));
                    break;

                case CursorBlinking.Expand:
                    // VS Code: scaleY 1 for 0-20%, 0 from 80% - the cursor
                    // collapses into its vertical centre and grows back.
                    _scale.BeginAnimation(ScaleTransform.ScaleYProperty, Pulse(0.2, 0.8));
                    break;
            }
        }

        private void OnBlinkTick(object sender, EventArgs e)
        {
            if (_shape == null) return;
            _shape.Opacity = _shape.Opacity > 0.5 ? 0 : 1;
        }

        /// <summary>
        /// One VS Code blink keyframe set: 1 until <paramref name="holdUntil"/>,
        /// eased down to 0 by <paramref name="goneFrom"/>, over a 0.5 s half
        /// cycle, auto-reversed - ten full cycles, then it rests at 1. The
        /// resting end matters: an idle editor is not redrawn 60 times a second
        /// forever for a blinking cursor.
        /// </summary>
        private static DoubleAnimationUsingKeyFrames Pulse(double holdUntil, double goneFrom)
        {
            const double half = 0.5;
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var animation = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(half),
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(10),
                FillBehavior = FillBehavior.HoldEnd,
            };
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(half * holdUntil))));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(half * goneFrom)), ease));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(half))));
            animation.Freeze();
            return animation;
        }

        private void StopBlink()
        {
            _blinkDelay?.Stop();
            _blinkTimer?.Stop();
            if (_shape != null)
            {
                // A null animation hands the property back to its base value.
                _shape.BeginAnimation(UIElement.OpacityProperty, null);
                _shape.Opacity = 1;
            }
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _scale.ScaleY = 1;
        }

        // ---- settings -------------------------------------------------------

        // Parsed once per pushed string array; the hub hands back the same
        // instances until the companion sends new settings.
        private static string[]? _parsedFrom;
        private static CursorShape[] _parsedShapes = new CursorShape[6];
        private static string? _parsedBlinkFrom;
        private static CursorBlinking _parsedBlink;

        private static CursorShape ShapeFor(VimMode mode, NvimStateHub state)
        {
            var styles = state.CursorStyles;
            if (!ReferenceEquals(styles, _parsedFrom))
            {
                var parsed = new CursorShape[6];
                for (int i = 0; i < parsed.Length; i++)
                    parsed[i] = ParseShape(i < styles.Length ? styles[i] : null);
                _parsedShapes = parsed;
                _parsedFrom = styles;
            }

            return _parsedShapes[ModeIndex(mode)];
        }

        /// <summary>
        /// The per-mode slot used by both vsneo_cursor_style and
        /// vsneo_cursor_color: normal, insert, replace, visual,
        /// operator-pending, cmdline.
        /// </summary>
        private static int ModeIndex(VimMode mode)
        {
            switch (mode)
            {
                case VimMode.Insert: return 1;
                case VimMode.Replace: return 2;
                case VimMode.Visual: return 3;
                case VimMode.OperatorPending: return 4;
                case VimMode.CmdLine: return 5;
                default: return 0;
            }
        }

        private static CursorBlinking BlinkingOf(NvimStateHub state)
        {
            var text = state.CursorBlinking;
            if (!ReferenceEquals(text, _parsedBlinkFrom))
            {
                _parsedBlink = ParseBlinking(text);
                _parsedBlinkFrom = text;
            }
            return _parsedBlink;
        }

        internal static CursorShape ParseShape(string? text)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "block-outline":
                case "outline":
                case "hollow":
                    return CursorShape.BlockOutline;
                case "line":
                case "bar":
                    return CursorShape.Line;
                case "line-thin":
                    return CursorShape.LineThin;
                case "underline":
                    return CursorShape.Underline;
                case "underline-thin":
                    return CursorShape.UnderlineThin;
                default:
                    return CursorShape.Block;
            }
        }

        internal static CursorBlinking ParseBlinking(string? text)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "smooth": return CursorBlinking.Smooth;
                case "phase": return CursorBlinking.Phase;
                case "expand": return CursorBlinking.Expand;
                case "solid": return CursorBlinking.Solid;
                default: return CursorBlinking.Blink;
            }
        }

        [DllImport("user32.dll")]
        private static extern uint GetCaretBlinkTime();

        /// <summary>
        /// The system caret blink interval (Control Panel's cursor blink rate),
        /// 0 when the user switched blinking off.
        /// </summary>
        private static int CaretBlinkMs()
        {
            try
            {
                uint ms = GetCaretBlinkTime();
                if (ms == 0 || ms == uint.MaxValue) return 0;
                return (int)Math.Min(ms, 5000);
            }
            catch
            {
                return 530;
            }
        }

        /// <summary>
        /// The theme's caret color (Tools &gt; Options &gt; Fonts and Colors,
        /// "Caret"). Shared with the cursor trail.
        /// </summary>
        internal static Color ReadCaretColor(IEditorFormatMap? formatMap)
        {
            try
            {
                var props = formatMap?.GetProperties("Caret");
                if (props != null && props.Contains(EditorFormatDefinition.ForegroundColorId)
                    && props[EditorFormatDefinition.ForegroundColorId] is Color c)
                    return c;
                if (props != null && props.Contains(EditorFormatDefinition.ForegroundBrushId)
                    && props[EditorFormatDefinition.ForegroundBrushId] is SolidColorBrush b)
                    return b.Color;
            }
            catch
            {
                // Fall through to gray: a format map hiccup is not worth more.
            }
            return Colors.Gray;
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_closed) return;
            Deactivate();
            _closed = true;

            _view.Caret.PositionChanged -= OnCaretMoved;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.GotAggregateFocus -= OnFocusChanged;
            _view.LostAggregateFocus -= OnFocusChanged;
            _view.Closed -= OnClosed;
            if (_readyHooked) VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;
            if (_subscribedTo != null)
            {
                _subscribedTo.ModeChanged -= OnModeChanged;
                _subscribedTo.CursorStyleChanged -= OnStyleChanged;
            }
            if (_formatMap != null) _formatMap.FormatMappingChanged -= OnFormatMappingChanged;
        }
    }
}
