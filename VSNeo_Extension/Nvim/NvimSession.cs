using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading; // TaskScheduler.GetAwaiter, for "await TaskScheduler.Default"
using VSNeo_Extension.Infrastructure;

namespace VSNeo_Extension.Nvim
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
        private Timer _stats;
        private int _ready;

        public NvimStateHub State { get; } = new NvimStateHub();

        /// <summary>
        /// nvim's buffer changed, from either side. Delivered by nvim_buf_attach, so
        /// it fires for edits nvim made on its own - which nothing on the Visual
        /// Studio side would otherwise ever hear about. An operator like x changes
        /// nvim's copy and leaves VS's untouched, and without this the two drift
        /// apart silently and stay that way.
        /// </summary>
        public event Action RemoteBufferChanged;

        private void OnNotification(string method, object[] args)
        {
            if (method == "nvim_buf_lines_event" || method == "nvim_buf_changedtick_event")
                RemoteBufferChanged?.Invoke();
        }
        public bool IsReady => Volatile.Read(ref _ready) == 1 && _breaker.IsClosed;
        public event Action<bool> ReadyChanged;

        public NvimSession(CircuitBreaker breaker)
        {
            _breaker = breaker;
            _breaker.StateChanged += _ => ReadyChanged?.Invoke(IsReady);
        }

        /// <summary>
        /// Runs once per session, in nvim rather than over RPC because both parts
        /// have to be in place before the first document is opened.
        ///
        /// filetype detection is what makes a buffer more than an array of lines:
        /// it is the difference between nvim knowing this is C# and not, and every
        /// plugin in milestone 5 branches on it. -u NORC skips the user's config, so
        /// nothing else would have switched it on.
        ///
        /// BufWriteCmd exists because naming a buffer after a real path means nvim
        /// believes it owns that file. Visual Studio owns saving, and two writers
        /// racing over one path on disk is a corrupted file, not a merge conflict.
        /// Claiming the write and clearing 'modified' makes :w a harmless no-op
        /// rather than an error or a clobber.
        /// </summary>
        private const string Bootstrap = @"
            vim.cmd('filetype plugin indent on')
            vim.api.nvim_create_autocmd('BufWriteCmd', {
              pattern = '*',
              callback = function(ev)
                vim.bo[ev.buf].modified = false
              end,
            })
        ";

        public async Task StartAsync(string nvimPath, CancellationToken ct)
        {
            await TaskScheduler.Default; // never start this on the UI thread

            try
            {
                Log.Write("starting nvim: " + nvimPath);
                var client = await NvimRpcClient.ConnectAsync(nvimPath, ct).ConfigureAwait(false);
                Log.Write("pipe connected");

                // Subscribe before the read loop starts, or the first redraw - the
                // one carrying the initial mode - can land before anyone is listening.
                client.NotificationReceived += State.OnNotification;
                client.NotificationReceived += OnNotification;
                client.Faulted += ex => _breaker.Trip(ex);
                client.BeginRead();

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
                await client.RequestAsync("nvim_exec_lua", Bootstrap, new object[0]).ConfigureAwait(false);

                _client = client;
                Volatile.Write(ref _ready, 1);
                _breaker.Reset();
                Log.Write("nvim connected and ui_attach succeeded");
                StartTrafficStats(client);
                ReadyChanged?.Invoke(true);
            }
            catch (Exception ex)
            {
                Log.Write("nvim start FAILED", ex);
                _breaker.Trip(ex);

                // Trip only opens the breaker on the third failure, so a single
                // startup failure is otherwise completely silent: no ready event,
                // no status bar text, and every key quietly passing through to VS
                // with nothing anywhere to say why. Announce it directly.
                ReadyChanged?.Invoke(false);
            }
        }

        /// <summary>
        /// Samples RPC volume every five seconds and logs it only when there is
        /// something to see. Idle traffic should be zero: nothing in this design
        /// polls, so anything ticking over while the editor sits untouched is a
        /// feedback loop, and the rate says which side is driving it.
        /// </summary>
        private void StartTrafficStats(NvimRpcClient client)
        {
            long lastSent = 0, lastReceived = 0;

            _stats = new Timer(_ =>
            {
                long sent = client.Sent, received = client.Received;
                long dSent = sent - lastSent, dReceived = received - lastReceived;
                lastSent = sent;
                lastReceived = received;

                if (dSent == 0 && dReceived == 0) return;

                Log.Write(string.Format(
                    "rpc/5s: sent {0}, received {1}   (totals {2}/{3})",
                    dSent, dReceived, sent, received));
            }, null, 5000, 5000);
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
            _stats?.Dispose();
            _stats = null;
            _client?.Dispose();
            _client = null;
        }
    }
}
