using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor.Effects
{
    // The cyberpunk set. Each is one class on the effect bases; see
    // ICursorEffect for the contract and CursorEffectRegistry for the names.

    /// <summary>
    /// glitch - on a jump, a mode change or focus: the cursor cell splits into
    /// red and cyan copies pulled apart (chromatic aberration) while
    /// horizontal tear bars jitter across the row, re-rolled every frame, and
    /// settle as it fades.
    /// </summary>
    internal sealed class GlitchEffect : HighlightEffect
    {
        private static readonly Color Red = Color.FromRgb(0xFF, 0x00, 0x3C);
        private static readonly Color Cyan = Color.FromRgb(0x00, 0xF0, 0xFF);

        public override string Name => "glitch";

        private static double LifeOf(CursorEffectContext c) => Math.Max(0.15, c.HighlightLifetime * 1.25);

        public override void OnJump(CursorEffectContext c, Rect from, Rect to)
        {
            if (!IsStep(c, from, to)) Start(to, LifeOf(c));
        }

        public override void OnModeChanged(CursorEffectContext c, VimMode from, VimMode to, Rect cell) => Start(cell, LifeOf(c));
        public override void OnFocus(CursorEffectContext c, Rect cell) => Start(cell, LifeOf(c));

        protected override void DrawHighlight(DrawingContext dc, CursorEffectContext c, in Highlight h, double t)
        {
            var rng = c.Random;
            double strength = 1 - t;
            var cell = h.Cell;

            // Aberration: the cell in red and cyan, pulled apart.
            double split = c.CellWidth * 0.45 * strength;
            double copies = c.Opacity * 0.7 * strength;
            dc.DrawRectangle(c.Tint(Red, copies), null, new Rect(cell.X - split, cell.Y, cell.Width, cell.Height));
            dc.DrawRectangle(c.Tint(Cyan, copies), null, new Rect(cell.X + split, cell.Y, cell.Width, cell.Height));

            // Tear bars: thin slices of the row, shoved sideways at random.
            int bars = 2 + rng.Next(4);
            double alpha = c.Opacity * 0.8 * strength;
            for (int i = 0; i < bars; i++)
            {
                double height = Math.Max(1, cell.Height * (0.08 + rng.NextDouble() * 0.18));
                double y = cell.Y + rng.NextDouble() * (cell.Height - height);
                double width = c.CellWidth * (2 + rng.NextDouble() * 8) * strength;
                double x = cell.X - width / 2 + (rng.NextDouble() - 0.5) * c.CellWidth * 6 * strength;
                var brush = (i % 3) switch { 0 => c.Tint(Red, alpha), 1 => c.Tint(Cyan, alpha), _ => c.Tint(alpha) };
                dc.DrawRectangle(brush, null, new Rect(x, y, width, height));
            }
        }
    }

    /// <summary>
    /// matrix - on a jump: glyphs (half-width katakana, 0 and 1) drop from
    /// the path like digital rain, fading as they fall.
    /// </summary>
    internal sealed class MatrixEffect : ParticleEffect
    {
        private const string Glyphs = "01ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ";
        private static readonly Typeface Face = new Typeface(new FontFamily("Consolas, MS Gothic, Yu Gothic"),
                                                             FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // Glyph outlines, centred on the origin, rebuilt only when the size or
        // DPI changes. Outlines rather than FormattedText: text carries its
        // brush, so fading it would take PushOpacity per glyph (an offscreen
        // layer each); an outline takes any tinted brush.
        private readonly Dictionary<int, Geometry> _shaped = new Dictionary<int, Geometry>();
        private double _shapedSize;
        private double _shapedDip;

        public override string Name => "matrix";
        protected override int MaxParticles => 200;

        public override void OnJump(CursorEffectContext c, Rect from, Rect to)
        {
            var start = Center(from);
            var travel = Center(to) - start;
            double distance = travel.Length;
            if (distance < 1 || c.Lifetime <= 0) return;

            if (IsStep(c, from, to)) return;
            int count = CountAlongPath(c, distance, 3, max: 60);
            for (int i = 0; i < count; i++)
            {
                double t = (i + c.Random.NextDouble()) / count;
                var at = start + travel * t + new Vector((c.Random.NextDouble() - 0.5) * c.CellWidth * 2, 0);
                var velocity = new Vector(0, (0.4 + c.Random.NextDouble() * 0.8) * c.Speed * c.Unit * 0.6);
                // Rain lingers: twice the particle lifetime.
                Emit(c.Random, at, velocity, c.Lifetime * 2 * (0.5 + 0.5 * t), tag: c.Random.Next(Glyphs.Length));
            }
        }

        protected override void DrawParticle(DrawingContext dc, CursorEffectContext c, in Particle p, double life)
        {
            var brush = c.Tint(c.Opacity * life);
            if (brush == null) return;
            dc.PushTransform(new TranslateTransform(p.Position.X, p.Position.Y));
            dc.DrawGeometry(brush, null, Shape(c, p.Tag));
            dc.Pop();
        }

        private Geometry Shape(CursorEffectContext c, int glyph)
        {
            double size = Math.Max(6, c.CellHeight * 0.75);
            if (_shapedSize != size || _shapedDip != c.PixelsPerDip)
            {
                _shaped.Clear();
                _shapedSize = size;
                _shapedDip = c.PixelsPerDip;
            }

            if (!_shaped.TryGetValue(glyph, out var outline))
            {
                var text = new FormattedText(Glyphs[glyph].ToString(), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Face, size, Brushes.White, c.PixelsPerDip);
                outline = text.BuildGeometry(new Point(-text.Width / 2, -text.Height / 2));
                outline.Freeze();
                _shaped[glyph] = outline;
            }
            return outline;
        }
    }

    /// <summary>
    /// circuit - on a jump: a right-angle neon trace draws itself from the old
    /// position to the new one, a bright head leading it, nodes at the corner
    /// and both ends, then the whole board fades.
    /// </summary>
    internal sealed class CircuitEffect : CursorEffect
    {
        private struct Trace
        {
            public Point A, Corner, B;
            public double Age, Life;
        }

        private readonly Trace[] _traces = new Trace[4];
        private int _count;

        public override string Name => "circuit";
        public override bool IsActive => _count > 0;

        public override void OnJump(CursorEffectContext c, Rect from, Rect to)
        {
            var a = Center(from);
            var b = Center(to);
            if ((b - a).Length < 1 || IsStep(c, from, to)) return;

            // Horizontal-then-vertical or the other way round: traces on a
            // board do both.
            var corner = c.Random.NextDouble() < 0.5 ? new Point(b.X, a.Y) : new Point(a.X, b.Y);
            int slot = _count < _traces.Length ? _count++ : 0;
            _traces[slot] = new Trace { A = a, Corner = corner, B = b, Age = 0, Life = Math.Max(0.35, c.Lifetime * 0.9) };
        }

        public override void Update(double dt)
        {
            for (int i = 0; i < _count; i++)
            {
                _traces[i].Age += dt;
                if (_traces[i].Age >= _traces[i].Life)
                {
                    _traces[i] = _traces[--_count];
                    i--;
                }
            }
        }

        public override void Render(DrawingContext dc, CursorEffectContext c)
        {
            double width = Math.Max(1.5, c.CellWidth * 0.16);
            double node = width * 1.8;

            for (int i = 0; i < _count; i++)
            {
                var tr = _traces[i];
                double draw = Math.Min(1, tr.Age / (tr.Life * 0.35));             // drawing on
                double fade = Math.Max(0, (tr.Age - tr.Life * 0.35) / (tr.Life * 0.65));
                double alpha = c.Opacity * (1 - fade);

                double first = (tr.Corner - tr.A).Length;
                double second = (tr.B - tr.Corner).Length;
                double reach = (first + second) * draw;

                // The head's position along the two segments.
                Point head;
                bool pastCorner = reach > first;
                if (!pastCorner)
                    head = first > 0 ? tr.A + (tr.Corner - tr.A) * (reach / first) : tr.A;
                else
                    head = second > 0 ? tr.Corner + (tr.B - tr.Corner) * ((reach - first) / second) : tr.B;

                var fill = c.Tint(alpha);
                var pen = c.Stroke(fill, width, PenLineCap.Square);
                var bus = c.Stroke(fill, Math.Max(1, width * 0.5));
                if (fill == null || pen == null || bus == null) continue;

                dc.DrawLine(pen, tr.A, pastCorner ? tr.Corner : head);
                if (pastCorner) dc.DrawLine(pen, tr.Corner, head);

                // A thin parallel bus line, offset a few pixels, for the board look.
                var offset = new Vector(width * 2.5, width * 2.5);
                dc.DrawLine(bus, tr.A + offset, (pastCorner ? tr.Corner : head) + offset);
                if (pastCorner) dc.DrawLine(bus, tr.Corner + offset, head + offset);

                dc.DrawRectangle(fill, null, new Rect(tr.A.X - node / 2, tr.A.Y - node / 2, node, node));
                if (pastCorner)
                    dc.DrawEllipse(fill, null, tr.Corner, node * 0.6, node * 0.6);
                if (draw >= 1)
                    dc.DrawRectangle(fill, null, new Rect(tr.B.X - node / 2, tr.B.Y - node / 2, node, node));

                // The bright head while it is still drawing.
                if (draw < 1)
                    dc.DrawEllipse(c.Tint(Colors.White, c.Opacity), null, head, node * 0.7, node * 0.7);
            }
        }

        public override void Clear() => _count = 0;
    }

    /// <summary>
    /// scanline - on a jump or focus: a scan line sweeps out from the cursor
    /// to both edges of the editor while a band on the cursor's row closes
    /// and fades.
    /// </summary>
    internal sealed class ScanlineEffect : HighlightEffect
    {
        public override string Name => "scanline";

        private static double LifeOf(CursorEffectContext c) => Math.Max(0.3, c.HighlightLifetime * 2);

        // A row effect as wide as the editor: only for landing on a new line
        // from afar. On every typed character or j it was constant sweeping
        // (and a full-width repaint per frame).
        public override void OnJump(CursorEffectContext c, Rect from, Rect to)
        {
            if (IsLineJump(c, from, to)) Start(to, LifeOf(c));
        }

        public override void OnFocus(CursorEffectContext c, Rect cell) => Start(cell, LifeOf(c));

        protected override void DrawHighlight(DrawingContext dc, CursorEffectContext c, in Highlight h, double t)
        {
            double eased = 1 - (1 - t) * (1 - t);        // ease-out sweep
            double cx = h.Cell.X + h.Cell.Width / 2;
            double cy = h.Cell.Y + h.Cell.Height / 2;
            double width = c.Viewport.Width;

            // The band on the cursor's row, closing to its centre.
            double band = h.Cell.Height * (1 - t);
            dc.DrawRectangle(c.Tint(c.Opacity * 0.18 * (1 - t)), null, new Rect(0, cy - band / 2, width, band));

            // The line itself, sweeping out from the cursor to both edges.
            double left = cx - cx * eased;
            double right = cx + (width - cx) * eased;
            var pen = c.Stroke(c.Tint(c.Opacity * (1 - t)), 1.5);
            if (pen != null) dc.DrawLine(pen, new Point(left, cy), new Point(right, cy));
        }
    }

    /// <summary>
    /// sparks - on every typed character: a few white-hot sparks spray from
    /// the cursor and arc down under gravity, cooling to the cursor's color.
    /// A lighter "power mode".
    /// </summary>
    internal sealed class SparksEffect : ParticleEffect
    {
        public override string Name => "sparks";
        protected override int MaxParticles => 120;

        public override void OnType(CursorEffectContext c, Rect cell)
        {
            int count = Math.Max(2, (int)Math.Round(6 * c.Density / 0.7));
            var origin = new Point(cell.X, cell.Y + cell.Height * 0.8);
            var gravity = new Vector(0, c.Speed * c.Unit * 6);
            for (int i = 0; i < count; i++)
            {
                var velocity = new Vector((c.Random.NextDouble() - 0.5) * 2,
                                          -(0.6 + c.Random.NextDouble())) * c.Speed * c.Unit * 1.2;
                double life = Math.Max(0.15, c.Lifetime * 0.7) * (0.6 + 0.4 * c.Random.NextDouble());
                Emit(c.Random, origin, velocity, life, acceleration: gravity);
            }
        }

        protected override void DrawParticle(DrawingContext dc, CursorEffectContext c, in Particle p, double life)
        {
            // A short streak behind the spark, along its velocity: white-hot
            // at first, then the cursor's color.
            var color = life > 0.7 ? Colors.White : c.Color;
            var pen = c.Stroke(c.Tint(color, c.Opacity * life), 1.5, PenLineCap.Round);
            if (pen != null) dc.DrawLine(pen, p.Position - p.Velocity * 0.025, p.Position);
        }
    }

    /// <summary>
    /// flicker - on a mode change or focus: the cursor's outline stutters like
    /// a failing neon tube catching, then holds and fades.
    /// </summary>
    internal sealed class FlickerEffect : HighlightEffect
    {
        public override string Name => "flicker";

        private static double LifeOf(CursorEffectContext c) => Math.Max(0.35, c.HighlightLifetime * 2);

        public override void OnModeChanged(CursorEffectContext c, VimMode from, VimMode to, Rect cell) =>
            Start(cell, LifeOf(c), c.Random.Next());
        public override void OnFocus(CursorEffectContext c, Rect cell) => Start(cell, LifeOf(c), c.Random.Next());

        protected override void DrawHighlight(DrawingContext dc, CursorEffectContext c, in Highlight h, double t)
        {
            // On/off in 1/30 s steps from the highlight's own seed, so the
            // stutter is stable per frame bucket, not noise per repaint; it
            // stutters for the first 60% and then holds.
            int bucket = (int)(h.Age * 30);
            uint hash = unchecked((uint)h.Seed ^ ((uint)bucket * 0x2545F491u));
            hash ^= hash >> 13;
            hash *= 0x5bd1e995u;
            bool lit = t > 0.6 || (hash & 3) != 0;

            double fade = 1 - t * t * t;
            var rect = h.Cell;
            rect.Inflate(2, 2);
            var glow = h.Cell;
            glow.Inflate(5, 5);

            double alpha = c.Opacity * fade * (lit ? 1 : 0.12);
            var tube = c.Stroke(c.Tint(alpha), 2);
            var halo = c.Stroke(c.Tint(alpha * 0.3), 4);
            if (tube != null) dc.DrawRectangle(null, tube, rect);
            if (halo != null) dc.DrawRectangle(null, halo, glow);
        }
    }
}
