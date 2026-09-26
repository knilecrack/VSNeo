using System;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Vim's mode-dependent caret, drawn as a WPF adornment. The approach is
    /// VsVim's (Src/VimWpf/Implementation/BlockCaret/BlockCaret.cs): an OPAQUE
    /// block that redraws the character under the caret in the contrasting
    /// color - real reverse video, like a terminal. A translucent overlay does
    /// not work here: the native caret and the glyph bleed through it.
    ///
    /// Shapes: full block in normal, bottom half block in operator-pending,
    /// bottom quarter block in replace, nothing in insert (Visual Studio's
    /// thin caret is Vim's bar already) or visual (VisualBlockCaretAdornment
    /// owns the live end there). Because the block is drawn, overwrite mode is
    /// never set for looks - overwrite only ever comes from the physical
    /// Insert key, and EnsureOverwriteOff in the key processor guards that.
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

    internal enum CaretShape { Block, HalfBlock, QuarterBlock }

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
        private CaretShape _shape = CaretShape.Block;
        private double _defaultCharWidth = -1;

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

            view.Caret.PositionChanged += OnCaretPositionChanged;
            view.LayoutChanged += OnLayoutChanged;
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
            Redraw();
        }

        public void Hide()
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            _anchor = null;
            _layer.RemoveAllAdornments();
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            // A null anchor is the hidden state; tracking never revives it.
            if (_anchor == null) return;
            _anchor = e.NewPosition.BufferPosition.Snapshot.CreateTrackingPoint(
                e.NewPosition.BufferPosition.Position, PointTrackingMode.Positive);
            Redraw();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_anchor != null) Redraw();
        }

        private void Redraw()
        {
            _layer.RemoveAllAdornments();
            if (_anchor == null) return;

            try
            {
                var point = _anchor.GetPoint(_view.TextSnapshot);
                var line = _view.GetTextViewLineContainingBufferPosition(point);

                // Unattached lines are mid-layout; the bounds calls below
                // would throw. The next LayoutChanged re-enters here.
                if (line == null || line.VisibilityState == VisibilityState.Unattached)
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
            // The cell under the caret, VsVim-style: character bounds, with
            // tabs, line breaks and virtual space degrading to a
            // default-width empty cell.
            var bounds = line.GetCharacterBounds(point);

            string glyph = string.Empty;
            double width = DefaultCharWidth();
            if (point.Position < _view.TextSnapshot.Length)
            {
                char c = point.GetChar();
                if (c == '\t' || c == '\r' || c == '\n')
                {
                    width = Math.Min(width, bounds.Width);
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

            GetCaretColors(out Brush fill, out Brush textBrush);

            if (_shape == CaretShape.Block)
            {
                // Opaque reverse-video cell: fill covers the native caret,
                // the glyph is redrawn on top with the line's baseline.
                var props = _formatMapService.GetClassificationFormatMap(_view)
                    .DefaultTextProperties;
                double glyphBaseline = Math.Max(0.0,
                    line.Baseline - bounds.TextTop - MeasureGlyph("A").Baseline);
                AddElement(new CaretElement(
                    new Rect(0, 0, width, line.TextHeight), fill, textBrush, glyph,
                    props.Typeface, props.FontRenderingEmSize, glyphBaseline),
                    point, bounds.Left, bounds.TextTop);
            }
            else
            {
                // Half/quarter block: an opaque bar at the bottom of the cell
                // over the existing text (vim's hor50/hor25 read), plus a
                // background strip hiding the native full-height caret that
                // would otherwise stick out above the bar.
                double height = line.TextHeight / (_shape == CaretShape.HalfBlock ? 2 : 4);
                var drawing = new DrawingGroup();
                drawing.Children.Add(new GeometryDrawing(
                    BackgroundBrush(), null,
                    new RectangleGeometry(new Rect(0, 0, 2.0, line.TextHeight))));
                drawing.Children.Add(new GeometryDrawing(
                    fill, null,
                    new RectangleGeometry(new Rect(0, line.TextHeight - height, width, height))));
                var image = new System.Windows.Controls.Image
                {
                    Source = new DrawingImage(drawing),
                    Width = width,
                    Height = line.TextHeight,
                };
                AddElement(image, point, bounds.Left, bounds.TextTop);
            }
        }

        private void AddElement(UIElement element, SnapshotPoint point, double left, double top)
        {
            System.Windows.Controls.Canvas.SetLeft(element, left);
            System.Windows.Controls.Canvas.SetTop(element, top);

            var span = point.Position < _view.TextSnapshot.Length
                ? new SnapshotSpan(point, point + 1)
                : new SnapshotSpan(point, point);
            _layer.AddAdornment(
                AdornmentPositioningBehavior.ViewportRelative, span, null, element, null);
        }

        /// <summary>
        /// Caret colors from Tools > Options > Fonts and Colors ("VSNeo Block
        /// Caret"); the defaults are plain reverse video.
        /// </summary>
        private void GetCaretColors(out Brush fill, out Brush textBrush)
        {
            fill = Brushes.White;
            textBrush = Brushes.Black;
            try
            {
                var properties = _editorFormatMapService
                    .GetEditorFormatMap(_view).GetProperties(FormatName);
                if (properties.Contains(EditorFormatDefinition.BackgroundColorId))
                    fill = new SolidColorBrush(
                        (Color)properties[EditorFormatDefinition.BackgroundColorId]);
                if (properties.Contains(EditorFormatDefinition.ForegroundColorId))
                    textBrush = new SolidColorBrush(
                        (Color)properties[EditorFormatDefinition.ForegroundColorId]);
            }
            catch
            {
                // Cosmetic only; the defaults above are always safe.
            }
            if (fill.CanFreeze) fill.Freeze();
            if (textBrush.CanFreeze) textBrush.Freeze();
        }

        private Brush BackgroundBrush()
        {
            try
            {
                // The view's own background is solid in practice; the "plain
                // text" classification background is Transparent in many VS
                // themes, and a transparent strip masks nothing.
                if (_view.Background is SolidColorBrush solid && solid.Color.A == 255)
                    return solid;
            }
            catch
            {
                // Fall through to the safe default.
            }
            return Brushes.Black;
        }

        private FormattedText MeasureGlyph(string text)
        {
            var properties = _formatMapService.GetClassificationFormatMap(_view)
                .DefaultTextProperties;
            return new FormattedText(text, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, properties.Typeface,
                properties.FontRenderingEmSize, Brushes.Black, 1.0);
        }

        private double DefaultCharWidth()
        {
            if (_defaultCharWidth < 0)
                _defaultCharWidth = MeasureGlyph("A").Width;
            return _defaultCharWidth;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnClosed;
        }

        /// <summary>Opaque block plus the redrawn glyph - the reverse-video cell.</summary>
        private sealed class CaretElement : FrameworkElement
        {
            private readonly Rect _rect;
            private readonly Brush _fill;
            private readonly Brush _textBrush;
            private readonly FormattedText? _glyph;

            public CaretElement(Rect rect, Brush fill, Brush textBrush, string glyph,
                Typeface typeface, double fontSize, double baseline)
            {
                _rect = rect;
                _fill = fill;
                _textBrush = textBrush;
                if (glyph.Length > 0)
                {
                    _glyph = new FormattedText(glyph, CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, typeface, fontSize, textBrush, 1.0);
                    _glyphY = baseline;
                }
            }

            private readonly double _glyphY;

            protected override void OnRender(DrawingContext dc)
            {
                dc.DrawRectangle(_fill, null, _rect);
                if (_glyph != null)
                    dc.DrawText(_glyph, new Point(0, _glyphY));
            }

            protected override Size MeasureOverride(Size availableSize) => _rect.Size;
        }
    }
}
