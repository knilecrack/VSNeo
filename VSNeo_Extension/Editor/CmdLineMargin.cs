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
    /// Draws Vim's command line under the editor.
    ///
    /// The content was already arriving - ext_cmdline has been enabled since the
    /// first session and NvimStateHub has cached it all along - it simply had
    /// nowhere to go, so ":" and "/" worked while appearing to do nothing at all.
    ///
    /// A margin rather than an adornment: the command line belongs below the text
    /// the way it does in Vim, it must not overlap code, and a margin that collapses
    /// to zero height costs nothing while hidden.
    /// </summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(CmdLineMargin.MarginName)]
    [Order(After = PredefinedMarginNames.HorizontalScrollBar)]
    [MarginContainer(PredefinedMarginNames.Bottom)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class CmdLineMarginProvider : IWpfTextViewMarginProvider
    {
        [Import]
        internal IClassificationFormatMapService FormatMapService { get; set; }

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin parent) =>
            new CmdLineMargin(host.TextView, FormatMapService);
    }

    internal sealed class CmdLineMargin : Border, IWpfTextViewMargin
    {
        public const string MarginName = "VSNeoCmdLine";

        private readonly IWpfTextView _view;
        private readonly TextBlock _text;
        private NvimStateHub _subscribedTo;
        private bool _disposed;

        public CmdLineMargin(IWpfTextView view, IClassificationFormatMapService formatMapService)
        {
            _view = view;

            _text = new TextBlock
            {
                Padding = new Thickness(6, 2, 6, 2),
                TextWrapping = TextWrapping.NoWrap,
            };

            // Follow the editor's own font, so the command line reads as part of the
            // editor rather than as a dialog that happens to be nearby.
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
            if (session == null || ReferenceEquals(_subscribedTo, session.State)) return;

            if (_subscribedTo != null) _subscribedTo.CmdLineChanged -= OnCmdLineChanged;
            session.State.CmdLineChanged += OnCmdLineChanged;
            _subscribedTo = session.State;
        }

        /// <summary>Called on the RPC read thread.</summary>
        private void OnCmdLineChanged(string content)
        {
            var dispatcher = Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => Show(content)));
#pragma warning restore VSTHRD001
        }

        private void Show(string content)
        {
            if (_disposed) return;

            // Only the focused view should show it. Several documents can be open,
            // and they all hear the same event.
            if (content == null || !_view.HasAggregateFocus)
            {
                Visibility = Visibility.Collapsed;
                _text.Text = string.Empty;
                return;
            }

            // The prompt comes separately from the content, and which one it is
            // matters: ":" and "/" are the same mechanism, so assuming ":" would
            // show a search as though it were a command.
            var session = VSNeo_ExtensionPackage.Session;
            _text.Text = (session?.State.CmdLinePrefix ?? ":") + content;
            _text.Foreground = ToBrush(_view.Background, invert: true);
            Background = _view.Background;
            Visibility = Visibility.Visible;
        }

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

        public ITextViewMargin GetTextViewMargin(string marginName) =>
            string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_subscribedTo != null) _subscribedTo.CmdLineChanged -= OnCmdLineChanged;
        }
    }
}
