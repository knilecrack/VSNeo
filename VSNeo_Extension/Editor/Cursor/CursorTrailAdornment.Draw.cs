using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// The quad itself: caret corners, the polygon, the brush, the effect host.
internal sealed partial class CursorTrailAdornment
{
    private const string LayerName = "VSNeoCursorTrail";

    private void Draw()
    {
        var shape = EnsureShape();
        if (shape == null)
        {
            return;
        }

        // The layer works in view coordinates; the corners are viewport-
        // relative, so the viewport's own offset goes back on here.
        double left = _view.ViewportLeft;
        double top = _view.ViewportTop;
        // The mode (and with it vsneo_cursor_color) can change between
        // moves; rebuild the brush only when the color actually did.
        var color = CaretColor();
        if (color != _fillColor)
        {
            _fillColor = color;
            shape.Fill = CaretBrush();
        }

        var points = shape.Points;
        for (int i = 0; i < 4; i++)
        {
            var corner = _motion.Position(i);
            points[i] = new Point(corner.X + left, corner.Y + top);
        }

        shape.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The caret's rectangle as four viewport-relative corners, or false
    /// when the caret is not on a laid-out, visible line.
    /// </summary>
    private bool TryGetCaretCorners(out Point[] corners)
    {
        corners = _scratch;

        // With VSNeo's own cursor on, aim at the shape it drew - a hollow
        // block or an underline gets a trail of its own shape, not the
        // hidden caret's.
        var custom = CustomCursorAdornment.For(_view);
        if (custom != null && custom.TryGetDrawnRect(out var drawn))
        {
            return FromViewRect(drawn, corners);
        }

        // Visual mode: the cursor is the block VisualBlockCaretAdornment
        // draws over Vim's cursor character. Visual Studio's caret sits at
        // the selection's exclusive end, one character off. An empty line
        // has no block, and falls through to the caret.
        if (State?.Mode == VimMode.Visual)
        {
            var visual = VisualBlockCaretAdornment.For(_view);
            if (visual != null && visual.TryGetDrawnRect(out var block))
            {
                return FromViewRect(block, corners);
            }
        }

        var caret = _view.Caret;

        ITextViewLine line;
        try
        { line = caret.ContainingTextViewLine; }
        catch (InvalidOperationException) { return false; }

        if (line == null || line.VisibilityState == VisibilityState.Unattached
            || line.VisibilityState == VisibilityState.Hidden)
        {
            return false;
        }

        double x = caret.Left - _view.ViewportLeft;
        double y = caret.Top - _view.ViewportTop;
        // The insert-mode caret is a thin bar; keep the trail at least
        // wide enough to read.
        double w = Math.Max(caret.Width, 2);
        double h = caret.Height;
        if (double.IsNaN(x) || double.IsNaN(y) || h <= 0)
        {
            return false;
        }

        corners[0] = new Point(x, y);
        corners[1] = new Point(x + w, y);
        corners[2] = new Point(x + w, y + h);
        corners[3] = new Point(x, y + h);
        return true;
    }

    private readonly Point[] _scratch = new Point[4];

    /// <summary>A view-coordinate rect as four viewport-relative corners.</summary>
    private bool FromViewRect(Rect rect, Point[] corners)
    {
        double x = rect.Left - _view.ViewportLeft;
        double y = rect.Top - _view.ViewportTop;
        double w = Math.Max(rect.Width, 2);
        double h = Math.Max(rect.Height, 2);
        corners[0] = new Point(x, y);
        corners[1] = new Point(x + w, y);
        corners[2] = new Point(x + w, y + h);
        corners[3] = new Point(x, y + h);
        return true;
    }

    private static Point Center(Point[] corners) =>
        new Point((corners[0].X + corners[1].X + corners[2].X + corners[3].X) / 4,
                  (corners[0].Y + corners[1].Y + corners[2].Y + corners[3].Y) / 4);

    private Polygon? EnsureShape()
    {
        if (_shape != null)
        {
            return _shape;
        }

        _layer ??= _view.GetAdornmentLayer(LayerName);
        EnsureFormatMap();

        _shape = new Polygon
        {
            Points = new PointCollection(new Point[4]),
            Fill = CaretBrush(),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, _shape, null);
        return _shape;
    }

    /// <summary>The effect layer, created the first time an effect is on.</summary>
    private Effects.CursorEffectHost EnsureEffects()
    {
        if (_effects != null)
        {
            return _effects;
        }

        _layer ??= _view.GetAdornmentLayer(LayerName);
        EnsureFormatMap();

        _effects = new Effects.CursorEffectHost();
        PlaceEffects(_effects);
        _layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, _effects, null);
        return _effects;
    }

    /// <summary>
    /// Keeps the effect element on the viewport: its coordinates are
    /// viewport-relative, the layer's are view coordinates.
    /// </summary>
    private void PlaceEffects(Effects.CursorEffectHost host)
    {
        System.Windows.Controls.Canvas.SetLeft(host, _view.ViewportLeft);
        System.Windows.Controls.Canvas.SetTop(host, _view.ViewportTop);
        host.Width = Math.Max(1, _view.ViewportWidth);
        host.Height = Math.Max(1, _view.ViewportHeight);
    }

    private void EnsureFormatMap()
    {
        if (_formatMap != null)
        {
            return;
        }

        _formatMap = _formatMapService.GetEditorFormatMap(_view);
        _formatMap.FormatMappingChanged += OnFormatMappingChanged;
    }

    private void OnFormatMappingChanged(object sender, FormatItemsEventArgs e)
    {
        if (_shape != null)
        {
            _shape.Fill = CaretBrush();
        }
    }

    /// <summary>
    /// The theme's caret color (Tools &gt; Options &gt; Fonts and Colors),
    /// translucent: the real caret stays the thing you read, the trail
    /// only shows where it came from.
    /// </summary>
    private Brush CaretBrush()
    {
        var color = CaretColor();
        var brush = new SolidColorBrush(Color.FromArgb(0x99, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The custom cursor's color for the current mode when that is on
    /// (vsneo_cursor_color), the theme's caret color otherwise.
    /// </summary>
    private Color CaretColor() =>
        CustomCursorAdornment.For(_view)?.CurrentColor
        ?? CustomCursorAdornment.ReadCaretColor(_formatMap);

    private Color _fillColor;
}
