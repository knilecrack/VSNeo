using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Neovide's scroll animation for the scrolls nvim asks for: &lt;C-d&gt;,
    /// &lt;C-u&gt;, &lt;C-f&gt;, &lt;C-b&gt;, zz/zt/zb, &lt;C-e&gt;/&lt;C-y&gt; and the
    /// far jumps (G, n) whose window move arrives as a topline report.
    /// ViewportSynchronizer.ApplyScroll hands the target here instead of
    /// jumping; wheel scrolling stays Visual Studio's own.
    ///
    /// The position is a critically damped spring - the cursor trail's, one
    /// dimension - measured in visual lines: lines of the visual snapshot, where
    /// a collapsed outlining region is a single line. Buffer lines would stall
    /// the animation on a fold's header while the spring crawled through its
    /// hidden lines. Each frame shows the fractional position with
    /// DisplayTextLineContainingBufferPosition and a negative pixel offset.
    ///
    /// Neovide's far_lines rule keeps long jumps sane: past one screen, the
    /// view first jumps to within far_lines of the target and only that last
    /// stretch animates, so G in a 5000-line file is not a five-second ride.
    ///
    /// Coexistence, which is most of this class's contract:
    /// - ViewportSynchronizer does not capture layouts while this animates, so
    ///   nvim hears the final topline once, not every frame's.
    /// - CursorSynchronizer skips Caret.EnsureVisible while this animates: nvim
    ///   keeps its cursor inside its window, which is where the animation ends,
    ///   and an instant EnsureVisible mid-flight would yank the view.
    /// - Anything else scrolling the view (the wheel, a VS navigation) cancels
    ///   the animation instead of being fought: a frame that finds the view
    ///   somewhere it did not put it stops.
    /// </summary>
    internal sealed class SmoothScroller
    {
        private readonly IWpfTextView _view;
        private double _position;    // visual lines, fractional
        private double _velocity;    // visual lines per second
        private int _target;         // visual line
        private double _length;      // seconds
        private double _lastShown = double.NaN;
        private bool _animating;
        private bool _rendering;
        private TimeSpan _lastFrame;
        private double _elapsed;     // seconds since the current target was set

        private SmoothScroller(IWpfTextView view)
        {
            _view = view;
            view.Closed += OnClosed;
        }

        /// <summary>The view's scroller, created on first use - nothing at view creation.</summary>
        public static SmoothScroller For(IWpfTextView view) =>
            view.Properties.GetOrCreateSingletonProperty(() => new SmoothScroller(view));

        /// <summary>True while an animated scroll is under way. Never creates a scroller.</summary>
        public static bool IsAnimating(ITextView view) =>
            view.Properties.TryGetProperty(typeof(SmoothScroller), out SmoothScroller s) && s._animating;

        /// <summary>
        /// Animates the view so <paramref name="editLine"/> ends up as the top
        /// line. False when animation is off or pointless; the caller then
        /// scrolls instantly as before.
        /// </summary>
        public bool ScrollTo(int editLine)
        {
            var state = VSNeo_ExtensionPackage.Session?.State;
            int ms = state?.ScrollAnimationMs ?? 0;
            if (ms <= 0 || !SystemParameters.ClientAreaAnimation) return false;
            if (_view.IsClosed || _view.InLayout) return false;

            if (!TryGetCurrentPosition(out double current)) return false;
            int target = VisualLineOf(editLine);
            if (target < 0) return false;

            double delta = target - current;
            if (Math.Abs(delta) < 0.01) return false;

            // A new scroll mid-flight retargets and keeps the velocity; a fresh
            // one starts from rest wherever the view actually is.
            if (!_animating)
            {
                _position = current;
                _velocity = 0;
            }

            int rows = Math.Max(1, (int)(_view.ViewportHeight / Math.Max(1, _view.LineHeight)));
            int far = Math.Max(0, state!.ScrollFarLines);
            if (Math.Abs(target - _position) > rows)
            {
                // Neovide's far_lines: only the last stretch animates.
                _position = target - Math.Sign(delta) * Math.Min(Math.Abs(delta), far);
                _velocity = 0;
                if (far == 0)
                {
                    Finish(target);
                    return true;
                }
            }

            _target = target;
            _length = ms / 1000.0;
            _elapsed = 0;
            _animating = true;
            Show(_position);
            StartFrames();
            return true;
        }

        /// <summary>Stops where it is. The view stays put; nothing snaps.</summary>
        public void Cancel()
        {
            _animating = false;
            _velocity = 0;
            StopFrames();
        }

        private void StartFrames()
        {
            if (_rendering) return;
            _rendering = true;
            _lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }

        private void StopFrames()
        {
            if (!_rendering) return;
            _rendering = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object sender, EventArgs e)
        {
            try
            {
                Frame(e as RenderingEventArgs);
            }
            catch (Exception ex)
            {
                // A throwing frame would throw sixty times a second; land the
                // scroll where nvim wanted it and stop.
                Infrastructure.Log.Write("smooth scroll frame failed", ex);
                int target = _target;
                Cancel();
                try { Finish(target); } catch { /* the view is going away */ }
            }
        }

        private void Frame(RenderingEventArgs? args)
        {
            if (!_animating || _view.IsClosed) { Cancel(); return; }
            if (_view.InLayout) return;

            double dt = 1.0 / 60;
            if (args != null)
            {
                if (args.RenderingTime == _lastFrame) return;
                if (_lastFrame != TimeSpan.Zero) dt = (args.RenderingTime - _lastFrame).TotalSeconds;
                _lastFrame = args.RenderingTime;
            }
            if (dt <= 0) return;
            if (dt > 1.0 / 30) dt = 1.0 / 30;

            // Someone else scrolled (the wheel, a VS navigation): give way.
            if (TryGetCurrentPosition(out double actual) && !double.IsNaN(_lastShown)
                && Math.Abs(actual - _lastShown) > 0.75)
            {
                Cancel();
                return;
            }

            // Critically damped spring, solved exactly for the step.
            double omega = 4.0 / _length;
            double x = _position - _target;
            double b = _velocity + omega * x;
            double decay = Math.Exp(-omega * dt);
            double nextX = (x + b * dt) * decay;
            _velocity = (b - omega * (x + b * dt)) * decay;
            _position = _target + nextX;

            // The spring covers ~90% of the way in its length and then crawls:
            // done within a tenth of a line (a couple of pixels, invisible), or
            // at twice the length regardless. Until it finishes, nvim's final
            // topline is held back (ViewportSynchronizer skips capture), so the
            // tail must not linger.
            _elapsed += dt;
            if ((Math.Abs(nextX) < 0.1 && Math.Abs(_velocity) < 2) || _elapsed >= 2 * _length)
            {
                Finish(_target);
                return;
            }

            Show(_position);
        }

        /// <summary>
        /// Lands exactly on the target line. _animating drops first, so the
        /// layout this causes is captured by ViewportSynchronizer like any other.
        /// </summary>
        private void Finish(int target)
        {
            _animating = false;
            _velocity = 0;
            StopFrames();
            Show(target);
            _lastShown = double.NaN;
        }

        private void Show(double position)
        {
            var visual = _view.VisualSnapshot;
            int maxLine = Math.Max(0, visual.LineCount - 1);
            if (position < 0) position = 0;
            if (position > maxLine) position = maxLine;

            int line = (int)Math.Floor(position);
            double fraction = position - line;

            var point = EditPointOfVisualLine(line);
            if (point == null) return;

            // Negative distance: the line's top sits that far above the view's
            // top edge - a fractional scroll position.
            _view.DisplayTextLineContainingBufferPosition(
                point.Value, -fraction * _view.LineHeight, ViewRelativePosition.Top);
            _lastShown = position;
        }

        /// <summary>
        /// Where the view is now, in visual lines: the first visible line plus
        /// how far it is scrolled past its top.
        /// </summary>
        private bool TryGetCurrentPosition(out double position)
        {
            position = 0;
            var lines = _view.TextViewLines;
            if (lines == null || lines.Count == 0) return false;

            var first = lines.FirstVisibleLine;
            int visualLine = VisualLineOfPoint(first.Start);
            if (visualLine < 0) return false;

            double fraction = first.Height > 0 ? (_view.ViewportTop - first.Top) / first.Height : 0;
            if (fraction < 0) fraction = 0;
            if (fraction > 1) fraction = 1;
            position = visualLine + fraction;
            return true;
        }

        private int VisualLineOf(int editLine)
        {
            var snapshot = _view.TextSnapshot;
            if (editLine < 0) editLine = 0;
            if (editLine >= snapshot.LineCount) editLine = snapshot.LineCount - 1;
            return VisualLineOfPoint(snapshot.GetLineFromLineNumber(editLine).Start);
        }

        private int VisualLineOfPoint(SnapshotPoint point)
        {
            // A point hidden inside a collapsed region maps to the region's
            // header, which is the line on screen.
            var visual = _view.BufferGraph.MapUpToSnapshot(
                point, PointTrackingMode.Negative, PositionAffinity.Predecessor, _view.VisualSnapshot);
            if (visual == null) return -1;
            return _view.VisualSnapshot.GetLineNumberFromPosition(visual.Value.Position);
        }

        private SnapshotPoint? EditPointOfVisualLine(int visualLine)
        {
            var visual = _view.VisualSnapshot;
            if (visualLine < 0) visualLine = 0;
            if (visualLine >= visual.LineCount) visualLine = visual.LineCount - 1;

            var start = visual.GetLineFromLineNumber(visualLine).Start;
            return _view.BufferGraph.MapDownToSnapshot(
                start, PointTrackingMode.Negative, _view.TextSnapshot, PositionAffinity.Successor);
        }

        private void OnClosed(object sender, EventArgs e)
        {
            Cancel();
            _view.Closed -= OnClosed;
        }
    }
}
