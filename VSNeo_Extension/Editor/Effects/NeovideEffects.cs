using System;
using System.Windows;
using System.Windows.Media;

namespace VSNeo_Extension.Editor.Effects
{
    // Neovide's six cursor VFX modes, on the effect bases. Same numbers as
    // before the refactor: particles per line of travel scale with density,
    // speeds with the font (context.Unit), and the start of a path dies first
    // so a trail recedes toward the cursor.

    /// <summary>railgun: a curling helix along the jump's path.</summary>
    internal sealed class RailgunEffect : ParticleEffect
    {
        public override string Name => "railgun";

        public override void OnJump(CursorEffectContext c, Rect from, Rect to)
        {
            var start = Center(from);
            var travel = Center(to) - start;
            double distance = travel.Length;
            if (distance < 1 || c.Lifetime <= 0) return;

            int count = CountAlongPath(c, distance, 8);
            for (int i = 0; i < count; i++)
            {
                double t = (i + c.Random.NextDouble()) / count;
                // The launch angle advances along the path (phase); every
                // particle keeps turning (curl).
                double angle = t / Math.PI * c.Phase * (distance / c.CellHeight);
                var velocity = new Vector(Math.Sin(angle), Math.Cos(angle)) * 2 * c.Speed * c.Unit;
                Emit(c.Random, start + travel * t, velocity, c.Lifetime * (0.35 + 0.65 * t),
                     rotation: Math.PI * c.Curl);
            }
        }

        protected override void DrawParticle(DrawingContext dc, CursorEffectContext c, in Particle p, double life)
        {
            double r = Math.Max(1.2, c.CellWidth * 0.2) * (0.5 + 0.5 * life);
            dc.PushOpacity(c.Opacity * life);
            dc.DrawEllipse(c.Brush, null, p.Position, r, r);
            dc.Pop();
        }
    }

    /// <summary>torpedo: exhaust thrown back against the direction of travel.</summary>
    internal sealed class TorpedoEffect : ParticleEffect
    {
        public override string Name => "torpedo";

        public override void OnJump(CursorEffectContext c, Rect from, Rect to)
        {
            var start = Center(from);
            var travel = Center(to) - start;
            double distance = travel.Length;
            if (distance < 1 || c.Lifetime <= 0) return;

            var dir = travel / distance;
            // The puffs are big: half railgun's count.
            int count = Math.Max(1, CountAlongPath(c, distance, 8) / 2);
            for (int i = 0; i < count; i++)
            {
                double t = (i + c.Random.NextDouble()) / count;
                var scatter = RandomUnit(c.Random) * 0.6;
                double spin = Math.PI * c.Curl * (c.Random.NextDouble() < 0.5 ? -1 : 1);
                Emit(c.Random, start + travel * t, (scatter - dir) * c.Speed * c.Unit,
                     c.Lifetime * (0.35 + 0.65 * t), rotation: spin);
            }
        }

        protected override void DrawParticle(DrawingContext dc, CursorEffectContext c, in Particle p, double life)
        {
            // Smoke: grows and thins as it ages.
            double r = Math.Max(1.2, c.CellWidth * 0.2) + c.CellWidth * 0.45 * (1 - life);
            dc.PushOpacity(c.Opacity * life * 0.6);
            dc.DrawEllipse(c.Brush, null, p.Position, r, r);
            dc.Pop();
        }
    }

    /// <summary>pixiedust: sparkles shaken off anywhere in the cell, falling.</summary>
    internal sealed class PixieDustEffect : ParticleEffect
    {
        public override string Name => "pixiedust";

        public override void OnJump(CursorEffectContext c, Rect from, Rect to)
        {
            var start = Center(from);
            var travel = Center(to) - start;
            double distance = travel.Length;
            if (distance < 1 || c.Lifetime <= 0) return;

            int count = CountAlongPath(c, distance, 8);
            for (int i = 0; i < count; i++)
            {
                double t = (i + c.Random.NextDouble()) / count;
                var jitter = new Vector((c.Random.NextDouble() - 0.5) * to.Width,
                                        (c.Random.NextDouble() - 0.5) * to.Height);
                var velocity = new Vector((c.Random.NextDouble() - 0.5) * 1.5,
                                          0.5 + c.Random.NextDouble()) * c.Speed * c.Unit;
                Emit(c.Random, start + travel * t + jitter, velocity,
                     c.Lifetime * (0.35 + 0.65 * t) * (0.6 + 0.4 * c.Random.NextDouble()));
            }
        }

        protected override void DrawParticle(DrawingContext dc, CursorEffectContext c, in Particle p, double life)
        {
            double r = Math.Max(1.2, c.CellWidth * 0.2) * 0.8;
            dc.PushOpacity(c.Opacity * life);
            dc.DrawRectangle(c.Brush, null, new Rect(p.Position.X - r, p.Position.Y - r, 2 * r, 2 * r));
            dc.Pop();
        }
    }

    /// <summary>sonicboom: a ring expanding from the destination, thinning as it goes.</summary>
    internal sealed class SonicBoomEffect : HighlightEffect
    {
        public override string Name => "sonicboom";

        public override void OnJump(CursorEffectContext c, Rect from, Rect to) => Start(to, c.HighlightLifetime);

        protected override void DrawHighlight(DrawingContext dc, CursorEffectContext c, in Highlight h, double t)
        {
            double size = Math.Max(h.Cell.Width, h.Cell.Height);
            double r = size * (0.5 + 2.0 * t);
            dc.PushOpacity(c.Opacity * (1 - t));
            dc.DrawEllipse(null, new Pen(c.Brush, Math.Max(1, c.CellWidth * 0.3 * (1 - t))), Center(h.Cell), r, r);
            dc.Pop();
        }
    }

    /// <summary>ripple: the cursor's own outline rippling outward.</summary>
    internal sealed class RippleEffect : HighlightEffect
    {
        public override string Name => "ripple";

        public override void OnJump(CursorEffectContext c, Rect from, Rect to) => Start(to, c.HighlightLifetime);

        protected override void DrawHighlight(DrawingContext dc, CursorEffectContext c, in Highlight h, double t)
        {
            double grow = Math.Max(h.Cell.Width, h.Cell.Height) * 1.5 * t;
            var rect = h.Cell;
            rect.Inflate(grow, grow);
            dc.PushOpacity(c.Opacity * (1 - t));
            dc.DrawRectangle(null, new Pen(c.Brush, 1 + 1.5 * (1 - t)), rect);
            dc.Pop();
        }
    }

    /// <summary>wireframe: a thin dashed box stretching wide, like a scan.</summary>
    internal sealed class WireframeEffect : HighlightEffect
    {
        public override string Name => "wireframe";

        public override void OnJump(CursorEffectContext c, Rect from, Rect to) => Start(to, c.HighlightLifetime);

        protected override void DrawHighlight(DrawingContext dc, CursorEffectContext c, in Highlight h, double t)
        {
            double size = Math.Max(h.Cell.Width, h.Cell.Height);
            var rect = h.Cell;
            rect.Inflate(size * 3 * t, size * 0.6 * t);
            dc.PushOpacity(c.Opacity * (1 - t));
            dc.DrawRectangle(null, new Pen(c.Brush, 1) { DashStyle = DashStyles.Dash }, rect);
            dc.Pop();
        }
    }
}
