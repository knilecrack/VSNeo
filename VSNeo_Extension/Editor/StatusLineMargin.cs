using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

[Export(typeof(IWpfTextViewMarginProvider))]
[Name(StatusLineMargin.MarginName)]
[Order(After = PredefinedMarginNames.HorizontalScrollBar)]
[MarginContainer(PredefinedMarginNames.Bottom)]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class StatusLineMarginProvider : IWpfTextViewMarginProvider
{
    // Populated by MEF after construction.
    [Import]
    internal IClassificationFormatMapService FormatMapService { get; set; } = null!;

    public IWpfTextViewMargin CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin parent) =>
        new StatusLineMargin(host.TextView, FormatMapService);
}

/// <summary>
/// Vim's statusline under each view, for 'laststatus=2'. nvim renders it -
/// the user's 'statusline', lualine, or the companion's default with the
/// mode chip - and this draws the colored runs it reports.
///
/// Fixed height and always there while 'laststatus' is 2: a bottom margin
/// that came and went (the old message margin) resized the view, and nvim's
/// window with it, on every operator. This one changes the view's height
/// once, when the option changes, and the viewport sync reports it once.
///
/// nvim has one window, so the line always describes the focused view.
/// Unfocused views keep what they last showed, dimmed like StatusLineNC.
/// </summary>
internal sealed class StatusLineMargin : Border, IWpfTextViewMargin
{
    public const string MarginName = "VSNeoStatusLine";

    // UI thread only; a statusline uses a handful of colors.
    private static readonly Dictionary<int, SolidColorBrush> s_brushes = new();

    private readonly IWpfTextView _view;
    private readonly TextBlock _text;
    private NvimStateHub _subscribedTo = null!;
    private int _readyHooked;
    private bool _disposed;

    // Read on the RPC thread: unfocused views skip the post unless the
    // margin has to appear or disappear (every view keeps the same height).
    private volatile bool _focused;
    private volatile bool _shown;
    private readonly double _fontSize;

    // UI thread only. nvim has one window, so one width: the focused
    // margin's, sent only when it changes.
    private static int s_sentColumns;
    private bool _checkDrawnWidth;

    // Statuslines lean on Nerd Font glyphs (powerline arrows, icons) that
    // editor fonts lack; WPF tries each family in turn, per character, so
    // only those glyphs come from here. Families that are not installed are
    // skipped. ponytail: a fixed list of the common Mono builds; add a
    // vsneo option if someone's Nerd Font is not on it.
    private const string NerdFontFallback =
        ", Symbols Nerd Font Mono, CaskaydiaMono Nerd Font Mono, CaskaydiaCove Nerd Font Mono"
        + ", JetBrainsMono Nerd Font Mono, FiraCode Nerd Font Mono, MesloLGS Nerd Font Mono"
        + ", Hack Nerd Font Mono, BlexMono Nerd Font Mono, SauceCodePro Nerd Font Mono";

