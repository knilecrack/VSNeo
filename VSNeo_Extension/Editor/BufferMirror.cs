using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

/// <summary>
/// Two-way mirror between a Visual Studio buffer and an nvim one.
///
/// Edits go over as spans, not whole files, in both directions: VS reports
/// exactly what changed in <c>e.Changes</c>, and nvim_buf_attach reports the
/// same from the other side, so a keystroke moves one short span rather than a
/// document. Whole-buffer traffic survives only in the initial prime and in the
/// drift repair.
///
/// Telling our own work from nvim's is the whole difficulty, and it is done by
/// changedtick. Counting outstanding writes was tried and is not enough: a write
/// that changes nothing, or that nvim rejects, produces no event, so a tally
/// keyed on arrivals never comes back down and silently swallows the next real
/// edit - which then reappears when the drift check resends VS's copy over it.
/// </summary>
internal sealed partial class BufferMirror : IDisposable
{
    private readonly ITextBuffer _buffer;

    private readonly NvimSession _session;

    private readonly string? _filePath;

    // Written on the UI thread (Dispose), read on the RPC reader and thread
    // pool: a stale read lets a retired mirror prime or apply once more.
    private volatile bool _disposed;

    /// <summary>Retired: the last document view over its buffer closed, or the
    /// document came back with a new ITextBuffer. Nothing may write through it.</summary>
    internal bool IsDisposed => _disposed;

    private readonly Timer _verify;

    private long _handle = -1;

    /// <summary>Flipped only after the first prime completes; events before that are ours.</summary>
    private int _hasPrimed;

    private readonly CursorSynchronizer _cursorSync;

    private readonly ITextUndoHistoryRegistry _undoRegistry;

    /// <summary>Private on purpose: go through <see cref="ForDocument"/>, which
    /// guarantees one writer per nvim buffer.</summary>
    private BufferMirror(ITextBuffer buffer, NvimSession session, string? filePath,
                        CursorSynchronizer cursorSync,
                        ITextUndoHistoryRegistry undoRegistry)
    {
        _buffer = buffer;
        _session = session;
        _filePath = filePath;
        _cursorSync = cursorSync;
        _undoRegistry = undoRegistry;
        _verify = new Timer(_ => Verify(), null, Timeout.Infinite, Timeout.Infinite);
        _buffer.Changed += OnBufferChanged;
        _session.RemoteBufferChanged += OnRemoteBufferChanged;
        _session.BufferLinesChanged += OnRemoteLines;
        _session.BufferDetached += OnRemoteDetached;
        _session.State.ModeChanged += OnModeChangedForUndo;
        WatchDirtyState();
        _buffer.Properties[typeof(BufferMirror)] = this;
    }

    // ---- insert-session undo grouping ---------------------------------
    //
    // Vim undoes a whole change in one step: c3wXYZ<Esc> then u restores
    // the three words. Here the change's deletion arrives as a remote edit
    // (one undo step) and the typed text is Visual Studio's own typing
    // (its own steps), so u took two presses - and a long insert several.
    //
    // The session is bracketed by buffer states, not by a transaction held
    // open (see InsertUndoGroup for why): begun when the mode becomes
    // insert or replace, or earlier by the drain when a change command's
    // deletion lands with the mode already reading insert (the lines event
    // travels ahead of the mode push), and ended on the way out. The key
    // processor's u and <C-r> walk the recorded range. Only the buffer
    // nvim's window shows takes part; every mirror hears ModeChanged.
    internal InsertUndoGroup InsertUndo { get; } = new InsertUndoGroup();   // UI thread only

    private int _pendingInsertUndoStart = -1;   // UI thread only

    private void OnModeChangedForUndo(VimMode mode)
    {
        bool insert = mode == VimMode.Insert || mode == VimMode.Replace;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || _disposed)
        {
            return;
        }

        // Every live mirror hears every mode change; only the shown buffer,
        // or one still holding session state to close or forget, has work
        // here. Without this each open document posted a Send-priority item
        // per i / <Esc>. Racy reads off the UI thread, on purpose: the
        // callback re-checks, and a stale "nothing to do" costs at most the
        // grouping of one insert session.
        if (!ReferenceEquals(TextViewCreationListener.ShownBuffer, _buffer)
            && !InsertUndo.IsOpen && _pendingInsertUndoStart < 0)
        {
            return;
        }
#pragma warning disable VSTHRD001
        _ = dispatcher.BeginInvoke(UiPriority.KeyResponse, new Action(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (insert)
            {
                BeginInsertUndoGroup();
            }
            else
            {
                EndInsertUndoGroup(mode);
            }
        }));
