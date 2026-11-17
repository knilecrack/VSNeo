// The search pipeline: the engine, backend, history and the queries.

namespace VSNeo.Seeky;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static partial class SeekyPickerController
{
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

    /// <summary>
    /// A search disposes its own source when it finishes, and can do so between the swap that
    /// hands us the source and our Cancel: nothing is left to cancel then.
    /// </summary>
    private static void CancelQuietly(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task HandleSearchAsync(string query, string mode, string grepMode, string fileMode)
    {
        int generation = Interlocked.Increment(ref searchGeneration);
        lastSearchQuery = query;
        lastSearchMode = mode;
        using var searchTokenSource = new CancellationTokenSource();
        CancellationTokenSource? previousSearch = Interlocked.Exchange(ref searchCancellation, searchTokenSource);
        if (previousSearch is not null)
        {
            CancelQuietly(previousSearch);
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
            bool editorMode = mode is "path" or "buffer" || SeekyLists.IsListMode(mode);
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
            var texts = new Dictionary<string, string>();
            int? selectedIndex = null;
            if (SeekyLists.IsListMode(mode))
            {
                Tuple<string, List<SeekyLists.Row>>? cache = listCache;
                if (cache is null || cache.Item1 != mode)
                {
                    List<SeekyLists.Row> rows = mode == "oldfiles"
                        ? SeekyLists.Oldfiles(RelativeToWorkspace)
                        : SeekyLists.IsNvimMode(mode)
                            ? await SeekyLists.NvimRowsAsync(mode, RelativeToWorkspace, PostStatus)
                            : new List<SeekyLists.Row>();
                    cache = Tuple.Create(mode, rows);
                    listCache = cache;
                }

                items = SeekyLists.Filter(mode, cache.Item2, query, MaxLineResults);
            }
            else if (mode == "buffer")
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
                        texts[JumpKey(displayPath, hit.Line)] = hit.Text;
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
                        if (editor.LineText(entry.Line) is { } declaration)
                        {
                            texts[JumpKey(displayPath, entry.Line)] = declaration;
                        }
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
                        texts[JumpKey(m.Path, m.Line)] = m.Text;
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
                    texts[JumpKey(h.Path, h.Line)] = h.Text;
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
            else if (mode == "mixed")
            {
                // Files & Folders: one fuzzy list over both, ranked by fff. Opening a folder
                // reveals it in Explorer; opening a file works as in Find Files, ':line[:col]'
                // included (file rows only - a folder has no lines).
                SeekyEngineClient.FileSearch found =
                    await Engine.FindMixedAsync(query, activeDocumentRelative, maxResults, cancellationToken);
                SeekyEngineClient.QueryLocation? location = found.Location;
                items = found.Items
                    .Select(m => (object)new
                    {
                        name = m.Path,
                        path = m.Path,
                        line = m.IsDirectory ? null : location?.Line,
                        col = m.IsDirectory ? null : location?.Col,
                        isDirectory = m.IsDirectory,
                        frecency = m.FrecencyScore,
                        gitStatus = m.GitStatus,
                        isBinary = m.IsBinary,
                    })
                    .ToList();
            }
            else if (query.Length == 0)
            {
                // Find Files on an empty prompt: recent files (fff's access frecency is never
                // fed - see RecentFiles). The active file is left off, so the top row is the
                // previous file and Enter flips between the last two.
                items = RecentFiles.InWorkspace(workspace!, activeDocumentRelative, maxResults)
                    .Select(path => (object)new { name = path, path })
                    .ToList();
            }
            else
            {
                // current_file deprioritizes the file already open in VS (alternate-file workflow).
                SeekyEngineClient.FileSearch search =
                    await Engine.FindFilesAsync(query, fileMode, activeDocumentRelative, maxResults, cancellationToken);

                // "Foo.cs:42:9": fff strips the location off the fuzzy text and hands it back, so
                // every row carries it - the preview centers on it and Enter opens there.
                SeekyEngineClient.QueryLocation? location = search.Location;
                items = search.Items
                    .Select(f => (object)new
                    {
                        name = f.Path,
                        path = f.Path,
                        line = location?.Line,
                        col = location?.Col,
                        frecency = f.FrecencyScore,
                        gitStatus = f.GitStatus,
                        isBinary = f.IsBinary,
                    })
                    .ToList();
            }

            if (cancellationToken.IsCancellationRequested || generation != searchGeneration)
            {
                SeekyLog.Info($"Search ({query.Length} chars) ({mode}/{grepMode}): discarded stale results ({items.Count} items)");
                return;
            }

            SeekyLog.Info($"Search ({query.Length} chars) ({mode}/{grepMode}): {items.Count} results in {stopwatch.ElapsedMilliseconds}ms");
            jumpColumns = columns;
            jumpTexts = texts;
            PostJson(new
            {
                type = "results",
                done = true,
                // A full document outline legitimately exceeds maxResults — it isn't capped.
                capped = mode == "buffer" || SeekyLists.IsListMode(mode)
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
            SeekyLog.Info($"Search ({query.Length} chars) ({mode}/{grepMode}): cancelled");
        }
        catch (Exception ex)
        {
            SeekyLog.Error($"Search ({query.Length} chars) ({mode}/{grepMode}) failed", ex);
            PostStatus("search failed: " + ex.Message);
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref searchCancellation, null, searchTokenSource);
        }
    }
}
