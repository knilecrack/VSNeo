using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VSNeo.Seeky;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// The Seeky picker's engine end to end: SeekyEngineClient (the shipped in-proc file) starts
/// a real seeky-engine, searches this repository through fff, and survives the engine dying.
/// Returns early without SEEKY_ENGINE_PATH (xunit 2 has no dynamic skip); the CI unit job
/// publishes the engine and points the variable at it, so there the test really runs.
/// </summary>
public class SeekyEngineClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Searches_this_repository_through_the_engine()
    {
        string? engine = EnginePath();
        if (engine == null) return;

        string repo = RepoRoot();
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var client = new SeekyEngineClient(() => engine, statuses.Enqueue);
        using var cts = new CancellationTokenSource(Timeout);

        await client.StartAsync(repo, cts.Token);
        Assert.Contains(statuses, s => s.StartsWith("index ready", StringComparison.Ordinal));

        var files = await client.FindFilesAsync("seekyengineclient", null, 10, cts.Token);
        Assert.Contains(files.Items, f => f.Path.EndsWith("Seeky/SeekyEngineClient.cs", StringComparison.Ordinal));

        var located = await client.FindFilesAsync("SeekyEngineClient.cs:12:5", null, 1, cts.Token);
        Assert.Equal(new SeekyEngineClient.QueryLocation(12, 5), located.Location);

        // A literal that occurs in exactly one known place outside this test.
        var grep = await client.GrepAsync("internal sealed class SeekyEngineClient", SeekyEngineClient.GrepMode.Plain, 20, cts.Token);
        var hit = Assert.Single(grep.Matches, m => m.Path.EndsWith("Seeky/SeekyEngineClient.cs", StringComparison.Ordinal));
        var range = Assert.Single(hit.Ranges);
        Assert.Equal("internal sealed class SeekyEngineClient", hit.Text.Substring(range.Start, range.End - range.Start));

        var badRegex = await client.GrepAsync("[unclosed", SeekyEngineClient.GrepMode.Regex, 5, cts.Token);
        Assert.NotNull(badRegex.RegexFallbackError);

        var symbols = await client.SymbolsAsync(repo, "SeekyEngineClient", 5, cts.Token);
        var symbol = Assert.Single(symbols, s => s.Name == "SeekyEngineClient" && s.Kind == "class");
        Assert.Equal(0, symbol.NameRanges[0].Start);

        await client.TrackQueryAsync("seekyengineclient", Path.Combine(repo, files.Items[0].Path), cts.Token);
        var history = await client.GetHistoryAsync(5, cts.Token);
        Assert.Contains("seekyengineclient", history);
    }

    [Fact]
    public async Task A_dead_engine_restarts_once_then_the_circuit_opens()
    {
        string? engine = EnginePath();
        if (engine == null) return;

        string repo = RepoRoot();
        using var client = new SeekyEngineClient(() => engine, null);
        using var cts = new CancellationTokenSource(Timeout);

        await client.StartAsync(repo, cts.Token);
        KillEngines();
        await WaitUntil(() => client.IsConnectionDead, cts.Token);

        // First death: the next call starts a fresh engine.
        await client.StartAsync(repo, cts.Token);
        var files = await client.FindFilesAsync("readme", null, 5, cts.Token);
        Assert.NotEmpty(files.Items);
        Assert.False(client.IsStopped);

        // Second death: no more restarts this session, and calls fail fast.
        KillEngines();
        await WaitUntil(() => client.IsStopped, cts.Token);
        await Assert.ThrowsAsync<SeekyEngineException>(() => client.StartAsync(repo, cts.Token));
    }

    [Fact]
    public async Task A_missing_engine_fails_the_call_not_the_process()
    {
        using var client = new SeekyEngineClient(() => Path.Combine(Path.GetTempPath(), "no-such-seeky-engine.exe"), null);
        var ex = await Assert.ThrowsAsync<SeekyEngineException>(() => client.StartAsync(RepoRoot(), CancellationToken.None));
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public async Task An_engine_that_dies_on_startup_fails_fast_and_opens_the_circuit()
    {
        // Any program that exits at once on the engine's arguments stands in for a crash in Main.
        string quitter = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "where.exe")
            : "/bin/false";
        if (!File.Exists(quitter))
        {
            return;
        }

        using var client = new SeekyEngineClient(() => quitter, null);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var first = await Assert.ThrowsAsync<SeekyEngineException>(() => client.StartAsync(RepoRoot(), CancellationToken.None));
        Assert.Contains("exited during startup", first.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"waited {clock.Elapsed} for a dead engine's pipe");

        await Assert.ThrowsAsync<SeekyEngineException>(() => client.StartAsync(RepoRoot(), CancellationToken.None));
        Assert.True(client.IsStopped);
        var stopped = await Assert.ThrowsAsync<SeekyEngineException>(() => client.StartAsync(RepoRoot(), CancellationToken.None));
        Assert.Contains("stopped", stopped.Message);
    }

    private static string? EnginePath()
    {
        string? path = Environment.GetEnvironmentVariable("SEEKY_ENGINE_PATH");
        return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : path;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static void KillEngines()
    {
        foreach (Process p in Process.GetProcessesByName("seeky-engine"))
        {
            using (p)
            {
                try { p.Kill(); } catch { }
            }
        }
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            await Task.Delay(50, ct);
        }
    }
}
