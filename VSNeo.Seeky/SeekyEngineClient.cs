// The VSNeo side of the seeky-engine pipe. The protocol is documented in
// SeekyEngine/EngineServer.cs; this file must agree with it.

namespace VSNeo.Seeky;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Talks to <c>seeky-engine.exe</c>, the child process that runs Seeky's fff search engine,
/// so that a crash or a leak in the native engine ends that process and never Visual Studio.
/// </summary>
/// <remarks>
/// <para>
/// The engine starts on the first call, never at package load, and is put in a kill-on-close
/// job (<see cref="Infrastructure.ProcessJob"/>, as nvim is) and handed this process's id, so
/// it cannot outlive Visual Studio.
/// </para>
/// <para>
/// A dead engine fails its pending calls with <see cref="SeekyEngineException"/> and is
/// restarted by the next call. The second death in a session opens the circuit: the picker
/// reports the engine as stopped instead of restarting it into the same crash on every
/// keystroke. The rest of VSNeo never depends on the engine.
/// </para>
/// <para>
/// Thread-safe; nothing here touches the UI thread. Results come back as plain records built
/// from the reply JSON on the reader thread.
/// </para>
/// </remarks>
internal sealed partial class SeekyEngineClient : IDisposable
{
    /// <summary>Deaths after which the engine is not restarted again this session.</summary>
    private const int MaxDeaths = 2;

    internal enum GrepMode
    {
        Plain,
        Regex,
        Fuzzy,
        Any,
    }

    internal readonly record struct FileItem(
        string Path, long FrecencyScore, string? GitStatus, bool IsBinary, bool IsDirectory);

    /// <summary>A <c>:line[:col]</c> suffix fff parsed off a file query; 1-based.</summary>
    internal readonly record struct QueryLocation(int Line, int? Col);

    internal sealed record FileSearch(IReadOnlyList<FileItem> Items, QueryLocation? Location);

    internal readonly record struct GrepMatch(
        string Path, int Line, string Text, int Col, SeekyRange[] Ranges,
        string? GitStatus, bool IsBinary, bool IsDefinition);

    internal sealed record GrepResult(IReadOnlyList<GrepMatch> Matches, string? RegexFallbackError);

    internal readonly record struct SymbolHit(
        string Path, int Line, int Col, string Text, string Kind, string Name,
        SeekyRange[] NameRanges, string? GitStatus, bool IsBinary);

    private readonly Func<string> enginePath;
    private readonly Action<string>? status;
    private readonly SemaphoreSlim connectGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, PendingCall> pending = new();
    private readonly object connectionSync = new();
    private long nextId;

    private sealed record PendingCall(Connection Connection, TaskCompletionSource<JsonElement> Completion);
    private int deaths;
    private volatile bool disposed;

    // The live connection, or null. Disposal and publication synchronize on connectionSync.
    private Connection? connection;

    /// <param name="enginePath">Where seeky-engine.exe is; read on each (re)start.</param>
    /// <param name="status">
    /// Receives the engine's status notes ("indexing… 1200 files"). Called on the reader
    /// thread; must not block.
    /// </param>
    internal SeekyEngineClient(Func<string> enginePath, Action<string>? status)
    {
        this.enginePath = enginePath;
        this.status = status;
    }

    /// <summary>The engine next to the extension: <c>Seeky\Engine\seeky-engine.exe</c>.</summary>
    internal static string DefaultEnginePath() =>
        Path.Combine(
            Path.GetDirectoryName(typeof(SeekyEngineClient).Assembly.Location) ?? string.Empty,
            "Seeky", "Engine", "seeky-engine.exe");

    /// <summary>True once the engine died <see cref="MaxDeaths"/> times; calls then fail fast.</summary>
    internal bool IsStopped => Volatile.Read(ref deaths) >= MaxDeaths;

    /// <summary>The live engine's pid, for tests that must kill this client's child and no other.</summary>
    internal int? EngineProcessId => Volatile.Read(ref connection) is { IsAlive: true } c ? c.ProcessId : null;

    /// <summary>True when no engine is connected (not started yet, or it died).</summary>
    internal bool IsConnectionDead => Volatile.Read(ref connection) is not { IsAlive: true };

    // ------------------------------------------------------------------ operations

    /// <summary>Ensures an index for <paramref name="dir"/> and waits for its scan.</summary>
    internal Task StartAsync(string dir, CancellationToken ct) =>
        CallAsync("start", w => w.WriteString("dir", dir), ct);

    internal Task RefreshGitStatusAsync(CancellationToken ct) => CallAsync("refreshGit", null, ct);

    internal async Task<IReadOnlyList<string>> GetHistoryAsync(int max, CancellationToken ct)
    {
        JsonElement r = await CallAsync("history", w => w.WriteNumber("max", max), ct).ConfigureAwait(false);
        var list = new List<string>();
        if (r.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement q in r.EnumerateArray())
            {
                list.Add(q.GetString() ?? string.Empty);
            }
        }

