using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Draws overlay labels pushed by Lua: flash-style jump letters, and
    /// anything else an overlay interaction wants to float over the text.
    ///
    /// This is deliberately a renderer and nothing more. The behavior -
    /// collecting a pattern, choosing matches, assigning labels - lives in
    /// the companion (see vsneo.jump in vsneo.lua), because nvim is the
    /// brain and because users can then rebind or replace it from ~/.vsneorc.
    /// nvim 0.12 has no UI event that reports extmarks, so plugins like
    /// flash.nvim cannot draw here themselves; this layer is what Lua gets
    /// instead.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class OverlayLabelsAdornmentProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("VSNeoOverlayLabels")]
        [Order(After = PredefinedAdornmentLayers.Text)]
        [TextViewRole(PredefinedTextViewRoles.Document)]
        // MEF populates this through the Export above; nothing in code assigns it.
        internal AdornmentLayerDefinition LayerDefinition = null!;

        public void TextViewCreated(IWpfTextView textView)
        {
            textView.Properties.GetOrCreateSingletonProperty(
                () => new OverlayLabelsAdornment(textView));
        }
    }

    internal sealed class OverlayLabelsAdornment
    {
        private const string LayerName = "VSNeoOverlayLabels";

        private readonly IAdornmentLayer _layer;
        private readonly IWpfTextView _view;
        // Assigned by BuildBrushes(), which the constructor calls; the compiler
        // cannot see through the method call.
        private Brush _labelBrush = null!;
        private Brush _matchBrush = null!;
        private bool _disposed;

        // Hub events reach every open view on the RPC read thread; only the
        // focused one draws labels. Tracked so unfocused views bail before
        // paying a dispatcher hop per overlay update. volatile: read on the
        // RPC thread.
        private volatile bool _focused;

        public OverlayLabelsAdornment(IWpfTextView view)
        {
            _view = view;
            _layer = view.GetAdornmentLayer(LayerName);
            _focused = view.HasAggregateFocus;

            BuildBrushes();
            Subscribe();
            view.GotAggregateFocus += OnGotFocus;
            view.LostAggregateFocus += OnLostFocus;
            view.Closed += OnClosed;
        }

        /// <summary>
        /// Same sources as the search adornment: the label takes CurSearch /
        /// IncSearch, so ':hi CurSearch guibg=...' in ~/.vsneorc themes it.
        /// </summary>
        private void BuildBrushes()
        {
            var state = VSNeo_ExtensionPackage.Session?.State;
            int rgb = state == null ? -1 : state.CurrentMatchColor;
            _labelBrush = Frozen(rgb >= 0
                ? Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
                : Color.FromArgb(0xFF, 0xFF, 0x9E, 0x40));
            _matchBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xD7, 0x00));
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        // Null until Subscribe() finds a live session; checked at every use.
        private NvimStateHub? _subscribedTo;
        private int _readyHooked;

        private void Subscribe()
        {
            var session = VSNeo_ExtensionPackage.Session;
            if (session == null)
            {
                // Created with the startup document, before the package loaded:
                // wait for the ready broadcast rather than staying deaf.
                if (Interlocked.Exchange(ref _readyHooked, 1) == 0)
                    VSNeo_ExtensionPackage.SessionReadyChanged += OnSessionReady;
                return;
            }
            if (ReferenceEquals(_subscribedTo, session.State)) return;
            session.State.OverlayLabelsChanged += OnLabelsChanged;
            session.State.HighlightsChanged += OnHighlightsChanged;
            _subscribedTo = session.State;
        }

        private void OnSessionReady(bool ready)
        {
            if (!ready) return;
            var dispatcher = _view.VisualElement.Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() =>
                {
                    BuildBrushes();
                    Subscribe();
                    Redraw();
                }));
