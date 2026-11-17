using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// Per-mode shape and blink from the pushed guicursor settings, parsed once per push.
internal sealed partial class CustomCursorAdornment
{
    // Parsed once per pushed string array; the hub hands back the same
    // instances until the companion sends new settings.
    private static string[]? s_parsedFrom;
    private static CursorShape[] s_parsedShapes = new CursorShape[CursorStyleDecision.ModeSlots];
    private static string? s_parsedBlinkFrom;
    private static CursorBlinking s_parsedBlink;

    private static CursorShape ShapeFor(VimMode mode, NvimStateHub state)
    {
        var styles = state.CursorStyles;
        if (!ReferenceEquals(styles, s_parsedFrom))
        {
            s_parsedShapes = CursorStyleDecision.ParseShapes(styles);
            s_parsedFrom = styles;
        }

        return s_parsedShapes[CursorStyleDecision.ModeIndex(mode)];
    }

    private static CursorBlinking BlinkingOf(NvimStateHub state)
    {
        var text = state.CursorBlinking;
        if (!ReferenceEquals(text, s_parsedBlinkFrom))
        {
            s_parsedBlink = CursorStyleDecision.ParseBlinking(text);
            s_parsedBlinkFrom = text;
        }

        return s_parsedBlink;
    }
}
