using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Infrastructure;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Flashes what u / Ctrl+R just changed - highlight-undo.nvim, drawn by
    /// Visual Studio. An undo that lands off screen scrolls there and changes
    /// text you have not looked at yet; the flash shows what came back (or a
    /// thin bar where text went) so the change is seen, not guessed.
    ///
    /// The key processor runs undo itself (Visual Studio's history is the
    /// authoritative one), captures the changed spans from the buffer's Changed
    /// events during that call and hands them here as tracking spans. They are
    /// drawn per laid-out line, now and again on each LayoutChanged while the
    /// flash lasts: the undo moves the caret and the view scrolls after this
    /// call, so a one-shot draw would paint the old screen.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class UndoFlashAdornmentProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(UndoFlashAdornment.LayerName)]
        [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
        [TextViewRole(PredefinedTextViewRoles.Document)]
        // MEF populates this through the Export above; nothing in code assigns it.
        internal AdornmentLayerDefinition LayerDefinition = null!;

        // Bookkeeping only (see TextViewCreationListener): the layer and the
        // timer are created on the first flash.
        public void TextViewCreated(IWpfTextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(() => new UndoFlashAdornment(textView));
    }

    internal sealed class UndoFlashAdornment
    {
        internal const string LayerName = "VSNeoUndoFlash";

        // Solid for a moment, then a fade: long enough to find after a jump,
        // short enough not to linger over the next edit.
        private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(250);

        // A whole-file undo (a reformat) is hundreds of spans; past this the
        // flash would cost more than it tells.
        private const int MaxSpans = 400;

        private readonly IWpfTextView _view;
        private IAdornmentLayer? _layer;
        private DispatcherTimer? _timer;
        private IReadOnlyList<ITrackingSpan>? _pending;
        private DateTime _startedAt;
        private Brush? _brush;
        private int _brushRgb = int.MinValue;
        private bool _closed;

        public UndoFlashAdornment(IWpfTextView view)
        {
            _view = view;
            view.LayoutChanged += OnLayoutChanged;
            view.Closed += OnClosed;
        }

        public static UndoFlashAdornment? For(ITextView view) =>
            view.Properties.TryGetProperty(typeof(UndoFlashAdornment), out UndoFlashAdornment flash)
                ? flash
                : null;

        /// <summary>
        /// UI thread, straight after the undo. <paramref name="changed"/> are the
        /// undo's new spans, tracking into later snapshots.
        /// </summary>
        public void Flash(IReadOnlyList<ITrackingSpan> changed)
        {
            if (_closed || changed.Count == 0) return;

            var state = VSNeo_ExtensionPackage.Session?.State;
            if (state == null || !state.UndoFlashEnabled) return;

            try
            {
                _layer ??= _view.GetAdornmentLayer(LayerName);
                EnsureBrush(state.YankColor);

                _pending = changed.Count > MaxSpans ? Trim(changed) : changed;
                _startedAt = DateTime.UtcNow;
                _layer.RemoveAllAdornments();
                Draw(_view.TextViewLines);

                if (_timer == null)
                {
                    _timer = new DispatcherTimer(DispatcherPriority.Input, _view.VisualElement.Dispatcher);
                    _timer.Tick += (s, e) => Clear();
                }
                // Restart rather than extend: a second undo replaces the first flash.
                _timer.Stop();
                _timer.Interval = HoldTime + FadeTime;
                _timer.Start();
            }
            catch (Exception ex)
            {
                // An adornment must never take the editor down with it.
                Log.Write("undo flash failed", ex);
                Clear();
            }
        }

        private static IReadOnlyList<ITrackingSpan> Trim(IReadOnlyList<ITrackingSpan> spans)
        {
            var kept = new List<ITrackingSpan>(MaxSpans);
            for (int i = 0; i < MaxSpans; i++) kept.Add(spans[i]);
            return kept;
        }

        // The view re-lays out lines as it scrolls to the undo; each newly
        // formatted line gets its share of the flash.
        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_pending == null || _layer == null) return;
            try
            {
                Draw(e.NewOrReformattedLines);
            }
            catch (Exception ex)
            {
                Log.Write("undo flash failed", ex);
                Clear();
            }
        }

        private void Draw(IEnumerable<ITextViewLine>? lines)
        {
            var pending = _pending;
            var layer = _layer;
            if (pending == null || layer == null || lines == null) return;

            var snapshot = _view.TextSnapshot;
            foreach (var line in lines)
            {
                if (line.Snapshot != snapshot) continue;
                foreach (var tracking in pending)
                {
                    var span = tracking.GetSpan(snapshot);
                    UIElement? element;
                    SnapshotSpan anchor;

                    if (span.IsEmpty)
                    {
                        // A pure deletion: nothing left to highlight, so a thin
                        // bar marks where the text was.
                        if (span.Start < line.Start || span.Start > line.End) continue;
                        if (span.Start == line.End && line.LineBreakLength == 0 && span.Start != snapshot.Length) continue;
                        double x = span.Start < line.End ? line.GetCharacterBounds(span.Start).Left : line.TextRight;
                        element = new Rectangle
                        {
                            Width = 2,
                            Height = line.TextHeight,
                            Fill = _brush,
                        };
                        Canvas.SetLeft(element, x - 1);
                        Canvas.SetTop(element, line.TextTop);
                        anchor = new SnapshotSpan(span.Start, 0);
                    }
                    else
                    {
                        // The part on this line, line break excluded: a restored
                        // line break has no width to show.
                        var part = span.Intersection(line.Extent);
                        if (part == null || part.Value.IsEmpty) continue;

                        var geometry = _view.TextViewLines.GetMarkerGeometry(part.Value);
                        if (geometry == null) continue;
                        element = new Image
                        {
                            Source = new DrawingImage(new GeometryDrawing(_brush, null, geometry)),
                            Width = geometry.Bounds.Width,
                            Height = geometry.Bounds.Height,
                        };
                        Canvas.SetLeft(element, geometry.Bounds.Left);
                        Canvas.SetTop(element, geometry.Bounds.Top);
                        anchor = part.Value;
                    }

                    FadeOut(element);
                    // TextRelative: the flash moves with its text as the view
                    // scrolls, and goes when its line is re-laid out (which
                    // redraws it here).
                    layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, anchor, null, element, null);
                }
            }
        }

        /// <summary>
        /// Fades with the rest of this flash: an element drawn late (a line
        /// that scrolled in) joins the fade where it already is. Without
        /// Windows animations it simply disappears when the timer ends.
        /// </summary>
        private void FadeOut(UIElement element)
        {
            if (!SystemParameters.ClientAreaAnimation) return;

            var elapsed = DateTime.UtcNow - _startedAt;
            var begin = HoldTime - elapsed;
            if (begin < TimeSpan.Zero) begin = TimeSpan.Zero;
            var remaining = HoldTime + FadeTime - elapsed - begin;
            if (remaining <= TimeSpan.Zero) { element.Opacity = 0; return; }

            var fade = new DoubleAnimation(1, 0, new Duration(remaining)) { BeginTime = begin };
            element.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        /// <summary>IncSearch's background, like the yank flash; highlight-undo.nvim's look.</summary>
        private void EnsureBrush(int rgb)
        {
            if (_brush != null && rgb == _brushRgb) return;
            _brushRgb = rgb;
            var brush = new SolidColorBrush(rgb >= 0
                ? Color.FromArgb(0xB0, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
                : Color.FromArgb(0x60, 0xFF, 0x9E, 0x40));
            brush.Freeze();
            _brush = brush;
        }

        private void Clear()
        {
            _timer?.Stop();
            _pending = null;
            _layer?.RemoveAllAdornments();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_closed) return;
            _closed = true;
            _timer?.Stop();
            _pending = null;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnClosed;
        }
    }
}
