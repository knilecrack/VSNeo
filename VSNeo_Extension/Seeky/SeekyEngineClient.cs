// The VSNeo side of the seeky-engine pipe. The protocol is documented in
// SeekyEngine/EngineServer.cs; this file must agree with it.

namespace VSNeo_Extension.Seeky;

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
/// A dead engine fails every pending call with <see cref="SeekyEngineException"/> and is
/// restarted by the next call. The second death in a session opens the circuit: the picker
/// reports the engine as stopped instead of restarting it into the same crash on every
/// keystroke. The rest of VSNeo never depends on the engine.
/// </para>
/// <para>
/// Thread-safe; nothing here touches the UI thread. Results come back as plain records built
/// from the reply JSON on the reader thread.
/// </para>
/// </remarks>
internal sealed class SeekyEngineClient : IDisposable
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
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private long nextId;
    private int deaths;
    private bool disposed;

    // The live connection, or null. Replaced as a unit under connectGate.
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

    /// <summary>Fuzzy file search; <paramref name="currentFile"/> ranks lower (alternate file).</summary>
    internal Task<FileSearch> FindFilesAsync(string query, string? currentFile, int max, CancellationToken ct) =>
        FileSearchAsync("files", query, currentFile, max, ct);

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

        return hits;
    }

    /// <summary>Frecency and history learning for a pick. <paramref name="path"/> absolute.</summary>
    internal Task TrackQueryAsync(string query, string path, CancellationToken ct) =>
        CallAsync("track", w => { w.WriteString("query", query); w.WriteString("path", path); }, ct);

    private async Task<FileSearch> FileSearchAsync(
        string op, string query, string? currentFile, int max, CancellationToken ct)
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
    /// Sends one request and awaits its reply's <c>result</c>. Cancelling abandons the reply
    /// and tells the engine to stop the work.
    /// </summary>
    private async Task<JsonElement> CallAsync(string op, Action<Utf8JsonWriter>? args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Connection conn = await EnsureConnectedAsync(ct).ConfigureAwait(false);

        long id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;

        byte[] request = Line(w =>
        {
            w.WriteNumber("id", id);
            w.WriteString("op", op);
            args?.Invoke(w);
        });

        using (ct.Register(() =>
        {
            if (pending.TryRemove(id, out TaskCompletionSource<JsonElement>? abandoned))
            {
                abandoned.TrySetCanceled();
                _ = SendAsync(conn, Line(w => { w.WriteString("op", "cancel"); w.WriteNumber("target", id); }));
            }
        }))
        {
            if (!await SendAsync(conn, request).ConfigureAwait(false))
            {
                pending.TryRemove(id, out _);
                throw new SeekyEngineException("the search engine connection was lost");
            }

            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private async Task<Connection> EnsureConnectedAsync(CancellationToken ct)
    {
        Connection? live = Volatile.Read(ref connection);
        if (live is { IsAlive: true })
        {
            return live;
        }

        await connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(SeekyEngineClient));
            }

            live = connection;
            if (live is { IsAlive: true })
            {
                return live;
            }

            if (IsStopped)
            {
                throw new SeekyEngineException(
                    "the search engine stopped after repeated crashes; restart Visual Studio to retry");
            }

            Connection started = await StartEngineAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref connection, started);
            _ = Task.Run(() => ReadLoopAsync(started));
            return started;
        }
        finally
        {
            connectGate.Release();
        }
    }

    private async Task<Connection> StartEngineAsync(CancellationToken ct)
    {
        string exe = enginePath();
        if (!File.Exists(exe))
        {
            throw new SeekyEngineException("the search engine is missing: " + exe);
        }

        using Process self = Process.GetCurrentProcess();
        string pipeName = $"vsneo-seeky-{self.Id}-{Guid.NewGuid():N}";
        string log = Path.Combine(Path.GetTempPath(), "vsneo-seeky-engine.log");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--pipe {pipeName} --parent {self.Id} --log \"{log}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
        };

        var process = new Process { StartInfo = psi };
        process.Start();
        SeekyLog.Info($"engine: started pid {process.Id} on pipe {pipeName}");

        // Before anything can go wrong, as for nvim: the job kills the engine with devenv.
        Infrastructure.ProcessJob? job = Infrastructure.ProcessJob.TryAssign(process);
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            // The engine creates the pipe within milliseconds of starting; ten seconds covers
            // a cold single-file extraction on a slow disk.
            await pipe.ConnectAsync(10_000, ct).ConfigureAwait(false);
        }
        catch
        {
            pipe.Dispose();
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // Already gone.
            }

            process.Dispose();
            job?.Dispose();
            throw;
        }

        return new Connection(process, job, pipe);
    }

    private async Task ReadLoopAsync(Connection conn)
    {
        try
        {
            using var reader = new StreamReader(conn.Pipe, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
            while (true)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length > 0)
                {
                    HandleLine(line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The engine went away; handled below like a clean end of stream.
        }
        catch (Exception ex)
        {
            SeekyLog.Error("engine: reader failed", ex);
        }

        OnDisconnected(conn);
    }

    private void HandleLine(string line)
    {
        JsonElement root;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            SeekyLog.Error("engine: unreadable reply", ex);
            return;
        }

        if (root.TryGetProperty("status", out JsonElement note))
        {
            try
            {
                status?.Invoke(note.GetString() ?? string.Empty);
            }
            catch (Exception ex)
            {
                SeekyLog.Error("engine: status handler failed", ex);
            }

            return;
        }

        if (!root.TryGetProperty("id", out JsonElement idElement) || !idElement.TryGetInt64(out long id)
            || !pending.TryRemove(id, out TaskCompletionSource<JsonElement>? tcs))
        {
            return; // a reply to a request that was cancelled meanwhile
        }

        if (root.TryGetProperty("error", out JsonElement error))
        {
            tcs.TrySetException(new SeekyEngineException(error.GetString() ?? "engine error"));
        }
        else
        {
            tcs.TrySetResult(root.TryGetProperty("result", out JsonElement result) ? result : default);
        }
    }

    private void OnDisconnected(Connection conn)
    {
        conn.MarkDead();
        bool expected = disposed;
        int exitCode = conn.TryGetExitCode();
        conn.Dispose();

        foreach (long id in pending.Keys)
        {
            if (pending.TryRemove(id, out TaskCompletionSource<JsonElement>? tcs))
            {
                tcs.TrySetException(new SeekyEngineException("the search engine exited"));
            }
        }

        if (expected)
        {
            return;
        }

        int count = Interlocked.Increment(ref deaths);
        SeekyLog.Info(
            $"engine: connection lost (exit code {exitCode}); death {count} of {MaxDeaths}"
            + (count >= MaxDeaths ? ", not restarting again this session" : ", the next search restarts it"));
    }

    private async Task<bool> SendAsync(Connection conn, byte[] line)
    {
        await writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!conn.IsAlive)
            {
                return false;
            }

            await conn.Pipe.WriteAsync(line, 0, line.Length).ConfigureAwait(false);
            await conn.Pipe.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>One request line: an object whose members <paramref name="members"/> writes, then '\n'.</summary>
    private static byte[] Line(Action<Utf8JsonWriter> members)
    {
        using var buffer = new MemoryStream(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            members(w);
            w.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int n) ? n : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static SeekyRange[] Ranges(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SeekyRange>();
        }

        var ranges = new List<SeekyRange>();
        foreach (JsonElement pair in list.EnumerateArray())
        {
            if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2)
            {
                ranges.Add(new SeekyRange(pair[0].GetInt32(), pair[1].GetInt32()));
            }
        }

        return ranges.ToArray();
    }

    /// <summary>
    /// Ends the engine: closing the pipe makes it exit on its own, destroying the fff instance
    /// cleanly. Never waits - this runs on the UI thread at package teardown - so the wait for
    /// that exit, and the job that kills an engine which does not, run in the background.
    /// </summary>
    public void Dispose()
    {
        disposed = true;
        Connection? last = Interlocked.Exchange(ref connection, null);
        if (last is not null)
        {
            last.ClosePipe();
            _ = Task.Run(last.Dispose);
        }
    }

    /// <summary>One engine process and the pipe to it.</summary>
    private sealed class Connection : IDisposable
    {
        private readonly Process process;
        private readonly Infrastructure.ProcessJob? job;
        private int dead;
        private int disposedFlag;

        internal Connection(Process process, Infrastructure.ProcessJob? job, NamedPipeClientStream pipe)
        {
            this.process = process;
            this.job = job;
            Pipe = pipe;
        }

        internal NamedPipeClientStream Pipe { get; }

        internal bool IsAlive => Volatile.Read(ref dead) == 0;

        internal void MarkDead() => Volatile.Write(ref dead, 1);

        internal int TryGetExitCode()
        {
            try
            {
                return process.WaitForExit(2000) ? process.ExitCode : -1;
            }
            catch
            {
                return -1;
            }
        }

        internal void ClosePipe()
        {
            MarkDead();
            try
            {
                Pipe.Dispose();
            }
            catch
            {
                // Best-effort teardown.
            }
        }

        /// <summary>Blocks up to two seconds for the exit; call off the UI thread.</summary>
        public void Dispose()
        {
            // Twice by design on teardown: the client's Dispose and the reader's disconnect.
            if (Interlocked.Exchange(ref disposedFlag, 1) != 0)
            {
                return;
            }

            ClosePipe();

            // A clean exit follows the pipe closing; the job is the backstop for one that
            // does not (and disposing it kills whatever is still running).
            if (!process.WaitForExit(2000))
            {
                SeekyLog.Info("engine: did not exit after its pipe closed; the job ends it");
            }

            job?.Dispose();
            process.Dispose();
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
}
