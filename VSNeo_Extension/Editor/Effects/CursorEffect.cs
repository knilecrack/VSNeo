using System;
using System.Windows;
using System.Windows.Media;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor.Effects
{
    /// <summary>
    /// The base to subclass. Every trigger is a no-op here, so an effect
    /// overrides only what it reacts to - an abstract class rather than
    /// default interface methods, which .NET Framework 4.8 (Visual Studio's
    /// runtime) does not support. The helpers are the ones every built-in
    /// effect needed.
    /// </summary>
    internal abstract class CursorEffect : ICursorEffect
    {
        public abstract string Name { get; }
        public virtual bool IsCostly => true;
        public abstract bool IsActive { get; }

        public virtual void OnJump(CursorEffectContext context, Rect from, Rect to) { }
        public virtual void OnType(CursorEffectContext context, Rect cell) { }
        public virtual void OnModeChanged(CursorEffectContext context, VimMode from, VimMode to, Rect cell) { }
        public virtual void OnFocus(CursorEffectContext context, Rect cell) { }

        public abstract void Update(double dt);
        public abstract void Render(DrawingContext dc, CursorEffectContext context);
        public abstract void Clear();

        protected static Point Center(Rect r) => new Point(r.X + r.Width / 2, r.Y + r.Height / 2);

        protected static Vector RandomUnit(Random rng)
        {
            double a = rng.NextDouble() * 2 * Math.PI;
            return new Vector(Math.Cos(a), Math.Sin(a));
        }

        /// <summary>
        /// A single step rather than a jump: a typed character, h/l, j/k - the
        /// cursor moved to an adjacent row, or at most two columns along its
        /// own. Every one of these fires OnJump; an effect big enough to be
        /// noise at typing speed (a flash, a sweep, a trace) skips them.
        /// </summary>
        protected static bool IsStep(CursorEffectContext context, Rect from, Rect to)
        {
            var d = Center(to) - Center(from);
            double rows = Math.Abs(d.Y) / Math.Max(1, context.CellHeight);
            if (rows >= 1.5) return false;
            return rows >= 0.5 || Math.Abs(d.X) <= context.CellWidth * 2.5;
        }

        /// <summary>The jump left its row by more than one line (G, n, {, a click).</summary>
        protected static bool IsLineJump(CursorEffectContext context, Rect from, Rect to) =>
            Math.Abs(Center(to).Y - Center(from).Y) >= context.CellHeight * 1.5;

        protected static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// How many particles a path of <paramref name="distance"/> pixels
        /// gets: <paramref name="perLine"/> per line of travel at the user's
        /// density, at least one, capped so one jump cannot flood a frame.
        /// </summary>
        protected static int CountAlongPath(CursorEffectContext context, double distance, double perLine,
                                            int max = 150)
        {
            int count = (int)Math.Round(distance / Math.Max(4, context.CellHeight) * context.Density * perLine);
            return Math.Max(1, Math.Min(max, count));
        }
    }

    /// <summary>
    /// Base for particle effects. Subclasses spawn with <see cref="Emit"/> from
    /// their triggers and draw one particle in <see cref="DrawParticle"/>; the
    /// storage (a fixed array, no allocation per particle), the physics
    /// (velocity, acceleration, velocity rotation) and the lifetime are here.
    /// </summary>
    internal abstract class ParticleEffect : CursorEffect
    {
        protected struct Particle
        {
            public Point Position;
            public Vector Velocity;
            public Vector Acceleration;   // px/s², e.g. gravity
            public double Rotation;       // radians per second applied to Velocity
            public double Life;           // seconds left
            public double MaxLife;
            public int Tag;               // effect-defined (a glyph index, a variant)
        }

        private Particle[]? _particles;
        private int _count;

        /// <summary>Most particles alive at once; the oldest-first slot is recycled beyond it.</summary>
        protected virtual int MaxParticles => 400;

        public override bool IsActive => _count > 0;

        protected void Emit(Random rng, Point position, Vector velocity, double life,
                            double rotation = 0, Vector acceleration = default, int tag = 0)
        {
            if (life <= 0) return;
            var particles = _particles ??= new Particle[MaxParticles];

            // Full: overwrite a random slot, so a dense burst at the cap thins
            // evenly instead of freezing whatever spawned first.
            int slot = _count < particles.Length ? _count++ : rng.Next(particles.Length);
            particles[slot] = new Particle
            {
                Position = position,
                Velocity = velocity,
                Acceleration = acceleration,
                Rotation = rotation,
                Life = life,
                MaxLife = life,
                Tag = tag,
            };
        }

        public override void Update(double dt)
        {
            var particles = _particles;
            if (particles == null) return;

            for (int i = 0; i < _count; i++)
            {
                ref var p = ref particles[i];
                p.Life -= dt;
                if (p.Life <= 0)
                {
                    // Swap-remove, and revisit this slot: it now holds the last one.
                    particles[i] = particles[--_count];
                    i--;
                    continue;
                }

                p.Velocity += p.Acceleration * dt;
                p.Position += p.Velocity * dt;
                if (p.Rotation != 0)
                {
                    double a = p.Rotation * dt;
                    double cos = Math.Cos(a), sin = Math.Sin(a);
                    p.Velocity = new Vector(p.Velocity.X * cos - p.Velocity.Y * sin,
                                            p.Velocity.X * sin + p.Velocity.Y * cos);
                }
            }
        }

        public override void Render(DrawingContext dc, CursorEffectContext context)
        {
            var particles = _particles;
            if (particles == null) return;
            for (int i = 0; i < _count; i++)
                DrawParticle(dc, context, in particles[i], particles[i].Life / particles[i].MaxLife);
        }

        /// <summary>Draw one particle. <paramref name="life"/> runs 1 at birth to 0 at death.</summary>
        protected abstract void DrawParticle(DrawingContext dc, CursorEffectContext context, in Particle p, double life);

        public override void Clear() => _count = 0;
    }

    /// <summary>
    /// Base for timed one-shot effects at a cell (a ring, a flash, a sweep).
    /// Subclasses call <see cref="Start"/> from their triggers and draw in
    /// <see cref="DrawHighlight"/> given t, which runs 0 at birth to 1 at end.
    /// </summary>
    internal abstract class HighlightEffect : CursorEffect
    {
        protected struct Highlight
        {
            public Rect Cell;
            public double Age;
            public double Life;
            public int Seed;     // effect-defined randomness that must hold still across frames
        }

        private readonly Highlight[] _highlights = new Highlight[8];
        private int _count;

        public override bool IsActive => _count > 0;

        protected void Start(Rect cell, double life, int seed = 0)
        {
            if (life <= 0) return;
            int slot = _count < _highlights.Length ? _count++ : 0;
            _highlights[slot] = new Highlight { Cell = cell, Age = 0, Life = life, Seed = seed };
        }

        public override void Update(double dt)
        {
            for (int i = 0; i < _count; i++)
            {
                _highlights[i].Age += dt;
                if (_highlights[i].Age >= _highlights[i].Life)
                {
                    _highlights[i] = _highlights[--_count];
                    i--;
                }
            }
        }

        public override void Render(DrawingContext dc, CursorEffectContext context)
        {
            for (int i = 0; i < _count; i++)
                DrawHighlight(dc, context, in _highlights[i], _highlights[i].Age / _highlights[i].Life);
        }

        /// <summary>Draw one highlight. <paramref name="t"/> runs 0 at birth to 1 at the end.</summary>
        protected abstract void DrawHighlight(DrawingContext dc, CursorEffectContext context, in Highlight h, double t);

        public override void Clear() => _count = 0;
    }
}
