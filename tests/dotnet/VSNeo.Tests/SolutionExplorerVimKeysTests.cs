using VSNeo_Extension.Editor;
using Xunit;

namespace VSNeo.Tests;

public class SolutionExplorerVimKeysTests
{
    [Theory]
    [InlineData((int)SolutionExplorerVimKey.J, (int)SolutionExplorerVimAction.Down)]
    [InlineData((int)SolutionExplorerVimKey.K, (int)SolutionExplorerVimAction.Up)]
    [InlineData((int)SolutionExplorerVimKey.ShiftG, (int)SolutionExplorerVimAction.Bottom)]
    [InlineData((int)SolutionExplorerVimKey.H, (int)SolutionExplorerVimAction.Collapse)]
    [InlineData((int)SolutionExplorerVimKey.L, (int)SolutionExplorerVimAction.Expand)]
    [InlineData((int)SolutionExplorerVimKey.O, (int)SolutionExplorerVimAction.Open)]
    [InlineData((int)SolutionExplorerVimKey.Enter, (int)SolutionExplorerVimAction.Open)]
    [InlineData((int)SolutionExplorerVimKey.Q, (int)SolutionExplorerVimAction.ReturnToEditor)]
    public void Single_keys_map_to_actions(int key, int expected)
    {
        int pending = 0;
        Assert.Equal(expected, (int)SolutionExplorerVimKeys.Next(
            ref pending, (SolutionExplorerVimKey)key, 100));
        Assert.Equal(0, pending);
    }

    [Fact]
    public void Two_g_keys_move_to_the_top()
    {
        int pending = 0;
        Assert.Equal(SolutionExplorerVimAction.Wait,
            SolutionExplorerVimKeys.Next(ref pending, SolutionExplorerVimKey.G, 100));
        Assert.NotEqual(0, pending);

        Assert.Equal(SolutionExplorerVimAction.Top,
            SolutionExplorerVimKeys.Next(ref pending, SolutionExplorerVimKey.G, 101));
        Assert.Equal(0, pending);
    }

    [Fact]
    public void Pending_g_expires()
    {
        int pending = 0;
        SolutionExplorerVimKeys.Next(ref pending, SolutionExplorerVimKey.G, 100);

        Assert.Equal(SolutionExplorerVimAction.Wait,
            SolutionExplorerVimKeys.Next(ref pending, SolutionExplorerVimKey.G,
                100 + SolutionExplorerVimKeys.TimeoutMs));
    }

    [Fact]
    public void Another_key_cancels_pending_g()
    {
        int pending = 0;
        SolutionExplorerVimKeys.Next(ref pending, SolutionExplorerVimKey.G, 100);

        Assert.Equal(SolutionExplorerVimAction.Down,
            SolutionExplorerVimKeys.Next(ref pending, SolutionExplorerVimKey.J, 101));
        Assert.Equal(0, pending);
    }
}
