// Seeky picker embedded in NeoVS — in-proc port of the standalone SeekyVS extension's
// SeekyModalWindowManager, keeping the page's message contract exactly (the same WebUI is
// hosted unmodified).

namespace VSNeo_Extension.Seeky;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

/// <summary>
/// Owns the embedded Seeky picker: the singleton <see cref="SeekyPickerWindow"/>, the page's
/// message traffic, and every search against the native fff engine.
/// </summary>
/// <remarks>
/// <para>
/// Ported from SeekyModalWindowManager with the out-of-proc seams replaced by in-proc ones:
/// the workspace comes from DTE instead of the extensibility RPC, and opening a result is
/// VsShellUtilities plus a DTE caret move. The dedicated pump thread, heartbeat watchdogs, and
/// loader resolvers of the original guarded against out-of-proc deadlock and loader hazards
/// that do not exist inside devenv, so they are not ported.
/// </para>
/// <para>
/// Threading: <see cref="Show"/>, message dispatch, and open run on the WPF UI thread (the
/// package calls <see cref="Show"/> there, and the window raises page messages there). DTE is
/// apartment-bound, so the workspace and the active document are captured during
/// <see cref="Show"/> and handed to the background work — fff searches run on the thread pool
/// and never touch DTE. Results are marshaled back to the page through the window's
/// dispatcher. Native-call failures are logged via <see cref="SeekyLog"/>, never thrown across.
/// </para>
/// </remarks>
internal static class SeekyPickerController
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

    // The fff engine runs in seeky-engine.exe, a child process: a crash there ends the
    // engine, never Visual Studio. Status notes from it land on the page's status line.
    private static readonly SeekyEngineClient Engine =
        new(SeekyEngineClient.DefaultEnginePath, PostStatus);

    /// <summary>The singleton picker window. UI thread only.</summary>
    private static SeekyPickerWindow? window;

    /// <summary>
    /// The workspace every background search runs against, captured by <see cref="Show"/> on
    /// the UI thread — nothing off that thread may ask DTE for it later.
    /// </summary>
    private static string? workspaceDir;

    /// <summary>
    /// The VS active document as a workspace-relative path ('/' separators), captured alongside
    /// <see cref="workspaceDir"/>, for fff's current_file deprioritization. Null when
    /// unavailable or outside the workspace. Goes stale if the user changes the active document
    /// while the popup is up — the cost is a mis-ranked row, not a wrong result.
    /// </summary>
    private static string? activeDocumentRelative;

    /// <summary>
    /// Font size, grep sub-mode and definitions filter as the page currently has them. Loaded on
    /// show, updated by the page's "stateChanged" messages, flushed to disk on hide — see
    /// <see cref="SeekyState"/> for where it lands.
    /// </summary>
    private static SeekyState popupState = new();

    private static string requestedMode = "files";

    /// <summary>Prompt text to open with (vsneo.seeky with a query); null to open empty.</summary>
    private static string? requestedQuery;

    /// <summary>
    /// The editor behind a Document Outline or Current File show, captured by <see cref="Show"/>
    /// before the popup takes focus (the active view goes with it); null for every other mode.
    /// Holds the snapshot, not a file read: the rows, the preview, and the line numbers all
    /// describe the text as Visual Studio has it, unsaved edits included — the standalone
    /// extension had to copy the lines across its RPC boundary to get the same.
    /// </summary>
    private static EditorSnapshot? editorSnapshot;

    /// <summary>
    /// The editor that had focus when the picker opened. Dismissing the picker hands keyboard
    /// focus back to it: hiding an activated window reactivates Visual Studio's main window,
    /// but nothing restores WPF focus inside it, and a text view without keyboard focus draws
    /// no caret (VSNeo's own cursor adornments stand down with it). UI thread only.
    /// </summary>
    private static IWpfTextView? returnView;

    /// <summary>
    /// Where each row of the last posted results should put the caret, keyed by
    /// <see cref="JumpKey"/>: the first highlighted match for grep and current file, the symbol
    /// name for symbols and outline. The page's open message carries only path and line, so the column
    /// is remembered here rather than changing the (upstream-shared) page contract. Replaced
    /// wholesale per result set; read on the UI thread, written from the thread pool.
    /// </summary>
    private static volatile Dictionary<string, int> jumpColumns = new();

    private static int searchGeneration;
    private static CancellationTokenSource? searchCancellation;
    private static string lastSearchQuery = string.Empty;
    private static string lastSearchMode = "files";

    /// <summary>
    /// Shows the picker, creating the window on first use, or re-showing and re-arming it after.
    /// Called on the WPF UI thread by the package (VSNeo_ExtensionPackage.OnSeekyRequested).
    /// </summary>
    /// <param name="mode">
    /// Picker mode the page should start in: "files", "grep", "git", "dirs", "symbols",
    /// "outline" (the page calls it "path"), or "lines" (the current file). Anything else opens
    /// "files". "resume" re-shows the last picker as it was left; see <see cref="TryResume"/>.
    /// </param>
    /// <param name="query">
    /// Pre-fills the prompt and searches immediately. Empty leaves the prompt empty.
    /// </param>
    internal static void Show(string mode, string query)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // Before anything takes focus: the popup's activation moves the active view away.
        IWpfTextView? activeView = ActiveWpfTextView();
        if (activeView is not null)
        {
            returnView = activeView;
        }

        if (mode == "resume" && TryResume())
        {
            return;
        }

        requestedMode = mode switch
        {
            "files" or "grep" or "git" or "dirs" or "symbols" or "path" or "lines" => mode,
            "outline" => "path",
            _ => "files",
        };

        // Set on every show, so a plain Live Grep after a word-under-cursor show clears it
        // rather than inheriting the previous term.
        requestedQuery = string.IsNullOrEmpty(query) ? null : query;

        editorSnapshot = requestedMode is "path" or "lines" ? CaptureEditorSnapshot(activeView) : null;

        // Synchronous here, ahead of everything else: the state file, the backend, and every
        // later search all key off this root, and resolving it is cheap UI-thread DTE work.
        RefreshWorkspace();
        popupState = SeekyState.Load(workspaceDir);
        ApplyWindowSize();

        SeekyPickerWindow w = window ?? CreateWindow();
        w.ResizeAndCenter(windowWidth, windowHeight);
        w.ApplyOpacityPercent(ResolveOpacity()); // may have changed in settings.json
        w.ShowAndFocus();

        if (!w.IsPageReady)
        {
            // The page's message listener does not exist until its script runs — OnPageReady
            // (DOMContentLoaded) posts the same sequence the re-show branch below does, picking
            // up requestedMode/requestedQuery as they then stand. Idempotent: a second Show
            // while the first navigation is in flight re-awaits the same task.
            _ = InitializeWindowAsync(w);
        }
        else
        {
            // The window is kept alive (hidden) between popups so WebView2 stays loaded —
            // re-showing is instant. Reset the page to a clean, empty state, then re-arm it.
            SeekyLog.Info($"Show (mode={requestedMode}): re-showing the existing window");
            PostJson(new { type = "reset" });

            // Before setMode: applyMode redraws the prompt label, which shows the grep sub-mode,
            // so the restored mode has to be in place by then.
            PostState();
            PostJson(new { type = "setMode", mode = requestedMode });
            PostRequestedQuery();
            _ = Task.Run(() => PostHistoryAsync());
        }

        // The backend follows the workspace on the thread pool (StartAsync restarts the index
        // when the root changed, no-ops otherwise); status is posted to the page.
        _ = Task.Run(() => EnsureBackendAsync());
    }

    /// <summary>
    /// Telescope's resume: re-shows the hidden window without the reset/setMode sequence, so the
    /// page comes back exactly as it was left — query, results, selection, preview, mode. The
    /// page never clears itself on hide (only on the host's 'reset'), and the window is kept
    /// alive between shows, so there is nothing to rebuild. The results are the ones that were
    /// on screen, not a re-run: as in Telescope, a stale list beats losing the selection.
    /// </summary>
    /// <returns>
    /// False when there is nothing to resume (no picker shown yet this session) or the solution
    /// changed since — the last results would point into a workspace that is no longer open.
    /// The caller then opens a fresh files picker.
    /// </returns>
    private static bool TryResume()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SeekyPickerWindow? w = window;
        if (w is null || !w.IsPageReady
            || !string.Equals(ResolveWorkspaceDir(), workspaceDir, StringComparison.OrdinalIgnoreCase))
        {
            SeekyLog.Info("Show (resume): nothing to resume, opening files");
            return false;
        }

        SeekyLog.Info($"Show (resume): re-showing the last picker ({lastSearchMode})");
        w.ApplyOpacityPercent(ResolveOpacity());
        w.ShowAndFocus();
        return true;
    }

    /// <summary>The singleton window, created and wired on the first show. UI thread only.</summary>
    private static SeekyPickerWindow CreateWindow()
    {
        SeekyLog.Info($"Show (mode={requestedMode}): creating the picker window");
        var w = new SeekyPickerWindow();
        w.PageReady += OnPageReady;
        w.MessageReceived += OnMessageReceived;
        w.CloseRequested += restoreEditorFocus =>
        {
            SeekyLog.Info($"CloseRequested ({(restoreEditorFocus ? "Escape" : "focus lost")}) — hiding window");
            HidePopup(restoreEditorFocus);
        };
        window = w;
        return w;
    }

    /// <summary>
    /// Extension unload hook: flush the popup state and destroy the fff instance.
    /// </summary>
    internal static void Shutdown()
    {
        SeekyLog.Info("Shutdown: stopping the search engine");

        // Normally flushed by HidePopup; this covers VS closing with the popup still up.
        popupState.Save(workspaceDir);

        CancellationTokenSource? pendingSearch = Interlocked.Exchange(ref searchCancellation, null);
        if (pendingSearch is not null)
        {
            pendingSearch.Cancel();
            pendingSearch.Dispose();
        }

        Engine.Dispose();
    }

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
        if (restoreEditorFocus)
        {
            FocusEditor(returnView);
        }

        // After the hide and the focus hand-off have run: a pointer Chromium hid for typing
        // would otherwise stay hidden over the editor (see EnsurePointerVisible).
        _ = window.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(SeekyPickerWindow.EnsurePointerVisible));
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

        _ = view.VisualElement.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                if (!view.IsClosed && !view.HasAggregateFocus)
                {
                    _ = view.VisualElement.Focus();
                }
            }));
    }

    private static void OnMessageReceived(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out JsonElement typeElement))
            {
                return;
            }

            switch (typeElement.GetString())
            {
                case "search":
                    {
                        string query = GetString(doc.RootElement, "query") ?? string.Empty;
                        string mode = GetString(doc.RootElement, "mode") ?? "files";
                        string grepMode = GetString(doc.RootElement, "grepMode") ?? "plain";
                        SeekyLog.Info($"WebMessageReceived: search mode={mode} grepMode={grepMode} query='{query}'");

                        // On the thread pool: fff native calls never run on the UI thread (a
                        // symbol sweep can hold the client's gate for seconds). No debounce
                        // here — the page debounces per keystroke before sending.
                        _ = Task.Run(() => HandleSearchAsync(query, mode, grepMode));
                        break;
                    }

                case "preview":
                    {
                        string? path = GetString(doc.RootElement, "path");
                        int? line = GetInt(doc.RootElement, "line");
                        bool isBinary = GetBool(doc.RootElement, "binary");
                        bool isDirectory = GetBool(doc.RootElement, "directory");
                        if (path is not null)
                        {
                            _ = Task.Run(() => HandlePreviewAsync(path, line, isBinary, isDirectory));
                        }

                        break;
                    }

                case "open":
                    {
                        string? path = GetString(doc.RootElement, "path");
                        int? line = GetInt(doc.RootElement, "line");
                        bool isDirectory = GetBool(doc.RootElement, "directory");
                        SeekyLog.Info($"WebMessageReceived: open '{path}' line {line} dir={isDirectory}");

                        // Close the popup immediately (telescope behavior). The document-open
                        // itself is VS UI-thread work, so unlike the out-of-proc original —
                        // whose open RPC deadlocked its pump thread — it runs right here.
                        // A directory opens in Explorer, which takes the foreground itself.
                        HidePopup(restoreEditorFocus: false);
                        if (isDirectory && path is not null && workspaceDir is not null)
                        {
                            string absoluteDir = Path.Combine(workspaceDir, path);
                            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{absoluteDir}\"") { UseShellExecute = true });
                        }
                        else
                        {
                            HandleOpen(path, line);
                        }

                        break;
                    }

                case "resizeWindow":
                    HandleResizeWindow(GetInt(doc.RootElement, "step") ?? 0);
                    break;

                case "stateChanged":
                    {
                        // Kept in memory and flushed on hide rather than written per keystroke:
                        // holding Ctrl+Plus autorepeats, and each repeat would otherwise be a
                        // read-modify-write of the state file.
                        bool? defsOnly = null;
                        if (doc.RootElement.TryGetProperty("defsOnly", out JsonElement defs)
                            && defs.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            defsOnly = defs.ValueKind == JsonValueKind.True;
                        }

                        popupState = popupState.With(
                            GetInt(doc.RootElement, "fontSize"),
                            GetString(doc.RootElement, "grepMode"),
                            defsOnly);
                        break;
                    }

                case "close":
                    SeekyLog.Info("WebMessageReceived: close — hiding window");
                    HidePopup(restoreEditorFocus: true);
                    break;
                default:
                    SeekyLog.Info($"WebMessageReceived: unhandled type '{typeElement.GetString()}'");
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed messages from the page.
        }
        catch (Exception ex)
        {
            // In-proc an unhandled exception escapes onto Visual Studio's UI thread; the
            // standalone's pump swallowed the same class of failure into its log instead.
            SeekyLog.Error("WebMessage handling failed", ex);
        }
    }

    // ------------------------------------------------------------------ Search / preview / open

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int number)
            ? number
            : null;

    private static bool GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.True;

    // The WebView2 belongs to the WPF UI thread, so every post from a background search is
    // marshaled through the window's dispatcher. Posts already on that thread go direct.
    private static void PostJson(object message)
    {
        string json = JsonSerializer.Serialize(message);
        SeekyPickerWindow? w = window;
        if (w is null)
        {
            return;
        }

        if (w.Dispatcher.CheckAccess())
        {
            w.PostJson(json);
        }
        else
        {
            _ = w.Dispatcher.BeginInvoke(new Action(() => w.PostJson(json)));
        }
    }

    private static void PostStatus(string message) => PostJson(new { type = "status", message });

    /// <summary>
    /// Frecency learning for a pick, fire-and-forget: the open never waits on the engine, and
    /// a failure costs ranking, not the pick.
    /// </summary>
    private static async Task TrackPickAsync(string query, string absolutePath)
    {
        try
        {
            await Engine.TrackQueryAsync(query, absolutePath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            SeekyLog.Error("track_query failed", ex);
        }
    }

    /// <summary>
    /// Pre-fills the prompt when the show request supplied a term. Sent after 'setMode',
    /// because the page searches on receipt and the search carries the mode with it.
    /// </summary>
    private static void PostRequestedQuery()
    {
        if (!string.IsNullOrEmpty(requestedQuery))
        {
            PostJson(new { type = "setQuery", query = requestedQuery });
        }
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

    /// <summary>
    /// Sends the restored popup state to the page. The font family rides along because it comes
    /// from the same settings file and lands on the same element — unlike the rest of this
    /// message it is read-only, hand-edited in settings.json and never written back.
    /// </summary>
    private static void PostState() => PostJson(new
    {
        type = "setState",
        fontFamily = ResolveFontFamily(),
        fontSize = popupState.FontSize,
        grepMode = popupState.GrepMode,
        defsOnly = popupState.DefsOnly,
    });

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

    // Math.Clamp does not exist on net472.
    private static int Clamp(int value, int low, int high) =>
        value < low ? low : value > high ? high : value;

    // ------------------------------------------------------------------ Workspace (DTE, UI thread)

    /// <summary>
    /// Re-resolves the workspace directory on every popup show (the user may have opened a
    /// different solution or folder since the last one) and snapshots the active document for
    /// current_file deprioritization. UI thread only — DTE is apartment-bound, which is exactly
    /// why background searches receive these values instead of asking for them.
    /// </summary>
    private static void RefreshWorkspace()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string? resolved = ResolveWorkspaceDir();
        SeekyLog.Info($"Workspace: resolved '{resolved ?? "(none)"}' (was '{workspaceDir ?? "(none)"}')");
        if (!string.Equals(resolved, workspaceDir, StringComparison.OrdinalIgnoreCase))
        {
            // The engine keys its symbol index by workspace, so a new root never serves the
            // old set.
            workspaceDir = resolved;
        }

        activeDocumentRelative = ResolveActiveDocumentRelative();
    }

    /// <summary>
    /// Snapshots the active editor for Document Outline: its document path, caret line, and
    /// text. Classification happens later, on the thread pool — a snapshot is immutable, so it
    /// can leave the UI thread. Null when no text editor is active; the picker then opens with
    /// a status line rather than silently not at all.
    /// </summary>
    private static EditorSnapshot? CaptureEditorSnapshot(IWpfTextView? view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (view is null)
            {
                return null;
            }

            ITextSnapshot snapshot = view.TextBuffer.CurrentSnapshot;
            string documentPath = view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)
                ? document.FilePath
                : string.Empty;

            // Mapped down to the document buffer: a projection view (Razor, say) reports its
            // caret in the view's buffer, which is not the one being outlined.
            SnapshotPoint? caret = view.Caret.Position.Point.GetPoint(view.TextBuffer, PositionAffinity.Successor);
            int caretLine = caret.HasValue ? caret.Value.GetContainingLine().LineNumber + 1 : 1;
            return new EditorSnapshot(documentPath, caretLine, snapshot);
        }
        catch (Exception ex)
        {
            SeekyLog.Error("Capturing the document outline failed", ex);
            return null;
        }
    }

    /// <summary>
    /// The active editor's search term for Grep Word Under Cursor: the selection's first line
    /// if there is a selection (a Vim visual selection is Visual Studio's selection too), else
    /// the identifier at the caret. Null when there is no editor or no term; the picker then
    /// opens with an empty prompt. UI thread only.
    /// </summary>
    internal static string? EditorSearchTerm()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        IWpfTextView? view = ActiveWpfTextView();
        if (view is null)
        {
            return null;
        }

        // A grep query is one line; a stray whole-file selection should not push a file
        // through fff.
        const int MaxLength = 200;
        string term;
        if (!view.Selection.IsEmpty)
        {
            term = view.Selection.StreamSelectionSpan.GetText();
            int lineBreak = term.IndexOfAny(['\r', '\n']);
            if (lineBreak >= 0)
            {
                term = term.Substring(0, lineBreak);
            }
        }
        else
        {
            // Expands both ways, so a caret just past a word's end (where Vim's block cursor
            // sits on the last letter's right edge) still picks the word up.
            ITextSnapshotLine line = view.Caret.Position.BufferPosition.GetContainingLine();
            string text = line.GetText();
            int column = view.Caret.Position.BufferPosition.Position - line.Start.Position;
            int start = column;
            while (start > 0 && IsWordChar(text[start - 1]))
            {
                start--;
            }

            int end = column;
            while (end < text.Length && IsWordChar(text[end]))
            {
                end++;
            }

            term = text.Substring(start, end - start);
        }

        term = term.Trim();
        if (term.Length > MaxLength)
        {
            term = term.Substring(0, MaxLength);
        }

        return term.Length == 0 ? null : term;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The active editor's WPF view, or null when no text editor is active. UI thread only.</summary>
    private static IWpfTextView? ActiveWpfTextView()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (!(Package.GetGlobalService(typeof(SVsTextManager)) is IVsTextManager textManager)
                || ErrorHandler.Failed(textManager.GetActiveView(1, null, out IVsTextView vsView))
                || vsView is null)
            {
                return null;
            }

            return WpfViewOf(vsView);
        }
        catch (Exception ex)
        {
            SeekyLog.Error("Active view query failed", ex);
            return null;
        }
    }

    private static IWpfTextView? WpfViewOf(IVsTextView vsView) =>
        Package.GetGlobalService(typeof(SComponentModel)) is IComponentModel model
            ? model.GetService<IVsEditorAdaptersFactoryService>()?.GetWpfTextView(vsView)
            : null;

    /// <summary>
    /// The open solution's directory; falls back to the active document's directory; then null.
    /// UI thread only.
    /// </summary>
    private static string? ResolveWorkspaceDir()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (Package.GetGlobalService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte)
            {
                string? solutionFile = dte.Solution?.FullName;
                if (solutionFile is not null && solutionFile.Length != 0)
                {
                    string? solutionDir = Path.GetDirectoryName(solutionFile);
                    if (!string.IsNullOrEmpty(solutionDir))
                    {
                        return solutionDir;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SeekyLog.Error("Solution query failed; trying active document", ex);
        }

        try
        {
            if (Package.GetGlobalService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte)
            {
                string? documentPath = dte.ActiveDocument?.FullName;
                if (documentPath is not null && documentPath.Length != 0)
                {
                    return Path.GetDirectoryName(documentPath);
                }
            }
        }
        catch (Exception ex)
        {
            SeekyLog.Error("Active document query failed", ex);
        }

        return null;
    }

    /// <summary>
    /// The VS active document relative to the workspace, or null when there is no document, no
    /// workspace, or the file sits outside the root. UI thread only.
    /// </summary>
    private static string? ResolveActiveDocumentRelative()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (workspaceDir is null
                || !(Package.GetGlobalService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte))
            {
                return null;
            }

            string? fullPath = dte.ActiveDocument?.FullName;
            if (fullPath is null || fullPath.Length == 0)
            {
                return null;
            }

            // RelativeToWorkspace passes an outside-the-root path through unchanged.
            string relative = RelativeToWorkspace(fullPath);
            return string.Equals(relative, fullPath, StringComparison.OrdinalIgnoreCase) ? null : relative;
        }
        catch (Exception ex)
        {
            SeekyLog.Error("Active document path query failed", ex);
            return null;
        }
    }

    /// <summary>
    /// <paramref name="fullPath"/> relative to the workspace ('/' separators), or the full path
    /// unchanged when there is no workspace or the file sits outside it — Path.Combine in the
    /// preview/open handlers passes rooted paths through, so an absolute path still works.
    /// </summary>
    private static string RelativeToWorkspace(string fullPath)
    {
        if (workspaceDir is null || fullPath.Length == 0)
        {
            return fullPath;
        }

        string prefix = workspaceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? fullPath.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/')
            : fullPath;
    }

    // ------------------------------------------------------------------ fff backend (thread pool)

    /// <summary>
    /// Brings the fff index up on every show (no-op when the workspace is unchanged and the
    /// scan finished; a restart when it changed), then refreshes git status so badges and Git
    /// Modified reflect changes made since the last popup. Best-effort, background; progress
    /// and failures land on the page's status line.
    /// </summary>
    private static async Task EnsureBackendAsync()
    {
        try
        {
            string? workspace = workspaceDir;
            if (workspace is null)
            {
                PostStatus("no workspace open — open a solution or a file");
                return;
            }

            await Engine.StartAsync(workspace, CancellationToken.None);
            await Engine.RefreshGitStatusAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            SeekyLog.Error("Search backend start failed", ex);
            PostStatus("search backend failed to start: " + ex.Message);
        }
    }

    /// <summary>Posts past queries (fff history LMDB, populated by track_query picks) to the
    /// page for ↑ history cycling. Best-effort; history is empty until the first picks.</summary>
    private static async Task PostHistoryAsync()
    {
        try
        {
            string? workspace = workspaceDir;
            if (workspace is null)
            {
                return;
            }

            await Engine.StartAsync(workspace, CancellationToken.None);
            IReadOnlyList<string> queries = await Engine.GetHistoryAsync(50, CancellationToken.None);
            PostJson(new { type = "history", queries });
        }
        catch (Exception ex)
        {
            SeekyLog.Error("history fetch failed", ex);
        }
    }

    private static async Task HandleSearchAsync(string query, string mode, string grepMode)
    {
        int generation = Interlocked.Increment(ref searchGeneration);
        lastSearchQuery = query;
        lastSearchMode = mode;
        using var searchTokenSource = new CancellationTokenSource();
        CancellationTokenSource? previousSearch = Interlocked.Exchange(ref searchCancellation, searchTokenSource);
        if (previousSearch is not null)
        {
            previousSearch.Cancel();
            previousSearch.Dispose();
        }

        CancellationToken cancellationToken = searchTokenSource.Token;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // The workspace was resolved on the UI thread before the popup showed; there is
            // nothing to wait for here (and DTE must never be touched from this thread).
            // Document Outline and Current File read one editor snapshot and need neither a
            // workspace nor the fff index.
            bool editorMode = mode is "path" or "lines";
            string? workspace = workspaceDir;
            if (workspace is null && !editorMode)
            {
                PostStatus("no workspace open — open a solution or a file");
                return;
            }

            // No-op when already indexed; restarts the index if the workspace changed.
            if (!editorMode)
            {
                await Engine.StartAsync(workspace!, cancellationToken);
            }

            const int maxResults = 100;

            // One file's matches are cheap to send and to render, and a common word in a long
            // file easily passes 100 — capping there would hide lines Vim's '/' would reach.
            const int MaxLineResults = 1000;
            List<object> items;
            var columns = new Dictionary<string, int>();
            int? selectedIndex = null;
            if (mode == "lines")
            {
                EditorSnapshot? editor = editorSnapshot;
                if (editor is null)
                {
                    items = new List<object>();
                    PostStatus("no editor was active");
                }
                else
                {
                    string displayPath = RelativeToWorkspace(editor.DocumentPath);
                    List<LineSearch.Hit> hits = LineSearch.Search(
                        editor.Lines, query, grepMode, editor.CaretLine, MaxLineResults,
                        out string? regexError, cancellationToken);
                    if (regexError is not null)
                    {
                        PostStatus($"regex error (fell back to literal): {regexError}");
                    }

                    foreach (LineSearch.Hit hit in hits)
                    {
                        columns[JumpKey(displayPath, hit.Line)] = hit.Ranges.Length > 0 ? hit.Ranges[0].Start : -1;
                    }

                    // Grep's row shape, so the page renders both alike.
                    items = hits
                        .Select(h => (object)new
                        {
                            name = h.Text,
                            path = displayPath,
                            line = h.Line,
                            text = h.Text,
                            ranges = h.Ranges.Select(r => new[] { r.Start, r.End }).ToArray(),
                            isDefinition = SymbolClassifier.IsDefinition(editor.DocumentPath, h.Text),
                        })
                        .ToList();
                }
            }
            else if (mode == "path")
            {
                // Empty query lists the whole file with the caret's symbol preselected; typing
                // fuzzy-filters by name.
                EditorSnapshot? editor = editorSnapshot;
                IReadOnlyList<SymbolOutline.Entry> outline =
                    editor?.Outline ?? Array.Empty<SymbolOutline.Entry>();
                if (editor is null || outline.Count == 0)
                {
                    items = new List<object>();
                    PostStatus(editor is null ? "no editor was active" : "no symbols in this document");
                }
                else
                {
                    string displayPath = RelativeToWorkspace(editor.DocumentPath);
                    items = OutlineItems(outline, displayPath, query, maxResults);
                    foreach (SymbolOutline.Entry entry in outline)
                    {
                        columns[JumpKey(displayPath, entry.Line)] = editor.NameColumn(entry);
                    }

                    if (query.Length == 0)
                    {
                        // File-bottom-first ordering (see OutlineItems): the enclosing symbol's
                        // index flips around the list's midpoint.
                        List<SymbolOutline.Entry> chain = SymbolOutline.ChainAt(outline, editor.CaretLine);
                        if (chain.Count > 0)
                        {
                            int fileOrder = IndexOfEntry(outline, chain[chain.Count - 1]);
                            if (fileOrder >= 0)
                            {
                                selectedIndex = outline.Count - 1 - fileOrder;
                            }
                        }
                    }
                }
            }
            else if (mode == "grep")
            {
                // Live-grep with an empty query matches everything — show nothing instead.
                if (string.IsNullOrWhiteSpace(query))
                {
                    items = new List<object>();
                }
                else
                {
                    // Native fff modes: 0 = plain SIMD (true literal), 1 = regex, 2 = fuzzy.
                    // The query goes raw — fff parses '*.cs pattern'-style constraints itself.
                    SeekyEngineClient.GrepMode nativeMode = grepMode switch
                    {
                        "regex" => SeekyEngineClient.GrepMode.Regex,
                        "fuzzy" => SeekyEngineClient.GrepMode.Fuzzy,
                        "any" => SeekyEngineClient.GrepMode.Any,
                        _ => SeekyEngineClient.GrepMode.Plain,
                    };
                    SeekyEngineClient.GrepResult result =
                        await Engine.GrepAsync(query, nativeMode, maxResults, cancellationToken);
                    if (result.RegexFallbackError is not null)
                    {
                        PostStatus($"regex error (fell back to literal): {result.RegexFallbackError}");
                    }

                    foreach (SeekyEngineClient.GrepMatch m in result.Matches)
                    {
                        // The first highlight, not m.Col: the ranges are already UTF-16 char
                        // indices into the line, fff's column is a byte offset.
                        columns[JumpKey(m.Path, m.Line)] = m.Ranges.Length > 0 ? m.Ranges[0].Start : -1;
                    }

                    items = result.Matches
                        .Select(m => (object)new
                        {
                            name = m.Text,
                            path = m.Path,
                            line = m.Line,
                            col = m.Col,
                            text = m.Text,
                            // (start, end) UTF-16 char-index pairs into 'text' for highlighting.
                            ranges = m.Ranges.Select(r => new[] { r.Start, r.End }).ToArray(),
                            gitStatus = m.GitStatus,
                            isBinary = m.IsBinary,
                            // NOT m.IsDefinition: fff_c.dll v0.10.1 reports false for every match.
                            isDefinition = SymbolClassifier.IsDefinition(m.Path, m.Text),
                        })
                        .ToList();
                }
            }
            else if (mode == "symbols")
            {
                // Workspace symbols: one cached sweep, fuzzy-filtered here per keystroke.
                // The engine sweeps once per workspace and filters per call.
                IReadOnlyList<SeekyEngineClient.SymbolHit> hits =
                    await Engine.SymbolsAsync(workspace!, query, maxResults, cancellationToken);
                foreach (var h in hits)
                {
                    // Onto the name, like gd lands: the declaration line starts with modifiers.
                    columns[JumpKey(h.Path, h.Line)] =
                        SymbolClassifier.TryClassify(h.Path, h.Text, out SymbolClassifier.Symbol symbol)
                            ? symbol.NameStart
                            : -1;
                }

                items = hits
                    .Select(h => (object)new
                    {
                        name = h.Name,
                        path = h.Path,
                        line = h.Line,
                        col = h.Col,
                        text = h.Text,
                        kind = h.Kind,
                        // Spans into 'name' (not 'text') — symbol rows highlight the name.
                        nameRanges = h.NameRanges.Select(r => new[] { r.Start, r.End }).ToArray(),
                        ranges = Array.Empty<int[]>(),
                        gitStatus = h.GitStatus,
                        isBinary = h.IsBinary,
                        isDefinition = true,
                    })
                    .ToList();
            }
            else if (mode == "git")
            {
                // "Git Modified": fuzzy file search filtered to files with a git status
                // (empty query → all modified files, frecency-ranked — the engine's FffNativeClient).
                IReadOnlyList<SeekyEngineClient.FileItem> files =
                    await Engine.GitModifiedAsync(query, maxResults, cancellationToken);
                items = files
                    .Select(f => (object)new
                    {
                        name = f.Path,
                        path = f.Path,
                        frecency = f.FrecencyScore,
                        gitStatus = f.GitStatus,
                        isBinary = f.IsBinary,
                    })
                    .ToList();
            }
            else if (mode == "dirs")
            {
                // Directory search: fuzzy over indexed directories. Opening reveals the folder.
                string? currentDir = activeDocumentRelative;
                // fff 0.11 searches folders through the mixed files-and-folders query; this mode
                // keeps only the folders.
                SeekyEngineClient.FileSearch mixed =
                    await Engine.FindMixedAsync(query, currentDir, maxResults, cancellationToken);
                IEnumerable<SeekyEngineClient.FileItem> dirs = mixed.Items.Where(i => i.IsDirectory);
                items = dirs
                    .Select(d => (object)new
                    {
                        name = d.Path,
                        path = d.Path,
                        isDirectory = true,
                    })
                    .ToList();
            }
            else
            {
                // current_file deprioritizes the file already open in VS (alternate-file workflow).
                string? currentFile = activeDocumentRelative;
                IReadOnlyList<SeekyEngineClient.FileItem> files =
                    (await Engine.FindFilesAsync(query, currentFile, maxResults, cancellationToken)).Items;
                items = files
                    .Select(f => (object)new
                    {
                        name = f.Path,
                        path = f.Path,
                        frecency = f.FrecencyScore,
                        gitStatus = f.GitStatus,
                        isBinary = f.IsBinary,
                    })
                    .ToList();
            }

            if (cancellationToken.IsCancellationRequested || generation != searchGeneration)
            {
                SeekyLog.Info($"Search '{query}' ({mode}/{grepMode}): discarded stale results ({items.Count} items)");
                return;
            }

            SeekyLog.Info($"Search '{query}' ({mode}/{grepMode}): {items.Count} results in {stopwatch.ElapsedMilliseconds}ms");
            jumpColumns = columns;
            PostJson(new
            {
                type = "results",
                done = true,
                // A full document outline legitimately exceeds maxResults — it isn't capped.
                capped = mode == "lines"
                    ? items.Count >= MaxLineResults
                    : mode == "path" && query.Length == 0 ? false : items.Count >= maxResults,
                duration = stopwatch.ElapsedMilliseconds,
                items,
                // The caret's enclosing symbol in Document Outline; null elsewhere, which the
                // page reads as "pick the best match".
                selectedIndex,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SeekyLog.Info($"Search '{query}' ({mode}/{grepMode}): cancelled");
        }
        catch (Exception ex)
        {
            SeekyLog.Error($"Search '{query}' ({mode}/{grepMode}) failed", ex);
            PostStatus("search failed: " + ex.Message);
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref searchCancellation, null, searchTokenSource);
        }
    }

    /// <summary>
    /// Document-outline rows for "path" mode. Empty query: the whole file, file-bottom-first —
    /// the page renders index 0 as the bottom row next to the prompt, so the file reads
    /// top-to-bottom on screen. Non-empty query: fuzzy name filter, best match at index 0.
    /// </summary>
    private static List<object> OutlineItems(
        IReadOnlyList<SymbolOutline.Entry> outline, string displayPath, string query, int maxResults)
    {
        if (query.Length == 0)
        {
            return outline
                .Reverse()
                .Select(e => OutlineItem(e, displayPath, Array.Empty<int[]>()))
                .ToList();
        }

        var scored = new List<(SymbolOutline.Entry Entry, int Score, (int Start, int End)[] Ranges)>();
        foreach (SymbolOutline.Entry entry in outline)
        {
            if (FuzzyMatcher.TryMatch(query, entry.Name, out int score, out (int Start, int End)[] ranges))
            {
                scored.Add((entry, score, ranges));
            }
        }

        scored.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Entry.Line.CompareTo(b.Entry.Line);
        });

        return scored
            .Take(maxResults)
            .Select(h => OutlineItem(h.Entry, displayPath, h.Ranges.Select(r => new[] { r.Start, r.End }).ToArray()))
            .ToList();
    }

    private static object OutlineItem(SymbolOutline.Entry entry, string displayPath, int[][] nameRanges) =>
        new
        {
            name = entry.Name,
            path = displayPath,
            line = entry.Line,
            depth = entry.Indent,
            kind = entry.Kind,
            nameRanges,
            ranges = Array.Empty<int[]>(),
            isDefinition = true,
        };

    /// <summary>
    /// The outline position of <paramref name="needle"/>, matched on declaration line (unique
    /// per outline), or -1.
    /// </summary>
    private static int IndexOfEntry(IReadOnlyList<SymbolOutline.Entry> outline, SymbolOutline.Entry needle)
    {
        for (int i = 0; i < outline.Count; i++)
        {
            if (outline[i].Line == needle.Line)
            {
                return i;
            }
        }

        return -1;
    }

    private static string JumpKey(string path, int line) => path + "\n" + line;

    private static async Task HandlePreviewAsync(string path, int? line, bool isBinary, bool isDirectory)
    {
        try
        {
            // Document Outline previews the snapshot it outlined, not the file on disk: the
            // line numbers came from the editor's text, unsaved edits included.
            EditorSnapshot? editor = editorSnapshot;
            if (editor is not null && !isDirectory
                && string.Equals(path, RelativeToWorkspace(editor.DocumentPath), StringComparison.OrdinalIgnoreCase))
            {
                PostJson(new { type = "preview", path, content = editor.PreviewText(), line });
                return;
            }

            string? workspace = workspaceDir;
            if (workspace is null)
            {
                return;
            }

            // Directories: list their entries (dirs first, then files) instead of file content.
            if (isDirectory)
            {
                string absoluteDir = Path.Combine(workspace, path);
                if (!Directory.Exists(absoluteDir))
                {
                    return;
                }

                var listing = Directory.GetDirectories(absoluteDir).Select(d => Path.GetFileName(d) + "/")
                    .Concat(Directory.GetFiles(absoluteDir).Select(Path.GetFileName)!)
                    .Take(300);
                PostJson(new { type = "preview", path, content = string.Join("\n", listing) });
                return;
            }

            // Never read binary files — the page shows a neutral note instead.
            if (isBinary)
            {
                PostJson(new { type = "preview", path, binary = true });
                return;
            }

            string absolutePath = Path.Combine(workspace, path);

            // Cap at ~200KB / 2000 lines — the preview is a glance, not an editor.
            const int maxBytes = 200 * 1024;
            const int maxLines = 2000;
            var buffer = new byte[maxBytes];
            int read;
            using (var stream = new FileStream(
                absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, maxBytes, FileOptions.SequentialScan))
            {
                read = await stream.ReadAsync(buffer, 0, maxBytes);
            }

            string text = Encoding.UTF8.GetString(buffer, 0, read);
            string[] lines = text.Split('\n');
            if (lines.Length > maxLines)
            {
                text = string.Join("\n", lines.Take(maxLines));
            }

            PostJson(new { type = "preview", path, content = text, line });
        }
        catch (Exception ex)
        {
            SeekyLog.Error($"Preview of '{path}' failed", ex);
            PostStatus("preview failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Opens a picked file at the match line and records the pick for frecency learning. UI
    /// thread only — document activation and the caret move are VS work; the extension's own
    /// focus/caret sync carries it from there.
    /// </summary>
    private static void HandleOpen(string? path, int? line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            // A rooted path (a Document Outline of a file outside the workspace) needs no root.
            if (path is null || (workspaceDir is null && !Path.IsPathRooted(path)))
            {
                return;
            }

            string absolutePath = workspaceDir is null ? path : Path.Combine(workspaceDir, path);
            SeekyLog.Info($"Open: '{absolutePath}' line {line}");

            // Frecency learning: record the pick (best-effort; never blocks the open). Not for
            // Document Outline or Current File, whose queries rank within one file. fff
            // canonicalizes the path, so it must be absolute — a workspace-relative path
            // resolves against devenv's CWD and fails with os error 3.
            if (lastSearchMode is not ("path" or "lines"))
            {
                string trackedQuery = lastSearchQuery;
                _ = Task.Run(() => TrackPickAsync(trackedQuery, absolutePath));
            }

            // The overload that hands back the frame and view: focus goes to the opened
            // document explicitly, since the picker's hide does not restore it anywhere.
            VsShellUtilities.OpenDocument(
                ServiceProvider.GlobalProvider, absolutePath, Guid.Empty,
                out _, out _, out IVsWindowFrame? frame, out IVsTextView? openedView);
            frame?.Show();

            // fff line numbers are 1-based, and so are DTE's. With a known column the caret
            // lands on the match or the symbol name; without one on the first non-blank, as
            // Vim's :N does — column 1 in an indented block is nowhere anyone meant to go.
            // CursorSynchronizer carries the caret to nvim from here.
            if (line is int lineNumber && lineNumber > 0
                && Package.GetGlobalService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte
                && dte.ActiveDocument?.Selection is EnvDTE.TextSelection selection)
            {
                if (jumpColumns.TryGetValue(JumpKey(path, lineNumber), out int column) && column >= 0)
                {
                    // LineCharOffset: 1-based, one per UTF-16 char (a tab counts as one).
                    selection.MoveToLineAndOffset(lineNumber, column + 1, false);
                }
                else
                {
                    selection.GotoLine(lineNumber, false);
                    selection.StartOfLine(EnvDTE.vsStartOfLineOptions.vsStartOfLineOptionsFirstText, false);
                }
            }

            FocusEditor(openedView is null ? null : WpfViewOf(openedView));
        }
        catch (Exception ex)
        {
            SeekyLog.Error($"Open of '{path}' failed", ex);
            PostStatus("open failed: " + ex.Message);
        }
    }
}

/// <summary>
/// The editor behind a Document Outline or Current File show: the document, the 1-based caret
/// line, and the snapshot both search. Split and classified once, lazily, on the first search
/// that asks — that runs on the thread pool, and a snapshot is safe to read there.
/// </summary>
internal sealed class EditorSnapshot
{
    // Preview cap, as for files read from disk: the preview is a glance, not an editor.
    private const int MaxPreviewLines = 2000;

    private readonly ITextSnapshot snapshot;
    private readonly Lazy<IReadOnlyList<string>> lines;
    private readonly Lazy<IReadOnlyList<SymbolOutline.Entry>> outline;

    internal EditorSnapshot(string documentPath, int caretLine, ITextSnapshot snapshot)
    {
        DocumentPath = documentPath;
        CaretLine = caretLine;
        this.snapshot = snapshot;
        lines = new Lazy<IReadOnlyList<string>>(ReadLines, LazyThreadSafetyMode.ExecutionAndPublication);
        outline = new Lazy<IReadOnlyList<SymbolOutline.Entry>>(
            () => SymbolOutline.ClassifyAll(documentPath, Lines),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal string DocumentPath { get; }

    internal int CaretLine { get; }

    /// <summary>The snapshot's lines, split once and shared by every search of this show.</summary>
    internal IReadOnlyList<string> Lines => lines.Value;

    internal IReadOnlyList<SymbolOutline.Entry> Outline => outline.Value;

    /// <summary>
    /// The UTF-16 column of the declaration's name on its line, or -1 when it is not there.
    /// Matched as a whole identifier: a name can also be a substring of the modifiers ahead of
    /// it ("in" inside "internal").
    /// </summary>
    internal int NameColumn(SymbolOutline.Entry entry)
    {
        if (entry.Line < 1 || entry.Line > snapshot.LineCount)
        {
            return -1;
        }

        string text = snapshot.GetLineFromLineNumber(entry.Line - 1).GetText();
        int from = 0;
        while (from <= text.Length - entry.Name.Length)
        {
            int at = text.IndexOf(entry.Name, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return -1;
            }

            int end = at + entry.Name.Length;
            bool startsWord = at == 0 || !IsIdentifierChar(text[at - 1]);
            bool endsWord = end == text.Length || !IsIdentifierChar(text[end]);
            if (startsWord && endsWord)
            {
                return at;
            }

            from = at + 1;
        }

        return -1;
    }

    /// <summary>The snapshot's text for the preview pane, capped like a file read from disk.</summary>
    internal string PreviewText()
    {
        int lines = Math.Min(snapshot.LineCount, MaxPreviewLines);
        int end = snapshot.GetLineFromLineNumber(lines - 1).EndIncludingLineBreak.Position;
        // Line breaks as the file has them, as HandlePreviewAsync sends a file read from disk.
        return snapshot.GetText(0, end);
    }

    private IReadOnlyList<string> ReadLines()
    {
        var lines = new List<string>(snapshot.LineCount);
        foreach (ITextSnapshotLine line in snapshot.Lines)
        {
            lines.Add(line.GetText());
        }

        return lines;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
