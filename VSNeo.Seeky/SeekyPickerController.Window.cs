// The window itself: hiding and focus, size and resizing, and the appearance it opens with.


using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text.Editor;

namespace VSNeo.Seeky;
internal static partial class SeekyPickerController
{
    // Ctrl+Shift+Plus/Minus resize step, and the floor it will not go below — small enough to be
    // a real reduction, large enough that the results and preview panes stay usable.
    private const double WindowResizeStep = 0.05;

    private const int MinWindowWidth = 600;

    private const int MinWindowHeight = 400;

    // Popup size when nothing is stored: three quarters of the screen — a fixed pixel
    // default reads tiny on a high-DPI display. Ctrl+Shift+Plus/Minus resizes from
    // there (persisted), 0 restores this.
    private const double DefaultWindowWidthFraction = 0.75;

    private const double DefaultWindowHeightFraction = 0.75;

    private static int windowWidth;

    private static int windowHeight;

    /// <summary>
    /// Brings the WebView2 up behind the first show. A failure means no page exists to report
    /// itself on, so the log is the only surface — the next Show retries nothing (the init
    /// task stays faulted) but re-reports.
    /// </summary>
    private static async Task InitializeWindowAsync(SeekyPickerWindow w)
    {
        try
        {
            await w.EnsureInitializedAsync();
        }
        catch (Exception ex)
        {
            SeekyLog.Error("Seeky window initialization failed", ex);
        }
    }

    /// <summary>
    /// First show's state push. The page registers its message listener during parse, so
    /// anything posted before DOMContentLoaded is lost — everything it needs to open with goes
    /// through here. (The standalone manager also re-read the state file at this point because
    /// its workspace resolution raced the page load; here the workspace is settled before the
    /// window exists.)
    /// </summary>
    private static void OnPageReady(object? sender, EventArgs e)
    {
        SeekyLog.Info($"DOMContentLoaded: posting setMode '{requestedMode}'");
        PostState();
        PostJson(new { type = "setMode", mode = requestedMode });
        PostRequestedQuery();
        _ = Task.Run(() => PostHistoryAsync());
    }

    // Hiding (instead of closing) keeps WebView2 and the page loaded — the next popup is
    // instant. The window is only closed on extension teardown.
    // restoreEditorFocus: true when the user dismissed the picker (Escape), so the editor it
    // opened over gets its focus back; false when focus went somewhere on purpose — a pick
    // (HandleOpen focuses the opened document) or a click into another window.
    private static void HidePopup(bool restoreEditorFocus)
    {
        if (window is null || !window.IsVisible)
        {
            return;
        }

        SeekyLog.Info("HidePopup: hiding window");
        popupState.Save(workspaceDir);
        window.Hide();
        backdrop?.Hide();
        if (restoreEditorFocus)
        {
            FocusEditor(returnView);
        }

        // After the hide and the focus hand-off have run: a pointer Chromium hid for typing
        // would otherwise stay hidden over the editor (see EnsurePointerVisible).
        SeekyPickerWindow.PostAt(
            window.Dispatcher,
            System.Windows.Threading.DispatcherPriority.Background,
            SeekyPickerWindow.EnsurePointerVisible);
    }

    /// <summary>
    /// Gives <paramref name="view"/> keyboard focus once the hide has settled. Deferred: hiding
    /// reactivates the main window, and Visual Studio's own activation handling runs at Normal
    /// priority — focusing first would be undone by it. Input runs after that.
    /// </summary>
    private static void FocusEditor(IWpfTextView? view)
    {
        if (view is null || view.IsClosed)
        {
            return;
        }

        SeekyPickerWindow.PostAt(
            view.VisualElement.Dispatcher,
            System.Windows.Threading.DispatcherPriority.Input,
            () =>
            {
                if (!view.IsClosed && !view.HasAggregateFocus)
                {
                    _ = view.VisualElement.Focus();
                }
            });
    }

    /// <summary>
    /// Sets the popup dimensions from the stored size, falling back to the default when none
    /// is stored. Clamped to the screen either way, so a size saved on a large monitor does not
    /// open larger than the display it is opening on.
    /// </summary>
    private static void ApplyWindowSize()
    {
        SeekyPickerWindow.GetPrimaryScreenSizePixels(out int screenWidth, out int screenHeight);
        if (popupState.WindowWidth > 0 && popupState.WindowHeight > 0)
        {
            windowWidth = Math.Min(popupState.WindowWidth, screenWidth);
            windowHeight = Math.Min(popupState.WindowHeight, screenHeight);
            return;
        }

        windowWidth = Math.Max(MinWindowWidth, (int)(screenWidth * DefaultWindowWidthFraction));
        windowHeight = Math.Max(MinWindowHeight, (int)(screenHeight * DefaultWindowHeightFraction));
    }

