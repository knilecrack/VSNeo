using System;
using System.Windows;

namespace VSNeo_Extension.Editor;

/// <summary>
/// The trail's four corners: where they are, how fast they are moving, and
/// how long each one's spring runs. One object per view, owned by the
/// adornment, which supplies the caret's corners and the settings; the maths
/// lives in <see cref="TrailMotionRules"/>.
///
/// Corners are ordered top-left, top-right, bottom-right, bottom-left, and
/// positions are viewport-relative, not view coordinates: a jump that also
/// scrolls should travel from where the cursor was *on screen*.
/// </summary>
internal sealed class TrailMotion
{
    private const int Corners = 4;

    private readonly double[] _x = new double[Corners];
    private readonly double[] _y = new double[Corners];
    private readonly double[] _velocityX = new double[Corners];
    private readonly double[] _velocityY = new double[Corners];
    private readonly double[] _length = { 1, 1, 1, 1 };

    /// <summary>Whether the corners describe a real caret rect yet.</summary>
    public bool HasPosition { get; private set; }

    public Point Position(int corner) => new(_x[corner], _y[corner]);

    /// <summary>
    /// Any corner still moving. The threshold is deliberately loose: below
    /// it the quad sits under the real caret and nobody can see it.
    /// </summary>
    public bool IsMoving
    {
        get
        {
            for (int i = 0; i < Corners; i++)
            {
                if (_velocityX[i] * _velocityX[i] + _velocityY[i] * _velocityY[i] > 4)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Places the quad on <paramref name="target"/> and stops it.</summary>
    public void Snap(Point[] target)
    {
        for (int i = 0; i < Corners; i++)
        {
            _x[i] = target[i].X;
            _y[i] = target[i].Y;
            _velocityX[i] = 0;
            _velocityY[i] = 0;
        }

        HasPosition = true;
    }

    /// <summary>Drops the position entirely: the caret is not on screen.</summary>
    public void Forget()
    {
        HasPosition = false;
        Stop();
    }

    /// <summary>Stops the springs where they are, so frames can stop.</summary>
    public void Stop()
    {
        for (int i = 0; i < Corners; i++)
        {
            _velocityX[i] = 0;
            _velocityY[i] = 0;
        }
    }

    /// <summary>
    /// Starts a move toward <paramref name="target"/>, assigning each corner
    /// its animation length. False when the move is too small to show or the
    /// settings ask for no animation at all - the caller then snaps.
    /// </summary>
    public bool Begin(Point[] target, double columnWidth,
                      double animationSeconds, double shortAnimationSeconds, double trail)
    {
        double fromX = (_x[0] + _x[1] + _x[2] + _x[3]) / Corners;
        double fromY = (_y[0] + _y[1] + _y[2] + _y[3]) / Corners;
        double toX = (target[0].X + target[1].X + target[2].X + target[3].X) / Corners;
        double toY = (target[0].Y + target[1].Y + target[2].Y + target[3].Y) / Corners;

        double travelX = toX - fromX;
        double travelY = toY - fromY;
        if (travelX * travelX + travelY * travelY < 1)
        {
            return false;
        }

        bool shortMove = TrailMotionRules.IsShortMove(
            travelX, travelY, columnWidth, target[3].Y - target[0].Y);
        double seconds = shortMove ? shortAnimationSeconds : animationSeconds;
        if (seconds <= 0)
        {
            return false;
        }

        double norm = Math.Sqrt(travelX * travelX + travelY * travelY);
        travelX /= norm;
        travelY /= norm;

        var dots = new double[Corners];
        for (int i = 0; i < Corners; i++)
        {
            double outwardX = target[i].X - toX;
            double outwardY = target[i].Y - toY;
            double outward = Math.Sqrt(outwardX * outwardX + outwardY * outwardY);
            if (outward > 0)
            {
                outwardX /= outward;
                outwardY /= outward;
            }

            dots[i] = outwardX * travelX + outwardY * travelY;
        }

        var ranks = new double[Corners];
        TrailMotionRules.Ranks(dots, ranks);
        for (int i = 0; i < Corners; i++)
        {
            _length[i] = TrailMotionRules.CornerLength(seconds, trail, ranks[i]);
        }

        return true;
    }

    /// <summary>
    /// One frame toward <paramref name="target"/>. Returns whether anything
    /// is still moving; within a pixel of the target the quad is hidden under
    /// the real caret anyway, and waiting for the spring's exponential tail
    /// would only leave a faint double caret on screen.
    /// </summary>
    public bool Advance(Point[] target, double dt)
    {
        bool moving = false;
        for (int i = 0; i < Corners; i++)
        {
            TrailMotionRules.Step(ref _x[i], ref _velocityX[i], target[i].X, _length[i], dt);
            TrailMotionRules.Step(ref _y[i], ref _velocityY[i], target[i].Y, _length[i], dt);

            double dx = _x[i] - target[i].X;
            double dy = _y[i] - target[i].Y;
            if (dx * dx + dy * dy > 1
                || _velocityX[i] * _velocityX[i] + _velocityY[i] * _velocityY[i] > 400)
            {
                moving = true;
            }
        }

        return moving;
    }
}
