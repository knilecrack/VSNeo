using System;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Shows relative line numbers in the left margin, Vim-style.
    ///
    /// Visual Studio has no built-in relative line numbers. This margin draws the
    /// distance from the caret line for every visible line, and the absolute line
    /// number on the caret line itself, so motions like 5j and 12k are readable at
    /// a glance.
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
        internal IClassificationFormatMapService FormatMapService { get; set; }

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin parent) =>
            new RelativeLineNumberMargin(host.TextView, FormatMapService);
    }

    internal sealed class RelativeLineNumberMargin : FrameworkElement, IWpfTextViewMargin
    {
        public const string MarginName = "VSNeoRelativeLineNumbers";

        private readonly IWpfTextView _view;
        private bool _disposed;
        private Brush _foreground;
        private Typeface _typeface;
        private double _fontSize;

        public RelativeLineNumberMargin(IWpfTextView view, IClassificationFormatMapService formatMapService)
        {
            _view = view;
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

            view.LayoutChanged += OnLayoutChanged;
            view.Caret.PositionChanged += OnCaretPositionChanged;
            view.ZoomLevelChanged += OnZoomLevelChanged;
            view.Closed += OnClosed;
        }

        private void UpdateForeground()
        {
            var solid = _view.Background as SolidColorBrush;
            if (solid == null) return;

            var c = solid.Color;
            bool dark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
            _foreground = dark ? Brushes.Gray : Brushes.DimGray;
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e) => InvalidateVisual();
        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => InvalidateVisual();

        // The width depends on the zoom-scaled font, so a zoom change needs a
        // fresh measure, not just a repaint.
        private void OnZoomLevelChanged(object sender, ZoomLevelChangedEventArgs e)
        {
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            if (_disposed) return;

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
                    string text = lineNumber == caretLine
                        ? (lineNumber + 1).ToString(CultureInfo.InvariantCulture)
                        : Math.Abs(lineNumber - caretLine).ToString(CultureInfo.InvariantCulture);

                    var formatted = new FormattedText(
                        text,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        fontSize,
                        _foreground,
                        VisualTreeHelper.GetDpi(this).PixelsPerDip);

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
                Infrastructure.Log.Write("relative line number render failed", ex);
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
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
        public double MarginSize => ActualWidth;
        public bool Enabled => true;

        public ITextViewMargin GetTextViewMargin(string marginName) =>
            string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

        private void OnClosed(object sender, EventArgs e)
        {
            _disposed = true;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            _view.ZoomLevelChanged -= OnZoomLevelChanged;
            _view.Closed -= OnClosed;
        }

        public void Dispose()
        {
            OnClosed(this, EventArgs.Empty);
        }
    }
}