#pragma warning restore VSTHRD001
        }

        private void OnLabelsChanged() => BeginRedraw();

        private void OnHighlightsChanged()
        {
            BuildBrushes();
            BeginRedraw();
        }

        // Runs on the UI thread (view focus events). Focus loss clears the
        // labels; focus gain draws the current set, if an overlay is active.
        // The flag is set from the event itself, not re-read from
        // HasAggregateFocus: that property is not reliable inside the events,
        // and a stale false there silenced the labels for good.
        private void OnGotFocus(object sender, EventArgs e)
        {
            _focused = true;
            Redraw();
        }

        private void OnLostFocus(object sender, EventArgs e)
        {
            _focused = false;
            Redraw();
        }

        private void BeginRedraw()
        {
            // Unfocused views draw nothing; the focus handler repaints on the
            // way back in.
            if (!_focused) return;

            var dispatcher = _view.VisualElement.Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(Redraw));
#pragma warning restore VSTHRD001
        }

        private void Redraw()
        {
            if (_disposed) return;

            try
            {
                _layer.RemoveAllAdornments();

                var session = VSNeo_ExtensionPackage.Session;
                var labels = session?.State.OverlayLabels;
                if (labels == null || labels.Count == 0 || !_view.HasAggregateFocus) return;

                var snapshot = _view.TextSnapshot;
                if (snapshot == null || snapshot.LineCount == 0) return;

                var lines = _view.TextViewLines;
                if (lines == null || lines.Count == 0) return;

                double fontSize;
                try { fontSize = _view.FormattedLineSource?.DefaultTextProperties.FontHintingEmSize ?? 13; }
                catch { fontSize = 13; }

                foreach (var label in labels)
                {
                    if (label.Line < 0 || label.Line >= snapshot.LineCount) continue;

                    var line = snapshot.GetLineFromLineNumber(label.Line);

                    int startCol = ColumnMapper.ByteToChar(line, label.StartByte);
                    int endCol = ColumnMapper.ByteToChar(line, label.EndByte);
                    if (startCol > line.Length) startCol = line.Length;
                    if (endCol > line.Length) endCol = line.Length;
                    if (endCol <= startCol) endCol = Math.Min(startCol + 1, line.Length);
                    if (startCol >= line.Length && line.Length > 0) startCol = line.Length - 1;
                    if (endCol <= startCol) continue;

                    var span = new SnapshotSpan(line.Start + startCol, line.Start + endCol);

                    Geometry geometry;
                    try { geometry = lines.GetMarkerGeometry(span); }
                    catch (Exception)
                    {
                        // GetMarkerGeometry throws while the view is mid-layout.
                        continue;
                    }
                    if (geometry == null) continue;

                    var image = new Image
                    {
                        Source = new DrawingImage(new GeometryDrawing(_matchBrush, null, geometry)),
                        Width = geometry.Bounds.Width,
                        Height = geometry.Bounds.Height,
                    };
                    Canvas.SetLeft(image, geometry.Bounds.Left);
                    Canvas.SetTop(image, geometry.Bounds.Top);
                    _layer.AddAdornment(
                        AdornmentPositioningBehavior.ViewportRelative, span, null, image, null);

                    if (string.IsNullOrEmpty(label.Text)) continue;

                    var box = new Border
                    {
                        Background = _labelBrush,
                        CornerRadius = new CornerRadius(2),
                        Padding = new Thickness(1, 0, 1, 0),
                        Child = new TextBlock
                        {
                            Text = label.Text,
                            Foreground = Brushes.Black,
                            FontWeight = FontWeights.Bold,
                            FontSize = fontSize,
                        },
                    };
                    Canvas.SetLeft(box, geometry.Bounds.Left);
                    Canvas.SetTop(box, geometry.Bounds.Top);
                    _layer.AddAdornment(
                        AdornmentPositioningBehavior.ViewportRelative, span, null, box, null);
                }
            }
            catch (Exception ex)
            {
                // An adornment must never take the editor down with it.
                Infrastructure.Log.Write("overlay label redraw failed", ex);
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_disposed) return;
            _disposed = true;

            if (_subscribedTo != null)
            {
                _subscribedTo.OverlayLabelsChanged -= OnLabelsChanged;
                _subscribedTo.HighlightsChanged -= OnHighlightsChanged;
            }
            if (Interlocked.Exchange(ref _readyHooked, 0) == 1)
                VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;

            _view.GotAggregateFocus -= OnGotFocus;
            _view.LostAggregateFocus -= OnLostFocus;
            _view.Closed -= OnClosed;
        }
    }
}
