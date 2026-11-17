using System;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

/// <summary>
/// Shows relative line numbers in the left margin, Vim-style: the distance
/// from the caret line on every visible line, and on the caret line itself
/// the absolute number in Hybrid mode or when nvim's 'number' is set, 0 otherwise.
///
/// Visual Studio has no built-in relative line numbers. Tools &gt; Options &gt;
/// VSNeo &gt; Editor selects whether the margin follows nvim's options or
/// overrides them. FollowNeovim uses the companion's vsneo_linenumbers push,
/// so ':set rnu' toggles it live and 'set relativenumber' in ~/.vsneorc
/// enables it permanently.
///
/// The rendering is the VsVim design: each number is shaped into a
/// FormattedText once and cached, and repaints only happen when the caret
/// changes lines or the layout shifts - never per keystroke or per
/// horizontal caret move.
/// </summary>
[Export(typeof(IWpfTextViewMarginProvider))]
[Name(RelativeLineNumberMargin.MarginName)]
[Order(After = PredefinedMarginNames.LineNumber)]
[MarginContainer(PredefinedMarginNames.Left)]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class RelativeLineNumberMarginProvider : IWpfTextViewMarginProvider
{
    [Import]
    internal IClassificationFormatMapService FormatMapService { get; set; } = null!;

    public IWpfTextViewMargin CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin marginContainer) =>
        new RelativeLineNumberMargin(host.TextView, FormatMapService, marginContainer);
}

internal sealed partial class RelativeLineNumberMargin : FrameworkElement, IWpfTextViewMargin
{
    public const string MarginName = "VSNeoRelativeLineNumbers";

    private readonly IWpfTextView _view;
    // The left margin container, kept so the stock line number margin can
    // be hidden while ours draws - two number columns read as a bug.
    private readonly IWpfTextViewMargin? _marginContainer;
    private bool _disposed;
    private bool _active;
    private int _lastCaretLine = -1;

    private Brush _foreground;
    private Color _caretForeground = Colors.Gainsboro;
    private Typeface _typeface;
    private double _fontSize;

    public RelativeLineNumberMargin(
        IWpfTextView view,
        IClassificationFormatMapService formatMapService,
        IWpfTextViewMargin? marginContainer)
    {
        _view = view;
        _marginContainer = marginContainer;
        ClipToBounds = true;

        _foreground = Brushes.Gray;
        _typeface = new Typeface("Consolas");
        _fontSize = 11;

        try
        {
            var map = formatMapService?.GetClassificationFormatMap(view);
            if (map != null)
            {
                _typeface = map.DefaultTextProperties.Typeface;
                _fontSize = map.DefaultTextProperties.FontRenderingEmSize;
            }
        }
        catch
        {
            // Cosmetic only; the defaults are perfectly readable.
        }

        UpdateForeground();

        Subscribe();
        VSNeoSettings.Changed += OnSettingsChanged;

        _active = ComputeActive();
        Visibility = _active ? Visibility.Visible : Visibility.Collapsed;
        if (_active)
        {
            SetNativeMarginVisibility(Visibility.Hidden);
        }

        view.LayoutChanged += OnLayoutChanged;
        view.Caret.PositionChanged += OnCaretPositionChanged;
        view.ZoomLevelChanged += OnZoomLevelChanged;
        view.ViewportHeightChanged += OnViewportHeightChanged;
        _focused = view.HasAggregateFocus;
        view.GotAggregateFocus += OnGotFocus;
        view.LostAggregateFocus += OnLostFocus;
        view.Closed += OnClosed;
    }

    // Set from the focus events, read on the RPC thread. Only the focused
    // view's cursor-line number follows the mode color live; the others
    // post nothing per mode change and repaint on focus gain instead.
    private volatile bool _focused;

    private void OnGotFocus(object sender, EventArgs e)
    {
        _focused = true;
        if (_active)
        {
            InvalidateVisual();
        }
    }

    private void OnLostFocus(object sender, EventArgs e) => _focused = false;

    private static bool ComputeActive()
    {
        // Off with VSNeo: relative numbers only serve Vim counts, and an
        // active margin keeps Visual Studio's own numbers hidden.
        if (!VSNeoSettings.Enabled)
        {
            return false;
        }

        var hub = VSNeo_ExtensionPackage.Session?.State;
        return RelativeLineNumberPolicy.IsActive(
            VSNeoSettings.RelativeLineNumbers,
            hub != null && hub.NvimRelativeNumber);
    }

    private static bool CaretLineIsAbsolute() =>
        RelativeLineNumberPolicy.IsCursorLineAbsolute(
            VSNeoSettings.RelativeLineNumbers,
            VSNeo_ExtensionPackage.Session?.State?.NvimNumber == true);

    private void ApplyActive()
    {
        bool active = ComputeActive();
        if (active == _active)
        {
            return;
        }

        _active = active;
        SetNativeMarginVisibility(active ? Visibility.Hidden : Visibility.Visible);
        Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        InvalidateMeasure();
        InvalidateVisual();
    }

    // Null until Subscribe() finds a live session; checked at every use.
    private NvimStateHub? _subscribedTo;
    private int _readyHooked;

