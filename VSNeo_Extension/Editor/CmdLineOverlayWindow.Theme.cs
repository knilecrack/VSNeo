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

// Colors: the editor theme, the accent, contrast, and the brush cache.
internal static partial class CmdLineOverlayWindow
{
    // Brushes alive only while their color signature holds (see Render);
    // rebuilt on a theme or cmdline-kind change, not per keystroke.
    private static (Color Surface, Color Accent, Color Text)? s_brushSignature;
    private static Brush? s_nameBrush;
    private static Brush? s_cursorBackground;
    private static Brush? s_cursorForeground;
    private static Brush? s_completionsBorder;
    private static Brush? s_completionText;
    private static Brush? s_selectedBackground;
    private static Brush? s_selectedForeground;

    /// <summary>vsneo_cursor_color's cmdline slot, or null when the user set none.</summary>
    private static Color? UserCmdLineColor(NvimStateHub state)
    {
        var colors = state.CursorColors;
        int rgb = colors != null && colors.Length > 5 ? colors[5] : -1;
        if (rgb < 0)
        {
            return null;
        }

        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>
    /// The active text view's background, or null when there is no usable
    /// view. The window has no view of its own, so the theme comes from
    /// whatever document is active at show time.
    /// </summary>
    private static Brush? ActiveViewBackground()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return GetActiveView()?.Background;
    }

    private static IWpfTextView? GetActiveView()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var textManager = Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager;
            if (textManager == null)
            {
                return null;
            }

            if (ErrorHandler.Failed(textManager.GetActiveView(0, null, out var vsView))
                || vsView == null)
            {
                return null;
            }

            var model = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            return model?.GetService<IVsEditorAdaptersFactoryService>()?.GetWpfTextView(vsView);
        }
        catch
        {
            // Cosmetic only; the fallback colors are perfectly readable.
            return null;
        }
    }

    /// <summary>Follow the editor's font so the overlay reads as part of the IDE.</summary>
    private static void ApplyEditorFont()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var view = GetActiveView();
            if (view == null)
            {
                return;
            }

            var model = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            var map = model?.GetService<IClassificationFormatMapService>()
                            ?.GetClassificationFormatMap(view);
            if (map == null)
            {
                return;
            }

            s_editorFont = map.DefaultTextProperties.Typeface.FontFamily;
            s_editorFontSize = map.DefaultTextProperties.FontRenderingEmSize;
            s_input.FontFamily = s_prompt.FontFamily = s_editorFont;
            s_input.FontSize = s_prompt.FontSize = s_editorFontSize;
        }
        catch
        {
            // Cosmetic only; the default font is perfectly readable.
        }
    }

    private static double Luminance(Color c) => (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);

    /// <summary><paramref name="a"/> moved toward <paramref name="b"/> by <paramref name="t"/> (0..1).</summary>
    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + ((b.R - a.R) * t)),
        (byte)Math.Round(a.G + ((b.G - a.G) * t)),
        (byte)Math.Round(a.B + ((b.B - a.B) * t)));

    /// <summary>Text color for a filled accent chip.</summary>
    private static Color OnAccent(Color accent) =>
        Luminance(accent) > 140 ? Color.FromRgb(0x16, 0x18, 0x1D) : Colors.White;

    // Frozen brushes by ARGB: a cmdline keystroke re-renders the popup,
    // and the colors only change with the theme and the kind, so a
    // handful of brushes serve every render.
    private static readonly Dictionary<uint, SolidColorBrush> SolidCache = new Dictionary<uint, SolidColorBrush>();

    private static SolidColorBrush Solid(Color color, double opacity = 1)
    {
        byte alpha = (byte)Math.Round(255 * Math.Max(0, Math.Min(1, opacity)));
        uint key = ((uint)alpha << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (SolidCache.TryGetValue(key, out var brush))
        {
            return brush;
        }

        if (SolidCache.Count > 256)
        {
            SolidCache.Clear();
        }

        brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        SolidCache[key] = brush;
        return brush;
    }
}
