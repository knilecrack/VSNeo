using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// beacon.nvim for Visual Studio: after a jump of vsneo_beacon_min_jump
    /// lines or more (G, n, %, F12, &lt;C-o&gt;, a mouse click far away), and when a
    /// document gains focus, a bar in the cursor's color flashes from the cursor
    /// to the right, shrinks back into it and fades - "the cursor is here".
    ///
    /// Insert and replace mode never flash: Enter moves a line at a time, and a
    /// paste that moves the caret far is not a jump anyone lost track of.
    ///
    /// The bar is one Rectangle animated by WPF itself (width scale + opacity),
    /// so there is no frame loop of our own. It follows its line through
    /// scrolls - a smooth scroll moves the jump target during the flash - by
    /// re-anchoring on LayoutChanged while visible.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class JumpBeaconProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("VSNeoBeacon")]
        [Order(After = PredefinedAdornmentLayers.Text, Before = "VSNeoCursorTrail")]
        [TextViewRole(PredefinedTextViewRoles.Document)]
        // MEF populates this through the Export above; nothing in code assigns it.
        internal AdornmentLayerDefinition LayerDefinition = null!;

        [Import]
        internal IEditorFormatMapService FormatMapService = null!;

        // Bookkeeping only (see the TextViewCreated invariant).
        public void TextViewCreated(IWpfTextView textView)
        {
            textView.Properties.GetOrCreateSingletonProperty(
                () => new JumpBeacon(textView, FormatMapService));
        }
    }

    internal sealed class JumpBeacon
    {
        private const string LayerName = "VSNeoBeacon";

        private readonly IWpfTextView _view;
        private readonly IEditorFormatMapService _formatMapService;
        private IAdornmentLayer? _layer;
        private IEditorFormatMap? _formatMap;
        private Rectangle? _bar;
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        private ITrackingPoint? _anchor;     // line start the bar sits on, while visible
        private bool _pending;               // one flash queued for after layout
        private bool _closed;

        public JumpBeacon(IWpfTextView view, IEditorFormatMapService formatMapService)
        {
            _view = view;
            _formatMapService = formatMapService;

            view.Caret.PositionChanged += OnCaretMoved;
            view.LayoutChanged += OnLayoutChanged;
            view.GotAggregateFocus += OnGotFocus;
            view.Closed += OnClosed;
        }

        private static NvimStateHub? State => VSNeo_ExtensionPackage.Session?.State;

        private static bool Enabled(NvimStateHub? state) =>
            state != null && state.BeaconEnabled && state.BeaconMs > 0
            && SystemParameters.ClientAreaAnimation;

        private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e)
        {
            if (_closed) return;
            var state = State;
            if (!Enabled(state) || !_view.HasAggregateFocus) return;

            var mode = state!.Mode;
            if (mode == VimMode.Insert || mode == VimMode.Replace || mode == VimMode.CmdLine) return;

            // Buffer lines are the right measure here: a jump over a collapsed
            // region is still a jump the eye has to follow.
            int from = e.OldPosition.BufferPosition.GetContainingLine().LineNumber;
            int to = e.NewPosition.BufferPosition.GetContainingLine().LineNumber;
            if (Math.Abs(to - from) < Math.Max(1, state.BeaconMinJump)) return;

            QueueFlash();
        }

        private void OnGotFocus(object sender, EventArgs e)
        {
            if (_closed || !Enabled(State)) return;
            QueueFlash();
        }

        /// <summary>
        /// The caret event arrives before the jump's scroll has laid out, so
        /// the flash waits for the dispatcher's Loaded pass - after layout -
        /// where the caret's line has its final geometry.
        /// </summary>
        private void QueueFlash()
        {
            if (_pending) return;
            _pending = true;
            var dispatcher = _view.VisualElement.Dispatcher;
            if (dispatcher == null) { _pending = false; return; }
#pragma warning disable VSTHRD001
            _ = dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Flash));
#pragma warning restore VSTHRD001
        }

        private void Flash()
        {
            _pending = false;
            if (_closed) return;

            try
            {
                var state = State;
                if (!Enabled(state)) return;

                var caretPoint = _view.Caret.Position.BufferPosition;
                var lineStart = caretPoint.GetContainingLine().Start;
                _anchor = caretPoint.Snapshot.CreateTrackingPoint(lineStart.Position, PointTrackingMode.Negative);

                var bar = EnsureBar();
                var color = CursorColor();

                // Bright at the cursor, fading out to the right.
                var fill = new LinearGradientBrush(
                    Color.FromArgb(0xC0, color.R, color.G, color.B),
                    Color.FromArgb(0x00, color.R, color.G, color.B),
                    new Point(0, 0.5), new Point(1, 0.5));
                fill.Freeze();
                bar.Fill = fill;

                double column = _view.FormattedLineSource?.ColumnWidth ?? 8;
                bar.Width = Math.Max(1, state!.BeaconWidth) * column;

                if (!Place()) return;

                var duration = TimeSpan.FromMilliseconds(state.BeaconMs);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                var shrink = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
                var fade = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
                fade.Completed += (s, e) => Hide();

                bar.Visibility = Visibility.Visible;
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
                bar.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            catch (Exception ex)
            {
                Infrastructure.Log.Write("jump beacon failed", ex);
                Hide();
            }
        }

        /// <summary>
        /// Puts the bar on its line at the caret's x. False (and hidden) when
        /// the line is not on screen.
        /// </summary>
        private bool Place()
        {
            if (_bar == null || _anchor == null) return false;
            if (_view.InLayout) return true;   // LayoutChanged calls back in

            var lines = _view.TextViewLines;
            if (lines == null) return false;

            var point = _anchor.GetPoint(_view.TextSnapshot);
            var line = lines.GetTextViewLineContainingBufferPosition(point);
            if (line == null || line.VisibilityState == Microsoft.VisualStudio.Text.Formatting.VisibilityState.Hidden)
            {
                Hide();
                return false;
            }

            double left;
            try { left = _view.Caret.Left; }
            catch (InvalidOperationException) { left = line.TextLeft; }

            Canvas.SetLeft(_bar, left);
            Canvas.SetTop(_bar, line.TextTop);
            _bar.Height = line.TextHeight;
            return true;
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_closed || _anchor == null || _bar == null || _bar.Visibility != Visibility.Visible) return;
            Place();
        }

        private void Hide()
        {
            _anchor = null;
            if (_bar == null) return;
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _bar.BeginAnimation(UIElement.OpacityProperty, null);
            _bar.Visibility = Visibility.Collapsed;
        }

        private Rectangle EnsureBar()
        {
            if (_bar != null) return _bar;

            _layer ??= _view.GetAdornmentLayer(LayerName);
            _bar = new Rectangle
            {
                IsHitTestVisible = false,
                RenderTransform = _scale,   // ScaleX shrinks toward the cursor (origin left)
                Visibility = Visibility.Collapsed,
            };
            _layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, _bar, null);
            return _bar;
        }

        /// <summary>The custom cursor's color for this mode, else the theme's caret color.</summary>
        private Color CursorColor()
        {
            var custom = CustomCursorAdornment.For(_view)?.CurrentColor;
            if (custom != null) return custom.Value;
            _formatMap ??= _formatMapService.GetEditorFormatMap(_view);
            return CustomCursorAdornment.ReadCaretColor(_formatMap);
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_closed) return;
            _closed = true;
            Hide();
            _view.Caret.PositionChanged -= OnCaretMoved;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.GotAggregateFocus -= OnGotFocus;
            _view.Closed -= OnClosed;
        }
    }
}
