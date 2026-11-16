using System;
using System.Collections.Generic;
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
    private readonly HashSet<long> _selfInflictedTicks = new HashSet<long>();
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
        _verify = new Timer(_ => Verify(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _buffer.Changed += OnBufferChanged;
        _session.RemoteBufferChanged += OnRemoteBufferChanged;
        _session.BufferLinesChanged += OnRemoteLines;
        _session.BufferDetached += OnRemoteDetached;
        _session.State.ModeChanged += OnModeChangedForUndo;
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
        _ = dispatcher.BeginInvoke(Infrastructure.UiPriority.KeyResponse, new Action(() =>
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
    /// nvim unhooked us from this buffer. It does that of its own accord when the
    /// buffer is unloaded or reloaded, and the only notice is this one event - so
    /// left alone the mirror keeps running against a buffer it no longer hears
    /// from, and every operator silently stops arriving.
    /// </summary>
    private void OnRemoteDetached(object[] args)
    {
        if (_disposed || args == null || args.Length < 1)
        {
            return;
        }

        long buf = args[0] is NvimHandle h ? h.Id : ToLong(args[0]);
        if (buf != Handle)
        {
            return;
        }

        Log.Write("nvim detached from buffer " + buf + " - reattaching");
        Reattach(buf);
    }

    // async void on purpose: the detach notification has nothing to hand a
    // task back to, and every fault is caught and logged inside. No Async
    // suffix because VSTHRD200 reserves it for methods returning an awaitable.
#pragma warning disable VSTHRD100
    private async void Reattach(long buf)
    {
        try
        {
            // Prime rather than merely reattach: a detach usually means the buffer
            // was reloaded, so its contents are no longer the ones we mirrored.
            await PrimeAsync(buf).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Write("could not reattach to buffer " + buf, ex);
        }
    }
#pragma warning restore VSTHRD100

    /// <summary>
    /// nvim changed its copy: bring Visual Studio's into line.
    ///
    /// This is the direction milestone 1 deliberately did without, and it is what
    /// makes operators real - x, dd, dw and every macro change nvim's buffer and
    /// nothing came back, so Visual Studio never saw the edit.
    ///
    /// nvim_buf_attach sends [buffer, changedtick, firstline, lastline,
    /// replacement, more]: firstline and lastline bound the replaced range in the
    /// *old* buffer, and lastline of -1 means the whole buffer went.
    /// </summary>
    private void OnRemoteLines(object[] args)
    {
        if (_disposed || args == null || args.Length < 5)
        {
            return;
        }

        long buf = args[0] is NvimHandle h ? h.Id : ToLong(args[0]);
        if (buf != Handle)
        {
            return;   // some other document
        }

        // A null changedtick means the *display* changed and the buffer did not.
        // That is 'inccommand' previewing a substitution while you type
        // :%s/foo/bar/, and it is emphatically not an edit: nvim reverts it
        // internally, and that revert is not a buffer change, so it produces no
        // event to undo it with. Applying one would write the preview into the
        // real file and leave it there.
        //
        // Worth being defensive about even with 'inccommand' off, because it is
        // the one event in this protocol that carries text nvim does not intend
        // to keep.
        if (args[1] == null)
        {
            return;
        }

        // Every span we send nvim comes straight back as one of these. Counting
        // what is still in flight is what separates an echo from an edit nvim
        // made on its own, and it is the distinction that matters rather than the
        // mode: cw deletes the word *as* it enters insert, so gating on insert
        // threw that deletion away and the word survived in Visual Studio. While
        // you type, by contrast, there is always a span outstanding, and applying
        // those echoes back over text the editor is still changing is what
        // disturbed accepting a completion.
        long tick = ToLong(args[1]);
        if (IsOwnEcho(tick))
        {
            Infrastructure.Log.Key("dropping own echo on buffer " + buf + ": tick " + tick
                  + " (selfTick " + Volatile.Read(ref _selfTick)
                  + ", inFlight " + Volatile.Read(ref _inFlight) + ")");
            return;
        }

        // Above our highest known tick while a write is unconfirmed: either
        // that write's echo (arriving ahead of its reply) or a genuine edit
        // whose event follows the reply on the wire - the reply's
        // continuation runs on the thread pool, so the reader can dispatch
        // the next frame before RecordSelfTick has run, and dropping it then
        // lost the edit (insert a, Esc, x: the x was undone by the next
        // verify). Neither can be told apart yet, so the event is parked,
        // and with it every later one, so the wire order survives.
        // ReleaseParked re-classifies them by tick once the in-flight count
        // is back at zero - every write's tick recorded by then.
        long seq = _session.NotificationSeq;
        lock (_parked)
        {
            if (Volatile.Read(ref _inFlight) > 0 || _parked.Count > 0)
            {
                if (_parked.Count == 0)
                {
                    _parkedSince = Environment.TickCount;
                }

                _parked.Enqueue(new ParkedLines(buf, args, tick, seq));
                Infrastructure.Log.Key("parking nvim edit on buffer " + buf + ": tick " + tick
                      + " (selfTick " + Volatile.Read(ref _selfTick)
                      + ", inFlight " + Volatile.Read(ref _inFlight)
                      + ", parked " + _parked.Count + ")");
                return;
            }

            // Nothing in flight, nothing parked: nvim did it. Accepted under
            // the lock, so a release running on the thread pool cannot
            // enqueue an older event behind this one.
            AcceptRemoteLines(buf, tick, args, seq);
        }
    }

    /// <summary>
    /// Events parked by OnRemoteLines, oldest first. Guarded by locking
    /// the queue itself: the RPC reader parks, a write's completion
    /// releases (thread pool).
    /// </summary>
    private readonly Queue<ParkedLines> _parked = new Queue<ParkedLines>();
    private int _parkedSince;   // Environment.TickCount when the oldest parked event arrived; under the lock

    private readonly struct ParkedLines(long buf, object[] args, long tick, long seq)
    {
        public readonly long Buf = buf;
        public readonly object[] Args = args;
        public readonly long Tick = tick;
        public readonly long Seq = seq;
    }

    /// <summary>
    /// Called when the in-flight count reaches zero, after the write's
    /// reply recorded its tick. Each parked event, in order: at or below
    /// the highest tick we caused -> our echo, dropped; above it -> nvim's
    /// own edit, accepted through the normal path (queued for the UI-thread
    /// drain). Stops as soon as a new write is in flight again; that
    /// write's completion resumes the release.
    /// </summary>
    private void ReleaseParked()
    {
        lock (_parked)
        {
            while (_parked.Count > 0 && Volatile.Read(ref _inFlight) == 0)
            {
                var p = _parked.Dequeue();
                long buf = Handle;
                if (_disposed || p.Buf != buf)
                {
                    continue;   // re-primed onto another buffer meanwhile
                }

                if (p.Tick > 0 && p.Tick <= Volatile.Read(ref _selfTick))
                {
                    Infrastructure.Log.Key("dropping parked echo on buffer " + buf + ": tick " + p.Tick
                          + " (selfTick " + Volatile.Read(ref _selfTick) + ")");
                    continue;
                }

                Log.Write("releasing parked nvim edit on buffer " + buf + ": tick " + p.Tick
                          + " (selfTick " + Volatile.Read(ref _selfTick) + ")");
                AcceptRemoteLines(buf, p.Tick, p.Args, p.Seq);
            }

            if (_parked.Count > 0)
            {
                _parkedSince = Environment.TickCount;
            }
        }
    }

    /// <summary>
    /// The rest of OnRemoteLines, once an event is known not to be our own
    /// echo. RPC reader thread, or the thread pool via ReleaseParked;
    /// always under the _parked lock, which keeps the two in wire order.
    /// </summary>
    private void AcceptRemoteLines(long buf, long tick, object[] args, long seq)
    {
        // Nothing nvim does before the first prime completes can be a genuine
        // edit: the window is not even showing this buffer yet. Any event this
        // early is the prime's own echo or attach noise, and applying it writes
        // the file into Visual Studio a second time - the "content doubles when
        // I open a file" report. The tick accounting alone was meant to cover
        // this and demonstrably did not, so the gate is explicit.
        if (Volatile.Read(ref _hasPrimed) == 0)
        {
            Log.Write("dropping pre-prime event on buffer " + buf
                      + " (tick " + tick + ") - it can only be our own prime echo");
            return;
        }

        int firstLine = (int)ToLong(args[2]);
        int lastLine = (int)ToLong(args[3]);
        var replacementRaw = args[4] as object[] ?? Array.Empty<object>();
        var replacement = new string[replacementRaw.Length];
        for (int i = 0; i < replacement.Length; i++)
        {
            replacement[i] = NvimStateHub.AsString(replacementRaw[i]);
        }

        // Accepted edits are rare and user-paced, and each one writes to a real
        // file - so record exactly why the echo guard let it through. When an
        // echo slips past, this line is what says how.
        Log.Write("accepting nvim edit on buffer " + buf + ": tick " + tick
                  + " (selfTick " + Volatile.Read(ref _selfTick)
                  + ", inFlight " + Volatile.Read(ref _inFlight)
                  + ", mirror " + GetHashCode() + "), lines "
                  + firstLine + "-" + lastLine + " replaced by " + replacement.Length);

        // Where this event sits on the wire: the caret correction after the
        // drain must use a cursor report that came after it, not before.
        _incoming.Enqueue(new RemoteEdit(firstLine, lastLine, replacement, seq,
            _buffer.CurrentSnapshot.Version.ReiteratedVersionNumber));

        // Collapse to one hop, so everything nvim produced for a single command
        // is drained together. That grouping is what makes the undo transaction
        // below cover the whole operator: cw and J each emit two events, and
        // applying them in separate callbacks would leave you pressing Ctrl+Z
        // twice to undo one keystroke.
        if (Interlocked.Exchange(ref _applyScheduled, 1) == 1)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        { Volatile.Write(ref _applyScheduled, 0); return; }

        // KeyResponse priority, for the same measured reason as the caret hop
        // in CursorSynchronizer: an unjoined SwitchToMainThreadAsync queues
        // behind Visual Studio's background work (373 ms average), and an
        // operator that lands half a second late reads as a frozen editor.
        // Same priority as the caret, so the two keep their wire order: an
        // edit and the cursor report after it apply in the order nvim sent.
#pragma warning disable VSTHRD001
        // The delegate is allocated once: this hop runs per accepted edit.
        var drain = _drainRemoteEditsAction ??= new Action(DrainRemoteEdits);
        _ = dispatcher.BeginInvoke(Infrastructure.UiPriority.KeyResponse, drain);
#pragma warning restore VSTHRD001
    }

    // RPC thread only, where OnRemoteLines runs.
    private Action? _drainRemoteEditsAction;

    private readonly System.Collections.Concurrent.ConcurrentQueue<RemoteEdit> _incoming = new System.Collections.Concurrent.ConcurrentQueue<RemoteEdit>();
    private int _applyScheduled;

    /// <summary>
    /// Wire positions the drain must not pass yet, oldest first. An
    /// operator's range (vsneo_range_action) names rows as of its place on
    /// the wire, and dispatcher order alone does not keep that place: the
    /// drain is coalesced, so one posted for an earlier edit also took
    /// edits nvim sent after the range, before the range was pinned to
    /// the text (ddjgcckdd as a macro commented the wrong line). The
    /// drain stops at the first edit numbered after a hold; the range's
    /// own callback pins its rows and releases.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentQueue<long> DrainHolds = new System.Collections.Concurrent.ConcurrentQueue<long>();
    // Mirrors whose drain stopped at a hold. UI thread.
    private static readonly Queue<BufferMirror> HeldDrains = new Queue<BufferMirror>();

    /// <summary>RPC reader thread: edits numbered after <paramref name="seq"/>
    /// wait for <see cref="ReleaseDrainHold"/>, which must follow.</summary>
    internal static void HoldDrainAfter(long seq) => DrainHolds.Enqueue(seq);

    /// <summary>UI thread: drops the oldest hold and applies what waited on it.</summary>
    internal static void ReleaseDrainHold()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        DrainHolds.TryDequeue(out _);
        for (int n = HeldDrains.Count; n > 0; n--)
        {
            HeldDrains.Dequeue().DrainRemoteEdits();
        }
    }

    // UI thread, the queue's only consumer, so peek-then-dequeue is safe.
    private bool TryTakeRemoteEdit(out RemoteEdit edit)
    {
        if (_incoming.TryPeek(out edit) && DrainHolds.TryPeek(out long hold) && edit.Seq > hold)
        {
            if (!HeldDrains.Contains(this))
            {
                HeldDrains.Enqueue(this);
            }

            return false;
        }

        return _incoming.TryDequeue(out edit);
    }

    /// <summary>
    /// The snapshot version the latest applied nvim edit produced, -1 before
    /// any. UI thread only. CursorSynchronizer reads it to tell a caret
    /// displaced by that edit (the drain replaces whole lines, breaks
    /// included, so the caret slides to the next line's start) from a move
    /// the user made: the former must not be echoed back as nvim's cursor.
    /// </summary>
    internal int RemoteAppliedVersion => _remoteAppliedVersion;
    private int _remoteAppliedVersion = -1;

    private readonly struct RemoteEdit(int first, int last, string[] replacement, long seq, int undoState)
    {
        public readonly int First = first;
        public readonly int Last = last;
        public readonly string[] Replacement = replacement;
        public readonly long Seq = seq;
        public readonly int UndoState = undoState;
    }

    /// <summary>
    /// Applies everything queued as one undo step.
    ///
    /// Visual Studio's undo is authoritative by design, so an operator has to
    /// look like a single edit to it. One nvim command is frequently several
    /// on_lines events - cw deletes the word and then inserts, J joins and then
    /// removes the line it emptied - and without a transaction around them each
    /// half becomes its own undo entry.
    /// </summary>
    private void DrainRemoteEdits()
    {
        using var perf = Infrastructure.Perf.Time("BufferMirror.DrainRemoteEdits");
        // Runs on the UI thread via the dispatcher hop in OnRemoteLines; the
        // analyzer cannot see through BeginInvoke, so the contract is asserted.
        ThreadHelper.ThrowIfNotOnUIThread();

        // Released first, so an event arriving mid-drain schedules a fresh pass
        // rather than being stranded in the queue.
        Volatile.Write(ref _applyScheduled, 0);

        if (_disposed)
        {
            return;
        }

        // The newest event drained: the caret correction waits for nvim's
        // cursor report after it.
        long lastSeq = 0;

        var history = TryGetUndoHistory();
        if (history == null)
        {
            while (TryTakeRemoteEdit(out var plain))
            {
                ApplyRemoteLines(plain);
                if (plain.Seq > lastSeq)
                {
                    lastSeq = plain.Seq;
                }
            }

            _cursorSync?.ReapplyAfterEdit(lastSeq);
            return;
        }

        // Preserve the state before the first applied edit: the mode push
        // for a change can arrive after this dispatcher callback.
        var modeNow = _session.State.Mode;
        bool insert = modeNow == VimMode.Insert || modeNow == VimMode.Replace;
        int firstUndoState = -1;
        bool changed = false;
        using (var transaction = history.CreateTransaction("VSNeo"))
        {
            while (TryTakeRemoteEdit(out var edit))
            {
                bool editChanged = ApplyRemoteLines(edit);
                if (editChanged && firstUndoState < 0)
                {
                    firstUndoState = edit.UndoState;
                }

                changed |= editChanged;
                if (edit.Seq > lastSeq)
                {
                    lastSeq = edit.Seq;
                }
            }

            // An empty transaction would still land in the undo stack, giving a
            // Ctrl+Z that appears to do nothing at all.
            if (changed)
            {
                transaction.Complete();
            }
            else
            {
                transaction.Cancel();
            }
        }

        if (firstUndoState >= 0)
        {
            if (insert)
            {
                BeginInsertUndoGroup(firstUndoState);
            }
            else
            {
                _pendingInsertUndoStart = firstUndoState;
            }
        }

        if (changed)
        {
            _cursorSync?.ReapplyAfterEdit(lastSeq);
        }
    }

    // Null is a real outcome: the undo registry can refuse the buffer, and the
    // catch covers it throwing. Callers fall back to an untransacted apply.
    private ITextUndoHistory? TryGetUndoHistory()
    {
        try
        { return _undoRegistry?.RegisterHistory(_buffer); }
        catch { return null; }
    }

    /// <summary>Returns true when the buffer actually changed.</summary>
    private bool ApplyRemoteLines(RemoteEdit edit)
    {
        if (_disposed || ApplyStopped)
        {
            return false;
        }

        try
        {
            var snapshot = _buffer.CurrentSnapshot;
            var replacement = edit.Replacement;

            // Refuse an edit that addresses lines Visual Studio does not have.
            //
            // Clamping these looks defensive and is the opposite. Both ends
            // collapse to the end of the buffer, the span becomes empty, and the
            // replacement is *appended* rather than replacing anything - so every
            // event after the two copies diverge duplicates its text again. It
            // does not degrade, it accumulates: one report of it reached six
            // thousand repeated lines.
            //
            // first == LineCount is legitimate and means append, which is what o
            // on the last line does. Beyond that the two are out of step, and the
            // only safe move is to stop and reconcile wholesale.
            if (edit.First > snapshot.LineCount ||
                (edit.Last >= 0 && edit.Last > snapshot.LineCount))
            {
                Log.Write("nvim edit spans lines VS does not have (first=" + edit.First
                          + " last=" + edit.Last + ", VS has " + snapshot.LineCount
                          + ") - refusing it and resyncing");

                // One is a momentary lag. Several in a row means the two copies
                // are not going to agree again on their own.
                if (Interlocked.Increment(ref _refusals) >= 3)
                {
                    TripApply("nvim kept addressing lines this document does not have");
                }

                ScheduleVerify();
                return false;
            }

            int first = Clamp(edit.First, 0, snapshot.LineCount);
            int last = edit.Last < 0 ? snapshot.LineCount : Clamp(edit.Last, first, snapshot.LineCount);

            RemoteLineEdit.Plan(snapshot, first, last, replacement, LineBreakOf(snapshot),
                                out int start, out int end, out string text);

            // The echo guard, and deliberately a comparison rather than
            // changedtick bookkeeping. Every span we send to nvim comes straight
            // back as one of these events, and the tick identifying it arrives on
            // a *later* reply than the notification - so a tick-based check races
            // and occasionally re-applies our own edit. Text that already matches
            // needs no edit whatever caused it.
            if (SpanMatchesText(snapshot, start, end, text))
            {
                return false;
            }

            // The same check one region further out. nvim reports our own
            // insertion as replacing the following line as well (a one-line
            // set_text insert echoes as "first-last replaced by 2"), so the
            // span comparison above cannot recognise that echo shape: the
            // replacement is the text already at [first, first + replacement
            // length). Applying it duplicated the line below the restored
            // one. A false positive costs one nvim edit, which the drift
            // verify heals half a second later; a false negative costs a
            // duplicated line, which the verify then seals into both copies.
            //
            // Only for the shape our own set_text echoes as: a replaced range,
            // never a pure insert. nvim reports o, O and yyP as first == last
            // (nothing replaced), and for those the wider span is simply the
            // line below the insertion point - which trivially "matches" when
            // the inserted line equals it: a blank line opened above a blank
            // line, a pasted line above its own copy. Those were dropped, and
            // the verify then sealed the loss into nvim (Visual Studio wins).
            int replacedEnd = first + replacement.Length;
            if (edit.Last > edit.First && replacement.Length > 0 && replacedEnd <= snapshot.LineCount)
            {
                int existingEnd = replacedEnd < snapshot.LineCount
                    ? snapshot.GetLineFromLineNumber(replacedEnd).Start.Position
                    : snapshot.Length;
                if (SpanMatchesText(snapshot, start, existingEnd, text))
                {
                    Infrastructure.Log.Key("dropping set_text-shaped echo on buffer " + Handle
                        + ": lines " + first + "-" + last + " already read as the "
                        + replacement.Length + " replacement lines");
                    return false;
                }
            }

            // After the echo checks, which compare whole lines.
            RemoteLineEdit.Narrow(snapshot.GetText(start, end - start), ref start, ref end, ref text);

            // Tagged VSNeo so OnBufferChanged recognises it as ours and does not
            // send it straight back to nvim.
            using (var apply = _buffer.CreateEdit(EditOptions.None, null, "VSNeo"))
            {
                apply.Replace(Span.FromBounds(start, end), text);
                // Set before Apply too: the caret may report the move from
                // inside it. One edit is one version step.
                _remoteAppliedVersion = snapshot.Version.VersionNumber + 1;
                _remoteAppliedVersion = apply.Apply().Version.VersionNumber;
            }

            // Duplication reports need the one fact this pins down: an nvim-side
            // event growing the VS buffer. If content doubles on open, this line
            // says which event did it and what the mirror thought it was applying.
            int added = _buffer.CurrentSnapshot.LineCount - snapshot.LineCount;
            if (added != 0)
            {
                Log.Write("nvim edit applied to VS: lines " + first + "-" + last
                          + " replaced by " + replacement.Length + ", VS grew by "
                          + added + " to " + _buffer.CurrentSnapshot.LineCount
                          + " (buffer " + Handle + ", " + (_filePath ?? "<unnamed>") + ")");
            }

            Volatile.Write(ref _refusals, 0);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("could not apply nvim's edit to the VS buffer", ex);
            return false;
        }
    }

    /// <summary>
    /// Ordinal equality between a snapshot span and a string, without
    /// materializing the span. snapshot.GetText(span) hands back a whole
    /// new string, and for a big range (a gg=G echo) that string lands on
    /// the large object heap on the UI thread - per accepted edit. Chunks
    /// through one reused buffer instead; runs on the UI thread only (the
    /// drain), which is why the buffer can be shared.
    /// </summary>
    private bool SpanMatchesText(ITextSnapshot snapshot, int start, int end, string text)
    {
        if (end - start != text.Length)
        {
            return false;
        }

        var chars = _compareChars ??= new char[4096];
        int pos = start, offset = 0;
        while (pos < end)
        {
            int n = Math.Min(chars.Length, end - pos);
            snapshot.CopyTo(pos, chars, 0, n);
            for (int i = 0; i < n; i++)
            {
                if (chars[i] != text[offset + i])
                {
                    return false;
                }
            }

            pos += n;
            offset += n;
        }

        return true;
    }

    // UI thread only (ApplyRemoteLines runs inside the drain).
    private char[]? _compareChars;

    private static string LineBreakOf(ITextSnapshot snapshot)
    {
        for (int i = 0; i < snapshot.LineCount; i++)
        {
            var text = snapshot.GetLineFromLineNumber(i).GetLineBreakText();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return Environment.NewLine;
    }

    /// <summary>Writes issued to nvim that have not yet been confirmed.</summary>
    private int _inFlight;

    private int _applyTripped;
    private int _refusals;
    private int _consecutiveDrifts;

    /// <summary>
    /// Stop applying nvim's edits to this document, permanently for its lifetime.
    ///
    /// The same reasoning as the session breaker: a mirror that is confidently
    /// wrong is worse than one that is switched off. Everything here writes to a
    /// real source file, and the failure that motivated it turned a fifty line
    /// file into six thousand - so once the two copies are demonstrably out of
    /// step, the honest move is to stop writing rather than to keep guessing.
    ///
    /// Visual Studio to nvim keeps running, and a resync follows, so nvim's copy
    /// still matches what you can see and motions stay correct. What is lost is
    /// operators taking effect - which is exactly the milestone 1 behaviour this
    /// worked in for weeks.
    /// </summary>
    private void TripApply(string reason)
    {
        if (Interlocked.Exchange(ref _applyTripped, 1) == 1)
        {
            return;
        }

        Log.Write("MIRROR STOPPED for " + (_filePath ?? "<unnamed>") + ": " + reason
                  + ". nvim's edits will no longer be applied to this document; "
                  + "reopen it to start again.");

        // RaiseMirrorStopped takes a non-null string; an unnamed buffer has
        // no path to give, and the log line above renders the same fallback.
        _session.RaiseMirrorStopped(_filePath ?? "<unnamed>");
        ScheduleVerify();
    }

    private bool ApplyStopped => Volatile.Read(ref _applyTripped) == 1;

    /// <summary>
    /// Far enough apart that the two are not merely a keystroke out of step.
    ///
    /// An operator leaves them differing by a line or two for a few hundred
    /// milliseconds, which is ordinary. A gap that is a sizeable fraction of the
    /// file is not something any single edit explains.
    /// </summary>
    private static bool WildlyApart(int vsLines, int nvimLines)
    {
        int difference = Math.Abs(vsLines - nvimLines);
        return difference > Math.Max(20, vsLines / 4);
    }

    /// <summary>The highest changedtick known to have been caused by us.</summary>
    private long _selfTick;

    /// <summary>
    /// Follows one write to completion and records the changedtick it produced.
    ///
    /// Counting echo *arrivals* was the bug behind nvim's edits being reverted: a
    /// write that changes nothing, or that nvim rejects, delivers no event at all,
    /// so the tally never came back down and the next genuine edit was swallowed
    /// as though it were ours. The drift check then resent Visual Studio's copy
    /// and undid it - a deleted line reappearing.
    ///
    /// A request always completes, success or failure, so counting those cannot
    /// leak. And the tick it leaves behind identifies our own work exactly, which
    /// no amount of counting can.
    ///
    /// async void on purpose: the edit path fires this and forgets it - the key
    /// path must never wait on a round trip - and TrackWriteAsync catches and
    /// logs every fault.
    /// </summary>
#pragma warning disable VSTHRD100
    private async void TrackWrite(long buf, Func<Task<object?>> issue) =>
        await TrackWriteAsync(buf, issue).ConfigureAwait(false);
#pragma warning restore VSTHRD100

    /// <summary>
    /// <paramref name="issue"/> sends the write and yields the changedtick it
    /// left behind, in the same reply (vsneo.apply_spans / set_all_lines).
    /// That used to be a second request after the write completed - two round
    /// trips per typed character. The reply is either the tick itself or
    /// apply_spans' [tick, failedCount, firstError].
    ///
    /// The in-flight count goes up before the write is issued, not after:
    /// the write's own lines event can never beat the count now.
    /// </summary>
    private async Task TrackWriteAsync(
        long buf, Func<Task<object?>> issue)
    {
        Interlocked.Increment(ref _inFlight);
        Infrastructure.Log.Key("track+ buffer " + buf
            + " inFlight " + Volatile.Read(ref _inFlight));

        // A steady stream of writes keeps parked events parked - the same
        // "never apply over text the editor is still changing" rule the
        // drop used to enforce. Bounded only by this log line, so a soak
        // shows whether any nvim edit waits noticeably long.
        lock (_parked)
        {
            if (_parked.Count > 0 && Environment.TickCount - _parkedSince > 300)
            {
                Log.Write("nvim edits parked for " + (Environment.TickCount - _parkedSince)
                          + " ms behind writes on buffer " + buf + " (" + _parked.Count + " parked)");
            }
        }

        try
        {
            var reply = await issue().ConfigureAwait(false);

            object? tick = reply;
            if (reply is object[] parts && parts.Length >= 1)
            {
                tick = parts[0];
                if (parts.Length >= 3 && ToLong(parts[1]) > 0)
                {
                    Log.Write("span rejected on buffer " + buf + " (" + parts[1]
                              + " of the batch): " + NvimStateHub.AsString(parts[2]!));
                }
            }

            RecordSelfTick(Convert.ToInt64(tick ?? (object)0L));
            Infrastructure.Log.Key("track- buffer " + buf + " tick " + tick
                + " selfTick " + Volatile.Read(ref _selfTick));
        }
        catch (Exception ex)
        {
            // A rejected write is ordinary while the two are momentarily out of
            // step, and self-correcting. What must not happen is the count
            // staying up, so this lives above the finally rather than instead.
            Log.Write("span rejected on buffer " + buf, ex);
        }
        finally
        {
            // Zero means every write's tick is recorded (RecordSelfTick ran
            // above, before this): what was parked can now be told apart.
            if (Interlocked.Decrement(ref _inFlight) == 0)
            {
                ReleaseParked();
            }
        }
    }

    /// <summary>
    /// Whole-buffer replace plus its changedtick, one round trip. The lines
    /// are encoded straight off the snapshot into the frame - no string
    /// per line, no lines array: on a 10K-line file that is the difference
    /// between priming with zero line allocations and with ten thousand.
    /// </summary>
    private Task<object?> SetAllLinesAsync(long buf, ITextSnapshot snapshot) =>
        _session.ExecLuaAsync(
            "return vsneo.set_all_lines(...)",
            w =>
            {
                w.WriteArrayHeader(2);
                w.WriteInt64(buf);
                w.WriteSnapshotLines(snapshot);
            });

    private void RecordSelfTick(long tick)
    {
        long current;
        while ((current = Volatile.Read(ref _selfTick)) < tick &&
               Interlocked.CompareExchange(ref _selfTick, tick, current) != current)
        {
        }
    }

    /// <summary>
    /// True when this event is certainly our own work: a tick at or below the
    /// highest we caused. Above it with a write still unconfirmed the event is
    /// ambiguous and OnRemoteLines parks it (see ReleaseParked); with nothing
    /// in flight, nvim did it.
    /// </summary>
    private bool IsOwnEcho(long tick) =>
        tick > 0 && tick <= Volatile.Read(ref _selfTick);

    private static int Clamp(int value, int low, int high) =>
        value < low ? low : (value > high ? high : value);

    private static long ToLong(object o)
    {
        try
        { return o == null ? -1 : Convert.ToInt64(o); }
        catch { return -1; }
    }

    /// <summary>
    /// This document's nvim buffer, or -1 until it has been created. Read from
    /// the edit path, which must never wait on the creation round trip: an edit
    /// arriving that early is covered by the priming that follows it.
    /// </summary>
    private long Handle => Volatile.Read(ref _handle);

    private async Task PrimeAsync(long buf)
    {
        // A retired mirror must not fill nvim's buffer from a text buffer
        // Visual Studio has dropped (a document closed mid-creation).
        if (buf < 0 || _disposed)
        {
            return;
        }

        // Attach first, then fill, so the fill's own event is seen and its tick
        // recorded as ours. Filling first leaves nvim's copy at a tick we never
        // learned about, and the first event after it looks like nvim's work.
        //
        // send_buffer false: we only want to be told that something changed, not
        // handed the contents. Verify decides whether it actually matters.
        await _session.RequestAsync(
            "nvim_buf_attach", buf, false, new Dictionary<string, object>())
            .ConfigureAwait(false);

        var snapshot = _buffer.CurrentSnapshot;
        Log.Write("priming buffer " + buf + " with " + snapshot.LineCount
                  + " lines (" + (_filePath ?? "<unnamed>") + ")");
        await TrackWriteAsync(buf, () => SetAllLinesAsync(buf, snapshot))
            .ConfigureAwait(false);

        // Only after this can an nvim event be a genuine edit rather than our
        // own prime echo; OnRemoteLines gates on it.
        Volatile.Write(ref _hasPrimed, 1);

        // The prime replaced every line, and nvim's manual folds went with
        // them (the companion forgot its agreed regions in set_all_lines).
        // Nothing else resends the regions after a reload - no edit, no
        // document switch - so the fold synchronizer is told to push them
        // again. Verify's drift repair does the same.
        NotifyPrimed();
    }

    private void NotifyPrimed()
    {
        try
        { Primed?.Invoke(_buffer); }
        catch (Exception ex) { Log.Write("Primed handler threw", ex); }
    }

    /// <summary>
    /// A mirror finished priming nvim's buffer from this text buffer. Raised
    /// off the UI thread (the RPC reply's continuation); subscribers hop.
    /// Static: the fold synchronizer is one MEF part, mirrors are many.
    /// </summary>
    internal static event Action<ITextBuffer>? Primed;

    /// <summary>
    /// Past this many separate spans, one whole-buffer call is cheaper than the
    /// round trips. Format-document and find-replace-all land here.
    /// </summary>
    private const int MaxSpansPerEdit = 64;

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        if (_disposed || !_session.IsReady)
        {
            return;
        }

        if (IsEchoOfOurOwnApply(e))
        {
            return;
        }

        if (e.Changes.Count == 0)
        {
            return;
        }

        // An edit landing before the buffer exists needs no span: the priming
        // that follows creation sends the current text in full anyway.
        long buf = Handle;
        if (buf < 0)
        {
            return;
        }

        if (e.Changes.Count > MaxSpansPerEdit)
        {
            ReplaceAll(buf, e.After);
            return;
        }

        Infrastructure.Log.Key("sending VS edit on buffer " + buf + ": "
            + e.Changes.Count + " span(s) (mirror " + GetHashCode() + ")");

        // Reverse order matters. Visual Studio reports every change against the
        // *old* snapshot, but each nvim_buf_set_text shifts everything after it.
        // Applying from the last span backwards leaves the earlier offsets still
        // valid, so none of them have to be recomputed.
        //
        // One call for the whole batch: every span lands and the changedtick
        // comes back in the same reply (vsneo.apply_spans). The tick after
        // the last span is at or above every span's own, and RecordSelfTick
        // keeps the max, so the echo guard sees exactly what per-span
        // tracking would have given it - with one round trip instead of N+1.
        //
        // The batch is written straight into the frame: building it as
        // object[] first cost an array per span and four boxed ints, per
        // typed character. The write itself runs synchronously inside
        // ExecLuaAsync's send - before the first await - so capturing the
        // change event is not a lifetime hazard.
        TrackWrite(buf, () => _session.ExecLuaAsync(
            "return vsneo.apply_spans(...)",
            w =>
            {
                w.WriteArrayHeader(2);
                w.WriteInt64(buf);
                w.WriteArrayHeader(e.Changes.Count);
                for (int i = e.Changes.Count - 1; i >= 0; i--)
                {
                    WriteSpan(w, e.Before, e.Changes[i]);
                }
            }));

        ScheduleVerify();
    }

    /// <summary>
    /// Translates one VS change into nvim_buf_set_text's arguments, written
    /// straight into the frame. Rows are 0-based and columns are UTF-8 byte
    /// offsets, so every column goes through ColumnMapper.
    /// </summary>
    private static void WriteSpan(MsgPackWriter w, ITextSnapshot before, ITextChange change)
    {
        var startLine = before.GetLineFromPosition(change.OldPosition);
        var endLine = before.GetLineFromPosition(change.OldEnd);

        int startCol = ColumnMapper.CharToByte(
            startLine, change.OldPosition - startLine.Start.Position);
        int endCol = ColumnMapper.CharToByte(
            endLine, change.OldEnd - endLine.Start.Position);

        SpanEncoder.WriteSpan(w,
            startLine.LineNumber, startCol,
            endLine.LineNumber, endCol,
            change.NewText);
    }

    private void ReplaceAll(long buf, ITextSnapshot snapshot)
    {
        TrackWrite(buf, () => SetAllLinesAsync(buf, snapshot));
    }

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
            Infrastructure.Log.Write("buffer sync span rejected", ex.GetBaseException());
        }
    }

    internal void RecordSelfInflicted(long changedTick)
    {
        lock (_selfInflictedTicks)
        {
            _selfInflictedTicks.Add(changedTick);
        }
    }

    private bool IsEchoOfOurOwnApply(TextContentChangedEventArgs e)
    {
        if (e.EditTag is string tag && tag == "VSNeo")
        {
            return true;
        }

        return false;
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
