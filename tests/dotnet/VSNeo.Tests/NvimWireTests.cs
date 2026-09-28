using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using VSNeo.Tests.Stubs;
using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// The wire path end to end against a real headless nvim: request/response,
/// streamed nvim_exec_lua arguments (the file prime), nvim_input, and the
/// vsneo_state fast path - everything the rest of the suite cannot see,
/// because it links the msgpack layer but not the RPC client. This test
/// exists because a prime that silently encoded a delegate instead of the
/// file's lines once shipped exactly that way: every headless check passed,
/// and the extension came up with empty buffers and a caret pinned to (0,0).
/// Skips on machines without an nvim binary; CI runs the Lua suites, so
/// nvim is always present there.
/// </summary>
public class NvimWireTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Prime_input_and_state_pushes_flow_over_the_real_wire()
    {
        var nvimPath = FindNvim();
        if (nvimPath == null) return;

        using var client = await NvimRpcClient.ConnectAsync(nvimPath, CancellationToken.None);

        var hub = new NvimStateHub();
        client.NotificationReceived += hub.OnNotification;
        client.StatePushReceived += hub.OnStatePush;
        Exception? fault = null;
        client.Faulted += ex => fault ??= ex;
        client.BeginRead();

        // The same attach sequence NvimSession runs: ui first, then the flag
        // the companion checks, then the companion itself on its channel id.
        var options = new Dictionary<string, object>
        {
            ["ext_linegrid"] = true,
            ["ext_cmdline"] = true,
            ["ext_messages"] = true,
            ["ext_popupmenu"] = true,
            ["rgb"] = true,
        };
        await client.RequestAsync("nvim_ui_attach", RequestTimeout, 200, 60, options);
        await client.RequestAsync("nvim_set_var", RequestTimeout, "vsneo", 1);
        var apiInfo = await client.RequestAsync("nvim_get_api_info", RequestTimeout) as object[];
        Assert.NotNull(apiInfo);
        long channel = Convert.ToInt64(apiInfo![0]);
        await client.RequestAsync(
            "nvim_exec_lua", RequestTimeout,
            File.ReadAllText(FindCompanionScript()), new object[] { channel });

        var created = await client.RequestAsync("nvim_create_buf", RequestTimeout, true, false);
        long buf = created is NvimHandle h ? h.Id : Convert.ToInt64(created!);
        Assert.True(buf > 0, "nvim_create_buf returned " + created);
        await client.RequestAsync("nvim_win_set_buf", RequestTimeout, 0, buf);

        // Primed the way BufferMirror primes: streamed off the snapshot into
        // an nvim_exec_lua frame. Big enough that nvim's redraw for it spans
        // several pipe reads, which is what exercises the stream reader's
        // probe path (a short frame decode, then a verified full skip).
        var lines = new string[4000];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = "line " + i + " of the wire test";
        ITextSnapshot snapshot = new StubSnapshot(string.Join("\n", lines));

        await client.ExecLuaAsync("return vsneo.set_all_lines(...)", w =>
        {
            w.WriteArrayHeader(2);
            w.WriteInt64(buf);
            w.WriteSnapshotLines(snapshot);
        });

        var count = await client.RequestAsync("nvim_buf_line_count", RequestTimeout, buf);
        Assert.Equal(lines.Length, Convert.ToInt32(count));

        // j moves the cursor; the companion's push must come back through the
        // state fast path with the new line. This is the per-keystroke loop
        // the whole extension stands on.
        var moved = WaitForCursor(hub, targetLine: 1);
        client.NotifyInput("j");
        await moved;

        // G slams a full-screen redraw through the skip walk on the way to
        // the last line.
        var bottom = WaitForCursor(hub, targetLine: lines.Length - 1);
        client.NotifyInput("G");
        await bottom;

        Assert.Equal(VimMode.Normal, hub.Mode);
        Assert.Null(fault);
    }

    /// <summary>Completes when the hub reports the cursor on the target line;
    /// throws after ten seconds - a push that never arrives is the failure
    /// this suite exists to catch.</summary>
    private static Task WaitForCursor(NvimStateHub hub, int targetLine)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.CursorMoved += (line, col) =>
        {
            if (line == targetLine) tcs.TrySetResult(null);
        };
        return Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)))
            .ContinueWith(t =>
            {
                if (!tcs.Task.IsCompleted)
                    throw new TimeoutException("the cursor never reached line " + targetLine);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static string? FindNvim()
    {
        var env = Environment.GetEnvironmentVariable("VSNEO_NVIM_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        var wellKnown = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Neovim", "bin", "nvim.exe");
        if (File.Exists(wellKnown)) return wellKnown;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (path != null)
        {
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (dir.Length == 0) continue;
                var candidate = Path.Combine(dir, "nvim.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string FindCompanionScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "VSNeo_Extension", "Lua", "vsneo.lua");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "could not locate VSNeo_Extension/Lua/vsneo.lua above " + AppContext.BaseDirectory);
    }
}
