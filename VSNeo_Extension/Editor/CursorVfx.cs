using System;
using System.Windows;
using System.Windows.Media;

namespace VSNeo_Extension.Editor
{
    /// <summary>Neovide's cursor VFX modes, combinable.</summary>
    [Flags]
    internal enum VfxModes
    {
        None = 0,
        // Particle trails, spawned along the path of a jump.
        Railgun = 1,
        Torpedo = 2,
        PixieDust = 4,
        // Highlights, drawn once at the destination.
        SonicBoom = 8,
        Ripple = 16,
        Wireframe = 32,
    }

    /// <summary>
    /// One jump's worth of VFX settings, read from the hub when the jump
    /// starts. Units follow Neovide's: seconds for lifetimes, 0..1 opacity
    /// (Neovide's 0..255 divided down), and its unitless density, speed,
    /// phase and curl.
    /// </summary>
    internal readonly struct VfxSettings
    {
        public readonly VfxModes Modes;
        public readonly double Opacity;
        public readonly double Lifetime;
        public readonly double HighlightLifetime;
        public readonly double Density;
        public readonly double Speed;
        public readonly double Phase;
        public readonly double Curl;

        public VfxSettings(VfxModes modes, double opacity, double lifetime, double highlightLifetime,
                           double density, double speed, double phase, double curl)
        {
            Modes = modes;
            Opacity = opacity;
            Lifetime = lifetime;
            HighlightLifetime = highlightLifetime;
            Density = density;
            Speed = speed;
            Phase = phase;
            Curl = curl;
        }

        /// <summary>
        /// Neovide's mode names, separated by commas or spaces (the companion
        /// joins a Lua list with commas). Unknown names are ignored.
        /// </summary>
        public static VfxModes ParseModes(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return VfxModes.None;

            var modes = VfxModes.None;
            foreach (var raw in text!.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "railgun": modes |= VfxModes.Railgun; break;
                    case "torpedo": modes |= VfxModes.Torpedo; break;
                    case "pixiedust": modes |= VfxModes.PixieDust; break;
                    case "sonicboom": modes |= VfxModes.SonicBoom; break;
                    case "ripple": modes |= VfxModes.Ripple; break;
                    case "wireframe": modes |= VfxModes.Wireframe; break;
                }
            }
            return modes;
        }
    }

    /// <summary>
    /// Neovide's cursor VFX: particles spawned along a jump's path and
    /// highlights at its destination, simulated per frame by the cursor
    /// trail's frame loop and drawn by this one element's OnRender - one
    /// DrawEllipse per particle, no WPF object per particle, nothing
    /// allocated per frame beyond the handful of highlight pens.
    ///
    /// Coordinates are viewport-relative, like the trail's; the owner keeps
    /// this element positioned at the viewport's origin. Particles are
    /// screen-space, as in Neovide: a scroll does not drag them along.
    /// </summary>
    internal sealed class CursorVfx : FrameworkElement
    {
        // A long jump at high density could ask for thousands; the cap keeps
        // one frame's OnRender bounded no matter what the rc says.
        private const int MaxParticles = 400;
        private const int MaxPerJump = 150;
        private const int MaxHighlights = 8;

        private struct Particle
        {
            public Point Position;
            public Vector Velocity;
            public double Rotation;   // radians per second applied to Velocity
            public double Life;       // seconds left
            public double MaxLife;
            public VfxModes Kind;
        }

        private struct Highlight
        {
            public Rect Cell;
            public double Age;
            public double Life;
            public VfxModes Kind;
        }

        private readonly Particle[] _particles = new Particle[MaxParticles];
        private int _particleCount;
        private readonly Highlight[] _highlights = new Highlight[MaxHighlights];
        private int _highlightCount;
        private readonly Random _rng = new Random();

        private double _opacity = 200 / 255.0;
        private double _cellWidth = 8;
        private Brush _brush = Brushes.Gray;
        private bool _drawnSomething;

        public CursorVfx()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = false;
        }

        public bool Alive => _particleCount > 0 || _highlightCount > 0;

        /// <summary>The caret color; opacity is applied per particle, so opaque here.</summary>
        public void SetColor(Color color)
        {
            // Called on every jump; a new brush only when the color changed.
            if (_brush is SolidColorBrush current
                && current.Color.R == color.R && current.Color.G == color.G && current.Color.B == color.B)
                return;
            var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            brush.Freeze();
            _brush = brush;
        }

        public void Clear()
        {
            _particleCount = 0;
            _highlightCount = 0;
            if (_drawnSomething) InvalidateVisual();
            _drawnSomething = false;
        }

        /// <summary>
        /// The cursor jumped from <paramref name="from"/> to <paramref name="to"/>
        /// (cell rects, viewport-relative). Spawns the trail particles along the
        /// path and the destination highlights.
        /// </summary>
        public void Jump(Rect from, Rect to, VfxSettings s)
        {
            if (s.Modes == VfxModes.None) return;

            _opacity = Math.Max(0, Math.Min(1, s.Opacity));
            _cellWidth = Math.Max(to.Width, 2);
            // Speeds scale with the font: Neovide's numbers are tuned for a
            // terminal cell, and a fixed px/s would look different at every
            // zoom level.
            double cellHeight = Math.Max(to.Height, 4);
            double unit = cellHeight / 6.0;

            var start = Center(from);
            var end = Center(to);
            var travel = end - start;
            double distance = travel.Length;

            if (distance >= 1 && s.Lifetime > 0
                && (s.Modes & (VfxModes.Railgun | VfxModes.Torpedo | VfxModes.PixieDust)) != 0)
            {
                var dir = travel / distance;
                // About eight particles per line of travel at Neovide's
                // default density (0.7); torpedo's puffs are big, so half.
                int count = (int)Math.Round(distance / cellHeight * s.Density * 8);
                count = Math.Max(1, Math.Min(MaxPerJump, count));

                for (int i = 0; i < count; i++)
                {
                    // 0 at the start of the path, 1 at the destination. The
                    // start dies first, so the trail recedes toward the cursor.
                    double t = (i + _rng.NextDouble()) / count;
                    var at = start + travel * t;
                    double life = s.Lifetime * (0.35 + 0.65 * t);

                    if ((s.Modes & VfxModes.Railgun) != 0)
                    {
                        // A helix: the launch angle advances along the path
                        // (phase), and every particle keeps turning (curl).
                        double angle = t / Math.PI * s.Phase * (distance / cellHeight);
                        var velocity = new Vector(Math.Sin(angle), Math.Cos(angle)) * 2 * s.Speed * unit;
                        Add(at, velocity, Math.PI * s.Curl, life, VfxModes.Railgun);
                    }

                    if ((s.Modes & VfxModes.Torpedo) != 0 && (i & 1) == 0)
                    {
                        // Exhaust: thrown back against the direction of travel,
                        // scattered, curling as it drifts.
                        var scatter = RandomUnit() * 0.6;
                        var velocity = (scatter - dir) * s.Speed * unit;
                        double spin = Math.PI * s.Curl * (_rng.NextDouble() < 0.5 ? -1 : 1);
                        Add(at, velocity, spin, life, VfxModes.Torpedo);
                    }

                    if ((s.Modes & VfxModes.PixieDust) != 0)
                    {
                        // Sparkles shaken off anywhere in the cell, falling.
                        var jitter = new Vector((_rng.NextDouble() - 0.5) * to.Width,
                                                (_rng.NextDouble() - 0.5) * to.Height);
                        var velocity = new Vector((_rng.NextDouble() - 0.5) * 1.5,
                                                  0.5 + _rng.NextDouble()) * s.Speed * unit;
                        Add(at + jitter, velocity, 0, life * (0.6 + 0.4 * _rng.NextDouble()),
                            VfxModes.PixieDust);
                    }
                }
            }

            if (s.HighlightLifetime > 0)
            {
                if ((s.Modes & VfxModes.SonicBoom) != 0) AddHighlight(to, s.HighlightLifetime, VfxModes.SonicBoom);
                if ((s.Modes & VfxModes.Ripple) != 0) AddHighlight(to, s.HighlightLifetime, VfxModes.Ripple);
                if ((s.Modes & VfxModes.Wireframe) != 0) AddHighlight(to, s.HighlightLifetime, VfxModes.Wireframe);
            }
        }

        private void Add(Point position, Vector velocity, double rotation, double life, VfxModes kind)
        {
            if (life <= 0) return;

            // Full: the oldest-spawned slot is not tracked, so overwrite a
            // random one - a dense railgun at the cap thins evenly instead of
            // freezing at whatever spawned first.
            int slot = _particleCount < MaxParticles ? _particleCount++ : _rng.Next(MaxParticles);
            _particles[slot] = new Particle
            {
                Position = position,
                Velocity = velocity,
                Rotation = rotation,
                Life = life,
                MaxLife = life,
                Kind = kind,
            };
        }

        private void AddHighlight(Rect cell, double life, VfxModes kind)
        {
            int slot = _highlightCount < MaxHighlights ? _highlightCount++ : 0;
            _highlights[slot] = new Highlight { Cell = cell, Age = 0, Life = life, Kind = kind };
        }

        /// <summary>
        /// Advances the simulation by <paramref name="dt"/> seconds and
        /// repaints. Returns whether anything is still alive - the frame loop
        /// stops once neither this nor the trail needs it.
        /// </summary>
        public bool Update(double dt)
        {
            if (!Alive)
            {
                if (_drawnSomething) { _drawnSomething = false; InvalidateVisual(); }
                return false;
            }

            for (int i = 0; i < _particleCount; i++)
            {
                ref var p = ref _particles[i];
                p.Life -= dt;
                if (p.Life <= 0)
                {
                    // Swap-remove, and revisit this slot: it now holds the last one.
                    _particles[i] = _particles[--_particleCount];
                    i--;
                    continue;
                }

                p.Position += p.Velocity * dt;
                if (p.Rotation != 0)
                {
                    double a = p.Rotation * dt;
                    double cos = Math.Cos(a), sin = Math.Sin(a);
                    p.Velocity = new Vector(p.Velocity.X * cos - p.Velocity.Y * sin,
                                            p.Velocity.X * sin + p.Velocity.Y * cos);
                }
            }

            for (int i = 0; i < _highlightCount; i++)
            {
                ref var h = ref _highlights[i];
                h.Age += dt;
                if (h.Age >= h.Life)
                {
                    _highlights[i] = _highlights[--_highlightCount];
                    i--;
                }
            }

            _drawnSomething = true;
            InvalidateVisual();
            return Alive;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var brush = _brush;
            double baseRadius = Math.Max(1.2, _cellWidth * 0.2);

            for (int i = 0; i < _particleCount; i++)
            {
                var p = _particles[i];
                double life = p.Life / p.MaxLife;   // 1 at birth, 0 at death

                switch (p.Kind)
                {
                    case VfxModes.Railgun:
                    {
                        double r = baseRadius * (0.5 + 0.5 * life);
                        dc.PushOpacity(_opacity * life);
                        dc.DrawEllipse(brush, null, p.Position, r, r);
                        dc.Pop();
                        break;
                    }
                    case VfxModes.Torpedo:
                    {
                        // Smoke: grows and thins as it ages.
                        double r = baseRadius + _cellWidth * 0.45 * (1 - life);
                        dc.PushOpacity(_opacity * life * 0.6);
                        dc.DrawEllipse(brush, null, p.Position, r, r);
                        dc.Pop();
                        break;
                    }
                    case VfxModes.PixieDust:
                    {
                        double r = baseRadius * 0.8;
                        dc.PushOpacity(_opacity * life);
                        dc.DrawRectangle(brush, null, new Rect(p.Position.X - r, p.Position.Y - r, 2 * r, 2 * r));
                        dc.Pop();
                        break;
                    }
                }
            }

            for (int i = 0; i < _highlightCount; i++)
            {
                var h = _highlights[i];
                double t = h.Age / h.Life;          // 0 at birth, 1 at death
                var cell = h.Cell;
                var center = Center(cell);
                double size = Math.Max(cell.Width, cell.Height);

                dc.PushOpacity(_opacity * (1 - t));
                switch (h.Kind)
                {
                    case VfxModes.SonicBoom:
                    {
                        // An expanding ring that thins as it goes.
                        double r = size * (0.5 + 2.0 * t);
                        var pen = new Pen(brush, Math.Max(1, _cellWidth * 0.3 * (1 - t)));
                        dc.DrawEllipse(null, pen, center, r, r);
                        break;
                    }
                    case VfxModes.Ripple:
                    {
                        // The cursor's own outline, rippling outward.
                        double grow = size * 1.5 * t;
                        var rect = cell;
                        rect.Inflate(grow, grow);
                        dc.DrawRectangle(null, new Pen(brush, 1 + 1.5 * (1 - t)), rect);
                        break;
                    }
                    case VfxModes.Wireframe:
                    {
                        // A thin dashed box stretching wide, like a scan line.
                        var rect = cell;
                        rect.Inflate(size * 3 * t, size * 0.6 * t);
                        var pen = new Pen(brush, 1) { DashStyle = DashStyles.Dash };
                        dc.DrawRectangle(null, pen, rect);
                        break;
                    }
                }
                dc.Pop();
            }
        }

        private Vector RandomUnit()
        {
            double a = _rng.NextDouble() * 2 * Math.PI;
            return new Vector(Math.Cos(a), Math.Sin(a));
        }

        private static Point Center(Rect r) => new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }
}
