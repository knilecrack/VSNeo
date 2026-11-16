using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// The ext_cmdline / ext_messages / ext_popupmenu half of the redraw stream,
/// and the pager threshold. Each test drives the same [batch] shape nvim sends
/// over RPC; no NvimSession or UI is involved.
/// </summary>
public class PromptStateTests
{
    private static object[] Chunks(string text) => new object[] { new object[] { 0L, text } };

    private static object[] Redraw(string name, object evt) =>
        new object[] { new object[] { name, evt } };

    [Fact]
    public void Cmdline_show_sets_content_prefix_and_position()
    {
        int visibility = 0;
        var prompt = new PromptState(() => visibility++);

        prompt.HandleRedraw(Redraw(
            "cmdline_show",
            new object[] { Chunks("wq"), 2L, ":", string.Empty, 0L }));

        Assert.Equal("wq", prompt.CmdLine);
        Assert.Equal(":", prompt.CmdLinePrefix);
        Assert.Equal(2, prompt.CmdLinePos);
        Assert.Equal(1, visibility);
    }

    [Fact]
    public void Cmdline_input_prompt_uses_the_prompt_field()
    {
        var prompt = new PromptState();
        prompt.HandleRedraw(Redraw(
            "cmdline_show",
            new object[] { Chunks("name"), 0L, string.Empty, "Name: ", 0L }));

        Assert.Equal("Name: ", prompt.CmdLinePrefix);
    }

    [Fact]
    public void Cmdline_pos_moves_the_cursor_without_reopening()
    {
        int visibility = 0;
        string? last = null;
        var prompt = new PromptState(() => visibility++);
        prompt.CmdLineChanged += v => last = v;

        prompt.HandleRedraw(Redraw("cmdline_show",
            new object[] { Chunks("abc"), 0L, ":", string.Empty, 0L }));
        prompt.HandleRedraw(Redraw("cmdline_pos", new object[] { 1L, 0L }));

        Assert.Equal(1, prompt.CmdLinePos);
        Assert.Equal("abc", last);
        Assert.Equal(1, visibility);
    }

    [Fact]
    public void Cmdline_hide_closes_and_notifies_visibility()
    {
        int visibility = 0;
        var prompt = new PromptState(() => visibility++);
        prompt.HandleRedraw(Redraw("cmdline_show",
            new object[] { Chunks("x"), 0L, ":", string.Empty, 0L }));

        prompt.HandleRedraw(Redraw("cmdline_hide", new object[0]));

        Assert.Null(prompt.CmdLine);
        Assert.Equal(2, visibility);
    }

    [Fact]
    public void Short_messages_stay_in_the_margin()
    {
        var prompt = new PromptState();
        prompt.HandleRedraw(Redraw(
            "msg_show",
            new object[] { "warning", Chunks("hello"), false, false, false }));

        Assert.Equal("hello", prompt.Message);
        Assert.Equal("warning", prompt.MessageKind);
        Assert.Null(prompt.PagerText);
    }

    [Fact]
    public void Long_messages_open_the_pager_and_clear_the_margin()
    {
        string? pager = null;
        var prompt = new PromptState();
        prompt.PagerChanged += v => pager = v;

        prompt.HandleRedraw(Redraw(
            "msg_show",
            new object[] { "info", Chunks("a\nb\nc\nd"), false, false, false }));

        Assert.Equal("a\nb\nc\nd", prompt.PagerText);
        Assert.Equal("a\nb\nc\nd", pager);
        Assert.Null(prompt.Message);
    }

    [Fact]
    public void Appended_messages_are_measured_together()
    {
        var prompt = new PromptState();
        prompt.HandleRedraw(Redraw(
            "msg_show", new object[] { "info", Chunks("a"), false, false, false }));
        prompt.HandleRedraw(Redraw(
            "msg_show", new object[] { "info", Chunks("b"), false, false, true }));

        Assert.Equal("ab", prompt.Message);
    }

    [Fact]
    public void History_goes_to_the_pager_one_line_per_entry()
    {
        var prompt = new PromptState();
        prompt.HandleRedraw(Redraw("msg_history_show", new object[]
        {
            new object[]
            {
                new object[] { "info", Chunks("one"), false },
                new object[] { "info", Chunks("two"), false },
            },
        }));

        Assert.Equal("one\ntwo", prompt.PagerText);
    }

    [Fact]
    public void Mode_and_showcmd_are_separate_surfaces()
    {
        var prompt = new PromptState();
        prompt.HandleRedraw(Redraw("msg_showmode", new object[] { Chunks("-- INSERT --") }));
        prompt.HandleRedraw(Redraw("msg_showcmd", new object[] { Chunks("d2") }));

        Assert.Equal("-- INSERT --", prompt.ModeMessage);
        Assert.Equal("d2", prompt.ShowCmd);

        prompt.HandleRedraw(Redraw("msg_showcmd",
            new object[] { new object[] { 0L, string.Empty } }));
        Assert.Null(prompt.ShowCmd);
    }

    [Fact]
    public void Msg_clear_clears_message_mode_and_showcmd()
    {
        var prompt = new PromptState();
        prompt.HandleRedraw(Redraw("msg_show", new object[] { "info", Chunks("hi"), false, false, false }));
        prompt.HandleRedraw(Redraw("msg_showmode", new object[] { Chunks("-- VISUAL --") }));
        prompt.HandleRedraw(Redraw("msg_showcmd", new object[] { Chunks("y") }));

        prompt.HandleRedraw(Redraw("msg_clear", new object[0]));

        Assert.Null(prompt.Message);
        Assert.Null(prompt.ModeMessage);
        Assert.Null(prompt.ShowCmd);
    }

    [Fact]
    public void Popupmenu_words_and_selection_follow_the_redraw()
    {
        int changes = 0;
        var prompt = new PromptState();
        prompt.CompletionsChanged += () => changes++;

        prompt.HandleRedraw(Redraw(
            "popupmenu_show",
            new object[]
            {
                new object[]
                {
                    new object[] { "foo", string.Empty, string.Empty, string.Empty },
                    new object[] { "bar", string.Empty, string.Empty, string.Empty },
                },
                1L, 0L, 0L, 0L,
            }));

        Assert.Equal(new[] { "foo", "bar" }, prompt.CompletionWords);
        Assert.Equal(1, prompt.CompletionSelected);

        prompt.HandleRedraw(Redraw("popupmenu_select", new object[] { 0L }));
        Assert.Equal(0, prompt.CompletionSelected);

        prompt.HandleRedraw(Redraw("popupmenu_hide", new object[0]));
        Assert.Empty(prompt.CompletionWords);
        Assert.Equal(-1, prompt.CompletionSelected);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Closing_the_pager_clears_it_and_notifies()
    {
        string? pager = null;
        var prompt = new PromptState();
        prompt.PagerChanged += v => pager = v;
        prompt.HandleRedraw(Redraw(
            "msg_show", new object[] { "info", Chunks("1\n2\n3\n4"), false, false, false }));

        prompt.ClosePager();

        Assert.Null(prompt.PagerText);
        Assert.Null(pager);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("a\nb", 2)]
    [InlineData("a\n", 2)]
    public void Count_lines_matches_the_pager_threshold(string? text, int expected)
    {
        Assert.Equal(expected, PromptState.CountLines(text));
    }
}
