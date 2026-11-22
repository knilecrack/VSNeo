using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Editor;

namespace VSNeo_Extension.Editor;

// Messages for a view in a floating document window. Messages live in the
// shell status bar (ModeStatusBarItem), but a floated document can sit on
// another monitor, far from that bar, and an E492 there goes unseen. Such a
// view draws the message over its own bottom-right corner instead. It is an
// adornment, not a margin: a margin resizes the view, and nvim's window
// with it (the reason messages left the view in the first place).
internal sealed partial class MessagePager
{
    private static readonly SolidColorBrush ErrorBackground = Frozen(Color.FromRgb(0xB0, 0x39, 0x2E));
    private static readonly SolidColorBrush ErrorForeground = Frozen(Color.FromRgb(0xF5, 0xF5, 0xF5));

    /// <summary>
    /// The last focused view lives in a floating window, so its message is
    /// drawn there and the status bar leaves it out. UI thread writes;
    /// ModeStatusBarItem reads it on the UI thread too.
    /// </summary>
    internal static bool FocusedViewFloats { get; private set; }

    // Read on the RPC thread to skip the post for views in the main window.
    private volatile bool _floats;
    private Border? _messageRoot;
    private TextBlock? _messageText;
    private bool _messageShown;

    /// <summary>
    /// UI thread, on focus gain. Only checked then: dragging a focused tab
    /// out of the main window is noticed at the next focus change.
    /// </summary>
    private void NoteFloating()
    {
        var window = Window.GetWindow(_view.VisualElement);
        _floats = window != null && !ReferenceEquals(window, Application.Current?.MainWindow);
        if (_floats != FocusedViewFloats)
        {
            FocusedViewFloats = _floats;
            ModeStatusBarItem.Refresh();
        }
    }

    /// <summary>
    /// UI thread, on focus loss. The overlay goes with the focus, so the
    /// status bar takes messages back: focus moving on to a tool window
    /// (Output, Error List) left them shown nowhere. A main-window or
    /// floating view gaining focus next sets the flag again.
    /// </summary>
    private void ReleaseFloating()
    {
        if (_floats && FocusedViewFloats)
        {
            FocusedViewFloats = false;
            ModeStatusBarItem.Refresh();
        }
    }

    /// <summary>RPC read thread.</summary>
    private void OnMessageChanged(string content)
    {
        if (!_focused || !_floats)
        {
            return;
        }
#pragma warning disable VSTHRD001
        _ = _view.VisualElement.Dispatcher.BeginInvoke(
            Infrastructure.UiPriority.Decoration, new Action(RenderMessage));
#pragma warning restore VSTHRD001
    }

    private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e) => PlaceMessage();

    private void RenderMessage()
    {
        using var perf = Infrastructure.Perf.Time("MessagePager.RenderMessage");
        if (_view.IsClosed)
        {
            return;
        }

        // Also cleared when nvim is gone or VSNeo is switched off: the
        // message would outlive the session that sent it.
        var session = VSNeo_ExtensionPackage.Session;
        var state = session != null && session.IsReady ? session.State : null;
        var message = _focused && _floats && Infrastructure.VSNeoSettings.Enabled ? state?.Message : null;
        if (state == null || string.IsNullOrEmpty(message))
        {
            if (_messageShown)
            {
                _layer.RemoveAdornment(_messageRoot!);
                _messageShown = false;
            }

            return;
        }

        if (_messageRoot == null)
        {
            _messageText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            _messageRoot = new Border
            {
                Child = _messageText,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 2),
                SnapsToDevicePixels = true,
            };
        }

        _messageText!.Text = ModeStatusBarItem.OneLine(message);
        _messageRoot.ToolTip = message;
        if (ModeStatusBarItem.IsError(state.MessageKind))
        {
            _messageRoot.Background = ErrorBackground;
            _messageRoot.BorderBrush = ErrorBackground;
            _messageText.Foreground = ErrorForeground;
        }
        else
        {
            // The pager's theming: the view's own background, so it reads
            // as part of the editor in any theme.
            var background = _view.Background as SolidColorBrush;
            var c = background?.Color ?? Colors.White;
            bool dark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
            _messageRoot.Background = background ?? Brushes.White;
            _messageRoot.BorderBrush = dark ? Brushes.DimGray : Brushes.Silver;
            _messageText.Foreground = dark ? Brushes.Gainsboro : Brushes.Black;
        }

        if (!_messageShown)
        {
            _layer.AddAdornment(AdornmentPositioningBehavior.ViewportRelative, null, null, _messageRoot, null);
            _messageShown = true;
        }

        PlaceMessage();
    }

    /// <summary>
    /// Bottom-right of the viewport, at most 60% wide; moved to the top
    /// while the caret is on the rows it would cover.
    /// </summary>
    private void PlaceMessage()
    {
        if (!_messageShown || _messageRoot == null)
        {
            return;
        }

        double maxWidth = Math.Max(120, _view.ViewportWidth * 0.6);
        _messageRoot.MaxWidth = maxWidth;
        _messageRoot.Measure(new Size(maxWidth, double.PositiveInfinity));
        var size = _messageRoot.DesiredSize;

        // Text-view coordinates, viewport origin added: see Place().
        double bottomTop = _view.ViewportBottom - size.Height - Inset;
        bool overCaret;
        try
        {
            overCaret = _view.Caret.Bottom > bottomTop;
        }
        catch (InvalidOperationException)
        {
            // Mid-layout the caret's line is not formatted; the next move re-places.
            overCaret = false;
        }

        Canvas.SetLeft(_messageRoot, Math.Max(_view.ViewportLeft, _view.ViewportRight - size.Width - Inset));
        Canvas.SetTop(_messageRoot, overCaret ? _view.ViewportTop + Inset : bottomTop);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
