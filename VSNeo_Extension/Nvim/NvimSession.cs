using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading; // TaskScheduler.GetAwaiter, for "await TaskScheduler.Default"
using VSNeo_Extension.Infrastructure;

namespace VSNeo_Extension.Nvim;

/// <summary>
/// Owns the lifetime of one embedded nvim per Visual Studio instance and
/// exposes it to the rest of the extension.
///
/// Attachment and activation are deliberately separate. Everything in here
/// runs off the UI thread; until <see cref="IsReady"/> flips true the key
/// processor stays in pass-through and Visual Studio behaves normally.
/// </summary>
internal sealed partial class NvimSession : IDisposable
{
    private readonly CircuitBreaker _breaker;

    private readonly Action<bool> _breakerChanged;

    // Null until StartAsync succeeds and again after Dispose; every consumer
    // re-reads the field and treats null as not-ready.
    private NvimRpcClient? _client;

    private Timer? _stats;

    private int _ready;

    /// <summary>The executable this session was started with.</summary>
    public string? NvimPath { get; private set; }

    /// <summary>"major.minor.patch" from nvim_get_api_info, null before it or if unreadable.</summary>
    public string? NvimVersion { get; private set; }

    /// <summary>
    /// One-line reason the last StartAsync failed, for the status bar and
    /// Tools &gt; VSNeo: Diagnostics. Null after a successful start.
    /// </summary>
    public string? StartupFailure { get; private set; }

    /// <summary>The breaker's last transport/startup fault, surfaced by diagnostics.</summary>
    public Exception? LastFault => _breaker.LastFault;

    /// <summary>
    /// The companion uses vim.on_key's typed argument (macros), extmarks and
    /// vim.fs; 0.10 is the first release that carries all of them.
    /// </summary>
    private static readonly Version MinNvimVersion = new(0, 10);

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

    /// <summary>nvim asked to toggle VSNeo, or explicitly enable/disable it.</summary>
    public event Action<string>? ToggleRequested;

    /// <summary>Raised on the RPC thread for vsneo_select_action: a command whose new selection goes to nvim as visual mode.</summary>
    public event Action<string>? SelectActionRequested;

    /// <summary>An operator's range for a Visual Studio command (vsneo.operator): rows 1-based, byte columns 0-based, end inclusive.</summary>
    public readonly struct RangeAction
    {
        public RangeAction(string command, string kind, int startRow, int startByte, int endRow, int endByte)
        {
            Command = command;
            Kind = kind;
            StartRow = startRow;
            StartByte = startByte;
            EndRow = endRow;
            EndByte = endByte;
        }
        public string Command { get; }
        /// <summary>"line", "char" or "block".</summary>
        public string Kind { get; }
        public int StartRow { get; }
        public int StartByte { get; }
        public int EndRow { get; }
        public int EndByte { get; }
    }

    /// <summary>Raised on the RPC thread for vsneo_range_action.</summary>
    public event Action<RangeAction>? RangeActionRequested;

    /// <summary>Answers nvim's rpcrequests (vsneo_syntax). Set before StartAsync.</summary>
    public Func<string, object[], System.Threading.Tasks.Task<object?>>? RequestHandler { get; set; }

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

    public bool IsReady => Volatile.Read(ref _ready) == 1 && _breaker.IsClosed;

    /// <summary>
    /// The wire position of the notification being handled (see
    /// <see cref="NvimRpcClient.NotificationSeq"/>); 0 with no connection.
    /// </summary>
    public long NotificationSeq => _client?.NotificationSeq ?? 0;

    public event Action<bool>? ReadyChanged;

    private void NotifyReady(bool ready)
    {
        var handlers = ReadyChanged;
        if (handlers == null)
        {
            return;
        }

        foreach (Action<bool> handler in handlers.GetInvocationList())
        {
            try
            { handler(ready); }
            catch (Exception ex)
            {
                Log.Write("a ReadyChanged subscriber threw; the session state stands", ex);
            }
        }
    }

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

        // Flag first, unconditionally: startup's re-check after it publishes
        // ready looks at this flag, so a fault that cleared _ready while it
        // was still 0 and paused before setting the flag could be missed,
        // leaving a dead transport ready.
        Interlocked.Exchange(ref _faultedBeforeReady, 1);
        if (Interlocked.Exchange(ref _ready, 0) == 1)
        {
            NotifyReady(false);
        }
    }

    public NvimSession(CircuitBreaker breaker)
    {
        _breaker = breaker;
        _breakerChanged = _ => NotifyReady(IsReady);
        _breaker.StateChanged += _breakerChanged;
    }

    /// <summary>Tail of nvim's stderr, for startup failures and diagnostics.</summary>
    public string StdErrTail => _client?.StdErrTail ?? string.Empty;

    public Task<object?> RequestAsync(string method, params object[] args)
    {
        var client = _client;
        if (client == null || !IsReady)
        {
            return Task.FromResult<object?>(null);
        }

        return client.RequestAsync(method, args);
    }

    /// <summary>nvim_exec_lua with the Lua arguments streamed into the frame;
    /// see <see cref="NvimRpcClient.ExecLuaAsync"/> for why this is its own
    /// name and not a RequestAsync overload.</summary>
    public Task<object?> ExecLuaAsync(string chunk, Action<MsgPackWriter> writeLuaArgs)
    {
        var client = _client;
        if (client == null || !IsReady)
        {
            return Task.FromResult<object?>(null);
        }

        return client.ExecLuaAsync(chunk, writeLuaArgs);
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposedFlag, 1);
        Volatile.Write(ref _ready, 0);
        _breaker.StateChanged -= _breakerChanged;
        _stats?.Dispose();
        _stats = null;
        _client?.Dispose();
        _client = null;
    }
}
