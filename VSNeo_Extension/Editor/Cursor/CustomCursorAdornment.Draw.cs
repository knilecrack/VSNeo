using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// Placing the cursor rectangle: the cell, the shape, the colour, the layer.
internal sealed partial class CustomCursorAdornment
{
    private const string LayerName = "VSNeoCursor";

    private void Update(bool restartBlink)
    {
        if (_closed)
        {
            return;
        }

        try
        {
            var session = VSNeo_ExtensionPackage.Session;
            var state = session?.State;
            if (_failed || session == null || !session.IsReady || state == null)
            {
                Deactivate();
                return;
            }

            // Unstyled, only the end-of-line block (see the class summary).
            bool styled = state.CursorStyleEnabled;
            if (!styled && !NeedsEndOfLineBlock(state.Mode))
            {
                Deactivate();
                return;
            }

            // Asserted every time, not only on activation: nothing in the
            // editor is known to unhide the caret, but if anything does,
            // the next event puts it back rather than showing two cursors.
            if (!_view.Caret.IsHidden)
            {
                _view.Caret.IsHidden = true;
            }

            _active = true;
            EnsureFormatMap();
            _currentColor = ColorFor(state.Mode, state);

            // _focused, not HasAggregateFocus: this runs inside the focus
            // events, where the property can still read false on the way
            // back in. With Visual Studio's caret hidden, that stale false
            // left no cursor at all until the caret next moved - after
            // closing the Seeky picker, most visibly. MessageMargin and
            // CmdLinePopup hit the same trap.
            if (!_focused || state.Mode == VimMode.Visual)
            {
                // Unfocused: Visual Studio draws no caret there either.
                // Visual: VisualBlockCaretAdornment is the cursor.
                // Stop the blink too: it used to keep toggling a hidden
                // shape's opacity at 2 Hz in every view ever focused.
                StopBlink();
                HideShape();
                return;
            }

            // Mid-layout the caret's line is not readable; LayoutChanged
            // follows and calls back in.
            if (_view.InLayout)
            {
                return;
            }

            if (!TryGetCell(out var cell, out double caretLeft))
            {
                HideShape();
                return;
            }

            var shape = styled ? ShapeFor(state.Mode, state) : CursorShape.Block;
            // The glow is a blur; on a software renderer that is a CPU
            // convolution on every repaint of the cursor (every blink
            // frame included).
            int glow = !styled || (state.CursorGlow > 0
                       && Infrastructure.RenderTier.ReduceEffects(_view.VisualElement))
                ? 0
                : state.CursorGlow;
            Place(shape, cell, caretLeft, _currentColor, glow);
            if (restartBlink)
            {
                RestartBlink(state);
            }
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

    /// <summary>
    /// Normal or operator-pending mode, focused, with the caret where
    /// Visual Studio's overwrite block has no character to cover: an empty
    /// line, the end of a line, or virtual space. UI thread; reads only
    /// the caret, so it is cheap enough for every caret move.
    /// </summary>
    private bool NeedsEndOfLineBlock(VimMode mode)
    {
        if (mode != VimMode.Normal && mode != VimMode.OperatorPending)
        {
            return false;
        }

        if (!_focused)
        {
            return false;
        }

        var caret = _view.Caret;
        if (caret.InVirtualSpace)
        {
            return true;
        }

        var position = caret.Position.BufferPosition;
        return position.Position == position.GetContainingLine().End.Position;
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
        try
        { line = caret.ContainingTextViewLine; }
        catch (InvalidOperationException) { return false; }

        if (line == null || line.VisibilityState == VisibilityState.Unattached
            || line.VisibilityState == VisibilityState.Hidden)
        {
            return false;
        }

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

                // A tab: Vim (with 'list' off, and Neovide the same) shows the
                // cursor on the tab's last cell, one cell wide, right against
                // the text - not a block over the whole tab stop, which read
                // as a tab-shaped cursor. The trail and beacon follow this cell.
                if (position.GetChar() == '\t' && bounds.Width > columnWidth)
                {
                    left = bounds.Right - columnWidth;
                    width = columnWidth;
                }
            }
        }

        double top = line.TextTop;
        double height = line.TextHeight;
        if (double.IsNaN(left) || double.IsNaN(top) || height <= 0)
        {
            return false;
        }

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
            {
                _glow = new DropShadowEffect
                {
                    Color = color,
                    BlurRadius = glow,
                    ShadowDepth = 0,
                    Opacity = 0.95,
                    RenderingBias = RenderingBias.Performance,
                };
            }

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
        if (_shape != null)
        {
            _shape.Visibility = Visibility.Collapsed;
        }
    }

    private Rectangle EnsureShape()
    {
        if (_shape != null)
        {
            return _shape;
        }

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
        if (_formatMap != null)
        {
            return;
        }

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
        int index = CursorStyleDecision.ModeIndex(mode);
        int rgb = index < colors.Length ? colors[index] : -1;
        if (rgb < 0)
        {
            return ReadCaretColor(_formatMap);
        }

        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    // ---- blinking ---------------------------------------------------------

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
            {
                return c;
            }

            if (props != null && props.Contains(EditorFormatDefinition.ForegroundBrushId)
                && props[EditorFormatDefinition.ForegroundBrushId] is SolidColorBrush b)
            {
                return b.Color;
            }
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
}
