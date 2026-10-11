// Seeky picker embedded in NeoVS — see SeekyPickerController.

namespace VSNeo.Seeky;

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

/// <summary>
/// The WPF host for the Seeky picker page (<c>Seeky/WebUI/index.html</c>) over WebView2.
/// </summary>
/// <remarks>
/// <para>
/// In-proc this is a plain <see cref="Window"/> on Visual Studio's own UI thread. The raw Win32
/// window, dedicated pump thread, and WebView2-loader resolver of the standalone SeekyVS
/// extension all belonged to its out-of-proc host, which cannot load the WindowsDesktop shared
/// framework; none of that exists here. Visual Studio ships the WebView2 assemblies and the
/// loader itself, so nothing resolves them (and the extension must not ship its own copies —
/// see the csproj's <c>ExcludeAssets="runtime"</c> note).
/// </para>
/// <para>
/// The window is created once and only hidden between popups, so the WebView2 runtime and the
/// loaded page survive and re-showing is instant. Alt+F4 and friends are demoted to hides; the
/// window is never really closed, it dies with Visual Studio's shell.
/// </para>
/// <para>
/// Escape is handled by the page (a document-level handler posts "close", wherever focus sits
/// in the page) and, as a backstop, here: when Win32 focus is on this window rather than inside
/// the WebView2 child, the page never sees the key. Focus is pushed into the page on every
/// activation (<see cref="FocusPage"/>), and losing activation closes the picker the way Visual
/// Studio's own Ctrl+T does (<see cref="CloseRequested"/>) — resume brings it back as it was.
/// </para>
/// </remarks>
internal sealed class SeekyPickerWindow : Window
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    private readonly WebView2 webView;

    // The shell's main window HWND, captured at construction: owner, centering reference, and
    // the DPI context for converting the physical-pixel sizes SeekyState stores into DIPs.
    private readonly IntPtr ownerHwnd;

    private Task? initializeTask;

    public SeekyPickerWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Width = 860;
        Height = 520;
        Background = new SolidColorBrush(Color.FromRgb(0x05, 0x08, 0x06)); // the page's --bg
        Topmost = false;
        ShowActivated = true;
        ShowInTaskbar = false;

        webView = new WebView2();
        Content = webView;

        // Owned by the shell's main window: the popup stays above Visual Studio without
        // floating over other applications, and dies with the shell. GetGlobalService needs
        // the UI thread, which this is.
        var interop = new WindowInteropHelper(this);
        if (Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell
            && ErrorHandler.Succeeded(shell.GetDialogOwnerHwnd(out IntPtr owner))
            && owner != IntPtr.Zero)
        {
            interop.Owner = owner;
            ownerHwnd = owner;
        }
        else
        {
            SeekyLog.Info("Seeky window: no shell owner HWND; will center on the primary screen");
        }
    }

    /// <summary>
    /// The page's script has run (DOMContentLoaded). Messages posted before this are lost — the
    /// page's listener is registered during parse — so the controller waits for
    /// <see cref="PageReady"/> before its first state push.
    /// </summary>
    internal bool IsPageReady { get; private set; }

    /// <summary>Fired on DOMContentLoaded of the hosted page. UI thread.</summary>
    internal event EventHandler? PageReady;

    /// <summary>A raw JSON message from the page. UI thread.</summary>
    internal event Action<string>? MessageReceived;

    /// <summary>
    /// The window wants to close: Escape reached it (argument true — the user dismissed the
    /// picker, give the editor its focus back), or it lost activation to another window
    /// (false — focus already went where the user sent it). UI thread.
    /// </summary>
    internal event Action<bool>? CloseRequested;

    /// <summary>
    /// Creates the WebView2 environment and navigates to the page, exactly once. A failed
    /// attempt stays faulted: the cause (a missing or broken WebView2 runtime) does not heal
    /// mid-session, and the controller logs the failure on every attempt to show.
    /// </summary>
    internal Task EnsureInitializedAsync() => initializeTask ??= InitializeCoreAsync();

    /// <summary>
    /// Posts a serialized message to the page. Dropped while <see cref="IsPageReady"/> is
    /// false — posting earlier would be lost anyway. UI thread only.
    /// </summary>
    internal void PostJson(string json)
    {
        if (!IsPageReady)
        {
            return;
        }

        try
        {
            webView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            SeekyLog.Error("PostWebMessageAsJson failed", ex);
        }
    }

    /// <summary>Shows (or re-shows) the window and pushes keyboard focus into the page.</summary>
    internal void ShowAndFocus()
    {
        Show();
        Activate();
        FocusPage();
    }

    /// <summary>
    /// Moves keyboard focus into the page. WPF focus on the control is what moves Win32 focus
    /// into the WebView2 child HWND (its OnGotFocus override does the hand-off) — but only when
    /// WPF sees focus arrive. After a hide, or a click into another window, WPF still believes
    /// the control holds focus (Win32 focus left through a child HWND WPF does not track), so
    /// a plain Focus() is a no-op and keys reach nothing until the input is clicked. Clearing
    /// first makes the arrival real. The page then puts the caret in its query input (its
    /// window 'focus' handler). On the first show the child does not exist yet — the page
    /// focuses itself on load.
    /// </summary>
    private void FocusPage()
    {
        if (webView.IsKeyboardFocusWithin)
        {
            Keyboard.ClearFocus();
        }

        _ = webView.Focus();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // Alt+Tab back, a click on the frame: the same hand-off as a show.
        FocusPage();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!IsVisible)
        {
            return; // our own Hide — already closing
        }

        // Deferred and re-checked: activation can flicker while WebView2 moves focus between
        // its own windows, and a picker that closed on that would close on its own opening.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (IsVisible && !IsActive)
            {
                CloseRequested?.Invoke(false);
            }
        }));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Only reached when focus is on the WPF window itself, not inside the page.
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke(true);
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// Sizes the window in physical pixels — the unit <see cref="SeekyState"/> stores, shared
    /// with the standalone SeekyVS popup — and centers it over the owner's main window like
    /// Ctrl+T, slightly above middle. WPF speaks DIPs, so the owner's monitor DPI scales both.
    /// </summary>
    internal void ResizeAndCenter(int widthPx, int heightPx)
    {
        double scale = OwnerDpiScale();
        Width = widthPx / scale;
        Height = heightPx / scale;

        if (ownerHwnd != IntPtr.Zero && GetWindowRect(ownerHwnd, out RECT rect))
        {
            // Centered over the VS main window, slightly above middle.
            Left = (rect.Left + Math.Max(0, ((rect.Right - rect.Left) - widthPx) / 2)) / scale;
            Top = (rect.Top + Math.Max(0, ((rect.Bottom - rect.Top) - heightPx) / 3)) / scale;
        }
        else
        {
            GetPrimaryScreenSizePixels(out int screenWidth, out int screenHeight);
            Left = Math.Max(0, (screenWidth - widthPx) / 2) / scale;
            Top = Math.Max(0, (screenHeight - heightPx) / 3) / scale;
        }
    }

    /// <summary>Whole-window opacity from settings.json (percent; 100 is opaque).</summary>
    internal void ApplyOpacityPercent(int percent) => Opacity = percent / 100.0;

    /// <summary>Primary display size in physical pixels, for clamping stored window sizes.</summary>
    internal static void GetPrimaryScreenSizePixels(out int width, out int height)
    {
        width = GetSystemMetrics(SmCxScreen);
        height = GetSystemMetrics(SmCyScreen);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The window is session-scoped: a destroyed WebView2 is the slow path back, so every
        // close gesture becomes a hide.
        e.Cancel = true;
        Hide();
    }

    private async Task InitializeCoreAsync()
    {
        // Own process, so the user-data folder is ours to choose (never next to devenv.exe,
        // whose own WebView2 data belongs to the shell).
        string userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VSNeo",
            "SeekyUserData");
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        SeekyLog.Info("Seeky window: WebView2 environment created");

        await webView.EnsureCoreWebView2Async(environment);
        CoreWebView2 core = webView.CoreWebView2;
        SeekyLog.Info("Seeky window: WebView2 initialized");

        // Chromium's accelerators are hostile to a keyboard-driven picker: Ctrl+P opens print
        // preview and Ctrl+N a new window, which would swallow the readline-style navigation
        // keys before the page's keydown handler ever sees them. Turning them off also kills
        // Ctrl+F's find bar, F12, zoom and the context menu — none of which belong in a popup.
        CoreWebView2Settings settings = core.Settings;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;

        // Zoom stays ON: Ctrl+wheel was the only way to resize the popup's text, and disabling
        // it took that away. Killing the accelerator keys above also took Ctrl+Plus/Minus, so
        // the page implements those itself against "fontSize" — see the setState message.
        settings.IsZoomControlEnabled = true;

        // Serve the deployed WebUI folder over a virtual https origin (avoids file:// quirks).
        // Anchored at the extension assembly — AppContext.BaseDirectory is devenv's directory.
        string extensionDir = Path.GetDirectoryName(typeof(SeekyPickerWindow).Assembly.Location)
            ?? AppContext.BaseDirectory;
        string webUiDir = Path.Combine(extensionDir, "Seeky", "WebUI");
        SeekyLog.Info($"WebUI dir: {webUiDir} (exists: {Directory.Exists(webUiDir)})");
        core.SetVirtualHostNameToFolderMapping(
            "seeky.vs", webUiDir, CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;
        core.DOMContentLoaded += OnDomContentLoaded;

        webView.Source = new Uri("https://seeky.vs/index.html");
        SeekyLog.Info($"Seeky window: navigated to https://seeky.vs/index.html (mapped to '{webUiDir}')");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e) =>
        MessageReceived?.Invoke(e.WebMessageAsJson);

    private void OnDomContentLoaded(object? sender, object e)
    {
        IsPageReady = true;
        SeekyLog.Info("Seeky window: DOMContentLoaded");
        PageReady?.Invoke(this, EventArgs.Empty);
    }

    private double OwnerDpiScale()
    {
        if (ownerHwnd != IntPtr.Zero)
        {
            uint dpi = GetDpiForWindow(ownerHwnd);
            if (dpi != 0)
            {
                return dpi / 96.0;
            }
        }

        return 1.0;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// Puts the mouse pointer back if the picker's hide left it hidden. Chromium hides the
    /// pointer while the user types into the page and shows it again on the next mouse move
    /// over the page. Its hide is the ShowCursor display counter, which Windows shares across
    /// the input queues it attaches when another process's window (the WebView2 child) is
    /// parented into ours. Hide the picker right after typing, before any mouse move, and
    /// the counter stays negative for Visual Studio too: no pointer over the editor until
    /// the picker was opened and moused over again. Called after the hide has settled.
    /// Logged, because the counter is only visible from here.
    /// </summary>
    internal static void EnsurePointerVisible()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CursorShowing) != 0)
        {
            return;
        }

        // Up to zero and no further: a counter pushed above zero would outlast whoever hides
        // the pointer next. Bounded in case the counter lives in a queue that detached.
        int count = -1;
        for (int i = 0; i < 16 && count < 0; i++)
        {
            count = ShowCursor(true);
        }

        System.Windows.Input.Mouse.UpdateCursor();
        SeekyLog.Info($"Seeky window: mouse pointer was hidden after hide; display counter now {count}");
    }

    private const int CursorShowing = 0x00000001;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool bShow);

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
