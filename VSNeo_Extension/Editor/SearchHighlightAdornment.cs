using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Infrastructure;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Draws Vim's hlsearch matches behind the text.
    ///
    /// nvim computes the matches and sends their positions; this margin turns them
    /// into background rectangles in the Visual Studio editor. It deliberately does
    /// not reimplement Vim's regex engine in C#.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class SearchHighlightAdornmentProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("VSNeoSearchHighlight")]
        [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
        [TextViewRole(PredefinedTextViewRoles.Document)]
#pragma warning disable CS0649 // Field is never assigned to; MEF populates it.
        internal AdornmentLayerDefinition LayerDefinition;
#pragma warning restore CS0649

        public void TextViewCreated(IWpfTextView textView)
        {
            textView.Properties.GetOrCreateSingletonProperty(
                () => new SearchHighlightAdornment(textView));
        }
    }

    internal sealed class SearchHighlightAdornment
    {
        private const string LayerName = "VSNeoSearchHighlight";

        private readonly IAdornmentLayer _layer;
        private readonly IWpfTextView _view;
        private readonly Brush _brush;
        private bool _disposed;

        public SearchHighlightAdornment(IWpfTextView view)
        {
            _view = view;
            _layer = view.GetAdornmentLayer(LayerName);

            // Semi-transparent yellow, close to Vim's default Search highlight.
            _brush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xD7, 0x00));
            _brush.Freeze();

            Subscribe();
            view.LayoutChanged += OnLayoutChanged;
            view.Closed += OnClosed;
        }

        private void Subscribe()
        {
            var session = VSNeo_ExtensionPackage.Session;
            if (session == null) return;
            session.State.SearchMatchesChanged += OnMatchesChanged;
        }

        private void OnMatchesChanged()
        {
            var dispatcher = _view.VisualElement.Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(Redraw));
#pragma warning restore VSTHRD001
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => Redraw();

        private void Redraw()
        {
            if (_disposed) return;

            try
            {
                _layer.RemoveAllAdornments();

                var session = VSNeo_ExtensionPackage.Session;
                var matches = session?.State.SearchMatches;
                if (matches == null || matches.Count == 0 || !_view.HasAggregateFocus) return;

                var snapshot = _view.TextSnapshot;
                if (snapshot == null) return;

                var lines = _view.TextViewLines;
                if (lines == null || lines.Count == 0) return;

                int firstVisible = lines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
                int lastVisible = lines.LastVisibleLine.End.GetContainingLine().LineNumber;

                foreach (var match in matches)
                {
                    if (match.Line < firstVisible || match.Line > lastVisible) continue;
                    if (match.Line >= snapshot.LineCount) continue;

                    var line = snapshot.GetLineFromLineNumber(match.Line);
                    string lineText = line.GetText();

                    int startCol = ColumnMapper.ByteToChar(lineText, match.StartByte);
                    int endCol = ColumnMapper.ByteToChar(lineText, match.EndByte);

                    if (startCol > line.Length) startCol = line.Length;
                    if (endCol > line.Length) endCol = line.Length;
                    if (endCol < startCol) endCol = startCol;

                    var span = new SnapshotSpan(line.Start + startCol, line.Start + endCol);

                    Geometry geometry;
                    try
                    {
                        geometry = _view.TextViewLines.GetMarkerGeometry(span);
                    }
                    catch (Exception)
                    {
                        // GetMarkerGeometry throws while the view is mid-layout.
                        continue;
                    }

                    if (geometry == null) continue;

                    var image = new Image
                    {
                        Source = new DrawingImage(new GeometryDrawing(_brush, null, geometry)),
                        Width = geometry.Bounds.Width,
                        Height = geometry.Bounds.Height,
                    };

                    Canvas.SetLeft(image, geometry.Bounds.Left);
                    Canvas.SetTop(image, geometry.Bounds.Top);

                    _layer.AddAdornment(
                        AdornmentPositioningBehavior.ViewportRelative, span, null, image, null);
                }
            }
            catch (Exception ex)
            {
                // An adornment must never take the editor down with it.
                Infrastructure.Log.Write("search highlight redraw failed", ex);
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_disposed) return;
            _disposed = true;

            var session = VSNeo_ExtensionPackage.Session;
            if (session != null)
                session.State.SearchMatchesChanged -= OnMatchesChanged;

            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnClosed;
        }
    }
}
