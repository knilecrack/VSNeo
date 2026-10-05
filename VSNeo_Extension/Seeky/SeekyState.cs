// SeekyVS — Visual Studio 2026 port spike for the Seeky VS Code extension.

namespace VSNeo_Extension.Seeky;

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// The popup state that survives a close: font size, grep sub-mode, the definitions filter, and
/// the colour theme.
/// </summary>
/// <remarks>
/// <para>
/// Two layers. <c>&lt;workspace&gt;\.vs\seeky\state.json</c> is written by the popup itself and
/// sits beside the frecency and history databases fff already keeps there, so each solution
/// remembers its own settings. <c>%LOCALAPPDATA%\SeekyVS\settings.json</c> is the hand-edited
/// file; it also carries <c>fontFamily</c> and <c>opacity</c>, which the popup only ever reads.
/// </para>
/// <para>
/// A read takes the last layer that carries the key, workspace winning over global. A write goes
/// to the workspace file, or to the global file when no solution is open — and merges into
/// whatever is already there rather than replacing it, so a hand-edited <c>fontFamily</c> is never
/// clobbered by a font-size change.
/// </para>
/// <para>
/// The theme is the exception: it is a look, not a per-solution preference, so it is read from
/// and written to the global file only.
/// </para>
/// <para>
/// Everything here is best-effort. A state file that cannot be read or written costs the user a
/// remembered preference, which is never worth failing a search over, so every path logs and
/// falls back to the defaults.
/// </para>
/// </remarks>
internal sealed record SeekyState
{
    /// <summary>Page font size in CSS px when nothing is stored — matches the stylesheet.</summary>
    internal const int DefaultFontSize = 13;

    private const int MinFontSize = 8;
    private const int MaxFontSize = 32;

    /// <summary>Below this a stored dimension is treated as absent rather than honoured.</summary>
    private const int MinWindowDimension = 200;

    /// <summary>fff's signature mode, and what Live Grep opens in when nothing is stored.</summary>
    private const string DefaultGrepMode = "fuzzy";

    /// <summary>What the popup looks like when nothing is stored — the original CRT palette.</summary>
    private const string DefaultTheme = "phosphor";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>CSS px, clamped to [<see cref="MinFontSize"/>, <see cref="MaxFontSize"/>].</summary>
    public int FontSize { get; init; } = DefaultFontSize;

    /// <summary>"plain", "regex", "fuzzy" or "any" — anything else is rejected on the way in.</summary>
    public string GrepMode { get; init; } = DefaultGrepMode;

    /// <summary>Whether Live Grep rows are filtered to definitions (Ctrl+D).</summary>
    public bool DefsOnly { get; init; }

    /// <summary>
    /// "phosphor", "dark", "light", "tokyo-night", "cyberpunk" or "catppuccin-{latte, frappe,
    /// macchiato, mocha}" (Ctrl+T) — anything else is rejected on the way in. Global, unlike the rest: see the remarks.
    /// </summary>
    public string Theme { get; init; } = DefaultTheme;

    /// <summary>
    /// Popup size in pixels, or 0 for "never set" — the caller then sizes from the screen. The two
    /// dimensions are stored and validated together: half a size is not a size.
    /// </summary>
    public int WindowWidth { get; init; }

    /// <inheritdoc cref="WindowWidth"/>
    public int WindowHeight { get; init; }

