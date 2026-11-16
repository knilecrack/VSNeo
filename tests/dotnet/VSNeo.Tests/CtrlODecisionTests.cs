using VSNeo_Extension.Editor;
using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// The two pending-key windows around u / Ctrl+R: the replace-mode Escape arm
/// and the i_CTRL-O excursion. These are the rules that keep a forwarded undo
/// from reaching nvim; they used to live inline in the key processor.
/// </summary>
public class CtrlODecisionTests
{
    [Fact]
    public void Replace_escape_resolves_normal_inside_the_window()
    {
        var result = CtrlODecision.ResolveReplaceEscape(
            VimMode.Replace, modeSequence: 10, nowTicks: 1000,
            pending: true, armedSequence: 10, armedTicks: 200);

        Assert.True(result.Resolved);
        Assert.Equal(VimMode.Normal, result.Mode);
        // The arm stays set; only the mode push that moves the sequence ends it.
        Assert.True(result.Pending);
    }

    [Theory]
    [InlineData(2200)] // exactly 2000 ms
    [InlineData(201)]  // a wrap-safe small delta
    public void Replace_escape_window_is_inclusive(int now)
    {
        var result = CtrlODecision.ResolveReplaceEscape(
            VimMode.Replace, 10, now, true, 10, 200);

        Assert.True(result.Resolved);
        Assert.Equal(VimMode.Normal, result.Mode);
    }

    [Fact]
    public void Replace_escape_clears_when_stale_or_mismatched()
    {
        var stale = CtrlODecision.ResolveReplaceEscape(
            VimMode.Replace, 10, 2201, true, 10, 200);
        Assert.False(stale.Resolved);
        Assert.False(stale.Pending);
        Assert.Equal(VimMode.Replace, stale.Mode);

        var wrongMode = CtrlODecision.ResolveReplaceEscape(
            VimMode.Insert, 10, 1000, true, 10, 200);
        Assert.False(wrongMode.Resolved);
        Assert.False(wrongMode.Pending);

        var wrongSequence = CtrlODecision.ResolveReplaceEscape(
            VimMode.Replace, 11, 1000, true, 10, 200);
        Assert.False(wrongSequence.Resolved);
        Assert.False(wrongSequence.Pending);
    }

    [Fact]
    public void No_replace_arm_leaves_the_mode_alone()
    {
        var result = CtrlODecision.ResolveReplaceEscape(
            VimMode.Replace, 10, 1000, false, 10, 200);

        Assert.False(result.Resolved);
        Assert.False(result.Pending);
        Assert.Equal(VimMode.Replace, result.Mode);
    }

    [Fact]
    public void Replace_escape_uses_unchecked_tick_arithmetic()
    {
        // Environment.TickCount wraps; the delta across the wrap is small.
        var result = CtrlODecision.ResolveReplaceEscape(
            VimMode.Replace, 10,
            nowTicks: int.MinValue + 100,
            pending: true,
            armedSequence: 10,
            armedTicks: int.MaxValue - 100);

        Assert.True(result.Resolved);
        Assert.Equal(VimMode.Normal, result.Mode);
    }

    [Fact]
    public void Ctrl_o_resolves_normal_while_the_excursion_is_current()
    {
        var atArm = CtrlODecision.ResolveCtrlO(
            VimMode.Insert, modeSequence: 10, nowTicks: 1000,
            pending: true, armedSequence: 10, armedTicks: 1000);
        Assert.Equal(VimMode.Normal, atArm.Mode);
        Assert.True(atArm.Pending);

        // i -> n is one transition; nvim's mode cache still says Insert.
        var oneStep = CtrlODecision.ResolveCtrlO(
            VimMode.Insert, 11, 1000, true, 10, 1000);
        Assert.Equal(VimMode.Normal, oneStep.Mode);
        Assert.True(oneStep.Pending);

        var replace = CtrlODecision.ResolveCtrlO(
            VimMode.Replace, 10, 1000, true, 10, 1000);
        Assert.Equal(VimMode.Normal, replace.Mode);
    }

    [Fact]
    public void Ctrl_o_expires_when_the_sequence_advances_by_two()
    {
        var result = CtrlODecision.ResolveCtrlO(
            VimMode.Insert, modeSequence: 12, nowTicks: 1000,
            pending: true, armedSequence: 10, armedTicks: 1000);

        Assert.Equal(VimMode.Insert, result.Mode);
        Assert.False(result.Pending);
    }

    [Theory]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public void Ctrl_o_expires_after_two_seconds_at_the_same_sequence(int delta, bool resolves)
    {
        var result = CtrlODecision.ResolveCtrlO(
            VimMode.Insert, modeSequence: 10, nowTicks: 1000 + delta,
            pending: true, armedSequence: 10, armedTicks: 1000);

        Assert.Equal(resolves ? VimMode.Normal : VimMode.Insert, result.Mode);
        Assert.Equal(resolves, result.Pending);
    }

    [Fact]
    public void Ctrl_o_leaves_other_modes_and_clear_state_alone()
    {
        var normal = CtrlODecision.ResolveCtrlO(
            VimMode.Normal, 10, 1000, true, 10, 1000);
        Assert.Equal(VimMode.Normal, normal.Mode);
        Assert.True(normal.Pending);

        var visual = CtrlODecision.ResolveCtrlO(
            VimMode.Visual, 10, 1000, true, 10, 1000);
        Assert.Equal(VimMode.Visual, visual.Mode);
        Assert.True(visual.Pending);

        var idle = CtrlODecision.ResolveCtrlO(
            VimMode.Insert, 10, 1000, false, 10, 1000);
        Assert.Equal(VimMode.Insert, idle.Mode);
        Assert.False(idle.Pending);
    }

    [Fact]
    public void Ctrl_o_uses_unchecked_tick_arithmetic()
    {
        var result = CtrlODecision.ResolveCtrlO(
            VimMode.Insert, modeSequence: 10,
            nowTicks: int.MinValue + 100,
            pending: true,
            armedSequence: 10,
            armedTicks: int.MaxValue - 100);

        Assert.Equal(VimMode.Normal, result.Mode);
        Assert.True(result.Pending);
    }
}
