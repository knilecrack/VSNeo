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

// Applying one remote edit in a VS undo transaction, and the guards that stop a runaway.
internal sealed partial class BufferMirror
{
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
}
