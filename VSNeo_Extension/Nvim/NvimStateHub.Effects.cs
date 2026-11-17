using System;

namespace VSNeo_Extension.Nvim;

// Cursor animation, style and trails, and the mode line settings.
internal sealed partial class NvimStateHub
{
    /// <summary>
    /// Cursor trail settings (CursorTrailAdornment), from ~/.vsneorc's
    /// vsneo_cursor_* variables - Neovide's names with a vsneo_ prefix.
    /// The defaults hold until the companion's first push. Ints only on
    /// the wire (milliseconds, per mille) so no float crosses msgpack, and
    /// each value is a single word: the RPC thread writes, the UI thread
    /// reads, no lock needed.
    /// </summary>
    public int CursorAnimationMs { get; private set; } = 130;

    /// <summary>
    /// vsneo_cursor_short_animation_length: moves of at most two columns
    /// on one line (typing, h/l). Neovide's default, 0.04 s.
    /// </summary>
    public int CursorShortAnimationMs { get; private set; } = 40;

    /// <summary>
    /// SmoothScroller: Neovide's scroll_animation_length (0 = instant) and
    /// scroll_animation_far_lines (past one screen, only this many lines
    /// at the end animate).
    /// </summary>
    public int ScrollAnimationMs { get; private set; } = 300;
    public int ScrollFarLines { get; private set; } = 1;

    /// <summary>JumpBeacon: on/off, jump threshold in lines, width in columns, duration.</summary>
    public bool BeaconEnabled { get; private set; } = true;
    public int BeaconMinJump { get; private set; } = 10;
    public int BeaconWidth { get; private set; } = 40;
    public int BeaconMs { get; private set; } = 400;

    /// <summary>
    /// vsneo_reduce_effects: -1 detect software rendering (default),
    /// 0 never reduce, 1 always reduce. See Infrastructure.RenderTier.
    /// </summary>
    public int ReduceEffects { get; private set; } = -1;
    public int CursorTrailPermille { get; private set; } = 800;
    public bool CursorAnimateInInsert { get; private set; } = true;

    /// <summary>
    /// Cursor effects (Editor/Effects): vsneo_cursor_vfx_mode as a comma-separated
    /// string (empty = off), then its numbers - opacity on Neovide's 0..255
    /// scale, lifetimes in ms, density/speed/phase/curl in per mille.
    /// </summary>
    public string CursorVfxModes { get; private set; } = string.Empty;
    public int CursorVfxOpacity { get; private set; } = 200;
    public int CursorVfxLifetimeMs { get; private set; } = 500;
    public int CursorVfxHighlightLifetimeMs { get; private set; } = 200;
    public int CursorVfxDensityPermille { get; private set; } = 700;
    public int CursorVfxSpeedPermille { get; private set; } = 10000;
    public int CursorVfxPhasePermille { get; private set; } = 1500;
    public int CursorVfxCurlPermille { get; private set; } = 1000;

    /// <summary>
    /// vsneo_cursor_animation is [lengthMs, trailPermille, animateInInsert,
    /// vfxModes, vfxOpacity, lifetimeMs, highlightLifetimeMs,
    /// densityPermille, speedPermille, phasePermille, curlPermille]. The VFX
    /// tail is optional on the wire, so an older companion still parses.
    /// </summary>
    private void HandleCursorAnimation(object[] args)
    {
        if (args == null || args.Length < 3)
        {
            return;
        }

        CursorAnimationMs = Math.Max(0, ToInt(args[0]));
        CursorTrailPermille = Math.Max(0, Math.Min(1000, ToInt(args[1])));
        CursorAnimateInInsert = ToInt(args[2]) != 0;

        if (args.Length < 11)
        {
            return;
        }

        CursorVfxModes = args[3] == null ? string.Empty : AsString(args[3]) ?? string.Empty;
        CursorVfxOpacity = Math.Max(0, Math.Min(255, ToInt(args[4])));
        CursorVfxLifetimeMs = Math.Max(0, ToInt(args[5]));
        CursorVfxHighlightLifetimeMs = Math.Max(0, ToInt(args[6]));
        CursorVfxDensityPermille = Math.Max(0, ToInt(args[7]));
        CursorVfxSpeedPermille = Math.Max(0, ToInt(args[8]));
        CursorVfxPhasePermille = ToInt(args[9]);
        CursorVfxCurlPermille = ToInt(args[10]);

        // The short-move length is a later addition to the tail.
        if (args.Length >= 12)
        {
            CursorShortAnimationMs = Math.Max(0, ToInt(args[11]));
        }

        // Smooth scroll and jump beacon, later still.
        if (args.Length >= 18)
        {
            ScrollAnimationMs = Math.Max(0, ToInt(args[12]));
            ScrollFarLines = Math.Max(0, ToInt(args[13]));
            BeaconEnabled = ToInt(args[14]) > 0;
            BeaconMinJump = Math.Max(1, ToInt(args[15]));
            BeaconWidth = Math.Max(1, ToInt(args[16]));
            BeaconMs = Math.Max(0, ToInt(args[17]));
        }

        if (args.Length >= 19)
        {
            ReduceEffects = Math.Max(-1, Math.Min(1, ToInt(args[18])));
        }
    }

