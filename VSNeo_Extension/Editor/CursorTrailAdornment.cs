using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Neovide-style cursor trail: when the caret jumps, a quad smears from
    /// where it was to where it is, its leading edge arriving first and its
    /// trailing edge catching up.
    ///
    /// Visual Studio's own caret is never touched - it moves instantly, as it
    /// always did, and stays authoritative. The trail is a translucent overlay
    /// drawn beneath it that exists only while it is moving and disappears on
    /// arrival. Nothing here is on the key path: the caret event only raises a
    /// flag and asks for frames, and the frames themselves (CompositionTarget
    /// .Rendering) are subscribed only while something is animating.
    ///
    /// The animation is Neovide's: each corner of the cursor quad is its own
    /// critically damped spring toward the matching corner of the destination.
    /// Corners facing the direction of travel get a short animation, trailing
    /// ones the full length, and the difference is the smear. Configured from
    /// ~/.vsneorc with Neovide's names under a vsneo_ prefix
    /// (vsneo_cursor_animation_length, vsneo_cursor_trail_size,
    /// vsneo_cursor_animate_in_insert_mode); off when Windows animations are.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class CursorTrailAdornmentProvider : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("VSNeoCursorTrail")]
        [Order(After = PredefinedAdornmentLayers.Text, Before = PredefinedAdornmentLayers.Caret)]
        [TextViewRole(PredefinedTextViewRoles.Document)]
        // MEF populates this through the Export above; nothing in code assigns it.
        internal AdornmentLayerDefinition LayerDefinition = null!;

        [Import]
        internal IEditorFormatMapService FormatMapService = null!;

        // Bookkeeping only (see the TextViewCreated invariant): the adornment
        // subscribes to view events and defers everything else - the format
        // map included - to the first frame it actually draws.
        public void TextViewCreated(IWpfTextView textView)
        {
            textView.Properties.GetOrCreateSingletonProperty(
                () => new CursorTrailAdornment(textView, FormatMapService));
        }
    }

    internal sealed class CursorTrailAdornment
    {
        private const string LayerName = "VSNeoCursorTrail";

        // Corners in order: top-left, top-right, bottom-right, bottom-left.
        // Positions are viewport-relative (0,0 is the top-left of what is on
        // screen), not view coordinates: a jump that also scrolls (G, gg, a
        // far n) should travel from where the cursor was *on screen*, as it
        // does in Neovide, not from where its old line now sits thousands of
        // pixels off the top.
        private readonly Point[] _pos = new Point[4];
        private readonly Vector[] _vel = new Vector[4];
        private readonly double[] _length = new double[4];

        private readonly IWpfTextView _view;
        private readonly IEditorFormatMapService _formatMapService;
        private IAdornmentLayer? _layer;
        private Polygon? _shape;
        private CursorVfx? _vfx;
        private IEditorFormatMap? _formatMap;

        private bool _hasPosition;   // _pos holds a real caret rect
        private bool _caretMoved;    // a real move since the last frame
        private bool _rendering;     // subscribed to CompositionTarget.Rendering
        private bool _closed;
        private TimeSpan _lastFrame;

        public CursorTrailAdornment(IWpfTextView view, IEditorFormatMapService formatMapService)
        {
            _view = view;
            _formatMapService = formatMapService;

            view.Caret.PositionChanged += OnCaretMoved;
            view.LayoutChanged += OnLayoutChanged;
            view.LostAggregateFocus += OnLostFocus;
            view.Closed += OnClosed;
        }

        private static NvimStateHub? State => VSNeo_ExtensionPackage.Session?.State;

        /// <summary>
        /// Off with no session (plain Visual Studio behaves as plain Visual
        /// Studio), with neither a trail length nor a VFX mode, and when
        /// Windows animations are off - "Show animations in Windows" is an
        /// accessibility switch, and a sliding cursor is exactly the motion it
        /// exists to stop.
        /// </summary>
        private static bool Enabled(NvimStateHub? state) =>
            state != null
            && (state.CursorAnimationMs > 0 || VfxModesOf(state) != VfxModes.None)
            && SystemParameters.ClientAreaAnimation;

        // Parsed once per distinct string: the hub hands back the same
        // instance until the companion pushes new settings.
        private static string? _parsedModesText;
        private static VfxModes _parsedModes;

        private static VfxModes VfxModesOf(NvimStateHub state)
        {
            var text = state.CursorVfxModes;
            if (!ReferenceEquals(text, _parsedModesText))
            {
                _parsedModes = VfxSettings.ParseModes(text);
                _parsedModesText = text;
            }
            return _parsedModes;
        }

        private static VfxSettings VfxSettingsOf(NvimStateHub state) =>
            new VfxSettings(
                VfxModesOf(state),
                state.CursorVfxOpacity / 255.0,
                state.CursorVfxLifetimeMs / 1000.0,
                state.CursorVfxHighlightLifetimeMs / 1000.0,
                state.CursorVfxDensityPermille / 1000.0,
                state.CursorVfxSpeedPermille / 1000.0,
                state.CursorVfxPhasePermille / 1000.0,
                state.CursorVfxCurlPermille / 1000.0);

        private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e)
        {
            if (_closed) return;

            var state = State;
            if (!Enabled(state) || !_view.HasAggregateFocus)
            {
                // Costs nothing while off: no frames, just forget the old
                // spot, so the first move after turning the trail on (or
                // focusing the view) snaps instead of animating from it.
                _hasPosition = false;
                _caretMoved = false;
                Settle(hide: true);
                _vfx?.Clear();
                StopFrames();
                return;
            }

            // PositionChanged also fires when an edit elsewhere re-snapshots
            // the caret without moving it. Only a new line or column is a move.
            var oldPoint = e.OldPosition.BufferPosition;
            var newPoint = e.NewPosition.BufferPosition;
            var oldLine = oldPoint.GetContainingLine();
            var newLine = newPoint.GetContainingLine();
            bool sameLine = oldLine.LineNumber == newLine.LineNumber;
            if (sameLine && oldPoint.Position - oldLine.Start.Position
                            == newPoint.Position - newLine.Start.Position)
            {
                RequestSnap();
                return;
            }

            // Visual Studio's caret sits at the selection's exclusive end in
            // visual mode, one character off the Vim cursor that
            // VisualBlockCaretAdornment draws; a trail landing there would
            // look wrong. The command line never moves the text caret.
            var mode = state!.Mode;
            if (mode == VimMode.Visual || mode == VimMode.CmdLine)
            {
                RequestSnap();
                return;
            }

            if ((mode == VimMode.Insert || mode == VimMode.Replace)
                && !state.CursorAnimateInInsert)
            {
                RequestSnap();
                return;
            }

            _caretMoved = true;
            StartFrames();
        }

        /// <summary>
        /// A scroll or an edit moves the caret on screen without the caret
        /// moving. Visual Studio scrolls instantly, so a cursor sliding after
        /// the text would look detached from it: the trail snaps instead -
        /// unless this layout is the scroll half of a real jump, in which case
        /// _caretMoved is already set and the frame animates.
        /// </summary>
        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_closed || !_hasPosition) return;
            RequestSnap();
        }

        private void OnLostFocus(object sender, EventArgs e)
        {
            _caretMoved = false;
            Settle(hide: true);
            _vfx?.Clear();
            StopFrames();
        }

        private void RequestSnap()
        {
            if (_caretMoved) return; // a pending animation already covers it
            StartFrames();
        }

        private void StartFrames()
        {
            if (_rendering || _closed) return;
            _rendering = true;
            _lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }

        private void StopFrames()
        {
            if (!_rendering) return;
            _rendering = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object sender, EventArgs e)
        {
            try
            {
                Frame(e as RenderingEventArgs);
            }
            catch (Exception ex)
            {
                // A decoration must never take the editor down with it, and
                // a throwing frame handler would throw again sixty times a
                // second: stop and hide.
                Infrastructure.Log.Write("cursor trail frame failed", ex);
                Settle(hide: true);
                _vfx?.Clear();
                StopFrames();
            }
        }

        private void Frame(RenderingEventArgs? args)
        {
            if (_closed) { StopFrames(); return; }

            // Mid-layout the caret geometry is not readable; the next frame is.
            if (_view.InLayout) return;

            // Frame time from WPF's own clock; the first frame of a run has no
            // predecessor, and a stalled UI thread must not become one giant
            // step that overshoots the whole animation.
            double dt = 1.0 / 60;
            if (args != null)
            {
                if (args.RenderingTime == _lastFrame) return; // same frame, fired twice
                if (_lastFrame != TimeSpan.Zero)
                    dt = (args.RenderingTime - _lastFrame).TotalSeconds;
                _lastFrame = args.RenderingTime;
            }
            if (dt <= 0) return;
            if (dt > 1.0 / 30) dt = 1.0 / 30;

            bool trailActive = StepTrail(dt);

            // Particles outlive the trail: the loop keeps running until both
            // are done, then unsubscribes.
            bool vfxActive = false;
            if (_vfx != null)
            {
                PlaceVfx(_vfx);
                vfxActive = _vfx.Update(dt);
            }

            if (!trailActive && !vfxActive) StopFrames();
        }

        /// <summary>One frame of the trail. Returns whether it is still moving.</summary>
        private bool StepTrail(double dt)
        {
            if (!TryGetCaretCorners(out var target))
            {
                // Caret scrolled out of view or not laid out: nothing to draw
                // toward, and nothing valid to remember.
                _hasPosition = false;
                _caretMoved = false;
                Settle(hide: true);
                return false;
            }

            if (_caretMoved)
            {
                _caretMoved = false;
                if (_hasPosition) EmitVfx(target);
                if (!_hasPosition || !BeginMove(target))
                {
                    Snap(target);
                    return false;
                }
            }
            else if (!IsMoving())
            {
                // Not animating: a snap request (scroll, edit, a move the
                // trail skips). Track the caret and go quiet.
                Snap(target);
                return false;
            }

            bool settled = true;
            for (int i = 0; i < 4; i++)
            {
                Step(ref _pos[i], ref _vel[i], target[i], _length[i], dt);
                // Within a pixel the quad is hidden under the real caret
                // anyway; waiting for the spring's long exponential tail
                // would only leave a faint double caret on screen.
                if ((_pos[i] - target[i]).Length > 1 || _vel[i].Length > 20) settled = false;
            }

            if (settled)
            {
                Snap(target);
                return false;
            }

            Draw();
            return true;
        }

        /// <summary>
        /// Particles and highlights for a jump, from where the cursor was
        /// (the trail's current quad - mid-flight if it was still moving) to
        /// where it is.
        /// </summary>
        private void EmitVfx(Point[] target)
        {
            var state = State;
            if (state == null) return;
            var settings = VfxSettingsOf(state);
            if (settings.Modes == VfxModes.None) return;

            var vfx = EnsureVfx();
            if (vfx == null) return;

            var from = new Rect(_pos[0], _pos[2]);
            var to = new Rect(target[0], target[2]);
            vfx.Jump(from, to, settings);
        }

        /// <summary>
        /// Assigns each corner its animation length from how well it faces the
        /// direction of travel. Returns false for a move too small to show.
        /// </summary>
        private bool BeginMove(Point[] target)
        {
            var from = Center(_pos);
            var to = Center(target);
            var travel = to - from;
            if (travel.Length < 1) return false;
            travel.Normalize();

            var state = State;
            double length = (state?.CursorAnimationMs ?? 0) / 1000.0;
            double trail = Math.Max(0, Math.Min(1, (state?.CursorTrailPermille ?? 0) / 1000.0));
            if (length <= 0) return false;

            // Rank the destination corners by alignment with the travel
            // direction: rank 3 leads, rank 0 trails. Tied corners (both right
            // corners on a pure horizontal move) share the average of their
            // ranks, so a straight move smears as a clean stretched box rather
            // than skewing one corner ahead of its twin.
            var dots = new double[4];
            for (int i = 0; i < 4; i++)
            {
                var outward = target[i] - to;
                if (outward.Length > 0) outward.Normalize();
                dots[i] = Vector.Multiply(outward, travel);
            }
            for (int i = 0; i < 4; i++)
            {
                double rank = 0;
                for (int j = 0; j < 4; j++)
                {
                    if (j == i) continue;
                    if (dots[j] < dots[i] - 1e-6) rank += 1;
                    else if (Math.Abs(dots[j] - dots[i]) <= 1e-6) rank += 0.5;
                }

                // Leading corners arrive quickly, trailing ones take the full
                // length; trail_size 0 makes them all equal (a plain slide).
                _length[i] = Math.Max(0.01, length * (1 - trail * rank / 3.0));
            }
            return true;
        }

        /// <summary>
        /// Critically damped spring, solved exactly for the step - Neovide's
        /// cursor animation. Settles in roughly <paramref name="length"/>
        /// seconds with no overshoot, and a retarget mid-flight (holding j)
        /// carries the current velocity instead of restarting from rest.
        /// </summary>
        private static void Step(ref Point position, ref Vector velocity, Point target,
                                 double length, double dt)
        {
            double omega = 4.0 / length;
            var x = position - target;
            var b = velocity + omega * x;
            double decay = Math.Exp(-omega * dt);

            var nextX = (x + b * dt) * decay;
            velocity = (b - omega * (x + b * dt)) * decay;
            position = target + nextX;
        }

        private bool IsMoving()
        {
            for (int i = 0; i < 4; i++)
                if (_vel[i].Length > 2) return true;
            return false;
        }

        private void Snap(Point[] target)
        {
            for (int i = 0; i < 4; i++)
            {
                _pos[i] = target[i];
                _vel[i] = default;
            }
            _hasPosition = true;
            Settle(hide: true);
        }

        /// <summary>
        /// Stops the trail. Frames are the caller's call: particles may still
        /// need them (Frame stops once neither is active).
        /// </summary>
        private void Settle(bool hide)
        {
            if (hide && _shape != null) _shape.Visibility = Visibility.Collapsed;
            for (int i = 0; i < 4; i++) _vel[i] = default;
        }

        private void Draw()
        {
            var shape = EnsureShape();
            if (shape == null) return;

            // The layer works in view coordinates; the corners are viewport-
            // relative, so the viewport's own offset goes back on here.
            double left = _view.ViewportLeft;
            double top = _view.ViewportTop;
            var points = shape.Points;
            for (int i = 0; i < 4; i++)
                points[i] = new Point(_pos[i].X + left, _pos[i].Y + top);

            shape.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// The caret's rectangle as four viewport-relative corners, or false
        /// when the caret is not on a laid-out, visible line.
        /// </summary>
        private bool TryGetCaretCorners(out Point[] corners)
        {
            corners = _scratch;
            var caret = _view.Caret;

            ITextViewLine line;
            try { line = caret.ContainingTextViewLine; }
            catch (InvalidOperationException) { return false; }

            if (line == null || line.VisibilityState == VisibilityState.Unattached
                || line.VisibilityState == VisibilityState.Hidden)
                return false;

            double x = caret.Left - _view.ViewportLeft;
            double y = caret.Top - _view.ViewportTop;
            // The insert-mode caret is a thin bar; keep the trail at least
            // wide enough to read.
            double w = Math.Max(caret.Width, 2);
            double h = caret.Height;
            if (double.IsNaN(x) || double.IsNaN(y) || h <= 0) return false;

            corners[0] = new Point(x, y);
            corners[1] = new Point(x + w, y);
            corners[2] = new Point(x + w, y + h);
            corners[3] = new Point(x, y + h);
            return true;
        }

        private readonly Point[] _scratch = new Point[4];

        private static Point Center(Point[] corners) =>
            new Point((corners[0].X + corners[1].X + corners[2].X + corners[3].X) / 4,
                      (corners[0].Y + corners[1].Y + corners[2].Y + corners[3].Y) / 4);

        private Polygon? EnsureShape()
        {
            if (_shape != null) return _shape;

            _layer ??= _view.GetAdornmentLayer(LayerName);
            EnsureFormatMap();

            _shape = new Polygon
            {
                Points = new PointCollection(new Point[4]),
                Fill = CaretBrush(),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            _layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, _shape, null);
            return _shape;
        }

        /// <summary>The VFX layer, created on the first jump that uses it.</summary>
        private CursorVfx? EnsureVfx()
        {
            if (_vfx != null) return _vfx;

            _layer ??= _view.GetAdornmentLayer(LayerName);
            EnsureFormatMap();

            _vfx = new CursorVfx();
            _vfx.SetColor(CaretColor());
            PlaceVfx(_vfx);
            _layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, _vfx, null);
            return _vfx;
        }

        /// <summary>
        /// Keeps the VFX element on the viewport: its coordinates are
        /// viewport-relative, the layer's are view coordinates.
        /// </summary>
        private void PlaceVfx(CursorVfx vfx)
        {
            System.Windows.Controls.Canvas.SetLeft(vfx, _view.ViewportLeft);
            System.Windows.Controls.Canvas.SetTop(vfx, _view.ViewportTop);
            vfx.Width = Math.Max(1, _view.ViewportWidth);
            vfx.Height = Math.Max(1, _view.ViewportHeight);
        }

        private void EnsureFormatMap()
        {
            if (_formatMap != null) return;
            _formatMap = _formatMapService.GetEditorFormatMap(_view);
            _formatMap.FormatMappingChanged += OnFormatMappingChanged;
        }

        private void OnFormatMappingChanged(object sender, FormatItemsEventArgs e)
        {
            if (_shape != null) _shape.Fill = CaretBrush();
            _vfx?.SetColor(CaretColor());
        }

        /// <summary>
        /// The theme's caret color (Tools &gt; Options &gt; Fonts and Colors),
        /// translucent: the real caret stays the thing you read, the trail
        /// only shows where it came from.
        /// </summary>
        private Brush CaretBrush()
        {
            var color = CaretColor();
            var brush = new SolidColorBrush(Color.FromArgb(0x99, color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }

        private Color CaretColor()
        {
            Color color = Colors.Gray;
            try
            {
                var props = _formatMap?.GetProperties("Caret");
                if (props != null && props.Contains(EditorFormatDefinition.ForegroundColorId)
                    && props[EditorFormatDefinition.ForegroundColorId] is Color c)
                    color = c;
                else if (props != null && props.Contains(EditorFormatDefinition.ForegroundBrushId)
                    && props[EditorFormatDefinition.ForegroundBrushId] is SolidColorBrush b)
                    color = b.Color;
            }
            catch
            {
                // Gray is a fine trail; a format map hiccup is not worth more.
            }
            return color;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_closed) return;
            _closed = true;
            StopFrames();

            _view.Caret.PositionChanged -= OnCaretMoved;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.LostAggregateFocus -= OnLostFocus;
            _view.Closed -= OnClosed;
            if (_formatMap != null) _formatMap.FormatMappingChanged -= OnFormatMappingChanged;
        }
    }
}
