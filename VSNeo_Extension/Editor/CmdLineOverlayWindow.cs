using System;
using System.Runtime.InteropServices;
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

namespace VSNeo_Extension.Editor
{
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
    /// </summary>
    internal static class CmdLineOverlayWindow
    {
        private const int MaxCompletionRows = 10;

        // All UI-thread only. The hub events arrive on the RPC read thread and
        // marshal in through the window's dispatcher.
        private static Window? _window;
        private static TextBlock _input = null!;
        private static StackPanel _completions = null!;
        private static Border _popup = null!;
        private static bool _visible;

        private static NvimStateHub? _subscribedTo;

        /// <summary>UI thread. Idempotent; the session never restarts in practice.</summary>
        public static void Attach(NvimSession session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (session == null || ReferenceEquals(_subscribedTo, session.State)) return;

            if (_subscribedTo != null)
            {
                _subscribedTo.CmdLineChanged -= OnCmdLineChanged;
                _subscribedTo.CompletionsChanged -= OnCompletionsChanged;
            }
            session.State.CmdLineChanged += OnCmdLineChanged;
            session.State.CompletionsChanged += OnCompletionsChanged;
            _subscribedTo = session.State;
        }

        /// <summary>UI thread (package Dispose).</summary>
        public static void Detach()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_subscribedTo != null)
            {
                _subscribedTo.CmdLineChanged -= OnCmdLineChanged;
                _subscribedTo.CompletionsChanged -= OnCompletionsChanged;
                _subscribedTo = null;
            }
            Hide();
            if (_window != null)
            {
                _window.Close();
                _window = null;
            }
        }

        /// <summary>Called on the RPC read thread.</summary>
        private static void OnCmdLineChanged(string content)
        {
            var dispatcher = _window?.Dispatcher
                ?? System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;

#pragma warning disable VSTHRD001
            // Fire-and-forget: the render is idempotent hub-state replay, and
            // the popup is the direct visual answer to a keystroke, so it goes
            // at Input priority like every other keystroke response.
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    Render();
                }));
#pragma warning restore VSTHRD001
        }

        /// <summary>Called on the RPC read thread.</summary>
        private static void OnCompletionsChanged()
        {
            var dispatcher = _window?.Dispatcher;
            if (dispatcher == null || !_visible) return;

#pragma warning disable VSTHRD001
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    Render();
                }));
