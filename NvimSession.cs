using System;
using System.Threading;
using System.Threading.Tasks;
using VSNeo.Infrastructure;

namespace VSNeo.Nvim
{
    /// <summary>
    /// Owns the lifetime of one embedded nvim per Visual Studio instance and
    /// exposes it to the rest of the extension.
    ///
    /// Attachment and activation are deliberately separate. Everything in here
    /// runs off the UI thread; until <see cref="IsReady"/> flips true the key
    /// processor stays in pass-through and Visual Studio behaves normally.
    /// </summary>
    internal sealed class NvimSession : IDisposable
    {
        private readonly CircuitBreaker _breaker;
        private NvimRpcClient _client;
        private int _ready;

        public NvimStateHub State { get; } = new NvimStateHub();
        public bool IsReady => Volatile.Read(ref _ready) == 1 && _breaker.IsClosed;
        public event Action<bool> ReadyChanged;

        public NvimSession(CircuitBreaker breaker)
        {
            _breaker = breaker;
            _breaker.StateChanged += _ => ReadyChanged?.Invoke(IsReady);
        }

        public async Task StartAsync(string nvimPath, CancellationToken ct)
        {
            await TaskScheduler.Default; // never start this on the UI thread

            try
            {
                var client = NvimRpcClient.Start(nvimPath);
                client.NotificationReceived += State.OnNotification;
                client.Faulted += ex => _breaker.Trip(ex);

                // ext_linegrid is required for the modern redraw protocol.
                // ext_cmdline and ext_messages hand us the ":" prompt and Vim's
                // own messages so we never have to reimplement either.
                var options = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["ext_linegrid"] = true,
                    ["ext_cmdline"] = true,
                    ["ext_messages"] = true,
                    ["rgb"] = true,
                };

                await client.RequestAsync("nvim_ui_attach", 200, 60, options).ConfigureAwait(false);
                await client.RequestAsync("nvim_set_var", "vsneo", 1).ConfigureAwait(false);

                _client = client;
                Volatile.Write(ref _ready, 1);
                _breaker.Reset();
                ReadyChanged?.Invoke(true);
            }
            catch (Exception ex)
            {
                _breaker.Trip(ex);
            }
        }

        /// <summary>Forward keys. Fire and forget by design: the decision was already made locally.</summary>
        public void Input(string keys)
        {
            var client = _client;
            if (client == null || !IsReady) return;
            client.Notify("nvim_input", keys);
        }

        public Task<object> RequestAsync(string method, params object[] args)
        {
            var client = _client;
            if (client == null || !IsReady) return Task.FromResult<object>(null);
            return client.RequestAsync(method, args);
        }

        public void Dispose()
        {
            Volatile.Write(ref _ready, 0);
            _client?.Dispose();
            _client = null;
        }
    }
}
