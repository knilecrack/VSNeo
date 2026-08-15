using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Draws Vim's message area above the command line.
    ///
    /// With ext_messages enabled, nvim sends :w results, substitution counts,
    /// "search hit BOTTOM" and error text through redraw notifications rather than
    /// painting them over the command line. It also sends mode text like
    /// "-- INSERT --" and "-- VISUAL --". Without this margin those are silently
    /// swallowed, so a :%s looks like it did nothing and the current mode is invisible.
    ///
    /// Positioned at the very bottom: the command line moved into the floating
    /// popup, so messages are the only thing still living down here.
    /// </summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(MessageMargin.MarginName)]
    [Order(After = PredefinedMarginNames.HorizontalScrollBar)]
    [MarginContainer(PredefinedMarginNames.Bottom)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class MessageMarginProvider : IWpfTextViewMarginProvider
    {
        // Populated by MEF after construction.
        [Import]
        internal IClassificationFormatMapService FormatMapService { get; set; } = null!;

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin parent) =>
            new MessageMargin(host.TextView, FormatMapService);
    }

    internal sealed class MessageMargin : Border, IWpfTextViewMargin
    {
        public const string MarginName = "VSNeoMessage";

        private readonly IWpfTextView _view;
        private readonly TextBlock _text;
        // Assigned by Subscribe(), which the constructor calls; the compiler
        // cannot see through the method call. Null until the first session attaches.
        private NvimStateHub _subscribedTo = null!;
        private int _readyHooked;
        private bool _disposed;

        public MessageMargin(IWpfTextView view, IClassificationFormatMapService formatMapService)
        {
            _view = view;

            _text = new TextBlock
            {
                Padding = new Thickness(6, 2, 6, 2),
                TextWrapping = TextWrapping.NoWrap,
            };

            try
            {
                var map = formatMapService?.GetClassificationFormatMap(view);
                if (map != null)
                {
                    _text.FontFamily = map.DefaultTextProperties.Typeface.FontFamily;
                    _text.FontSize = map.DefaultTextProperties.FontRenderingEmSize;
                }
            }
            catch
            {
                // Cosmetic only; the default font is perfectly readable.
            }

            Child = _text;
            Visibility = Visibility.Collapsed;

            Subscribe();
            view.Closed += (s, e) => Dispose();
        }

        private void Subscribe()
        {
            var session = VSNeo_ExtensionPackage.Session;
            if (session == null)
            {
                // Created with the startup document, before the package loaded:
                // wait for the ready broadcast rather than staying deaf.
                if (System.Threading.Interlocked.Exchange(ref _readyHooked, 1) == 0)
                    VSNeo_ExtensionPackage.SessionReadyChanged += OnSessionReady;
                return;
            }
            if (ReferenceEquals(_subscribedTo, session.State)) return;

            if (_subscribedTo != null)
            {
                _subscribedTo.MessageChanged -= OnMessageChanged;
                _subscribedTo.ModeMessageChanged -= OnModeMessageChanged;
            }
            session.State.MessageChanged += OnMessageChanged;
            session.State.ModeMessageChanged += OnModeMessageChanged;
            _subscribedTo = session.State;
        }

        private void OnSessionReady(bool ready)
        {
            if (!ready) return;
            var dispatcher = Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            // Fire-and-forget: nothing meaningful to do with the DispatcherOperation.
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(Subscribe));
#pragma warning restore VSTHRD001
        }

        /// <summary>Called on the RPC read thread.</summary>
        private void OnMessageChanged(string content)
        {
            var dispatcher = Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            // Fire-and-forget: nothing meaningful to do with the DispatcherOperation.
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => Render()));
#pragma warning restore VSTHRD001
        }

        /// <summary>Called on the RPC read thread.</summary>
        private void OnModeMessageChanged(string content)
        {
            var dispatcher = Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            // Fire-and-forget: nothing meaningful to do with the DispatcherOperation.
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => Render()));
#pragma warning restore VSTHRD001
        }

        private void Render()
        {
            if (_disposed) return;

            var session = VSNeo_ExtensionPackage.Session;
            var state = session?.State;

            // Mode text ("-- INSERT --", "-- VISUAL --") takes precedence over
            // ordinary messages, matching Vim: the mode indicator is replaced by
            // any real message, but while no message is active the mode remains
            // visible.
            string? mode = state?.ModeMessage;
            string? message = state?.Message;
            string? content = !string.IsNullOrEmpty(mode) ? mode : message;

            if (content == null || !_view.HasAggregateFocus)
            {
                Visibility = Visibility.Collapsed;
                _text.Text = string.Empty;
                return;
            }

            var foreground = ToBrush(_view.Background, invert: true);
            _text.Foreground = IsError(state?.MessageKind) ? Brushes.Red : foreground;
            Background = _view.Background;
            _text.Text = content;

            Visibility = Visibility.Visible;
        }

        private static bool IsError(string? kind) =>
            string.Equals(kind, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "emsg", StringComparison.OrdinalIgnoreCase);

        /// <summary>Readable against the editor's own background, whatever theme is on.</summary>
        private static Brush ToBrush(Brush background, bool invert)
        {
            var solid = background as SolidColorBrush;
            if (solid == null) return Brushes.Gray;

            var c = solid.Color;
            bool dark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
            return invert && dark ? Brushes.White : Brushes.Black;
        }

        public FrameworkElement VisualElement => this;
        public double MarginSize => ActualHeight;
        public bool Enabled => true;

        public ITextViewMargin? GetTextViewMargin(string marginName) =>
            string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_subscribedTo != null)
            {
                _subscribedTo.MessageChanged -= OnMessageChanged;
                _subscribedTo.ModeMessageChanged -= OnModeMessageChanged;
            }
            if (System.Threading.Interlocked.Exchange(ref _readyHooked, 0) == 1)
                VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;
        }
    }
}
