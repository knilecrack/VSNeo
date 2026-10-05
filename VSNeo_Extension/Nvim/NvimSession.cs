using System;
using System.Collections.Generic;
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
        // Null until StartAsync succeeds and again after Dispose; every consumer
        // re-reads the field and treats null as not-ready.
        private NvimRpcClient? _client;
        private Timer? _stats;
        private int _ready;

        // Dispose can land while StartAsync is still awaiting nvim's startup
        // requests (Visual Studio closing seconds after launch). StartAsync
        // checks this after its last await and retires the client instead of
        // publishing ready into a package that is already gone.
        private int _disposedFlag;

        // The transport faulted between the last startup request and the ready
        // publish. OnClientFaulted has nothing to announce then (_ready is
        // still 0), so without this the session would go ready on a dead pipe.
        private int _faultedBeforeReady;

        // A wedged nvim (blocked prompt, stuck plugin) can answer the pipe yet
        // never respond; the startup requests are bounded so they fault into the
        // circuit breaker instead of hanging package initialization forever.
        private static readonly TimeSpan StartupRequestTimeout = TimeSpan.FromSeconds(15);

        public NvimStateHub State { get; } = new NvimStateHub();

        /// <summary>
        /// nvim's buffer changed, from either side. Delivered by nvim_buf_attach, so
        /// it fires for edits nvim made on its own - which nothing on the Visual
        /// Studio side would otherwise ever hear about. An operator like x changes
        /// nvim's copy and leaves VS's untouched, and without this the two drift
        /// apart silently and stay that way.
        ///
        /// Carries the nvim buffer id (-1 when the event did not name one), so a
        /// mirror reacts to its own buffer only. It used to be a bare signal, and
        /// every edit anywhere woke every open document's mirror into a whole-file
        /// verify: twenty open tabs, twenty full-file hashes per editing pause.
        /// </summary>
        public event Action<long>? RemoteBufferChanged;

        /// <summary>
        /// A range of lines changed in nvim, with the payload nvim_buf_attach sends:
        /// [buffer, changedtick, firstline, lastline, replacement, more]. firstline
        /// and lastline bound the replaced range in the *old* buffer, and lastline of
        /// -1 means the whole buffer was replaced.
        /// </summary>
        public event Action<object[]>? BufferLinesChanged;

        /// <summary>
        /// nvim stopped sending updates for a buffer: [buffer]. It does this on its
        /// own whenever the buffer is unloaded or reloaded, and says nothing further.
        /// Without handling it the mirror goes quietly deaf - still believing it is
        /// attached, still repairing drift it can no longer see coming.
        /// </summary>
        public event Action<object[]>? BufferDetached;

        /// <summary>
        /// nvim asked for a Visual Studio command to be run, by name and arguments.
        /// This is what lets a Vim mapping reach Roslyn.
        /// </summary>
        public event Action<string, string>? ActionRequested;

        /// <summary>
        /// nvim asked for editor focus to move to an adjacent tab group, by direction
        /// ("left" / "down" / "up" / "right"). Visual Studio owns the splits, so only
        /// it can say which group lies that way.
        /// </summary>
        public event Action<string>? FocusRequested;

        /// <summary>
        /// nvim asked for the previous document in most-recently-used order. Sent
        /// for both the single toggle and the repeated-press walk; the navigator
        /// owns the traversal state.
        /// </summary>
        public event Action? MruRequested;

        /// <summary>
        /// nvim asked to start a labeled tab jump: Visual Studio rewrites the tab
        /// captions with labels, then reads the pick back through nvim.
        /// </summary>
        public event Action? TabJumpRequested;

        /// <summary>
        /// nvim read the label key of a labeled tab jump (empty string = canceled
        /// with Escape); Visual Studio activates that tab and restores captions.
        /// </summary>
        public event Action<string>? TabJumpPicked;

        /// <summary>
        /// nvim asked to open the embedded Seeky picker, by mode
        /// ("files" / "grep" / "symbols" / "git") and an optional initial query.
        /// </summary>
        public event Action<string, string>? SeekyRequested;

        /// <summary>
        /// A document's mirror gave up: nvim's edits are no longer being applied to
        /// it. Surfaced because degrading silently is how someone keeps typing into
        /// something that has quietly stopped working.
        /// </summary>
        public event Action<string>? MirrorStopped;

        internal void RaiseMirrorStopped(string filePath) => MirrorStopped?.Invoke(filePath);

        /// <summary>The buffer a nvim_buf_*_event names in its first argument, or -1.</summary>
        private static long BufferIdOf(object[] args)
        {
            if (args == null || args.Length == 0) return -1;
            if (args[0] is NvimHandle h) return h.Id;
            try { return args[0] == null ? -1 : Convert.ToInt64(args[0]); }
            catch { return -1; }
        }

        private void OnNotification(string method, object[] args)
        {
            switch (method)
            {
                // Per-subscriber isolation: one mirror per open document listens
                // here, and a plain multicast Invoke stops at the first throw -
                // every mirror subscribed after it would miss the event and drift
                // silently until Verify caught it.
                case "nvim_buf_lines_event":
                    Fanout.Invoke(BufferLinesChanged, args, "BufferLinesChanged");
                    Fanout.Invoke(RemoteBufferChanged, BufferIdOf(args), "RemoteBufferChanged");
                    break;
                case "nvim_buf_changedtick_event":
                    Fanout.Invoke(RemoteBufferChanged, BufferIdOf(args), "RemoteBufferChanged");
                    break;
                case "nvim_buf_detach_event":
                    Fanout.Invoke(BufferDetached, args, "BufferDetached");
                    break;
                case "vsneo_action":
                    if (args != null && args.Length > 0)
                        ActionRequested?.Invoke(
                            NvimStateHub.AsString(args[0]),
                            args.Length > 1 ? NvimStateHub.AsString(args[1]) : string.Empty);
                    break;
                case "vsneo_focus":
                    if (args != null && args.Length > 0)
                        FocusRequested?.Invoke(NvimStateHub.AsString(args[0]));
                    break;
                case "vsneo_mru":
                    MruRequested?.Invoke();
                    break;
                case "vsneo_tabs":
                    TabJumpRequested?.Invoke();
                    break;
                case "vsneo_tab_pick":
                    if (args != null && args.Length > 0)
                        TabJumpPicked?.Invoke(NvimStateHub.AsString(args[0]));
                    break;
                case "vsneo_seeky_config":
                    // [palette map|nil, prompt_normal 0/1]: see send_seeky_config in vsneo.lua.
                    if (args != null && args.Length > 1)
                    {
                        Dictionary<string, string>? palette = null;
                        if (args[0] is IDictionary<string, object?> map)
                        {
                            palette = new Dictionary<string, string>();
                            foreach (var entry in map)
                                if (entry.Value != null) palette[entry.Key] = NvimStateHub.AsString(entry.Value);
                        }
                        VSNeo.Seeky.SeekyPickerController.Configure(palette, NvimStateHub.AsString(args[1]) == "1");
                    }
                    break;
                case "vsneo_seeky":
                    if (args != null && args.Length > 0)
                        SeekyRequested?.Invoke(
                            NvimStateHub.AsString(args[0]),
                            args.Length > 1 ? NvimStateHub.AsString(args[1]) : string.Empty);
                    break;
            }
        }
        public bool IsReady => Volatile.Read(ref _ready) == 1 && _breaker.IsClosed;

        /// <summary>
        /// The wire position of the notification being handled (see
        /// <see cref="NvimRpcClient.NotificationSeq"/>); 0 with no connection.
        /// </summary>
        public long NotificationSeq => _client?.NotificationSeq ?? 0;
        public event Action<bool>? ReadyChanged;

        /// <summary>
        /// The transport is gone: the read loop died or nvim exited. There is no
        /// reconnect path in this session, so the honest state is not-ready -
        /// otherwise IsReady stays true and the key processor keeps swallowing
        /// keystrokes into a channel nothing is reading, which is the "extension
        /// stopped working and VS feels broken" report.
        /// </summary>
        private void OnClientFaulted(Exception ex)
        {
            Log.Write("nvim transport faulted - falling back to plain Visual Studio input", ex);
            _breaker.Trip(ex);
            if (Interlocked.Exchange(ref _ready, 0) == 1)
                ReadyChanged?.Invoke(false);
            else
                Volatile.Write(ref _faultedBeforeReady, 1);
        }

        public NvimSession(CircuitBreaker breaker)
        {
            _breaker = breaker;
            _breaker.StateChanged += _ => ReadyChanged?.Invoke(IsReady);
        }


        public async Task StartAsync(string nvimPath, CancellationToken ct)
        {
            await TaskScheduler.Default; // never start this on the UI thread

            NvimRpcClient? client = null;
            Volatile.Write(ref _faultedBeforeReady, 0);
            try
            {
                Log.Write("starting nvim: " + nvimPath);
                client = await NvimRpcClient.ConnectAsync(nvimPath, ct).ConfigureAwait(false);
                Log.Write("pipe connected");

                // Subscribe before the read loop starts, or the first redraw - the
                // one carrying the initial mode - can land before anyone is listening.
                client.NotificationReceived += State.OnNotification;
                client.StatePushReceived += State.OnStatePush;
                client.NotificationReceived += OnNotification;
                client.Faulted += OnClientFaulted;
                client.BeginRead();

                // ext_linegrid is required for the modern redraw protocol.
                // ext_cmdline and ext_messages hand us the ":" prompt and Vim's
                // own messages so we never have to reimplement either.
                // ext_popupmenu reports wildmenu (cmdline Tab-completion) as
                // events instead of drawing it, so the popup can render it.
                var options = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["ext_linegrid"] = true,
                    ["ext_cmdline"] = true,
                    ["ext_messages"] = true,
                    ["ext_popupmenu"] = true,
                    ["rgb"] = true,
                };

                await client.RequestAsync("nvim_ui_attach", StartupRequestTimeout, 200, 60, options).ConfigureAwait(false);
                await client.RequestAsync("nvim_set_var", StartupRequestTimeout, "vsneo", 1).ConfigureAwait(false);
                // nvim_get_api_info returns [channel_id, metadata]: the id is how the
                // companion addresses its notifications back to this connection.
                var apiInfo = await client.RequestAsync("nvim_get_api_info", StartupRequestTimeout).ConfigureAwait(false) as object[];
                if (apiInfo == null || apiInfo.Length < 1)
                    throw new InvalidOperationException("nvim_get_api_info returned nothing usable");

                long channel = Convert.ToInt64(apiInfo[0]);
                await client.RequestAsync("nvim_exec_lua", StartupRequestTimeout, NvimLua.Script, new object[] { channel })
                            .ConfigureAwait(false);
                Log.Write("state companion installed on channel " + channel);
            }
            catch (Exception ex)
            {
                Log.Write("nvim start FAILED", ex);

                // A failure after the pipe connected (ui_attach timing out, the
                // companion refusing to load) used to leave the client
                // unowned: nvim, its pipe, the read loop and the job handle all
                // lived until devenv exited, and the handlers subscribed above
                // kept feeding a dead session's notifications into the hub.
                // Only the client that never became _client is ours to close.
                if (client != null && !ReferenceEquals(client, _client))
                {
                    try { client.Dispose(); } catch (Exception dex) { Log.Write("disposing failed nvim client", dex); }
                }

                _breaker.Trip(ex);

                // Trip only opens the breaker on the third failure, so a single
                // startup failure is otherwise completely silent: no ready event,
                // no status bar text, and every key quietly passing through to VS
                // with nothing anywhere to say why. Announce it directly.
                ReadyChanged?.Invoke(false);
                return;
            }

            // Outside the try. A ReadyChanged subscriber that threw used to land
            // in the catch above, which announced a failed start over a session
            // that was live (_ready set, _client assigned): the badge said
            // fallback while keys kept going to nvim, and every subscriber after
            // the throwing one never heard that the session was ready.
            var started = client!;
            if (Volatile.Read(ref _disposedFlag) != 0 || Volatile.Read(ref _faultedBeforeReady) != 0)
            {
                Log.Write(Volatile.Read(ref _disposedFlag) != 0
                    ? "nvim started after the session was disposed - retiring it"
                    : "nvim transport faulted during startup - not going ready");
                try { started.Dispose(); } catch (Exception dex) { Log.Write("disposing the client", dex); }
                ReadyChanged?.Invoke(false);
                return;
            }

            _client = started;
            Interlocked.Exchange(ref _ready, 1);

            // The check above is not atomic with the publication: Dispose or a
            // transport fault can land between the two. Publish first, then look
            // again (the Interlocked calls are full fences): whichever side wrote
            // last sees the other's write, so a disposed or faulted client is
            // never left standing as ready.
            if (Interlocked.CompareExchange(ref _disposedFlag, 0, 0) != 0
                || Interlocked.CompareExchange(ref _faultedBeforeReady, 0, 0) != 0)
            {
                Log.Write("nvim session was disposed or faulted while going ready - retiring it");
                Interlocked.Exchange(ref _ready, 0);
                _client = null;
                try { started.Dispose(); } catch (Exception dex) { Log.Write("disposing the client", dex); }
                ReadyChanged?.Invoke(false);
                return;
            }

            _breaker.Reset();
            Log.Write("nvim connected and ui_attach succeeded");
            StartTrafficStats(started);
            try
            {
                ReadyChanged?.Invoke(true);
            }
            catch (Exception ex)
            {
                Log.Write("a ReadyChanged subscriber threw; the session is ready regardless", ex);
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
            if (Volatile.Read(ref _holdGeneration) != 0)
            {
                if (TryHold(keys)) return;
                // Past the cap or the deadline: the hold is over. The keys held
                // so far go first, in order, then this one - sending this one
                // alone would put it ahead of everything typed before it.
                FlushHold(client);
            }
            Infrastructure.Perf.KeySent();
            client.NotifyInput(keys);
        }

        /// <summary>Ends the hold early and sends what it held, in order.</summary>
        private void FlushHold(NvimRpcClient client)
        {
            string[] held;
            lock (_held)
            {
                Volatile.Write(ref _holdGeneration, 0);
                held = _held.ToArray();
                _held.Clear();
            }
            if (held.Length > 0)
                Infrastructure.Log.Write("input hold overflowed; sending " + held.Length + " held keys now");
            foreach (var k in held) client.NotifyInput(k);
        }

        // A document switch in flight. From the moment a view takes focus until
        // nvim_win_set_buf lands, nvim's window still shows the *previous*
        // document, and a quick dd or x typed into the new one used to edit that
        // background tab through its mirror. Keys are held here meanwhile and
        // replayed, in order, once the switch (and the caret push after it) is
        // done. Bounded: past the cap or the deadline the keys go straight
        // through again, which is what always happened before.
        private int _holdGeneration;
        private int _holdStartedTicks;
        private readonly System.Collections.Generic.List<string> _held = new System.Collections.Generic.List<string>();
        private const int MaxHeldKeys = 32;
        private const int MaxHoldMs = 500;

        /// <summary>Starts holding keys. Returns the generation to end it with.</summary>
        public int BeginInputHold()
        {
            lock (_held)
            {
                int generation = Interlocked.Increment(ref _holdGenerationSeq);
                Volatile.Write(ref _holdStartedTicks, Environment.TickCount);
                Volatile.Write(ref _holdGeneration, generation);
                return generation;
            }
        }

        private int _holdGenerationSeq;

        /// <summary>
        /// Ends the hold started with <paramref name="generation"/>. A newer hold
        /// (focus moved on again) supersedes it: the keys stay held and replay
        /// when that one ends, into the document the user is now looking at.
        /// </summary>
        public void EndInputHold(int generation, bool replay)
        {
            string[] held;
            lock (_held)
            {
                if (Volatile.Read(ref _holdGeneration) != generation) return;
                Volatile.Write(ref _holdGeneration, 0);
                held = _held.ToArray();
                _held.Clear();
            }

            if (held.Length == 0) return;
            if (!replay)
            {
                Infrastructure.Log.Write("dropping " + held.Length + " held keys: the document switch failed");
                return;
            }

            var client = _client;
            if (client == null || !IsReady) return;
            foreach (var keys in held) client.NotifyInput(keys);
        }

        private bool TryHold(string keys)
        {
            lock (_held)
            {
                if (Volatile.Read(ref _holdGeneration) == 0) return false;
                if (_held.Count >= MaxHeldKeys
                    || unchecked(Environment.TickCount - Volatile.Read(ref _holdStartedTicks)) > MaxHoldMs)
                    return false;
                _held.Add(keys);
                return true;
            }
        }

        public Task<object?> RequestAsync(string method, params object[] args)
        {
            var client = _client;
            if (client == null || !IsReady) return Task.FromResult<object?>(null);
            return client.RequestAsync(method, args);
        }

        /// <summary>nvim_exec_lua with the Lua arguments streamed into the frame;
        /// see <see cref="NvimRpcClient.ExecLuaAsync"/> for why this is its own
        /// name and not a RequestAsync overload.</summary>
        public Task<object?> ExecLuaAsync(string chunk, Action<MsgPackWriter> writeLuaArgs)
        {
            var client = _client;
            if (client == null || !IsReady) return Task.FromResult<object?>(null);
            return client.ExecLuaAsync(chunk, writeLuaArgs);
        }

        public void Dispose()
        {
            Volatile.Write(ref _disposedFlag, 1);
            Volatile.Write(ref _ready, 0);
            _stats?.Dispose();
            _stats = null;
            _client?.Dispose();
            _client = null;
        }
    }
}
