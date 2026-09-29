// SeekyVS — Visual Studio 2026 port spike for the Seeky VS Code extension.

namespace VSNeo_Extension.Seeky;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Search backend over the native fff C FFI library (<c>Tools/fff_c.dll</c>) — replaces the
/// fff-mcp stdio sidecar. One fff instance per workspace; workspace changes go through
/// <c>fff_restart_index</c> instead of recreating the instance.
/// </summary>
/// <remarks>
/// Memory management (per crates/fff-c/include/fff.h): every call returns a heap
/// <c>FffResult*</c> envelope freed with <c>fff_free_result</c>; the envelope does NOT own its
/// <c>handle</c> payload — payloads are freed separately (<c>fff_free_search_result</c>,
/// <c>fff_free_grep_result</c>, <c>fff_free_scan_progress</c>, <c>fff_destroy</c>). Fields come
/// through the accessor functions wherever the library exports one; the handful of structs read
/// directly (<c>FffCreateOptions</c>, <c>FffScanProgress</c>, <c>FffMatchRange</c>,
/// <c>FffDirItem</c>, the <c>FffDirSearchResult</c> header) are all blittable and read as plain
/// loads — via <see cref="Marshal.PtrToStructure{T}(IntPtr)"/> on the cold paths and paired
/// <see cref="Marshal.ReadInt32(IntPtr)"/> loads per match range, because this assembly compiles
/// without unsafe code (the .NET original dereferenced the pointers directly). Every pointer is
/// therefore either freed exactly once here or owned by the native instance. All native calls
/// are serialized through a single gate — fff's thread-safety guarantees are undocumented, and
/// searches are fast enough that contention is theoretical.
/// </remarks>
internal sealed class FffNativeClient : IDisposable
{
    private const string LibraryName = "fff_c.dll";
    private const uint CreateOptionsVersion = 2; // FFF_CREATE_OPTIONS_VERSION
    private const int ScanWaitTimeoutMs = 30_000;
    private const int DisposeGateTimeoutMs = 2_000;
    private const int HangCheckPeriodMs = 5_000;
    private const int SlowCallMs = 1_000;

    /// <summary>Files searched per <c>fff_live_grep</c> call (its <c>page_limit</c>).</summary>
    internal const uint c_FilePageLimit = 512;

    private const int MaxGrepPages = 400;
    private const int GrepBudgetMs = 3_000;

    /// <summary>
    /// Ranked files pulled per "Git Modified" query before the git-status filter is applied. Large
    /// because the filter is client-side and a modified file can sit anywhere in the fuzzy ranking,
    /// so anything smaller silently hides modified files. It is a ceiling, not a cost: fff returns
    /// what actually matched, which for a typed query is a small set, and this side rejects
    /// unmodified candidates on the native pointer without marshalling them. Only "show me
    /// everything" on a very large workspace pays for the whole pool, and that is one deliberate
    /// keystroke rather than a per-character cost.
    /// </summary>
    private const uint GitModifiedPoolSize = 20_000;

    private static readonly object LoaderLock = new();
    private static bool libraryLoaded;

    /// <summary>
    /// Serializes every native call. A <see cref="SemaphoreSlim"/> rather than a <c>lock</c> so
    /// waiters can be awaited and cancelled: searches run per keystroke while the symbol sweep
    /// can hold the gate for seconds at a time, and blocking pool threads on that is how you
    /// starve the thread pool under typing.
    /// </summary>
    private readonly SemaphoreSlim gate = new(1, 1);

    private IntPtr handle;
    private string? workspaceDir;
    private bool scanWaitCompleted;
    private int disposed;

    /// <summary>
    /// The one timer behind <see cref="CallWithWatchdog"/>, created on the first native call.
    /// </summary>
    private Timer? hangTimer;

    /// <summary>
    /// <see cref="Stopwatch"/> timestamp of the native call currently in flight, 0 when idle.
    /// Written around every watched call, read by <see cref="ReportIfHung"/>.
    /// </summary>
    private long inFlightSince;

    private string inFlightName = string.Empty;

    /// <summary>
    /// Bumped whenever the underlying index is replaced (<c>fff_restart_index</c>). Paged
    /// operations capture it and abort if it moves: a <c>file_offset</c> from the previous index
    /// means nothing to the new one, so continuing would silently skip or repeat files.
    /// </summary>
    private int workspaceGeneration;

    /// <summary>Grep sub-mode for <c>fff_live_grep</c> (0 = plain SIMD, 1 = regex, 2 = fuzzy).</summary>
    internal enum GrepMode : byte
    {
        Plain = 0,
        Regex = 1,
        Fuzzy = 2,
    }

    /// <summary>A fuzzy file-search result.</summary>
    internal readonly record struct FileItem(string Path, long FrecencyScore, string? GitStatus, bool IsBinary);

    /// <summary>A directory-search result (fff_search_directories).</summary>
    internal readonly record struct DirItem(string Path, string Name);

    /// <summary>
    /// A single grep match. <paramref name="Ranges"/> holds the highlight spans as
    /// (start, end) UTF-16 char indices into <paramref name="Text"/> (the native side reports
    /// them as byte offsets into the UTF-8 line; converted here). Empty when the backend
    /// provides no ranges.
    /// </summary>
    internal readonly record struct GrepMatch(
        string Path, int Line, string Text, int Col, SeekyRange[] Ranges,
        string? GitStatus, bool IsBinary, bool IsDefinition);

    /// <summary>Grep results plus the regex-fallback notice, if any.</summary>
    internal sealed record GrepResult(IReadOnlyList<GrepMatch> Matches, string? RegexFallbackError);

    /// <summary>
    /// One page of grep results. <paramref name="NextFileOffset"/> is 0 when the search reached
    /// the end of the file set; otherwise it is the offset to pass to the next call.
    /// </summary>
    internal sealed record GrepPage(
        IReadOnlyList<GrepMatch> Matches, string? RegexFallbackError, uint NextFileOffset, uint TotalFiles);

    /// <summary>
    /// Ensures an fff instance exists for <paramref name="dir"/>: creates it on first use,
    /// restarts the index when the workspace changed, no-ops otherwise. Waits for the initial
    /// scan to finish and reports progress through <paramref name="reportStatus"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="reportStatus"/> is invoked while the gate is held — it must not call back
    /// into this client or block on the UI thread.
    /// </remarks>
    public Task StartAsync(string dir, Action<string>? reportStatus, CancellationToken cancellationToken)
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace is .NET 7+; the same two throws by hand.
        if (dir is null)
        {
            throw new ArgumentNullException(nameof(dir));
        }