    /// <summary>
    /// VSNeo's own cursor (CustomCursorAdornment), from ~/.vsneorc's
    /// vsneo_cursor_style / vsneo_cursor_blinking. Off until the rc sets
    /// either, so Visual Studio's caret stays untouched by default.
    /// Styles are indexed normal, insert, replace, visual, operator-pending,
    /// cmdline. The array is replaced, never mutated, so readers can cache
    /// by reference.
    /// </summary>
    public bool CursorStyleEnabled { get; private set; }
    public string[] CursorStyles { get; private set; } = new string[6];
    public string CursorBlinking { get; private set; } = "blink";

    /// <summary>
    /// vsneo_cursor_color per mode (same slots as CursorStyles) as 0xRRGGBB,
    /// -1 for "the theme's caret color". Replaced, never mutated.
    /// </summary>
    public int[] CursorColors { get; private set; } = { -1, -1, -1, -1, -1, -1 };

    /// <summary>vsneo_cursor_glow blur radius in pixels, 0 for none.</summary>
    public int CursorGlow { get; private set; }

    /// <summary>
    /// ModeLineTint: vsneo_mode_line (off by default) and the tint's
    /// opacity, vsneo_mode_line_opacity, in per mille.
    /// </summary>
    public bool ModeLineEnabled { get; private set; }
    public int ModeLineOpacityPermille { get; private set; } = 120;

    public event Action CursorStyleChanged = null!;

    /// <summary>
    /// vsneo_cursor_style is [enabled, normal, insert, replace, visual,
    /// operator, cmdline, blinking, then six colors in the same mode order
    /// and the glow radius].
    /// </summary>
    // The last push, verbatim: the companion sends the style on every
    // SourcePost, which fires for each ftplugin, indent and syntax file
    // sourced on a FileType - half a dozen pushes per opened C# file, each
    // one fanned out as a UI post to every open view. An identical push
    // changes nothing and raises nothing.
    private object[]? _lastCursorStyleArgs;

    private static bool SameArgs(object[]? a, object[] b)
    {
        if (a == null || a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (!Equals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void HandleCursorStyle(object[] args)
    {
        if (args == null || args.Length < 8)
        {
            return;
        }

        if (SameArgs(_lastCursorStyleArgs, args))
        {
            return;
        }

        _lastCursorStyleArgs = args;

        var styles = new string[6];
        for (int i = 0; i < 6; i++)
        {
            styles[i] = args[1 + i] == null ? string.Empty : AsString(args[1 + i]) ?? string.Empty;
        }

        CursorStyles = styles;
        CursorBlinking = args[7] == null ? "blink" : AsString(args[7]) ?? "blink";

        // Colors and glow are an optional tail, so an older companion
        // still parses.
        if (args.Length >= 15)
        {
            var colors = new int[6];
            for (int i = 0; i < 6; i++)
            {
                int rgb = ToInt(args[8 + i]);
                colors[i] = rgb < 0 || rgb > 0xFFFFFF ? -1 : rgb;
            }

            CursorColors = colors;
            CursorGlow = Math.Max(0, Math.Min(60, ToInt(args[14])));
        }

        if (args.Length >= 17)
        {
            ModeLineEnabled = ToInt(args[15]) > 0;
            ModeLineOpacityPermille = Math.Max(0, Math.Min(1000, ToInt(args[16])));
        }

        CursorStyleEnabled = ToInt(args[0]) > 0;
        CursorStyleChanged?.Invoke();
    }
}
