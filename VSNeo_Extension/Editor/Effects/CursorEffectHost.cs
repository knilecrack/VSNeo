using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor.Effects
{
    /// <summary>
    /// The one element every cursor effect draws into, and the only thing
    /// the cursor trail talks to. It turns the user's vsneo_cursor_vfx_mode
    /// into live effect instances, forwards triggers, advances and draws
    /// them, and isolates them: an effect that throws is dropped for the
    /// session and logged, so a bug in one effect cannot take the editor (or
    /// the other effects) down.
    ///
    /// Coordinates are viewport-relative; the owner keeps this element on the
    /// viewport's origin and sized to it.
    /// </summary>
    internal sealed class CursorEffectHost : FrameworkElement
    {
        private readonly List<ICursorEffect> _effects = new List<ICursorEffect>();
        private readonly CursorEffectContext _context = new CursorEffectContext();
        private string? _configuredText;
        private Color _brushColor;
        private bool _drawnSomething;

        public CursorEffectHost()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = false;
        }

        public bool HasEffects => _effects.Count > 0;

        /// <summary>
        /// Makes the active effects match <paramref name="modes"/> (the raw
        /// vsneo_cursor_vfx_mode value). Instances of effects still listed are
        /// kept, so reconfiguring does not cut a running animation short.
        /// </summary>
        public void Configure(string? modes)
        {
            if (string.Equals(modes, _configuredText, StringComparison.Ordinal)) return;
            _configuredText = modes;

            var names = CursorEffectRegistry.ParseNames(modes);
            var next = new List<ICursorEffect>(names.Count);
            foreach (var name in names)
            {
                var existing = _effects.Find(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                ICursorEffect? effect = existing;
                if (effect == null)
                {
                    try { effect = CursorEffectRegistry.Create(name); }
                    catch (Exception ex) { Infrastructure.Log.Write("cursor effect '" + name + "' failed to create", ex); }
                }
                if (effect != null) next.Add(effect);
            }
            // A copy: Safe removes an effect that throws.
            foreach (var old in _effects.ToArray())
                if (!next.Contains(old)) Safe(old, e => e.Clear());

            _effects.Clear();
            _effects.AddRange(next);
            InvalidateVisual();
        }

        /// <summary>
        /// Refreshes what effects may know about the moment. Called by the
        /// owner before every trigger.
        /// </summary>
        public CursorEffectContext Prepare(NvimStateHub state, Color color, Rect cell)
        {
            var c = _context;
            if (color != _brushColor || c.Brush == Brushes.Gray)
            {
                _brushColor = color;
                c.Color = Color.FromRgb(color.R, color.G, color.B);
                var brush = new SolidColorBrush(c.Color);
                brush.Freeze();
                c.Brush = brush;
            }
            c.Opacity = Math.Max(0, Math.Min(1, state.CursorVfxOpacity / 255.0));
            c.Lifetime = state.CursorVfxLifetimeMs / 1000.0;
            c.HighlightLifetime = state.CursorVfxHighlightLifetimeMs / 1000.0;
            c.Density = state.CursorVfxDensityPermille / 1000.0;
            c.Speed = state.CursorVfxSpeedPermille / 1000.0;
            c.Phase = state.CursorVfxPhasePermille / 1000.0;
            c.Curl = state.CursorVfxCurlPermille / 1000.0;
            c.CellWidth = Math.Max(cell.Width, 2);
            c.CellHeight = Math.Max(cell.Height, 4);
            c.Viewport = new Size(Math.Max(1, Width), Math.Max(1, Height));
            c.Mode = state.Mode;
            try { c.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { /* keep the last */ }
            return c;
        }

        // Triggers. `reduce`: software rendering, so costly effects sit it out.
        public void Jump(Rect from, Rect to, bool reduce) =>
            Each(reduce, e => e.OnJump(_context, from, to));

        public void Type(Rect cell, bool reduce) =>
            Each(reduce, e => e.OnType(_context, cell));

        public void ModeChanged(VimMode from, VimMode to, Rect cell, bool reduce) =>
            Each(reduce, e => e.OnModeChanged(_context, from, to, cell));

        public void Focus(Rect cell, bool reduce) =>
            Each(reduce, e => e.OnFocus(_context, cell));

        /// <summary>Advances every active effect; true while any still is.</summary>
        public bool Update(double dt)
        {
            bool any = false;
            for (int i = 0; i < _effects.Count; i++)
            {
                var effect = _effects[i];
                if (!effect.IsActive) continue;
                // Inline try/catch, not Safe(effect, e => e.Update(dt)): that
                // shape allocated a capturing delegate per effect per frame.
                try { effect.Update(dt); }
                catch (Exception ex) { Disable(effect, ex); i--; continue; }
                any |= effect.IsActive;
            }

            if (any || _drawnSomething)
            {
                _drawnSomething = any;
                InvalidateVisual();
            }
            return any;
        }

        public void Clear()
        {
            foreach (var e in _effects.ToArray()) Safe(e, x => x.Clear());   // a copy: Safe may remove
            if (_drawnSomething) InvalidateVisual();
            _drawnSomething = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            for (int i = 0; i < _effects.Count; i++)
            {
                var effect = _effects[i];
                if (!effect.IsActive) continue;
                try { effect.Render(dc, _context); }
                catch (Exception ex) { Disable(effect, ex); i--; }
            }
        }

        /// <summary>The per-frame twin of <see cref="Safe"/>: same removal, same log, no delegate.</summary>
        private void Disable(ICursorEffect effect, Exception ex)
        {
            Infrastructure.Log.Write("cursor effect '" + effect.Name + "' threw and was turned off", ex);
            _effects.Remove(effect);
        }

        private void Each(bool reduce, Action<ICursorEffect> action)
        {
            for (int i = 0; i < _effects.Count; i++)
            {
                var effect = _effects[i];
                if (reduce && effect.IsCostly) continue;
                if (!Safe(effect, action)) i--;
            }
        }

        /// <summary>
        /// Runs one effect call; on a throw the effect is removed (for this
        /// view, until the next reconfigure) and logged once. False when it
        /// was removed.
        /// </summary>
        private bool Safe(ICursorEffect effect, Action<ICursorEffect> action)
        {
            try
            {
                action(effect);
                return true;
            }
            catch (Exception ex)
            {
                Infrastructure.Log.Write("cursor effect '" + effect.Name + "' threw and was turned off", ex);
                _effects.Remove(effect);
                return false;
            }
        }
    }
}
