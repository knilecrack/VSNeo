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

            -- Visual Studio decides what wraps. If nvim wrapped as well its screen
            -- lines would stop matching VS's, and H, M, L and the <C-d> family are
            -- all defined in screen lines - they would drift by however many lines
            -- nvim thought had wrapped.
            vim.o.wrap = false

            -- Nothing renders nvim's own scroll padding, and a non-zero value here
            -- makes nvim scroll the window when VS would not have, desynchronising
            -- the topline the viewport synchroniser just set.
            vim.o.scrolloff = 0
            vim.o.sidescrolloff = 0

            -- Nothing draws a status line either, and every row it occupies is a row
            -- the text window does not have. Measured: with ext_cmdline on, a grid of
            -- 30 gives a 29-line window by default and a 30-line window with this
            -- off. Zero chrome means the viewport synchroniser can pass Visual
            -- Studio's visible line count straight through, and <C-d> then scrolls by
            -- what you can actually see.
            vim.o.laststatus = 0
            vim.api.nvim_create_autocmd('BufWriteCmd', {
              pattern = '*',
              callback = function(ev)
                vim.bo[ev.buf].modified = false
              end,
            })
        ";

        /// <summary>
        /// The companion. nvim reports its own mode and cursor over rpcnotify rather
        /// than us reading them out of the redraw stream.
        ///
        /// The redraw stream is a *rendering* feed. It was never meant to answer
        /// "where is the cursor" - that arrived as a side effect of win_viewport,
        /// batched until the next flush, and only because no other window was
        /// current. Mode came from mode_change, whose names describe how to draw the
        /// cursor rather than what mode Vim is in. Both worked, and both were
        /// inference over a stream that is free to change how it paints.
        ///
        /// CursorMoved, CursorMovedI and ModeChanged are the events Vim actually
        /// documents for this, they fire when the thing happens rather than when the
        /// screen is repainted, and they carry the real values: nvim_get_mode().mode
        /// and nvim_win_get_cursor(). BufEnter is included so switching documents
        /// reports a position immediately instead of after the first motion.
        ///
        /// The channel id has to be passed in - the companion has to know who to
        /// notify, and there is exactly one of us.
        /// </summary>
        private const string Companion = @"
            local chan = ...
            local group = vim.api.nvim_create_augroup('VSNeo', { clear = true })

            local function push()
              local ok, pos = pcall(vim.api.nvim_win_get_cursor, 0)
              if not ok then return end
              -- row is 1-based from nvim and 0-based everywhere in the extension;
              -- col is already a 0-based byte offset, which is what ColumnMapper wants.
              vim.rpcnotify(chan, 'vsneo_state', vim.api.nvim_get_mode().mode, pos[1] - 1, pos[2])
            end

            vim.api.nvim_create_autocmd({ 'CursorMoved', 'CursorMovedI', 'BufEnter' }, {
              group = group,
              callback = push,
            })

            -- ModeChanged matches against 'old:new', so it needs its own pattern.
            vim.api.nvim_create_autocmd('ModeChanged', {
              group = group,
              pattern = '*:*',
              callback = push,
            })

            push()
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

                // nvim_get_api_info returns [channel_id, metadata]: the id is how the
                // companion addresses its notifications back to this connection.
                var apiInfo = await client.RequestAsync("nvim_get_api_info").ConfigureAwait(false) as object[];
                if (apiInfo == null || apiInfo.Length < 1)
                    throw new InvalidOperationException("nvim_get_api_info returned nothing usable");

                long channel = Convert.ToInt64(apiInfo[0]);
                await client.RequestAsync("nvim_exec_lua", Companion, new object[] { channel })
                            .ConfigureAwait(false);
                Log.Write("state companion installed on channel " + channel);

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
