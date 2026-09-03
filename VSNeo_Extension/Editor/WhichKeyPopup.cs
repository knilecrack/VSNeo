using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// The which-key hint, LazyVim style: press a prefix (<leader>, or the head
    /// of any multi-key mapping) in normal or visual mode and pause, and the
    /// continuations appear in a bar at the bottom of the viewport.
    ///
    /// Purely informational - it never takes focus and never sees a key. The
    /// key processor drives it: every key sent to nvim in normal/visual mode is
    /// matched against the cached mapping table (NvimStateHub.KeymapChildren),
    /// and only a sequence that is a strict prefix of some mapping arms the
    /// delay timer. A completed or dead sequence cancels it, so a fast typist
    /// never sees the popup at all - it exists for the pause, which is exactly
    /// when the hint is wanted.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class WhichKeyPopupProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("VSNeoWhichKeyPopup")]
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
                () => new WhichKeyPopup(textView, FormatMapService));
        }
    }

    internal sealed class WhichKeyPopup
    {
        private const string LayerName = "VSNeoWhichKeyPopup";
        private const int MaxRows = 12;

        private readonly IWpfTextView _view;
        private readonly IAdornmentLayer _layer;
        private readonly Border _popup;
        private readonly StackPanel _rows;
        private readonly FontFamily _fontFamily;
        private readonly double _fontSize;
        private readonly DispatcherTimer _delay;
        // Assigned by Subscribe(), which the constructor calls; the compiler
        // cannot see through the method call. Null until the first session attaches.
        private NvimStateHub _subscribedTo = null!;
        private int _readyHooked;
        private bool _visible;
        private bool _disposed;
        private string _prefix = string.Empty;
        private VimMode _mode = VimMode.Normal;

        public WhichKeyPopup(IWpfTextView view, IClassificationFormatMapService formatMapService)
        {
            _view = view;
            _layer = view.GetAdornmentLayer(LayerName);

            _rows = new StackPanel();
            _popup = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
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

            // which-key's own delay, give or take: long enough that someone who
            // knows the mapping never triggers it, short enough that a hesitant
            // pause does.
            _delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _delay.Tick += (s, e) =>
            {
                _delay.Stop();
                Render();
            };

            Subscribe();
            view.LostAggregateFocus += (s, e) => Cancel();
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
                _subscribedTo.ModeChanged -= OnModeChanged;
            session.State.ModeChanged += OnModeChanged;
            _subscribedTo = session.State;
        }

        private void OnSessionReady(bool ready)
        {
            if (!ready) return;
            var dispatcher = _view.VisualElement.Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            // Fire-and-forget: nothing meaningful to do with the DispatcherOperation.
            _ = dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(Subscribe));
#pragma warning restore VSTHRD001
        }

        /// <summary>
        /// UI thread (the key path is). Records the pending prefix and arms the
        /// delay; when the popup is already up and the prefix just grew, the
        /// re-render is immediate - the pause already happened.
        /// </summary>
        public void Track(string prefix, VimMode mode)
        {
            if (_disposed) return;

            _prefix = prefix;
            _mode = mode;

            if (_visible)
            {
                Render();
                return;
            }

            _delay.Stop();
            _delay.Start();
        }

        /// <summary>UI thread. The sequence resolved or died; the hint is over.</summary>
        public void Cancel()
        {
            _delay.Stop();
            Hide();
        }

        /// <summary>Called on the RPC read thread.</summary>
        private void OnModeChanged(VimMode mode)
        {
            // A sequence cannot straddle a mode change; whatever was pending
            // either ran or was aborted.
            var dispatcher = _view.VisualElement.Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            // Fire-and-forget: nothing meaningful to do with the DispatcherOperation.
            _ = dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(Cancel));
