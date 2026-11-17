using VSNeo_Extension.Editor;
using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// guicursor text to shape and blink. The adornment keeps the caches and the
/// drawing; these pin what the pushed strings mean, including the fallbacks a
/// typo in the rc lands on.
/// </summary>
public class CursorStyleDecisionTests
{
    [Theory]
    [InlineData(VimMode.Normal, 0)]
    [InlineData(VimMode.Insert, 1)]
    [InlineData(VimMode.Replace, 2)]
    [InlineData(VimMode.Visual, 3)]
    [InlineData(VimMode.OperatorPending, 4)]
    [InlineData(VimMode.CmdLine, 5)]
    [InlineData(VimMode.Unknown, 0)]
    [InlineData(VimMode.Terminal, 0)]
    public void Mode_slots_match_the_settings_order(VimMode mode, int expected)
    {
        Assert.Equal(expected, CursorStyleDecision.ModeIndex(mode));
        Assert.InRange(expected, 0, CursorStyleDecision.ModeSlots - 1);
    }

    [Theory]
    [InlineData("block", (int)CursorShape.Block)]
    [InlineData("block-outline", (int)CursorShape.BlockOutline)]
    [InlineData("outline", (int)CursorShape.BlockOutline)]
    [InlineData("hollow", (int)CursorShape.BlockOutline)]
    [InlineData("line", (int)CursorShape.Line)]
    [InlineData("bar", (int)CursorShape.Line)]
    [InlineData("line-thin", (int)CursorShape.LineThin)]
    [InlineData("underline", (int)CursorShape.Underline)]
    [InlineData("underline-thin", (int)CursorShape.UnderlineThin)]
    [InlineData("  BAR  ", (int)CursorShape.Line)]
    [InlineData("BAR", (int)CursorShape.Line)]
    [InlineData("nonsense", (int)CursorShape.Block)]
    [InlineData("", (int)CursorShape.Block)]
    [InlineData(null, (int)CursorShape.Block)]
    public void Shape_text_falls_back_to_a_block(string? text, int expected)
    {
        Assert.Equal((CursorShape)expected, CursorStyleDecision.ParseShape(text));
    }

    [Theory]
    [InlineData("blink", (int)CursorBlinking.Blink)]
    [InlineData("smooth", (int)CursorBlinking.Smooth)]
    [InlineData("phase", (int)CursorBlinking.Phase)]
    [InlineData("expand", (int)CursorBlinking.Expand)]
    [InlineData("solid", (int)CursorBlinking.Solid)]
    [InlineData("  expand ", (int)CursorBlinking.Expand)]
    [InlineData("none", (int)CursorBlinking.Blink)]
    [InlineData("", (int)CursorBlinking.Blink)]
    [InlineData(null, (int)CursorBlinking.Blink)]
    public void Blink_text_falls_back_to_blink(string? text, int expected)
    {
        Assert.Equal((CursorBlinking)expected, CursorStyleDecision.ParseBlinking(text));
    }

    [Fact]
    public void A_pushed_array_fills_every_slot()
    {
        var parsed = CursorStyleDecision.ParseShapes(
            new[] { "block", "bar", "underline", "hollow", "line-thin", "underline-thin" });

        Assert.Equal(CursorStyleDecision.ModeSlots, parsed.Length);
        Assert.Equal(CursorShape.Block, parsed[CursorStyleDecision.ModeIndex(VimMode.Normal)]);
        Assert.Equal(CursorShape.Line, parsed[CursorStyleDecision.ModeIndex(VimMode.Insert)]);
        Assert.Equal(CursorShape.Underline, parsed[CursorStyleDecision.ModeIndex(VimMode.Replace)]);
        Assert.Equal(CursorShape.BlockOutline, parsed[CursorStyleDecision.ModeIndex(VimMode.Visual)]);
        Assert.Equal(CursorShape.LineThin, parsed[CursorStyleDecision.ModeIndex(VimMode.OperatorPending)]);
        Assert.Equal(CursorShape.UnderlineThin, parsed[CursorStyleDecision.ModeIndex(VimMode.CmdLine)]);
    }

    [Fact]
    public void A_short_or_missing_array_fills_the_rest_with_blocks()
    {
        var shortArray = CursorStyleDecision.ParseShapes(new[] { "bar" });
        Assert.Equal(CursorShape.Line, shortArray[0]);
        Assert.All(shortArray[1..], shape => Assert.Equal(CursorShape.Block, shape));

        var missing = CursorStyleDecision.ParseShapes(null);
        Assert.Equal(CursorStyleDecision.ModeSlots, missing.Length);
        Assert.All(missing, shape => Assert.Equal(CursorShape.Block, shape));
    }
}
