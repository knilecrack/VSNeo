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
        // Read once: CancellationTokenSource.Token throws ObjectDisposedException
        // after Dispose, and a send or BeginRead racing Dispose must not turn
        // into that instead of a plain "closed".
        private readonly CancellationToken _shutdownToken;
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

        /// <summary>
        /// Raised on the read thread for each vsneo_state push, decoded by the
        /// stream reader's fast path straight into a struct - no frame, no args
        /// array, no boxes, on the most frequent notification there is. Ordered
        /// with <see cref="NotificationReceived"/> through the same sequence
        /// number. Only ever raised alongside, never instead of the sequence
        /// bump.
        /// </summary>
        public event Action<StatePush>? StatePushReceived;

        private long _notificationSeq;

        /// <summary>
        /// How many notifications have been dispatched, counting the one being
        /// dispatched right now. Read inside a NotificationReceived handler it
        /// numbers that notification, and the numbers follow nvim's wire order -
        /// which is what lets a consumer tell whether one report came after
        /// another (CursorSynchronizer: a cursor report after an edit).
        /// </summary>
        public long NotificationSeq => Interlocked.Read(ref _notificationSeq);

        /// <summary>Raised on a background thread when the transport dies for any reason.</summary>
        public event Action<Exception>? Faulted;

        private NvimRpcClient(Process process, Stream channel)
        {
            _process = process;
            _channel = channel;
            _shutdownToken = _shutdown.Token;
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
        private static async Task<Stream> ConnectPipeAsync(Process process, string pipeName, CancellationToken ct)
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
            if (Volatile.Read(ref _disposed) != 0) return;

            // Deliberately not awaited: it reports failure through the Faulted
            // event, which is what trips the breaker.
            _ = Task.Run(() => ReadLoopAsync(_shutdownToken));
        }

        /// <summary>The last few lines nvim wrote to stderr, for diagnostics.</summary>
        public string StdErrTail => string.Join(Environment.NewLine, _stderr.ToArray());

        public Task<object?> RequestAsync(string method, params object[] args)
            => RequestAsync(method, Timeout.InfiniteTimeSpan, args);

        /// <summary>
        /// nvim_exec_lua with the Lua arguments written straight into the frame
        /// (WriteArrayHeader, WriteInt64, WriteSnapshotLines): for payloads that
        /// do not exist as objects yet, like a whole file's lines, which must
        /// not be materialized just to be encoded. Deliberately not a
        /// RequestAsync overload: with (method, chunk, lambda) the params
        /// overload binds instead, the lambda lands in object[], and a delegate
        /// goes on the wire - which is exactly the bug this shape once shipped.
        /// </summary>
        public Task<object?> ExecLuaAsync(string chunk, Action<MsgPackWriter> writeLuaArgs)
        {
            var id = unchecked((uint)Interlocked.Increment(ref _msgId));
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            if (FailIfDisposed(id, tcs)) return tcs.Task;

            LogRpc("request", "nvim_exec_lua", new object[] { chunk });

            _ = SendExecLuaAsync(id, chunk, writeLuaArgs);
            return tcs.Task;
        }

        /// <summary>
        /// Request with a bounded wait. Startup uses this: an nvim that answers
        /// the pipe but never responds (a wedged plugin, a blocked prompt) would
        /// otherwise leave the TCS in _pending forever and hang package
        /// initialization with nothing logged. On timeout the pending entry is
        /// removed before the exception is set, so a response that arrives late
        /// is dropped by Dispatch's TryRemove instead of completing a dead task.
        /// </summary>
        public Task<object?> RequestAsync(string method, TimeSpan timeout, params object[] args)
        {
            var id = unchecked((uint)Interlocked.Increment(ref _msgId));
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            if (FailIfDisposed(id, tcs)) return tcs.Task;

            LogRpc("request", method, args);

            Timer? timer = null;
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                timer = new Timer(_ =>
                {
                    if (_pending.TryRemove(id, out var p))
                        p.TrySetException(new TimeoutException(
                            "Neovim did not answer " + method + " within "
                            + (int)timeout.TotalSeconds + " seconds."));
                });
                timer.Change(timeout, Timeout.InfiniteTimeSpan);

                // The timer is one-shot; retire it as soon as the request
                // settles any way so it cannot fire into a completed TCS.
                _ = tcs.Task.ContinueWith(
                    _ => timer.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }

            _ = SendRequestAsync(id, method, args ?? Array.Empty<object>());

            return tcs.Task;
        }

        /// <summary>
        /// Add-then-check against Dispose: the entry goes into _pending first,
        /// then _disposed is read. Dispose sets the flag first and sweeps
        /// _pending after, so whichever side loses the race, the entry is
        /// either swept by Dispose or failed here - never left for a response
        /// that will not come. Returns true when the request is settled.
        /// </summary>
        private bool FailIfDisposed(uint id, TaskCompletionSource<object?> tcs)
        {
            if (Volatile.Read(ref _disposed) == 0) return false;
            if (_pending.TryRemove(id, out _))
                tcs.TrySetException(new ObjectDisposedException(nameof(NvimRpcClient)));
            return true;
        }

        /// <summary>
        /// Encodes the request frame directly (no frame array, no boxed msgid)
        /// and resolves the pending entry if the write itself fails.
        /// </summary>
        private async Task SendRequestAsync(uint id, string method, object[] args)
        {
            Interlocked.Increment(ref _sent);
            var writer = RentWriter();
            try
            {
                writer.WriteRequestFrame(id, method, args);
                await WriteLockedAsync(writer.Buffer, writer.Length).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_pending.TryRemove(id, out var p))
                    p.TrySetException(ex.GetBaseException());
            }
            finally
            {
                ReturnWriter(writer);
            }
        }

        /// <summary>The streamed-args twin of <see cref="SendRequestAsync(uint, string, object[])"/>:
        /// the frame is [0, msgid, "nvim_exec_lua", [chunk, args]] and writeLuaArgs
        /// writes the args array.</summary>
        private async Task SendExecLuaAsync(uint id, string chunk, Action<MsgPackWriter> writeLuaArgs)
        {
            Interlocked.Increment(ref _sent);
            var writer = RentWriter();
            try
            {
                writer.WriteRequestFrameHead(id, "nvim_exec_lua");
                writer.WriteArrayHeader(2);
                writer.WriteValue(chunk);
                writeLuaArgs(writer);
                await WriteLockedAsync(writer.Buffer, writer.Length).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_pending.TryRemove(id, out var p))
                    p.TrySetException(ex.GetBaseException());
            }
            finally
            {
                ReturnWriter(writer);
            }
        }

        /// <summary>
        /// nvim_input on the key path, one per swallowed keystroke. The frame for
        /// a given key string is constant, so it is encoded once and cached: a
        /// held-down key (jjjjj...) costs a dictionary lookup and one write, not
        /// a fresh frame, writer and encode per repeat. Distinct strings are
        /// bounded by the mappings a user has; the cap keeps a pathological
        /// caller from growing it without limit.
        /// </summary>
        private readonly ConcurrentDictionary<string, byte[]> _inputFrames
            = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        private const int MaxCachedInputFrames = 1024;

        public void NotifyInput(string keys)
        {
            LogRpc("notify", "nvim_input", new object[] { keys });

            if (!_inputFrames.TryGetValue(keys, out var bytes))
            {
                if (_inputFrames.Count >= MaxCachedInputFrames)
                {
                    _ = SendNotifyAsync(new object[] { 2, "nvim_input", new object[] { keys } });
                    return;
                }

                var writer = new MsgPackWriter();
                writer.WriteValue(new object[] { 2, "nvim_input", new object[] { keys } });
                bytes = new byte[writer.Length];
                Buffer.BlockCopy(writer.Buffer, 0, bytes, 0, writer.Length);
                _inputFrames.TryAdd(keys, bytes);
            }

            _ = SendRawAsync(bytes);
        }

        /// <summary>
        /// One async method for the whole fire-and-forget send, faults included:
        /// the previous SendAsync().ContinueWith(OnlyOnFaulted) shape allocated
        /// the state machine, its task, and the continuation per keystroke.
        /// </summary>
        private async Task SendNotifyAsync(object[] frame)
        {
            try
            {
                await SendAsync(frame).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A write that loses the race against Dispose is shutdown, not a
                // fault; tripping the breaker then only adds log noise.
                if (Volatile.Read(ref _disposed) == 0)
                    Faulted?.Invoke(ex.GetBaseException());
            }
        }

        private async Task SendRawAsync(byte[] bytes)
        {
            try
            {
                Interlocked.Increment(ref _sent);
                await WriteLockedAsync(bytes, bytes.Length).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _disposed) == 0)
                    Faulted?.Invoke(ex.GetBaseException());
            }
        }

        /// <summary>
        /// Trace-gated like Log.Key (VSNEO_TRACE_KEYS=1, Debug only): one line per
        /// outgoing call, so a repro can show exactly what reached nvim and in what
        /// order. The preview is the first short string argument - the keys for
        /// nvim_input - with Lua chunks truncated.
        /// </summary>
        [Conditional("DEBUG")]
        private static void LogRpc(string kind, string method, object[] args)
        {
            var preview = string.Empty;
            if (args != null)
            {
                foreach (var a in args)
                {
                    if (a is string s)
                    {
                        preview = s.Length > 60 ? s.Substring(0, 60) + "..." : s;
                        break;
                    }
                }
            }
            Infrastructure.Log.Key("rpc " + kind + " " + method
                + (preview.Length == 0 ? "" : "  " + preview));
        }

        private async Task SendAsync(object[] frame)
        {
            Interlocked.Increment(ref _sent);

            // Encoding is synchronous and happens outside the lock: only the I/O is
            // serialised, so a slow write never blocks another caller's encode.
            // The writer is pooled: one per in-flight send at most, instead of a
            // fresh 512-byte buffer per keystroke.
            var writer = RentWriter();
            try
            {
                writer.WriteValue(frame);
                await WriteLockedAsync(writer.Buffer, writer.Length).ConfigureAwait(false);
            }
            finally
            {
                ReturnWriter(writer);
            }
        }

        /// <summary>
        /// The only serialised part of a send. No FlushAsync: PipeStream.Flush
        /// is a no-op on both .NET Framework and .NET (pipes are unbuffered),
        /// so it only cost an async yield per send.
        /// </summary>
        private async Task WriteLockedAsync(byte[] buf, int len)
        {
            await _writeLock.WaitAsync(_shutdownToken).ConfigureAwait(false);
            try
            {
                await _channel.WriteAsync(buf, 0, len, _shutdownToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Reusable encode buffers. Sends serialise on the write lock, so the
        /// pool never holds more than a couple of entries in practice; a writer
        /// that grew past 1 MB (a whole-file resend) is left to the GC rather
        /// than pinning the memory for the session.
        /// </summary>
        private static readonly ConcurrentQueue<MsgPackWriter> WriterPool = new ConcurrentQueue<MsgPackWriter>();
        private const int MaxPooledBufferLength = 1024 * 1024;

        private static MsgPackWriter RentWriter() =>
            WriterPool.TryDequeue(out var writer) ? writer : new MsgPackWriter();

        private static void ReturnWriter(MsgPackWriter writer)
        {
            if (writer.Buffer.Length <= MaxPooledBufferLength)
            {
                writer.Reset();
                WriterPool.Enqueue(writer);
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
                        // Drain everything already buffered before paying for an
                        // async read: nvim flushes a keystroke's redraw and its
                        // state push in one write, so the second (and third...)
                        // item costs no Task and no state machine.
                        while (reader.TryRead(out var buffered))
                            DispatchItem(buffered);

                        var result = await reader.ReadAsync(ct).ConfigureAwait(false);

                        if (result.IsEmpty)
                        {
                            // Dispose kills nvim, and the pipe closing is then the
                            // expected end of the loop, not a fault. Logging it as
                            // one made every routine Visual Studio shutdown look like
                            // nvim crashing mid-session.
                            if (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) break;

                            // Clean EOF: nvim exited or closed the channel. This is
                            // every bit as fatal as an exception, and used to pass
                            // silently - no Faulted, no log, and the key path kept
                            // swallowing input into a channel nothing was reading.
                            Infrastructure.Log.Write("nvim closed the RPC pipe (process exited?). stderr tail: "
                                                     + StdErrTail);
                            Faulted?.Invoke(new IOException("Neovim closed the RPC pipe."));
                            break;
                        }

                        DispatchItem(result);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) when (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
            {
                // Dispose, not a fault. The shutdown token cannot end a pending
                // pipe read on .NET Framework (PipeStream ignores it mid-read);
                // it is _channel.Dispose() closing the handle that does, and
                // that read completes with ERROR_OPERATION_ABORTED, which
                // PipeStream surfaces as an IOException rather than the
                // zero-byte read the branch above expects. Reporting it
                // tripped the breaker and posted a status-bar update against
                // a package already in Dispose.
            }
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

        /// <summary>One decoded item from the read loop: a state push to its own
        /// channel, a response to its pending request, anything else through
        /// Dispatch. Never an end-of-stream - the async read reports that one.</summary>
        private void DispatchItem(MsgPackStreamReader.ReadResult result)
        {
            if (result.State is StatePush push)
            {
                Interlocked.Increment(ref _notificationSeq);
                try
                {
                    StatePushReceived?.Invoke(push);
                }
                catch (Exception ex)
                {
                    // Same rule as Dispatch: one throwing handler must not kill
                    // the only thread that hears from nvim.
                    Infrastructure.Log.Write("state push handler threw", ex);
                }
                return;
            }

            if (result.Response is MsgPackStreamReader.NvimResponse response)
            {
                Interlocked.Increment(ref _received);
                CompleteRequest(response.MsgId, response.Error, response.Result);
                return;
            }

            if (result.Frame != null)
                Dispatch(result.Frame);
        }

        private void Dispatch(object[] frame)
        {
            if (frame == null || frame.Length < 3) return;

            Interlocked.Increment(ref _received);

            // The reader widens integers to long; Convert's interface dance is
            // the slow path, taken never in practice.
            long frameType = frame[0] is long ft ? ft : Convert.ToInt64(frame[0]);
            switch (frameType)
            {
                case 1: // response that missed the fast path (a frame split across reads)
                    var id = frame[1] is long mid ? unchecked((uint)mid) : Convert.ToUInt32(frame[1]);
                    CompleteRequest(id, frame[2], frame.Length > 3 ? frame[3] : null);
                    break;

                case 2: // notification
                    // A msgpack-rpc notification always carries a method name, so
                    // nil here would mean a malformed frame; the read loop's catch
                    // already treats that as fatal.
                    var method = ToUtf8(frame[1])!;
                    var args = frame[2] as object[] ?? Array.Empty<object>();
                    Interlocked.Increment(ref _notificationSeq);
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

        /// <summary>Settles the pending entry for one response, whichever decode
        /// path it arrived by. A response for an unknown id (a late answer to a
        /// timed-out request) is dropped, as before.</summary>
        private void CompleteRequest(uint id, object? error, object? result)
        {
            if (!_pending.TryRemove(id, out var tcs)) return;
            if (error != null) tcs.TrySetException(new NvimException(Describe(error)));
            else tcs.TrySetResult(result);
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
            // The flag goes first: the read loop and the send paths read it to
            // tell shutdown from a fault, and RequestAsync reads it after
            // registering its entry (see FailIfDisposed).
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _shutdown.Cancel(); } catch { }
            try { _channel.Dispose(); } catch { }
            try { if (!_process.HasExited) _process.Kill(); } catch { }
            // Closing the job is the backstop that also fires when Kill did not run.
            try { _job?.Dispose(); } catch { }
            try { _process.Dispose(); } catch { }

            // Not left to the read loop's finally: that only runs if BeginRead
            // did, and a request registered after its sweep would otherwise
            // wait for a response from a closed pipe forever.
            FailAllPending(new ObjectDisposedException(nameof(NvimRpcClient)));

            try { _shutdown.Dispose(); } catch { }
            // _writeLock is deliberately not disposed. A send that already
            // holds it releases it from its finally after the write fails, and
            // SemaphoreSlim.Release throws ObjectDisposedException then - which
            // replaces the real I/O error as the exception a caller sees. The
            // semaphore owns no kernel handle unless AvailableWaitHandle is
            // touched, which nothing here does, so there is nothing to free.
        }
    }

    internal sealed class NvimException : Exception
    {
        public NvimException(string? message) : base(message) { }
    }
}
