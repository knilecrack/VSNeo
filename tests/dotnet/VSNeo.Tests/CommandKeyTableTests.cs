using System;
using VSNeo_Extension.Editor;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// The command -> Vim key tables behind VsNeoCommandFilter. They run on the
/// key path, so every mapping is pinned here: a change to one of them is a
/// behavior change, not a refactor.
/// </summary>
public class CommandKeyTableTests
{
    private static readonly Guid OtherGroup = new("00000000-0000-0000-0000-000000000001");

    [Theory]
    [InlineData(CommandKeyTable.Left, "<Left>")]
    [InlineData(CommandKeyTable.Right, "<Right>")]
    [InlineData(CommandKeyTable.Up, "<Up>")]
    [InlineData(CommandKeyTable.Down, "<Down>")]
    [InlineData(CommandKeyTable.LeftExt, "<S-Left>")]
    [InlineData(CommandKeyTable.RightExt, "<S-Right>")]
    [InlineData(CommandKeyTable.UpExt, "<S-Up>")]
    [InlineData(CommandKeyTable.DownExt, "<S-Down>")]
    [InlineData(CommandKeyTable.WordPrev, "<C-Left>")]
    [InlineData(CommandKeyTable.WordPrevExt, "<C-Left>")]
    [InlineData(CommandKeyTable.WordNext, "<C-Right>")]
    [InlineData(CommandKeyTable.WordNextExt, "<C-Right>")]
    [InlineData(CommandKeyTable.Bol, "<Home>")]
    [InlineData(CommandKeyTable.BolExt, "<Home>")]
    [InlineData(CommandKeyTable.FirstChar, "<Home>")]
    [InlineData(CommandKeyTable.FirstCharExt, "<Home>")]
    [InlineData(CommandKeyTable.Eol, "<End>")]
    [InlineData(CommandKeyTable.EolExt, "<End>")]
    [InlineData(CommandKeyTable.Home, "<C-Home>")]
    [InlineData(CommandKeyTable.HomeExt, "<C-Home>")]
    [InlineData(CommandKeyTable.End, "<C-End>")]
    [InlineData(CommandKeyTable.EndExt, "<C-End>")]
    [InlineData(CommandKeyTable.PageUp, "<PageUp>")]
    [InlineData(CommandKeyTable.PageUpExt, "<PageUp>")]
    [InlineData(CommandKeyTable.PageDown, "<PageDown>")]
    [InlineData(CommandKeyTable.PageDownExt, "<PageDown>")]
    [InlineData(CommandKeyTable.Delete, "<Del>")]
    [InlineData(CommandKeyTable.Backspace, "<BS>")]
    [InlineData(CommandKeyTable.Tab, "<Tab>")]
    [InlineData(CommandKeyTable.BackTab, "<S-Tab>")]
    public void Normal_mode_navigation_maps_to_nvim_keys(uint id, string expected)
    {
        Assert.Equal(expected, CommandKeyTable.NormalModeKeyFor(CommandKeyTable.VsStd2K, id));
    }

    [Theory]
    [InlineData(CommandKeyTable.Cancel)]
    [InlineData(CommandKeyTable.Return)]
    [InlineData(CommandKeyTable.Paste)]
    public void Normal_mode_leaves_commands_it_does_not_own(uint id)
    {
        Assert.Null(CommandKeyTable.NormalModeKeyFor(CommandKeyTable.VsStd2K, id));
    }

