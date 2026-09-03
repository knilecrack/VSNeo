using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// The permanent Vim-mode indicator in Visual Studio's status bar: a colored
    /// badge ("NORMAL", "INSERT", ...) docked to the far left, where Vim users
    /// look for the mode, and visible no matter which view has focus.
    ///
    /// IVsStatusbar has no concept of a permanent region: SetText writes into the
    /// transient feedback area, which the shell overwrites with its own "Ready"
    /// a heartbeat later. The durable approach is the one every extension with a
    /// status-bar presence converges on: the shell's status bar is ordinary WPF
    /// in the main window's visual tree, so we find it and add our own element.
    /// The transient SetText calls stay for the connect/fallback announcements
    /// they were made for.
    ///
    /// Driven by the hub's ModeChanged, not by msg_showmode: the redraw stream's
    /// "-- INSERT --" is a message that clears, while the pushed mode is the fact
    /// the key path itself routes on - the indicator cannot disagree with what
    /// the next keystroke will do.
    /// </summary>
    internal static class ModeStatusBarItem
    {
        private static Border _badge = null!;
        private static TextBlock _text = null!;
        private static Border _recBadge = null!;
        private static TextBlock _recText = null!;
        private static UIElement _inserted = null!;
        private static object _host = null!;
        private static NvimStateHub _subscribedTo = null!;
        private static DispatcherTimer _retry = null!;
        private static int _attempts;

        /// <summary>UI thread; the package posts here from ReadyChanged.</summary>
        public static void Attach(NvimSession session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!ReferenceEquals(_subscribedTo, session.State))
            {
                if (_subscribedTo != null)
                {
                    _subscribedTo.ModeChanged -= OnModeChanged;
                    _subscribedTo.RecordingChanged -= OnRecordingChanged;
                }
                session.State.ModeChanged += OnModeChanged;
                session.State.RecordingChanged += OnRecordingChanged;
                _subscribedTo = session.State;
            }

            if (EnsureItem())
            {
                _inserted.Visibility = Visibility.Visible;
                Render();
            }
            else
            {
                // The package background-loads, which can beat the shell's status
                // bar into existence. Retry briefly rather than never showing up.
                if (_retry == null)
                {
                    _attempts = 0;
                    _retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    _retry.Tick += OnRetry;
                    _retry.Start();
                }
            }
        }

        /// <summary>UI thread. Fallback (VS input) shows nothing: a mode readout
        /// while keystrokes bypass nvim would be a lie.</summary>
        public static void Hide()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_inserted != null) _inserted.Visibility = Visibility.Collapsed;
        }

        /// <summary>UI thread (package Dispose asserts it).</summary>
        public static void Detach()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_retry != null)
            {
                _retry.Stop();
                _retry = null!;
            }
            if (_subscribedTo != null)
            {
                _subscribedTo.ModeChanged -= OnModeChanged;
                _subscribedTo.RecordingChanged -= OnRecordingChanged;
                _subscribedTo = null!;
            }
            if (_inserted != null)
            {
                if (_host is StatusBar bar) bar.Items.Remove(_inserted);
                else if (_host is Panel panel) panel.Children.Remove(_inserted);
                _inserted = null!;
                _host = null!;
                _badge = null!;
                _text = null!;
                _recBadge = null!;
                _recText = null!;
            }
        }

        private static void OnRetry(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Two minutes, not ten seconds: on a cold start with a big
            // solution the whole bottom strip stays unarranged well past the
            // old 20-attempt window, and the badge never appeared at all.
            // Failures log every 20th attempt; the log already says the rest.
            if (EnsureItem(logFailure: _attempts % 20 == 0) || ++_attempts >= 240)
            {
                _retry.Stop();
                _retry = null!;
            }

            if (_inserted != null)
            {
                _inserted.Visibility = Visibility.Visible;
                Render();
            }
        }

        /// <summary>
        /// Locates the status-bar row and pins the badge to its left edge.
        /// False when the shell chrome is not arranged yet; the caller retries.
        ///
        /// The row is found by shape, not by control type: a full-width
        /// DockPanel no taller than a row, hugging the bottom of the main
        /// window. The earlier strategy anchored on the shell's StatusBar -
        /// which turned out to be SccStatusBar, the git-branch item, the only
        /// real StatusBar in the chrome and one that materializes only once a
        /// solution with source control finishes loading. Sessions that
        /// attached before that moment found no StatusBar at all and the badge
        /// never appeared. The row DockPanel, by contrast, exists from window
        /// creation; the 14:49 survey logged its chain as
        /// SccStatusBar &lt; ... &lt; DockPanel(2560x24) &lt; Grid &lt; MainWindow.
        ///
        /// Fallback, for a shell whose bottom row is not a DockPanel: an item
        /// inserted into the leftmost StatusBar, if one exists. Every decision
        /// is logged, because shell chrome differs across VS versions and this
        /// file cannot see the user's screen.
        /// </summary>
        private static bool EnsureItem(bool logFailure = true)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_inserted != null) return true;

            var window = Application.Current?.MainWindow;
            if (window == null) return false;

            _text = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _badge = new Border
            {
                Child = _text,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            // Macro recording rides beside the mode as its own red badge,
            // noice-style: the one piece of Vim state the mode cannot show.
            _recText = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = FrozenBrush(0xF5F5F5),
            };
            _recBadge = new Border
            {
                Child = _recText,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = FrozenBrush(0xB0392E),
                Visibility = Visibility.Collapsed,
            };
            var strip = new StackPanel { Orientation = Orientation.Horizontal };
            strip.Children.Add(_badge);
            strip.Children.Add(_recBadge);

            try
            {
                var row = FindStatusBarRow(window);
                if (row != null)
                {
                    DockPanel.SetDock(strip, Dock.Left);
                    row.Children.Insert(0, strip);
                    _inserted = strip;
                    _host = row;
                    Infrastructure.Log.Write("mode badge: docked left into the status-bar row ("
                        + (int)row.ActualWidth + "x" + (int)row.ActualHeight + ")");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Infrastructure.Log.Write("mode badge: row insertion failed, falling back", ex);
                // The strip may already be parented if the throw came late;
                // the fallback below would then fail with "already a child".
                if (strip.Parent is Panel stuck) stuck.Children.Remove(strip);
            }

            var bars = new List<StatusBar>();
            CollectStatusBars(window, bars);
            if (bars.Count == 0)
            {
                if (logFailure)
                    Infrastructure.Log.Write("mode badge: no row DockPanel and no StatusBar found: "
                        + DescribeBottomChrome(window));
                return false;
            }

            var leftmost = bars[0];
            double leftmostX = double.MaxValue;
            foreach (var bar in bars)
            {
                try
                {
                    var x = bar.TransformToAncestor(window).Transform(new Point(0, 0)).X;
                    if (x < leftmostX)
                    {
                        leftmostX = x;
                        leftmost = bar;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Not yet arranged; a retry pass will see it placed.
                }
            }

            var item = new StatusBarItem
            {
                Content = strip,
                Padding = new Thickness(2, 0, 2, 0),
            };
            leftmost.Items.Insert(0, item);
            _inserted = item;
            _host = leftmost;
            Infrastructure.Log.Write("mode badge: inserted as item of "
                + leftmost.GetType().Name + " (fallback)");
            return true;
        }

        /// <summary>
        /// The bottom status-bar row of the main window: a DockPanel spanning
        /// nearly the full window width, no taller than a row, sitting in the
        /// bottom fifth. The bottom-most matching panel wins. The shape sieve
        /// is what keeps the choice off inner template panels (narrow) and off
        /// the window's root DockPanel (full height - a left dock there would
        /// be a vertical strip down the whole window). Null when nothing fits.
        /// </summary>
        private static DockPanel FindStatusBarRow(Window window)
        {
            var panels = new List<DockPanel>();
            CollectDockPanels(window, panels);

            DockPanel best = null!;
            double bestY = -1;
            foreach (var panel in panels)
            {
                if (panel.ActualHeight <= 0 || panel.ActualHeight > 60) continue;
                if (panel.ActualWidth < window.ActualWidth * 0.8) continue;

                double y;
                try
                {
                    y = panel.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
                }
                catch (InvalidOperationException)
                {
                    continue; // not arranged yet
                }

                if (y > window.ActualHeight * 0.8 && y > bestY)
                {
                    bestY = y;
                    best = panel;
                }
            }
            return best;
        }

        private static void CollectDockPanels(DependencyObject root, List<DockPanel> found)
        {
            if (root is DockPanel panel) found.Add(panel);

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                CollectDockPanels(VisualTreeHelper.GetChild(root, i), found);
        }

        /// <summary>What the bottom strip actually contains, for the next debugging round.</summary>
        private static string DescribeBottomChrome(Window window)
        {
            var sb = new System.Text.StringBuilder();
            CollectBottomChrome(window, window, sb);
            return sb.Length == 0 ? "nothing arranged in the bottom strip" : sb.ToString();
        }

        private static void CollectBottomChrome(DependencyObject root, Window window,
            System.Text.StringBuilder sb)
        {
            if (root is FrameworkElement fe && fe.ActualHeight > 0 && fe.ActualHeight <= 60
                && fe.ActualWidth >= window.ActualWidth * 0.5)
            {
                try
                {
                    var y = fe.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
                    if (y > window.ActualHeight * 0.8)
                        sb.Append(" [").Append(fe.GetType().Name)
                          .Append(' ').Append((int)fe.ActualWidth)
                          .Append('x').Append((int)fe.ActualHeight)
                          .Append(" y=").Append((int)y).Append(']');
                }
                catch (InvalidOperationException) { }
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                CollectBottomChrome(VisualTreeHelper.GetChild(root, i), window, sb);
        }

        private static void CollectStatusBars(DependencyObject root, List<StatusBar> found)
        {
            if (root is StatusBar bar)
            {
                found.Add(bar);
                return;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                CollectStatusBars(VisualTreeHelper.GetChild(root, i), found);
        }

        /// <summary>Called on the RPC read thread.</summary>
        private static void OnModeChanged(VimMode mode)
        {
            var dispatcher = _inserted?.Dispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            // Fire-and-forget: Render reads the hub state itself and has nothing
            // to report. Normal priority, not Input - this display is not the
            // keystroke response, the hub's cached mode already was.
            _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Render));
#pragma warning restore VSTHRD001
        }

        /// <summary>Called on the RPC read thread.</summary>
        private static void OnRecordingChanged()
        {
            var dispatcher = _inserted?.Dispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            // Fire-and-forget, like OnModeChanged: Render re-reads the hub.
            _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Render));