#pragma warning restore VSTHRD001
    }

    /// <summary>The buffer's current state as InsertUndoGroup names it.</summary>
    internal int UndoState => _buffer.CurrentSnapshot.Version.ReiteratedVersionNumber;

    /// <summary>UI thread. Marks the insert session's start if this is the shown buffer and none is open.</summary>
    internal void BeginInsertUndoGroup(int? startState = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        int? pendingStart = startState ?? (_pendingInsertUndoStart >= 0 ? _pendingInsertUndoStart : (int?)null);
        _pendingInsertUndoStart = -1;
        if (_disposed || InsertUndo.IsOpen)
        {
            return;
        }

        if (!ReferenceEquals(TextViewCreationListener.ShownBuffer, _buffer))
        {
            return;
        }

        InsertUndo.Begin(pendingStart ?? UndoState);
    }

    /// <summary>
    /// UI thread. Marks the insert session's end: on leaving insert, before
    /// a normal-mode u or &lt;C-r&gt; (the mode push that would end it may
    /// still be in flight), and on dispose. Nothing to roll back or
    /// complete - only two numbers are recorded.
    /// </summary>
    internal void EndInsertUndoGroup(VimMode mode = VimMode.Normal)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (mode != VimMode.OperatorPending)
        {
            _pendingInsertUndoStart = -1;
        }

        if (!InsertUndo.IsOpen)
        {
            return;
        }

        InsertUndo.End(UndoState);
    }

    /// <summary>
    /// UI thread, zero I/O. A key sent to nvim in normal or visual mode: the
    /// pending start belongs to the change command whose deletion drained
    /// it, so it is valid only until the next key. Without this, x then
    /// iabc&lt;Esc&gt; then u undid the x along with the typing.
    /// </summary>
    internal void ForgetPendingInsertUndoStart() => _pendingInsertUndoStart = -1;

    /// <summary>
    /// The mirror for this buffer, or null when none has attached yet. Never
    /// creates one: this runs on the key path, where only reads are allowed.
    /// </summary>
    public static BufferMirror? TryGetForBuffer(ITextBuffer buffer) =>
        buffer.Properties.TryGetProperty(typeof(BufferMirror), out BufferMirror mirror)
            ? mirror
            : null;

    /// <summary>
    /// Remote edits received but not yet applied to Visual Studio. While this
    /// is true the local buffer is behind nvim's, and text typed into it can
    /// land inside a span a queued deletion is about to replace - the typed
    /// character is wiped, and its echo back (nvim accepted it, at a clamped
    /// position) is dropped as self-originated. The key path checks this to
    /// route insert-mode typing through nvim instead of Visual Studio; an
    /// in-memory read, so the zero-I/O invariant holds.
    /// </summary>
    public bool HasUnappliedRemoteEdits => !_incoming.IsEmpty;

    /// <summary>
    /// nvim edits parked behind a Visual Studio write still in flight (see
    /// OnRemoteLines): received, not yet queued for the drain. Not for the
    /// key path - it takes the parking lock.
    /// </summary>
    public bool HasParkedRemoteEdits
    {
        get
        {
            lock (_parked)
            {
                return _parked.Count > 0;
            }
        }
    }

    /// <summary>
    /// Visual Studio edits sent to nvim and not yet confirmed. While any are
    /// out, nvim's cursor describes text it has not caught up with, so an
    /// insert-mode caret correction from nvim would drag the caret backwards
    /// (CursorSynchronizer checks this). An in-memory read, zero I/O.
    /// </summary>
    public bool HasLocalEditsInFlight => Volatile.Read(ref _inFlight) > 0;

    /// <summary>
    /// The snapshot version the latest applied nvim edit produced, -1 before
    /// any. UI thread only. CursorSynchronizer reads it to tell a caret
    /// displaced by that edit (the drain replaces whole lines, breaks
    /// included, so the caret slides to the next line's start) from a move
    /// the user made: the former must not be echoed back as nvim's cursor.
    /// </summary>
    internal int RemoteAppliedVersion => _remoteAppliedVersion;

    private int _remoteAppliedVersion = -1;

    /// <summary>
    /// The mirror can race a focus switch and address a span nvim no longer has.
    /// That is self-correcting on the next prime; leaving the task unobserved is
    /// not, so faults are drained rather than left for the finalizer.
    /// </summary>
    private static void Observe(Task task) => _ = ObserveAsync(task);

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            // The task is an RPC reply from NvimRpcClient: completed on the
            // thread pool, never joined from the UI thread, so the deadlock
            // VSTHRD003 warns about has no path here.
#pragma warning disable VSTHRD003
            await task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        catch (Exception ex)
        {
            Log.Write("buffer sync span rejected", ex.GetBaseException());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _buffer.Changed -= OnBufferChanged;
        _session.RemoteBufferChanged -= OnRemoteBufferChanged;
        _session.BufferLinesChanged -= OnRemoteLines;
        _session.BufferDetached -= OnRemoteDetached;
        _session.State.ModeChanged -= OnModeChangedForUndo;
        UnwatchDirtyState();
        _verify.Dispose();
        // Dispose runs on the UI thread (view close, reopen); an open insert
        // session must not outlive the mirror. CheckAccess is the guard the
        // analyzer cannot see.
#pragma warning disable VSTHRD010
        try
        {
            if (ThreadHelper.CheckAccess())
            {
                EndInsertUndoGroup();
            }
        }
        catch { }
#pragma warning restore VSTHRD010

        // Give up ownership, but only if it is still ours. A mirror that has
        // already been replaced must not evict its successor on the way out -
        // ForDocument disposes the old one before constructing the new one
        // over the same key, and the key path reads the buffer property.
        if (_buffer.Properties.TryGetProperty(typeof(BufferMirror), out BufferMirror registered)
            && ReferenceEquals(registered, this))
        {
            _buffer.Properties.RemoveProperty(typeof(BufferMirror));
        }

        string key = KeyFor(_buffer, _filePath);
        lock (Live)
        {
            if (Live.TryGetValue(key, out var current) && ReferenceEquals(current, this))
            {
                Live.Remove(key);
            }
        }

        // The nvim buffer itself stays: Registry keeps its handle, and a
        // reopen adopts it through EnsureCreatedAsync and re-primes.
        Log.Write("disposed mirror " + GetHashCode() + " for " + (_filePath ?? "<unnamed>"));
    }
}