        if (string.IsNullOrWhiteSpace(dir))
        {
            throw new ArgumentException(
                "The value cannot be an empty string or composed entirely of whitespace.", nameof(dir));
        }

        ThrowIfDisposed();

        return Task.Run(
            async () =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    EnsureInstanceCore(dir, reportStatus, cancellationToken);
                }
                finally
                {
                    gate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>Fuzzy file search; returns workspace-relative paths with frecency scores.
    /// <paramref name="currentFile"/> deprioritizes the currently open file (fff current_file).</summary>
    public Task<IReadOnlyList<FileItem>> FindFilesAsync(string query, string? currentFile, int maxResults, CancellationToken cancellationToken)
    {
        ThrowIfNegativeOrZero(maxResults, nameof(maxResults));
        ThrowIfDisposed();

        return Task.Run<IReadOnlyList<FileItem>>(
            async () =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfNotStartedCore();
                    return SearchFilesCore(
                        query, currentFile, (uint)maxResults, useGlob: false, maxResults,
                        gitModifiedOnly: false, out _);
                }
                finally
                {
                    gate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>Fuzzy directory search (fff_search_directories); paths are workspace-relative.</summary>
    public Task<IReadOnlyList<DirItem>> FindDirectoriesAsync(string query, string? currentFile, int maxResults, CancellationToken cancellationToken)
    {
        ThrowIfNegativeOrZero(maxResults, nameof(maxResults));
        ThrowIfDisposed();

        return Task.Run<IReadOnlyList<DirItem>>(
            async () =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfNotStartedCore();

                    IntPtr result = CallWithWatchdog(
                        "search_directories",
                        () => Native.fff_search_directories(handle, ToUtf8(query), ToUtf8(currentFile), 0, 0, (uint)maxResults));
                    IntPtr payload = UnwrapResult(result, "search_directories");
                    try
                    {
                        // A successful FffResult with a null handle would otherwise reach the
                        // header read below, where the struct read dereferences null.
                        if (payload == IntPtr.Zero)
                        {
                            return (IReadOnlyList<DirItem>)Array.Empty<DirItem>();
                        }

                        // v0.10.1 has no fff_dir_search_result_get_count export (it exists only
                        // on main) — read the count from the result struct header instead.
                        uint count = Marshal.PtrToStructure<FffDirSearchResultHeader>(payload).Count;
                        var items = new List<DirItem>((int)count);
                        for (uint i = 0; i < count; i++)
                        {
                            IntPtr item = Native.fff_dir_search_result_get_item(payload, i);
                            if (item == IntPtr.Zero)
                            {
                                continue;
                            }

                            // FffDirItem { char* relative_path; char* dir_name; i32 frecency } —
                            // the header exposes no accessors, so read the tiny struct directly.
                            FffDirItem native = Marshal.PtrToStructure<FffDirItem>(item);
                            string? path = PtrToStringUtf8(native.RelativePath);
                            if (path is not null)
                            {
                                items.Add(new DirItem(path, PtrToStringUtf8(native.DirName) ?? path));
                            }
                        }

                        return (IReadOnlyList<DirItem>)items;
                    }
                    finally
                    {
                        Native.fff_free_dir_search_result(payload);
                    }
                }
                finally
                {
                    gate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Recent queries from fff's history LMDB (fff_get_historical_query; 0 = most recent).
    /// History is populated by fff_track_query, i.e. queries that led to a picked result.
    /// </summary>
    public Task<IReadOnlyList<string>> GetHistoryAsync(int max, CancellationToken cancellationToken)
    {
        if (max < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, "The value must be non-negative.");
        }

        ThrowIfDisposed();

        return Task.Run<IReadOnlyList<string>>(
            async () =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var queries = new List<string>();
                    if (handle == IntPtr.Zero)
                    {
                        return (IReadOnlyList<string>)queries;
                    }

                    // Counted in int, not ulong: 'offset < (ulong)max' with a negative max wraps to
                    // ~1.8e19 and spins native history calls while holding the gate.
                    for (int offset = 0; offset < max; offset++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        IntPtr result = Native.fff_get_historical_query(handle, (ulong)offset);
                        IntPtr payload = UnwrapResult(result, "get_historical_query");
                        if (payload == IntPtr.Zero)
                        {
                            break; // no more history
                        }

                        try
                        {
                            string? query = PtrToStringUtf8(payload);
                            // Spelled out rather than string.IsNullOrEmpty: on net472 that check
                            // carries no NotNullWhen annotation, so it wouldn't prove query non-null.
                            if (query is null || query.Length == 0)
                            {
                                break;
                            }

                            queries.Add(query);
                        }
                        finally
                        {
                            Native.fff_free_string(payload);
                        }
                    }

                    return (IReadOnlyList<string>)queries;
                }
                finally
                {
                    gate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// "Git Modified" mode: files with a non-empty git status. Runs the normal fuzzy search and
    /// filters client-side. An empty query means "all modified files, ranked by frecency" — if
    /// <c>fff_search</c> returns nothing for it, fall back to <c>fff_glob</c> with '*'
    /// (glob-only search ranked by frecency, per the header).
    /// </summary>
    /// <remarks>
    /// fff exposes no git-status filter, so the filtering happens here — which means the pool it
    /// is applied to has to be much larger than the result count. Asking for <c>maxResults</c>
    /// ranked files and filtering those (as this did) shows only the modified files that happen
    /// to land in the fuzzy top hundred, so a repo with dozens of modified files reports two.
    /// </remarks>
    public Task<IReadOnlyList<FileItem>> GitModifiedAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        ThrowIfNegativeOrZero(maxResults, nameof(maxResults));
        ThrowIfDisposed();

        return Task.Run<IReadOnlyList<FileItem>>(
            async () =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfNotStartedCore();

                    List<FileItem> items = SearchFilesCore(
                        query, null, GitModifiedPoolSize, useGlob: false, maxResults, gitModifiedOnly: true,
                        out uint rankedCount);
                    if (rankedCount == 0 && query.Length == 0)
                    {
                        SeekyLog.Info("fff: empty-query search returned nothing; falling back to fff_glob '*'");
                        items = SearchFilesCore(
                            "*", null, GitModifiedPoolSize, useGlob: true, maxResults, gitModifiedOnly: true,
                            out _);
                    }

                    return items;
                }
                finally
                {
                    gate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>Asks fff to refresh its git-status cache (best-effort; logs the update count).</summary>
    public Task RefreshGitStatusAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        return Task.Run(
            async () =>
            {
                try
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (handle == IntPtr.Zero)
                        {
                            return;
                        }

                        IntPtr result = Native.fff_refresh_git_status(handle);
                        _ = UnwrapResult(result, "refresh_git_status", out long updated);
                        SeekyLog.Info($"fff refresh_git_status: {updated} files updated");
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                catch (Exception ex)
                {
                    SeekyLog.Error("fff refresh_git_status failed", ex);
                }
            },
            cancellationToken);
    }

    /// <param name="pageSize">How many ranked files to ask fff for.</param>
    /// <param name="maxItems">How many to keep after filtering.</param>
    /// <param name="gitModifiedOnly">
    /// Keep only files with a non-empty git status. Tested on the native pointer before anything
    /// is marshalled, so an over-fetched candidate pool costs accessor calls rather than strings.
    /// </param>
    /// <param name="rankedCount">
    /// How many files fff ranked, before <paramref name="gitModifiedOnly"/> filtering — the caller
    /// needs to tell "the search found nothing" apart from "nothing it found was modified".
    /// </param>
    private List<FileItem> SearchFilesCore(
        string query,
        string? currentFile,
        uint pageSize,
        bool useGlob,
        int maxItems,
        bool gitModifiedOnly,
        out uint rankedCount)
    {
        IntPtr result = useGlob
            ? CallWithWatchdog("glob", () => Native.fff_glob(handle, ToUtf8(query), ToUtf8(currentFile), 0, 0, pageSize))
            : CallWithWatchdog("search", () => Native.fff_search(handle, ToUtf8(query), ToUtf8(currentFile), 0, 0, pageSize, 0, 0));
        IntPtr payload = UnwrapResult(result, useGlob ? "glob" : "search");
        try
        {
            uint count = Native.fff_search_result_get_count(payload);
            rankedCount = count;
            var items = new List<FileItem>((int)Math.Min(count, (uint)maxItems));
            for (uint i = 0; i < count && items.Count < maxItems; i++)
            {
                IntPtr item = Native.fff_search_result_get_item(payload, i);
                if (item == IntPtr.Zero)
                {
                    continue;
                }

                IntPtr gitStatus = Native.fff_file_item_get_git_status(item);
                if (gitModifiedOnly && IsNullOrEmptyUtf8(gitStatus))
                {
                    continue;
                }

                string? path = PtrToStringUtf8(Native.fff_file_item_get_relative_path(item));
                if (path is not null)
                {
                    items.Add(new FileItem(
                        path,
                        Native.fff_file_item_get_total_frecency_score(item),
                        PtrToStringUtf8(gitStatus),
                        Native.fff_file_item_get_is_binary(item)));
                }
            }

            return items;
        }
        finally
        {
            Native.fff_free_search_result(payload);
        }
    }

    /// <summary>
    /// Content search in the given mode; the query is passed raw (fff parses
    /// <c>*.cs pattern</c>-style constraints itself). Pages through the file set until
    /// <paramref name="maxResults"/> matches are collected or the files run out.
    /// </summary>
    public Task<GrepResult> GrepAsync(string query, GrepMode mode, int maxResults, CancellationToken cancellationToken)
    {
        ThrowIfNegativeOrZero(maxResults, nameof(maxResults));
        ThrowIfDisposed();

        return Task.Run(
            async () =>
            {
                var matches = new List<GrepMatch>(maxResults);
                string? fallbackError = null;
                uint fileOffset = 0;
                var stopwatch = Stopwatch.StartNew();
                int generation = Volatile.Read(ref workspaceGeneration);

                // fff's page_limit counts FILES SEARCHED, not matches: a single call stops after
                // page_limit files and reports where to resume. Passing maxResults straight
                // through (as this did) silently searched only the first 100 files of the
                // workspace and reported the result as complete.
                for (int page = 0; page < MaxGrepPages; page++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    GrepPage result;
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        // The gate is released between pages, so the workspace can be swapped
                        // mid-sweep; a file_offset from the old index would then address the
                        // wrong files. Abandon what we have rather than return a mixture.
                        if (Volatile.Read(ref workspaceGeneration) != generation)
                        {
                            SeekyLog.Info($"fff grep: workspace changed mid-sweep after {page} page(s); discarding");
                            return new GrepResult([], fallbackError);
                        }

                        // Only what is still missing is marshalled: a broad query ('e', 'public')
                        // can match tens of thousands of lines in one 512-file page, and every one
                        // costs three native string decodes plus its highlight ranges.
                        result = GrepPageCore(
                            query, mode, fileOffset, c_FilePageLimit, maxResults - matches.Count, withRanges: true);
                    }
                    finally
                    {
                        gate.Release();
                    }

                    if (fallbackError is null && result.RegexFallbackError is not null)
                    {
                        fallbackError = result.RegexFallbackError;
                        SeekyLog.Info($"fff live_grep: regex fallback: {fallbackError}");
                    }

                    matches.AddRange(result.Matches);
                    if (matches.Count >= maxResults)
                    {
                        return new GrepResult(matches, fallbackError);
                    }

                    if (result.NextFileOffset == 0 || stopwatch.ElapsedMilliseconds > GrepBudgetMs)
                    {
                        break;
                    }

                    fileOffset = result.NextFileOffset;
                }

                return new GrepResult(matches, fallbackError);
            },
            cancellationToken);
    }

    /// <summary>
    /// A single grep page starting at <paramref name="fileOffset"/>, searching at most
    /// <paramref name="filePageLimit"/> files. Used by <see cref="SymbolIndex"/> to sweep the
    /// whole workspace; most callers want <see cref="GrepAsync"/>.
    /// </summary>
    /// <param name="withRanges">
    /// False to leave <see cref="GrepMatch.Ranges"/> empty. A sweep that reads only the line text
    /// pays for the byte-offset translation of every match otherwise.
    /// </param>
    public Task<GrepPage> GrepPageAsync(
        string query,
        GrepMode mode,
        uint fileOffset,
        uint filePageLimit,
        CancellationToken cancellationToken,
        bool withRanges = true)
    {
        if (filePageLimit == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(filePageLimit), filePageLimit, "The value must be non-zero.");
        }

        ThrowIfDisposed();

        return Task.Run(
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return GrepPageCore(query, mode, fileOffset, filePageLimit, int.MaxValue, withRanges);
                }
                finally
                {
                    gate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// The current index generation, for callers that page across multiple
    /// <see cref="GrepPageAsync"/> calls (see <see cref="SymbolIndex"/>): capture it before the
    /// first page and re-check it after each one, because a <c>file_offset</c> is only meaningful
    /// against the index that produced it.
    /// </summary>
    public int WorkspaceGeneration => Volatile.Read(ref workspaceGeneration);

    /// <summary>Caller must hold <see cref="gate"/>.</summary>
    /// <param name="maxMatches">
    /// Stop marshalling after this many matches. The native search has already run by then — this
    /// bounds the managed half, which is the expensive one.
    /// </param>
    /// <param name="withRanges">False to skip highlight-range translation entirely.</param>
    private GrepPage GrepPageCore(
        string query, GrepMode mode, uint fileOffset, uint filePageLimit, int maxMatches, bool withRanges)
    {
        ThrowIfNotStartedCore();

        IntPtr result = CallWithWatchdog("live_grep", () => Native.fff_live_grep(
            handle,
            ToUtf8(query),
            (byte)mode,
            maxFileSize: 0,
            maxMatchesPerFile: 0,
            smartCase: true,
            fileOffset: fileOffset,
            pageLimit: filePageLimit,
            timeBudgetMs: 0,
            beforeContext: 0,
            afterContext: 0,
            classifyDefinitions: true));
        IntPtr payload = UnwrapResult(result, "live_grep");
        try
        {
            // Reported, not logged: every page of a paged sweep repeats the same fallback, and
            // SeekyLog writes each line with its own open/append/close under a global lock — 400
            // of those, while this call holds the gate, for one bad regex. Callers log it once.
            string? fallbackError = PtrToStringUtf8(Native.fff_grep_result_get_regex_fallback_error(payload));

            uint count = Native.fff_grep_result_get_count(payload);
            var matches = new List<GrepMatch>((int)Math.Min(count, (uint)maxMatches));
            var pathCache = new Utf8StringCache();
            for (uint i = 0; i < count && matches.Count < maxMatches; i++)
            {
                IntPtr match = Native.fff_grep_result_get_match(payload, i);
                if (match == IntPtr.Zero)
                {
                    continue;
                }

                IntPtr pathPtr = Native.fff_grep_match_get_relative_path(match);
                if (pathPtr == IntPtr.Zero)
                {
                    continue;
                }

                string path = pathCache.GetOrDecode(pathPtr, NullTerminatedByteCount(pathPtr));

                // The line is taken as raw native bytes rather than through PtrToStringUtf8 because
                // the match ranges are byte offsets into exactly these bytes. Decoding to a string
                // and re-encoding does not round-trip: bytes fff accepted but .NET rejects come
                // back as U+FFFD, three bytes where the original was one, sliding every later
                // offset.
                byte[] lineBytes = ReadNullTerminatedBytes(Native.fff_grep_match_get_line_content(match));
                string text = Encoding.UTF8.GetString(lineBytes);

                // is_definition is read but never trusted: fff_c.dll v0.10.1 reports false for
                // every match, including its own documented cases ('class Foo', 'fn bar').
                // SymbolClassifier does the real classification — see SymbolIndex.
                matches.Add(new GrepMatch(
                    path,
                    checked((int)Native.fff_grep_match_get_line_number(match)),
                    text,
                    (int)Native.fff_grep_match_get_col(match),
                    withRanges ? ReadMatchRanges(match, lineBytes) : [],
                    PtrToStringUtf8(Native.fff_grep_match_get_git_status(match)),
                    Native.fff_grep_match_get_is_binary(match),
                    Native.fff_grep_match_get_is_definition(match)));
            }

            return new GrepPage(
                matches,
                fallbackError,
                Native.fff_grep_result_get_next_file_offset(payload),
                Native.fff_grep_result_get_total_files(payload));
        }
        finally
        {
            Native.fff_free_grep_result(payload);
        }
    }

    /// <summary>
    /// Wraps a native call with a hang detector: while the call is still running a WATCHDOG line
    /// is logged every <see cref="HangCheckPeriodMs"/>ms (a hung fff call otherwise looks identical
    /// to "no results"). Slow-but-finished calls over <see cref="SlowCallMs"/>ms are logged once,
    /// on completion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reporting is <b>periodic</b>, not one-shot: a wedged call keeps reporting (5s, 10s, 15s…)
    /// so the log shows it is still stuck rather than leaving a single line and silence.
    /// </para>
    /// <para>
    /// One long-lived timer for the whole client, not one per call. This wraps every native entry
    /// point — ~130 <c>live_grep</c> calls per symbol sweep plus one per keystroke — and a
    /// per-call <see cref="Timer"/> costs a timer object, a closure, a <see cref="Stopwatch"/> and
    /// two <c>TimerQueue</c> lock acquisitions each. Since <see cref="gate"/> serializes every
    /// native call, at most one is ever in flight, so a single timer reading one timestamp field
    /// does the same job for two volatile writes per call. The idle cost is one callback every
    /// five seconds that reads a <see cref="long"/> and returns.
    /// </para>
    /// </remarks>
    private IntPtr CallWithWatchdog(string name, Func<IntPtr> call)
    {
        EnsureHangTimer();
        long start = Stopwatch.GetTimestamp();
        inFlightName = name;
        Volatile.Write(ref inFlightSince, start);
        try
        {
            return call();
        }
        finally
        {
            // In a finally so a throwing call (a loader DllNotFoundException, say) cannot leave
            // the slot armed and the watchdog reporting a hang that already ended.
            Volatile.Write(ref inFlightSince, 0);
            long elapsedMs = ElapsedMillisecondsSince(start);
            if (elapsedMs > SlowCallMs)
            {
                SeekyLog.Info($"fff {name} took {elapsedMs}ms");
            }
        }
    }

    // Stopwatch.GetElapsedTime is .NET 7+; the same conversion by hand for net472.
    private static long ElapsedMillisecondsSince(long startTimestamp) =>
        (long)((Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency);

    private void EnsureHangTimer()
    {
        if (hangTimer is not null)
        {
            return;
        }

        var timer = new Timer(
            static state => ((FffNativeClient)state!).ReportIfHung(),
            this,
            HangCheckPeriodMs,
            HangCheckPeriodMs);

        // Kept only if the field was empty AND this client is still alive. A scheduled Timer is
        // rooted by the timer queue, so one published after Dispose would go on firing every five
        // seconds for the life of the process with nothing left to report on.
        if (Interlocked.CompareExchange(ref hangTimer, timer, null) is null
            && Volatile.Read(ref disposed) == 0)
        {
            return;
        }

        Interlocked.CompareExchange(ref hangTimer, null, timer);
        timer.Dispose();
    }

    /// <summary>
    /// Logs the in-flight native call if it has been running long enough to look wedged. Must
    /// never throw — an unhandled exception on a timer callback takes down the process, and a hang
    /// <i>detector</i> has no business failing the host it is watching. <see cref="SeekyLog"/>
    /// swallows its own errors, which is the whole of the guarantee.
    /// </summary>
    private void ReportIfHung()
    {
        long since = Volatile.Read(ref inFlightSince);
        if (since == 0)
        {
            return;
        }

        long elapsedMs = ElapsedMillisecondsSince(since);
        if (elapsedMs >= HangCheckPeriodMs)
        {
            // inFlightName may lag inFlightSince by an instruction; the call has to have been
            // running for seconds to get here, so a name that stale cannot be the one reported.
            SeekyLog.Info($"WATCHDOG: fff {inFlightName} still running after {elapsedMs}ms (possible native hang)");
        }
    }

    /// <summary>
    /// Records a picked result for frecency learning (<c>fff_track_query</c>). Best-effort:
    /// failures are logged, never thrown.
    /// </summary>
    public Task TrackQueryAsync(string query, string relativePath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        return Task.Run(
            async () =>
            {
                try
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (handle == IntPtr.Zero)
                        {
                            return;
                        }

                        IntPtr result = Native.fff_track_query(handle, ToUtf8(query), ToUtf8(relativePath));
                        _ = UnwrapResult(result, "track_query", out long ok);
                        SeekyLog.Info($"fff track_query('{query}', '{relativePath}'): {(ok == 1 ? "ok" : "failed")}");
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                catch (Exception ex)
                {
                    SeekyLog.Error("fff track_query failed", ex);
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Waits for the in-flight native call to finish before destroying the instance — freeing the
    /// handle underneath a running <c>fff_*</c> call would fault in native code. The gate itself
    /// is deliberately NOT disposed: a queued waiter would then throw
    /// <see cref="ObjectDisposedException"/> from <c>WaitAsync</c> instead of the clean
    /// <c>ThrowIfDisposed</c>/no-op path, and a bare <see cref="SemaphoreSlim"/> holds nothing
    /// worth reclaiming.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        // Before the gate wait, so it happens even on the timeout path below.
        Interlocked.Exchange(ref hangTimer, null)?.Dispose();

        // Bounded, because this runs on the extension-unload path and a background symbol sweep
        // can hold the gate for its whole build budget with nothing able to cancel it. Skipping
        // fff_destroy leaks an instance in a process that is already going away; blocking the
        // unload for twenty seconds is the worse trade.
        if (!gate.Wait(DisposeGateTimeoutMs))
        {
            SeekyLog.Info(
                $"fff: a native call still held the gate after {DisposeGateTimeoutMs}ms; skipping destroy");
            return;
        }

        try
        {
            if (handle != IntPtr.Zero)
            {
                SeekyLog.Info("fff: destroying instance");
                Native.fff_destroy(handle);
                handle = IntPtr.Zero;
                workspaceDir = null;
                scanWaitCompleted = false;
                Interlocked.Increment(ref workspaceGeneration);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // ObjectDisposedException.ThrowIf is .NET 7+; the same throw by hand.
    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    // ArgumentOutOfRangeException.ThrowIfNegativeOrZero is .NET 7+; the same throw by hand.
    private static void ThrowIfNegativeOrZero(int value, string paramName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "The value must be greater than zero.");
        }
    }

    // ------------------------------------------------------------------ instance lifecycle

    private void EnsureInstanceCore(
        string dir,
        Action<string>? reportStatus,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Re-checked here under the gate, not only at the public entry point: a StartAsync that
        // cleared ThrowIfDisposed just before Dispose ran would otherwise reach
        // fff_create_instance_with below and leave a live instance behind — watcher threads, open
        // LMDBs — that nothing owns and nothing will ever destroy.
        ThrowIfDisposed();
        EnsureLibraryLoaded();

        bool sameWorkspace = handle != IntPtr.Zero
            && workspaceDir is not null
            && string.Equals(
                TrimTrailingSeparators(workspaceDir),
                TrimTrailingSeparators(dir),
                StringComparison.OrdinalIgnoreCase);
        if (sameWorkspace && scanWaitCompleted)
        {
            return;
        }

        if (handle != IntPtr.Zero && !sameWorkspace)
        {
            SeekyLog.Info($"fff: restarting index for '{dir}' (was '{workspaceDir}')");
            reportStatus?.Invoke("reindexing…");
            IntPtr restartResult = Native.fff_restart_index(handle, ToUtf8(dir));
            UnwrapResult(restartResult, "restart_index");
            workspaceDir = dir;
            scanWaitCompleted = false;

            // Any file_offset held by an in-flight paged sweep now refers to the old index.
            Interlocked.Increment(ref workspaceGeneration);
        }
        else if (handle == IntPtr.Zero)
        {
            string stateDir = Path.Combine(dir, ".vs", "seeky");
            Directory.CreateDirectory(stateDir);

            IntPtr basePath = Utf8ToCoTaskMem(dir);
            IntPtr frecencyDb = Utf8ToCoTaskMem(Path.Combine(stateDir, "frecency.db"));
            IntPtr historyDb = Utf8ToCoTaskMem(Path.Combine(stateDir, "history.db"));
            IntPtr logFile = Utf8ToCoTaskMem(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeekyVS", "fff.log"));
            IntPtr logLevel = Utf8ToCoTaskMem("info");
            try
            {
                var options = new FffCreateOptions
                {
                    Version = CreateOptionsVersion,
                    BasePath = basePath,
                    FrecencyDbPath = frecencyDb,
                    HistoryDbPath = historyDb,
                    EnableMmapCache = 0,
                    EnableContentIndexing = 1,
                    Watch = 1,
                    AiMode = 0,
                    LogFilePath = logFile,
                    LogLevel = logLevel,
                    CacheBudgetMaxFiles = 0,
                    CacheBudgetMaxBytes = 0,
                    CacheBudgetMaxFileSize = 0,
                    EnableFsRootScanning = 0,
                    EnableHomeDirScanning = 0,
                    FollowSymlinks = 0,
                };
                SeekyLog.Info($"fff: creating instance for '{dir}' (dll '{GetLibraryPath()}')");
                IntPtr result = Native.fff_create_instance_with(in options);
                handle = UnwrapResult(result, "create_instance_with");
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("fff create_instance_with returned a null handle");
                }

                workspaceDir = dir;
                scanWaitCompleted = false;
                SeekyLog.Info("fff: instance created");
            }
            finally
            {
                Marshal.FreeCoTaskMem(basePath);
                Marshal.FreeCoTaskMem(frecencyDb);
                Marshal.FreeCoTaskMem(historyDb);
                Marshal.FreeCoTaskMem(logFile);
                Marshal.FreeCoTaskMem(logLevel);
            }
        }

        // Wait for the (re)scan, polling progress for the status line.
        var waitStart = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reportStatus?.Invoke($"indexing… {GetScannedFileCount()} files");
            IntPtr waitResult = Native.fff_wait_for_scan(handle, 500);
            _ = UnwrapResult(waitResult, "wait_for_scan", out long completed);
            if (completed == 1)
            {
                break;
            }

            if (waitStart.ElapsedMilliseconds > ScanWaitTimeoutMs)
            {
                SeekyLog.Info("fff: scan wait timed out; continuing with a partial index");
                break;
            }
        }

        scanWaitCompleted = true;
        ulong scannedFiles = GetScannedFileCount();
        reportStatus?.Invoke($"index ready — {scannedFiles} files");
        SeekyLog.Info($"fff: scan complete in {waitStart.ElapsedMilliseconds}ms ({scannedFiles} files)");
    }

    /// <summary>
    /// Workspace roots for comparison only — 'O:\repo' and 'O:\repo\' name the same workspace, and
    /// telling them apart costs a full <c>fff_restart_index</c>. Never used for the path handed to
    /// the native side, so collapsing a drive root ('C:\' to 'C:') is harmless: both sides of the
    /// comparison go through here. Allocates nothing when there is nothing to trim.
    /// </summary>
    private static string TrimTrailingSeparators(string dir) =>
        dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private void ThrowIfNotStartedCore()
    {
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The fff search instance has not been started.");
        }
    }

    private ulong GetScannedFileCount()
    {
        IntPtr result = Native.fff_get_scan_progress(handle);
        IntPtr payload = UnwrapResult(result, "get_scan_progress");
        try
        {
            if (payload == IntPtr.Zero)
            {
                return 0;
            }

            return Marshal.PtrToStructure<FffScanProgress>(payload).ScannedFilesCount;
        }
        finally
        {
            Native.fff_free_scan_progress(payload);
        }
    }

    /// <summary>
    /// Reads a grep match's highlight spans. Native <c>FffMatchRange</c> values are BYTE offsets
    /// into <paramref name="utf8Line"/>, but the page works in UTF-16 char indices, so they are
    /// translated through a byte-offset→UTF-16-prefix table. Offsets are clamped defensively
    /// (including mid-multibyte cuts and swapped ends); degenerate spans are dropped.
    /// </summary>
    /// <param name="utf8Line">
    /// The match's line as the native library holds it, copied out of the parent
    /// <c>FffGrepResult</c> before that result is freed.
    /// </param>
    private static SeekyRange[] ReadMatchRanges(IntPtr match, byte[] utf8Line)
    {
        uint count = Native.fff_grep_match_get_match_ranges_count(match);
        if (count == 0 || utf8Line.Length == 0)
        {
            return [];
        }

        // An all-ASCII line — nearly every line of source — needs no table at all: one byte is
        // exactly one UTF-16 unit, so the native offsets are already char indices. Skipping the
        // table here is what keeps a full symbol sweep from allocating an int[] per match.
        int[]? prefixUtf16Counts = IsAscii(utf8Line) ? null : BuildUtf16PrefixCounts(utf8Line);

        var ranges = new SeekyRange[count];
        int written = 0;
        for (uint i = 0; i < count; i++)
        {
            IntPtr rangePtr = Native.fff_grep_match_get_match_range(match, i);
            if (rangePtr == IntPtr.Zero)
            {
                continue;
            }

            FffMatchRange range = ReadMatchRange(rangePtr);
            int startByte = (int)Math.Min(range.Start, (uint)utf8Line.Length);
            int endByte = (int)Math.Min(range.End, (uint)utf8Line.Length);
            if (endByte < startByte)
            {
                (startByte, endByte) = (endByte, startByte);
            }

            int start = prefixUtf16Counts is null ? startByte : prefixUtf16Counts[startByte];
            int end = prefixUtf16Counts is null ? endByte : prefixUtf16Counts[endByte];
            if (end > start)
            {
                ranges[written++] = new SeekyRange(start, end);
            }
        }

        if (written == ranges.Length)
        {
            return ranges;
        }

        var trimmed = new SeekyRange[written];
        Array.Copy(ranges, trimmed, written);
        return trimmed;
    }

    // Ascii.IsValid is .NET 8+; the same test as a plain loop.
    private static bool IsAscii(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Maps each byte offset in <paramref name="utf8"/> to the number of UTF-16 units that precede
    /// it in the decoded string. Offsets landing inside a multi-byte sequence map to the start of
    /// the character they cut into.
    /// </summary>
    /// <remarks>
    /// Decoded one byte at a time through Encoding.UTF8's own Decoder rather than with
    /// hand-rolled sequence-length arithmetic for two reasons: these are the native library's
    /// bytes, which are not guaranteed to be well-formed UTF-8, and driving the same decoder
    /// engine as the Encoding.UTF8.GetString that produced the string applies the same U+FFFD
    /// replacement policy by construction — anything else lets the table and the string disagree
    /// on malformed input. (The .NET original used Rune.DecodeFromUtf8, which net472 does not
    /// have; the incremental decoder feed also sidesteps the two runtimes disagreeing on how many
    /// U+FFFDs an ill-formed sequence is worth.)
    /// </remarks>
    private static int[] BuildUtf16PrefixCounts(byte[] utf8)
    {
        var prefixCounts = new int[utf8.Length + 1];
        Decoder decoder = Encoding.UTF8.GetDecoder();
        char[] decoded = new char[4]; // one byte step emits at most a surrogate pair or a U+FFFD plus its byte
        int utf16Count = 0;

        for (int byteOffset = 0; byteOffset < utf8.Length; byteOffset++)
        {
            // Buffered multi-byte sequences emit nothing until their last byte, so the offsets
            // inside a sequence keep pointing at the count before the character started.
            prefixCounts[byteOffset] = utf16Count;
            utf16Count += decoder.GetChars(utf8, byteOffset, 1, decoded, 0, flush: false);
        }

        // The flush turns a truncated trailing sequence into its U+FFFD, exactly as the flush
        // inside GetString did when the line text was decoded.
        utf16Count += decoder.GetChars(utf8, utf8.Length, 0, decoded, 0, flush: true);
        prefixCounts[utf8.Length] = utf16Count;
        return prefixCounts;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Checks the FffResult envelope, frees it, and returns the payload handle. On failure,
    /// throws with the native error string (the envelope is still freed exactly once).
    /// </summary>
    private static IntPtr UnwrapResult(IntPtr result, string operation) =>
        UnwrapResult(result, operation, out _);

    private static IntPtr UnwrapResult(IntPtr result, string operation, out long intValue)
    {
        intValue = 0;
        if (result == IntPtr.Zero)
        {
            throw new InvalidOperationException($"fff {operation}: null FffResult");
        }

        try
        {
            if (!Native.fff_result_get_success(result))
            {
                string error = PtrToStringUtf8(Native.fff_result_get_error(result)) ?? "unknown error";
                SeekyLog.Info($"fff {operation} failed: {error}");
                throw new InvalidOperationException($"fff {operation}: {error}");
            }

            intValue = Native.fff_result_get_int_value(result);
            return Native.fff_result_get_handle(result);
        }
        finally
        {
            Native.fff_free_result(result);
        }
    }

    // Marshal.PtrToStringUTF8 does not exist on net472; count, copy, decode by hand.
    private static string? PtrToStringUtf8(IntPtr ptr) =>
        ptr == IntPtr.Zero ? null : Encoding.UTF8.GetString(ReadNullTerminatedBytes(ptr));

    /// <summary>
    /// Copies a NUL-terminated native UTF-8 string out into managed bytes. Empty for a null
    /// pointer. The native side keeps owning its memory — this is the one copy every consumer
    /// decodes from.
    /// </summary>
    private static byte[] ReadNullTerminatedBytes(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
        {
            return [];
        }

        int length = NullTerminatedByteCount(ptr);
        var bytes = new byte[length];
        Marshal.Copy(ptr, bytes, 0, length);
        return bytes;
    }

    /// <summary>Bytes before the terminating NUL of a native string. The pointer must be non-null.</summary>
    private static int NullTerminatedByteCount(IntPtr ptr)
    {
        int length = 0;
        while (Marshal.ReadByte(ptr, length) != 0)
        {
            length++;
        }

        return length;
    }

    /// <summary>
    /// True when a native UTF-8 string pointer is null or points at "". Lets a caller reject an
    /// item before paying <see cref="PtrToStringUtf8"/> for it.
    /// </summary>
    private static bool IsNullOrEmptyUtf8(IntPtr ptr) => ptr == IntPtr.Zero || Marshal.ReadByte(ptr) == 0;

    /// <summary>
    /// Encodes a string as the null-terminated UTF-8 byte array the native API takes. DllImport
    /// cannot marshal UTF-8 strings (LibraryImport's StringMarshalling.Utf8 is .NET 7+), so the
    /// conversion happens at the call sites. Null stays null and marshals as a NULL pointer,
    /// matching the original's string? parameters.
    /// </summary>
    private static byte[]? ToUtf8(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, 0, value.Length, bytes, 0);
        return bytes; // zero-initialized, so the terminator is already in place
    }

    // Marshal.StringToCoTaskMemUTF8 does not exist on net472; the same allocation by hand.
    private static IntPtr Utf8ToCoTaskMem(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        IntPtr ptr = Marshal.AllocCoTaskMem(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return ptr;
    }

    /// <summary>
    /// Decodes native UTF-8 strings, reusing the previous result when the bytes repeat.
    /// </summary>
    /// <remarks>
    /// Sized for the one-element case on purpose: grep output arrives grouped by file, so every
    /// match after the first in a file repeats the path immediately before it. A hit costs a
    /// byte compare against the cached copy instead of a UTF-8 decode plus an allocation, and —
    /// the part that outlives the call — the retained results then share one string per file
    /// rather than carrying one per match, which is most of what a cached symbol index is made
    /// of.
    /// </remarks>
    private sealed class Utf8StringCache
    {
        private byte[] bytes = [];
        private int length;
        private string? value;

        public string GetOrDecode(IntPtr utf8, int utf8Length)
        {
            if (value is not null && utf8Length == length && BytesEqual(utf8, bytes, length))
            {
                return value;
            }

            if (bytes.Length < utf8Length)
            {
                bytes = new byte[Math.Max(utf8Length, 128)];
            }

            Marshal.Copy(utf8, bytes, 0, utf8Length);
            length = utf8Length;
            value = Encoding.UTF8.GetString(bytes, 0, length);
            return value;
        }

        private static bool BytesEqual(IntPtr a, byte[] b, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (Marshal.ReadByte(a, i) != b[i])
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Reads a blittable native match range as two plain loads. <see cref="Marshal.PtrToStructure"/>
    /// boxes its result on .NET Framework, and these run per match range, so the box is not worth
    /// paying for. The pointer must be non-null and naturally aligned (it always is — Rust
    /// allocated it). (The .NET original dereferenced the pointer directly; this assembly
    /// compiles without unsafe code.)
    /// </summary>
    private static FffMatchRange ReadMatchRange(IntPtr ptr) =>
        new()
        {
            Start = (uint)Marshal.ReadInt32(ptr),
            End = (uint)Marshal.ReadInt32(ptr, sizeof(uint)),
        };

    // The extension host doesn't probe our folder for native assets, and net472 has no
    // NativeLibrary.SetDllImportResolver — so fff_c.dll is loaded by absolute path before the
    // first native call instead; once LoadLibrary has brought the module into the process, the
    // DllImport("fff_c.dll") declarations below bind to it by name.
    private static void EnsureLibraryLoaded()
    {
        if (Volatile.Read(ref libraryLoaded))
        {
            return;
        }

        // The flag is set AFTER the load, under a lock. Setting it first (as the resolver flag
        // once was) lets a second caller skip the wait and P/Invoke before the library is there,
        // which surfaces as DllNotFoundException for fff_c.dll. Callers are serialized by the
        // instance gate today, so this is hardening rather than a live bug.
        lock (LoaderLock)
        {
            if (libraryLoaded)
            {
                return;
            }

            string libraryPath = GetLibraryPath();
            SeekyLog.Info($"fff loader path: {libraryPath} (exists: {File.Exists(libraryPath)})");

            if (LoadLibrary(libraryPath) == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                var ex = new DllNotFoundException(
                    $"fff: LoadLibrary failed for '{libraryPath}' (Win32 error {error})");
                SeekyLog.Error("fff: could not load the native search library", ex);
                throw ex;
            }

            Volatile.Write(ref libraryLoaded, true);
        }
    }

    /// <summary>
    /// The absolute path fff_c.dll is loaded from — in the Seeky payload folder the VSIX ships
    /// beside this assembly.
    /// </summary>
    private static string GetLibraryPath()
    {
        string extensionDir = Path.GetDirectoryName(typeof(FffNativeClient).Assembly.Location)
            ?? AppContext.BaseDirectory;
        return Path.Combine(extensionDir, "Seeky", "Tools", LibraryName);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    // ------------------------------------------------------------------ native bindings
    // Bound against crates/fff-c/include/fff.h (cbindgen). All functions return a heap
    // FffResult* except the fff_free_*/fff_destroy/fff_*_get_* accessors. String parameters go
    // in as null-terminated UTF-8 byte arrays (see ToUtf8); string returns come out as IntPtr
    // and are decoded by PtrToStringUtf8.

    private static class Native
    {
        [DllImport(LibraryName)]
        internal static extern IntPtr fff_create_instance_with(in FffCreateOptions opts);

        [DllImport(LibraryName)]
        internal static extern void fff_destroy(IntPtr handle);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_search(
            IntPtr handle, byte[]? query, byte[]? currentFile,
            uint maxThreads, uint pageIndex, uint pageSize,
            int comboBoostMultiplier, uint minComboCount);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_glob(
            IntPtr handle, byte[]? pattern, byte[]? currentFile,
            uint maxThreads, uint pageIndex, uint pageSize);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_refresh_git_status(IntPtr handle);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_search_directories(
            IntPtr handle, byte[]? query, byte[]? currentFile,
            uint maxThreads, uint pageIndex, uint pageSize);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_dir_search_result_get_item(IntPtr result, uint index);

        [DllImport(LibraryName)]
        internal static extern void fff_free_dir_search_result(IntPtr result);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_get_historical_query(IntPtr handle, ulong offset);

        [DllImport(LibraryName)]
        internal static extern void fff_free_string(IntPtr s);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_live_grep(
            IntPtr handle, byte[]? query, byte mode,
            ulong maxFileSize, uint maxMatchesPerFile,
            [MarshalAs(UnmanagedType.I1)] bool smartCase,
            uint fileOffset, uint pageLimit, ulong timeBudgetMs,
            uint beforeContext, uint afterContext,
            [MarshalAs(UnmanagedType.I1)] bool classifyDefinitions);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_get_scan_progress(IntPtr handle);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_wait_for_scan(IntPtr handle, ulong timeoutMs);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_restart_index(IntPtr handle, byte[]? newPath);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_track_query(IntPtr handle, byte[]? query, byte[]? filePath);

        [DllImport(LibraryName)]
        internal static extern void fff_free_result(IntPtr result);

        [DllImport(LibraryName)]
        internal static extern void fff_free_search_result(IntPtr result);

        [DllImport(LibraryName)]
        internal static extern void fff_free_grep_result(IntPtr result);

        [DllImport(LibraryName)]
        internal static extern void fff_free_scan_progress(IntPtr result);

        [DllImport(LibraryName)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool fff_result_get_success(IntPtr result);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_result_get_error(IntPtr result);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_result_get_handle(IntPtr result);

        [DllImport(LibraryName)]
        internal static extern long fff_result_get_int_value(IntPtr result);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_search_result_get_item(IntPtr result, uint index);

        [DllImport(LibraryName)]
        internal static extern uint fff_search_result_get_count(IntPtr result);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_file_item_get_relative_path(IntPtr item);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_file_item_get_git_status(IntPtr item);

        [DllImport(LibraryName)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool fff_file_item_get_is_binary(IntPtr item);

        [DllImport(LibraryName)]
        internal static extern long fff_file_item_get_total_frecency_score(IntPtr item);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_grep_result_get_match(IntPtr result, uint index);

        [DllImport(LibraryName)]
        internal static extern uint fff_grep_result_get_count(IntPtr result);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_grep_result_get_regex_fallback_error(IntPtr result);

        [DllImport(LibraryName)]
        internal static extern uint fff_grep_result_get_next_file_offset(IntPtr result);

        [DllImport(LibraryName)]
        internal static extern uint fff_grep_result_get_total_files(IntPtr result);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_grep_match_get_relative_path(IntPtr match);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_grep_match_get_git_status(IntPtr match);

        [DllImport(LibraryName)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool fff_grep_match_get_is_binary(IntPtr match);

        [DllImport(LibraryName)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool fff_grep_match_get_is_definition(IntPtr match);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_grep_match_get_line_content(IntPtr match);

        [DllImport(LibraryName)]
        internal static extern ulong fff_grep_match_get_line_number(IntPtr match);

        [DllImport(LibraryName)]
        internal static extern uint fff_grep_match_get_col(IntPtr match);

        [DllImport(LibraryName)]
        internal static extern uint fff_grep_match_get_match_ranges_count(IntPtr match);

        [DllImport(LibraryName)]
        internal static extern IntPtr fff_grep_match_get_match_range(IntPtr match, uint index);
    }

    // Blittable mirror of FffCreateOptions (cbindgen, x64 layout; C99 bool = 1 byte → byte).
    [StructLayout(LayoutKind.Sequential)]
    private struct FffCreateOptions
    {
        internal uint Version;
        internal IntPtr BasePath;
        internal IntPtr FrecencyDbPath;
        internal IntPtr HistoryDbPath;
        internal byte EnableMmapCache;
        internal byte EnableContentIndexing;
        internal byte Watch;
        internal byte AiMode;
        internal IntPtr LogFilePath;
        internal IntPtr LogLevel;
        internal ulong CacheBudgetMaxFiles;
        internal ulong CacheBudgetMaxBytes;
        internal ulong CacheBudgetMaxFileSize;
        internal byte EnableFsRootScanning;
        internal byte EnableHomeDirScanning;
        internal byte FollowSymlinks;
    }

    // Blittable mirror of FffScanProgress (uint64 + 3 bools, C layout = 16 bytes).
    [StructLayout(LayoutKind.Sequential)]
    private struct FffScanProgress
    {
        internal ulong ScannedFilesCount;
        internal byte IsScanning;
        internal byte IsWatcherReady;
        internal byte IsWarmupComplete;
    }

    // Blittable mirror of FffMatchRange (two uint32s, byte offsets into the UTF-8 line).
    [StructLayout(LayoutKind.Sequential)]
    private struct FffMatchRange
    {
        internal uint Start;
        internal uint End;
    }

    // Blittable mirror of FffDirItem (two char* + i32; C layout with 4-byte tail padding).
    [StructLayout(LayoutKind.Sequential)]
    private struct FffDirItem
    {
        internal IntPtr RelativePath;
        internal IntPtr DirName;
        internal int MaxAccessFrecency;
    }

    // Blittable mirror of the FffDirSearchResult header — v0.10.1 exports no count accessor,
    // so the count is read from the struct (layout matches the tagged v0.10.1 fff.h).
    [StructLayout(LayoutKind.Sequential)]
    private struct FffDirSearchResultHeader
    {
        internal IntPtr Items;
        internal IntPtr Scores;
        internal uint Count;
        internal uint TotalMatched;
        internal uint TotalDirs;
    }
}
