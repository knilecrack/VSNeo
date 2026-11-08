using VSNeo.Seeky;
using Xunit;

namespace VSNeo.Tests;

public class SeekyQuickfixActionTests
{
    [Theory]
    [InlineData(null, SeekyQuickfixAction.Replace)]
    [InlineData("", SeekyQuickfixAction.Replace)]
    [InlineData("replace", SeekyQuickfixAction.Replace)]
    [InlineData("append", SeekyQuickfixAction.Append)]
    [InlineData("new", SeekyQuickfixAction.NewList)]
    [InlineData("unexpected", SeekyQuickfixAction.Replace)]
    public void Export_action_defaults_to_replace(string? value, string expected)
    {
        Assert.Equal(expected, SeekyQuickfixAction.Parse(value));
    }
}
