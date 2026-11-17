// Everything that writes to the channel: frames, the writer pool, the write lock.
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VSNeo_Extension.Nvim;

internal sealed partial class NvimRpcClient
{
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
            {
                p.TrySetException(ex.GetBaseException());
            }
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
            {
                p.TrySetException(ex.GetBaseException());
            }
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
            {
                Faulted?.Invoke(ex.GetBaseException());
            }
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
            {
                Faulted?.Invoke(ex.GetBaseException());
            }
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
}
