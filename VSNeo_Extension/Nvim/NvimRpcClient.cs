using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace VSNeo_Extension.Nvim;


/// <summary>
/// Minimal msgpack-rpc client for an embedded Neovim process.
/// Wire format is the msgpack-rpc spec, NOT JSON-RPC:
///   request      [0, msgid, method, params]
///   response     [1, msgid, error, result]
///   notification [2, method, params]
/// Safe to call from any thread. Nothing here touches the UI thread and
/// nothing here ever blocks a caller synchronously.
/// </summary>
internal sealed partial class NvimRpcClient : IDisposable
{
    private readonly Process _process;

    private readonly Stream _channel;

    private readonly ConcurrentQueue<string> _stderr;

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

    // Set when the read loop ends, before its final sweep: nothing will
    // ever answer a request registered after that, and Dispose may be far
    // off (or never come), so FailIfDisposed fails those too.
    private int _dead;

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

    private NvimRpcClient(Process process, Stream channel, ConcurrentQueue<string>? stderr = null)
    {
        _process = process;
        _channel = channel;
        _stderr = stderr ?? new ConcurrentQueue<string>();
        _shutdownToken = _shutdown.Token;
    }

    /// <summary>Starts the long-lived read loop. Call once, after subscribing.</summary>
    public void BeginRead()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

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
        if (FailIfDisposed(id, tcs))
        {
            return tcs.Task;
        }

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
        if (FailIfDisposed(id, tcs))
        {
            return tcs.Task;
        }

        LogRpc("request", method, args);

        Timer? timer = null;
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            timer = new Timer(_ =>
            {
                if (_pending.TryRemove(id, out var p))
                {
                    p.TrySetException(new TimeoutException(
                        "Neovim did not answer " + method + " within "
                        + (int)timeout.TotalSeconds + " seconds."));
                }
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
    /// that will not come. The read loop's death (_dead) is the same race
    /// against its own final sweep. Returns true when the request is settled.
    /// </summary>
    private bool FailIfDisposed(uint id, TaskCompletionSource<object?> tcs)
    {
        bool disposed = Volatile.Read(ref _disposed) != 0;
        if (!disposed && Volatile.Read(ref _dead) == 0)
        {
            return false;
        }

        if (_pending.TryRemove(id, out _))
        {
            tcs.TrySetException(disposed
                ? new ObjectDisposedException(nameof(NvimRpcClient))
                : (Exception)new IOException("Neovim RPC channel closed."));
        }

        return true;
    }

    public void Dispose()
    {
        // The flag goes first: the read loop and the send paths read it to
        // tell shutdown from a fault, and RequestAsync reads it after
        // registering its entry (see FailIfDisposed).
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        { _shutdown.Cancel(); }
        catch { }

        try
        { _channel.Dispose(); }
        catch { }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
            }
        }
        catch { }
        // Closing the job is the backstop that also fires when Kill did not run.
        try
        { _job?.Dispose(); }
        catch { }

        try
        { _process.Dispose(); }
        catch { }

        // Not left to the read loop's finally: that only runs if BeginRead
        // did, and a request registered after its sweep would otherwise
        // wait for a response from a closed pipe forever.
        FailAllPending(new ObjectDisposedException(nameof(NvimRpcClient)));

        try
        { _shutdown.Dispose(); }
        catch { }
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
