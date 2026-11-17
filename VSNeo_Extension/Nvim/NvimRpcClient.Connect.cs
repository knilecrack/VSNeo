// Starting nvim and connecting to its named pipe.


using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace VSNeo_Extension.Nvim;

internal sealed partial class NvimRpcClient
{
    /// <summary>
    /// Starts nvim and connects to it over a named pipe.
    ///
    /// Not stdio, and not by preference. nvim's stdio is libuv-backed and on
    /// Windows it requires overlapped handles; the anonymous pipes .NET creates
    /// for RedirectStandardInput/Output are synchronous, and nvim exits with
    /// code 1 about 100ms after start having written nothing at all - no stderr,
    /// no message, just gone. The same nvim answers a request perfectly over a
    /// shell pipe or a file, which makes this look like our encoding until you
    /// measure it. --listen sidesteps the whole thing: NamedPipeClientStream
    /// opens with PipeOptions.Asynchronous, which is what libuv wants.
    ///
    /// --headless rather than --embed because --embed implies stdio RPC.
    /// The arguments and the config directory are <see cref="NvimLaunch"/>'s.
    ///
    /// The read loop does not start here. The caller subscribes to
    /// <see cref="NotificationReceived"/> first and then calls
    /// <see cref="BeginRead"/>, so no redraw can slip past an unwired handler.
    /// </summary>
    public static async Task<NvimRpcClient> ConnectAsync(
        string nvimPath, string? configDirectory, CancellationToken ct)
    {
        // A per-session pipe name. Two Visual Studio instances must not collide.
        var pipeName = "vsneo-" + Guid.NewGuid().ToString("n");

        var psi = new ProcessStartInfo
        {
            FileName = nvimPath,
            Arguments = NvimLaunch.Arguments(pipeName),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var pair in NvimLaunch.Environment(configDirectory))
        {
            psi.EnvironmentVariables[pair.Key] = pair.Value;
        }

        var stderr = new ConcurrentQueue<string>();
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        // Drain before waiting for the RPC pipe: enough early stderr output
        // would otherwise block nvim before it ever creates that pipe.
        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data == null)
            {
                return;
            }

            stderr.Enqueue(e.Data);
            while (stderr.Count > 20)
            {
                stderr.TryDequeue(out _);
            }
        };
        process.BeginErrorReadLine();

        // Before anything can go wrong: with --listen there is no stdin to reach
        // EOF, so without this a killed devenv leaves nvim running forever.
        var job = Infrastructure.ProcessJob.TryAssign(process);

        NvimRpcClient? client = null;
        try
        {
            var pipe = await ConnectPipeAsync(process, pipeName, ct).ConfigureAwait(false);
            client = new NvimRpcClient(process, pipe, stderr) { _job = job };
        }
        finally
        {
            if (client == null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch { }

                process.Dispose();
                job?.Dispose();
            }
        }

        // client is non-null here: every path that failed to create it threw
        // out of the try above, so only the success path reaches this point.
        return client!;
    }

    /// <summary>
    /// nvim creates the pipe a moment after the process starts, so the first
    /// connect usually loses the race. Retry until it answers, nvim dies, or we
    /// give up - never block, this runs inside the async startup path.
    /// </summary>
    private static async Task<Stream> ConnectPipeAsync(Process process, string pipeName, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new IOException(
                    "Neovim exited with code " + process.ExitCode + " before its pipe was ready.");
            }

            // Asynchronous is not optional: it is the overlapped-handle mode
            // libuv expects, and the whole reason this class is not on stdio.
            var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            bool connected = false;
            try
            {
                await pipe.ConnectAsync(100, ct).ConfigureAwait(false);
                connected = true;
            }
            catch (OperationCanceledException) { pipe.Dispose(); throw; }
            catch { pipe.Dispose(); }

            if (connected)
            {
                return pipe;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "Neovim did not open " + pipeName + " within 10 seconds.");
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }
}
