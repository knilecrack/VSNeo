using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

public class ViewportOptionsTests
{
    [Fact]
    public void Scrolloff_notification_tracks_the_effective_value()
    {
        var hub = new NvimStateHub();

        hub.OnNotification("vsneo_scrolloff", new object[] { 7L });
        Assert.Equal(7, hub.Scrolloff);

        hub.OnNotification("vsneo_scrolloff", new object[] { 2L });
        Assert.Equal(2, hub.Scrolloff);
    }

    [Fact]
    public void Scrolloff_notification_clamps_negative_and_missing_values()
    {
        var hub = new NvimStateHub();
        hub.OnNotification("vsneo_scrolloff", new object[] { 7L });

        hub.OnNotification("vsneo_scrolloff", new object[] { -2L });
        Assert.Equal(0, hub.Scrolloff);

        hub.OnNotification("vsneo_scrolloff", new object[] { 7L });
        hub.OnNotification("vsneo_scrolloff", System.Array.Empty<object>());
        Assert.Equal(0, hub.Scrolloff);
    }
}
