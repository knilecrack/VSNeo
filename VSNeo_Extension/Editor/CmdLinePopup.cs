using System;
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
    /// Vim's command line as a floating, centered popup - the noice.nvim look -
    /// with nvim's wildmenu (Tab-completion) rendered as a list underneath.
    ///
    /// Everything shown is hub state: ext_cmdline for the input, ext_popupmenu
    /// for the completions. The key path is untouched; this is a renderer.
    /// Covers / and ? as well as :, so search gets the popup and the live
    /// match highlights at the same time.
    /// </summary>
    // Disabled: superseded by CmdLineOverlayWindow, a single session-level
    // shell-owned window (the Ctrl+Q shape). The per-view adornment popup paid
    // one dispatcher hop per open document per cmdline keystroke to draw in
    // exactly one view. Nothing else references this provider, so removing the
    // export is the whole switch; put it back to re-enable the in-view popup.
    //
    //     [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class CmdLinePopupProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("VSNeoCmdLinePopup")]
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
                () => new CmdLinePopup(textView, FormatMapService));
        }
    }

    internal sealed class CmdLinePopup
    {
        private const string LayerName = "VSNeoCmdLinePopup";
        private const int MaxCompletionRows = 10;

        private readonly IWpfTextView _view;
        private readonly IAdornmentLayer _layer;
        private readonly Border _popup;
        private readonly TextBlock _input;
        private readonly StackPanel _completions;
        // Assigned by Subscribe(), which the constructor calls; the compiler
        // cannot see through the method call. Null until the first session attaches.
        private NvimStateHub _subscribedTo = null!;
        private int _readyHooked;
        private bool _visible;
        private bool _disposed;

        // Hub events reach every open view on the RPC read thread; only the
        // focused one shows the popup. Tracked so unfocused views bail before
        // paying a dispatcher hop per cmdline keystroke. volatile: read on the
        // RPC thread.
        private volatile bool _focused;
        private int _renderPending;

        public CmdLinePopup(IWpfTextView view, IClassificationFormatMapService formatMapService)
        {
            _view = view;
            _layer = view.GetAdornmentLayer(LayerName);
            _focused = view.HasAggregateFocus;

            _input = new TextBlock { TextWrapping = TextWrapping.NoWrap };
            _completions = new StackPanel();

            _popup = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Children = { _input, _completions },
                },
                Effect = new DropShadowEffect
                {
                    BlurRadius = 12,
                    ShadowDepth = 2,
                    Opacity = 0.4,
                },
            };

            // Follow the editor's own font, so the popup reads as part of the
            // editor rather than as a dialog that happens to be nearby.
            try
            {
                var map = formatMapService?.GetClassificationFormatMap(view);
                if (map != null)
                {
                    _input.FontFamily = map.DefaultTextProperties.Typeface.FontFamily;
                    _input.FontSize = map.DefaultTextProperties.FontRenderingEmSize;
                }
            }
            catch
            {
                // Cosmetic only; the default font is perfectly readable.
            }

            Subscribe();
            view.LayoutChanged += OnLayoutChanged;
            view.GotAggregateFocus += OnGotFocus;
            view.LostAggregateFocus += OnLostFocus;
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
            {
                _subscribedTo.CmdLineChanged -= OnCmdLineChanged;
                _subscribedTo.CompletionsChanged -= OnCompletionsChanged;
            }
            session.State.CmdLineChanged += OnCmdLineChanged;
            session.State.CompletionsChanged += OnCompletionsChanged;
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

        /// <summary>Called on the RPC read thread.</summary>
        private void OnCmdLineChanged(string content) => BeginRender();

        /// <summary>Called on the RPC read thread.</summary>
        private void OnCompletionsChanged() => BeginRender();

        // Runs on the UI thread (view focus events). Focus loss hides the
        // popup; focus gain renders the current cmdline state, if any. The
        // flag is set from the event itself, not re-read from
        // HasAggregateFocus: that property is not reliable inside the events,
        // and a stale false there silenced the popup for good.
        private void OnGotFocus(object sender, EventArgs e)
        {
            _focused = true;
            Render();
        }

        private void OnLostFocus(object sender, EventArgs e)
        {
            _focused = false;
            Render();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_visible) Position();
        }

        private void BeginRender()
        {
            // Unfocused views never show the popup; the focus handler renders
            // on the way back in.
            if (!_focused) return;

            var dispatcher = _view.VisualElement.Dispatcher;
            if (dispatcher == null) return;

            // cmdline_show and popupmenu_select arrive in the same redraw
            // batch (every Tab press); one queued render covers both.
            if (Interlocked.Exchange(ref _renderPending, 1) == 1) return;

#pragma warning disable VSTHRD001
            // Fire-and-forget: nothing meaningful to do with the DispatcherOperation.
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() =>
                {
                    Volatile.Write(ref _renderPending, 0);
                    Render();
                }));
