using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

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
internal static partial class ModeStatusBarItem
{
    private static Border s_badge = null!;
    private static TextBlock s_text = null!;
    private static Border s_recBadge = null!;
    private static TextBlock s_recText = null!;
    private static UIElement s_inserted = null!;
    private static object s_host = null!;
    private static NvimStateHub s_subscribedTo = null!;
    private static DispatcherTimer s_retry = null!;
    private static int s_attempts;

    /// <summary>UI thread; the package posts here from ReadyChanged.</summary>
    public static void Attach(NvimSession session)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!ReferenceEquals(s_subscribedTo, session.State))
        {
            if (s_subscribedTo != null)
            {
                s_subscribedTo.ModeChanged -= OnModeChanged;
                s_subscribedTo.RecordingChanged -= OnRecordingChanged;
            }

            session.State.ModeChanged += OnModeChanged;
            session.State.RecordingChanged += OnRecordingChanged;
            s_subscribedTo = session.State;
        }

        if (EnsureItem())
        {
            s_inserted.Visibility = Visibility.Visible;
            Render();
        }
        else
        {
            // The package background-loads, which can beat the shell's status
            // bar into existence. Retry briefly rather than never showing up.
            if (s_retry == null)
            {
                s_attempts = 0;
                s_retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                s_retry.Tick += OnRetry;
                s_retry.Start();
            }
        }
    }

    /// <summary>UI thread. Fallback (VS input) shows nothing: a mode readout
    /// while keystrokes bypass nvim would be a lie.</summary>
    public static void Hide()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (s_inserted != null)
        {
            s_inserted.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>UI thread (package Dispose asserts it).</summary>
    public static void Detach()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (s_retry != null)
        {
            s_retry.Stop();
            s_retry = null!;
        }

        if (s_subscribedTo != null)
        {
            s_subscribedTo.ModeChanged -= OnModeChanged;
            s_subscribedTo.RecordingChanged -= OnRecordingChanged;
            s_subscribedTo = null!;
        }

        if (s_inserted != null)
        {
            if (s_host is StatusBar bar)
            {
                bar.Items.Remove(s_inserted);
            }
            else if (s_host is Panel panel)
            {
                panel.Children.Remove(s_inserted);
            }

            s_inserted = null!;
            s_host = null!;
            s_badge = null!;
            s_text = null!;
            s_recBadge = null!;
            s_recText = null!;
        }
    }

    private static void OnRetry(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // Two minutes, not ten seconds: on a cold start with a big
        // solution the whole bottom strip stays unarranged well past the
        // old 20-attempt window, and the badge never appeared at all.
        // Failures log every 20th attempt; the log already says the rest.
        if (EnsureItem(logFailure: s_attempts % 20 == 0) || ++s_attempts >= 240)
        {
            s_retry.Stop();
            s_retry = null!;
        }

        if (s_inserted != null)
        {
            s_inserted.Visibility = Visibility.Visible;
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
        if (s_inserted != null)
        {
            return true;
        }

        var window = Application.Current?.MainWindow;
        if (window == null)
        {
            return false;
        }

        s_text = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        s_badge = new Border
        {
            Child = s_text,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Click to enable or disable VSNeo",
        };
        s_badge.MouseLeftButtonUp += (_, _) => VSNeo_ExtensionPackage.ToggleEnabled();
        // Macro recording rides beside the mode as its own red badge,
        // noice-style: the one piece of Vim state the mode cannot show.
        s_recText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = FrozenBrush(0xF5F5F5),
        };
        s_recBadge = new Border
        {
            Child = s_recText,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = FrozenBrush(0xB0392E),
            Visibility = Visibility.Collapsed,
        };
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        strip.Children.Add(s_badge);
        strip.Children.Add(s_recBadge);

        try
        {
            var row = FindStatusBarRow(window);
            if (row != null)
            {
                DockPanel.SetDock(strip, Dock.Left);
                row.Children.Insert(0, strip);
                s_inserted = strip;
                s_host = row;
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
            if (strip.Parent is Panel stuck)
            {
                stuck.Children.Remove(strip);
            }
        }

        var bars = new List<StatusBar>();
        CollectStatusBars(window, bars);
        if (bars.Count == 0)
        {
            if (logFailure)
            {
                Infrastructure.Log.Write("mode badge: no row DockPanel and no StatusBar found: "
                    + DescribeBottomChrome(window));
            }

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
        s_inserted = item;
        s_host = leftmost;
        Infrastructure.Log.Write("mode badge: inserted as item of "
            + leftmost.GetType().Name + " (fallback)");
        return true;
    }

    /// <summary>Called on the RPC read thread.</summary>
    private static void OnModeChanged(VimMode mode)
    {
        var dispatcher = s_inserted?.Dispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

#pragma warning disable VSTHRD001
        // Fire-and-forget: Render reads the hub state itself and has nothing
        // to report. Decoration priority: this display is not the keystroke
        // response, the hub's cached mode already was. (Normal sits *above*
        // Render in WPF's order, so the badge used to jump ahead of layout.)
        _ = dispatcher.BeginInvoke(Infrastructure.UiPriority.Decoration, new Action(Render));
#pragma warning restore VSTHRD001
    }

    /// <summary>Called on the RPC read thread.</summary>
    private static void OnRecordingChanged()
    {
        var dispatcher = s_inserted?.Dispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

#pragma warning disable VSTHRD001
        // Fire-and-forget, like OnModeChanged: Render re-reads the hub.
        _ = dispatcher.BeginInvoke(Infrastructure.UiPriority.Decoration, new Action(Render));
#pragma warning restore VSTHRD001
    }
}
