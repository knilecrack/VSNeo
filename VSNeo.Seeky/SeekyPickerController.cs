// Seeky picker embedded in NeoVS — in-proc port of the standalone SeekyVS extension's
// SeekyModalWindowManager, keeping the page's message contract exactly (the same WebUI is
// hosted unmodified).

namespace VSNeo.Seeky;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;

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
internal static partial class SeekyPickerController
{
    // The fff engine runs in seeky-engine.exe, a child process: a crash there ends the
    // engine, never Visual Studio. Status notes from it land on the page's status line.
    private static readonly SeekyEngineClient Engine =
        new(SeekyEngineClient.DefaultEnginePath, PostStatus);

    /// <summary>The singleton picker window and its optional acrylic backdrop. UI thread only.</summary>
    private static SeekyPickerWindow? window;

    private static SeekyBackdropWindow? backdrop;

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

    /// <summary>
    /// The line text behind each <see cref="jumpColumns"/> entry, where the row has one: the
    /// quickfix list wants a byte column (nvim's), the columns above are UTF-16 offsets.
    /// </summary>
    private static volatile Dictionary<string, string> jumpTexts = new();

    /// <summary>
    /// The colorscheme's colors for the page's 'nvim' theme, keyed by CSS variable name without
    /// the dashes; null until the companion has pushed them. See <see cref="Configure"/>.
    /// </summary>
    private static volatile Dictionary<string, string>? nvimPalette;

    /// <summary>vim.g.vsneo_seeky_prompt_normal: Escape leaves the prompt for a normal mode.</summary>
    private static volatile bool promptNormal;

    /// <summary>
    /// The rows of the list mode this show opened in (<see cref="SeekyLists"/>), and which mode
    /// they belong to: Visual Studio's are captured by <see cref="Show"/>, nvim's fetched on the
    /// first search. Cleared on every show.
    /// </summary>
    private static volatile Tuple<string, List<SeekyLists.Row>>? listCache;

    private static int searchGeneration;

    private static CancellationTokenSource? searchCancellation;

    private static string lastSearchQuery = string.Empty;

    private static string lastSearchMode = "files";

    /// <summary>
    /// Shows the picker, creating the window on first use, or re-showing and re-arming it after.
    /// Called on the WPF UI thread by the package (VSNeo_ExtensionPackage.OnSeekyRequested).
    /// </summary>
    /// <param name="mode">
    /// Picker mode the page should start in: "files", "grep", "git", "symbols", "mixed" (Files &amp;
    /// Folders; "dirs" is its older name), "outline" (the page calls it "path"), or "lines" (the
    /// current file; the page calls it "buffer"). Anything else opens "files". "resume" re-shows
    /// the last picker as it was left; see <see cref="TryResume"/>.
    /// </param>
    /// <param name="query">
    /// Pre-fills the prompt and searches immediately. Empty leaves the prompt empty.
    /// </param>
    internal static void Show(string mode, string query)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // Before anything takes focus: the popup's activation moves the active view away.
        IWpfTextView? activeView = ActiveWpfTextView();

        // Unconditional, null included: a show from a tool window (Solution Explorer) must
        // not hand Escape's focus to an older editor, possibly closed since. Kept while the
        // picker is up - the active view is then nobody's, and the one it opened over stands.
        if (window is null || !window.IsVisible)
        {
            returnView = activeView;
        }

        if (mode == "resume" && TryResume())
        {
            return;
        }

        requestedMode = mode switch
        {
            "files" or "grep" or "git" or "symbols" or "path" or "buffer" or "mixed" => mode,
            "outline" => "path",
            "lines" => "buffer",
            "dirs" => "mixed",
            _ when SeekyLists.IsListMode(mode) => mode,
            _ => "files",
        };

        // Set on every show, so a plain Live Grep after a word-under-cursor show clears it
        // rather than inheriting the previous term.
        requestedQuery = string.IsNullOrEmpty(query) ? null : query;

        // On every show, not only Outline and Current File: Tab reaches Current File from any
        // mode. Cheap - a reference to an immutable snapshot, split only if searched.
        editorSnapshot = CaptureEditorSnapshot(activeView);

        // Synchronous here, ahead of everything else: the state file, the backend, and every
        // later search all key off this root, and resolving it is cheap UI-thread DTE work.
        RefreshWorkspace();
        popupState = SeekyState.Load(workspaceDir);
        ApplyWindowSize();

        // Visual Studio's lists are read here, on the UI thread, before the popup takes focus.
        List<SeekyLists.Row>? capturedRows = requestedMode switch
        {
            "buffers" => SeekyLists.CaptureBuffers(RelativeToWorkspace),
            "diagnostics" => SeekyLists.CaptureDiagnostics(RelativeToWorkspace),
            "vscommands" => SeekyLists.CaptureVsCommands(),
            _ => null,
        };
        listCache = capturedRows is null ? null : Tuple.Create(requestedMode, capturedRows);

        SeekyPickerWindow w = window ?? CreateWindow();
        w.ResizeAndCenter(windowWidth, windowHeight);
        ShowPicker(w);

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
        ShowPicker(w);
        return true;
    }

    /// <summary>
    /// vsneo_seeky_config from the companion: the colorscheme's palette and the prompt-normal
    /// switch, pushed after the rc and again on every ColorScheme. Any thread; stored only - the
    /// page gets them with the next show's state.
    /// </summary>
    internal static void Configure(Dictionary<string, string>? palette, bool promptNormalMode)
    {
        nvimPalette = palette;
        promptNormal = promptNormalMode;
    }

    private static void ShowPicker(SeekyPickerWindow w)
    {
        // Both may have changed in settings.json.
        w.ApplyOpacityPercent(ResolveOpacity());
        bool blur = ResolveBlurBackground();
        w.ApplyBackgroundBlur(blur);
        if (blur)
        {
            if (backdrop is null)
            {
                backdrop = new SeekyBackdropWindow(w.ShellOwnerHwnd);
                backdrop.DismissRequested += () => HidePopup(restoreEditorFocus: true);
            }

            backdrop.ShowOverOwner();
        }
        else
        {
            backdrop?.Hide();
        }

        w.ShowAndFocus();
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
            CancelQuietly(pendingSearch);
            pendingSearch.Dispose();
        }

        Engine.Dispose();
    }
}