#pragma warning restore VSTHRD001
        }

        private static void Render()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var state = VSNeo_ExtensionPackage.Session?.State;
                if (state == null || state.CmdLine == null)
                {
                    Hide();
                    return;
                }

                EnsureWindow();

                var background = ActiveViewBackground() ?? Brushes.White;
                var foreground = Inverted(background);

                _popup.Background = background;
                _popup.BorderBrush = CachedBorderBrush(foreground);
                _input.Foreground = foreground;

                RenderInput(state, foreground);
                RenderCompletions(state, background, foreground);

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
            if (_window != null) return;

            _input = new TextBlock { TextWrapping = TextWrapping.NoWrap };
            _completions = new StackPanel();

            _popup = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Children = { _input, _completions },
                },
                Effect = new DropShadowEffect
                {
                    BlurRadius = 12,
                    ShadowDepth = 2,
                    Opacity = 0.4,
                },
                // Display-only: clicks fall through to the editor underneath.
                IsHitTestVisible = false,
            };

            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                SizeToContent = SizeToContent.Height,
                Content = _popup,
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
                if (hwnd == IntPtr.Zero) return;
                long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
                SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExNoActivate));
            };

            ApplyEditorFont();

            // If anything but Detach closes the window, forget it: otherwise
            // _window would keep referencing a closed Window, and the next
            // Show() would throw per cmdline keystroke until VS restarts.
            window.Closed += (s, e) =>
            {
                if (ReferenceEquals(_window, window)) _window = null;
            };

            _window = window;
        }

        private const int GwlExStyle = -20;
        private const long WsExNoActivate = 0x08000000;

        // The *Ptr pair exists on x64, which is the only place Visual Studio
        // 2022+ runs; there is no 32-bit fallback to worry about.
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>
        /// Top-center of the Visual Studio main window, a little below the
        /// title bar - where Ctrl+Q's popup sits. Recomputed per show; if the
        /// shell is dragged mid-cmdline the window catches up on the next one.
        /// </summary>
        private static void Show()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = _window;
            if (window == null) return;

            var owner = new WindowInteropHelper(window).Owner;
            if (owner != IntPtr.Zero && GetWindowRect(owner, out var rect))
            {
                // GetWindowRect reports physical pixels; WPF positions windows in
                // device-independent units (1/96"). On a scaled display the two
                // differ by the DPI factor, and unconverted the window lands
                // off-center and oversized.
                double scale = GetDpiForWindow(owner) / 96.0;
                if (scale <= 0) scale = 1;

                double ownerWidth = (rect.Right - rect.Left) / scale;
                double ownerHeight = (rect.Bottom - rect.Top) / scale;

                double width = Math.Min(600, Math.Max(200, ownerWidth * 0.5));
                window.Width = width;
                _popup.MaxHeight = ownerHeight * 0.6;

                window.Left = rect.Left / scale + (ownerWidth - width) / 2;
                window.Top = rect.Top / scale + Math.Max(0, ownerHeight * 0.12);
            }

            if (!_visible)
            {
                window.Show();
                _visible = true;
            }
        }

        private static void Hide()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_visible) return;
            _window?.Hide();
            _visible = false;
        }

        /// <summary>
        /// Prompt, text, and a block cursor sitting on a character rather than
        /// between two of them - Vim's convention. Without it a long
        /// substitution is edited blind.
        /// </summary>
        private static void RenderInput(NvimStateHub state, Brush foreground)
        {
            var content = state.CmdLine ?? string.Empty;
            int cursor = ColumnMapper.ByteToChar(content, state.CmdLinePos);
            if (cursor < 0) cursor = 0;
            if (cursor > content.Length) cursor = content.Length;

            _input.Inlines.Clear();
            _input.Inlines.Add(new Run((state.CmdLinePrefix ?? ":") + content.Substring(0, cursor)));

            // Past the end of the line the cursor has no character to sit on,
            // so it gets a space to occupy instead.
            var under = cursor < content.Length ? content.Substring(cursor, 1) : " ";
            _input.Inlines.Add(new Run(under)
            {
                Background = foreground,
                Foreground = _popup.Background,
            });

            if (cursor + 1 < content.Length)
                _input.Inlines.Add(new Run(content.Substring(cursor + 1)));
        }

        /// <summary>
        /// The wildmenu, capped at a window of rows around the selection - a
        /// long completion list (:e **/foo&lt;Tab&gt;) must not cover the IDE.
        /// </summary>
        private static void RenderCompletions(NvimStateHub state, Brush background, Brush foreground)
        {
            _completions.Children.Clear();

            var words = state.CompletionWords;
            if (words == null || words.Count == 0) return;

            int selected = state.CompletionSelected;
            int first = 0;
            if (selected >= MaxCompletionRows) first = selected - MaxCompletionRows + 1;
            int last = Math.Min(words.Count, first + MaxCompletionRows);

            for (int i = first; i < last; i++)
            {
                var row = new TextBlock
                {
                    Text = words[i],
                    Padding = new Thickness(2, 0, 2, 0),
                    Foreground = foreground,
                };
                if (i == selected)
                {
                    row.Background = foreground;
                    row.Foreground = background;
                }
                _completions.Children.Add(row);
            }
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
                if (textManager == null) return null;
                if (ErrorHandler.Failed(textManager.GetActiveView(0, null, out var vsView))
                    || vsView == null)
                    return null;

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
                if (view == null) return;
                var model = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                var map = model?.GetService<IClassificationFormatMapService>()
                                ?.GetClassificationFormatMap(view);
                if (map == null) return;

                _input.FontFamily = map.DefaultTextProperties.Typeface.FontFamily;
                _input.FontSize = map.DefaultTextProperties.FontRenderingEmSize;
            }
            catch
            {
                // Cosmetic only; the default font is perfectly readable.
            }
        }

        /// <summary>Readable against the editor's own background, whatever theme is on.</summary>
        private static Brush Inverted(Brush background)
        {
            var solid = background as SolidColorBrush;
            if (solid == null) return Brushes.Gray;

            var c = solid.Color;
            bool dark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
            return dark ? Brushes.White : Brushes.Black;
        }

        // The border color only changes with the theme; rebuilding the brush
        // per render was one allocation per cmdline keystroke.
        private static Color _borderColor;
        private static Brush? _borderBrush;

        private static Brush CachedBorderBrush(Brush foreground)
        {
            var color = (foreground as SolidColorBrush ?? Brushes.Gray).Color;
            if (_borderBrush == null || color != _borderColor)
            {
                _borderColor = color;
                _borderBrush = new SolidColorBrush(color) { Opacity = 0.4 };
            }
            return _borderBrush;
        }
    }
}
