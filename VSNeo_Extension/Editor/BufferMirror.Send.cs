using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// Visual Studio to nvim: spans out, the prime, and telling our own echoes apart.
internal sealed partial class BufferMirror
{
    private readonly HashSet<long> _selfInflictedTicks = new HashSet<long>();

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
        Log.Key("track+ buffer " + buf
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
            Log.Key("track- buffer " + buf + " tick " + tick
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

        Log.Key("sending VS edit on buffer " + buf + ": "
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
}