#pragma warning restore VSTHRD001
        }

        private void Render()
        {
            if (_disposed) return;

            try
            {
                var state = VSNeo_ExtensionPackage.Session?.State;

                // Only the focused view shows it. Several documents can be open,
                // and they all hear the same event.
                if (state == null || state.CmdLine == null || !_view.HasAggregateFocus)
                {
                    Hide();
                    return;
                }

                var background = _view.Background ?? Brushes.White;
                var foreground = Inverted(background);

                _popup.Background = background;
                _popup.BorderBrush = CachedBorderBrush(foreground);
                _input.Foreground = foreground;

                RenderInput(state, foreground);
                RenderCompletions(state, background, foreground);

                Show();
            }
            catch (Exception ex)
            {
                // An adornment must never take the editor down with it.
                Log.Write("cmdline popup render failed", ex);
            }
        }

        /// <summary>
        /// Prompt, text, and a block cursor sitting on a character rather than
        /// between two of them - Vim's convention, and the one the caret in the
        /// editor above is already following. Without it a long substitution is
        /// edited blind.
        /// </summary>
        private void RenderInput(NvimStateHub state, Brush foreground)
        {
            var content = state.CmdLine ?? string.Empty;
            int cursor = ColumnMapper.ByteToChar(content, state.CmdLinePos);
            if (cursor < 0) cursor = 0;
            if (cursor > content.Length) cursor = content.Length;

            _input.Inlines.Clear();
            _input.Inlines.Add(new Run((state.CmdLinePrefix ?? ":") + content.Substring(0, cursor)));

            // Past the end of the line the cursor has no character to sit on,
            // so it gets a space to occupy instead.
            var under = cursor < content.Length ? content.Substring(cursor, 1) : " ";
            _input.Inlines.Add(new Run(under)
            {
                Background = foreground,
                Foreground = _popup.Background,
            });

            if (cursor + 1 < content.Length)
                _input.Inlines.Add(new Run(content.Substring(cursor + 1)));
        }

        /// <summary>
        /// The wildmenu, capped at a window of rows around the selection - a
        /// long completion list (:e **/foo&lt;Tab&gt;) must not cover the file.
        /// </summary>
        private void RenderCompletions(NvimStateHub state, Brush background, Brush foreground)
        {
            _completions.Children.Clear();

            var words = state.CompletionWords;
            if (words == null || words.Count == 0) return;

            int selected = state.CompletionSelected;
            int first = 0;
            if (selected >= MaxCompletionRows) first = selected - MaxCompletionRows + 1;
            int last = Math.Min(words.Count, first + MaxCompletionRows);

            for (int i = first; i < last; i++)
            {
                var row = new TextBlock
                {
                    Text = words[i],
                    Padding = new Thickness(2, 0, 2, 0),
                    Foreground = foreground,
                };
                if (i == selected)
                {
                    row.Background = foreground;
                    row.Foreground = background;
                }
                _completions.Children.Add(row);
            }
        }

        private void Show()
        {
            Position();

            if (!_visible)
            {
                _layer.AddAdornment(
                    AdornmentPositioningBehavior.OwnerControlled, null, null, _popup, null);
                _visible = true;
            }
        }

        private void Hide()
        {
            if (!_visible) return;
            _layer.RemoveAdornment(_popup);
            _visible = false;
        }

        /// <summary>
        /// Centered horizontally, a little below the top edge - where the eye
        /// already is - rather than at the bottom where a status line lives.
        /// OwnerControlled means Visual Studio never repositions this; layout
        /// changes (scroll, resize) re-run it instead.
        /// </summary>
        private void Position()
        {
            double width = Math.Min(600, Math.Max(200, _view.ViewportWidth * 0.7));
            _popup.Width = width;
            _popup.MaxHeight = _view.ViewportHeight * 0.6;

            Canvas.SetLeft(_popup, (_view.ViewportWidth - width) / 2);
            Canvas.SetTop(_popup, Math.Max(0, _view.ViewportHeight * 0.15));
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

        // The border color only changes with the theme; rebuilding the brush
        // per render was one allocation per cmdline keystroke.
        private Color _borderColor;
        private Brush? _borderBrush;

        private Brush CachedBorderBrush(Brush foreground)
        {
            var color = (foreground as SolidColorBrush ?? Brushes.Gray).Color;
            if (_borderBrush == null || color != _borderColor)
            {
                _borderColor = color;
                _borderBrush = new SolidColorBrush(color) { Opacity = 0.4 };
            }
            return _borderBrush;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_disposed) return;
            _disposed = true;

            Hide();

            if (_subscribedTo != null)
            {
                _subscribedTo.CmdLineChanged -= OnCmdLineChanged;
                _subscribedTo.CompletionsChanged -= OnCompletionsChanged;
            }
            if (Interlocked.Exchange(ref _readyHooked, 0) == 1)
                VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;

            _view.LayoutChanged -= OnLayoutChanged;
            _view.GotAggregateFocus -= OnGotFocus;
            _view.LostAggregateFocus -= OnLostFocus;
            _view.Closed -= OnClosed;
        }
    }
}
