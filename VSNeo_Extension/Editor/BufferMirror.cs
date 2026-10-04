using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
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
    internal sealed class BufferMirror : IDisposable
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
        // (one undo transaction) and the typed text is Visual Studio's own
        // typing (its own units), so u took two presses - and a long insert
        // took several, Visual Studio undoing typing in word-sized chunks.
        //
        // One undo transaction spans the insert session: opened when the mode
        // becomes insert or replace, or earlier by the drain when a change
        // command's deletion lands with the mode already reading insert (the
        // lines event travels ahead of the mode push), and completed on the
        // way out. Every transaction created meanwhile - the drain's, Visual
        // Studio's typing, a completion commit - nests inside it. Only the
        // buffer nvim's window shows takes part; every mirror hears ModeChanged.
        private ITextUndoTransaction? _insertTransaction;   // UI thread only

        private void OnModeChangedForUndo(VimMode mode)
        {
            bool insert = mode == VimMode.Insert || mode == VimMode.Replace;
            bool replace = mode == VimMode.Replace;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || _disposed) return;
#pragma warning disable VSTHRD001
            _ = dispatcher.BeginInvoke(Infrastructure.UiPriority.KeyResponse, new Action(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (insert) OpenInsertTransaction(); else CloseInsertTransaction();
                TrackReplaceSession(replace);
            }));
