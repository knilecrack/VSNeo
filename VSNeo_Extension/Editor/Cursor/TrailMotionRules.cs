using System;

namespace VSNeo_Extension.Editor;

/// <summary>
/// The trail's corners as numbers: Neovide's critically damped spring, one
/// per corner of the cursor quad, and the per-corner animation lengths that
/// make the quad smear instead of sliding.
///
/// Extracted from the adornment because it is the part that can be wrong
/// without anyone seeing it at first: a step that overshoots, a length of
/// zero that never settles, a retarget that loses its velocity. Nothing here
/// touches WPF or the session, so the tests drive it directly (net8.0, no
/// WPF), while <see cref="TrailMotion"/> keeps the corners for the frame
/// loop.
/// </summary>
internal static class TrailMotionRules
{
    /// <summary>
    /// Neovide's short animation: a move of at most two columns on the same
    /// line - typing, h/l, x - gets its own, much quicker length, so text
    /// entry stays crisp while real jumps still smear. "Same line" is judged
    /// from the quad's centre, so a retarget while the trail is still in
    /// flight from a far jump counts as long.
    /// </summary>
    public static bool IsShortMove(double travelX, double travelY, double columnWidth, double rowHeight) =>
        Math.Abs(travelY) < Math.Max(1, rowHeight / 2)
        && Math.Abs(travelX) <= 2 * columnWidth + 0.5;

    /// <summary>
    /// Ranks the corners by how well they face the direction of travel: 3
    /// leads, 0 trails. Tied corners (both right corners on a pure horizontal
    /// move) share the average of their ranks, so a straight move smears as a
    /// clean stretched box rather than skewing one corner ahead of its twin.
    /// </summary>
    public static void Ranks(double[] dots, double[] ranks)
    {
        for (int i = 0; i < dots.Length; i++)
        {
            double rank = 0;
            for (int j = 0; j < dots.Length; j++)
            {
                if (j == i)
                {
                    continue;
                }

                if (dots[j] < dots[i] - 1e-6)
                {
                    rank += 1;
                }
                else if (Math.Abs(dots[j] - dots[i]) <= 1e-6)
                {
                    rank += 0.5;
                }
            }

            ranks[i] = rank;
        }
    }

    /// <summary>
    /// Leading corners arrive quickly, trailing ones take the full length;
    /// a trail of 0 makes them all equal (a plain slide). The floor keeps a
    /// corner from being given a zero length, which the spring divides by.
    /// </summary>
    public static double CornerLength(double animationSeconds, double trail, double rank) =>
        Math.Max(0.01, animationSeconds * (1 - trail * rank / 3.0));

    /// <summary>
    /// One step of the critically damped spring, solved exactly for
    /// <paramref name="dt"/> - Neovide's cursor animation. Settles in roughly
    /// <paramref name="length"/> seconds with no overshoot, and a retarget
    /// mid-flight (holding j) carries the current velocity instead of
    /// restarting from rest.
    /// </summary>
    public static void Step(ref double position, ref double velocity, double target,
                            double length, double dt)
    {
        double omega = 4.0 / length;
        double x = position - target;
        double b = velocity + omega * x;
        double decay = Math.Exp(-omega * dt);

        double nextX = (x + b * dt) * decay;
        velocity = (b - omega * (x + b * dt)) * decay;
        position = target + nextX;
    }
}