#pragma warning restore VSTHRD001
        }

        private static void Render()
        {
            if (_text == null) return;

            var state = VSNeo_ExtensionPackage.Session?.State;
            if (state == null)
            {
                _text.Text = string.Empty;
                _recBadge.Visibility = Visibility.Collapsed;
                return;
            }

            _text.Text = ModeText(state.Mode, state.VisualKind);
            _badge.Background = BadgeBrush(state.Mode);
            _text.Foreground = ContrastBrush(state.Mode);

            var reg = state.RecordingReg;
            if (reg == null)
            {
                _recBadge.Visibility = Visibility.Collapsed;
            }
            else
            {
                _recText.Text = "REC @" + reg;
                _recBadge.Visibility = Visibility.Visible;
            }
        }

        private static string ModeText(VimMode mode, char visualKind)
        {
            switch (mode)
            {
                case VimMode.Normal: return "NORMAL";
                case VimMode.Insert: return "INSERT";
                case VimMode.Replace: return "REPLACE";
                case VimMode.Visual:
                    // The hub's collapsed Visual carries the flavour separately;
                    // Vim draws all three and there is no reason to know less here.
                    if (visualKind == 'V') return "VISUAL LINE";
                    if (visualKind == '\x16') return "VISUAL BLOCK";
                    return "VISUAL";
                case VimMode.CmdLine: return "COMMAND";
                case VimMode.OperatorPending: return "OP-PENDING";
                case VimMode.Terminal: return "TERMINAL";
                default: return string.Empty;
            }
        }

        /// <summary>
        /// Badge colors in the Vim statusline tradition: green for normal, blue
        /// for insert, purple for visual, red for replace, amber for the command
        /// line. The palette is deliberately not taken from nvim highlight groups:
        /// 'ModeMsg' is one group for every mode, so it cannot tell them apart,
        /// and per-mode colors are what the eye navigates by.
        /// </summary>
        private static SolidColorBrush BadgeBrush(VimMode mode)
        {
            uint rgb;
            switch (mode)
            {
                case VimMode.Normal: rgb = 0x5B8C3E; break;
                case VimMode.Insert: rgb = 0x2F6FBF; break;
                case VimMode.Visual: rgb = 0x8A4FA3; break;
                case VimMode.Replace: rgb = 0xB0392E; break;
                case VimMode.CmdLine: rgb = 0xB07D1E; break;
                case VimMode.OperatorPending: rgb = 0x556070; break;
                case VimMode.Terminal: rgb = 0x2E8B8B; break;
                default: return System.Windows.Media.Brushes.Transparent;
            }

            return FrozenBrush(rgb);
        }

        /// <summary>White on the dark badges, near-black on the bright ones.</summary>
        private static SolidColorBrush ContrastBrush(VimMode mode)
        {
            switch (mode)
            {
                case VimMode.Normal:
                case VimMode.CmdLine:
                    return FrozenBrush(0x1E1E1E);
                default:
                    return FrozenBrush(0xF5F5F5);
            }
        }

        private static SolidColorBrush FrozenBrush(uint rgb)
        {
            var brush = new SolidColorBrush(Color.FromRgb(
                (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            return brush;
        }
    }
}
