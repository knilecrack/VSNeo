using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Draws Vim's block cursor over the character under nvim's cursor while a
    /// visual selection is active.
    ///
    /// This exists because Visual Studio cannot be made to draw it: its own
    /// block caret (overwrite mode) renders nothing when the caret sits at a
    /// selection's exclusive end, which is exactly where a forward selection
    /// parks it - so the last character of a visual selection looked unselected
    /// even though Vim counts it. The selection itself is drawn inclusively by
    /// CursorSynchronizer; this adornment only marks which end is live.
    ///
    /// Driven, not self-updating: CursorSynchronizer already computes the exact
    /// cursor point (path-filtered, fold-snapped) on every state push, so it
    /// calls Show/Hide here. Layout changes (scrolls, edits) only re-anchor the
    /// last point.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class VisualBlockCaretAdornmentProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("VSNeoVisualBlockCaret")]
        [Order(After = PredefinedAdornmentLayers.Text)]
        [TextViewRole(PredefinedTextViewRoles.Document)]
        // MEF populates this through the Export above; nothing in code assigns it.
        internal AdornmentLayerDefinition LayerDefinition = null!;

        [Import]
        internal IClassificationTypeRegistryService ClassificationRegistry = null!;

        [Import]
        internal IClassificationFormatMapService FormatMapService = null!;

        public void TextViewCreated(IWpfTextView textView)
        {
            textView.Properties.GetOrCreateSingletonProperty(
                () => new VisualBlockCaretAdornment(textView, ClassificationRegistry, FormatMapService));
        }
    }

    internal sealed class VisualBlockCaretAdornment
    {
        private const string LayerName = "VSNeoVisualBlockCaret";

        private readonly IAdornmentLayer _layer;
        private readonly IWpfTextView _view;
        private readonly Brush _brush;
        // Null while hidden; the visible state's only record.
        private ITrackingPoint? _anchor;

        public VisualBlockCaretAdornment(
            IWpfTextView view,
            IClassificationTypeRegistryService classificationRegistry,
            IClassificationFormatMapService formatMapService)
        {
            _view = view;
            _layer = view.GetAdornmentLayer(LayerName);
            _brush = BuildBrush(view, classificationRegistry, formatMapService);

            view.LayoutChanged += OnLayoutChanged;
            view.Closed += OnClosed;
        }

        /// <summary>The per-view instance the creation listener made, if any.</summary>
        public static VisualBlockCaretAdornment? For(IWpfTextView view)
        {
            return view.Properties.TryGetProperty(
                typeof(VisualBlockCaretAdornment), out VisualBlockCaretAdornment adornment)
                ? adornment
                : null;
        }

        /// <summary>Reverse-video effect: the text colour as a translucent block.</summary>
        private static Brush BuildBrush(
            IWpfTextView view,
            IClassificationTypeRegistryService classificationRegistry,
            IClassificationFormatMapService formatMapService)
        {
            try
            {
                var formatMap = formatMapService.GetClassificationFormatMap(view);
                var properties = formatMap.GetTextProperties(
                    classificationRegistry.GetClassificationType("plain text"));
                var brush = properties.ForegroundBrush;
                if (brush.CanFreeze) brush.Freeze();
                return brush;
            }
            catch
            {
                return Brushes.White;
            }
        }

        /// <summary>Mark the character at <paramref name="point"/>. UI thread.</summary>
        public void Show(SnapshotPoint point)
        {
            _anchor = point.Snapshot.CreateTrackingPoint(point.Position, PointTrackingMode.Positive);
            Redraw();
        }

        public void Hide()
        {
            _anchor = null;
            _layer.RemoveAllAdornments();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_anchor != null) Redraw();
        }

        private void Redraw()
        {
            _layer.RemoveAllAdornments();
            if (_anchor == null) return;

            try
            {
                var point = _anchor.GetPoint(_view.TextSnapshot);
                var line = point.GetContainingLine();

                // The cursor always sits on a character; an empty line has none
                // to mark.
                if (point.Position >= line.End.Position) return;

                var span = new SnapshotSpan(point, point + 1);
                Geometry geometry;
                try
                {
                    geometry = _view.TextViewLines.GetMarkerGeometry(span);
                }
                catch (Exception)
                {
                    // GetMarkerGeometry throws while the view is mid-layout.
                    return;
                }
                if (geometry == null) return;   // scrolled out of the layout

                var image = new Image
                {
                    Source = new DrawingImage(new GeometryDrawing(_brush, null, geometry)),
                    Width = geometry.Bounds.Width,
                    Height = geometry.Bounds.Height,
                    Opacity = 0.6,
                };

                Canvas.SetLeft(image, geometry.Bounds.Left);
                Canvas.SetTop(image, geometry.Bounds.Top);

                _layer.AddAdornment(
                    AdornmentPositioningBehavior.ViewportRelative, span, null, image, null);
            }
            catch
            {
                // A missing block is recoverable; taking the editor down is not.
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnClosed;
        }
    }
}
