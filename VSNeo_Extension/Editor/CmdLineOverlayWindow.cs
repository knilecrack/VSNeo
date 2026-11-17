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

/// <summary>
/// Vim's command line as a shell-level floating window - the Ctrl+Q
/// (feature search) shape: top-center of the Visual Studio window, outside
/// any text view. Covers / and ? as well as :, so search gets the popup
/// and the live match highlights at the same time.
///
/// One instance per session, not per view: the cmdline is global state in
/// nvim, and the old per-view adornment popup paid one dispatcher hop per
/// open document per cmdline keystroke to draw in exactly one of them.
///
/// Display-only by construction: the window is non-activatable
/// (WS_EX_NOACTIVATE) and click-through, so keyboard focus never leaves
/// the editor and every keystroke keeps flowing through the command
/// filter to nvim exactly as before. Everything shown is hub state:
/// ext_cmdline for the input, ext_popupmenu for the wildmenu.
///
/// Styled after noice.nvim's cmdline popup: a title chip on the border
/// names what is being typed (Cmdline, Search, Lua, Substitute, ...), and
/// the border, prompt, command name and cursor take that kind's accent
/// color, so a search never reads as a command. The surface is lifted a
/// little off the editor's background, and the wildmenu is a list with
/// its selection highlighted and an n/total counter on the border.
/// </summary>
internal static partial class CmdLineOverlayWindow
{
    // All UI-thread only. The hub events arrive on the RPC read thread and
    // marshal in through the window's dispatcher.
    private static Window? s_window;
    private static TextBlock s_prompt = null!;
    private static TextBlock s_input = null!;
    private static StackPanel s_completions = null!;
    private static Border s_completionsHost = null!;
    private static Border s_popup = null!;
    private static Border s_titleChip = null!;
    private static TextBlock s_title = null!;
    private static Border s_counterChip = null!;
    private static TextBlock s_counter = null!;
    private static FontFamily? s_editorFont;
    private static double s_editorFontSize;
    private static bool s_visible;

    private static NvimStateHub? s_subscribedTo;

    /// <summary>UI thread. Idempotent; the session never restarts in practice.</summary>
    public static void Attach(NvimSession session)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (session == null)
        {
            return;
        }

        if (!ReferenceEquals(s_subscribedTo, session.State))
        {
            if (s_subscribedTo != null)
            {
                s_subscribedTo.CmdLineChanged -= OnCmdLineChanged;
                s_subscribedTo.CompletionsChanged -= OnCompletionsChanged;
            }

            session.State.CmdLineChanged += OnCmdLineChanged;
            session.State.CompletionsChanged += OnCompletionsChanged;
            s_subscribedTo = session.State;
        }

        BeginRender();
    }

    /// <summary>UI thread (package Dispose).</summary>
    public static void Detach()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (s_subscribedTo != null)
        {
            s_subscribedTo.CmdLineChanged -= OnCmdLineChanged;
            s_subscribedTo.CompletionsChanged -= OnCompletionsChanged;
            s_subscribedTo = null;
        }

        Hide();
        if (s_window != null)
        {
            s_window.Close();
            s_window = null;
        }
    }

    /// <summary>Called on the RPC read thread.</summary>
    private static void OnCmdLineChanged(string content) => BeginRender();

    /// <summary>Called on the RPC read thread.</summary>
    private static void OnCompletionsChanged()
    {
        if (!s_visible)
        {
            return;
        }

        BeginRender();
    }

    private static int s_renderPending;
    private static Action? s_renderAction;

    /// <summary>
    /// One queued render at a time, shared by both events: holding Backspace
    /// on a long :command emits a change per character, and each render
    /// rebuilds the input's inline runs - only the last content can be
    /// visible. The delegate is allocated once.
    /// </summary>
    private static void BeginRender()
    {
        var dispatcher = s_window?.Dispatcher
            ?? System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        if (System.Threading.Interlocked.Exchange(ref s_renderPending, 1) == 1)
        {
            return;
        }

#pragma warning disable VSTHRD001
        // Fire-and-forget: the render is idempotent hub-state replay, and
        // the popup is the direct visual answer to a keystroke, so it goes
        // at Input priority like every other keystroke response.
        var render = s_renderAction ??= new Action(RenderPosted);
        _ = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, render);
