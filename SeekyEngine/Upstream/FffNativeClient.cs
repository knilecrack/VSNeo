// SeekyVS — Visual Studio 2026 port spike for the Seeky VS Code extension.

namespace SeekyVS;

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
/// <c>FffMixedItem</c>, the <c>FffSearchResult</c>/<c>FffMixedSearchResult</c> headers) are all blittable and read as plain
/// loads via <see cref="ReadStruct{T}"/>. Every pointer is therefore either freed exactly once
/// here or owned by the native instance. All native calls are serialized through a single gate —
/// fff's thread-safety guarantees are undocumented, and searches are fast enough that contention
/// is theoretical.
/// </remarks>
internal sealed partial class FffNativeClient : IDisposable
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

    private static readonly System.Threading.Lock ResolverLock = new();
    private static bool resolverInstalled;

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

        /// <summary>
        /// Not a native live_grep mode: lines matching ANY of several literal patterns, via
        /// fff_multi_grep (SIMD Aho-Corasick). See <see cref="SplitMultiGrepQuery"/>.
        /// </summary>
        Any = 3,
    }

    /// <summary>A fuzzy file-search result.</summary>
    internal readonly record struct FileItem(string Path, long FrecencyScore, string? GitStatus, bool IsBinary);

    /// <summary>
    /// A <c>:line[:col]</c> suffix fff parsed off a file query (<c>Foo.cs:42:9</c>); 1-based,
    /// <paramref name="Col"/> null when only a line was given. Ranges keep their start.
    /// </summary>
    internal readonly record struct QueryLocation(int Line, int? Col);

    /// <summary>File-search results plus the location parsed from the query, if any.</summary>
    internal sealed record FileSearch(IReadOnlyList<FileItem> Items, QueryLocation? Location);

    /// <summary>
    /// A files-and-folders result (fff_search_mixed). Any trailing separator on a directory path
    /// is trimmed so callers can render and join both kinds alike.
    /// </summary>
    internal readonly record struct MixedItem(
        string Path, bool IsDirectory, long FrecencyScore, string? GitStatus, bool IsBinary);

    /// <summary>Files-and-folders results plus the location parsed from the query, if any.</summary>
    internal sealed record MixedSearch(IReadOnlyList<MixedItem> Items, QueryLocation? Location);

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
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
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
    /// <paramref name="currentFile"/> deprioritizes the currently open file (fff current_file).
    /// A <c>:line[:col]</c> suffix on the query comes back as <see cref="FileSearch.Location"/>.</summary>
    public Task<FileSearch> FindFilesAsync(string query, string? currentFile, int maxResults, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);
        ThrowIfDisposed();

        return Task.Run(
            async () =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfNotStartedCore();
                    List<FileItem> items = SearchFilesCore(
                        query, currentFile, (uint)maxResults, useGlob: false, maxResults,
                        gitModifiedOnly: false, out _, out QueryLocation? location);
                    return new FileSearch(items, location);
                }
                finally
                {
                    gate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Fuzzy search over files and directories in one ranked list (fff_search_mixed); paths are
    /// workspace-relative. <paramref name="currentFile"/> feeds fff's distance scoring. A
    /// <c>:line[:col]</c> suffix on the query comes back as <see cref="MixedSearch.Location"/>.
    /// </summary>
    public Task<MixedSearch> FindMixedAsync(string query, string? currentFile, int maxResults, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);
        ThrowIfDisposed();

        return Task.Run(
            async () =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfNotStartedCore();

                    IntPtr result = CallWithWatchdog("search_mixed", () => Native.fff_search_mixed(handle, query, currentFile, 0, 0, (uint)maxResults, 0, 0));
                    IntPtr payload = UnwrapResult(result, "search_mixed");
                    try
                    {
                        // A successful FffResult with a null handle would otherwise reach the
                        // header read below, where ReadStruct dereferences null.
                        if (payload == IntPtr.Zero)
                        {
                            return new MixedSearch([], null);
                        }

                        // v0.10.1 exports no fff_mixed_search_result_get_count or location
                        // accessor — read both from the result struct header instead.
                        FffMixedSearchResultHeader header = ReadStruct<FffMixedSearchResultHeader>(payload);
                        uint count = header.Count;
                        var items = new List<MixedItem>((int)count);
                        for (uint i = 0; i < count; i++)
                        {
                            IntPtr item = Native.fff_mixed_search_result_get_item(payload, i);
                            if (item == IntPtr.Zero)
                            {
                                continue;
                            }

                            // No per-field accessors for FffMixedItem either; it is blittable.
                            FffMixedItem native = ReadStruct<FffMixedItem>(item);
                            bool isDirectory = native.ItemType == 1;
                            string? path = PtrToString(native.RelativePath);
                            if (path is not null && isDirectory)
                            {
                                path = TrimTrailingSeparators(path);
                            }

                            if (string.IsNullOrEmpty(path))
                            {
                                continue; // the workspace root itself, as a directory hit
                            }

                            items.Add(new MixedItem(
                                path,
                                isDirectory,
                                native.TotalFrecencyScore,
                                PtrToString(native.GitStatus),
                                native.IsBinary != 0));
                        }

                        return new MixedSearch(items, ToQueryLocation(header.Location));
                    }
                    finally
                    {
                        Native.fff_free_mixed_search_result(payload);
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
        ArgumentOutOfRangeException.ThrowIfNegative(max);
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
                            string? query = PtrToString(payload);
                            if (string.IsNullOrEmpty(query))
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);
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
                        out uint rankedCount, out _);
                    if (rankedCount == 0 && query.Length == 0)
                    {
                        SeekyLog.Info("fff: empty-query search returned nothing; falling back to fff_glob '*'");
                        items = SearchFilesCore(
                            "*", null, GitModifiedPoolSize, useGlob: true, maxResults, gitModifiedOnly: true,
                            out _, out _);
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
    /// <param name="location">The <c>:line[:col]</c> fff parsed off the query; null when none.</param>
    private List<FileItem> SearchFilesCore(
        string query,
        string? currentFile,
        uint pageSize,
        bool useGlob,
        int maxItems,
        bool gitModifiedOnly,
        out uint rankedCount,
        out QueryLocation? location)
    {
        IntPtr result = useGlob
            ? CallWithWatchdog("glob", () => Native.fff_glob(handle, query, currentFile, 0, 0, pageSize))
            : CallWithWatchdog("search", () => Native.fff_search(handle, query, currentFile, 0, 0, pageSize, 0, 0));
        IntPtr payload = UnwrapResult(result, useGlob ? "glob" : "search");
        try
        {
            uint count = Native.fff_search_result_get_count(payload);
            rankedCount = count;

            // No accessor for the parsed location in v0.10.1 — read it off the result header.
            location = payload == IntPtr.Zero
                ? null
                : ToQueryLocation(ReadStruct<FffSearchResultHeader>(payload).Location);
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

                string? path = PtrToString(Native.fff_file_item_get_relative_path(item));
                if (path is not null)
                {
                    items.Add(new FileItem(
                        path,
                        Native.fff_file_item_get_total_frecency_score(item),
                        PtrToString(gitStatus),
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);
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
        ArgumentOutOfRangeException.ThrowIfZero(filePageLimit);
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

        IntPtr result;
        if (mode == GrepMode.Any)
        {
            (string patterns, string? constraints) = SplitMultiGrepQuery(query);
            if (patterns.Length == 0)
            {
                return new GrepPage([], null, 0, 0); // only constraints typed so far
            }

            result = CallWithWatchdog("multi_grep", () => Native.fff_multi_grep(
                handle,
                patterns,
                constraints,
                maxFileSize: 0,
                maxMatchesPerFile: 0,
                smartCase: true,
                fileOffset: fileOffset,
                pageLimit: filePageLimit,
                timeBudgetMs: 0,
                beforeContext: 0,
                afterContext: 0,
                classifyDefinitions: true));
        }
        else
        {
            result = CallWithWatchdog("live_grep", () => Native.fff_live_grep(
                handle,
                query,
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
        }

        IntPtr payload = UnwrapResult(result, mode == GrepMode.Any ? "multi_grep" : "live_grep");
        try
        {
            // Reported, not logged: every page of a paged sweep repeats the same fallback, and
            // SeekyLog writes each line with its own open/append/close under a global lock — 400
            // of those, while this call holds the gate, for one bad regex. Callers log it once.
            string? fallbackError = PtrToString(Native.fff_grep_result_get_regex_fallback_error(payload));

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

                string path = pathCache.GetOrDecode(NullTerminatedSpan(pathPtr));

                // The line is taken as raw native bytes rather than through PtrToString because the
                // match ranges are byte offsets into exactly these bytes. Decoding to a string and
                // re-encoding does not round-trip: bytes fff accepted but .NET rejects come back as
                // U+FFFD, three bytes where the original was one, sliding every later offset.
                ReadOnlySpan<byte> lineBytes = NullTerminatedSpan(Native.fff_grep_match_get_line_content(match));
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
                    PtrToString(Native.fff_grep_match_get_git_status(match)),
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
    /// The <c>:line[:col]</c> fff parsed off a query, or null. Tag: 0 none, 1 line, 2 line+col,
    /// 3 range (its start is kept).
    /// </summary>
    private static QueryLocation? ToQueryLocation(FffLocation parsed) =>
        parsed.Tag is >= 1 and <= 3 && parsed.Line > 0
            ? new QueryLocation(parsed.Line, parsed.Tag >= 2 && parsed.Col > 0 ? parsed.Col : null)
            : null;

    /// <summary>
    /// Splits an "any" grep query for fff_multi_grep, which takes the patterns and the file
    /// constraints as separate arguments (live_grep parses both out of one string itself).
    /// Constraint-shaped tokens — <c>*.cs</c>, <c>**/*.{c,h}</c>, <c>src/</c>, <c>./x</c>,
    /// <c>!test/</c>, <c>git:modified</c> — go to the constraints; the rest of the text is split
    /// on <c>|</c> into literal patterns, so <c>TODO|FIXME *.cs</c> finds either word in C# files.
    /// </summary>
    /// <returns>
    /// The patterns joined with newlines (empty when there are none) and the constraints,
    /// space-joined, or null when there are none.
    /// </returns>
    internal static (string Patterns, string? Constraints) SplitMultiGrepQuery(string query)
    {
        var text = new List<string>();
        var constraints = new List<string>();
        foreach (string token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bool isConstraint = token.StartsWith("git:", StringComparison.Ordinal)
                || token.StartsWith("./", StringComparison.Ordinal)
                || (token.Length > 1 && token[0] == '!')
                || token.EndsWith('/')
                || token.Contains('*');
            (isConstraint ? constraints : text).Add(token);
        }

        string[] patterns = string.Join(' ', text)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (string.Join('\n', patterns), constraints.Count > 0 ? string.Join(' ', constraints) : null);
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
            // In a finally so a throwing call (a resolver DllNotFoundException, say) cannot leave
            // the slot armed and the watchdog reporting a hang that already ended.
            Volatile.Write(ref inFlightSince, 0);
            long elapsedMs = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (elapsedMs > SlowCallMs)
            {
                SeekyLog.Info($"fff {name} took {elapsedMs}ms");
            }
        }
    }

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

        long elapsedMs = (long)Stopwatch.GetElapsedTime(since).TotalMilliseconds;
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

                        IntPtr result = Native.fff_track_query(handle, query, relativePath);
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        EnsureResolver();

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
            IntPtr restartResult = Native.fff_restart_index(handle, dir);
            UnwrapResult(restartResult, "restart_index");
            workspaceDir = dir;
            scanWaitCompleted = false;

            // Any file_offset held by an in-flight paged sweep now refers to the old index.
            Interlocked.Increment(ref workspaceGeneration);
        }
        else if (handle == IntPtr.Zero)
        {
            string extensionDir = Path.GetDirectoryName(typeof(FffNativeClient).Assembly.Location)
                ?? AppContext.BaseDirectory;
            string stateDir = Path.Combine(dir, ".vs", "seeky");
            Directory.CreateDirectory(stateDir);

            IntPtr basePath = Marshal.StringToCoTaskMemUTF8(dir);
            IntPtr frecencyDb = Marshal.StringToCoTaskMemUTF8(Path.Combine(stateDir, "frecency.db"));
            IntPtr historyDb = Marshal.StringToCoTaskMemUTF8(Path.Combine(stateDir, "history.db"));
            IntPtr logFile = Marshal.StringToCoTaskMemUTF8(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeekyVS", "fff.log"));
            IntPtr logLevel = Marshal.StringToCoTaskMemUTF8("info");
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
                SeekyLog.Info($"fff: creating instance for '{dir}' (dll '{extensionDir}\\Tools\\fff_c.dll')");
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

            return ReadStruct<FffScanProgress>(payload).ScannedFilesCount;
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
    /// The match's line as the native library holds it. Borrowed from the parent
    /// <c>FffGrepResult</c> — valid only until that result is freed.
    /// </param>
    private static SeekyRange[] ReadMatchRanges(IntPtr match, ReadOnlySpan<byte> utf8Line)
    {
        uint count = Native.fff_grep_match_get_match_ranges_count(match);
        if (count == 0 || utf8Line.IsEmpty)
        {
            return [];
        }

        // An all-ASCII line — nearly every line of source — needs no table at all: one byte is
        // exactly one UTF-16 unit, so the native offsets are already char indices. Skipping the
        // table here is what keeps a full symbol sweep from allocating an int[] per match.
        int[]? prefixUtf16Counts = Ascii.IsValid(utf8Line) ? null : BuildUtf16PrefixCounts(utf8Line);

        var ranges = new SeekyRange[count];
        int written = 0;
        for (uint i = 0; i < count; i++)
        {
            IntPtr rangePtr = Native.fff_grep_match_get_match_range(match, i);
            if (rangePtr == IntPtr.Zero)
            {
                continue;
            }

            FffMatchRange range = ReadStruct<FffMatchRange>(rangePtr);
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

        return written == ranges.Length ? ranges : ranges.AsSpan(0, written).ToArray();
    }

    /// <summary>
    /// Maps each byte offset in <paramref name="utf8"/> to the number of UTF-16 units that precede
    /// it in the decoded string. Offsets landing inside a multi-byte sequence map to the start of
    /// the character they cut into.
    /// </summary>
    private static int[] BuildUtf16PrefixCounts(ReadOnlySpan<byte> utf8)
    {
        var prefixCounts = new int[utf8.Length + 1];
        int utf16Count = 0;
        int byteOffset = 0;

        while (byteOffset < utf8.Length)
        {
            // Decoded with Rune rather than hand-rolled sequence-length arithmetic for two
            // reasons: these are the native library's bytes, which are not guaranteed to be
            // well-formed UTF-8, and Rune applies the same maximal-subpart U+FFFD replacement
            // policy as the Encoding.UTF8.GetString that produced the string these offsets index
            // into — anything else lets the table and the string disagree on malformed input.
            _ = Rune.DecodeFromUtf8(utf8[byteOffset..], out Rune rune, out int bytesConsumed);
            for (int i = 0; i < bytesConsumed; i++)
            {
                prefixCounts[byteOffset + i] = utf16Count;
            }

            // Utf16SequenceLength, not 1: an astral-plane character is a surrogate PAIR, and
            // counting it as a single unit shifts every range after the first emoji on the line.
            utf16Count += rune.Utf16SequenceLength;
            byteOffset += bytesConsumed;
        }

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
                string error = PtrToString(Native.fff_result_get_error(result)) ?? "unknown error";
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

    private static string? PtrToString(IntPtr ptr) =>
        ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);

    /// <summary>
    /// True when a native UTF-8 string pointer is null or points at "". Lets a caller reject an
    /// item before paying <see cref="Marshal.PtrToStringUTF8"/> for it.
    /// </summary>
    private static unsafe bool IsNullOrEmptyUtf8(IntPtr ptr) => ptr == IntPtr.Zero || *(byte*)ptr == 0;

    /// <summary>
    /// Decodes native UTF-8 strings, reusing the previous result when the bytes repeat.
    /// </summary>
    /// <remarks>
    /// Sized for the one-element case on purpose: grep output arrives grouped by file, so every
    /// match after the first in a file repeats the path immediately before it. A hit costs a
    /// vectorized span compare instead of a UTF-8 decode plus an allocation, and — the part that
    /// outlives the call — the retained results then share one string per file rather than
    /// carrying one per match, which is most of what a cached symbol index is made of.
    /// </remarks>
    private sealed class Utf8StringCache
    {
        private byte[] bytes = [];
        private int length;
        private string? value;

        public string GetOrDecode(ReadOnlySpan<byte> utf8)
        {
            if (value is not null && utf8.SequenceEqual(bytes.AsSpan(0, length)))
            {
                return value;
            }

            if (bytes.Length < utf8.Length)
            {
                bytes = new byte[Math.Max(utf8.Length, 128)];
            }

            utf8.CopyTo(bytes);
            length = utf8.Length;
            value = Encoding.UTF8.GetString(utf8);
            return value;
        }
    }

    /// <summary>
    /// A span over a NUL-terminated native UTF-8 string, without copying it. Borrowed from
    /// whatever native object owns the pointer — valid only until that object is freed. Empty for
    /// a null pointer.
    /// </summary>
    private static unsafe ReadOnlySpan<byte> NullTerminatedSpan(IntPtr ptr) =>
        MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)ptr);

    /// <summary>
    /// Reads a blittable native struct as a plain load. <see cref="Marshal.PtrToStructure{T}(IntPtr)"/>
    /// does the same job through a marshalling helper and throws <see cref="NullReferenceException"/>
    /// on a null pointer; these run per match range, so the helper is not worth paying for. The
    /// pointer must be non-null and naturally aligned (it always is — Rust allocated it).
    /// </summary>
    private static unsafe T ReadStruct<T>(IntPtr ptr)
        where T : unmanaged =>
        *(T*)ptr;

    // The extension host doesn't probe our folder for native assets — same pattern as the
    // WebView2Loader resolver: resolve fff_c.dll relative to the extension assembly.
    private static void EnsureResolver()
    {
        if (Volatile.Read(ref resolverInstalled))
        {
            return;
        }

        // The flag is set AFTER the resolver is installed, under a lock. Setting it first (as
        // this did) lets a second caller skip the wait and P/Invoke before the resolver exists,
        // which surfaces as DllNotFoundException for fff_c.dll. Callers are serialized by the
        // instance gate today, so this is hardening rather than a live bug.
        lock (ResolverLock)
        {
            if (resolverInstalled)
            {
                return;
            }

            string extensionDir = Path.GetDirectoryName(typeof(FffNativeClient).Assembly.Location)
                ?? AppContext.BaseDirectory;
            string libraryPath = Path.Combine(extensionDir, "Tools", LibraryName);
            SeekyLog.Info($"fff loader path: {libraryPath} (exists: {File.Exists(libraryPath)})");

            NativeLibrary.SetDllImportResolver(typeof(FffNativeClient).Assembly, (name, _, _) =>
            {
                if (string.Equals(name, LibraryName, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(libraryPath))
                {
                    return NativeLibrary.Load(libraryPath);
                }

                return IntPtr.Zero; // default resolution for everything else (user32 etc.)
            });

            Volatile.Write(ref resolverInstalled, true);
        }
    }

    // ------------------------------------------------------------------ native bindings
    // Bound against crates/fff-c/include/fff.h (cbindgen). All functions return a heap
    // FffResult* except the fff_free_*/fff_destroy/fff_*_get_* accessors.

    private static partial class Native
    {
        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_create_instance_with(in FffCreateOptions opts);

        [LibraryImport(LibraryName)]
        internal static partial void fff_destroy(IntPtr handle);

        [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr fff_search(
            IntPtr handle, string query, string? currentFile,
            uint maxThreads, uint pageIndex, uint pageSize,
            int comboBoostMultiplier, uint minComboCount);

        [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr fff_glob(
            IntPtr handle, string pattern, string? currentFile,
            uint maxThreads, uint pageIndex, uint pageSize);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_refresh_git_status(IntPtr handle);

        [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr fff_search_mixed(
            IntPtr handle, string query, string? currentFile,
            uint maxThreads, uint pageIndex, uint pageSize,
            int comboBoostMultiplier, uint minComboCount);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_mixed_search_result_get_item(IntPtr result, uint index);

        [LibraryImport(LibraryName)]
        internal static partial void fff_free_mixed_search_result(IntPtr result);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_get_historical_query(IntPtr handle, ulong offset);

        [LibraryImport(LibraryName)]
        internal static partial void fff_free_string(IntPtr s);

        [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr fff_live_grep(
            IntPtr handle, string query, byte mode,
            ulong maxFileSize, uint maxMatchesPerFile,
            [MarshalAs(UnmanagedType.I1)] bool smartCase,
            uint fileOffset, uint pageLimit, ulong timeBudgetMs,
            uint beforeContext, uint afterContext,
            [MarshalAs(UnmanagedType.I1)] bool classifyDefinitions);

        [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr fff_multi_grep(
            IntPtr handle, string patternsJoined, string? constraints,
            ulong maxFileSize, uint maxMatchesPerFile,
            [MarshalAs(UnmanagedType.I1)] bool smartCase,
            uint fileOffset, uint pageLimit, ulong timeBudgetMs,
            uint beforeContext, uint afterContext,
            [MarshalAs(UnmanagedType.I1)] bool classifyDefinitions);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_get_scan_progress(IntPtr handle);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_wait_for_scan(IntPtr handle, ulong timeoutMs);

        [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr fff_restart_index(IntPtr handle, string newPath);

        [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr fff_track_query(IntPtr handle, string query, string filePath);

        [LibraryImport(LibraryName)]
        internal static partial void fff_free_result(IntPtr result);

        [LibraryImport(LibraryName)]
        internal static partial void fff_free_search_result(IntPtr result);

        [LibraryImport(LibraryName)]
        internal static partial void fff_free_grep_result(IntPtr result);

        [LibraryImport(LibraryName)]
        internal static partial void fff_free_scan_progress(IntPtr result);

        [LibraryImport(LibraryName)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static partial bool fff_result_get_success(IntPtr result);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_result_get_error(IntPtr result);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_result_get_handle(IntPtr result);

        [LibraryImport(LibraryName)]
        internal static partial long fff_result_get_int_value(IntPtr result);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_search_result_get_item(IntPtr result, uint index);

        [LibraryImport(LibraryName)]
        internal static partial uint fff_search_result_get_count(IntPtr result);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_file_item_get_relative_path(IntPtr item);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_file_item_get_git_status(IntPtr item);

        [LibraryImport(LibraryName)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static partial bool fff_file_item_get_is_binary(IntPtr item);

        [LibraryImport(LibraryName)]
        internal static partial long fff_file_item_get_total_frecency_score(IntPtr item);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_grep_result_get_match(IntPtr result, uint index);

        [LibraryImport(LibraryName)]
        internal static partial uint fff_grep_result_get_count(IntPtr result);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_grep_result_get_regex_fallback_error(IntPtr result);

        [LibraryImport(LibraryName)]
        internal static partial uint fff_grep_result_get_next_file_offset(IntPtr result);

        [LibraryImport(LibraryName)]
        internal static partial uint fff_grep_result_get_total_files(IntPtr result);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_grep_match_get_relative_path(IntPtr match);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_grep_match_get_git_status(IntPtr match);

        [LibraryImport(LibraryName)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static partial bool fff_grep_match_get_is_binary(IntPtr match);

        [LibraryImport(LibraryName)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static partial bool fff_grep_match_get_is_definition(IntPtr match);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_grep_match_get_line_content(IntPtr match);

        [LibraryImport(LibraryName)]
        internal static partial ulong fff_grep_match_get_line_number(IntPtr match);

        [LibraryImport(LibraryName)]
        internal static partial uint fff_grep_match_get_col(IntPtr match);

        [LibraryImport(LibraryName)]
        internal static partial uint fff_grep_match_get_match_ranges_count(IntPtr match);

        [LibraryImport(LibraryName)]
        internal static partial IntPtr fff_grep_match_get_match_range(IntPtr match, uint index);
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

    // Blittable mirror of FffLocation (u8 tag + four i32; C layout pads the tag to 4 bytes).
    [StructLayout(LayoutKind.Sequential)]
    private struct FffLocation
    {
        internal byte Tag;
        internal int Line;
        internal int Col;
        internal int EndLine;
        internal int EndCol;
    }

    // Blittable mirror of the FffSearchResult header, read for the parsed query location
    // (layout matches the tagged v0.10.1 fff.h).
    [StructLayout(LayoutKind.Sequential)]
    private struct FffSearchResultHeader
    {
        internal IntPtr Items;
        internal IntPtr Scores;
        internal uint Count;
        internal uint TotalMatched;
        internal uint TotalFiles;
        internal FffLocation Location;
    }

    // Blittable mirror of FffMixedItem: u8 item_type (C pads it to 8 before the first pointer,
    // as does sequential layout here), three char*, five 8-byte ints, bool (tail-padded).
    [StructLayout(LayoutKind.Sequential)]
    private struct FffMixedItem
    {
        internal byte ItemType;
        internal IntPtr RelativePath;
        internal IntPtr DisplayName;
        internal IntPtr GitStatus;
        internal ulong Size;
        internal ulong Modified;
        internal long AccessFrecencyScore;
        internal long ModificationFrecencyScore;
        internal long TotalFrecencyScore;
        internal byte IsBinary;
    }

    // Blittable mirror of the FffMixedSearchResult header — v0.10.1 exports no count accessor,
    // so the count is read from the struct (layout matches the tagged v0.10.1 fff.h).
    [StructLayout(LayoutKind.Sequential)]
    private struct FffMixedSearchResultHeader
    {
        internal IntPtr Items;
        internal IntPtr Scores;
        internal uint Count;
        internal uint TotalMatched;
        internal uint TotalFiles;
        internal uint TotalDirs;
        internal FffLocation Location;
    }
}
