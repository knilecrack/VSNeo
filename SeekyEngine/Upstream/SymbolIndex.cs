// SeekyVS — Visual Studio 2026 port spike for the Seeky VS Code extension.

namespace SeekyVS;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The workspace symbol list behind the "Symbols" picker: declaration-shaped lines swept out of
/// the workspace with one paged regex grep, classified by <see cref="SymbolClassifier"/>, then
/// fuzzy-filtered in-process per keystroke.
/// </summary>
/// <remarks>
/// <para>
/// Sweeping once and filtering in memory — rather than grepping per keystroke — is what makes the
/// picker feel like Go To All: the expensive part (a full-workspace grep) happens once per
/// <see cref="FreshnessWindow"/>, and typing only re-runs <see cref="FuzzyMatcher"/> over a string
/// list. It also sidesteps the ranking problem: a per-keystroke grep can only rank the matches
/// that survived its own cap, so a symbol whose name matches perfectly can be missing entirely.
/// </para>
/// <para>
/// The sweep pattern is a cheap over-inclusive prefilter (any line led by a declaration keyword or
/// access modifier); <see cref="SymbolClassifier"/> rejects the rest. Symbols that no keyword
/// introduces — TypeScript class members, C++ methods defined without modifiers — are not swept.
/// </para>
/// </remarks>
internal static class SymbolIndex
{
    /// <summary>
    /// How long a built index is served before a refresh is triggered. Past this the cached
    /// symbols are still returned immediately — the re-sweep runs behind them.
    /// </summary>
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(30);

    private const int MaxSymbols = 200_000;

    // A full sweep is ~130 pages / 2.6s on a 2700-file solution (msbuild), so the time budget is
    // what normally stops a runaway sweep; the page cap is only a backstop against a paging bug.
    private const int MaxPages = 5_000;
    private const int BuildBudgetMs = 20_000;

    // Lines led by a declaration keyword or an access modifier, in the languages
    // SymbolClassifier knows. Rust's regex crate: no lookaround, so this is plain alternation.
    private const string SweepPattern =
        @"(?i)^\s*(?:\[[^\]]*\]\s*)*(?:pub|public|private|protected|internal|friend|shared|static|" +
        @"async|virtual|override|overrides|overridable|abstract|sealed|partial|extern|unsafe|" +
        @"readonly|const|required|volatile|event|file|export|default|declare|impl|trait|mod|fn|" +
        @"func|def|class|interface|struct|record|enum|delegate|namespace|module|structure|type|" +
        @"function|let|var|dim|sub|property|operator)\b";

    private static readonly SemaphoreSlim BuildGate = new(1, 1);

    private static IReadOnlyList<Entry> cached = [];
    private static string? cachedWorkspace;
    private static long cachedAtTicks;

    /// <summary>A declared symbol: where it is, what it is, and the source line it came from.</summary>
    internal readonly record struct Entry(
        string Path, int Line, int Col, string Text, string Kind, string Name, string? GitStatus, bool IsBinary);

    /// <summary>A symbol that matched a query, with the match spans into <see cref="Entry.Name"/>.</summary>
    internal readonly record struct Hit(Entry Entry, int Score, SeekyRange[] NameRanges);

    /// <summary>Drops the cached index — call when the workspace root changes.</summary>
    internal static void Invalidate()
    {
        cached = [];
        cachedWorkspace = null;
        cachedAtTicks = 0;
    }

    /// <summary>
    /// Returns the symbol list for <paramref name="workspaceDir"/>. Blocks on a sweep only when
    /// there is no index yet; a stale one is returned as-is while it refreshes in the background,
    /// so typing never waits on a re-sweep. Concurrent callers share a single sweep.
    /// </summary>
    internal static async Task<IReadOnlyList<Entry>> GetAsync(
        FffNativeClient client,
        string workspaceDir,
        Action<string>? reportStatus,
        CancellationToken cancellationToken)
    {
        if (TryGetCached(workspaceDir, out IReadOnlyList<Entry> entries, out bool stale))
        {
            if (stale)
            {
                RefreshInBackground(client, workspaceDir);
            }

            return entries;
        }

        await BuildGate.WaitAsync(cancellationToken);
        try
        {
            // A sweep may have completed while this call waited on the gate.
            if (TryGetCached(workspaceDir, out entries, out _))
            {
                return entries;
            }

            return await SweepAndStoreAsync(client, workspaceDir, reportStatus, cancellationToken);
        }
        finally
        {
            BuildGate.Release();
        }
    }

