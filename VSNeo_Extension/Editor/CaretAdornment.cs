using System;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Vim's mode-dependent caret, drawn as a WPF adornment. The approach is
    /// VsVim's (Src/VimWpf/Implementation/BlockCaret/BlockCaret.cs), behaviors
    /// included:
    ///
    /// - The native caret is HIDDEN while the block is shown (Caret.IsHidden);
    ///   painting over a blinking native caret never looks right.
    /// - The block blinks with the system caret blink time, and the blink
    ///   cycle restarts on every caret move - gVim's behavior, so a caret in
    ///   motion never disappears mid-stroke.
    /// - Nothing is drawn while the view lacks aggregate focus.
    /// - The element is reused: a caret move repositions it and only a stale
    ///   one (new glyph, shape, size or colors) is rebuilt - rebuilding per
    ///   keystroke is the cost that got the naive relative-number margin
    ///   disabled.
    /// - The cell is an OPAQUE reverse-video block: the character under the
    ///   caret is redrawn in the contrasting color, like a terminal. Half and
    ///   quarter blocks get the same treatment clipped to the bar region.
    ///
    /// Shapes come from nvim's 'guicursor' when the user sets one
    /// (GuiCursor.ShapeFor: block, horNN, verNN, blinkon0 honored), else the
    /// default table: full block in normal, bottom half block in
    /// operator-pending, bottom quarter block in replace, nothing in insert
    /// (Visual Studio's thin caret is Vim's bar already) or visual
    /// (VisualBlockCaretAdornment owns the live end there). Because the block
    /// is drawn, overwrite mode is never set for looks - overwrite only ever
    /// comes from the physical Insert key, and EnsureOverwriteOff in the key
    /// processor guards that.
    ///
    /// Driven, not self-updating: CursorSynchronizer calls Show/Hide on mode
    /// changes. Between those it follows the Visual Studio caret directly -
    /// the synchronizer keeps it equal to nvim's cursor in the modes nvim
    /// owns, and in replace mode typing moves it without any push from nvim.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class CaretAdornmentProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("VSNeoCaret")]
        [Order(After = PredefinedAdornmentLayers.Text)]
        [TextViewRole(PredefinedTextViewRoles.Document)]
        // MEF populates this through the Export above; nothing in code assigns it.
        internal AdornmentLayerDefinition LayerDefinition = null!;

        [Import]
        internal IClassificationTypeRegistryService ClassificationRegistry = null!;

        [Import]
        internal IClassificationFormatMapService FormatMapService = null!;

        [Import]
        internal IEditorFormatMapService EditorFormatMapService = null!;

        public void TextViewCreated(IWpfTextView textView)
        {
            textView.Properties.GetOrCreateSingletonProperty(
                () => new CaretAdornment(
                    textView, ClassificationRegistry, FormatMapService, EditorFormatMapService));
        }
    }

    /// <summary>Tools > Options > Fonts and Colors entry for the caret colors.</summary>
    [Export(typeof(EditorFormatDefinition))]
    [Name("VSNeo Block Caret")]
    [UserVisible(true)]
    internal sealed class BlockCaretFormatDefinition : EditorFormatDefinition
    {
        public BlockCaretFormatDefinition()
        {
            DisplayName = "VSNeo Block Caret";
            BackgroundColor = Colors.White;   // the block's fill
            ForegroundColor = Colors.Black;   // the redrawn glyph
        }
    }

    internal sealed class CaretAdornment
    {
        private const string LayerName = "VSNeoCaret";
        private const string FormatName = "VSNeo Block Caret";

        private readonly IAdornmentLayer _layer;
        private readonly IWpfTextView _view;
        private readonly IClassificationFormatMapService _formatMapService;
        private readonly IEditorFormatMapService _editorFormatMapService;

        // Null while hidden; the visible state's only record.
        private ITrackingPoint? _anchor;
        private CaretShape _shape = CaretShape.FullBlock();
        private double _defaultCharWidth = -1;

        // The drawn caret, reused across moves. _drawn* is what it currently
        // shows, so a redraw can tell "move it" from "rebuild it".
        private CaretElement? _element;
        private bool _added;
        private CaretShape _drawnShape;
        private string _drawnGlyph = string.Empty;
        private double _drawnWidth = -1;
        private double _drawnHeight = -1;
        private Color _drawnFill;
        private Color _drawnText;
        private double _drawnGlyphY = -1;
        // Buffer position of the last draw. The glide animates only when this
        // changes: a scroll or a drag shifts the element's viewport-relative
        // coordinates without the caret moving in the buffer, and animating
        // THAT made the block fly around on mouse wheel and selections.
        private int _drawnPosition = -1;

        private readonly DispatcherTimer? _blinkTimer;

        public CaretAdornment(
            IWpfTextView view,
            IClassificationTypeRegistryService classificationRegistry,
            IClassificationFormatMapService formatMapService,
            IEditorFormatMapService editorFormatMapService)
        {
            _view = view;
            _formatMapService = formatMapService;
            _editorFormatMapService = editorFormatMapService;
            _layer = view.GetAdornmentLayer(LayerName);
            _blinkTimer = CreateBlinkTimer(view.VisualElement.Dispatcher, OnBlink);

            view.Caret.PositionChanged += OnCaretPositionChanged;
            view.LayoutChanged += OnLayoutChanged;
            view.GotAggregateFocus += OnFocusChanged;
            view.LostAggregateFocus += OnFocusChanged;
            view.Closed += OnClosed;
        }

        /// <summary>The per-view instance the creation listener made, if any.</summary>
        public static CaretAdornment? For(IWpfTextView view)
        {
            return view.Properties.TryGetProperty(typeof(CaretAdornment), out CaretAdornment adornment)
                ? adornment
                : null;
        }

        /// <summary>Show the caret at <paramref name="point"/>. UI thread.</summary>
        public void Show(SnapshotPoint point, CaretShape shape)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            _anchor = point.Snapshot.CreateTrackingPoint(point.Position, PointTrackingMode.Positive);
            _shape = shape;
            _view.Caret.IsHidden = true;
            if (shape.Blinks)
                RestartBlinkCycle();
            else
                _blinkTimer?.Stop();
            Redraw();
        }

        public void Hide()
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            _anchor = null;
            RemoveElement();
            _blinkTimer?.Stop();
            if (!_view.IsClosed) _view.Caret.IsHidden = false;
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            // A null anchor is the hidden state; tracking never revives it.
            if (_anchor == null) return;
            _anchor = e.NewPosition.BufferPosition.Snapshot.CreateTrackingPoint(
                e.NewPosition.BufferPosition.Position, PointTrackingMode.Positive);
            RestartBlinkCycle();
            Redraw();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_anchor != null) Redraw();
        }

        private void OnFocusChanged(object sender, EventArgs e) => Redraw();

        private void Redraw()
        {
            try
            {
                // An unfocused view shows no caret at all, matching the native
                // one; the anchor survives, so focus returning brings it back.
                if (_anchor == null || !_view.HasAggregateFocus)
                {
                    RemoveElement();
                    return;
                }

                var point = _anchor.GetPoint(_view.TextSnapshot);

                // Mid-layout the line lookup can throw; the LayoutChanged that
                // follows re-enters here.
                if (_view.InLayout) return;

                var line = _view.GetTextViewLineContainingBufferPosition(point);

                if (line == null)
                {
                    // Scrolled out of the layout: no caret on screen.
                    RemoveElement();
                    return;
                }
                // Unattached lines are mid-layout; the bounds calls below
                // would throw. The next LayoutChanged re-enters here.
                if (line.VisibilityState == VisibilityState.Unattached)
                    return;

                Draw(line, point);
            }
            catch (Exception ex)
            {
                // A missing caret is recoverable; taking the editor down is not.
                Infrastructure.Log.Write("caret: Redraw failed", ex);
            }
        }

        private void Draw(ITextViewLine line, SnapshotPoint point)
        {
            // The cell under the caret, VsVim-style: character bounds, with a
            // block on a tab floating over the tab's LAST cell (gVim's read of
            // it) and line breaks degrading to an empty break-width cell.
            var bounds = line.GetCharacterBounds(point);

            string glyph = string.Empty;
            double width = DefaultCharWidth();
            double left = bounds.Left;
            if (point.Position < _view.TextSnapshot.Length)
            {
                char c = point.GetChar();
                if (c == '\t')
                {
                    left += Math.Max(0.0, bounds.Width - width);
                    width = Math.Min(width, bounds.Width);
                }
                else if (c == '\r' || c == '\n')
                {
                    width = bounds.Width;
                }
                else if (char.IsHighSurrogate(c) && point.Position + 1 < _view.TextSnapshot.Length)
                {
                    glyph = new SnapshotSpan(point, 2).GetText();
                    width = bounds.Width;
                }
                else
                {
                    glyph = c.ToString();
                    width = bounds.Width;
                }
            }

            double cellHeight = line.TextHeight;

            var props = _formatMapService.GetClassificationFormatMap(_view)
                .DefaultTextProperties;
            double glyphY = Math.Max(0.0,
                line.Baseline - bounds.TextTop
                - MeasureGlyph("A", props.Typeface, props.FontRenderingEmSize).Baseline);

            GetCaretColors(out Color fill, out Color text);

            // Glide only for real buffer-position moves made by keyboard (or
            // nvim): not scrolls, not clicks, not drags.
            bool glide = Infrastructure.VSNeoSettings.AnimateCaretMovement
                && point.Position != _drawnPosition
                && System.Windows.Input.Mouse.LeftButton != System.Windows.Input.MouseButtonState.Pressed;
            _drawnPosition = point.Position;

            if (_element != null
                && _drawnShape == _shape
                && _drawnGlyph == glyph
                && _drawnWidth == width
                && _drawnHeight == cellHeight
                && _drawnFill == fill
                && _drawnText == text
                && _drawnGlyphY == glyphY)
            {
                // The common case: a plain move. Reposition, never rebuild -
                // gliding there when the option is on.
                PositionElement(_element, left, bounds.TextTop, animate: _added && glide);
                if (!_added)
                    _added = AddElement(_element, point);
                return;
            }

            // A rebuild still glides: the old element's (possibly
            // mid-animation) position is where the new one starts from -
            // otherwise every move onto a different character teleported,
            // and the glide only ever ran between identical glyphs.
            bool wasAdded = _added;
            double oldLeft = _element != null
                ? System.Windows.Controls.Canvas.GetLeft(_element) : double.NaN;
            double oldTop = _element != null
                ? System.Windows.Controls.Canvas.GetTop(_element) : double.NaN;

            RemoveElement();
            _element = new CaretElement(
                width, cellHeight, BarRect(width, cellHeight), fill, text, glyph,
                props.Typeface, props.FontRenderingEmSize, glyphY);
            _drawnShape = _shape;
            _drawnGlyph = glyph;
            _drawnWidth = width;
            _drawnHeight = cellHeight;
            _drawnFill = fill;
            _drawnText = text;
            _drawnGlyphY = glyphY;
            PositionElement(_element, left, bounds.TextTop,
                animate: wasAdded && glide,
                fromLeft: oldLeft, fromTop: oldTop);
            _added = AddElement(_element, point);
            _element.Visibility = Visibility.Visible;
            RestartBlinkCycle();
        }

        // ------------------------------------------------------------------
        // Animations. Both are pure WPF property animations: the glide runs
        // on the render thread and the blink fade is one self-reversing
        // opacity animation, so neither costs anything per frame in managed
        // code and the key path stays allocation-trivial.
        // ------------------------------------------------------------------

        private static readonly System.Windows.Media.Animation.EasingFunctionBase GlideEase = Freeze(
            new System.Windows.Media.Animation.QuadraticEase
            { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut });

        private static readonly System.Windows.Media.Animation.EasingFunctionBase BlinkEase = Freeze(
            new System.Windows.Media.Animation.QuadraticEase
            { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut });

        private static T Freeze<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        private void PositionElement(
            FrameworkElement element, double left, double top, bool animate,
            double fromLeft = double.NaN, double fromTop = double.NaN)
        {
            if (animate && double.IsNaN(fromLeft))
                fromLeft = System.Windows.Controls.Canvas.GetLeft(element);
            if (animate && double.IsNaN(fromTop))
                fromTop = System.Windows.Controls.Canvas.GetTop(element);

            if (!animate || double.IsNaN(fromLeft) || double.IsNaN(fromTop))
            {
                element.BeginAnimation(System.Windows.Controls.Canvas.LeftProperty, null);
                element.BeginAnimation(System.Windows.Controls.Canvas.TopProperty, null);
                System.Windows.Controls.Canvas.SetLeft(element, left);
                System.Windows.Controls.Canvas.SetTop(element, top);
                return;
            }

            // Reading the property mid-animation yields the current animated
            // value, so rapid motions chain: each keystroke retargets the
            // glide from wherever the block visibly is right now. Duration
            // scales with distance: a one-line step is a quick 60 ms nudge,
            // a gg across the file a ~200 ms sweep.
            double distance = Math.Sqrt(
                (fromLeft - left) * (fromLeft - left)
                + (fromTop - top) * (fromTop - top));
            var duration = TimeSpan.FromMilliseconds(
                Math.Min(60 + distance / 6, 200));

            if (fromLeft != left)
            {
                element.BeginAnimation(
                    System.Windows.Controls.Canvas.LeftProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(fromLeft, left, duration)
                    { EasingFunction = GlideEase });
            }
            if (fromTop != top)
            {
                element.BeginAnimation(
                    System.Windows.Controls.Canvas.TopProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(fromTop, top, duration)
                    { EasingFunction = GlideEase });
            }
        }

        /// <summary>
        /// The reversed region within the cell: the whole cell for a block,
        /// the bottom Percent-thick strip for horNN, the left Percent-wide
        /// strip for verNN (at least a pixel - a ver5 on a narrow cell still
        /// has to show something).
        /// </summary>
        private Rect BarRect(double width, double cellHeight) =>
            _shape.Kind switch
            {
                CaretShape.ShapeKind.Horizontal => new Rect(
                    0, cellHeight * (100 - _shape.Percent) / 100.0,
                    width, cellHeight * _shape.Percent / 100.0),
                CaretShape.ShapeKind.Vertical => new Rect(
                    0, 0, Math.Max(1.0, width * _shape.Percent / 100.0), cellHeight),
                _ => new Rect(0, 0, width, cellHeight),
            };

        private bool AddElement(UIElement element, SnapshotPoint point)
        {
            var span = point.Position < _view.TextSnapshot.Length
                ? new SnapshotSpan(point, point + 1)
                : new SnapshotSpan(point, point);
            return _layer.AddAdornment(
                AdornmentPositioningBehavior.ViewportRelative, span, null, element, null);
        }

        private void RemoveElement()
        {
            if (_element != null)
                _layer.RemoveAdornment(_element);
            _element = null;
            _added = false;
        }

        /// <summary>
        /// Caret colors from Tools > Options > Fonts and Colors ("VSNeo Block
        /// Caret"); the defaults are plain reverse video.
        /// </summary>
        private void GetCaretColors(out Color fill, out Color text)
        {
            fill = Colors.White;
            text = Colors.Black;
            try
            {
                var properties = _editorFormatMapService
                    .GetEditorFormatMap(_view).GetProperties(FormatName);
                if (properties.Contains(EditorFormatDefinition.BackgroundColorId))
                    fill = (Color)properties[EditorFormatDefinition.BackgroundColorId];
                if (properties.Contains(EditorFormatDefinition.ForegroundColorId))
                    text = (Color)properties[EditorFormatDefinition.ForegroundColorId];
            }
            catch
            {
                // Cosmetic only; the defaults above are always safe.
            }
        }

        private FormattedText MeasureGlyph(string text, Typeface typeface, double fontSize)
        {
            return new FormattedText(text, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, 1.0);
        }

        private double DefaultCharWidth()
        {
            if (_defaultCharWidth < 0)
            {
                var properties = _formatMapService.GetClassificationFormatMap(_view)
                    .DefaultTextProperties;
                _defaultCharWidth = MeasureGlyph(
                    "A", properties.Typeface, properties.FontRenderingEmSize).Width;
            }
            return _defaultCharWidth;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _blinkTimer?.Stop();
            RemoveElement();
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.GotAggregateFocus -= OnFocusChanged;
            _view.LostAggregateFocus -= OnFocusChanged;
            _view.Closed -= OnClosed;
        }

        // ------------------------------------------------------------------
        // Blinking, VsVim-style: the system caret blink time, with the cycle
        // restarted on every caret move so the block never disappears
        // mid-motion.
        // ------------------------------------------------------------------

        private void OnBlink(object sender, EventArgs e)
        {
            if (_element == null) return;
            _element.Visibility = _element.Visibility == Visibility.Visible
                ? Visibility.Hidden
                : Visibility.Visible;
        }

        private void RestartBlinkCycle()
        {
            if (_anchor == null || !_shape.Blinks) return;

            // The fade is one self-reversing opacity animation on the render
            // thread; (re)starting it on every caret move is what keeps the
            // block visible mid-motion, same contract as the timer path.
            if (BlinkAvailable && Infrastructure.VSNeoSettings.FadeCaretBlink)
            {
                _blinkTimer?.Stop();
                if (_element != null)
                {
                    _element.Visibility = Visibility.Visible;
                    _element.BeginAnimation(UIElement.OpacityProperty, MakeBlinkFade());
                }
                return;
            }

            if (_blinkTimer == null) return;
            _blinkTimer.Stop();
            _blinkTimer.Start();
            if (_element != null)
            {
                // Back to hard blinking (option toggled mid-session): clear a
                // fade that may still be running, or it keeps fading under
                // the timer's Visibility toggles.
                _element.BeginAnimation(UIElement.OpacityProperty, null);
                _element.Opacity = 1.0;
                _element.Visibility = Visibility.Visible;
            }
        }

        private static System.Windows.Media.Animation.DoubleAnimation MakeBlinkFade() =>
            new System.Windows.Media.Animation.DoubleAnimation(
                1.0, 0.0, TimeSpan.FromMilliseconds(s_blinkMs))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = BlinkEase,
            };

        [DllImport("user32.dll")]
        private static extern uint GetCaretBlinkTime();

        // 0 = error, uint.MaxValue = "do not blink"; both mean no blink at all.
        private static readonly uint s_blinkMs = GetCaretBlinkTime();

        private static bool BlinkAvailable => s_blinkMs != 0 && s_blinkMs != uint.MaxValue;

        private static DispatcherTimer? CreateBlinkTimer(Dispatcher dispatcher, EventHandler onBlink)
        {
            if (!BlinkAvailable) return null;
            uint ms = s_blinkMs;

            // VsVim guards this constructor: a reported-but-unreproducible
            // conversion bug throws for perfectly valid inputs (VsVim#631).
            try
            {
                return new DispatcherTimer(
                    TimeSpan.FromMilliseconds(ms), DispatcherPriority.Normal, onBlink, dispatcher);
            }
            catch (ArgumentOutOfRangeException)
            {
                return new DispatcherTimer(
                    TimeSpan.FromSeconds(2), DispatcherPriority.Normal, onBlink, dispatcher);
            }
        }

        /// <summary>
        /// The reverse-video cell: an opaque fill over the bar region with the
        /// glyph redrawn on top, clipped to the same region - so half and
        /// quarter blocks reverse only their slice of the character.
        /// </summary>
        private sealed class CaretElement : FrameworkElement
        {
            private readonly double _width;
            private readonly double _height;
            private readonly Rect _bar;
            private readonly Brush _fill;
            private readonly FormattedText? _glyph;
            private readonly double _glyphY;

            public CaretElement(
                double width, double height, Rect bar,
                Color fill, Color text, string glyph,
                Typeface typeface, double fontSize, double glyphY)
            {
                _width = width;
                _height = height;
                _bar = bar;
                _fill = Frozen(fill);
                _glyphY = glyphY;
                if (glyph.Length > 0)
                {
                    var brush = Frozen(text);
                    _glyph = new FormattedText(glyph, CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, typeface, fontSize, brush, 1.0);
                }
            }

            private static Brush Frozen(Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }

            protected override void OnRender(DrawingContext dc)
            {
                dc.PushClip(new RectangleGeometry(_bar));
                dc.DrawRectangle(_fill, null, _bar);
                if (_glyph != null)
                    dc.DrawText(_glyph, new Point(0, _glyphY));
                dc.Pop();
            }

            protected override Size MeasureOverride(Size availableSize) => new Size(_width, _height);
        }
    }
}
