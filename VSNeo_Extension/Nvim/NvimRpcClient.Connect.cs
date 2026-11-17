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
    /// --headless rather than --embed because --embed implies stdio RPC. The
    /// difference that costs us is that --embed makes nvim defer startup until a
    /// UI attaches; with -u NORC there is nothing to defer, but milestone 5 will
    /// want to revisit this when real configs start loading.
    ///
    /// The read loop does not start here. The caller subscribes to
    /// <see cref="NotificationReceived"/> first and then calls
    /// <see cref="BeginRead"/>, so no redraw can slip past an unwired handler.
    /// </summary>
    public static async Task<NvimRpcClient> ConnectAsync(
        string nvimPath, CancellationToken ct, params string[] extraArgs)
    {
        // A per-session pipe name. Two Visual Studio instances must not collide.
        var pipeName = "vsneo-" + Guid.NewGuid().ToString("n");

        // -u NORC keeps the user's init.lua out of the startup path for now: a
        // slow plugin manager there is a hang we would inherit.
        // -i NONE keeps the default shada file out: every Visual Studio
        // instance runs its own nvim, and a shared shada leaks registers,
        // marks, jumplist and history between them (nvim merges it on
        // focus changes, so a yank in one instance surfaces in another's
        // registers popup).
        var args = new List<string>
        {
            "--headless", "-u", "NORC", "-i", "NONE",
            // Opt-in plugins use the standard packages layout rooted at
            // ~/.vsneo (pack/<group>/start/<name>, or opt/<name> for
            // :packadd from ~/.vsneorc). packpath must be set with --cmd,
            // which runs before startup's packloadall: afterwards both
            // packloadall and :packadd ignore *start* directories, which
            // is verified behavior, not a quirk to code around. Only this
            // root is added - the user's regular nvim plugins stay out.
            "--cmd", "\"exe 'set packpath+=' . fnameescape(expand('~/.vsneo'))\"",
            "--listen", @"\\.\pipe\" + pipeName
        };
        if (extraArgs != null)
        {
            args.AddRange(extraArgs);
        }

        var psi = new ProcessStartInfo
        {
            FileName = nvimPath,
            Arguments = string.Join(" ", args),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

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
