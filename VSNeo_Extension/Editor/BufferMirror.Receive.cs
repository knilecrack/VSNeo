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

// nvim to Visual Studio: attach, detach, the parked and queued remote edits, and the drain.
internal sealed partial class BufferMirror
{
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
}