    public StatusLineMargin(IWpfTextView view, IClassificationFormatMapService formatMapService)
    {
        _view = view;
        _focused = view.HasAggregateFocus;

        // The editor's font, so nvim's column arithmetic (%=, %<) lines up
        // with what is drawn.
        var family = new FontFamily("Consolas");
        double size = 13;
        try
        {
            var map = formatMapService?.GetClassificationFormatMap(view);
            if (map != null)
            {
                family = map.DefaultTextProperties.Typeface.FontFamily;
                size = map.DefaultTextProperties.FontRenderingEmSize;
            }
        }
        catch
        {
            // Cosmetic only; the fallback font reads fine.
        }

        _fontSize = size;
        _text = new TextBlock
        {
            FontFamily = new FontFamily(family.Source + NerdFontFallback),
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = _text;
        ClipToBounds = true;
        ApplyZoom();
        Visibility = Visibility.Collapsed;

        Subscribe();
        view.GotAggregateFocus += OnGotFocus;
        view.LostAggregateFocus += OnLostFocus;
        view.ZoomLevelChanged += OnZoomLevelChanged;
        SizeChanged += (_, _) => SendWidth();
        Infrastructure.VSNeoSettings.Changed += Render;
        Render();
    }

    /// <summary>
    /// The text is drawn zoomed and nvim counts columns of zoomed text: at
    /// 117% an unzoomed line ended a sixth short of the right edge. Changes
    /// the margin's height with the zoom - once per zoom step, never per key.
    /// </summary>
    private void ApplyZoom()
    {
        double size = _fontSize * _view.ZoomLevel / 100.0;
        _text.FontSize = size;
        Height = Math.Ceiling(size * _text.FontFamily.LineSpacing) + 2;
    }

    private void OnZoomLevelChanged(object sender, ZoomLevelChangedEventArgs e)
    {
        ApplyZoom();
        SendWidth();
    }

    /// <summary>
    /// Tells nvim how many columns this margin holds, so '%=' reaches its
    /// right edge: it spans line numbers and scrollbar too, nvim's window
    /// only the text. Resize, zoom and focus only - never the key path.
    /// </summary>
    private void SendWidth()
    {
        var session = VSNeo_ExtensionPackage.Session;
        if (!_shown || !_focused || session == null || ActualWidth <= 0)
        {
            return;
        }

        // A first estimate, in Visual Studio's Display text formatting. Only
        // an estimate: Display hints each advance per size and DPI, and the
        // drawn column came out 8.667 DIPs for an estimated 8.913 at 117%,
        // 8.0 for 7.617 at 100%. CheckDrawnWidth corrects from what is drawn.
        var sample = new FormattedText(
            "0000000000", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(_text.FontFamily, _text.FontStyle, _text.FontWeight, _text.FontStretch),
            _text.FontSize, Brushes.Black, null, TextOptions.GetTextFormattingMode(_text),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double column = sample.WidthIncludingTrailingWhitespace / 10;
        SendColumns(column > 0 ? (int)(ActualWidth / column) : 0);
    }

    private void SendColumns(int columns)
    {
        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || columns <= 0)
        {
            return;
        }

        _checkDrawnWidth = true;
        if (columns == s_sentColumns)
        {
            return;
        }

        s_sentColumns = columns;
        _ = session.RequestAsync("nvim_exec_lua", "vsneo.statusline_width(...)", new object[] { columns })
            .ContinueWith(
                t => { _ = t.Exception; },
                CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted,
                System.Threading.Tasks.TaskScheduler.Default);
    }

    /// <summary>
    /// After the line for a sent width is drawn: does it fit? Measures the
    /// drawn text and, if a column more or fewer fits, sends the corrected
    /// count - one more round trip per resize or zoom, never per key. Only a
    /// line exactly as long as the width sent qualifies (one that fills it
    /// with %=); other lines say nothing about the column width.
    /// </summary>
    private void CheckDrawnWidth(int chars)
    {
        if (!_checkDrawnWidth || chars != s_sentColumns || ActualWidth <= 0)
        {
            return;
        }

        _checkDrawnWidth = false;
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double column = _text.DesiredSize.Width / chars;
        int fit = column > 0 ? (int)(ActualWidth / column) : 0;
        if (fit != chars)
        {
            SendColumns(fit);
            _checkDrawnWidth = false;   // converged: the drawn column does not depend on the count
        }
    }

    private void Subscribe()
    {
        // Posted from the ready broadcast: the view can close before the post runs.
        if (_disposed)
        {
            return;
        }

        var session = VSNeo_ExtensionPackage.Session;
        if (session == null)
        {
            // Created with the startup document, before the package loaded.
            if (Interlocked.Exchange(ref _readyHooked, 1) == 0)
            {
                VSNeo_ExtensionPackage.SessionReadyChanged += OnSessionReady;
            }

            return;
        }

        if (ReferenceEquals(_subscribedTo, session.State))
        {
            return;
        }

        if (_subscribedTo != null)
        {
            _subscribedTo.StatusLineChanged -= OnStatusLineChanged;
        }

        session.State.StatusLineChanged += OnStatusLineChanged;
        _subscribedTo = session.State;
        Render();
    }

    private void OnSessionReady(bool ready)
    {
        if (!ready)
        {
            return;
        }
#pragma warning disable VSTHRD001
        _ = Dispatcher.BeginInvoke(Infrastructure.UiPriority.Decoration, new Action(Subscribe));
#pragma warning restore VSTHRD001
    }

    /// <summary>RPC read thread.</summary>
    private void OnStatusLineChanged()
    {
        bool visible = _subscribedTo?.StatusLine.LastStatus == 2;
        if (!_focused && visible == _shown)
        {
            return;
        }
#pragma warning disable VSTHRD001
        _ = Dispatcher.BeginInvoke(Infrastructure.UiPriority.Decoration, new Action(Render));
#pragma warning restore VSTHRD001
    }

    // The flag comes from the event, never HasAggregateFocus: inside the
    // focus events that property can still read the old value.
    private void OnGotFocus(object sender, EventArgs e)
    {
        _focused = true;
        Render();
        SendWidth();
    }

    private void OnLostFocus(object sender, EventArgs e)
    {
        _focused = false;
        Opacity = 0.6;
    }

    private void Render()
    {
        using var perf = Infrastructure.Perf.Time("StatusLineMargin.Render");
        if (_disposed)
        {
            return;
        }

        var line = Infrastructure.VSNeoSettings.Enabled
            ? VSNeo_ExtensionPackage.Session?.State.StatusLine ?? StatusLineSnapshot.Off
            : StatusLineSnapshot.Off;
        _shown = line.LastStatus == 2;
        Visibility = _shown ? Visibility.Visible : Visibility.Collapsed;
        if (!_shown || !_focused)
        {
            return;
        }

        Opacity = 1;
        var fallbackBg = _view.Background as SolidColorBrush;
        Background = line.Bg >= 0 ? BrushFor(line.Bg) : fallbackBg;
        _text.Foreground = line.Fg >= 0 ? BrushFor(line.Fg) : Contrast(fallbackBg);

        var inlines = _text.Inlines;
        inlines.Clear();
        int chars = 0;
        foreach (var seg in line.Segments)
        {
            chars += seg.Text.Length;
            var run = new Run(seg.Text);
            if (seg.Fg >= 0)
            {
                run.Foreground = BrushFor(seg.Fg);
            }

            if (seg.Bg >= 0)
            {
                run.Background = BrushFor(seg.Bg);
            }

            if (seg.Bold)
            {
                run.FontWeight = FontWeights.Bold;
            }

            inlines.Add(run);
        }

        CheckDrawnWidth(chars);
    }

    private static SolidColorBrush BrushFor(int rgb)
    {
        if (!s_brushes.TryGetValue(rgb, out var brush))
        {
            brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            s_brushes[rgb] = brush;
        }

        return brush;
    }

    private static Brush Contrast(SolidColorBrush? background)
    {
        var c = background?.Color ?? Colors.White;
        return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128 ? Brushes.Gainsboro : Brushes.Black;
    }

    public FrameworkElement VisualElement => this;
    public double MarginSize => ActualHeight;
    public bool Enabled => true;

    public ITextViewMargin? GetTextViewMargin(string marginName) =>
        string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_subscribedTo != null)
        {
            _subscribedTo.StatusLineChanged -= OnStatusLineChanged;
        }

        if (Interlocked.Exchange(ref _readyHooked, 0) == 1)
        {
            VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;
        }

        _view.GotAggregateFocus -= OnGotFocus;
        _view.LostAggregateFocus -= OnLostFocus;
        _view.ZoomLevelChanged -= OnZoomLevelChanged;
        Infrastructure.VSNeoSettings.Changed -= Render;
    }
}
