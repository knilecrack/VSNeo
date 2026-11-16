using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// The completion popup under the command line: cells, rows, selection.
internal static partial class CmdLineOverlayWindow
{
    private const int MaxCompletionRows = 10;

    private static readonly List<Border> CompletionCells = new List<Border>();

    /// <summary>
    /// The wildmenu, capped at a window of rows around the selection - a
    /// long completion list (:e **/foo&lt;Tab&gt;) must not cover the IDE.
    /// The selection is an accent-tinted row; the counter chip on the
    /// border says where in the whole list it is.
    /// </summary>
    private static void RenderCompletions(NvimStateHub state)
    {
        var words = state.CompletionWords;
        if (words == null || words.Count == 0)
        {
            s_completionsHost.Visibility = Visibility.Collapsed;
            s_counterChip.Visibility = Visibility.Collapsed;
            return;
        }

        s_completionsHost.Visibility = Visibility.Visible;
        s_completionsHost.BorderBrush = s_completionsBorder;

        int selected = state.CompletionSelected;
        s_counter.Text = selected >= 0
            ? (selected + 1) + "/" + words.Count
            : words.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        s_counterChip.Visibility = Visibility.Visible;

        int first = 0;
        if (selected >= MaxCompletionRows)
        {
            first = selected - MaxCompletionRows + 1;
        }

        int last = Math.Min(words.Count, first + MaxCompletionRows);

        var normal = s_completionText;
        int index = 0;
        for (int i = first; i < last; i++, index++)
        {
            var cell = RentCompletionCell(index);
            var row = (TextBlock)cell.Child;
            row.Text = words[i];
            if (s_editorFont != null)
            {
                row.FontFamily = s_editorFont;
                row.FontSize = s_editorFontSize;
            }

            if (i == selected)
            {
                cell.Background = s_selectedBackground;
                row.Foreground = s_selectedForeground;
                row.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                row.Foreground = normal;
            }
        }

        // Rows past the window keep their cells but not their visibility.
        for (int i = index; i < CompletionCells.Count; i++)
        {
            CompletionCells[i].Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The next pooled wildmenu row, reset to the unselected style;
    /// created up to the window cap once and parented for good.</summary>
    private static Border RentCompletionCell(int index)
    {
        if (index == CompletionCells.Count)
        {
            var cell = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 1, 8, 1),
                Child = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis },
            };
            CompletionCells.Add(cell);
            s_completions.Children.Add(cell);
        }

        var rented = CompletionCells[index];
        rented.ClearValue(Border.BackgroundProperty);
        rented.Visibility = Visibility.Visible;
        ((TextBlock)rented.Child).ClearValue(TextElement.FontWeightProperty);
        return rented;
    }
}
