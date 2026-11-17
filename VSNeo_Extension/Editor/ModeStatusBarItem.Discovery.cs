using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace VSNeo_Extension.Editor;

// Finding the status bar row in the shell visual tree, and saying so when it cannot.
internal static partial class ModeStatusBarItem
{
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
            if (panel.ActualHeight <= 0 || panel.ActualHeight > 60)
            {
                continue;
            }

            if (panel.ActualWidth < window.ActualWidth * 0.8)
            {
                continue;
            }

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
        if (root is DockPanel panel)
        {
            found.Add(panel);
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            CollectDockPanels(VisualTreeHelper.GetChild(root, i), found);
        }
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
                {
                    sb.Append(" [").Append(fe.GetType().Name)
                      .Append(' ').Append((int)fe.ActualWidth)
                      .Append('x').Append((int)fe.ActualHeight)
                      .Append(" y=").Append((int)y).Append(']');
                }
            }
            catch (InvalidOperationException) { }
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            CollectBottomChrome(VisualTreeHelper.GetChild(root, i), window, sb);
        }
    }

    private static void CollectStatusBars(DependencyObject root, List<StatusBar> found)
    {
        if (root is StatusBar bar)
        {
            found.Add(bar);
            return;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            CollectStatusBars(VisualTreeHelper.GetChild(root, i), found);
        }
    }
}
