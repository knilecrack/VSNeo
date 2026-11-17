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

// CompositionTarget.Rendering, subscribed only while something is animating.
internal sealed partial class CursorTrailAdornment
{
    private void StartFrames()
    {
        if (_rendering || _closed)
        {
            return;
        }

        _rendering = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopFrames()
    {
        if (!_rendering)
        {
            return;
        }

        _rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object sender, EventArgs e)
    {
        using var perf = Infrastructure.Perf.Time("CursorTrailAdornment.OnFrame");
        try
        {
            Frame(e as RenderingEventArgs);
        }
        catch (Exception ex)
        {
            // A decoration must never take the editor down with it, and
            // a throwing frame handler would throw again sixty times a
            // second: stop and hide.
            Infrastructure.Log.Write("cursor trail frame failed", ex);
            Settle(hide: true);
            _effects?.Clear();
            StopFrames();
        }
    }

    private void Frame(RenderingEventArgs? args)
    {
        if (_closed)
        { StopFrames(); return; }

        // Mid-layout the caret geometry is not readable; the next frame is.
        if (_view.InLayout)
        {
            return;
        }

        // Frame time from WPF's own clock; the first frame of a run has no
        // predecessor, and a stalled UI thread must not become one giant
        // step that overshoots the whole animation.
        double dt = 1.0 / 60;
        if (args != null)
        {
            if (args.RenderingTime == _lastFrame)
            {
                return; // same frame, fired twice
            }

            if (_lastFrame != TimeSpan.Zero)
            {
                dt = (args.RenderingTime - _lastFrame).TotalSeconds;
            }

            _lastFrame = args.RenderingTime;
        }

        if (dt <= 0)
        {
            return;
        }

        if (dt > 1.0 / 30)
        {
            dt = 1.0 / 30;
        }

        bool trailActive = StepTrail(dt);

        if (_typedPending || _focusPending || _modePending)
        {
            FireTriggers();
        }

        // Effects outlive the trail: the loop keeps running until both
        // are done, then unsubscribes.
        bool effectsActive = false;
        if (_effects != null)
        {
            PlaceEffects(_effects);
            effectsActive = _effects.Update(dt);
        }

        if (!trailActive && !effectsActive)
        {
            StopFrames();
        }
    }
}
