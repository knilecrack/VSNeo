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
                    _subscribedTo.ModeChanged -= OnModeChanged;
                session.State.ModeChanged += OnModeChanged;
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
            }
        }

        private static void OnRetry(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (EnsureItem() || ++_attempts >= 20)
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
        /// Locates the shell's status bar and pins the badge to the left edge.
        /// False when the visual tree has no status bar yet; the caller retries.
        ///
        /// The shell chrome holds more than one StatusBar, and the one a plain
        /// tree walk reaches first is the right-hand cluster - inserting an item
        /// there parked the badge next to the git and encoding items. And the
        /// first DockPanel ancestor is an inner template panel that clips extra
        /// children, which hid the badge outright. So: pick the leftmost bar by
        /// screen position, walk up to the ancestor DockPanel that is actually
        /// the status-bar row (row height, window width), and dock the badge
        /// left inside it. Every step falls back to inserting an item into the
        /// leftmost bar - visible in the worst case, right-ish at worst - and
        /// every decision is logged, because shell chrome differs across VS
        /// versions and this file cannot see the user's screen.
        /// </summary>
        private static bool EnsureItem()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_inserted != null) return true;

            var window = Application.Current?.MainWindow;
            if (window == null) return false;

            var bars = new List<StatusBar>();
            CollectStatusBars(window, bars);
            if (bars.Count == 0)
            {
                Infrastructure.Log.Write("mode badge: no StatusBar in the visual tree yet");
                return false;
            }

            StatusBar leftmost = null!;
            double leftmostX = double.MaxValue;
            var survey = new System.Text.StringBuilder();
            foreach (var bar in bars)
            {
                try
                {
                    var p = bar.TransformToAncestor(window).Transform(new Point(0, 0));
                    survey.Append(" [").Append(bar.GetType().Name)
                          .Append(" x=").Append((int)p.X)
                          .Append(" y=").Append((int)p.Y)
                          .Append(" w=").Append((int)bar.ActualWidth)
                          .Append(']');
                    if (p.X < leftmostX)
                    {
                        leftmostX = p.X;
                        leftmost = bar;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Not yet arranged; a retry pass will see it placed.
                    survey.Append(" [").Append(bar.GetType().Name).Append(" unarranged]");
                }
            }
            if (leftmost == null) leftmost = bars[0];
            Infrastructure.Log.Write("mode badge: " + bars.Count + " status bar(s):" + survey);

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
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            try
            {
                var row = FindRowPanel(leftmost, window);
                if (row != null)
                {
                    DockPanel.SetDock(_badge, Dock.Left);
                    row.Children.Insert(0, _badge);
                    _inserted = _badge;
                    _host = row;
                    Infrastructure.Log.Write("mode badge: docked left into " + row.GetType().Name
                        + " (parent chain: " + DescribeChain(leftmost) + ")");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Infrastructure.Log.Write("mode badge: dock-panel insertion failed, falling back", ex);
                // The badge may already be parented if the throw came late;
                // the fallback below would then fail with "already a child".
                if (_badge.Parent is Panel stuck) stuck.Children.Remove(_badge);
            }

            var item = new StatusBarItem
            {
                Content = _badge,
                Padding = new Thickness(2, 0, 2, 0),
            };
            leftmost.Items.Insert(0, item);
            _inserted = item;
            _host = leftmost;
            Infrastructure.Log.Write("mode badge: inserted as item of "
                + leftmost.GetType().Name + " at x=" + (int)leftmostX);
            return true;
        }

        /// <summary>
        /// The ancestor of <paramref name="bar"/> that is the status-bar row: a
        /// DockPanel no taller than a row and spanning at least half the window.
        /// The height/width sieve is what keeps the choice off the inner
        /// template DockPanels (tiny, clip extra children) and off the main
        /// window's root DockPanel (full height; a left-docked child there would
        /// be a vertical strip down the whole window). Null when nothing fits.
        /// </summary>
        private static DockPanel FindRowPanel(StatusBar bar, Window window)
        {
            for (DependencyObject node = VisualTreeHelper.GetParent(bar);
                 node != null;
                 node = VisualTreeHelper.GetParent(node))
            {
                if (node is DockPanel dock
                    && dock.ActualHeight > 0 && dock.ActualHeight <= 60
                    && dock.ActualWidth >= window.ActualWidth * 0.5)
                    return dock;
            }
            return null!;
        }

        private static string DescribeChain(DependencyObject node)
        {
            var sb = new System.Text.StringBuilder();
            for (; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (sb.Length > 0) sb.Append(" < ");
                sb.Append(node.GetType().Name);
                if (node is FrameworkElement fe)
                    sb.Append('(').Append((int)fe.ActualWidth).Append('x').Append((int)fe.ActualHeight).Append(')');
            }
            return sb.ToString();
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

        private static void Render()
        {
            if (_text == null) return;

            var state = VSNeo_ExtensionPackage.Session?.State;
            if (state == null)
            {
                _text.Text = string.Empty;
                return;
            }

            _text.Text = ModeText(state.Mode, state.VisualKind);
            _badge.Background = BadgeBrush(state.Mode);
            _text.Foreground = ContrastBrush(state.Mode);
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
