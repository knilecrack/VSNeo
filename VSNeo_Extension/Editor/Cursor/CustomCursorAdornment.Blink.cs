using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// The blink: timers for the plain styles, animations for the smooth ones.
internal sealed partial class CustomCursorAdornment
{
    /// <summary>
    /// Solid while moving, blinking once it rests: every caret move stops
    /// the blink and re-arms it one blink interval later, as Visual Studio
    /// and VS Code both do.
    /// </summary>
    private void RestartBlink(NvimStateHub state)
    {
        StopBlink();

        var blinking = BlinkingOf(state);
        int interval = CaretBlinkMs();
        if (blinking == CursorBlinking.Solid || interval <= 0)
        {
            return;
        }

        _blinkDelay ??= new DispatcherTimer(DispatcherPriority.Normal, _view.VisualElement.Dispatcher);
        _blinkDelay.Tick -= OnBlinkDelay;
        _blinkDelay.Tick += OnBlinkDelay;
        _blinkDelay.Interval = TimeSpan.FromMilliseconds(interval);
        _blinkDelay.Start();
    }

    private void OnBlinkDelay(object sender, EventArgs e)
    {
        _blinkDelay?.Stop();
        if (_closed || _shape == null || _shape.Visibility != Visibility.Visible)
        {
            return;
        }

        var state = VSNeo_ExtensionPackage.Session?.State;
        if (state == null)
        {
            return;
        }

        var blinking = BlinkingOf(state);

        // The smooth styles are motion; with Windows animations off they
        // fall back to a plain blink, which is what the system caret does.
        // Software rendering too: ten seconds at 60 fps of a repainted
        // cursor is CPU work there, and a frame stream over Remote Desktop.
        if (blinking != CursorBlinking.Blink
            && (!SystemParameters.ClientAreaAnimation
                || Infrastructure.RenderTier.ReduceEffects(_view.VisualElement)))
        {
            blinking = CursorBlinking.Blink;
        }

        switch (blinking)
        {
            case CursorBlinking.Blink:
                _blinkTimer ??= new DispatcherTimer(DispatcherPriority.Normal, _view.VisualElement.Dispatcher);
                _blinkTimer.Tick -= OnBlinkTick;
                _blinkTimer.Tick += OnBlinkTick;
                _blinkTimer.Interval = TimeSpan.FromMilliseconds(CaretBlinkMs());
                _shape.Opacity = 0;
                _blinkTimer.Start();
                break;

            case CursorBlinking.Smooth:
                // VS Code: opacity 1 for 0-20%, 0 from 60%, ease-in-out.
                _shape.BeginAnimation(UIElement.OpacityProperty, Pulse(0.2, 0.6));
                break;

            case CursorBlinking.Phase:
                // VS Code: opacity 1 for 0-20%, 0 from 90%.
                _shape.BeginAnimation(UIElement.OpacityProperty, Pulse(0.2, 0.9));
                break;

            case CursorBlinking.Expand:
                // VS Code: scaleY 1 for 0-20%, 0 from 80% - the cursor
                // collapses into its vertical centre and grows back.
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, Pulse(0.2, 0.8));
                break;
        }
    }

    private void OnBlinkTick(object sender, EventArgs e)
    {
        if (_shape == null)
        {
            return;
        }

        _shape.Opacity = _shape.Opacity > 0.5 ? 0 : 1;
    }

    /// <summary>
    /// One VS Code blink keyframe set: 1 until <paramref name="holdUntil"/>,
    /// eased down to 0 by <paramref name="goneFrom"/>, over a 0.5 s half
    /// cycle, auto-reversed - ten full cycles, then it rests at 1. The
    /// resting end matters: an idle editor is not redrawn 60 times a second
    /// forever for a blinking cursor.
    /// </summary>
    private static DoubleAnimationUsingKeyFrames Pulse(double holdUntil, double goneFrom)
    {
        const double Half = 0.5;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(Half),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(10),
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Half * holdUntil))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Half * goneFrom)), ease));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Half))));
        animation.Freeze();
        return animation;
    }

    private void StopBlink()
    {
        _blinkDelay?.Stop();
        _blinkTimer?.Stop();
        if (_shape != null)
        {
            // A null animation hands the property back to its base value.
            _shape.BeginAnimation(UIElement.OpacityProperty, null);
            _shape.Opacity = 1;
        }

        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _scale.ScaleY = 1;
    }

    // ---- settings -------------------------------------------------------

    [DllImport("user32.dll")]
    private static extern uint GetCaretBlinkTime();

    /// <summary>
    /// The system caret blink interval (Control Panel's cursor blink rate),
    /// 0 when the user switched blinking off.
    /// </summary>
    private static int CaretBlinkMs()
    {
        try
        {
            uint ms = GetCaretBlinkTime();
            if (ms == 0 || ms == uint.MaxValue)
            {
                return 0;
            }

            return (int)Math.Min(ms, 5000);
        }
        catch
        {
            return 530;
        }
    }
}