    /// <summary>
    /// Ctrl+Shift+Plus/Minus/0 from the page. Step 0 restores the default size and forgets the
    /// stored one; otherwise the popup grows or shrinks by <see cref="WindowResizeStep"/>,
    /// clamped to the screen and to a floor that keeps both panes usable. Runs on the UI
    /// thread — the window raises page messages there.
    /// </summary>
    private static void HandleResizeWindow(int step)
    {
        if (window is null)
        {
            return;
        }

        SeekyPickerWindow.GetPrimaryScreenSizePixels(out int screenWidth, out int screenHeight);
        if (step == 0)
        {
            popupState = popupState.WithWindowSize(0, 0);
        }
        else
        {
            // Max, not the raw metric: on a display smaller than the floor the clamp bounds
            // would cross over and pin the popup to the screen size instead of the floor.
            int maxWidth = Math.Max(screenWidth, MinWindowWidth);
            int maxHeight = Math.Max(screenHeight, MinWindowHeight);
            double scale = 1 + (step * WindowResizeStep);
            popupState = popupState.WithWindowSize(
                Clamp((int)Math.Round(windowWidth * scale), MinWindowWidth, maxWidth),
                Clamp((int)Math.Round(windowHeight * scale), MinWindowHeight, maxHeight));
        }

        ApplyWindowSize();
        window.ResizeAndCenter(windowWidth, windowHeight);
        PostStatus($"window {windowWidth}×{windowHeight}" + (step == 0 ? " (default)" : string.Empty));
    }

    private const string MonoFontStack = "'Cascadia Code', Consolas, 'Courier New', monospace";

    private const string SystemFontStack = "'Segoe UI', system-ui, sans-serif";

    /// <summary>
    /// Resolves the popup font from <c>%LOCALAPPDATA%\SeekyVS\settings.json</c>
    /// (<c>{ "fontFamily": "…" }</c>), re-read on every popup show so edits apply without
    /// restarting VS. Keywords: <c>mono</c> (default) and <c>system</c>; any other string is
    /// used verbatim as a CSS font-family value after sanitizing (it is injected into an inline
    /// style). Missing/malformed/unsafe values fall back to the mono stack.
    /// </summary>
    private static string ResolveFontFamily()
    {
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeekyVS",
            "settings.json");
        try
        {
            if (!File.Exists(settingsPath))
            {
                // Seed a discoverable default once; ignore failures (dir may not exist yet).
                try
                {
                    File.WriteAllText(settingsPath, "{\n  \"fontFamily\": \"mono\"\n}\n");
                }
                catch (Exception ex)
                {
                    SeekyLog.Error("settings.json: writing the default file failed", ex);
                }

                return MonoFontStack;
            }

            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            string? fontFamily =
                settings.RootElement.TryGetProperty("fontFamily", out JsonElement element)
                && element.ValueKind == JsonValueKind.String
                    ? element.GetString()?.Trim()
                    : null;

            // Explicit null checks, not string.IsNullOrEmpty: the net472 reference assemblies
            // carry no NotNullWhen annotations, so the compiler's flow analysis sees nothing.
            if (fontFamily is null || fontFamily.Length == 0 || fontFamily.Equals("mono", StringComparison.OrdinalIgnoreCase))
            {
                return MonoFontStack;
            }

            if (fontFamily.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                return SystemFontStack;
            }

            // Verbatim values land in an inline style — reject anything that could break out
            // of the CSS declaration.
            if (fontFamily.Any(c => c is ';' or '{' or '}' or '<' or '>' or '"' or '\'' || char.IsControl(c)))
            {
                SeekyLog.Info($"settings.json: rejecting unsafe fontFamily value '{fontFamily}'");
                return MonoFontStack;
            }

            SeekyLog.Info($"settings.json: fontFamily '{fontFamily}'");
            return fontFamily;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            SeekyLog.Error($"settings.json: unreadable ({settingsPath})", ex);
            return MonoFontStack;
        }
    }

    /// <summary>Reads <c>"opacity"</c> (percent, clamped 30–100) from settings.json; default 100.</summary>
    private static int ResolveOpacity()
    {
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeekyVS",
            "settings.json");
        try
        {
            if (!File.Exists(settingsPath))
            {
                return 100;
            }

            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (settings.RootElement.TryGetProperty("opacity", out JsonElement element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out int opacity))
            {
                return Clamp(opacity, 30, 100);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            SeekyLog.Error("settings.json: unreadable opacity", ex);
        }

        return 100;
    }

    /// <summary>Reads <c>"blurBackground"</c> from settings.json; default false.</summary>
    private static bool ResolveBlurBackground()
    {
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeekyVS",
            "settings.json");
        try
        {
            return File.Exists(settingsPath)
                && SeekyAppearanceSettings.ReadBlurBackground(File.ReadAllText(settingsPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            SeekyLog.Error("settings.json: unreadable blurBackground", ex);
            return false;
        }
    }

    // Math.Clamp does not exist on net472.
    private static int Clamp(int value, int low, int high) =>
        value < low ? low : value > high ? high : value;

    // ------------------------------------------------------------------ Workspace (DTE, UI thread)
}
