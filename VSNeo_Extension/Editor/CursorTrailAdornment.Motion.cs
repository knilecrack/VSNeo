using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// The springs: corner targets, the per-frame step, and the effects they trigger.
internal sealed partial class CursorTrailAdornment
{
    /// <summary>One frame of the trail. Returns whether it is still moving.</summary>
    private bool StepTrail(double dt)
    {
        if (!TryGetCaretCorners(out var target))
        {
            // Caret scrolled out of view or not laid out: nothing to draw
            // toward, and nothing valid to remember.
            _hasPosition = false;
            _caretMoved = false;
            Settle(hide: true);
            return false;
        }

        if (_caretMoved)
        {
            _caretMoved = false;
            if (_hasPosition)
            {
                EmitVfx(target);
            }

            if (!_hasPosition || !BeginMove(target))
            {
                Snap(target);
                return false;
            }
        }
        else if (!IsMoving())
        {
            // Not animating: a snap request (scroll, edit, a move the
            // trail skips). Track the caret and go quiet.
            Snap(target);
            return false;
        }

        bool settled = true;
        for (int i = 0; i < 4; i++)
        {
            Step(ref _pos[i], ref _vel[i], target[i], _length[i], dt);
            // Within a pixel the quad is hidden under the real caret
            // anyway; waiting for the spring's long exponential tail
            // would only leave a faint double caret on screen.
            if ((_pos[i] - target[i]).Length > 1 || _vel[i].Length > 20)
            {
                settled = false;
            }
        }

        if (settled)
        {
            Snap(target);
            return false;
        }

        Draw();
        return true;
    }

    /// <summary>
    /// Particles and highlights for a jump, from where the cursor was
    /// (the trail's current quad - mid-flight if it was still moving) to
    /// where it is.
    /// </summary>
    private void EmitVfx(Point[] target)
    {
        var host = PrepareEffects(new Rect(target[0], target[2]));
        if (host == null)
        {
            return;
        }

        host.Jump(new Rect(_pos[0], _pos[2]), new Rect(target[0], target[2]),
                  Infrastructure.RenderTier.ReduceEffects(_view.VisualElement));
    }

    /// <summary>
    /// Fires the queued non-jump triggers at the cursor's current cell.
    /// Frame-time, not event-time: the caret event can arrive before the
    /// line it moved to has been laid out.
    /// </summary>
    private void FireTriggers()
    {
        bool typed = _typedPending, focus = _focusPending, mode = _modePending;
        _typedPending = _focusPending = _modePending = false;

        if (!TryGetCaretCorners(out var corners))
        {
            return;
        }

        var cell = new Rect(corners[0], corners[2]);
        var host = PrepareEffects(cell);
        if (host == null)
        {
            return;
        }

        bool reduce = Infrastructure.RenderTier.ReduceEffects(_view.VisualElement);
        if (typed)
        {
            host.Type(cell, reduce);
        }

        if (mode)
        {
            host.ModeChanged(_modeFrom, _modeTo, cell, reduce);
        }

        if (focus)
        {
            host.Focus(cell, reduce);
        }
    }

    /// <summary>
    /// The effect host, configured from vsneo_cursor_vfx_mode and primed
    /// with this moment's color and settings; null when no effect is on.
    /// </summary>
    private Effects.CursorEffectHost? PrepareEffects(Rect cell)
    {
        var state = State;
        if (state == null)
        {
            return null;
        }

        if (_effects == null && !EffectsConfigured(state))
        {
            return null;
        }

        var host = EnsureEffects();
        host.Configure(state.CursorVfxModes);
        if (!host.HasEffects)
        {
            return null;
        }

        PlaceEffects(host);
        host.Prepare(state, CaretColor(), cell);
        return host;
    }

    /// <summary>
    /// Assigns each corner its animation length from how well it faces the
    /// direction of travel. Returns false for a move too small to show.
    /// </summary>
    private bool BeginMove(Point[] target)
    {
        var from = Center(_pos);
        var to = Center(target);
        var travel = to - from;
        if (travel.Length < 1)
        {
            return false;
        }

        // Neovide's short animation: a move of at most two columns on the
        // same line - typing, h/l, x - gets its own, much quicker length,
        // so text entry stays crisp while real jumps still smear. "Same
        // line" is judged from the quad's centre, so a retarget while the
        // trail is still in flight from a far jump counts as long.
        var state = State;
        double column = _view.FormattedLineSource?.ColumnWidth ?? 8;
        double rowTolerance = Math.Max(1, (target[3].Y - target[0].Y) / 2);
        bool shortMove = Math.Abs(travel.Y) < rowTolerance
                         && Math.Abs(travel.X) <= 2 * column + 0.5;
        int ms = shortMove
            ? (state?.CursorShortAnimationMs ?? 0)
            : (state?.CursorAnimationMs ?? 0);
        double length = ms / 1000.0;
        travel.Normalize();
        double trail = Math.Max(0, Math.Min(1, (state?.CursorTrailPermille ?? 0) / 1000.0));
        if (length <= 0)
        {
            return false;
        }

        // Rank the destination corners by alignment with the travel
        // direction: rank 3 leads, rank 0 trails. Tied corners (both right
        // corners on a pure horizontal move) share the average of their
        // ranks, so a straight move smears as a clean stretched box rather
        // than skewing one corner ahead of its twin.
        var dots = new double[4];
        for (int i = 0; i < 4; i++)
        {
            var outward = target[i] - to;
            if (outward.Length > 0)
            {
                outward.Normalize();
            }

            dots[i] = Vector.Multiply(outward, travel);
        }

        for (int i = 0; i < 4; i++)
        {
            double rank = 0;
            for (int j = 0; j < 4; j++)
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

            // Leading corners arrive quickly, trailing ones take the full
            // length; trail_size 0 makes them all equal (a plain slide).
            _length[i] = Math.Max(0.01, length * (1 - trail * rank / 3.0));
        }

        return true;
    }

    /// <summary>
    /// Critically damped spring, solved exactly for the step - Neovide's
    /// cursor animation. Settles in roughly <paramref name="length"/>
    /// seconds with no overshoot, and a retarget mid-flight (holding j)
    /// carries the current velocity instead of restarting from rest.
    /// </summary>
    private static void Step(ref Point position, ref Vector velocity, Point target,
                             double length, double dt)
    {
        double omega = 4.0 / length;
        var x = position - target;
        var b = velocity + omega * x;
        double decay = Math.Exp(-omega * dt);

        var nextX = (x + b * dt) * decay;
        velocity = (b - omega * (x + b * dt)) * decay;
        position = target + nextX;
    }

    private bool IsMoving()
    {
        for (int i = 0; i < 4; i++)
        {
            if (_vel[i].Length > 2)
            {
                return true;
            }
        }

        return false;
    }

    private void Snap(Point[] target)
    {
        for (int i = 0; i < 4; i++)
        {
            _pos[i] = target[i];
            _vel[i] = default;
        }

        _hasPosition = true;
        Settle(hide: true);
    }

    /// <summary>
    /// Stops the trail. Frames are the caller's call: particles may still
    /// need them (Frame stops once neither is active).
    /// </summary>
    private void Settle(bool hide)
    {
        if (hide && _shape != null)
        {
            _shape.Visibility = Visibility.Collapsed;
        }

        for (int i = 0; i < 4; i++)
        {
            _vel[i] = default;
        }
    }
}
