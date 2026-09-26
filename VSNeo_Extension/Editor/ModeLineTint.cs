using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// modes.nvim for Visual Studio: the cursor line gets a faint wash of the
    /// current mode's color, so the mode is visible where you are looking
    /// rather than in the status bar. Opt-in (vsneo_mode_line).
    ///
    /// The colors are vsneo_cursor_color's, per mode, so the tint, the cursor,
    /// the trail and the beacon all agree. Modes the rc leaves uncolored fall
    /// back to modes.nvim's palette - teal insert, red replace, purple visual,
    /// amber operator-pending - and normal mode stays untinted unless it has a
    /// color of its own, as in modes.nvim.
    ///
    /// One full-width Rectangle behind the text (below the text layer, so
    /// glyphs and the selection stay readable). Mode switches cross-fade the
    /// color over 120 ms; everything else is a move of that one element on
    /// caret-line change or layout - nothing per keystroke on the same line.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ModeLineTintProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("VSNeoModeLine")]
        [Order(Before = PredefinedAdornmentLayers.Text)]
        [TextViewRole(PredefinedTextViewRoles.Document)]
        // MEF populates this through the Export above; nothing in code assigns it.
        internal AdornmentLayerDefinition LayerDefinition = null!;

        // Bookkeeping only (see the TextViewCreated invariant).
        public void TextViewCreated(IWpfTextView textView)
        {
            textView.Properties.GetOrCreateSingletonProperty(() => new ModeLineTint(textView));
        }
    }

    internal sealed class ModeLineTint
    {
        private const string LayerName = "VSNeoModeLine";

        private readonly IWpfTextView _view;
        private IAdornmentLayer? _layer;
        private Rectangle? _bar;
        private SolidColorBrush? _fill;      // unfrozen: its Color cross-fades
        private Color _shownColor;
        private bool _hasColor;
        private int _lastLine = -1;
        private NvimStateHub? _subscribedTo;
        private bool _readyHooked;
        private bool _closed;

        public ModeLineTint(IWpfTextView view)
        {
            _view = view;
            view.Caret.PositionChanged += OnCaretMoved;
            view.LayoutChanged += OnLayoutChanged;
            view.GotAggregateFocus += OnFocusChanged;
            view.LostAggregateFocus += OnFocusChanged;
            view.Closed += OnClosed;
            Subscribe();
        }

        /// <summary>
        /// The tint for a mode: vsneo_cursor_color's slot when set, else
        /// modes.nvim's palette; null for "no tint" (normal and cmdline by
        /// default). Also used by the relative line number margin for the
        /// cursor line's number.
        /// </summary>
        internal static Color? TintColor(NvimStateHub state, VimMode mode)
        {
            int slot = mode switch
            {
                VimMode.Insert => 1,
                VimMode.Replace => 2,
                VimMode.Visual => 3,
                VimMode.OperatorPending => 4,
                VimMode.CmdLine => 5,
                _ => 0,
            };

            var colors = state.CursorColors;
            int rgb = slot < colors.Length ? colors[slot] : -1;
            if (rgb < 0)
            {
                rgb = slot switch
                {
                    1 => 0x78CCC5,   // insert: teal
                    2 => 0xC75C6A,   // replace: red
                    3 => 0x9745BE,   // visual: purple
                    4 => 0xF5C359,   // operator-pending: amber
                    _ => -1,         // normal, cmdline: untinted
                };
            }
            if (rgb < 0) return null;
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        // ---- wiring ----------------------------------------------------------

        private void Subscribe()
        {
            if (!_readyHooked)
            {
                VSNeo_ExtensionPackage.SessionReadyChanged += OnSessionReady;
                _readyHooked = true;
            }
            var session = VSNeo_ExtensionPackage.Session;
            if (session == null || ReferenceEquals(_subscribedTo, session.State)) return;

            if (_subscribedTo != null)
            {
                _subscribedTo.ModeChanged -= OnModeChanged;
                _subscribedTo.CursorStyleChanged -= OnSettingsChanged;
            }
            session.State.ModeChanged += OnModeChanged;
            session.State.CursorStyleChanged += OnSettingsChanged;
            _subscribedTo = session.State;
        }

        // RPC-thread events hop to the UI thread.
        private void OnSessionReady(bool ready) => Post(() => { Subscribe(); Update(fade: false); });
        private void OnModeChanged(VimMode mode) => Post(() => Update(fade: true));
        private void OnSettingsChanged() => Post(() => Update(fade: false));

        private void Post(Action action)
        {
            if (_closed) return;
            var dispatcher = _view.VisualElement.Dispatcher;
            if (dispatcher == null) return;
#pragma warning disable VSTHRD001
            _ = dispatcher.BeginInvoke(DispatcherPriority.Input, action);
#pragma warning restore VSTHRD001
        }

        private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e)
        {
            // Only a line change moves the bar; typing along a line does not.
            int line;
            try { line = e.NewPosition.BufferPosition.GetContainingLine().LineNumber; }
            catch { return; }
            if (line == _lastLine && _bar?.Visibility == Visibility.Visible) return;
            Update(fade: false);
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_bar == null || _bar.Visibility != Visibility.Visible) return;
            Update(fade: false);
        }

        private void OnFocusChanged(object sender, EventArgs e) => Update(fade: false);

        // ---- drawing -----------------------------------------------------------

        private void Update(bool fade)
        {
            if (_closed) return;
            try
            {
                var state = VSNeo_ExtensionPackage.Session?.State;
                if (state == null || !state.ModeLineEnabled || !_view.HasAggregateFocus)
                {
                    Hide();
                    return;
                }

                var color = TintColor(state, state.Mode);
                if (color == null)
                {
                    Hide();
                    return;
                }

                if (_view.InLayout) return;   // LayoutChanged calls back in

                ITextViewLine line;
                try { line = _view.Caret.ContainingTextViewLine; }
                catch (InvalidOperationException) { return; }
                if (line == null || line.VisibilityState == VisibilityState.Unattached
                    || line.VisibilityState == VisibilityState.Hidden)
                {
                    Hide();
                    return;
                }

                var bar = EnsureBar();
                byte alpha = (byte)Math.Round(255 * Math.Max(0, Math.Min(1, state.ModeLineOpacityPermille / 1000.0)));
                var target = Color.FromArgb(alpha, color.Value.R, color.Value.G, color.Value.B);
                SetColor(target, fade && bar.Visibility == Visibility.Visible);

                // Full width of the view, on the line's text rows (a CodeLens
                // row above the text is not the cursor line).
                Canvas.SetLeft(bar, _view.ViewportLeft);
                Canvas.SetTop(bar, line.TextTop);
                bar.Width = Math.Max(1, _view.ViewportWidth);
                bar.Height = Math.Max(1, line.TextHeight);
                bar.Visibility = Visibility.Visible;
                _lastLine = _view.Caret.Position.BufferPosition.GetContainingLine().LineNumber;
            }
            catch (Exception ex)
            {
                Infrastructure.Log.Write("mode line tint failed", ex);
                Hide();
            }
        }

        /// <summary>
        /// Cross-fades on a mode switch (120 ms), snaps otherwise; no fade at
        /// all when Windows animations are off.
        /// </summary>
        private void SetColor(Color target, bool fade)
        {
            if (_fill == null) return;
            if (_hasColor && target == _shownColor) return;

            if (fade && _hasColor && SystemParameters.ClientAreaAnimation)
            {
                var animation = new ColorAnimation(_shownColor, target, TimeSpan.FromMilliseconds(120))
                {
                    FillBehavior = FillBehavior.HoldEnd,
                };
                _fill.BeginAnimation(SolidColorBrush.ColorProperty, animation);
            }
            else
            {
                _fill.BeginAnimation(SolidColorBrush.ColorProperty, null);
                _fill.Color = target;
            }
            _shownColor = target;
            _hasColor = true;
        }

        private void Hide()
        {
            _lastLine = -1;
            if (_bar != null) _bar.Visibility = Visibility.Collapsed;
        }

        private Rectangle EnsureBar()
        {
            if (_bar != null) return _bar;

            _layer ??= _view.GetAdornmentLayer(LayerName);
            _fill = new SolidColorBrush(Colors.Transparent);
            _bar = new Rectangle
            {
                Fill = _fill,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            _layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, _bar, null);
            return _bar;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_closed) return;
            _closed = true;
            _view.Caret.PositionChanged -= OnCaretMoved;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.GotAggregateFocus -= OnFocusChanged;
            _view.LostAggregateFocus -= OnFocusChanged;
            _view.Closed -= OnClosed;
            if (_readyHooked) VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;
            if (_subscribedTo != null)
            {
                _subscribedTo.ModeChanged -= OnModeChanged;
                _subscribedTo.CursorStyleChanged -= OnSettingsChanged;
            }
        }
    }
}
