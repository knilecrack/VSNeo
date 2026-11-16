using VSNeo_Extension.Editor;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// The trail's maths, separated from the WPF quad that draws it: which moves
/// get the short animation, how corners are ranked for the smear, the spring
/// itself, and the per-corner lengths.
/// </summary>
public class TrailMotionRulesTests
{
    [Theory]
    [InlineData(0, 0, true)]        // no travel at all is still "short"
    [InlineData(8, 0, true)]        // one column, same line
    [InlineData(16, 0, true)]       // two columns
    [InlineData(16.6, 0, false)]    // just past 2 * columnWidth + 0.5
    [InlineData(8, 20, false)]      // one column, different line
    [InlineData(-8, 0, true)]       // leftwards counts too
    public void Short_moves_are_same_line_and_at_most_two_columns(double x, double y, bool expected)
    {
        Assert.Equal(expected, TrailMotionRules.IsShortMove(x, y, columnWidth: 8, rowHeight: 40));
    }

    [Fact]
    public void A_short_line_height_still_tolerates_a_pixel()
    {
        // rowHeight / 2 would be 0 for a tiny line; the floor keeps the
        // same-line test from becoming "exactly zero".
        Assert.True(TrailMotionRules.IsShortMove(0, 0.4, columnWidth: 8, rowHeight: 0));
        Assert.False(TrailMotionRules.IsShortMove(0, 1.6, columnWidth: 8, rowHeight: 0));
    }

    [Fact]
    public void Corners_are_ranked_by_alignment()
    {
        var ranks = new double[4];
        TrailMotionRules.Ranks(new[] { -0.9, -0.1, 0.4, 1.0 }, ranks);

        Assert.Equal(0, ranks[0]);
        Assert.Equal(1, ranks[1]);
        Assert.Equal(2, ranks[2]);
        Assert.Equal(3, ranks[3]);
    }

    [Fact]
    public void Tied_corners_share_the_average_of_their_ranks()
    {
        // A pure horizontal move: the two right corners face forward equally
        // and the two left corners trail equally, so each pair shares a rank.
        var ranks = new double[4];
        TrailMotionRules.Ranks(new[] { -1.0, 1.0, 1.0, -1.0 }, ranks);

        Assert.Equal(0.5, ranks[0]);   // trailing pair: ranks 0 and 1 average
        Assert.Equal(2.5, ranks[1]);   // leading pair: ranks 2 and 3 average
        Assert.Equal(2.5, ranks[2]);
        Assert.Equal(0.5, ranks[3]);
    }

    [Fact]
    public void Leading_corners_get_shorter_animations_than_trailing_ones()
    {
        double trailing = TrailMotionRules.CornerLength(0.3, trail: 1, rank: 0);
        double leading = TrailMotionRules.CornerLength(0.3, trail: 1, rank: 3);

        Assert.True(leading < trailing);
        Assert.Equal(0.3, trailing, 6);
        Assert.Equal(0.01, leading, 6);   // trail 1 and rank 3 ask for zero; the floor answers
    }

    [Fact]
    public void No_trail_makes_every_corner_arrive_together()
    {
        for (int rank = 0; rank <= 3; rank++)
        {
            Assert.Equal(0.3, TrailMotionRules.CornerLength(0.3, trail: 0, rank), 6);
        }
    }

    [Fact]
    public void The_spring_converges_without_overshooting()
    {
        double position = 0, velocity = 0;
        double previous = position;

        for (int frame = 0; frame < 120; frame++)
        {
            TrailMotionRules.Step(ref position, ref velocity, target: 100, length: 0.3, dt: 1.0 / 60);
            Assert.True(position >= previous, $"overshot backwards on frame {frame}");
            Assert.True(position <= 100 + 1e-9, $"overshot past the target on frame {frame}: {position}");
            previous = position;
        }

        Assert.Equal(100, position, 0);
    }

    [Fact]
    public void A_retarget_keeps_the_velocity_it_had()
    {
        double position = 0, velocity = 0;
        for (int frame = 0; frame < 10; frame++)
        {
            TrailMotionRules.Step(ref position, ref velocity, target: 100, length: 0.3, dt: 1.0 / 60);
        }

        Assert.True(velocity > 0, "expected the spring to be moving before the retarget");
        double before = velocity;

        // A new target behind us: the carried velocity must show up as a
        // slower start, not as a restart from rest.
        TrailMotionRules.Step(ref position, ref velocity, target: -100, length: 0.3, dt: 1.0 / 60);
        Assert.True(velocity < before);
        Assert.False(double.IsNaN(position) || double.IsNaN(velocity));
    }

    [Fact]
    public void A_hitched_frame_does_not_fly_off()
    {
        double position = 0, velocity = 0;

        // Half a second between frames: the exact solution stays bounded, a
        // numeric integrator would not.
        TrailMotionRules.Step(ref position, ref velocity, target: 100, length: 0.3, dt: 0.5);

        Assert.False(double.IsNaN(position));
        Assert.InRange(position, 0, 100);
    }
}
