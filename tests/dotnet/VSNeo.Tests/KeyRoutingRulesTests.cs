using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

public class KeyRoutingRulesTests
{
    [Fact]
    public void Rules_default_to_unspecified()
    {
        var rules = new KeyRoutingRules();
        Assert.Equal(KeyOwner.Unspecified, rules.OwnerFor("<C-p>", VimMode.Normal));
        Assert.Equal(KeyOwner.Unspecified, rules.OwnerFor(null, VimMode.Insert));
    }

    [Fact]
    public void Key_rules_and_mode_defaults_have_the_expected_precedence()
    {
        var rules = new KeyRoutingRules();
        Assert.True(rules.Apply("a:ide c:vim", out _));
        Assert.True(rules.Apply("<F2> n:vim c:ide", out _));

        Assert.Equal(KeyOwner.Vim, rules.OwnerFor("<f2>", VimMode.Normal));
        Assert.Equal(KeyOwner.Ide, rules.OwnerFor("<F2>", VimMode.Insert));
        Assert.Equal(KeyOwner.Ide, rules.OwnerFor("<F2>", VimMode.CmdLine));
        Assert.Equal(KeyOwner.Ide, rules.OwnerFor("<C-p>", VimMode.Normal));
        Assert.Equal(KeyOwner.Vim, rules.OwnerFor("<C-p>", VimMode.CmdLine));
        Assert.Equal(KeyOwner.Vim, rules.OwnerFor(null, VimMode.CmdLine));
    }

    [Fact]
    public void Later_assignments_override_earlier_ones()
    {
        var rules = new KeyRoutingRules();
        Assert.True(rules.Apply("<C-p> n-v:ide i:vim", out _));
        Assert.True(rules.Apply("<C-p> n:vim", out _));

        Assert.Equal(KeyOwner.Vim, rules.OwnerFor("<C-p>", VimMode.Normal));
        Assert.Equal(KeyOwner.Vim, rules.OwnerFor("<C-p>", VimMode.Insert));
        Assert.Equal(KeyOwner.Ide, rules.OwnerFor("<C-p>", VimMode.Visual));
    }

    [Fact]
    public void Visual_groups_replace_and_operator_pending_map_to_vsneo_modes()
    {
        var rules = new KeyRoutingRules();
        Assert.True(rules.Apply("<CR> x:ide s:vim v:ide r:vim o:ide", out _));

        Assert.Equal(KeyOwner.Ide, rules.OwnerFor("<CR>", VimMode.Visual));
        Assert.Equal(KeyOwner.Vim, rules.OwnerFor("<CR>", VimMode.Replace));
        Assert.Equal(KeyOwner.Ide, rules.OwnerFor("<CR>", VimMode.OperatorPending));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<C-p>")]
    [InlineData("<C-p> n:other")]
    [InlineData("<C-p> bogus")]
    [InlineData("<C-p> -v:ide")]
    [InlineData("<C-p> n-:ide")]
    [InlineData("<C-p> n-a:ide")]
    [InlineData("<C-p n:ide")]
    [InlineData("<C-p>x n:ide")]
    public void Invalid_definitions_do_not_partially_mutate_the_snapshot(string definition)
    {
        var rules = new KeyRoutingRules();
        Assert.True(rules.Apply("<C-p> n:ide", out _));

        Assert.False(rules.Apply(definition, out string error));
        Assert.NotEmpty(error);
        Assert.Equal(KeyOwner.Ide, rules.OwnerFor("<C-p>", VimMode.Normal));
        Assert.Equal(KeyOwner.Unspecified, rules.OwnerFor("<C-p>", VimMode.Insert));
    }
}
