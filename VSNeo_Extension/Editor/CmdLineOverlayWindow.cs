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
    ///
    /// Styled after noice.nvim's cmdline popup: a title chip on the border
    /// names what is being typed (Cmdline, Search, Lua, Substitute, ...), and
    /// the border, prompt, command name and cursor take that kind's accent
    /// color, so a search never reads as a command. The surface is lifted a
    /// little off the editor's background, and the wildmenu is a list with
    /// its selection highlighted and an n/total counter on the border.
    /// </summary>
    internal static class CmdLineOverlayWindow
    {
        private const int MaxCompletionRows = 10;

        // All UI-thread only. The hub events arrive on the RPC read thread and
        // marshal in through the window's dispatcher.
        private static Window? _window;
        private static TextBlock _prompt = null!;
        private static TextBlock _input = null!;
        private static StackPanel _completions = null!;
        private static Border _completionsHost = null!;
        private static Border _popup = null!;
        private static Border _titleChip = null!;
        private static TextBlock _title = null!;
        private static Border _counterChip = null!;
        private static TextBlock _counter = null!;
        private static FontFamily? _editorFont;
        private static double _editorFontSize;
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

                _popup.Background = Solid(surface);
                _popup.BorderBrush = Solid(accent, 0.75);
                _titleChip.Background = Solid(accent);
                _title.Foreground = Solid(OnAccent(accent));
                _title.Text = kind.Title;
                _counterChip.Background = Solid(surface);
                _counterChip.BorderBrush = Solid(accent, 0.75);
                _counter.Foreground = Solid(text, 0.7);
                _prompt.Foreground = Solid(accent);
                _input.Foreground = Solid(text);

                RenderInput(state, kind, accent, surface);
                RenderCompletions(state, accent, text);

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

            // Prompt (":", "/", an input() prompt) and the text, side by side.
            _prompt = new TextBlock
            {
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _input = new TextBlock { TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
            var inputRow = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_prompt, Dock.Left);
            inputRow.Children.Add(_prompt);
            inputRow.Children.Add(_input);

            _completions = new StackPanel();
            _completionsHost = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(-4, 8, -4, 0),
                Padding = new Thickness(0, 6, 0, 0),
                Child = _completions,
                Visibility = Visibility.Collapsed,
            };

            _popup = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 10),
                BorderThickness = new Thickness(1.5),
                // Room above for the title chip, which sits across the border.
                Margin = new Thickness(0, 10, 0, 0),
                Child = new StackPanel { Children = { inputRow, _completionsHost } },
                Effect = new DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 3,
                    Opacity = 0.45,
                },
            };

            _title = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
            _titleChip = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 1, 7, 2),
                Margin = new Thickness(16, 1, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = _title,
            };

            _counter = new TextBlock { FontSize = 11 };
            _counterChip = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 0, 6, 1),
                Margin = new Thickness(0, 1, 16, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Child = _counter,
                Visibility = Visibility.Collapsed,
            };

            var root = new Grid
            {
                Children = { _popup, _titleChip, _counterChip },
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

                double width = Math.Min(640, Math.Max(360, ownerWidth * 0.5));
                window.Width = width;
                _popup.MaxHeight = ownerHeight * 0.6;

                window.Left = (rect.Left / scale) + ((ownerWidth - width) / 2);
                window.Top = (rect.Top / scale) + Math.Max(0, ownerHeight * 0.12);
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
        /// substitution is edited blind. The command name (s, lua, set) is
        /// drawn in the accent color, the cursor as an accent block.
        /// </summary>
        private static void RenderInput(NvimStateHub state, CmdLineKind kind, Color accent, Color surface)
        {
            var content = state.CmdLine ?? string.Empty;
            int cursor = ColumnMapper.ByteToChar(content, state.CmdLinePos);
            if (cursor < 0) cursor = 0;
            if (cursor > content.Length) cursor = content.Length;

            _prompt.Text = state.CmdLinePrefix ?? string.Empty;
            _prompt.Visibility = string.IsNullOrEmpty(_prompt.Text) ? Visibility.Collapsed : Visibility.Visible;

            _input.Inlines.Clear();
            var name = kind.CommandLength > 0
                ? (kind.CommandStart, kind.CommandStart + kind.CommandLength)
                : (-1, -1);
            var nameBrush = Solid(accent);

            AddText(content, 0, cursor, name, nameBrush);

            // Past the end of the line the cursor has no character to sit on,
            // so it gets a space to occupy instead.
            var under = cursor < content.Length ? content.Substring(cursor, 1) : " ";
            _input.Inlines.Add(new Run(under)
            {
                Background = Solid(accent),
                Foreground = Solid(surface),
            });

            AddText(content, cursor + 1, content.Length, name, nameBrush);
        }

        /// <summary>content[from, to) as runs, the command-name part bold in the accent color.</summary>
        private static void AddText(string content, int from, int to, (int Start, int End) name, Brush nameBrush)
        {
            if (from >= to) return;
            int a = Math.Max(from, Math.Min(to, name.Start));
            int b = Math.Max(from, Math.Min(to, name.End));
            if (name.Start < 0 || a >= b)
            {
                _input.Inlines.Add(new Run(content.Substring(from, to - from)));
                return;
            }
            if (a > from) _input.Inlines.Add(new Run(content.Substring(from, a - from)));
            _input.Inlines.Add(new Run(content.Substring(a, b - a))
            {
                Foreground = nameBrush,
                FontWeight = FontWeights.SemiBold,
            });
            if (to > b) _input.Inlines.Add(new Run(content.Substring(b, to - b)));
        }

        /// <summary>
        /// The wildmenu, capped at a window of rows around the selection - a
        /// long completion list (:e **/foo&lt;Tab&gt;) must not cover the IDE.
        /// The selection is an accent-tinted row; the counter chip on the
        /// border says where in the whole list it is.
        /// </summary>
        private static void RenderCompletions(NvimStateHub state, Color accent, Color text)
        {
            _completions.Children.Clear();

            var words = state.CompletionWords;
            if (words == null || words.Count == 0)
            {
                _completionsHost.Visibility = Visibility.Collapsed;
                _counterChip.Visibility = Visibility.Collapsed;
                return;
            }

            _completionsHost.Visibility = Visibility.Visible;
            _completionsHost.BorderBrush = Solid(accent, 0.25);

            int selected = state.CompletionSelected;
            _counter.Text = selected >= 0
                ? (selected + 1) + "/" + words.Count
                : words.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _counterChip.Visibility = Visibility.Visible;

            int first = 0;
            if (selected >= MaxCompletionRows) first = selected - MaxCompletionRows + 1;
            int last = Math.Min(words.Count, first + MaxCompletionRows);

            var normal = Solid(text, 0.85);
            for (int i = first; i < last; i++)
            {
                var row = new TextBlock
                {
                    Text = words[i],
                    Foreground = normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                if (_editorFont != null)
                {
                    row.FontFamily = _editorFont;
                    row.FontSize = _editorFontSize;
                }

                var cell = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 1, 8, 1),
                    Child = row,
                };
                if (i == selected)
                {
                    cell.Background = Solid(accent, 0.28);
                    row.Foreground = Solid(text);
                    row.FontWeight = FontWeights.SemiBold;
                }
                _completions.Children.Add(cell);
            }
        }

        /// <summary>vsneo_cursor_color's cmdline slot, or null when the user set none.</summary>
        private static Color? UserCmdLineColor(NvimStateHub state)
        {
            var colors = state.CursorColors;
            int rgb = colors != null && colors.Length > 5 ? colors[5] : -1;
            if (rgb < 0) return null;
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        /// <summary>What is being typed, and how it should look.</summary>
        private readonly struct CmdLineKind
        {
            public readonly string Title;
            public readonly Color Accent;
            public readonly int CommandStart;
            public readonly int CommandLength;

            public CmdLineKind(string title, Color accent, int commandStart = -1, int commandLength = 0)
            {
                Title = title;
                Accent = accent;
                CommandStart = commandStart;
                CommandLength = commandLength;
            }
        }

        // tokyonight's accents: distinct at a glance, readable on dark themes
        // (darkened for light ones in Render).
        private static readonly Color Blue = Color.FromRgb(0x7A, 0xA2, 0xF7);
        private static readonly Color Amber = Color.FromRgb(0xE0, 0xAF, 0x68);
        private static readonly Color Cyan = Color.FromRgb(0x7D, 0xCF, 0xFF);
        private static readonly Color Red = Color.FromRgb(0xF7, 0x76, 0x8E);
        private static readonly Color Green = Color.FromRgb(0x9E, 0xCE, 0x6A);
        private static readonly Color Purple = Color.FromRgb(0xBB, 0x9A, 0xF7);
        private static readonly Color Orange = Color.FromRgb(0xFF, 0x9E, 0x64);

        // An optional range (%, '<,'>, 1,5, .,$+3), then the command name - or
        // a bare ! (a shell filter).
        private static readonly Regex CommandName = new Regex(
            @"^\s*(?:%|'<,'>|[\d.$+\-,;]*)\s*([A-Za-z]+|!)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// The prompt says which mechanism is open (":", "/", "?", "=" or an
        /// input() prompt); for ":" the command name narrows it further.
        /// </summary>
        private static CmdLineKind Classify(string prefix, string content)
        {
            switch (prefix)
            {
                case "/": return new CmdLineKind("Search", Amber);
                case "?": return new CmdLineKind("Search \u2191", Amber);
                case "=": return new CmdLineKind("Expression", Orange);
                case ":": break;
                default: return new CmdLineKind("Input", Green);
            }

            if (content.TrimStart().StartsWith("=", StringComparison.Ordinal))
                return new CmdLineKind("Lua", Cyan);

            var m = CommandName.Match(content);
            if (!m.Success) return new CmdLineKind("Cmdline", Blue);

            var group = m.Groups[1];
            string name = group.Value;
            int start = group.Index, length = group.Length;

            if (name == "!") return new CmdLineKind("Shell", Red, start, length);
            if (name == "lua" || name == "luafile" || name == "luado")
                return new CmdLineKind("Lua", Cyan, start, length);
            if (IsAbbreviation(name, "help", 1))
                return new CmdLineKind("Help", Green, start, length);

            // :s[ubstitute] and :g[lobal]/:v[global] take a delimiter right
            // after the name: :s/a/b/, :g/pat/d. :set and :sort do not match -
            // they are not abbreviations of either.
            int after = start + length;
            bool delimited = after < content.Length && !char.IsLetterOrDigit(content[after])
                             && !char.IsWhiteSpace(content[after]) && content[after] != '"' && content[after] != '|';
            if (delimited && IsAbbreviation(name, "substitute", 1))
                return new CmdLineKind("Substitute", Purple, start, length);
            if (delimited && (IsAbbreviation(name, "global", 1) || IsAbbreviation(name, "vglobal", 1)))
                return new CmdLineKind("Global", Purple, start, length);

            return new CmdLineKind("Cmdline", Blue, start, length);
        }

        /// <summary>Vim's :s[ubstitute] rule: at least the required prefix, and a prefix of the full name.</summary>
        private static bool IsAbbreviation(string name, string full, int required) =>
            name.Length >= required && name.Length <= full.Length
            && string.CompareOrdinal(full, 0, name, 0, name.Length) == 0;

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

                _editorFont = map.DefaultTextProperties.Typeface.FontFamily;
                _editorFontSize = map.DefaultTextProperties.FontRenderingEmSize;
                _input.FontFamily = _prompt.FontFamily = _editorFont;
                _input.FontSize = _prompt.FontSize = _editorFontSize;
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
            if (SolidCache.TryGetValue(key, out var brush)) return brush;

            if (SolidCache.Count > 256) SolidCache.Clear();
            brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            brush.Freeze();
            SolidCache[key] = brush;
            return brush;
        }
    }
}
