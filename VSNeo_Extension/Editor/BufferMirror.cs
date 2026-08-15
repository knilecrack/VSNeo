using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
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
        private bool _disposed;

        private readonly System.Threading.Timer _verify;
        private long _handle = -1;

        /// <summary>Flipped only after the first prime completes; events before that are ours.</summary>
        private int _hasPrimed;

        private readonly CursorSynchronizer _cursorSync;
        private readonly Microsoft.VisualStudio.Text.Operations.ITextUndoHistoryRegistry _undoRegistry;

        /// <summary>Private on purpose: go through <see cref="ForDocument"/>, which
        /// guarantees one writer per nvim buffer.</summary>
        private BufferMirror(ITextBuffer buffer, NvimSession session, string? filePath,
                            CursorSynchronizer cursorSync,
                            Microsoft.VisualStudio.Text.Operations.ITextUndoHistoryRegistry undoRegistry)
        {
            _buffer       = buffer;
            _session      = session;
            _filePath     = filePath;
            _cursorSync   = cursorSync;
            _undoRegistry = undoRegistry;
            _verify = new System.Threading.Timer(
                _ => Verify(), null,
                System.Threading.Timeout.Infinite,
                System.Threading.Timeout.Infinite);
            _buffer.Changed += OnBufferChanged;
            _session.RemoteBufferChanged += ScheduleVerify;
            _session.BufferLinesChanged += OnRemoteLines;
            _session.BufferDetached += OnRemoteDetached;
        }

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
            if (IsOwnEcho(tick)) return;

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
            var replacement = (args[4] as object[] ?? new object[0])
                              .Select(NvimStateHub.AsString)
                              .ToArray();

            // Accepted edits are rare and user-paced, and each one writes to a real
            // file - so record exactly why the echo guard let it through. When an
            // echo slips past, this line is what says how.
            Log.Write("accepting nvim edit on buffer " + buf + ": tick " + tick
                      + " (selfTick " + Volatile.Read(ref _selfTick)
                      + ", inFlight " + Volatile.Read(ref _inFlight) + "), lines "
                      + firstLine + "-" + lastLine + " replaced by " + replacement.Length);

            _incoming.Enqueue(new RemoteEdit(firstLine, lastLine, replacement));

            // Collapse to one hop, so everything nvim produced for a single command
            // is drained together. That grouping is what makes the undo transaction
            // below cover the whole operator: cw and J each emit two events, and
            // applying them in separate callbacks would leave you pressing Ctrl+Z
            // twice to undo one keystroke.
            if (Interlocked.Exchange(ref _applyScheduled, 1) == 1) return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) { Volatile.Write(ref _applyScheduled, 0); return; }

            // Input priority, for the same measured reason as the caret hop in
            // CursorSynchronizer: an unjoined SwitchToMainThreadAsync queues
            // behind Visual Studio's background work (373 ms average), and an
            // operator that lands half a second late reads as a frozen editor.
#pragma warning disable VSTHRD001
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(DrainRemoteEdits));
#pragma warning restore VSTHRD001
        }

        private readonly System.Collections.Concurrent.ConcurrentQueue<RemoteEdit> _incoming
            = new System.Collections.Concurrent.ConcurrentQueue<RemoteEdit>();
        private int _applyScheduled;

        private readonly struct RemoteEdit
        {
            public readonly int First;
            public readonly int Last;
            public readonly string[] Replacement;

            public RemoteEdit(int first, int last, string[] replacement)
            {
                First = first;
                Last = last;
                Replacement = replacement;
            }
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
            // Runs on the UI thread via the dispatcher hop in OnRemoteLines; the
            // analyzer cannot see through BeginInvoke, so the contract is asserted.
            ThreadHelper.ThrowIfNotOnUIThread();

            // Released first, so an event arriving mid-drain schedules a fresh pass
            // rather than being stranded in the queue.
            Volatile.Write(ref _applyScheduled, 0);

            if (_disposed) return;

            var history = TryGetUndoHistory();
            if (history == null)
            {
                while (_incoming.TryDequeue(out var plain)) ApplyRemoteLines(plain);
                _cursorSync?.ReapplyAfterEdit();
                return;
            }

            bool changed = false;
            using (var transaction = history.CreateTransaction("VSNeo"))
            {
                while (_incoming.TryDequeue(out var edit))
                    changed |= ApplyRemoteLines(edit);

                // An empty transaction would still land in the undo stack, giving a
                // Ctrl+Z that appears to do nothing at all.
                if (changed) transaction.Complete();
                else transaction.Cancel();
            }

            if (changed) _cursorSync?.ReapplyAfterEdit();
        }

        // Null is a real outcome: the undo registry can refuse the buffer, and the
        // catch covers it throwing. Callers fall back to an untransacted apply.
        private Microsoft.VisualStudio.Text.Operations.ITextUndoHistory? TryGetUndoHistory()
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

                int start = first < snapshot.LineCount
                    ? snapshot.GetLineFromLineNumber(first).Start.Position
                    : snapshot.Length;
                int end = last < snapshot.LineCount
                    ? snapshot.GetLineFromLineNumber(last).Start.Position
                    : snapshot.Length;

                string text = Compose(snapshot, first, last, replacement);

                // The echo guard, and deliberately a comparison rather than
                // changedtick bookkeeping. Every span we send to nvim comes straight
                // back as one of these events, and the tick identifying it arrives on
                // a *later* reply than the notification - so a tick-based check races
                // and occasionally re-applies our own edit. Text that already matches
                // needs no edit whatever caused it.
                if (string.Equals(snapshot.GetText(Span.FromBounds(start, end)), text, StringComparison.Ordinal))
                    return false;

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
        /// Builds the replacement text, and the line breaks are the whole difficulty.
        /// nvim deals in lines; Visual Studio deals in a character range that happens
        /// to span them.
        /// </summary>
        private static string Compose(ITextSnapshot snapshot, int first, int last, string[] lines)
        {
            // Deleting the range outright: the span already covers the trailing break,
            // so replacing it with nothing joins the two ends correctly.
            if (lines.Length == 0) return string.Empty;

            string newline = LineBreakOf(snapshot);
            string body = string.Join(newline, lines);

            // Appending past the last line needs a break in front of it, since the
            // span starts at the very end of the buffer rather than at a line start.
            if (first >= snapshot.LineCount && snapshot.Length > 0)
                return newline + body;

            // A range that stops short of the end consumed a trailing break, so put
            // one back or the following line joins onto this one.
            return last < snapshot.LineCount ? body + newline : body;
        }

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
        private async void TrackWrite(long buf, System.Threading.Tasks.Task write) =>
            // The task being followed is foreign by design: observing a write that
            // was issued elsewhere to completion is the entire purpose of this pair.