#pragma warning restore VSTHRD001
        }

        private void Render()
        {
            if (_disposed) return;

            try
            {
                var state = VSNeo_ExtensionPackage.Session?.State;
                if (state == null || !_view.HasAggregateFocus) { Hide(); return; }

                var children = state.KeymapChildren(_mode, _prefix);
                if (children.Count == 0) { Hide(); return; }

                var background = _view.Background ?? Brushes.White;
                var foreground = Inverted(background);
                _popup.Background = background;
                _popup.BorderBrush = new SolidColorBrush(
                    (foreground as SolidColorBrush ?? Brushes.Gray).Color) { Opacity = 0.4 };
                var dimBrush = new SolidColorBrush(
                    (foreground as SolidColorBrush ?? Brushes.Gray).Color) { Opacity = 0.75 };

                _rows.Children.Clear();

                _rows.Children.Add(new TextBlock
                {
                    FontFamily = _fontFamily,
                    FontSize = _fontSize,
                    FontWeight = FontWeights.Bold,
                    Foreground = dimBrush,
                    Text = DisplayPrefix(_prefix),
                });

                // One row per distinct next token. A token that is itself a
                // prefix of longer mappings is a group; a token that completes
                // a mapping shows its description.
                var prefixDepth = NvimStateHub.SplitKeyTokens(_prefix).Count;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                int extra = 0;
                foreach (var entry in children)
                {
                    var tokens = NvimStateHub.SplitKeyTokens(entry.Lhs);
                    var next = tokens[prefixDepth];
                    if (!seen.Add(next)) continue;

                    bool isLeaf = tokens.Count == prefixDepth + 1;
                    string desc = isLeaf
                        ? entry.Desc
                        : "+" + CountLonger(children, next, prefixDepth) + " mappings";

                    if (_rows.Children.Count > MaxRows)
                    {
                        extra++;
                        continue;
                    }

                    var line = new TextBlock
                    {
                        FontFamily = _fontFamily,
                        FontSize = _fontSize,
                        Foreground = foreground,
                        TextWrapping = TextWrapping.NoWrap,
                    };
                    line.Inlines.Add(new Run(DisplayToken(next) + "  ") { FontWeight = FontWeights.Bold });
                    line.Inlines.Add(new Run(desc) { Foreground = dimBrush });
                    _rows.Children.Add(line);
                }

                if (extra > 0)
                    _rows.Children.Add(new TextBlock
                    {
                        FontFamily = _fontFamily,
                        FontSize = _fontSize,
                        Foreground = dimBrush,
                        Text = "+" + extra + " more",
                    });

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
                Log.Write("which-key popup render failed", ex);
            }
        }

        private static int CountLonger(IReadOnlyList<KeymapEntry> children, string token, int depth)
        {
            int count = 0;
            foreach (var entry in children)
            {
                var tokens = NvimStateHub.SplitKeyTokens(entry.Lhs);
                if (tokens[depth] == token) count++;
            }
            return count;
        }

        private void Hide()
        {
            if (!_visible) return;
            _layer.RemoveAdornment(_popup);
            _visible = false;
        }

        /// <summary>
        /// A bar at the bottom of the viewport, which-key's own spot: the eye
        /// lands there after a pause without leaving the text. OwnerControlled
        /// means Visual Studio never repositions this; it is short-lived enough
        /// not to track scrolling.
        /// </summary>
        private void Position()
        {
            _popup.MaxWidth = Math.Max(200, _view.ViewportWidth - 16);
            _popup.Measure(new Size(_popup.MaxWidth, double.PositiveInfinity));

            Canvas.SetLeft(_popup, 8);
            Canvas.SetTop(_popup,
                Math.Max(0, _view.ViewportHeight - _popup.DesiredSize.Height - 12));
        }

        private static string DisplayPrefix(string prefix)
        {
            return string.Join(" ",
                NvimStateHub.SplitKeyTokens(prefix).Select(DisplayToken).ToArray());
        }

        private static string DisplayToken(string token)
        {
            if (token == " ") return "SPC";
            return token;
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

            Cancel();

            if (_subscribedTo != null)
                _subscribedTo.ModeChanged -= OnModeChanged;
            if (Interlocked.Exchange(ref _readyHooked, 0) == 1)
                VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;

            _view.Closed -= OnClosed;
        }
    }
}
