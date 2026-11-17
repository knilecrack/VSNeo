using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

/// <summary>VS Code's editor.cursorStyle values.</summary>
internal enum CursorShape { Block, BlockOutline, Line, LineThin, Underline, UnderlineThin }

/// <summary>VS Code's editor.cursorBlinking values.</summary>
internal enum CursorBlinking { Blink, Smooth, Phase, Expand, Solid }

/// <summary>
/// guicursor's two halves as text: which shape and which blink the companion
/// pushed, per mode. The adornment keeps the caches (the hub hands back the
/// same array until new settings arrive); this answers what the text means,
/// which is the part worth testing without a WPF view.
/// </summary>
internal static class CursorStyleDecision
{
    /// <summary>
    /// One slot per mode the settings carry, plus the fallback at 0: normal,
    /// insert, replace, visual, operator-pending, cmdline. Shared by
    /// vsneo_cursor_style and vsneo_cursor_color.
    /// </summary>
    public const int ModeSlots = 6;

    public static int ModeIndex(VimMode mode)
    {
        switch (mode)
        {
            case VimMode.Insert:
                return 1;
            case VimMode.Replace:
                return 2;
            case VimMode.Visual:
                return 3;
            case VimMode.OperatorPending:
                return 4;
            case VimMode.CmdLine:
                return 5;
            default:
                return 0;
        }
    }

    /// <summary>
    /// One pushed style string. Anything unrecognised (and null) is a block:
    /// the same shape Visual Studio draws, so a typo in the rc degrades to the
    /// plain cursor rather than to nothing.
    /// </summary>
    public static CursorShape ParseShape(string? text)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "block-outline":
            case "outline":
            case "hollow":
                return CursorShape.BlockOutline;
            case "line":
            case "bar":
                return CursorShape.Line;
            case "line-thin":
                return CursorShape.LineThin;
            case "underline":
                return CursorShape.Underline;
            case "underline-thin":
                return CursorShape.UnderlineThin;
            default:
                return CursorShape.Block;
        }
    }

    public static CursorBlinking ParseBlinking(string? text)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "smooth":
                return CursorBlinking.Smooth;
            case "phase":
                return CursorBlinking.Phase;
            case "expand":
                return CursorBlinking.Expand;
            case "solid":
                return CursorBlinking.Solid;
            default:
                return CursorBlinking.Blink;
        }
    }

    /// <summary>
    /// A whole pushed style array, one entry per mode slot. A short or null
    /// array fills the rest with the default, so a companion that pushes fewer
    /// entries than the slots this build knows cannot index past its own data.
    /// </summary>
    public static CursorShape[] ParseShapes(string[]? styles)
    {
        var parsed = new CursorShape[ModeSlots];
        for (int i = 0; i < parsed.Length; i++)
        {
            parsed[i] = ParseShape(styles != null && i < styles.Length ? styles[i] : null);
        }

        return parsed;
    }
}
