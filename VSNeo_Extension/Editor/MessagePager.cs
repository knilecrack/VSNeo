using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class MessagePagerProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(MessagePager.LayerName)]
        [Order(After = PredefinedAdornmentLayers.Caret)]
        [TextViewRole(PredefinedTextViewRoles.Document)]
        // MEF populates this through the Export above; nothing in code assigns it.
        internal AdornmentLayerDefinition LayerDefinition = null!;

        [Import]
        internal IClassificationFormatMapService FormatMapService { get; set; } = null!;

        // Bookkeeping only: the pager builds nothing until output arrives.
        public void TextViewCreated(IWpfTextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(
                () => new MessagePager(textView, FormatMapService));
    }

    /// <summary>
    /// Long command output - :map, :set all, :ls, :messages - in a closable
    /// overlay, Vim's "more" prompt drawn by Visual Studio.
    ///
    /// With ext_messages nvim hands such output to the UI and never clears it,
    /// and the message margin used to grow to fit it: a pane over the editor
    /// that none of q, Q, Escape or :q closed, that came back with the next
    /// document, and whose q started a recording. Here it is an overlay at the
    /// bottom of the focused view with a close button, and while it is open it
    /// owns the keyboard the way the more prompt does:
    ///   j / Down            one line down      k / Up           one line up
    ///   Space / f / PgDn    one page down      b / PgUp         one page up
    ///   d / Ctrl+D          half a page down   u / Ctrl+U       half a page up
    ///   g / Home            top                G / End          bottom
    ///   q / Escape / Enter  close
    /// Any other key closes it and then does its usual job, as after Vim's
    /// hit-enter prompt - ':' goes straight on to the next command.
    ///
    /// The text lives in the hub (PagerText), so switching documents keeps it;
    /// only the focused view draws. The key path asks <see cref="IsOpen"/>, an
    /// in-memory field, so the zero-I/O invariant holds.
    /// </summary>
    internal sealed class MessagePager
    {
        internal const string LayerName = "VSNeoMessagePager";
        private const double MaxHeightFraction = 0.6;
        private const double Inset = 8;

        private readonly IWpfTextView _view;
        private readonly IClassificationFormatMapService _formatMapService;
        private readonly IAdornmentLayer _layer;

        private Border? _root;
        private ScrollViewer? _scroll;
        private TextBlock? _body;
        private TextBlock? _title;
        private TextBlock? _footer;
        private int _lineCount;
        private string? _shownText;

        private NvimStateHub? _subscribedTo;
        private int _readyHooked;
        private volatile bool _focused;

        /// <summary>UI thread. True while the overlay is on screen and owns the keys.</summary>
        public bool IsOpen { get; private set; }

        public MessagePager(IWpfTextView view, IClassificationFormatMapService formatMapService)
        {
            _view = view;
            _formatMapService = formatMapService;
            _layer = view.GetAdornmentLayer(LayerName);
            _focused = view.HasAggregateFocus;

            Subscribe();
            view.GotAggregateFocus += OnGotFocus;
            view.LostAggregateFocus += OnLostFocus;
            view.ViewportWidthChanged += OnViewportSizeChanged;
            view.ViewportHeightChanged += OnViewportSizeChanged;
            view.Closed += OnClosed;
        }

        private void Subscribe()
        {
            var session = VSNeo_ExtensionPackage.Session;
            if (session == null)
            {
                // Created with the startup document, before the package loaded.
                if (Interlocked.Exchange(ref _readyHooked, 1) == 0)
                    VSNeo_ExtensionPackage.SessionReadyChanged += OnSessionReady;
                return;
            }
            if (ReferenceEquals(_subscribedTo, session.State)) return;

            if (_subscribedTo != null) _subscribedTo.PagerChanged -= OnPagerChanged;
            session.State.PagerChanged += OnPagerChanged;
            _subscribedTo = session.State;
        }

        private void OnSessionReady(bool ready)
        {
            if (!ready) return;
#pragma warning disable VSTHRD001
            _ = _view.VisualElement.Dispatcher.BeginInvoke(
                Infrastructure.UiPriority.Decoration, new Action(Subscribe));
#pragma warning restore VSTHRD001
        }

        /// <summary>RPC read thread. Only the focused view draws; the rest catch up on focus.</summary>
        private void OnPagerChanged(string? text)
        {
            if (!_focused) return;
#pragma warning disable VSTHRD001
            _ = _view.VisualElement.Dispatcher.BeginInvoke(
                Infrastructure.UiPriority.Decoration, new Action(Render));
#pragma warning restore VSTHRD001
        }

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

        private void OnViewportSizeChanged(object sender, EventArgs e)
        {
            if (IsOpen) Place();
        }

        /// <summary>UI thread. Shows the hub's pager text in this view, or hides the overlay.</summary>
        private void Render()
        {
            using var perf = Infrastructure.Perf.Time("MessagePager.Render");
            if (_view.IsClosed) return;

            var text = _focused ? VSNeo_ExtensionPackage.Session?.State.PagerText : null;
            if (text == null)
            {
                Hide();
                return;
            }

            if (_root == null) Build();
            if (!ReferenceEquals(text, _shownText))
            {
                _shownText = text;
                _lineCount = NvimStateHub.CountLines(text);
                _body!.Text = text;
                _title!.Text = "Output  (" + _lineCount + " lines)";
                _scroll!.ScrollToHome();
            }

            ApplyTheme();
            if (!IsOpen)
            {
                _layer.AddAdornment(AdornmentPositioningBehavior.ViewportRelative, null, null, _root!, null);
                IsOpen = true;
            }
            Place();
        }

        private void Hide()
        {
            if (!IsOpen) return;
            _layer.RemoveAllAdornments();
            IsOpen = false;
        }

        /// <summary>Closes the pager for every view: the output was dismissed, not just hidden.</summary>
        private void Close()
        {
            _shownText = null;
            Hide();
            VSNeo_ExtensionPackage.Session?.State.ClosePager();
        }

        private void Build()
        {
            var fontFamily = new FontFamily("Consolas");
            double fontSize = 13;
            try
            {
                var map = _formatMapService?.GetClassificationFormatMap(_view);
                if (map != null)
                {
                    fontFamily = map.DefaultTextProperties.Typeface.FontFamily;
                    fontSize = map.DefaultTextProperties.FontRenderingEmSize;
                }
            }
            catch
            {
                // Cosmetic only; the fallback font reads fine.
            }

            _title = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

            var close = new Button
            {
                Content = "✕",
                ToolTip = "Close (q, Esc or Enter)",
                Focusable = false,
                Padding = new Thickness(6, 0, 6, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            close.Click += (s, e) => Close();

            var header = new DockPanel { Margin = new Thickness(8, 4, 4, 4) };
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
            header.Children.Add(_title);

            _body = new TextBlock
            {
                FontFamily = fontFamily,
                FontSize = fontSize,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(8, 0, 8, 4),
            };
            _scroll = new ScrollViewer
            {
                Content = _body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Focusable = false,
            };
            _scroll.ScrollChanged += (s, e) => UpdateFooter();

            _footer = new TextBlock
            {
                Margin = new Thickness(8, 2, 8, 4),
                Opacity = 0.7,
                FontSize = Math.Max(9, fontSize - 2),
            };

            var layout = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(_footer, Dock.Bottom);
            layout.Children.Add(header);
            layout.Children.Add(_footer);
            layout.Children.Add(_scroll);

            _root = new Border
            {
                Child = layout,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                SnapsToDevicePixels = true,
            };
        }

        private void ApplyTheme()
        {
            var background = _view.Background as SolidColorBrush;
            var c = background?.Color ?? Colors.White;
            bool dark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;

            var foreground = dark ? Brushes.Gainsboro : Brushes.Black;
            _root!.Background = background ?? Brushes.White;
            _root.BorderBrush = dark ? Brushes.DimGray : Brushes.Silver;
            _title!.Foreground = foreground;
            _body!.Foreground = foreground;
            _footer!.Foreground = foreground;
        }

        /// <summary>Bottom of the viewport, full width less an inset, at most 60% high.</summary>
        private void Place()
        {
            if (_root == null) return;

            double width = Math.Max(120, _view.ViewportWidth - 2 * Inset);
            double maxHeight = Math.Max(80, _view.ViewportHeight * MaxHeightFraction);

            _root.Width = width;
            _root.MaxHeight = maxHeight;
            _root.Measure(new Size(width, maxHeight));
            double height = Math.Min(_root.DesiredSize.Height, maxHeight);

            // Text-view coordinates (see PeekPopup.Position). ViewportRelative
            // keeps the panel fixed through later scrolls by shifting it with
            // each scroll delta, but the coordinates it starts from are the
            // layer's, so the viewport origin is added here; without it a
            // pager opened in a scrolled view sat a screenful above the text.
            Canvas.SetLeft(_root, _view.ViewportLeft + Inset);
            Canvas.SetTop(_root, _view.ViewportTop + Math.Max(0, _view.ViewportHeight - height - Inset));
            UpdateFooter();
        }

        private double LineHeight =>
            _body != null && _lineCount > 0 && _body.ActualHeight > 0
                ? _body.ActualHeight / _lineCount
                : 16;

        private void UpdateFooter()
        {
            if (_footer == null || _scroll == null) return;

            double lineHeight = LineHeight;
            int first = (int)(_scroll.VerticalOffset / lineHeight) + 1;
            int last = Math.Min(_lineCount, (int)((_scroll.VerticalOffset + _scroll.ViewportHeight) / lineHeight));
            if (last < first) last = first;

            _footer.Text = (last >= _lineCount && first <= 1 ? "all " + _lineCount + " lines" : "lines " + first + "-" + last + " of " + _lineCount)
                + "  ·  j/k scroll, Space/b page, g/G top/bottom, q Esc Enter close";
        }

        /// <summary>
        /// A key in nvim notation ("j", "&lt;Esc&gt;", "&lt;C-d&gt;") while the pager
        /// is open. True when the pager used it; false when it closed the pager
        /// and the key should carry on to its usual handler. UI thread.
        /// </summary>
        public bool HandleKey(string key)
        {
            if (!IsOpen || _scroll == null) return false;

            double line = LineHeight;
            double page = Math.Max(line, _scroll.ViewportHeight - line);

            switch (key)
            {
                case "q":
                case "<Esc>":
                case "<CR>":
                    Close();
                    return true;

                case "j":
                case "<Down>":
                    _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + line);
                    return true;
                case "k":
                case "<Up>":
                    _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - line);
                    return true;

                case " ":
                case "<Space>":
                case "f":
                case "<PageDown>":
                case "<C-f>":
                    _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + page);
                    return true;
                case "b":
                case "<PageUp>":
                case "<C-b>":
                    _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - page);
                    return true;

                case "d":
                case "<C-d>":
                    _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + page / 2);
                    return true;
                case "u":
                case "<C-u>":
                    _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - page / 2);
                    return true;

                case "g":
                case "<Home>":
                    _scroll.ScrollToHome();
                    return true;
                case "G":
                case "<End>":
                    _scroll.ScrollToEnd();
                    return true;

                case "<Left>":
                    _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - 4 * line);
                    return true;
                case "<Right>":
                    _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset + 4 * line);
                    return true;

                default:
                    Close();
                    return false;
            }
        }

        /// <summary>The per-view pager, if the creation listener made one and it is open.</summary>
        public static MessagePager? OpenFor(ITextView view) =>
            view.Properties.TryGetProperty(typeof(MessagePager), out MessagePager pager) && pager.IsOpen
                ? pager
                : null;

        private void OnClosed(object sender, EventArgs e)
        {
            if (_subscribedTo != null) _subscribedTo.PagerChanged -= OnPagerChanged;
            if (Interlocked.Exchange(ref _readyHooked, 0) == 1)
                VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;
            _view.GotAggregateFocus -= OnGotFocus;
            _view.LostAggregateFocus -= OnLostFocus;
            _view.ViewportWidthChanged -= OnViewportSizeChanged;
            _view.ViewportHeightChanged -= OnViewportSizeChanged;
            _view.Closed -= OnClosed;
        }
    }
}