        return list;
    }

    /// <summary>
    /// File search; <paramref name="currentFile"/> ranks lower (alternate file).
    /// <paramref name="match"/> is the page's fileMode: "fuzzy", "plain" or "glob".
    /// </summary>
    internal Task<FileSearch> FindFilesAsync(
        string query, string match, string? currentFile, int max, CancellationToken ct) =>
        FileSearchAsync("files", query, currentFile, max, ct, match);

    /// <summary>Files and folders in one fuzzy list (<c>fff_search_mixed</c>).</summary>
    internal Task<FileSearch> FindMixedAsync(string query, string? currentFile, int max, CancellationToken ct) =>
        FileSearchAsync("mixed", query, currentFile, max, ct);

    /// <summary>Files with a git status; an empty query lists them all, by frecency.</summary>
    internal async Task<IReadOnlyList<FileItem>> GitModifiedAsync(string query, int max, CancellationToken ct)
    {
        JsonElement r = await CallAsync("git", w => { w.WriteString("query", query); w.WriteNumber("max", max); }, ct)
            .ConfigureAwait(false);
        return ReadFiles(r);
    }

    internal async Task<GrepResult> GrepAsync(string query, GrepMode mode, int max, CancellationToken ct)
    {
        JsonElement r = await CallAsync(
            "grep",
            w =>
            {
                w.WriteString("query", query);
                w.WriteString("mode", mode switch
                {
                    GrepMode.Regex => "regex",
                    GrepMode.Fuzzy => "fuzzy",
                    GrepMode.Any => "any",
                    _ => "plain",
                });
                w.WriteNumber("max", max);
            },
            ct).ConfigureAwait(false);

        var matches = new List<GrepMatch>();
        if (r.TryGetProperty("matches", out JsonElement items))
        {
            foreach (JsonElement m in items.EnumerateArray())
            {
                matches.Add(new GrepMatch(
                    Str(m, "path") ?? string.Empty,
                    Int(m, "line"),
                    Str(m, "text") ?? string.Empty,
                    Int(m, "col"),
                    Ranges(m, "ranges"),
                    Str(m, "gitStatus"),
                    Bool(m, "isBinary"),
                    Bool(m, "isDefinition")));
            }
        }

        return new GrepResult(matches, Str(r, "regexError"));
    }

    /// <summary>
    /// Workspace symbols matching <paramref name="query"/> (an empty query lists them). The
    /// engine sweeps once per workspace and filters per call.
    /// </summary>
    internal async Task<IReadOnlyList<SymbolHit>> SymbolsAsync(string dir, string query, int max, CancellationToken ct)
    {
        JsonElement r = await CallAsync(
            "symbols",
            w =>
            {
                w.WriteString("dir", dir);
                w.WriteString("query", query);
                w.WriteNumber("max", max);
            },
            ct).ConfigureAwait(false);

        var hits = new List<SymbolHit>();
        if (r.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement s in r.EnumerateArray())
            {
                hits.Add(new SymbolHit(
                    Str(s, "path") ?? string.Empty,
                    Int(s, "line"),
                    Int(s, "col"),
                    Str(s, "text") ?? string.Empty,
                    Str(s, "kind") ?? string.Empty,
                    Str(s, "name") ?? string.Empty,
                    Ranges(s, "nameRanges"),
                    Str(s, "gitStatus"),
                    Bool(s, "isBinary")));
            }
        }

        // One row per class: a partial type is reported once per file.
        return SeekySymbolCollapse.PartialClasses(hits);
    }

    /// <summary>Frecency and history learning for a pick. <paramref name="path"/> absolute.</summary>
    internal Task TrackQueryAsync(string query, string path, CancellationToken ct) =>
        CallAsync("track", w => { w.WriteString("query", query); w.WriteString("path", path); }, ct);

    private async Task<FileSearch> FileSearchAsync(
        string op, string query, string? currentFile, int max, CancellationToken ct, string? match = null)
    {
        JsonElement r = await CallAsync(
            op,
            w =>
            {
                w.WriteString("query", query);
                if (currentFile is not null)
                {
                    w.WriteString("currentFile", currentFile);
                }

                if (match is not null)
                {
                    w.WriteString("match", match);
                }

                w.WriteNumber("max", max);
            },
            ct).ConfigureAwait(false);

        IReadOnlyList<FileItem> items =
            r.TryGetProperty("items", out JsonElement list) ? ReadFiles(list) : Array.Empty<FileItem>();
        QueryLocation? location = null;
        if (r.TryGetProperty("location", out JsonElement loc) && loc.ValueKind == JsonValueKind.Object)
        {
            int? col = loc.TryGetProperty("col", out JsonElement c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : null;
            location = new QueryLocation(Int(loc, "line"), col);
        }

        return new FileSearch(items, location);
    }

    private static IReadOnlyList<FileItem> ReadFiles(JsonElement array)
    {
        var files = new List<FileItem>();
        if (array.ValueKind != JsonValueKind.Array)
        {
            return files;
        }

        foreach (JsonElement f in array.EnumerateArray())
        {
            files.Add(new FileItem(
                Str(f, "path") ?? string.Empty,
                f.TryGetProperty("frecency", out JsonElement fr) && fr.TryGetInt64(out long score) ? score : 0,
                Str(f, "gitStatus"),
                Bool(f, "isBinary"),
                Bool(f, "isDirectory")));
        }

        return files;
    }

    // ------------------------------------------------------------------ transport

    /// <summary>
    /// Ends the engine: closing the pipe makes it exit on its own, destroying the fff instance
    /// cleanly. Never waits - this runs on the UI thread at package teardown - so the wait for
    /// that exit, and the job that kills an engine which does not, run in the background.
    /// </summary>
    public void Dispose()
    {
        Connection? last;
        lock (connectionSync)
        {
            disposed = true;
            last = Interlocked.Exchange(ref connection, null);
        }

        if (last is not null)
        {
            last.ClosePipe();
            _ = Task.Run(last.Dispose);
        }
    }
}

/// <summary>The engine reported an error, exited, or cannot be started.</summary>
internal sealed class SeekyEngineException : Exception
{
    internal SeekyEngineException(string message)
        : base(message)
    {
    }

    /// <summary>The engine is not installed: a packaging fault, not a crash, so it never trips the circuit.</summary>
    internal bool Missing { get; init; }
}
