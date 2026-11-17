using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VSNeo_Extension.Editor;

// Shaped numbers, cached: the glyph cache and its key, and the caret-line variant.
internal sealed partial class RelativeLineNumberMargin
{
    // One shaped glyph run per displayed number and font configuration,
    // shared by every margin: the per-repaint cost is a dictionary lookup
    // and a DrawText per visible line. It used to be per margin, so every
    // document opened (and every zoom step back and forth) re-shaped the
    // whole screenful from scratch. UI thread only, like every margin.
    private static readonly Dictionary<GlyphKey, FormattedText> Glyphs =
        new Dictionary<GlyphKey, FormattedText>();

    // Font size x zoom x DPI combinations multiply; a user zooming through
    // every step should not grow this without bound.
    private const int GlyphCacheLimit = 4096;

    private readonly struct GlyphKey : IEquatable<GlyphKey>
    {
        public readonly int Number;
        public readonly double Size;
        public readonly double PixelsPerDip;
        public readonly Typeface Typeface;
        public readonly Brush Foreground;

        public GlyphKey(int number, double size, double pixelsPerDip, Typeface typeface, Brush foreground)
        {
            Number = number;
            Size = size;
            PixelsPerDip = pixelsPerDip;
            Typeface = typeface;
            Foreground = foreground;
        }

        public bool Equals(GlyphKey o) =>
            Number == o.Number && Size == o.Size && PixelsPerDip == o.PixelsPerDip
            && ReferenceEquals(Foreground, o.Foreground) && Typeface.Equals(o.Typeface);

        public override bool Equals(object? obj) => obj is GlyphKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Number;
                h = h * 397 ^ Size.GetHashCode();
                h = h * 397 ^ PixelsPerDip.GetHashCode();
                h = h * 397 ^ Typeface.GetHashCode();
                return h * 397 ^ Foreground.GetHashCode();
            }
        }
    }

    // Per render, for the slow-render detail line.
    private int _shapedThisRender;
    private double _pixelsPerDip = 1;

    // The cursor line's number (Vim's CursorLineNr): the mode color while
    // the mode-line tint is on, a brighter foreground otherwise. One entry,
    // re-shaped only when the number, the size or the color changes - at
    // most once per caret line change or mode switch.
    private FormattedText? _caretGlyph;
    private int _caretGlyphNumber = -1;
    private double _caretGlyphSize;
    private Color _caretGlyphColor;

    private FormattedText CaretGlyphFor(int number, double fontSize)
    {
        var state = VSNeo_ExtensionPackage.Session?.State;
        Color color = _caretForeground;
        if (state != null && state.ModeLineEnabled
            && ModeLineTint.TintColor(state, state.Mode) is Color tint)
        {
            color = tint;
        }

        if (_caretGlyph != null && _caretGlyphNumber == number
            && _caretGlyphSize == fontSize && _caretGlyphColor == color)
        {
            return _caretGlyph;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _caretGlyph = new FormattedText(
            number.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            fontSize,
            brush,
            _pixelsPerDip);
        _caretGlyph.SetFontWeight(FontWeights.Bold);
        _caretGlyphNumber = number;
        _caretGlyphSize = fontSize;
        _caretGlyphColor = color;
        return _caretGlyph;
    }

    private FormattedText GlyphFor(int number, double fontSize)
    {
        var key = new GlyphKey(number, fontSize, _pixelsPerDip, _typeface, _foreground);
        if (Glyphs.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (Glyphs.Count >= GlyphCacheLimit)
        {
            Glyphs.Clear();
        }

        var formatted = new FormattedText(
            number.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            fontSize,
            _foreground,
            _pixelsPerDip);
        Glyphs[key] = formatted;
        _shapedThisRender++;
        return formatted;
    }
}