#pragma warning restore VSTHRD001
        }

        // Replace-session undo, UI thread only. Replace mode types through nvim
        // (R, gR), so every replaced character arrives as its own drain and its
        // own undo step: Rhello<Esc> took five u. Session-wide transactions are
        // off (see OpenInsertTransaction), so the session's steps are counted
        // instead and undone as one, while nothing has edited the buffer since.
        // ponytail: an edit drained in the same pass as the session's last
        // characters (Rab<Esc>x typed faster than the UI thread) joins the group.
        private bool _inReplaceSession;
        private int _replaceSessionSteps;
        private int _replaceGroupSteps;
        private int _replaceGroupVersion = -1;
        private bool _replaceGroupUndone;

        private void TrackReplaceSession(bool replace)
        {
            if (replace == _inReplaceSession) return;
            _inReplaceSession = replace;
            if (replace) { _replaceSessionSteps = 0; return; }
            if (_replaceSessionSteps < 2) return;
            _replaceGroupSteps = _replaceSessionSteps;
            _replaceGroupVersion = _buffer.CurrentSnapshot.Version.VersionNumber;
            _replaceGroupUndone = false;
        }

        /// <summary>
        /// UI thread. How many history steps u (or &lt;C-r&gt;) takes now: the
        /// last replace session's count while the buffer is exactly as that
        /// session (or its undo) left it, else one. Call
        /// <see cref="NoteUndoRedo"/> afterwards.
        /// </summary>
        internal int UndoSteps(bool undo) =>
            _replaceGroupSteps > 1 && _replaceGroupUndone != undo
            && _buffer.CurrentSnapshot.Version.VersionNumber == _replaceGroupVersion
                ? _replaceGroupSteps : 1;

        internal void NoteUndoRedo(bool undo, int steps)
        {
            if (steps < 2) { _replaceGroupSteps = 0; return; }
            _replaceGroupUndone = undo;
            _replaceGroupVersion = _buffer.CurrentSnapshot.Version.VersionNumber;
        }

        /// <summary>UI thread. Starts the insert session's transaction if this is the shown buffer and none is open.</summary>
        internal void OpenInsertTransaction()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Off (1.6.3). A real document's undo history in Visual Studio is an
            // adapter over the shell undo manager, and a transaction held open
            // across the insert session could not be closed there: reading its
            // undo primitives, its state and completing it all threw
            // NotSupportedException, the transaction stayed open, and an open
            // transaction refuses Undo - u was dead after the first change.
            // Per-edit undo (1.6.1) is back until grouping is rebuilt another
            // way and checked live; the headless tests cannot see this history.
            if (!InsertSessionUndoGrouping) return;
            if (_disposed || _insertTransaction != null) return;
            if (!ReferenceEquals(TextViewCreationListener.ShownBuffer, _buffer)) return;

            var history = TryGetUndoHistory();
            if (history == null) return;
            try
            {
                _insertTransaction = history.CreateTransaction("Vim insert");
                _insertOpenedAtVersion = _buffer.CurrentSnapshot.Version.VersionNumber;
            }
            catch (Exception ex)
            {
                Log.Write("could not open the insert undo transaction", ex);
                _insertTransaction = null;
            }
        }

        /// <summary>
        /// UI thread. Ends the insert session's transaction: on leaving insert,
        /// before any undo or redo (an open transaction refuses them), and on
        /// dispose. An empty one is cancelled so it leaves no no-op undo step.
        /// </summary>
        internal void CloseInsertTransaction()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var transaction = _insertTransaction;
            if (transaction == null) return;
            _insertTransaction = null;
            try
            {
                if (transaction.State != Microsoft.VisualStudio.Text.Operations.UndoTransactionState.Open) return;
                // Emptiness from the buffer's version, not transaction.UndoPrimitives:
                // a real document's history is Visual Studio's adapter over the
                // shell undo manager, and its transactions throw NotSupportedException
                // for UndoPrimitives. The close then failed, the transaction stayed
                // open with nothing holding it, every later edit nested inside it,
                // and an open transaction refuses Undo - u did nothing for the rest
                // of the session (1.6.2).
                if (_buffer.CurrentSnapshot.Version.VersionNumber == _insertOpenedAtVersion) transaction.Cancel();
                else transaction.Complete();
            }
            catch (Exception ex)
            {
                Log.Write("could not close the insert undo transaction", ex);
                // Never leave it open (an open transaction refuses Undo), and never
                // cancel or dispose one that holds edits: in the editor's history,
                // Cancel rolls the edits inside it back - the user's typing gone.
                // Completing is the only safe way out.
                try
                {
                    if (transaction.State == Microsoft.VisualStudio.Text.Operations.UndoTransactionState.Open)
                        transaction.Complete();
                }
                catch (Exception cex)
                {
                    Log.Write("could not complete the insert undo transaction either - undo may stay unavailable", cex);
                }
            }
        }

        private int _insertOpenedAtVersion;   // UI thread only

        /// <summary>See OpenInsertTransaction: off until grouping works with Visual Studio's own undo history.</summary>
        private static readonly bool InsertSessionUndoGrouping = false;

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
            if (_disposed || args == null || args.Length < 1) return;

            long buf = args[0] is NvimHandle h ? h.Id : ToLong(args[0]);
            if (buf != Handle) return;

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
            if (_disposed || args == null || args.Length < 5) return;

            long buf = args[0] is NvimHandle h ? h.Id : ToLong(args[0]);
            if (buf != Handle) return;   // some other document

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
            if (args[1] == null) return;

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
                replacement[i] = NvimStateHub.AsString(replacementRaw[i]);

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
            _incoming.Enqueue(new RemoteEdit(firstLine, lastLine, replacement, _session.NotificationSeq));

            // Collapse to one hop, so everything nvim produced for a single command
            // is drained together. That grouping is what makes the undo transaction
            // below cover the whole operator: cw and J each emit two events, and
            // applying them in separate callbacks would leave you pressing Ctrl+Z
            // twice to undo one keystroke.
            if (Interlocked.Exchange(ref _applyScheduled, 1) == 1) return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) { Volatile.Write(ref _applyScheduled, 0); return; }

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

        private readonly struct RemoteEdit(int first, int last, string[] replacement, long seq)
        {
            public readonly int First = first;
            public readonly int Last = last;
            public readonly string[] Replacement = replacement;
            public readonly long Seq = seq;
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

            if (_disposed) return;

            // The newest event drained: the caret correction waits for nvim's
            // cursor report after it.
            long lastSeq = 0;

            var history = TryGetUndoHistory();
            if (history == null)
            {
                while (_incoming.TryDequeue(out var plain))
                {
                    ApplyRemoteLines(plain);
                    if (plain.Seq > lastSeq) lastSeq = plain.Seq;
                }
                _cursorSync?.ReapplyAfterEdit(lastSeq);
                return;
            }

            // A change command's deletion (cw, c3l, S) arrives with the mode
            // already reading insert: open the insert session's transaction
            // now, so the deletion and the typing that follows undo together.
            var modeNow = _session.State.Mode;
            if (modeNow == VimMode.Insert || modeNow == VimMode.Replace) OpenInsertTransaction();

            bool changed = false;
            using (var transaction = history.CreateTransaction("VSNeo"))
            {
                while (_incoming.TryDequeue(out var edit))
                {
                    changed |= ApplyRemoteLines(edit);
                    if (edit.Seq > lastSeq) lastSeq = edit.Seq;
                }

                // An empty transaction would still land in the undo stack, giving a
                // Ctrl+Z that appears to do nothing at all.
                if (changed) transaction.Complete();
                else transaction.Cancel();
            }
            if (changed && _inReplaceSession) _replaceSessionSteps++;

            if (changed) _cursorSync?.ReapplyAfterEdit(lastSeq);
        }

        // Null is a real outcome: the undo registry can refuse the buffer, and the
        // catch covers it throwing. Callers fall back to an untransacted apply.
        private ITextUndoHistory? TryGetUndoHistory()
        {
            try { return _undoRegistry?.RegisterHistory(_buffer); }
            catch { return null; }
        }

        /// <summary>Returns true when the buffer actually changed.</summary>
        private bool ApplyRemoteLines(RemoteEdit edit)
        {
            if (_disposed || ApplyStopped) return false;

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
                        TripApply("nvim kept addressing lines this document does not have");

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
                    return false;

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

                // Tagged VSNeo so OnBufferChanged recognises it as ours and does not
                // send it straight back to nvim.
                using (var apply = _buffer.CreateEdit(EditOptions.None, null, "VSNeo"))
                {
                    apply.Replace(Span.FromBounds(start, end), text);
                    apply.Apply();
                }

                // Duplication reports need the one fact this pins down: an nvim-side
                // event growing the VS buffer. If content doubles on open, this line
                // says which event did it and what the mirror thought it was applying.
                int added = _buffer.CurrentSnapshot.LineCount - snapshot.LineCount;
                if (added != 0)
                    Log.Write("nvim edit applied to VS: lines " + first + "-" + last
                              + " replaced by " + replacement.Length + ", VS grew by "
                              + added + " to " + _buffer.CurrentSnapshot.LineCount
                              + " (buffer " + Handle + ", " + (_filePath ?? "<unnamed>") + ")");

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
            if (end - start != text.Length) return false;

            var chars = _compareChars ??= new char[4096];
            int pos = start, offset = 0;
            while (pos < end)
            {
                int n = Math.Min(chars.Length, end - pos);
                snapshot.CopyTo(pos, chars, 0, n);
                for (int i = 0; i < n; i++)
                    if (chars[i] != text[offset + i]) return false;
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
                if (!string.IsNullOrEmpty(text)) return text;
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
            if (Interlocked.Exchange(ref _applyTripped, 1) == 1) return;

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
            try
            {
                var reply = await issue().ConfigureAwait(false);

                object? tick = reply;
                if (reply is object[] parts && parts.Length >= 1)
                {
                    tick = parts[0];
                    if (parts.Length >= 3 && ToLong(parts[1]) > 0)
                        Log.Write("span rejected on buffer " + buf + " (" + parts[1]
                                  + " of the batch): " + NvimStateHub.AsString(parts[2]!));
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
                Interlocked.Decrement(ref _inFlight);
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
        /// True when this event is our own work rather than something nvim did.
        ///
        /// A tick at or below the highest we caused is certainly ours. Above it, the
        /// answer depends on whether one of our writes is still unconfirmed: if so
        /// this is most likely its event arriving before the tick that identifies it,
        /// and applying it would fight text the editor is still changing. With
        /// nothing in flight, nvim did it.
        /// </summary>
        private bool IsOwnEcho(long tick)
        {
            if (tick > 0 && tick <= Volatile.Read(ref _selfTick)) return true;
            return Volatile.Read(ref _inFlight) > 0;
        }

        private static int Clamp(int value, int low, int high) =>
            value < low ? low : (value > high ? high : value);

        private static long ToLong(object o)
        {
            try { return o == null ? -1 : Convert.ToInt64(o); }
            catch { return -1; }
        }

        /// <summary>
        /// This document's nvim buffer, or -1 until it has been created. Read from
        /// the edit path, which must never wait on the creation round trip: an edit
        /// arriving that early is covered by the priming that follows it.
        /// </summary>
        private long Handle => Volatile.Read(ref _handle);

        /// <summary>
        /// Creates the nvim buffer for this document, once. Every document gets its
        /// own, named after the real file.
        ///
        /// Sharing nvim's buffer 0 across all documents was the deeper problem
        /// underneath several smaller ones: two open files overwrote each other,
        /// every focus change resent an entire file to swap the contents over, and
        /// the jumplist, file marks and filetype had nothing stable to attach to.
        /// A named buffer per document removes all of that, and is what any plugin
        /// needs to work at all.
        /// </summary>
        /// <summary>
        /// One nvim buffer per <em>file</em>, not per ITextBuffer.
        ///
        /// Visual Studio makes a fresh ITextBuffer for the same path more often than
        /// you would expect - reopening a closed document, peek definition, diff
        /// views - and each one used to create its own nvim buffer and then try to
        /// claim a name nvim had already given away. nvim answers that with
        /// "E95: Buffer with this name already exists", creation threw, and because
        /// nothing logged that path the only visible symptom was that nvim's window
        /// still held the empty startup buffer: every window-relative motion did
        /// nothing and every caret push came back "cursor line out of range".
        /// </summary>
        private static readonly Dictionary<string, Task<long>> Registry
            = new Dictionary<string, Task<long>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Which mirror currently owns each nvim buffer, keyed exactly as
        /// <see cref="Registry"/> is.
        ///
        /// Sharing the handle was deliberate and correct; sharing it between two
        /// *live* mirrors was not. Visual Studio hands out a fresh ITextBuffer for
        /// the same path more often than you would expect - reopening a document is
        /// enough - and both mirrors then held nvim buffer 2, each resending its own
        /// snapshot over the other's every 500ms. Measured on an idle editor: two
        /// mirrors alternating at 2Hz for ninety seconds, 57 lines against 31,
        /// neither ever winning.
        /// </summary>
        private static readonly Dictionary<string, BufferMirror> Live
            = new Dictionary<string, BufferMirror>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Buffers with no file on disk cannot collide, so they key on identity.</summary>
        private static string KeyFor(ITextBuffer buffer, string? filePath) =>
            // net472's IsNullOrEmpty carries no [NotNullWhen], hence the forgiveness.
            string.IsNullOrEmpty(filePath)
                ? " anonymous:" + buffer.GetHashCode()
                : filePath!;

        /// <summary>
        /// The one mirror for this document, creating it if this is the first view of
        /// it and retiring any earlier mirror bound to a stale ITextBuffer.
        ///
        /// Always go through here rather than constructing one per text buffer: one
        /// nvim buffer must have exactly one writer, or the drift repair on each side
        /// spends the session undoing the other's.
        /// </summary>
        public static BufferMirror ForDocument(
            ITextBuffer buffer, NvimSession session, string? filePath,
            CursorSynchronizer cursorSync,
            Microsoft.VisualStudio.Text.Operations.ITextUndoHistoryRegistry undoRegistry)
        {
            // Called from the view attach (UI thread); retiring a mirror closes
            // its insert transaction, which the undo history wants there.
            ThreadHelper.ThrowIfNotOnUIThread();
            string key = KeyFor(buffer, filePath);

            lock (Live)
            {
                if (Live.TryGetValue(key, out var existing))
                {
                    if (!existing._disposed && ReferenceEquals(existing._buffer, buffer))
                        return existing;

                    Log.Write("retiring the previous mirror for " + (filePath ?? "<unnamed>")
                              + ": Visual Studio has given this document a new text buffer");
                    existing.Dispose();
                }

                var mirror = new BufferMirror(buffer, session, filePath, cursorSync, undoRegistry);
                Log.Write("created mirror " + mirror.GetHashCode() + " for " + (filePath ?? "<unnamed>"));
                Live[key] = mirror;
                return mirror;
            }
        }

        public async Task<long> EnsureCreatedAsync()
        {
            long existing = Handle;
            if (existing >= 0) return existing;

            string key = KeyFor(_buffer, _filePath);

            Task<long> creation;
            bool ours = false;
            lock (Registry)
            {
                if (!Registry.TryGetValue(key, out creation))
                {
                    creation = CreateAsync();
                    Registry[key] = creation;
                    ours = true;
                }
            }

            try
            {
                long handle = await creation.ConfigureAwait(false);
                System.Threading.Volatile.Write(ref _handle, handle);

                // A cached creation means an earlier mirror for this path already
                // filled the nvim buffer from *its* snapshot, and that mirror is now
                // retired. This document is the one on screen, so its text is the text
                // that counts - without this the adopted buffer keeps the dead view's
                // contents and every motion is computed against the wrong file.
                if (!ours && !_disposed)
                {
                    Log.Write("adopting nvim buffer " + handle + " for "
                              + (_filePath ?? "<unnamed>") + " - re-priming");
                    await PrimeAsync(handle).ConfigureAwait(false);
                }

                return handle;
            }
            catch (Exception ex)
            {
                // Do not leave a faulted task cached, or this document can never
                // recover: drop it so the next focus retries from scratch.
                lock (Registry)
                {
                    if (Registry.TryGetValue(key, out var current) && ReferenceEquals(current, creation))
                        Registry.Remove(key);
                }

                Log.Write("could not create an nvim buffer for " + (_filePath ?? "<unnamed>"), ex);
                throw;
            }
        }

        private async Task<long> CreateAsync()
        {
            // nvim may already hold a buffer for this file: :b, gf or a plugin
            // loaded it before Visual Studio ever showed the document. Naming a
            // fresh buffer the same path fails with E95, and a second buffer
            // for one path splits the edit stream - so adopt nvim's. Same
            // acwrite guard, same prime: Visual Studio's text wins, which is
            // right - nvim's self-loaded copy is disk content, and any unsaved
            // edits a plugin made there are discarded by design. Visual Studio
            // owns files.
            if (!string.IsNullOrEmpty(_filePath))
            {
                var found = await _session.RequestAsync(
                    "nvim_exec_lua", "return vsneo.find_buffer(...)",
                    new object[] { _filePath! }).ConfigureAwait(false);

                long adopted = 0;
                try { if (found != null) adopted = Convert.ToInt64(found); }
                catch { /* a non-numeric answer just means "not found" */ }

                if (adopted > 0)
                {
                    Log.Write("adopting nvim buffer " + adopted + " for " + _filePath
                              + " - nvim loaded it first");
                    await _session.RequestAsync(
                        "nvim_buf_set_option", adopted, "buftype", "acwrite")
                        .ConfigureAwait(false);
                    System.Threading.Volatile.Write(ref _handle, adopted);
                    await PrimeAsync(adopted).ConfigureAwait(false);
                    ScheduleVerify();
                    return adopted;
                }
            }

            // listed: true so :ls and :b see it like any other file.
            var created = await _session.RequestAsync("nvim_create_buf", true, false)
                                        .ConfigureAwait(false);

            long handle = created is NvimHandle h ? h.Id : Convert.ToInt64(created);
            if (handle <= 0) throw new InvalidOperationException("nvim_create_buf returned " + created);

            // acwrite routes :w to the BufWriteCmd the session installed, so nvim
            // cannot write this path from under Visual Studio.
            await _session.RequestAsync("nvim_buf_set_option", handle, "buftype", "acwrite")
                          .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(_filePath))
            {
                try
                {
                    // Non-null by the guard; net472's IsNullOrEmpty has no
                    // [NotNullWhen] to prove it to the compiler.
                    await _session.RequestAsync("nvim_buf_set_name", handle, _filePath!)
                                  .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // An unnamed buffer costs filetype and file marks. Losing the
                    // whole document to an exception costs everything, so carry on.
                    Log.Write("could not name buffer " + handle + " " + _filePath, ex);
                }
            }

            // Published before priming, not after. OnRemoteLines discards events for
            // any other buffer by comparing against this handle, and it does that
            // before counting the echo - so while it was still -1 the prime's own
            // event was dropped uncounted and left the tally one high. The settled
            // Verify cleaned that up, which is why it showed as a stale echo rather
            // than as a bug.
            System.Threading.Volatile.Write(ref _handle, handle);

            await PrimeAsync(handle).ConfigureAwait(false);

            // Detection has to run after both the name and the contents are in place;
            // some filetypes are decided by the first line, not the extension.
            await _session.RequestAsync(
                "nvim_exec_lua",
                "vim.api.nvim_buf_call(..., function() vim.cmd('filetype detect') end)",
                new object[] { handle }).ConfigureAwait(false);

            Log.Write("nvim buffer " + handle + " created for " + (_filePath ?? "<unnamed>"));

            // Check the two agree once things settle, rather than waiting for the
            // first edit to find out they never did.
            ScheduleVerify();
            return handle;
        }

        /// <summary>
        /// Re-checks the two buffers once editing pauses, and resends the file if
        /// they have diverged.
        ///
        /// Sending spans instead of the whole file made divergence permanent. A span
        /// is only meaningful against the exact text nvim already holds, and in
        /// milestone 1 nvim edits its own copy all the time - every normal-mode
        /// operator does, and none of it comes back to VS. Whole-file replace hid
        /// that by resynchronising on every keystroke. This restores the same
        /// self-healing without giving up the cheap path: spans while you type, one
        /// comparison when you stop.
        ///
        /// Visual Studio is authoritative here, which is what makes operators
        /// no-ops rather than corruption. Milestone 2 inverts this by applying
        /// nvim's edits back, and then this check becomes a safety net instead of
        /// the mechanism.
        /// </summary>
        private int _verifying;

        /// <summary>
        /// nvim's side of some buffer changed. Only this document's buffer is
        /// this mirror's business; an event that names none (-1) is taken as
        /// possibly ours.
        /// </summary>
        private void OnRemoteBufferChanged(long buf)
        {
            if (buf < 0 || buf == Handle) ScheduleVerify();
        }

        // The last state both sides were seen to agree on: Visual Studio's
        // snapshot version and nvim's changedtick, for this nvim buffer (a
        // changedtick means nothing across buffers, and adoption or a
        // re-prime can change which one this mirror writes). While neither
        // has moved, the buffers are still equal and a pass needs no hashing
        // on either side. -1: no agreement known.
        private long _agreedBuffer = -1;
        private int _agreedVersion = -1;
        private long _agreedTick = -1;

#pragma warning disable VSTHRD100
        private async void Verify()
        {
            // async void on a timer: a slow round trip (busy nvim, big file) could
            // otherwise overlap the next scheduled pass, and two concurrent
            // whole-buffer fetches interleave their drift counters and resends.
            if (Interlocked.CompareExchange(ref _verifying, 1, 0) == 1) return;

            try
            {
                long buf = Handle;
                if (_disposed || !_session.IsReady || buf < 0) return;

                // Snapshots are immutable, so reading one off the UI thread is safe.
                var snapshot = _buffer.CurrentSnapshot;
                int version = snapshot.Version.VersionNumber;

                // Digest compare, not nvim_buf_get_lines(0, -1): the settled case
                // is nearly every pass, and it used to ship the whole file back
                // over the pipe on each editing pause - every line msgpack-decoded
                // into a second managed array, many times per minute on large
                // files. The hash answers the same question in a 64-byte payload.
                //
                // When Visual Studio's side has not moved since the two last
                // agreed, nvim is handed the tick of that agreement: if its side
                // has not moved either, it answers without hashing, and neither
                // side reads the file at all.
                long knownTick = buf == _agreedBuffer && version == _agreedVersion ? _agreedTick : -1;
                var raw = await _session.RequestAsync(
                    "nvim_exec_lua", "return vsneo.buffer_hash(...)", new object[] { buf, knownTick })
                                        .ConfigureAwait(false) as object[];
                if (raw == null || raw.Length < 2 || _disposed) return;

                var theirsHash = NvimStateHub.AsString(raw[0]);
                int theirLines = Convert.ToInt32(raw[1]);
                long theirTick = raw.Length > 2 ? ToLong(raw[2]) : -1;

                bool unchanged = knownTick >= 0 && string.IsNullOrEmpty(theirsHash);
                if (unchanged
                    || (snapshot.LineCount == theirLines
                        && !string.IsNullOrEmpty(theirsHash)
                        && string.Equals(theirsHash, HashSnapshot(snapshot), StringComparison.OrdinalIgnoreCase)))
                {
                    _agreedBuffer = theirTick >= 0 ? buf : -1;
                    _agreedVersion = version;
                    _agreedTick = theirTick;

                    // Settled and identical, so nothing can still be in flight. An
                    // echo counted but never delivered would otherwise persist for the
                    // whole session and swallow a real edit later - which is precisely
                    // how the buffers came apart, and the sort of accounting slip that
                    // should cost one edit rather than every edit after it.
                    Volatile.Write(ref _consecutiveDrifts, 0);
                    return;
                }

                _agreedBuffer = -1;
                _agreedVersion = -1;
                _agreedTick = -1;

                // The comparison above was against the snapshot read before the
                // round trip. If Visual Studio has moved on since, the mismatch
                // may be nothing but that edit - its span is already on its way
                // to nvim - and resending the old snapshot would put nvim *behind*
                // the editor: a keystroke typed during the round trip wiped, and
                // an operator run in that window built from stale lines. Not a
                // drift observation, so it neither counts toward the trip nor
                // grows the backoff; re-check from fresh state instead.
                int now = _buffer.CurrentSnapshot.Version.VersionNumber;
                if (_disposed || now != version)
                {
                    if (!_disposed)
                        Log.Write("VS edited buffer " + buf + " during verify (v" + version + " -> v" + now
                                  + ") - re-checking instead of resending a stale snapshot");
                    ScheduleVerify();
                    return;
                }

                // Only the drift path needs to resend, and it resends from the
                // snapshot - no lines array is ever materialized.
                Log.Write("mirror drifted in buffer " + buf + " (VS " + snapshot.LineCount
                          + " lines, nvim " + theirLines + ") - resending");

                // A line or two apart is an operator in flight. A gap this size is
                // not something any single edit explains, but it is also what an
                // external file change or a reload produces - and the right response
                // to that is to re-prime nvim from Visual Studio, not to stop the
                // mirror permanently. Only repeated failure to converge is a reason
                // to give up.
                int drifts = Interlocked.Increment(ref _consecutiveDrifts);
                if (drifts >= 5)
                    TripApply("the mirror kept diverging after " + drifts
                              + " repairs, so repairing it is not working");

                if (WildlyApart(snapshot.LineCount, theirLines))
                    Log.Write("large drift in buffer " + buf + " ("
                              + Math.Abs(snapshot.LineCount - theirLines)
                              + " lines apart) - re-priming nvim from Visual Studio");

                // Tracked like any other write, and that is the whole point. Left
                // untracked, this resend's own event came back looking like nvim's
                // work: it replaces the range nvim *had* with the lines VS has, so
                // applying it grew VS by exactly the gap, which widened the gap,
                // which triggered the next resend. Fifty-two lines every five
                // hundred milliseconds, without limit.
                await TrackWriteAsync(buf, () => SetAllLinesAsync(buf, snapshot))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("mirror verify failed", ex);
            }
            finally
            {
                Volatile.Write(ref _verifying, 0);
            }
        }
#pragma warning restore VSTHRD100

        /// <summary>
        /// sha256 of the lines joined with '\n' - the exact convention
        /// vsneo.buffer_hash uses on the nvim side, so equal digests mean equal
        /// line arrays. Runs on the verify timer thread, never the key path.
        ///
        /// Streamed: each line is copied out of the snapshot in chunks through
        /// one reused char buffer, encoded through one reused byte buffer, and
        /// fed to the hash as it goes. It used to build the whole file three
        /// times over (a string per line, the joined text, its UTF-8 bytes); on
        /// any file past a few thousand lines two of those land on the large
        /// object heap, which only a full (gen2) collection frees - in
        /// devenv's heap, the kind of pause that reads as Visual Studio
        /// stuttering - and this ran on every editing pause.
        ///
        /// The buffers are this mirror's own: Verify never overlaps itself
        /// (_verifying), and mirrors verify concurrently.
        /// </summary>
        private string HashSnapshot(ITextSnapshot snapshot)
        {
            var chars = _hashChars ??= new char[HashChunk];
            var bytes = _hashBytes ??= new byte[System.Text.Encoding.UTF8.GetMaxByteCount(HashChunk)];
            // One encoder across the whole text, as a single GetBytes over the
            // joined string would be: a surrogate pair split between chunks
            // still encodes as one character.
            var encoder = _hashEncoder ??= System.Text.Encoding.UTF8.GetEncoder();
            encoder.Reset();

            // Reused across passes like the buffers: a fresh SHA256 object per
            // editing pause showed up in allocation profiles for no benefit.
            var sha = _hash ??= System.Security.Cryptography.SHA256.Create();
            sha.Initialize();
            {
                int count;
                int lineCount = snapshot.LineCount;
                for (int i = 0; i < lineCount; i++)
                {
                    var line = snapshot.GetLineFromLineNumber(i);
                    int pos = line.Start.Position, end = line.End.Position;
                    while (pos < end)
                    {
                        int n = Math.Min(chars.Length, end - pos);
                        snapshot.CopyTo(pos, chars, 0, n);
                        pos += n;
                        count = encoder.GetBytes(chars, 0, n, bytes, 0, false);
                        sha.TransformBlock(bytes, 0, count, null, 0);
                    }

                    // The separator goes through the encoder too, so anything it
                    // is still holding comes out before it, in order.
                    if (i < lineCount - 1)
                    {
                        chars[0] = '\n';
                        count = encoder.GetBytes(chars, 0, 1, bytes, 0, false);
                        sha.TransformBlock(bytes, 0, count, null, 0);
                    }
                }

                count = encoder.GetBytes(chars, 0, 0, bytes, 0, true);
                sha.TransformFinalBlock(bytes, 0, count);
                return ToHex(sha.Hash);
            }
        }

        private const int HashChunk = 4096;
        private char[]? _hashChars;
        private byte[]? _hashBytes;
        private System.Text.Encoder? _hashEncoder;
        private System.Security.Cryptography.SHA256? _hash;

        private static string ToHex(byte[] digest)
        {
            const string digits = "0123456789abcdef";
            var hex = new char[digest.Length * 2];
            for (int i = 0; i < digest.Length; i++)
            {
                hex[2 * i] = digits[digest[i] >> 4];
                hex[2 * i + 1] = digits[digest[i] & 0xF];
            }
            return new string(hex);
        }

        /// <summary>
        /// Long enough that it never runs mid-keystroke, short enough that the next
        /// motion after you stop typing is already working against correct text.
        /// </summary>
        private const int VerifyDelayMs = 500;

        /// <summary>
        /// Repair that is working converges in a single pass, so a second and a third
        /// mean the two ends disagree about something resending cannot settle. Backing
        /// off keeps that from becoming a permanent write loop against nvim - which is
        /// exactly what it became: 2Hz for ninety seconds on an editor nobody was
        /// touching, and it would have run all session.
        ///
        /// Doubling from 500ms, capped at half a minute. Convergence resets it, so the
        /// ordinary case still re-checks promptly.
        /// </summary>
        private int VerifyDelay
        {
            get
            {
                int drifts = Math.Min(Volatile.Read(ref _consecutiveDrifts), 6);
                return Math.Min(VerifyDelayMs << drifts, 30000);
            }
        }

        private void ScheduleVerify()
        {
            try { _verify.Change(VerifyDelay, System.Threading.Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        private async Task PrimeAsync(long buf)
        {
            // A retired mirror must not fill nvim's buffer from a text buffer
            // Visual Studio has dropped (a document closed mid-creation).
            if (buf < 0 || _disposed) return;

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
            // Nothing else resends the regions after a drift repair or a
            // reload - no edit, no document switch - so the fold synchronizer
            // is told to push them again.
            try { Primed?.Invoke(_buffer); }
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
            if (_disposed || !_session.IsReady) return;
            if (IsEchoOfOurOwnApply(e)) return;
            if (e.Changes.Count == 0) return;

            // An edit landing before the buffer exists needs no span: the priming
            // that follows creation sends the current text in full anyway.
            long buf = Handle;
            if (buf < 0) return;

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
                        WriteSpan(w, e.Before, e.Changes[i]);
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
            lock (_selfInflictedTicks) _selfInflictedTicks.Add(changedTick);
        }

        private bool IsEchoOfOurOwnApply(TextContentChangedEventArgs e)
        {
            if (e.EditTag is string tag && tag == "VSNeo") return true;
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _buffer.Changed -= OnBufferChanged;
            _session.RemoteBufferChanged -= OnRemoteBufferChanged;
            _session.BufferLinesChanged -= OnRemoteLines;
            _session.BufferDetached -= OnRemoteDetached;
            _session.State.ModeChanged -= OnModeChangedForUndo;
            _verify.Dispose();
            // Dispose runs on the UI thread (view close, reopen); an open insert
            // transaction must not outlive the mirror. CheckAccess is the guard
            // the analyzer cannot see.
#pragma warning disable VSTHRD010
            try { if (ThreadHelper.CheckAccess()) CloseInsertTransaction(); } catch { }
#pragma warning restore VSTHRD010

            // Give up ownership, but only if it is still ours. A mirror that has
            // already been replaced must not evict its successor on the way out -
            // ForDocument disposes the old one before constructing the new one
            // over the same key, and the key path reads the buffer property.
            if (_buffer.Properties.TryGetProperty(typeof(BufferMirror), out BufferMirror registered)
                && ReferenceEquals(registered, this))
                _buffer.Properties.RemoveProperty(typeof(BufferMirror));

            string key = KeyFor(_buffer, _filePath);
            lock (Live)
            {
                if (Live.TryGetValue(key, out var current) && ReferenceEquals(current, this))
                    Live.Remove(key);
            }

            // The nvim buffer itself stays: Registry keeps its handle, and a
            // reopen adopts it through EnsureCreatedAsync and re-primes.
            Log.Write("disposed mirror " + GetHashCode() + " for " + (_filePath ?? "<unnamed>"));
        }
    }
}