    /// <summary>The hand-edited global settings file, also read for fontFamily and opacity.</summary>
    internal static string GlobalSettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SeekyVS",
        "settings.json");

    /// <summary>
    /// Reads the stored state for <paramref name="workspaceDir"/>, falling back through the global
    /// settings file to the defaults. Never throws.
    /// </summary>
    internal static SeekyState Load(string? workspaceDir)
    {
        JsonObject? global = ReadJsonObject(GlobalSettingsPath);
        SeekyState state = Overlay(new SeekyState(), global);
        state = Overlay(state, ReadJsonObject(WorkspaceStatePath(workspaceDir)));

        // Global only — a theme left in a solution's state file must not override it.
        string? theme = global is null ? null : NormalizeTheme(TryString(global, "theme"));
        return theme is null ? state : state with { Theme = theme };
    }

    /// <summary>
    /// Writes this state to <paramref name="workspaceDir"/>'s state file, or to the global
    /// settings file when no solution is open. Never throws.
    /// </summary>
    internal void Save(string? workspaceDir)
    {
        string path = WorkspaceStatePath(workspaceDir) ?? GlobalSettingsPath;
        try
        {
            // Merged, not replaced: writing the global file would otherwise drop the fontFamily
            // and opacity the user hand-edited there, neither of which this type models.
            JsonObject root = ReadJsonObject(path) ?? new JsonObject();
            root["fontSize"] = FontSize;
            root["grepMode"] = GrepMode;
            root["defsOnly"] = DefsOnly;

            // Removed rather than written as 0 when unset: this file is hand-editable, and
            // "windowWidth": 0 reads as a broken setting rather than an absent one.
            if (WindowWidth > 0 && WindowHeight > 0)
            {
                root["windowWidth"] = WindowWidth;
                root["windowHeight"] = WindowHeight;
            }
            else
            {
                root.Remove("windowWidth");
                root.Remove("windowHeight");
            }

            if (path == GlobalSettingsPath)
            {
                root["theme"] = Theme;
            }

            WriteJsonObject(path, root);
            SeekyLog.Info($"state: saved to '{path}' (fontSize {FontSize}, grepMode {GrepMode}, defsOnly {DefsOnly})");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            SeekyLog.Error($"state: saving '{path}' failed", ex);
        }

        if (path != GlobalSettingsPath)
        {
            SaveThemeGlobally();
        }
    }

    /// <summary>
    /// Merges <see cref="Theme"/> into the global settings file, skipping the write when it
    /// already holds it — the common case, and a hand-edited file should not be rewritten for
    /// nothing on every popup close.
    /// </summary>
    private void SaveThemeGlobally()
    {
        try
        {
            JsonObject root = ReadJsonObject(GlobalSettingsPath) ?? new JsonObject();
            if (TryString(root, "theme") == Theme)
            {
                return;
            }

            root["theme"] = Theme;
            WriteJsonObject(GlobalSettingsPath, root);
            SeekyLog.Info($"state: theme '{Theme}' saved to '{GlobalSettingsPath}'");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            SeekyLog.Error($"state: saving the theme to '{GlobalSettingsPath}' failed", ex);
        }
    }

    private static void WriteJsonObject(string path, JsonObject root)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, root.ToJsonString(WriteOptions) + Environment.NewLine);
    }

    /// <summary>Applies a page-reported state, rejecting anything out of range.</summary>
    internal SeekyState With(int? fontSize, string? grepMode, bool? defsOnly, string? theme = null) => this with
    {
        FontSize = fontSize is null ? FontSize : ClampFontSize(fontSize.Value),
        GrepMode = NormalizeGrepMode(grepMode) ?? GrepMode,
        DefsOnly = defsOnly ?? DefsOnly,
        Theme = NormalizeTheme(theme) ?? Theme,
    };

    /// <summary>
    /// Records a popup size, or clears it when either dimension is below
    /// <see cref="MinWindowDimension"/> — which is how Ctrl+Shift+0 gets back to "size from the
    /// screen" rather than pinning the default as if the user had chosen it.
    /// </summary>
    internal SeekyState WithWindowSize(int width, int height)
    {
        bool usable = width >= MinWindowDimension && height >= MinWindowDimension;
        return this with
        {
            WindowWidth = usable ? width : 0,
            WindowHeight = usable ? height : 0,
        };
    }

    /// <summary>
    /// Beside fff's frecency and history databases. Null when no solution is open, which is what
    /// sends a write to the global file instead.
    /// </summary>
    private static string? WorkspaceStatePath(string? workspaceDir) =>
        string.IsNullOrEmpty(workspaceDir)
            ? null
            : Path.Combine(workspaceDir, ".vs", "seeky", "state.json");

    private static SeekyState Overlay(SeekyState state, JsonObject? json)
    {
        if (json is null)
        {
            return state;
        }

        SeekyState overlaid = state.With(
            TryInt(json, "fontSize"), TryString(json, "grepMode"), TryBool(json, "defsOnly"));

        // Only when the file actually carries a size — WithWindowSize would otherwise read a
        // missing key as 0 and clear a size inherited from the layer below.
        int? width = TryInt(json, "windowWidth");
        int? height = TryInt(json, "windowHeight");
        return width is null && height is null
            ? overlaid
            : overlaid.WithWindowSize(width ?? 0, height ?? 0);
    }

    private static JsonObject? ReadJsonObject(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            SeekyLog.Error($"state: unreadable '{path}'", ex);
            return null;
        }
    }

    // Math.Clamp does not exist on net472; the same clamp by hand.
    private static int ClampFontSize(int size) =>
        size < MinFontSize ? MinFontSize : size > MaxFontSize ? MaxFontSize : size;

    private static string? NormalizeGrepMode(string? mode) =>
        mode is "plain" or "regex" or "fuzzy" or "any" ? mode : null;

    // Must match the page's THEMES list and its [data-theme] stylesheet blocks.
    private static string? NormalizeTheme(string? theme) =>
        theme is "phosphor" or "dark" or "light" or "tokyo-night" or "cyberpunk"
            or "catppuccin-latte" or "catppuccin-frappe" or "catppuccin-macchiato" or "catppuccin-mocha"
            ? theme
            : null;

    // JsonNode's GetValue<T> throws on a type mismatch, and a hand-edited settings file is exactly
    // where "fontSize": "16" shows up. Probe instead, and let a wrong-typed key read as absent.
    private static int? TryInt(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue(out int result) ? result : null;

    private static bool? TryBool(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue(out bool result) ? result : null;

    private static string? TryString(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue(out string? result) ? result : null;
}