    [Theory]
    [InlineData(CommandKeyTable.Cancel, "<Esc>")]
    [InlineData(CommandKeyTable.Return, "<CR>")]
    [InlineData(CommandKeyTable.Up, "<Up>")]
    [InlineData(CommandKeyTable.Down, "<Down>")]
    [InlineData(CommandKeyTable.Left, "<Left>")]
    [InlineData(CommandKeyTable.Right, "<Right>")]
    [InlineData(CommandKeyTable.PageUp, "<PageUp>")]
    [InlineData(CommandKeyTable.PageDown, "<PageDown>")]
    [InlineData(CommandKeyTable.Bol, "<Home>")]
    [InlineData(CommandKeyTable.Home, "<Home>")]
    [InlineData(CommandKeyTable.Eol, "<End>")]
    [InlineData(CommandKeyTable.End, "<End>")]
    [InlineData(CommandKeyTable.Backspace, "<BS>")]
    [InlineData(CommandKeyTable.Tab, "<Tab>")]
    public void Pager_keys_map_to_nvim_keys(uint id, string expected)
    {
        Assert.Equal(expected, CommandKeyTable.PagerKeyFor(CommandKeyTable.VsStd2K, id));
    }

    [Theory]
    [InlineData(CommandKeyTable.Delete)]
    [InlineData(CommandKeyTable.BackTab)]
    [InlineData(CommandKeyTable.WordNext)]
    public void Pager_leaves_commands_it_does_not_own(uint id)
    {
        Assert.Null(CommandKeyTable.PagerKeyFor(CommandKeyTable.VsStd2K, id));
    }

    [Theory]
    [InlineData(CommandKeyTable.Return, "<CR>")]
    [InlineData(CommandKeyTable.Backspace, "<BS>")]
    [InlineData(CommandKeyTable.Tab, "<Tab>")]
    [InlineData(CommandKeyTable.BackTab, "<S-Tab>")]
    [InlineData(CommandKeyTable.Delete, "<Del>")]
    [InlineData(CommandKeyTable.Left, "<Left>")]
    [InlineData(CommandKeyTable.Right, "<Right>")]
    [InlineData(CommandKeyTable.Up, "<Up>")]
    [InlineData(CommandKeyTable.Down, "<Down>")]
    [InlineData(CommandKeyTable.Bol, "<Home>")]
    [InlineData(CommandKeyTable.Eol, "<End>")]
    public void Cmdline_keys_map_to_nvim_keys(uint id, string expected)
    {
        Assert.Equal(expected, CommandKeyTable.CmdLineKeyFor(CommandKeyTable.VsStd2K, id));
    }

    [Theory]
    [InlineData(CommandKeyTable.Cancel)]
    [InlineData(CommandKeyTable.Home)]
    [InlineData(CommandKeyTable.End)]
    [InlineData(CommandKeyTable.PageUp)]
    [InlineData(CommandKeyTable.PageDown)]
    [InlineData(CommandKeyTable.WordPrev)]
    public void Cmdline_leaves_commands_it_does_not_own(uint id)
    {
        Assert.Null(CommandKeyTable.CmdLineKeyFor(CommandKeyTable.VsStd2K, id));
    }

    [Fact]
    public void Every_table_ignores_other_command_groups()
    {
        Assert.Null(CommandKeyTable.NormalModeKeyFor(OtherGroup, CommandKeyTable.Left));
        Assert.Null(CommandKeyTable.PagerKeyFor(OtherGroup, CommandKeyTable.Return));
        Assert.Null(CommandKeyTable.CmdLineKeyFor(OtherGroup, CommandKeyTable.Return));
        Assert.False(CommandKeyTable.IsCancel(OtherGroup, CommandKeyTable.Cancel));
        Assert.False(CommandKeyTable.IsPaste(CommandKeyTable.VsStd2K, CommandKeyTable.Paste));
    }

    [Fact]
    public void Cancel_and_paste_are_recognised_in_their_own_groups()
    {
        Assert.True(CommandKeyTable.IsCancel(CommandKeyTable.VsStd2K, CommandKeyTable.Cancel));
        Assert.True(CommandKeyTable.IsPaste(CommandKeyTable.VsStd97, CommandKeyTable.Paste));
        Assert.False(CommandKeyTable.IsCancel(CommandKeyTable.VsStd2K, CommandKeyTable.Paste));
        Assert.False(CommandKeyTable.IsPaste(CommandKeyTable.VsStd97, CommandKeyTable.Cancel));
    }
}
