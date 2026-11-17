// The page's message contract: dispatch, the JSON helpers, and the posts back.


using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace VSNeo.Seeky;
internal static partial class SeekyPickerController
{
    private static void OnMessageReceived(string json)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
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
                    string fileMode = GetString(doc.RootElement, "fileMode") ?? "fuzzy";
                    SeekyLog.Info($"WebMessageReceived: search mode={mode} grepMode={grepMode} fileMode={fileMode} query length {query.Length}");

                    // On the thread pool: fff native calls never run on the UI thread (a
                    // symbol sweep can hold the client's gate for seconds). No debounce
                    // here — the page debounces per keystroke before sending.
                    _ = Task.Run(() => HandleSearchAsync(query, mode, grepMode, fileMode));
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
                    int? col = GetInt(doc.RootElement, "col");
                    string? split = GetString(doc.RootElement, "split");
                    bool isDirectory = GetBool(doc.RootElement, "directory");
                    SeekyLog.Info($"WebMessageReceived: open '{path}' line {line} col {col} dir={isDirectory}");

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
                        HandleOpen(path, line, col, split);
                    }

                    break;
                }

                case "quickfix":
                    HandleQuickfix(doc.RootElement);
                    break;

                case "pick":
                {
                    string? pickMode = GetString(doc.RootElement, "mode");
                    string? name = GetString(doc.RootElement, "name");
                    HidePopup(restoreEditorFocus: true);
                    if (pickMode is not null && name is not null)
                    {
                        SeekyLists.Pick(pickMode, name);
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
                        defsOnly,
                        GetString(doc.RootElement, "theme"),
                        GetString(doc.RootElement, "fileMode"));
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

    // The kind check is load-bearing: TryGetInt32 THROWS on a non-number (it does not return
    // false), and Find Files rows carry "line": null whenever the query has no ':line' suffix.
    private static int? GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number)
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
            SeekyPickerWindow.PostAt(
                w.Dispatcher,
                System.Windows.Threading.DispatcherPriority.Normal,
                () => w.PostJson(json));
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
        fileMode = popupState.FileMode,
        defsOnly = popupState.DefsOnly,
        theme = popupState.Theme,
        palette = nvimPalette,
        promptNormal,
        vsneo = true, // turns on the page's VSNeo-only keys: Ctrl+V/X splits, Ctrl+Q quickfix
    });
}
