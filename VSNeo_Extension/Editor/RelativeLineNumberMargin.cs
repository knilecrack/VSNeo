using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Shows relative line numbers in the left margin, Vim-style: the distance
    /// from the caret line on every visible line, and on the caret line itself
    /// the absolute number when nvim's 'number' is set, 0 otherwise.
    ///
    /// Visual Studio has no built-in relative line numbers. The margin follows
    /// nvim's 'relativenumber' option (pushed by the companion as
    /// vsneo_linenumbers, so ':set rnu' toggles it live and 'set
    /// relativenumber' in ~/.vsneorc enables it permanently), overridden by
    /// Tools &gt; Options &gt; VSNeo &gt; General.
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

    internal sealed class RelativeLineNumberMargin : FrameworkElement, IWpfTextViewMargin
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
        private Typeface _typeface;
        private double _fontSize;

        // One shaped glyph run per displayed number, cleared when the font or
        // zoom changes. The per-repaint cost of this margin is a dictionary
        // lookup and a DrawText per visible line - the FormattedText
        // construction that made the first version expensive happens once per
        // distinct number per font configuration.
        private readonly Dictionary<int, FormattedText> _glyphs = new Dictionary<int, FormattedText>();

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
            if (_active) SetNativeMarginVisibility(Visibility.Hidden);

            view.LayoutChanged += OnLayoutChanged;
            view.Caret.PositionChanged += OnCaretPositionChanged;
            view.ZoomLevelChanged += OnZoomLevelChanged;
            view.ViewportHeightChanged += OnViewportHeightChanged;
            view.Closed += OnClosed;
        }

        private static bool ComputeActive()
        {
            switch (VSNeoSettings.RelativeLineNumbers)
            {
                case RelativeLineNumbersMode.AlwaysOn: return true;
                case RelativeLineNumbersMode.AlwaysOff: return false;
                default:
                    var hub = VSNeo_ExtensionPackage.Session?.State;
                    return hub != null && hub.NvimRelativeNumber;
            }
        }

        /// <summary>The caret line's number follows 'number' like Vim's does.</summary>
        private static bool NvimNumberOn() =>
            VSNeo_ExtensionPackage.Session?.State?.NvimNumber == true;

        private void ApplyActive()
        {
            bool active = ComputeActive();
            if (active == _active) return;

            _active = active;
            SetNativeMarginVisibility(active ? Visibility.Hidden : Visibility.Visible);
            Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            InvalidateMeasure();
            InvalidateVisual();
        }

        // VsVim's trick: zero the stock margin's width before hiding it, and
        // put the numbers back exactly when ours steps aside. Without this a
        // user with VS line numbers on gets two margin columns.
        private double _nativeWidth;
        private double _nativeMinWidth;
        private double _nativeMaxWidth = double.PositiveInfinity;
        private bool _nativeHidden;

        private void SetNativeMarginVisibility(Visibility visibility)
        {
            try
            {
                var element = (_marginContainer?.GetTextViewMargin(PredefinedMarginNames.LineNumber)
                    as IWpfTextViewMargin)?.VisualElement;
                if (element == null) return;

                if (visibility == Visibility.Hidden && element.Visibility == Visibility.Visible)
                {
                    _nativeWidth = element.Width;
                    _nativeMinWidth = element.MinWidth;
                    _nativeMaxWidth = element.MaxWidth;
                    element.Width = 0.0;
                    element.MinWidth = 0.0;
                    element.MaxWidth = 0.0;
                    element.Visibility = Visibility.Hidden;
                    _nativeHidden = true;
                }
                else if (visibility == Visibility.Visible && _nativeHidden)
                {
                    element.Width = _nativeWidth;
                    element.MinWidth = _nativeMinWidth;
                    element.MaxWidth = _nativeMaxWidth;
                    element.Visibility = Visibility.Visible;
                    _nativeHidden = false;
                }
            }
            catch (Exception ex)
            {
                // Losing the toggle is cosmetic; never take the editor down over it.
                Log.Write("native line number margin toggle failed", ex);
            }
        }

        private void UpdateForeground()
        {
            var solid = _view.Background as SolidColorBrush;
            if (solid == null) return;

            var c = solid.Color;
            bool dark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
            _foreground = dark ? Brushes.Gray : Brushes.DimGray;
        }

        // ----------------------------------------------------------------
        // Session/settings wiring, same pattern as SearchHighlightAdornment:
        // the view can be created before the package finishes loading.
        // ----------------------------------------------------------------

        // Null until Subscribe() finds a live session; checked at every use.
        private NvimStateHub? _subscribedTo;
        private int _readyHooked;

        private void Subscribe()
        {
            var session = VSNeo_ExtensionPackage.Session;
            if (session == null)
            {
                if (Interlocked.Exchange(ref _readyHooked, 1) == 0)
                    VSNeo_ExtensionPackage.SessionReadyChanged += OnSessionReady;
                return;
            }
            if (ReferenceEquals(_subscribedTo, session.State)) return;
            session.State.LineNumbersChanged += OnLineNumbersChanged;
            _subscribedTo = session.State;
        }

        private void OnSessionReady(bool ready)
        {
            if (!ready) return;
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

        // Raised from DialogPage.OnApply, already on the UI thread.
        private void OnSettingsChanged() => ApplyActive();

        // ----------------------------------------------------------------
        // Redraw triggers, deliberately gated: a repaint that nobody can see
        // is the whole cost profile of this margin.
        // ----------------------------------------------------------------

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            // Only a caret *line* change moves the numbers; horizontal motion
            // and insert-mode typing leave every digit where it was.
            int newLine;
            try { newLine = e.NewPosition.BufferPosition.GetContainingLine().LineNumber; }
            catch { return; }
            if (newLine == _lastCaretLine) return;

            _lastCaretLine = newLine;
            InvalidateVisual();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (!_active) return;
            if (e.NewOrReformattedLines.Count > 0
                || e.TranslatedLines.Count > 0
                || e.VerticalTranslation)
                InvalidateVisual();
        }

        private void OnViewportHeightChanged(object sender, EventArgs e)
        {
            if (_active) InvalidateVisual();
        }

        // The cache holds zoom-scaled glyphs, so a zoom change needs a fresh
        // shaping pass and a fresh measure, not just a repaint.
        private void OnZoomLevelChanged(object sender, ZoomLevelChangedEventArgs e)
        {
            _glyphs.Clear();
            InvalidateMeasure();
            InvalidateVisual();
        }

        private FormattedText GlyphFor(int number, double fontSize)
        {
            if (_glyphs.TryGetValue(number, out var cached)) return cached;

            var formatted = new FormattedText(
                number.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typeface,
                fontSize,
                _foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            _glyphs[number] = formatted;
            return formatted;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            if (_disposed || !_active) return;

            try
            {
                var lines = _view.TextViewLines;
                if (lines == null || lines.Count == 0) return;

                var snapshot = _view.TextSnapshot;
                if (snapshot == null) return;

                // The caret can be at an invalid position while the view is being
                // laid out or closed; GetContainingLine throws in that state.
                var caretPosition = _view.Caret.Position.BufferPosition;
                if (caretPosition.Snapshot != snapshot) return;

                int caretLine = caretPosition.GetContainingLine().LineNumber;
                _lastCaretLine = caretLine;
                bool caretAbsolute = NvimNumberOn();

                // line.Top is in text-view coordinates, which scroll with the
                // content; the margin's origin is the viewport's top. Without
                // this subtraction every number is drawn its full scroll offset
                // too low, ClipToBounds cuts the lot, and the margin appears to
                // render only the first screenful of the file.
                double viewportTop = _view.ViewportTop;

                // Editor zoom scales the text surface but not this margin, and the
                // line coordinates stay unzoomed. At 145% the numbers came out at
                // 1/1.45 of the real line pitch, drifting rows away from their code
                // the further they sat from the first visible line. Positions and
                // glyphs both have to be scaled by the zoom factor to track the text.
                double zoom = Math.Max(0.01, _view.ZoomLevel / 100.0);
                double fontSize = _fontSize * zoom;

                foreach (var line in lines)
                {
                    if (line.VisibilityState != VisibilityState.FullyVisible
                        && line.VisibilityState != VisibilityState.PartiallyVisible)
                        continue;

                    int lineNumber = line.Start.GetContainingLine().LineNumber;
                    int number = lineNumber == caretLine
                        ? (caretAbsolute ? lineNumber + 1 : 0)
                        : Math.Abs(lineNumber - caretLine);

                    var formatted = GlyphFor(number, fontSize);

                    // Centered on the text, not the line: a line carrying a CodeLens
                    // header is taller than its text, and Top/Height span the lot, so
                    // centering on them floats the number up into the lens row.
                    double y = (line.TextTop - viewportTop) * zoom
                               + (line.TextHeight * zoom - formatted.Height) / 2;
                    if (y + formatted.Height < 0 || y > ActualHeight) continue;

                    double x = Math.Max(0, ActualWidth - formatted.Width - 4);
                    dc.DrawText(formatted, new Point(x, y));
                }
            }
            catch (Exception ex)
            {
                // A margin must never take the editor down with it.
                Log.Write("relative line number render failed", ex);
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (!_active) return new Size(0, 0);

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
            if (_disposed) return;
            _disposed = true;

            _view.LayoutChanged -= OnLayoutChanged;
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            _view.ZoomLevelChanged -= OnZoomLevelChanged;
            _view.ViewportHeightChanged -= OnViewportHeightChanged;
            _view.Closed -= OnClosed;

            VSNeoSettings.Changed -= OnSettingsChanged;
            if (_readyHooked == 1)
                VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;
            if (_subscribedTo != null)
                _subscribedTo.LineNumbersChanged -= OnLineNumbersChanged;

            // Hand the stock margin its space back on the way out.
            SetNativeMarginVisibility(Visibility.Visible);
        }

        public void Dispose()
        {
            OnClosed(this, EventArgs.Empty);
        }
    }
}
