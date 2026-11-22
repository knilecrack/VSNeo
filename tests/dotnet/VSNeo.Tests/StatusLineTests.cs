using System.Text;
using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

public class StatusLineTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Statusline_notification_carries_segments_and_colors()
    {
        var hub = new NvimStateHub();
        int raised = 0;
        hub.StatusLineChanged += () => raised++;

        hub.OnNotification("vsneo_statusline", new object[]
        {
            2L, 0x111111L, 0x222222L, new object[]
            {
                new object[] { Utf8(" NORMAL "), 0x1E1E1EL, 0x5B8C3EL, true },
                new object[] { Utf8(" é.cs"), -1L, -1L, false },
                "not a segment",
            },
        });

        var line = hub.StatusLine;
        Assert.Equal(1, raised);
        Assert.Equal(2, line.LastStatus);
        Assert.Equal(0x111111, line.Fg);
        Assert.Equal(0x222222, line.Bg);
        Assert.Equal(2, line.Segments.Length);
        Assert.Equal(" NORMAL ", line.Segments[0].Text);
        Assert.Equal(0x5B8C3E, line.Segments[0].Bg);
        Assert.True(line.Segments[0].Bold);
        Assert.Equal(" é.cs", line.Segments[1].Text);
        Assert.Equal(-1, line.Segments[1].Fg);
        Assert.False(line.Segments[1].Bold);
    }

    [Fact]
    public void Statusline_is_off_until_told_and_ignores_short_payloads()
    {
        var hub = new NvimStateHub();
        Assert.Equal(0, hub.StatusLine.LastStatus);

        hub.OnNotification("vsneo_statusline", new object[] { 2L });
        Assert.Equal(0, hub.StatusLine.LastStatus);
        Assert.Empty(hub.StatusLine.Segments);
    }
}
