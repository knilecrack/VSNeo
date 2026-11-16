// The read loop, frame dispatch, and the pending-request sweep.

namespace VSNeo_Extension.Nvim;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;


internal sealed partial class NvimRpcClient
{
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
                    {
                        DispatchItem(buffered);
                    }

                    var result = await reader.ReadAsync(ct).ConfigureAwait(false);

                    if (result.IsEmpty)
                    {
                        // Dispose kills nvim, and the pipe closing is then the
                        // expected end of the loop, not a fault. Logging it as
                        // one made every routine Visual Studio shutdown look like
                        // nvim crashing mid-session.
                        if (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                        {
                            break;
                        }

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
            // Flag first, sweep after: the add-then-check in FailIfDisposed.
            Interlocked.Exchange(ref _dead, 1);
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
            // Counted like any other inbound frame: the rpc/5s traffic line
            // otherwise misses the most frequent notification on the wire.
            Interlocked.Increment(ref _received);
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
        {
            Dispatch(result.Frame);
        }
    }

    private void Dispatch(object[] frame)
    {
        if (frame == null || frame.Length < 3)
        {
            return;
        }

        Interlocked.Increment(ref _received);

        // The reader widens integers to long; Convert's interface dance is
        // the slow path, taken never in practice.
        long frameType = frame[0] is long ft ? ft : Convert.ToInt64(frame[0]);
        switch (frameType)
        {
            case 0: // a request from nvim: vim.rpcrequest(chan, method, ...)
                if (frame.Length < 4)
                {
                    break;
                }

                long requestId = frame[1] is long rid ? rid : Convert.ToInt64(frame[1]);
                var requestMethod = ToUtf8(frame[2]) ?? string.Empty;
                var requestArgs = frame[3] as object[] ?? Array.Empty<object>();
                // Not awaited: the read loop must keep reading. nvim waits for
                // the answer, so nothing else arrives meanwhile anyway.
                _ = AnswerRequestAsync(requestId, requestMethod, requestArgs);
                break;

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
        if (!_pending.TryRemove(id, out var tcs))
        {
            return;
        }

        if (error != null)
        {
            tcs.TrySetException(new NvimException(Describe(error)));
        }
        else
        {
            tcs.TrySetResult(result);
        }
    }

    private static string? ToUtf8(object o) =>
        o is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : o as string ?? o?.ToString();

    private static string? Describe(object error) =>
        error is object[] parts && parts.Length > 1 ? ToUtf8(parts[1]) : ToUtf8(error);

    private void FailAllPending(Exception ex)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(ex);
            }
        }
    }
}
