using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

namespace VSNeo.Nvim
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
        private readonly Stream _stdin;
        private readonly Stream _stdout;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<object>> _pending
            = new ConcurrentDictionary<uint, TaskCompletionSource<object>>();
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private int _msgId;
        private int _disposed;

        /// <summary>Raised on a background thread for every notification nvim sends.</summary>
        public event Action<string, object[]> NotificationReceived;

        /// <summary>Raised on a background thread when the transport dies for any reason.</summary>
        public event Action<Exception> Faulted;

        private NvimRpcClient(Process process)
        {
            _process = process;
            _stdin = process.StandardInput.BaseStream;
            _stdout = process.StandardOutput.BaseStream;
        }

        public static NvimRpcClient Start(string nvimPath, params string[] extraArgs)
        {
            // --embed puts nvim in RPC mode over stdio and makes it wait for a UI
            // to attach. -u NORC keeps the user's init.lua out of the startup path
            // for now: a slow plugin manager there is a hang we would inherit.
            var args = new List<string> { "--embed", "-u", "NORC" };
            if (extraArgs != null) args.AddRange(extraArgs);

            var psi = new ProcessStartInfo
            {
                FileName = nvimPath,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();

            var client = new NvimRpcClient(process);
            Task.Run(() => client.ReadLoopAsync(client._shutdown.Token));
            return client;
        }

        public Task<object> RequestAsync(string method, params object[] args)
        {
            var id = unchecked((uint)Interlocked.Increment(ref _msgId));
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                t => Faulted?.Invoke(t.Exception.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private async Task SendAsync(object[] frame)
        {
            var bytes = MessagePackSerializer.Typeless.Serialize(frame);
            await _writeLock.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                await _stdin.WriteAsync(bytes, 0, bytes.Length, _shutdown.Token).ConfigureAwait(false);
                await _stdin.FlushAsync(_shutdown.Token).ConfigureAwait(false);
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
                // MessagePackStreamReader hands back one complete message at a time,
                // which is what we need since nvim's stream carries no length prefix.
                using (var reader = new MessagePackStreamReader(_stdout, leaveOpen: true))
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var chunk = await reader.ReadAsync(ct).ConfigureAwait(false);
                        if (chunk == null) break; // nvim exited
                        Dispatch(MessagePackSerializer.Typeless.Deserialize(chunk.Value) as object[]);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
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

            switch (Convert.ToInt32(frame[0]))
            {
                case 1: // response
                    var id = Convert.ToUInt32(frame[1]);
                    if (!_pending.TryRemove(id, out var tcs)) return;
                    if (frame[2] != null) tcs.TrySetException(new NvimException(Describe(frame[2])));
                    else tcs.TrySetResult(frame.Length > 3 ? frame[3] : null);
                    break;

                case 2: // notification
                    var method = ToUtf8(frame[1]);
                    var args = frame[2] as object[] ?? Array.Empty<object>();
                    NotificationReceived?.Invoke(method, args);
                    break;
            }
        }

        private static string ToUtf8(object o) =>
            o is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : o as string ?? o?.ToString();

        private static string Describe(object error) =>
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
            try { if (!_process.HasExited) _process.Kill(); } catch { }
            try { _process.Dispose(); } catch { }
            _shutdown.Dispose();
            _writeLock.Dispose();
        }
    }

    internal sealed class NvimException : Exception
    {
        public NvimException(string message) : base(message) { }
    }
}
