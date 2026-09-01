using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Flashes the text a yank just grabbed - LazyVim's "highlight on yank",
    /// drawn by Visual Studio instead of nvim.
    ///
    /// nvim's TextYankPost reports the region; this margin shows it for a few
    /// hundred milliseconds. The flash is the confirmation that y did what you
    /// meant, which is otherwise invisible in an editor that renders no
    /// operator feedback of its own.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class YankFlashAdornmentProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("VSNeoYankFlash")]
        [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
        [TextViewRole(PredefinedTextViewRoles.Document)]
        // MEF populates this through the Export above; nothing in code assigns it.
        internal AdornmentLayerDefinition LayerDefinition = null!;

        public void TextViewCreated(IWpfTextView textView)
        {
            textView.Properties.GetOrCreateSingletonProperty(
                () => new YankFlashAdornment(textView));
        }
    }

    internal sealed class YankFlashAdornment
    {
        private const string LayerName = "VSNeoYankFlash";

        // LazyVim flashes for ~150-200ms; slightly longer reads better against
        // Visual Studio's busier background.
        private static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(250);

        private readonly IAdornmentLayer _layer;
        private readonly IWpfTextView _view;
        private readonly DispatcherTimer _timer;
        // Assigned by BuildBrush(), which the constructor calls; the compiler
        // cannot see through the method call.
        private Brush _brush = null!;
        private bool _disposed;

        public YankFlashAdornment(IWpfTextView view)
        {
            _view = view;
            _layer = view.GetAdornmentLayer(LayerName);

            _timer = new DispatcherTimer(DispatcherPriority.Input, _view.VisualElement.Dispatcher)
            {
                Interval = FlashDuration,
            };
            _timer.Tick += OnTimerTick;

            BuildBrush();
            Subscribe();

            view.Closed += OnClosed;
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
            session.State.YankFlashed += OnYankFlashed;
            session.State.HighlightsChanged += OnHighlightsChanged;
            _subscribedTo = session.State;
        }

        private void OnSessionReady(bool ready)
        {
            if (!ready) return;
            var dispatcher = _view.VisualElement.Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            _ = dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                BuildBrush();
                Subscribe();
            }));
#pragma warning restore VSTHRD001
        }

        /// <summary>IncSearch's background, like vim.hl.on_yank's default higroup.</summary>
        private void BuildBrush()
        {
            var state = VSNeo_ExtensionPackage.Session?.State;
            int rgb = state == null ? -1 : state.YankColor;

            _brush = new SolidColorBrush(rgb >= 0
                ? Color.FromArgb(0xB0, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
                : Color.FromArgb(0x60, 0xFF, 0x9E, 0x40));
            _brush.Freeze();
        }

        private void OnHighlightsChanged() => BuildBrush();

        private void OnYankFlashed(IReadOnlyList<SearchMatch> segments)
        {
            var dispatcher = _view.VisualElement.Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            _ = dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Flash(segments)));
#pragma warning restore VSTHRD001
        }

        private void Flash(IReadOnlyList<SearchMatch> segments)
        {
            if (_disposed) return;

            try
            {
                _layer.RemoveAllAdornments();

                var snapshot = _view.TextSnapshot;
                if (snapshot != null)
                {
                    foreach (var segment in segments)
                    {
                        if (segment.Line >= snapshot.LineCount) continue;

                        var line = snapshot.GetLineFromLineNumber(segment.Line);

                        int startCol = ColumnMapper.ByteToChar(line, segment.StartByte);
                        int endCol = segment.EndByte > line.Length
                            ? line.Length
                            : ColumnMapper.ByteToChar(line, segment.EndByte);
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

                // Restart rather than extend: a second yank replaces the first flash.
                _timer.Stop();
                _timer.Start();
            }
            catch (Exception ex)
            {
                // An adornment must never take the editor down with it.
                Log.Write("yank flash failed", ex);
            }
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            _timer.Stop();
            _layer.RemoveAllAdornments();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_disposed) return;
            _disposed = true;

            _timer.Stop();

            if (_subscribedTo != null)
            {
                _subscribedTo.YankFlashed -= OnYankFlashed;
                _subscribedTo.HighlightsChanged -= OnHighlightsChanged;
            }
            if (Interlocked.Exchange(ref _readyHooked, 0) == 1)
                VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;

            _view.Closed -= OnClosed;
        }
    }
}
