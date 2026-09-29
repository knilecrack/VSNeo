using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// Long command output goes to the pager, not the message margin. With
/// ext_messages nvim hands :map, :set all and :messages to the UI and never
/// clears them, and the margin used to grow into a full-screen pane nothing
/// could close. These pin the hub's half of the fix: what counts as long,
/// that msg_clear leaves the pager alone (only its own close ends it), and
/// that :messages - msg_history_show, which nothing handled - reaches it.
/// </summary>
public class MessagePagerTests
{
    private static object[] Chunks(string text) =>
        new object[] { new object[] { 0L, text, 0L } };

    private static object[] MsgShow(string kind, string text, bool append = false) =>
        new object[] { "msg_show", new object[] { kind, Chunks(text), false, false, append, 1L, "typed_cmd" } };

    private static void Redraw(NvimStateHub hub, params object[][] batches) =>
        hub.OnNotification("redraw", batches);

    [Fact]
    public void Short_messages_stay_in_the_margin()
    {
        var hub = new NvimStateHub();
        Redraw(hub, MsgShow("echo", "one\ntwo\nthree"));

        Assert.Equal("one\ntwo\nthree", hub.Message);
        Assert.Null(hub.PagerText);
    }

    [Fact]
    public void Long_output_goes_to_the_pager_and_clears_the_margin()
    {
        var hub = new NvimStateHub();
        Redraw(hub, MsgShow("echo", "short"));

        string? raised = null;
        hub.PagerChanged += t => raised = t;
        Redraw(hub, MsgShow("list_cmd", "a\nb\nc\nd"));

        Assert.Equal("a\nb\nc\nd", hub.PagerText);
        Assert.Equal("a\nb\nc\nd", raised);
        Assert.Null(hub.Message);
    }

    [Fact]
    public void Appended_pieces_are_measured_together()
    {
        var hub = new NvimStateHub();
        Redraw(hub, MsgShow("list_cmd", "a\nb"));
        Assert.Null(hub.PagerText);

        Redraw(hub, MsgShow("list_cmd", "\nc\nd", append: true));
        Assert.Equal("a\nb\nc\nd", hub.PagerText);
    }

    [Fact]
    public void Msg_clear_leaves_the_pager_open_and_ClosePager_ends_it()
    {
        var hub = new NvimStateHub();
        Redraw(hub, MsgShow("list_cmd", "1\n2\n3\n4\n5"));
        Redraw(hub, new object[] { "msg_clear", Array.Empty<object>() });
        Assert.NotNull(hub.PagerText);

        string? raised = "unset";
        hub.PagerChanged += t => raised = t;
        hub.ClosePager();
        Assert.Null(hub.PagerText);
        Assert.Null(raised);
    }

    [Fact]
    public void Message_history_goes_to_the_pager_one_line_per_entry()
    {
        var hub = new NvimStateHub();
        var entries = new object[]
        {
            new object[] { "echo", Chunks("first"), false },
            new object[] { "emsg", Chunks("E492: Not an editor command: x"), false },
            new object[] { "echo", Chunks(" (continued)"), true },
        };
        Redraw(hub, new object[] { "msg_history_show", new object[] { entries, false } });

        Assert.Equal("first\nE492: Not an editor command: x (continued)", hub.PagerText);
    }

    [Theory]
    [InlineData("msg_history_show")]
    [InlineData("msg_show")]
    [InlineData("msg_clear")]
    public void The_stream_reader_keeps_the_pager_events(string name)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(name);
        Assert.Equal(name, NvimStateHub.MatchHandledRedrawEvent(bytes, 0, bytes.Length));
        Assert.True(NvimStateHub.IsHandledRedrawEvent(name));
    }

    [Fact]
    public async Task Map_and_messages_reach_the_pager_over_the_real_wire()
    {
        var nvimPath = NvimWireTests.FindNvim();
        if (nvimPath == null) return;

        var timeout = TimeSpan.FromSeconds(15);
        using var client = await NvimRpcClient.ConnectAsync(nvimPath, CancellationToken.None);
        var hub = new NvimStateHub();
        client.NotificationReceived += hub.OnNotification;
        client.StatePushReceived += hub.OnStatePush;
        client.BeginRead();

        var options = new Dictionary<string, object>
        {
            ["ext_linegrid"] = true,
            ["ext_cmdline"] = true,
            ["ext_messages"] = true,
            ["rgb"] = true,
        };
        await client.RequestAsync("nvim_ui_attach", timeout, 200, 60, options);
        await client.RequestAsync("nvim_set_var", timeout, "vsneo", 1);
        var apiInfo = await client.RequestAsync("nvim_get_api_info", timeout) as object[];
        long channel = Convert.ToInt64(apiInfo![0]);
        await client.RequestAsync("nvim_exec_lua", timeout,
            File.ReadAllText(NvimWireTests.FindCompanionScript()), new object[] { channel });

        var opened = WaitForPager(hub, t => t.Contains("<C-W>") || t.Contains("<C-w>"));
        client.NotifyInput(":map<CR>");
        await opened;
        Assert.Null(hub.Message);

        hub.ClosePager();
        await client.RequestAsync("nvim_command", timeout, "echomsg 'pager-history-probe'");
        var history = WaitForPager(hub, t => t.Contains("pager-history-probe"));
        client.NotifyInput(":messages<CR>");
        await history;
    }

    private static Task WaitForPager(NvimStateHub hub, Func<string, bool> match)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.PagerChanged += t =>
        {
            if (t != null && match(t)) tcs.TrySetResult(null);
        };
        return Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)))
            .ContinueWith(_ =>
            {
                if (!tcs.Task.IsCompleted) throw new TimeoutException("the pager never opened");
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