#pragma warning disable VSTHRD003
            await TrackWriteAsync(buf, write).ConfigureAwait(false);
#pragma warning restore VSTHRD003
#pragma warning restore VSTHRD100

        private async System.Threading.Tasks.Task TrackWriteAsync(
            long buf, System.Threading.Tasks.Task write)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                // Foreign task, deliberately awaited: see TrackWrite above.
#pragma warning disable VSTHRD003
                await write.ConfigureAwait(false);
#pragma warning restore VSTHRD003

                var tick = await _session.RequestAsync("nvim_buf_get_changedtick", buf)
                                         .ConfigureAwait(false);
                RecordSelfTick(Convert.ToInt64(tick ?? (object)0L));
            }
            catch (Exception ex)
            {
                // A rejected span is ordinary while the two are momentarily out of
                // step, and self-correcting. What must not happen is the count
                // staying up, so this lives above the finally rather than instead.
                Log.Write("span rejected on buffer " + buf, ex);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

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
        private long Handle => System.Threading.Volatile.Read(ref _handle);

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
        private static readonly Dictionary<string, System.Threading.Tasks.Task<long>> Registry
            = new Dictionary<string, System.Threading.Tasks.Task<long>>(StringComparer.OrdinalIgnoreCase);

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
                Live[key] = mirror;
                return mirror;
            }
        }

        public async System.Threading.Tasks.Task<long> EnsureCreatedAsync()
        {
            long existing = Handle;
            if (existing >= 0) return existing;

            string key = KeyFor(_buffer, _filePath);

            System.Threading.Tasks.Task<long> creation;
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
                if (!ours)
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

        private async System.Threading.Tasks.Task<long> CreateAsync()
        {
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
                var mine = _buffer.CurrentSnapshot.Lines.Select(l => l.GetText()).ToArray();

                var raw = await _session.RequestAsync("nvim_buf_get_lines", buf, 0, -1, false)
                                        .ConfigureAwait(false) as object[];
                if (raw == null || _disposed) return;

                var theirs = raw.Select(NvimStateHub.AsString).ToArray();
                if (mine.Length == theirs.Length && mine.SequenceEqual(theirs, StringComparer.Ordinal))
                {
                    // Settled and identical, so nothing can still be in flight. An
                    // echo counted but never delivered would otherwise persist for the
                    // whole session and swallow a real edit later - which is precisely
                    // how the buffers came apart, and the sort of accounting slip that
                    // should cost one edit rather than every edit after it.
                    Volatile.Write(ref _consecutiveDrifts, 0);
                    return;
                }

                Log.Write("mirror drifted in buffer " + buf + " (VS " + mine.Length
                          + " lines, nvim " + theirs.Length + ") - resending");

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

                if (WildlyApart(mine.Length, theirs.Length))
                    Log.Write("large drift in buffer " + buf + " ("
                              + Math.Abs(mine.Length - theirs.Length)
                              + " lines apart) - re-priming nvim from Visual Studio");

                // Tracked like any other write, and that is the whole point. Left
                // untracked, this resend's own event came back looking like nvim's
                // work: it replaces the range nvim *had* with the lines VS has, so
                // applying it grew VS by exactly the gap, which widened the gap,
                // which triggered the next resend. Fifty-two lines every five
                // hundred milliseconds, without limit.
                await TrackWriteAsync(buf, _session.RequestAsync(
                    "nvim_buf_set_lines", buf, 0, -1, false, mine.Cast<object>().ToArray()))
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

        private async System.Threading.Tasks.Task PrimeAsync(long buf)
        {
            if (buf < 0) return;

            // Attach first, then fill, so the fill's own event is seen and its tick
            // recorded as ours. Filling first leaves nvim's copy at a tick we never
            // learned about, and the first event after it looks like nvim's work.
            //
            // send_buffer false: we only want to be told that something changed, not
            // handed the contents. Verify decides whether it actually matters.
            await _session.RequestAsync(
                "nvim_buf_attach", buf, false, new Dictionary<string, object>())
                .ConfigureAwait(false);

            var lines = _buffer.CurrentSnapshot.Lines.Select(l => l.GetText()).ToArray();
            Log.Write("priming buffer " + buf + " with " + lines.Length
                      + " lines (" + (_filePath ?? "<unnamed>") + ")");
            await TrackWriteAsync(buf,
                _session.RequestAsync("nvim_buf_set_lines", buf, 0, -1, false, lines))
                .ConfigureAwait(false);

            // Only after this can an nvim event be a genuine edit rather than our
            // own prime echo; OnRemoteLines gates on it.
            Volatile.Write(ref _hasPrimed, 1);
        }

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

            // Reverse order matters. Visual Studio reports every change against the
            // *old* snapshot, but each nvim_buf_set_text shifts everything after it.
            // Applying from the last span backwards leaves the earlier offsets still
            // valid, so none of them have to be recomputed.
            for (int i = e.Changes.Count - 1; i >= 0; i--)
                SendSpan(buf, e.Before, e.Changes[i]);

            ScheduleVerify();
        }

        /// <summary>
        /// Translates one VS change into nvim_buf_set_text. Rows are 0-based and
        /// columns are UTF-8 byte offsets, so every column goes through ColumnMapper.
        /// </summary>
        private void SendSpan(long buf, ITextSnapshot before, ITextChange change)
        {
            var startLine = before.GetLineFromPosition(change.OldPosition);
            var endLine = before.GetLineFromPosition(change.OldEnd);

            int startCol = ColumnMapper.CharToByte(
                startLine.GetText(), change.OldPosition - startLine.Start.Position);
            int endCol = ColumnMapper.CharToByte(
                endLine.GetText(), change.OldEnd - endLine.Start.Position);

            TrackWrite(buf, _session.RequestAsync(
                "nvim_buf_set_text", buf,
                startLine.LineNumber, startCol,
                endLine.LineNumber, endCol,
                SplitLines(change.NewText)));
        }

        /// <summary>
        /// nvim wants the replacement as one entry per line. A pure deletion arrives
        /// as empty text, which splits to a single empty string - exactly the "replace
        /// this span with nothing" that joins the two ends together.
        /// </summary>
        private static object[] SplitLines(string text) =>
            string.IsNullOrEmpty(text)
                ? new object[] { string.Empty }
                : text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                      .Cast<object>()
                      .ToArray();

        private void ReplaceAll(long buf, ITextSnapshot snapshot)
        {
            var lines = snapshot.Lines.Select(l => l.GetText()).ToArray();
            TrackWrite(buf, _session.RequestAsync("nvim_buf_set_lines", buf, 0, -1, false, lines));
        }

        /// <summary>
        /// The mirror can race a focus switch and address a span nvim no longer has.
        /// That is self-correcting on the next prime; leaving the task unobserved is
        /// not, so faults are drained rather than left for the finalizer.
        /// </summary>
        private static void Observe(System.Threading.Tasks.Task task) =>
            // The continuation itself has nothing left to fail but the log write,
            // so its task is deliberately discarded.
            _ = task.ContinueWith(
                // OnlyOnFaulted means this only runs for a faulted task, so
                // Exception is always set here.
                t => Infrastructure.Log.Write("buffer sync span rejected", t.Exception!.GetBaseException()),
                System.Threading.CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted,
                System.Threading.Tasks.TaskScheduler.Default);

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
            _session.RemoteBufferChanged -= ScheduleVerify;
            _session.BufferLinesChanged -= OnRemoteLines;
            _session.BufferDetached -= OnRemoteDetached;
            _verify.Dispose();

            // Give up ownership, but only if it is still ours. A mirror that has
            // already been replaced must not evict its successor on the way out.
            string key = KeyFor(_buffer, _filePath);
            lock (Live)
            {
                if (Live.TryGetValue(key, out var current) && ReferenceEquals(current, this))
                    Live.Remove(key);
            }
        }
    }
}
