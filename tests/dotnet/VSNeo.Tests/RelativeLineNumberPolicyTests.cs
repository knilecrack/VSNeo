using VSNeo_Extension.Infrastructure;
using Xunit;

namespace VSNeo.Tests;

public class RelativeLineNumberPolicyTests
{
    [Fact]
    public void Persisted_mode_values_remain_stable()
    {
        Assert.Equal(0, (int)RelativeLineNumbersMode.FollowNeovim);
        Assert.Equal(1, (int)RelativeLineNumbersMode.AlwaysOn);
        Assert.Equal(2, (int)RelativeLineNumbersMode.AlwaysOff);
        Assert.Equal(3, (int)RelativeLineNumbersMode.Hybrid);
    }

    [Theory]
    [InlineData(RelativeLineNumbersMode.FollowNeovim, false, false)]
    [InlineData(RelativeLineNumbersMode.FollowNeovim, true, true)]
    [InlineData(RelativeLineNumbersMode.AlwaysOn, false, true)]
    [InlineData(RelativeLineNumbersMode.AlwaysOn, true, true)]
    [InlineData(RelativeLineNumbersMode.AlwaysOff, false, false)]
    [InlineData(RelativeLineNumbersMode.AlwaysOff, true, false)]
    [InlineData(RelativeLineNumbersMode.Hybrid, false, true)]
    [InlineData(RelativeLineNumbersMode.Hybrid, true, true)]
    public void Margin_activation_uses_the_selected_policy(
        RelativeLineNumbersMode mode,
        bool nvimRelativeNumber,
        bool expected)
    {
        Assert.Equal(expected, RelativeLineNumberPolicy.IsActive(mode, nvimRelativeNumber));
    }

    [Theory]
    [InlineData(RelativeLineNumbersMode.FollowNeovim, false, false)]
    [InlineData(RelativeLineNumbersMode.FollowNeovim, true, true)]
    [InlineData(RelativeLineNumbersMode.AlwaysOn, false, false)]
    [InlineData(RelativeLineNumbersMode.AlwaysOn, true, true)]
    [InlineData(RelativeLineNumbersMode.Hybrid, false, true)]
    [InlineData(RelativeLineNumbersMode.Hybrid, true, true)]
    public void Cursor_line_style_uses_the_selected_policy(
        RelativeLineNumbersMode mode,
        bool nvimNumber,
        bool expected)
    {
        Assert.Equal(expected, RelativeLineNumberPolicy.IsCursorLineAbsolute(mode, nvimNumber));
    }

    [Fact]
    public void Hybrid_is_active_and_absolute_before_nvim_options_arrive()
    {
        Assert.True(RelativeLineNumberPolicy.IsActive(RelativeLineNumbersMode.Hybrid, false));
        Assert.True(RelativeLineNumberPolicy.IsCursorLineAbsolute(RelativeLineNumbersMode.Hybrid, false));
    }

    [Fact]
    public void Switching_between_always_on_and_hybrid_keeps_activation_and_changes_cursor_style()
    {
        var mode = RelativeLineNumbersMode.AlwaysOn;
        Assert.True(RelativeLineNumberPolicy.IsActive(mode, nvimRelativeNumber: true));
        Assert.False(RelativeLineNumberPolicy.IsCursorLineAbsolute(mode, nvimNumber: false));

        mode = RelativeLineNumbersMode.Hybrid;
        Assert.True(RelativeLineNumberPolicy.IsActive(mode, nvimRelativeNumber: false));
        Assert.True(RelativeLineNumberPolicy.IsCursorLineAbsolute(mode, nvimNumber: false));

        mode = RelativeLineNumbersMode.AlwaysOn;
        Assert.True(RelativeLineNumberPolicy.IsActive(mode, nvimRelativeNumber: false));
        Assert.False(RelativeLineNumberPolicy.IsCursorLineAbsolute(mode, nvimNumber: false));
    }
}
