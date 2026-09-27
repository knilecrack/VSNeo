using System;
using System.Windows;
using System.Windows.Media;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor.Effects
{
    /// <summary>
    /// One cursor effect: something drawn around the cursor in response to
    /// what the cursor does. The host (<see cref="CursorEffectHost"/>) owns
    /// the frame loop, the drawing surface, the color and the settings; an
    /// effect only reacts to triggers, advances its own state, and draws.
    ///
    /// Adding an effect:
    ///   1. Subclass <see cref="CursorEffect"/> - or <see cref="ParticleEffect"/>
    ///      for particles, <see cref="HighlightEffect"/> for a timed one-shot
    ///      flash - and override only the triggers it reacts to.
    ///   2. Register a name in <see cref="CursorEffectRegistry"/>'s built-in list.
    ///   3. Users turn it on with that name in vim.g.vsneo_cursor_vfx_mode
    ///      (a string or a list), and ':source' applies it live.
    ///
    /// Contract:
    /// - Everything runs on the UI thread, inside the editor's own frame. A
    ///   trigger or Update that blocks freezes Visual Studio; keep them to
    ///   arithmetic and array writes.
    /// - Rects and points are viewport-relative (0,0 is the top-left of what
    ///   is on screen); the host maps them into the view.
    /// - Update(dt) advances by dt seconds (clamped to at most 1/30 s). The
    ///   frame loop runs only while some effect reports <see cref="IsActive"/>,
    ///   so an effect that finished must say so, or it keeps the editor
    ///   repainting.
    /// - Render draws the current state with the context's colors and
    ///   opacity. It must not allocate per particle; a few objects per frame
    ///   (a Pen) are fine.
    /// - An effect that throws is removed from the session and logged; it
    ///   never takes the editor down, and it never throws twice.
    /// </summary>
    internal interface ICursorEffect
    {
        /// <summary>The name users put in vsneo_cursor_vfx_mode. Lowercase, no spaces.</summary>
        string Name { get; }

        /// <summary>
        /// True when this effect should stand down under software rendering
        /// (Remote Desktop, a GPU-less VM): anything that redraws much of the
        /// screen or many shapes per frame.
        /// </summary>
        bool IsCostly { get; }

        /// <summary>True while there is anything left to animate or draw.</summary>
        bool IsActive { get; }

        /// <summary>The cursor jumped from one cell to another (both viewport-relative).</summary>
        void OnJump(CursorEffectContext context, Rect from, Rect to);

        /// <summary>A character was typed at <paramref name="cell"/> (insert/replace mode).</summary>
        void OnType(CursorEffectContext context, Rect cell);

        /// <summary>The Vim mode changed while the cursor sat at <paramref name="cell"/>.</summary>
        void OnModeChanged(CursorEffectContext context, VimMode from, VimMode to, Rect cell);

        /// <summary>The editor gained focus with the cursor at <paramref name="cell"/>.</summary>
        void OnFocus(CursorEffectContext context, Rect cell);

        /// <summary>Advance by <paramref name="dt"/> seconds.</summary>
        void Update(double dt);

        /// <summary>Draw the current state.</summary>
        void Render(DrawingContext dc, CursorEffectContext context);

        /// <summary>Drop everything at once (focus lost, effect turned off).</summary>
        void Clear();
    }

    /// <summary>
    /// What an effect may know about the moment: the cursor's color, the
    /// user's settings (Neovide's vfx names, already converted to seconds and
    /// 0..1), the cell size, the viewport, and a shared random source.
    /// Refreshed by the host before every trigger and render; effects read
    /// it and must not keep a reference past the call.
    /// </summary>
    internal sealed class CursorEffectContext
    {
        /// <summary>The cursor's color for the current mode, opaque.</summary>
        public Color Color { get; internal set; } = Colors.Gray;

        /// <summary><see cref="Color"/> as a frozen brush; apply opacity with PushOpacity.</summary>
        public Brush Brush { get; internal set; } = Brushes.Gray;

        /// <summary>vsneo_cursor_vfx_opacity, 0..1.</summary>
        public double Opacity { get; internal set; } = 200 / 255.0;

        /// <summary>vsneo_cursor_vfx_particle_lifetime, seconds.</summary>
        public double Lifetime { get; internal set; } = 0.5;

        /// <summary>vsneo_cursor_vfx_particle_highlight_lifetime, seconds.</summary>
        public double HighlightLifetime { get; internal set; } = 0.2;

        /// <summary>vsneo_cursor_vfx_particle_density (Neovide's scale, default 0.7).</summary>
        public double Density { get; internal set; } = 0.7;

        /// <summary>vsneo_cursor_vfx_particle_speed (Neovide's scale, default 10).</summary>
        public double Speed { get; internal set; } = 10;

        /// <summary>vsneo_cursor_vfx_particle_phase (default 1.5).</summary>
        public double Phase { get; internal set; } = 1.5;

        /// <summary>vsneo_cursor_vfx_particle_curl (default 1).</summary>
        public double Curl { get; internal set; } = 1;

        /// <summary>Width of one character cell, pixels.</summary>
        public double CellWidth { get; internal set; } = 8;

        /// <summary>Height of one text line, pixels.</summary>
        public double CellHeight { get; internal set; } = 16;

        /// <summary>
        /// A length unit that scales with the font (a sixth of a line).
        /// Multiply speeds by it so zoom does not change how an effect looks.
        /// </summary>
        public double Unit => Math.Max(4, CellHeight) / 6.0;

        /// <summary>The visible editor area, pixels.</summary>
        public Size Viewport { get; internal set; }

        /// <summary>For text effects (FormattedText needs it).</summary>
        public double PixelsPerDip { get; internal set; } = 1;

        /// <summary>The current Vim mode.</summary>
        public VimMode Mode { get; internal set; }

        /// <summary>Shared random source; do not create your own per frame.</summary>
        public Random Random { get; } = new Random();
    }
}