#pragma warning restore VSTHRD001
    }

    // A named method rather than a lambda: the analyzer reads an assertion
    // inside a lambda as the enclosing method's, which made BeginRender
    // UI-thread-only and flagged its RPC-thread callers.
    private static void RenderPosted()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        System.Threading.Volatile.Write(ref s_renderPending, 0);
        Render();
    }

    private static void Render()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var state = VSNeo_ExtensionPackage.Session?.State;
            if (!VSNeoSettings.Enabled || state == null || state.CmdLine == null)
            {
                Hide();
                return;
            }

            EnsureWindow();

            var editor = (ActiveViewBackground() as SolidColorBrush)?.Color ?? Colors.White;
            bool dark = Luminance(editor) < 128;
            var text = dark ? Color.FromRgb(0xE4, 0xE6, 0xEB) : Color.FromRgb(0x1F, 0x23, 0x28);
            // Lifted off the editor a touch, so the popup reads as a
            // surface above the text rather than a hole in it.
            var surface = Mix(editor, text, dark ? 0.07 : 0.04);

            var kind = Classify(state.CmdLinePrefix ?? string.Empty, state.CmdLine ?? string.Empty);
            // The user's vsneo_cursor_color for cmdline mode, when set (a
            // table without a cmdline entry follows normal's), themes every
            // kind, so a preset carries into the command line; the chip
            // still names the kind. Otherwise each kind has its own hue.
            var hue = UserCmdLineColor(state) ?? kind.Accent;
            // Tuned for dark themes; on a light one the same hues are
            // darkened so the prompt and command name stay legible.
            var accent = dark ? hue : Mix(hue, Colors.Black, 0.35);

            // Brushes change only with the theme or the cmdline kind, not
            // per keystroke; while the signature holds, the controls keep
            // the brushes they already have and none are (re)allocated.
            var signature = (surface, accent, text);
            if (signature != s_brushSignature)
            {
                s_brushSignature = signature;
                s_popup.Background = Solid(surface);
                s_popup.BorderBrush = Solid(accent, 0.75);
                s_titleChip.Background = Solid(accent);
                s_title.Foreground = Solid(OnAccent(accent));
                s_counterChip.Background = Solid(surface);
                s_counterChip.BorderBrush = Solid(accent, 0.75);
                s_counter.Foreground = Solid(text, 0.7);
                s_prompt.Foreground = Solid(accent);
                s_input.Foreground = Solid(text);
                s_nameBrush = Solid(accent);
                s_cursorBackground = Solid(accent);
                s_cursorForeground = Solid(surface);
                s_completionsBorder = Solid(accent, 0.25);
                s_completionText = Solid(text, 0.85);
                s_selectedBackground = Solid(accent, 0.28);
                s_selectedForeground = Solid(text);
            }

            s_title.Text = kind.Title;

            RenderInput(state, kind);
            RenderCompletions(state);

            Show();
        }
        catch (Exception ex)
        {
            // A cosmetic overlay must never take the editor down with it.
            Log.Write("cmdline overlay render failed", ex);
        }
    }

    private static void EnsureWindow()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (s_window != null)
        {
            return;
        }

        // Everything below is rebuilt from scratch: pooled runs and cells
        // belong to the previous window's controls and cannot re-parent,
        // and a held brush signature must not suppress styling the new ones.
        InputRuns.Clear();
        CompletionCells.Clear();
        s_brushSignature = null;

        // Prompt (":", "/", an input() prompt) and the text, side by side.
        s_prompt = new TextBlock
        {
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        s_input = new TextBlock { TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        var inputRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(s_prompt, Dock.Left);
        inputRow.Children.Add(s_prompt);
        inputRow.Children.Add(s_input);

        s_completions = new StackPanel();
        s_completionsHost = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(-4, 8, -4, 0),
            Padding = new Thickness(0, 6, 0, 0),
            Child = s_completions,
            Visibility = Visibility.Collapsed,
        };

        s_popup = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 10),
            BorderThickness = new Thickness(1.5),
            // Room above for the title chip, which sits across the border.
            Margin = new Thickness(0, 10, 0, 0),
            Child = new StackPanel { Children = { inputRow, s_completionsHost } },
            Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 3,
                Opacity = 0.45,
            },
        };

        s_title = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
        s_titleChip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 1, 7, 2),
            Margin = new Thickness(16, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = s_title,
        };

        s_counter = new TextBlock { FontSize = 11 };
        s_counterChip = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 0, 6, 1),
            Margin = new Thickness(0, 1, 16, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = s_counter,
            Visibility = Visibility.Collapsed,
        };

        var root = new Grid
        {
            Children = { s_popup, s_titleChip, s_counterChip },
            // Display-only: clicks fall through to the editor underneath.
            IsHitTestVisible = false,
            // The shadow needs room inside the window, or it is clipped.
            Margin = new Thickness(0, 0, 0, 12),
        };

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            SizeToContent = SizeToContent.Height,
            Content = root,
        };

        // Owned by the shell's main window: correct z-order above Visual
        // Studio (but not above other applications) and it dies with the
        // shell. GetGlobalService needs the UI thread, which this is.
        var interop = new WindowInteropHelper(window);
        var shell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
        if (shell != null
            && ErrorHandler.Succeeded(shell.GetDialogOwnerHwnd(out var owner))
            && owner != IntPtr.Zero)
        {
            interop.Owner = owner;
        }

        // Without NOACTIVATE the first Show would pull keyboard focus out
        // of the editor and the next cmdline keystroke would go nowhere.
        window.SourceInitialized += (s, e) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExNoActivate));
        };

        ApplyEditorFont();

        // If anything but Detach closes the window, forget it: otherwise
        // s_window would keep referencing a closed Window, and the next
        // Show() would throw per cmdline keystroke until VS restarts.
        window.Closed += (s, e) =>
        {
            if (ReferenceEquals(s_window, window))
            {
                s_window = null;
                // A rebuilt window starts hidden; a stale true would make
                // Show() skip its window.Show() for the rest of the session.
                s_visible = false;
            }
        };

        s_window = window;
    }

    /// <summary>
    /// Top-center of the Visual Studio main window, a little below the
    /// title bar - where Ctrl+Q's popup sits. Recomputed per show; if the
    /// shell is dragged mid-cmdline the window catches up on the next one.
    /// </summary>
    private static void Show()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var window = s_window;
        if (window == null)
        {
            return;
        }

        var owner = new WindowInteropHelper(window).Owner;
        if (owner != IntPtr.Zero && GetWindowRect(owner, out var rect))
        {
            // GetWindowRect reports physical pixels; WPF positions windows in
            // device-independent units (1/96"). On a scaled display the two
            // differ by the DPI factor, and unconverted the window lands
            // off-center and oversized.
            double scale = GetDpiForWindow(owner) / 96.0;
            if (scale <= 0)
            {
                scale = 1;
            }

            double ownerWidth = (rect.Right - rect.Left) / scale;
            double ownerHeight = (rect.Bottom - rect.Top) / scale;

            double width = Math.Min(640, Math.Max(360, ownerWidth * 0.5));
            window.Width = width;
            s_popup.MaxHeight = ownerHeight * 0.6;

            window.Left = (rect.Left / scale) + ((ownerWidth - width) / 2);
            window.Top = (rect.Top / scale) + Math.Max(0, ownerHeight * 0.12);
        }

        if (!s_visible)
        {
            window.Show();
            s_visible = true;
        }
    }

    /// <summary>
    /// UI thread. Also called by the package when the session goes down:
    /// the hub never clears CmdLine on disconnect, so a popup open when
    /// nvim died would otherwise float for the rest of the session.
    /// </summary>
    public static void Hide()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!s_visible)
        {
            return;
        }

        s_window?.Hide();
        s_visible = false;
    }
}
