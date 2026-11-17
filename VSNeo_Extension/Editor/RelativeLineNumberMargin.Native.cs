using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Editor;
using VSNeo_Extension.Infrastructure;

namespace VSNeo_Extension.Editor;

// The margin Visual Studio owns: hiding it when ours is on, and its colours.
internal sealed partial class RelativeLineNumberMargin
{
    // VsVim's trick: zero the stock margin's width before hiding it, and
    // put the numbers back exactly when ours steps aside. Without this a
    // user with VS line numbers on gets two margin columns.
    private double _nativeWidth;
    private double _nativeMinWidth;
    private double _nativeMaxWidth = double.PositiveInfinity;
    private bool _nativeHidden;

    private void SetNativeMarginVisibility(Visibility visibility)
    {
        try
        {
            var element = (_marginContainer?.GetTextViewMargin(PredefinedMarginNames.LineNumber)
                as IWpfTextViewMargin)?.VisualElement;
            if (element == null)
            {
                return;
            }

            if (visibility == Visibility.Hidden && element.Visibility == Visibility.Visible)
            {
                _nativeWidth = element.Width;
                _nativeMinWidth = element.MinWidth;
                _nativeMaxWidth = element.MaxWidth;
                element.Width = 0.0;
                element.MinWidth = 0.0;
                element.MaxWidth = 0.0;
                element.Visibility = Visibility.Hidden;
                _nativeHidden = true;
            }
            else if (visibility == Visibility.Visible && _nativeHidden)
            {
                element.Width = _nativeWidth;
                element.MinWidth = _nativeMinWidth;
                element.MaxWidth = _nativeMaxWidth;
                element.Visibility = Visibility.Visible;
                _nativeHidden = false;
            }
        }
        catch (Exception ex)
        {
            // Losing the toggle is cosmetic; never take the editor down over it.
            Log.Write("native line number margin toggle failed", ex);
        }
    }

    private void UpdateForeground()
    {
        var solid = _view.Background as SolidColorBrush;
        if (solid == null)
        {
            return;
        }

        var c = solid.Color;
        bool dark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
        _foreground = dark ? Brushes.Gray : Brushes.DimGray;
        _caretForeground = dark ? Colors.Gainsboro : Colors.Black;
    }

    // ----------------------------------------------------------------
    // Session/settings wiring, same pattern as SearchHighlightAdornment:
    // the view can be created before the package finishes loading.
    // ----------------------------------------------------------------
}