    /// <summary>
    /// Re-sweeps behind the served results. Skipped when a sweep is already running — a stale
    /// index for one more keystroke is much cheaper than queueing full sweeps.
    /// </summary>
    private static void RefreshInBackground(FffNativeClient client, string workspaceDir)
    {
        if (!BuildGate.Wait(0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // No status callback: the user is mid-search and owns the status line.
                await SweepAndStoreAsync(client, workspaceDir, reportStatus: null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                SeekyLog.Error("Background symbol refresh failed", ex);
            }
            finally
            {
                BuildGate.Release();
            }
        });
    }

    private static async Task<IReadOnlyList<Entry>> SweepAndStoreAsync(
        FffNativeClient client,
        string workspaceDir,
        Action<string>? reportStatus,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Entry> built = await BuildAsync(client, workspaceDir, reportStatus, cancellationToken);

        // Stored against the workspace it was swept from: if the workspace changed mid-sweep,
        // the next lookup simply misses and rebuilds rather than serving the wrong solution.
        cached = built;
        cachedWorkspace = workspaceDir;
        cachedAtTicks = Stopwatch.GetTimestamp();
        return built;
    }

    /// <summary>
    /// Ranks <paramref name="entries"/> against <paramref name="query"/> by fuzzy name match. An
    /// empty query lists the workspace's symbols in index order.
    /// </summary>
    internal static List<Hit> Query(IReadOnlyList<Entry> entries, string query, int maxResults)
    {
        var hits = new List<Hit>(Math.Min(maxResults, entries.Count));
        if (query.Length == 0)
        {
            for (int i = 0; i < entries.Count && hits.Count < maxResults; i++)
            {
                hits.Add(new Hit(entries[i], 0, []));
            }

            return hits;
        }

        var scored = new List<Hit>();
        foreach (Entry entry in entries)
        {
            if (FuzzyMatcher.TryMatch(query, entry.Name, out int score, out (int Start, int End)[] ranges))
            {
                var named = new SeekyRange[ranges.Length];
                for (int i = 0; i < ranges.Length; i++)
                {
                    named[i] = new SeekyRange(ranges[i].Start, ranges[i].End);
                }

                scored.Add(new Hit(entry, score, named));
            }
        }

        scored.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            // Equal-scoring names are usually the same identifier declared in several places —
            // the type declaration is nearly always the one being looked for.
            int byKind = KindRank(a.Entry.Kind).CompareTo(KindRank(b.Entry.Kind));
            if (byKind != 0)
            {
                return byKind;
            }

            int byLength = a.Entry.Name.Length.CompareTo(b.Entry.Name.Length);
            if (byLength != 0)
            {
                return byLength;
            }

            int byPath = string.CompareOrdinal(a.Entry.Path, b.Entry.Path);
            return byPath != 0 ? byPath : a.Entry.Line.CompareTo(b.Entry.Line);
        });

        for (int i = 0; i < scored.Count && i < maxResults; i++)
        {
            hits.Add(scored[i]);
        }

        return hits;
    }

    /// <summary>
    /// Returns the cached symbols for <paramref name="workspaceDir"/>, if any, and whether they
    /// are past <see cref="FreshnessWindow"/>. A stale hit is still a hit — the caller serves it
    /// and refreshes behind it.
    /// </summary>
    /// <summary>
    /// Tie-break order for equally-scoring symbols: types, then members, then fields, then the
    /// per-file <c>namespace</c> lines (numerous and rarely what a symbol search is after).
    /// </summary>
    private static int KindRank(string kind) => kind switch
    {
        "class" or "interface" or "struct" or "record" or "enum" or "delegate" or "impl" or "type" => 0,
        "method" or "property" => 1,
        "namespace" => 3,
        _ => 2,
    };

    private static bool TryGetCached(string workspaceDir, out IReadOnlyList<Entry> entries, out bool stale)
    {
        entries = [];
        stale = false;
        if (!string.Equals(cachedWorkspace, workspaceDir, StringComparison.OrdinalIgnoreCase)
            || cachedAtTicks == 0)
        {
            return false;
        }

        entries = cached;
        stale = Stopwatch.GetElapsedTime(cachedAtTicks) > FreshnessWindow;
        return true;
    }

    private static async Task<IReadOnlyList<Entry>> BuildAsync(
        FffNativeClient client,
        string workspaceDir,
        Action<string>? reportStatus,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var entries = new List<Entry>();
        uint fileOffset = 0;
        uint totalFiles = 0;
        int scannedLines = 0;

        // A file offset only means anything against the index that produced it. This sweep spans
        // many calls, so a workspace change partway through would silently address the wrong
        // files from that point on — start over instead.
        int generation = client.WorkspaceGeneration;

        for (int page = 0; page < MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (client.WorkspaceGeneration != generation)
            {
                SeekyLog.Info($"Symbol sweep: workspace changed after {page} page(s); abandoning the partial index");
                return [];
            }

            // withRanges: false — the sweep highlights nothing. Query() builds its own spans over
            // the symbol NAME with FuzzyMatcher, so per-match highlight ranges over the line would
            // be translated from native byte offsets and thrown away, tens of thousands of times.
            FffNativeClient.GrepPage result = await client.GrepPageAsync(
                SweepPattern,
                FffNativeClient.GrepMode.Regex,
                fileOffset,
                FffNativeClient.c_FilePageLimit,
                cancellationToken,
                withRanges: false);

            if (result.RegexFallbackError is not null)
            {
                // The sweep pattern is a constant, so this means the regex engine rejected it —
                // the results below would be a literal search for the pattern text.
                SeekyLog.Error(
                    "Symbol sweep: fff rejected the sweep pattern",
                    new InvalidOperationException(result.RegexFallbackError));
                return [];
            }

            totalFiles = result.TotalFiles == 0 ? totalFiles : result.TotalFiles;
            foreach (FffNativeClient.GrepMatch match in result.Matches)
            {
                scannedLines++;
                if (!SymbolClassifier.TryClassify(match.Path, match.Text, out SymbolClassifier.Symbol symbol))
                {
                    continue;
                }

                entries.Add(new Entry(
                    match.Path,
                    match.Line,
                    symbol.NameStart,
                    match.Text.Trim(),
                    symbol.Kind,
                    symbol.Name,
                    match.GitStatus,
                    match.IsBinary));

                if (entries.Count >= MaxSymbols)
                {
                    SeekyLog.Info($"Symbol sweep: stopped at the {MaxSymbols} symbol cap");
                    return entries;
                }
            }

            if (result.NextFileOffset == 0)
            {
                break;
            }

            if (stopwatch.ElapsedMilliseconds > BuildBudgetMs)
            {
                SeekyLog.Info(
                    $"Symbol sweep: stopped after {stopwatch.ElapsedMilliseconds}ms " +
                    $"({entries.Count} symbols, {fileOffset}/{totalFiles} files) — partial index");
                break;
            }

            fileOffset = result.NextFileOffset;
            reportStatus?.Invoke($"indexing symbols… {entries.Count} found");
        }

        SeekyLog.Info(
            $"Symbol sweep: {entries.Count} symbols from {scannedLines} candidate lines " +
            $"across {totalFiles} files in {stopwatch.ElapsedMilliseconds}ms ('{workspaceDir}')");
        return entries;
    }
}
