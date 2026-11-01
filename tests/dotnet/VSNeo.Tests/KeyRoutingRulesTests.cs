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
    }

    [Fact]
    public void Mode_defaults_cover_shortcuts_only()
    {
        var rules = new KeyRoutingRules();
        Assert.True(rules.Apply("n:ide c:vim", out _));

        Assert.Equal(KeyOwner.Ide, rules.OwnerFor("<C-d>", VimMode.Normal));
        Assert.Equal(KeyOwner.Ide, rules.OwnerFor("<A-j>", VimMode.Normal));
        Assert.Equal(KeyOwner.Ide, rules.OwnerFor("<F5>", VimMode.Normal));
        Assert.Equal(KeyOwner.Vim, rules.OwnerFor("<C-v>", VimMode.CmdLine));

        // Typed text, Escape and the editing keys keep VSNeo's own routing.
        Assert.Equal(KeyOwner.Unspecified, rules.OwnerFor(null, VimMode.Normal));
        Assert.Equal(KeyOwner.Unspecified, rules.OwnerFor("<BS>", VimMode.Normal));
        Assert.Equal(KeyOwner.Unspecified, rules.OwnerFor("<CR>", VimMode.Normal));
        Assert.Equal(KeyOwner.Unspecified, rules.OwnerFor("<Esc>", VimMode.Normal));
        Assert.Equal(KeyOwner.Unspecified, rules.OwnerFor("<S-Tab>", VimMode.Normal));
        Assert.Equal(KeyOwner.Unspecified, rules.OwnerFor("<Right>", VimMode.Normal));

        // ...unless a rule names the key itself.
        Assert.True(rules.Apply("<CR> n:vim", out _));
        Assert.Equal(KeyOwner.Vim, rules.OwnerFor("<CR>", VimMode.Normal));
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
    public void Dash_lists_modes_as_in_IdeaVim_not_a_range()
    {
        var rules = new KeyRoutingRules();
        Assert.True(rules.Apply("<C-x> i:ide n-v:vim", out _));

        Assert.Equal(KeyOwner.Ide, rules.OwnerFor("<C-x>", VimMode.Insert));
        Assert.Equal(KeyOwner.Vim, rules.OwnerFor("<C-x>", VimMode.Normal));
        Assert.Equal(KeyOwner.Vim, rules.OwnerFor("<C-x>", VimMode.Visual));

        Assert.True(rules.Apply("<C-y> n-i-v:ide", out _));
        Assert.Equal(KeyOwner.Ide, rules.OwnerFor("<C-y>", VimMode.Insert));
        Assert.Equal(KeyOwner.Unspecified, rules.OwnerFor("<C-y>", VimMode.CmdLine));

        Assert.False(rules.Apply("<C-z> n-a:ide", out _));
        Assert.False(rules.Apply("<C-z> nv:ide", out _));
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
