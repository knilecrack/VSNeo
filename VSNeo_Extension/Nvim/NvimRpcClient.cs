using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace VSNeo_Extension.Nvim
{
    /// <summary>
    /// Minimal msgpack-rpc client for an embedded Neovim process.
    /// Wire format is the msgpack-rpc spec, NOT JSON-RPC:
    ///   request      [0, msgid, method, params]
    ///   response     [1, msgid, error, result]
    ///   notification [2, method, params]
    /// Safe to call from any thread. Nothing here touches the UI thread and
    /// nothing here ever blocks a caller synchronously.
    /// </summary>
    internal sealed class NvimRpcClient : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _channel;
        private readonly ConcurrentQueue<string> _stderr = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<object?>> _pending
            = new ConcurrentDictionary<uint, TaskCompletionSource<object?>>();
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private int _msgId;
        private int _disposed;
        private long _sent;
        private long _received;
        // TryAssign can fail to enroll the process in a job, so this stays null
        // on machines where the job object cannot be created.
        private Infrastructure.ProcessJob? _job;

        /// <summary>Traffic counters. A storm shows up here before anywhere else.</summary>
        public long Sent => Volatile.Read(ref _sent);
        public long Received => Volatile.Read(ref _received);

        /// <summary>Raised on a background thread for every notification nvim sends.</summary>
        public event Action<string, object[]>? NotificationReceived;

        /// <summary>Raised on a background thread when the transport dies for any reason.</summary>
        public event Action<Exception>? Faulted;

        private NvimRpcClient(Process process, Stream channel)
        {
            _process = process;
            _channel = channel;
        }

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
            var args = new List<string>
            {
                "--headless", "-u", "NORC",
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
            if (extraArgs != null) args.AddRange(extraArgs);

            var psi = new ProcessStartInfo
            {
                FileName = nvimPath,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();

            // Before anything can go wrong: with --listen there is no stdin to reach
            // EOF, so without this a killed devenv leaves nvim running forever.
            var job = Infrastructure.ProcessJob.TryAssign(process);

            NvimRpcClient? client = null;
            try
            {
                var pipe = await ConnectPipeAsync(process, pipeName, ct).ConfigureAwait(false);
                client = new NvimRpcClient(process, pipe) { _job = job };
            }
            finally
            {
                if (client == null)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    process.Dispose();
                    job?.Dispose();
                }
            }

            // client is non-null here: every path that failed to create it threw
            // out of the try above, so only the success path reaches this point.
            NvimRpcClient connected = client!;

            // Drained continuously: an unread stderr pipe fills and then blocks nvim.
            // The tail is kept only so a fault can say what nvim complained about.
            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                connected._stderr.Enqueue(e.Data);
                while (connected._stderr.Count > 20) connected._stderr.TryDequeue(out _);
            };
            process.BeginErrorReadLine();

            return connected;
        }

        /// <summary>
        /// nvim creates the pipe a moment after the process starts, so the first
        /// connect usually loses the race. Retry until it answers, nvim dies, or we
        /// give up - never block, this runs inside the async startup path.
        /// </summary>
        private static async Task<Stream> ConnectPipeAsync(
            Process process, string pipeName, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (process.HasExited)
                    throw new IOException(
                        "Neovim exited with code " + process.ExitCode + " before its pipe was ready.");

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

                if (connected) return pipe;

                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException(
                        "Neovim did not open " + pipeName + " within 10 seconds.");

                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }

        /// <summary>Starts the long-lived read loop. Call once, after subscribing.</summary>
        public void BeginRead()
        {
            // Deliberately not awaited: it reports failure through the Faulted
            // event, which is what trips the breaker.
            _ = Task.Run(() => ReadLoopAsync(_shutdown.Token));
        }

        /// <summary>The last few lines nvim wrote to stderr, for diagnostics.</summary>
        public string StdErrTail => string.Join(Environment.NewLine, _stderr.ToArray());

        public Task<object?> RequestAsync(string method, params object[] args)
        {
            var id = unchecked((uint)Interlocked.Increment(ref _msgId));
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            var frame = new object[] { 0, id, method, args ?? Array.Empty<object>() };
            _ = SendAsync(frame).ContinueWith(t =>
            {
                if (t.IsFaulted && _pending.TryRemove(id, out var p))
                    p.TrySetException(t.Exception.GetBaseException());
            }, TaskScheduler.Default);

            return tcs.Task;
        }

        /// <summary>Fire and forget. Used for nvim_input, where we never want to await.</summary>
        public void Notify(string method, params object[] args)
        {
            var frame = new object[] { 2, method, args ?? Array.Empty<object>() };
            _ = SendAsync(frame).ContinueWith(
                t =>
                {
                    // A write that loses the race against Dispose is shutdown, not a
                    // fault; tripping the breaker then only adds log noise.
                    if (Volatile.Read(ref _disposed) == 0)
                        Faulted?.Invoke(t.Exception.GetBaseException());
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private async Task SendAsync(object[] frame)
        {
            Interlocked.Increment(ref _sent);

            // Encoding is synchronous and happens outside the lock: only the I/O is
            // serialised, so a slow write never blocks another caller's encode.
            var writer = new MsgPackWriter();
            writer.WriteValue(frame);

            await _writeLock.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                await _channel.WriteAsync(writer.Buffer, 0, writer.Length, _shutdown.Token).ConfigureAwait(false);
                await _channel.FlushAsync(_shutdown.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            try
            {
                // Hands back one complete frame at a time, which is what we need
                // since nvim's stream carries no length prefix.
                using (var reader = new MsgPackStreamReader(_channel))
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var frame = await reader.ReadFrameAsync(ct).ConfigureAwait(false);
                        if (frame == null)
                        {
                            // Dispose kills nvim, and the pipe closing is then the
                            // expected end of the loop, not a fault. Logging it as
                            // one made every routine Visual Studio shutdown look like
                            // nvim crashing mid-session.
                            if (ct.IsCancellationRequested) break;

                            // Clean EOF: nvim exited or closed the channel. This is
                            // every bit as fatal as an exception, and used to pass
                            // silently - no Faulted, no log, and the key path kept
                            // swallowing input into a channel nothing was reading.
                            Infrastructure.Log.Write("nvim closed the RPC pipe (process exited?). stderr tail: "
                                                     + StdErrTail);
                            Faulted?.Invoke(new IOException("Neovim closed the RPC pipe."));
                            break;
                        }

                        Dispatch(frame);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // One bad frame or one throwing handler must be loud, not silent:
                // this loop dying IS the extension dying.
                Infrastructure.Log.Write("nvim read loop died. stderr tail: " + StdErrTail, ex);
                Faulted?.Invoke(ex);
            }
            finally
            {
                FailAllPending(new IOException("Neovim RPC channel closed."));
            }
        }

        private void Dispatch(object[] frame)
        {
            if (frame == null || frame.Length < 3) return;

            Interlocked.Increment(ref _received);

            switch (Convert.ToInt32(frame[0]))
            {
                case 1: // response
                    var id = Convert.ToUInt32(frame[1]);
                    if (!_pending.TryRemove(id, out var tcs)) return;
                    if (frame[2] != null) tcs.TrySetException(new NvimException(Describe(frame[2])));
                    else tcs.TrySetResult(frame.Length > 3 ? frame[3] : null);
                    break;

                case 2: // notification
                    // A msgpack-rpc notification always carries a method name, so
                    // nil here would mean a malformed frame; the read loop's catch
                    // already treats that as fatal.
                    var method = ToUtf8(frame[1])!;
                    var args = frame[2] as object[] ?? Array.Empty<object>();
                    try
                    {
                        NotificationReceived?.Invoke(method, args);
                    }
                    catch (Exception ex)
                    {
                        // A throwing handler must not kill the read loop: this
                        // thread is the only way anything hears from nvim.
                        Infrastructure.Log.Write("notification handler threw for " + method, ex);
                    }
                    break;
            }
        }

        private static string? ToUtf8(object o) =>
            o is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : o as string ?? o?.ToString();

        private static string? Describe(object error) =>
            error is object[] parts && parts.Length > 1 ? ToUtf8(parts[1]) : ToUtf8(error);

        private void FailAllPending(Exception ex)
        {
            foreach (var key in _pending.Keys)
                if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(ex);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _shutdown.Cancel(); } catch { }
            try { _channel.Dispose(); } catch { }
            try { if (!_process.HasExited) _process.Kill(); } catch { }
            // Closing the job is the backstop that also fires when Kill did not run.
            try { _job?.Dispose(); } catch { }
            try { _process.Dispose(); } catch { }
            _shutdown.Dispose();
            _writeLock.Dispose();
        }
    }

    internal sealed class NvimException : Exception
    {
        public NvimException(string? message) : base(message) { }
    }
}