    private void Subscribe()
    {
        // Posted from the ready broadcast: the view can close before the
        // post runs, and subscribing then held it for the whole session.
        if (_disposed)
        {
            return;
        }

        var session = VSNeo_ExtensionPackage.Session;
        if (session == null)
        {
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
            _subscribedTo.LineNumbersChanged -= OnLineNumbersChanged;
            _subscribedTo.ModeChanged -= OnModeChanged;
        }

        session.State.LineNumbersChanged += OnLineNumbersChanged;
        session.State.ModeChanged += OnModeChanged;
        _subscribedTo = session.State;
    }

    private void OnSessionReady(bool ready)
    {
        if (!ready)
        {
            return;
        }
#pragma warning disable VSTHRD001
        _ = Dispatcher?.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                Subscribe();
                ApplyActive();
                InvalidateVisual();
            }));
#pragma warning restore VSTHRD001
    }

    // Raised on the RPC read thread; all the state it touches is WPF.
    private void OnLineNumbersChanged()
    {
#pragma warning disable VSTHRD001
        _ = Dispatcher?.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                ApplyActive();
                InvalidateVisual();
            }));
#pragma warning restore VSTHRD001
    }

    // RPC thread. Only the cursor line's number changes color with the
    // mode, and only while the mode-line tint is on.
    private void OnModeChanged(VimMode mode)
    {
        if (!_active || !_focused
            || VSNeo_ExtensionPackage.Session?.State.ModeLineEnabled != true)
        {
            return;
        }
#pragma warning disable VSTHRD001
        _ = Dispatcher?.BeginInvoke(DispatcherPriority.Input, new Action(InvalidateVisual));
#pragma warning restore VSTHRD001
    }

    // Raised from DialogPage.OnApply, already on the UI thread.
    private void OnSettingsChanged()
    {
        bool wasActive = _active;
        ApplyActive();
        if (wasActive && _active)
        {
            InvalidateVisual();
        }
    }

    // ----------------------------------------------------------------
    // Redraw triggers, deliberately gated: a repaint that nobody can see
    // is the whole cost profile of this margin.
    // ----------------------------------------------------------------

    private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
    {
        // Only a caret *line* change moves the numbers; horizontal motion
        // and insert-mode typing leave every digit where it was.
        int newLine;
        try
        { newLine = e.NewPosition.BufferPosition.GetContainingLine().LineNumber; }
        catch { return; }

        if (newLine == _lastCaretLine)
        {
            return;
        }

        _lastCaretLine = newLine;
        InvalidateVisual();
    }

    private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
    {
        using var perf = Perf.Time("RelativeLineNumberMargin.OnLayoutChanged");
        if (!_active)
        {
            return;
        }

        if (e.VerticalTranslation || e.TranslatedLines.Count > 0)
        {
            InvalidateVisual();
            return;
        }

        if (e.NewOrReformattedLines.Count == 0)
        {
            return;
        }

        // Typing reformats the line being typed on at every keystroke, and
        // that alone used to repaint the whole margin - ~50 DrawText calls
        // per character in insert mode for digits that did not change.
        // A reformat changes what this margin shows only if it changes
        // which lines are on screen or where they sit: same first line,
        // same line count, same caret line and same geometry means the
        // numbers drawn last time are still exactly right.
        if (RenderSignature() != _renderedSignature)
        {
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!_active)
        {
            return new Size(0, 0);
        }

        // Width needs to fit the largest absolute line number.
        int lineCount = _view.TextSnapshot?.LineCount ?? 0;
        int digits = Math.Max(2, lineCount.ToString(CultureInfo.InvariantCulture).Length);

        var probe = new FormattedText(
            new string('0', digits),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _fontSize * Math.Max(0.01, _view.ZoomLevel / 100.0),
            _foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        double height = double.IsInfinity(availableSize.Height) || double.IsNaN(availableSize.Height)
            ? 0
            : availableSize.Height;

        return new Size(probe.Width + 8, height);
    }

    public FrameworkElement VisualElement => this;
    public double MarginSize => _active ? ActualWidth : 0;
    public bool Enabled => _active;

    public ITextViewMargin? GetTextViewMargin(string marginName) =>
        string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

    private void OnClosed(object sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _view.LayoutChanged -= OnLayoutChanged;
        _view.Caret.PositionChanged -= OnCaretPositionChanged;
        _view.ZoomLevelChanged -= OnZoomLevelChanged;
        _view.GotAggregateFocus -= OnGotFocus;
        _view.LostAggregateFocus -= OnLostFocus;
        _view.ViewportHeightChanged -= OnViewportHeightChanged;
        _view.Closed -= OnClosed;

        VSNeoSettings.Changed -= OnSettingsChanged;
        if (_readyHooked == 1)
        {
            VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;
        }

        if (_subscribedTo != null)
        {
            _subscribedTo.LineNumbersChanged -= OnLineNumbersChanged;
            _subscribedTo.ModeChanged -= OnModeChanged;
        }

        // Hand the stock margin its space back on the way out.
        SetNativeMarginVisibility(Visibility.Visible);
    }

    public void Dispose()
    {
        OnClosed(this, EventArgs.Empty);
    }
}
