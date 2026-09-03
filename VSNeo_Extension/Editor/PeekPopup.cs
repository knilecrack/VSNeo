using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// The peek popup: pressing " in normal or visual mode pops nvim's
    /// registers, and ` or ' pops the mark list - one-line previews next to
    /// the caret, so the next key is a choice rather than a guess.
    ///
    /// Purely informational - it never takes focus and never sees a key. The
    /// key path's only involvement is firing the fetch. Register dismissal
    /// rides ShowCmdChanged (nvim clears showcmd when the pending " resolves);
    /// a pending ` or ' is not reliable in showcmd, so the marks list is
    /// dismissed by the next key the processor sends and guarded by age
    /// instead. Either way, every key's behaviour stays exactly where it was.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class PeekPopupProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("VSNeoPeekPopup")]
        [Order(After = PredefinedAdornmentLayers.Text)]
        [TextViewRole(PredefinedTextViewRoles.Document)]
#pragma warning disable CS0649 // Field is never assigned to; MEF populates it.
        internal AdornmentLayerDefinition LayerDefinition = null!;
#pragma warning restore CS0649

        // Populated by MEF after construction.
        [Import]
        internal IClassificationFormatMapService FormatMapService { get; set; } = null!;

        public void TextViewCreated(IWpfTextView textView)
        {
            textView.Properties.GetOrCreateSingletonProperty(
                () => new PeekPopup(textView, FormatMapService));
        }
    }

    internal sealed class PeekPopup
    {
        private const string LayerName = "VSNeoPeekPopup";

        private readonly IWpfTextView _view;
        private readonly IAdornmentLayer _layer;
        private readonly Border _popup;
        private readonly StackPanel _rows;
        private readonly FontFamily _fontFamily;
        private readonly double _fontSize;
        // Assigned by Subscribe(), which the constructor calls; the compiler
        // cannot see through the method call. Null until the first session attaches.
        private NvimStateHub _subscribedTo = null!;
        private int _readyHooked;
        private bool _visible;
        private bool _marksShowing;
        private bool _disposed;

        public PeekPopup(IWpfTextView view, IClassificationFormatMapService formatMapService)
        {
            _view = view;
            _layer = view.GetAdornmentLayer(LayerName);

            _rows = new StackPanel();
            _popup = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                BorderThickness = new Thickness(1),
                Child = _rows,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 10,
                    ShadowDepth = 2,
                    Opacity = 0.4,
                },
            };

            FontFamily fontFamily = new FontFamily("Consolas");
            double fontSize = 12;
            try
            {
                var map = formatMapService?.GetClassificationFormatMap(view);
                if (map != null)
                {
                    fontFamily = map.DefaultTextProperties.Typeface.FontFamily;
                    fontSize = map.DefaultTextProperties.FontRenderingEmSize;
                }
            }
            catch
            {
                // Cosmetic only; the default font is perfectly readable.
            }
            _fontFamily = fontFamily;
            _fontSize = fontSize;

            Subscribe();
            view.LostAggregateFocus += (s, e) => Hide();
            view.Closed += OnClosed;
        }

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

            if (_subscribedTo != null)
                _subscribedTo.ShowCmdChanged -= OnShowCmdChanged;
            session.State.ShowCmdChanged += OnShowCmdChanged;
            _subscribedTo = session.State;
        }

        private void OnSessionReady(bool ready)
        {
            if (!ready) return;
            var dispatcher = _view.VisualElement.Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            // Fire-and-forget: nothing meaningful to do with the DispatcherOperation.
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(Subscribe));
#pragma warning restore VSTHRD001
        }

        /// <summary>
        /// Called on the RPC read thread. A null showcmd means the pending "
        /// resolved - register picked, Escape, anything - so the peek is over.
        /// </summary>
        private void OnShowCmdChanged(string content)
        {
            if (content != null) return;
            var dispatcher = _view.VisualElement.Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            // Fire-and-forget: nothing meaningful to do with the DispatcherOperation.
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(Hide));
#pragma warning restore VSTHRD001
        }

        /// <summary>
        /// UI thread. rows are [name, preview] pairs straight from
        /// vsneo.registers(). The fetch is issued before the key reaches nvim
        /// (nvim will not answer RPC while it waits for the register name), so
        /// the response typically beats the redraw that sets showcmd - the
        /// showcmd check would drop every popup. Staleness is guarded by age
        /// instead, same as marks; dismissal still rides ShowCmdChanged.
        /// </summary>
        public void ShowRows(IReadOnlyList<string[]> rows, int requestedAt)
        {
            if (_disposed || rows.Count == 0) return;
            if (Environment.TickCount - requestedAt > 500) return;
            if (!_view.HasAggregateFocus) return;

            _marksShowing = false;
            Render(rows);
        }

        /// <summary>
        /// UI thread. Marks variant: unlike ", a pending ` or ' is not reliable
        /// in showcmd across layouts and nvim versions, so staleness is guarded
        /// by age (a response older than 500 ms describes a sequence that
        /// already resolved) and dismissal rides OnKeySent - the key processor
        /// sees the completing or aborting key before nvim does.
        /// </summary>
        public void ShowMarks(IReadOnlyList<string[]> rows, int requestedAt)
        {
            if (_disposed || rows.Count == 0) return;
            // int subtraction wraps; the 49-day TickCount rollover reads as
            // "too old" or "just now" once a month - either is cosmetic here.
            if (Environment.TickCount - requestedAt > 500) return;
            if (!_view.HasAggregateFocus) return;

            _marksShowing = true;
            Render(rows);
        }

        /// <summary>
        /// UI thread (the key path is). A key went to nvim: any pending mark
        /// peek is resolved by it, one way or the other. Register rows keep
        /// their showcmd dismissal; hiding twice is a no-op.
        /// </summary>
        public void OnKeySent()
        {
            if (_marksShowing) Hide();
        }

        private void Render(IReadOnlyList<string[]> rows)
        {
            try
            {
                var background = _view.Background ?? Brushes.White;
                var foreground = Inverted(background);

                _popup.Background = background;
                _popup.BorderBrush = new SolidColorBrush(
                    (foreground as SolidColorBrush ?? Brushes.Gray).Color) { Opacity = 0.4 };

                _rows.Children.Clear();
                var previewBrush = new SolidColorBrush(
                    (foreground as SolidColorBrush ?? Brushes.Gray).Color) { Opacity = 0.8 };
                foreach (var row in rows)
                {
                    var line = new TextBlock
                    {
                        FontFamily = _fontFamily,
                        FontSize = _fontSize,
                        Foreground = foreground,
                        TextWrapping = TextWrapping.NoWrap,
                    };
                    line.Inlines.Add(new Run(row[0] + "  ") { FontWeight = FontWeights.Bold });
                    line.Inlines.Add(new Run(row[1]) { Foreground = previewBrush });
                    _rows.Children.Add(line);
                }

                Position();
                if (!_visible)
                {
                    _layer.AddAdornment(
                        AdornmentPositioningBehavior.OwnerControlled, null, null, _popup, null);
                    _visible = true;
                }
            }
            catch (Exception ex)
            {
                // An adornment must never take the editor down with it.
                Log.Write("peek popup render failed", ex);
            }
        }

        private void Hide()
        {
            _marksShowing = false;
            if (!_visible) return;
            _layer.RemoveAdornment(_popup);
            _visible = false;
        }

        /// <summary>
        /// Just under the caret, like a completion list - that is where the eye
        /// is when the key is pressed. OwnerControlled means Visual Studio never
        /// repositions this; it is short-lived enough not to track scrolling.
        /// </summary>
        private void Position()
        {
            var caret = _view.Caret.Position.BufferPosition;
            var line = _view.GetTextViewLineContainingBufferPosition(caret);
            var bounds = line.GetCharacterBounds(caret);

            double left = bounds.Left - _view.ViewportLeft;
            double top = bounds.Bottom - _view.ViewportTop + 2;

            _popup.MaxWidth = Math.Max(200, _view.ViewportWidth - left - 20);
            Canvas.SetLeft(_popup, Math.Max(0, left));
            Canvas.SetTop(_popup, top);
        }

        /// <summary>Readable against the editor's own background, whatever theme is on.</summary>
        private static Brush Inverted(Brush background)
        {
            var solid = background as SolidColorBrush;
            if (solid == null) return Brushes.Gray;

            var c = solid.Color;
            bool dark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
            return dark ? Brushes.White : Brushes.Black;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_disposed) return;
            _disposed = true;

            Hide();

            if (_subscribedTo != null)
                _subscribedTo.ShowCmdChanged -= OnShowCmdChanged;
            if (Interlocked.Exchange(ref _readyHooked, 0) == 1)
                VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;

            _view.Closed -= OnClosed;
        }
    }
}
