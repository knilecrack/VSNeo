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
            _motion.Forget();
            _caretMoved = false;
            Settle(hide: true);
            return false;
        }

        if (_caretMoved)
        {
            _caretMoved = false;
            if (_motion.HasPosition)
            {
                EmitVfx(target);
            }

            if (!_motion.HasPosition || !BeginMove(target))
            {
                Snap(target);
                return false;
            }
        }
        else if (!_motion.IsMoving)
        {
            // Not animating: a snap request (scroll, edit, a move the
            // trail skips). Track the caret and go quiet.
            Snap(target);
            return false;
        }

        if (!_motion.Advance(target, dt))
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
        var host = PrepareEffects(new Rect(_motion.Position(0), _motion.Position(2)));
        if (host == null)
        {
            return;
        }

        host.Jump(new Rect(_motion.Position(0), _motion.Position(2)), new Rect(target[0], target[2]),
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
    /// Hands the move to <see cref="TrailMotion"/>, which assigns each corner
    /// its animation length. The settings are read here: motion knows nothing
    /// about the session.
    /// </summary>
    private bool BeginMove(Point[] target)
    {
        var state = State;

        // "Same line" is judged from the quad's centre (TrailMotion), so a
        // retarget while the trail is still in flight from a far jump counts
        // as long rather than short.
        double column = _view.FormattedLineSource?.ColumnWidth ?? 8;
        return _motion.Begin(
            target,
            column,
            (state?.CursorAnimationMs ?? 0) / 1000.0,
            (state?.CursorShortAnimationMs ?? 0) / 1000.0,
            Math.Max(0, Math.Min(1, (state?.CursorTrailPermille ?? 0) / 1000.0)));
    }

    private void Snap(Point[] target)
    {
        _motion.Snap(target);
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

        _motion.Stop();
    }
}
