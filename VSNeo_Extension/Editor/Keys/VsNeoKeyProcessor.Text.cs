using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// Typed characters, VS-side undo/redo, insert delete-word and overwrite.
internal sealed partial class VsNeoKeyProcessor
{
    /// <summary>
    /// Printable characters. These deliberately do not come from PreviewKeyDown:
    /// a virtual key is not a character. Shift+2 is "@" on a US layout and """ on
    /// a UK one, and dead keys and AltGr have no single-key representation at all.
    /// WPF has already done that translation by the time it raises TextInput, so
    /// counts, "$", "%" and friends land correctly on every layout.
    ///
    /// Keys handled in PreviewKeyDown never reach here - marking the key event
    /// handled suppresses the composition - so there is no double send.
    /// </summary>
    public override void TextInput(TextCompositionEventArgs args)
    {
        using var perf = Infrastructure.Perf.Time("VsNeoKeyProcessor.TextInput");
        // WPF raises text input on the UI thread; UndoRedo below needs it.
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!VSNeoSettings.Enabled)
        {
            EnsureOverwriteOff();
            return;
        }

        // The pager's letter keys (j, k, q, G, Space...); see PreviewKeyDown.
        if (IsDocumentView && !ForeignFocus() && !string.IsNullOrEmpty(args.Text)
            && MessagePager.OpenFor(_view) is MessagePager pager
            && pager.HandleKey(args.Text))
        {
            args.Handled = true;
            return;
        }

        var session = Session;
        if (session == null)
        {
            EnsureOverwriteOff();
            return;
        }
        // Typed text is never :Sethandler's (KeyRoutingRules.IsShortcut).
        var mode = ResolveCtrlO(session, session.State.Mode);
        if (!ShouldIntercept(session))
        {
            // Passthrough is still text reaching the editor, so the
            // overwrite invariant applies here too: the IntelliSense gate
            // passes keys through with the mode cache wherever nvim left it,
            // and if that is Normal the block caret means overwrite is on -
            // a j typed while a signature tooltip was open used to replace
            // the character under the caret with j.
            EnsureOverwriteOff();
            return;
        }

        // Insert and replace belong to Visual Studio. The typed text reaches
        // nvim through the buffer mirror instead of through the key path.
        if (mode == VimMode.Insert || mode == VimMode.Replace)
        {
            EnsureOverwriteOff();

            // Unless the mirror is behind. A c-family command (cc, cw, s)
            // deletes text as it enters insert, and that deletion travels
            // ahead of the mode push on the wire, so cache == Insert means
            // the edit is already queued - but not yet applied. Typing into
            // Visual Studio now lands in the pre-deletion buffer and the
            // queued deletion wipes it; the echo of the letter (nvim
            // accepted it, at a clamped position) is then dropped as
            // self-originated. That is the "one typed letter jumps to the
            // start of the line" race. Route through nvim instead: it
            // inserts post-deletion at its own cursor, and the letter comes
            // back through the same ordered stream, behind the deletion.
            // Replace mode (R, gR) always goes through nvim: Visual Studio's
            // overwrite has none of Vim's rules (<BS> restoring the original,
            // gR replacing screen cells), and nobody needs completion there.
            var mirror = BufferMirror.TryGetForBuffer(_view.TextBuffer);
            if (InsertKeyDecision.ShouldRouteTypedText(
                mode, replace: mode == VimMode.Replace,
                hasUnappliedRemoteEdits: mirror != null && mirror.HasUnappliedRemoteEdits))
            {
                var routed = KeyEncoder.EncodeText(args.Text);
                if (routed != null)
                {
                    session.Input(routed);
                    args.Handled = true;
                }
            }

            return;
        }

        // u is Visual Studio's undo, deliberately never nvim's. VS's
        // ITextUndoHistory is authoritative (undo-a-Roslyn-rename has to
        // work), and nvim's own undo tree reaches all the way back to the
        // mirror's initial empty buffer: forwarded, u walks it down to
        // nothing and the mirror applies every step, emptying the file.
        // The edit lands in nvim through the mirror like any VS-side change.
        if (InsertKeyDecision.ShouldUndoText(mode, args.Text))
        {
            Infrastructure.Log.Key("  -> undo (VS-side)");
            ResetWhichKey();
            DropPendingCount(session);
            UndoRedo(undo: true);
            ReleaseCtrlO(session);
            args.Handled = true;
            return;
        }

        // Ctrl+Alt is AltGr on many layouts. KeyEncoder never claims the
        // chord in PreviewKeyDown (it leaves it unhandled, so this event
        // still fires), but a printable character produced by it is text:
        // passed to Visual Studio in normal mode, AltGr's } @ [ ] | ~ \
        // overwrote the character under the caret on German, French and
        // Nordic layouts. Only a non-printable result stays Visual Studio's.
        if (InsertKeyDecision.ShouldLeaveCtrlAltText(
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt))
                == (ModifierKeys.Control | ModifierKeys.Alt),
            string.IsNullOrEmpty(args.Text) || char.IsControl(args.Text[0])))
        {
            return;
        }

        var keys = KeyEncoder.EncodeText(args.Text);
        if (keys == null)
        {
            return;
        }

        // Peek prefixes in normal or visual mode: " starts register
        // selection, ` and ' start a mark jump. The fetch must go out
        // BEFORE the key: once nvim is parked waiting for the register or
        // mark character it stops servicing RPC requests, so a request
        // sent after the key is answered only when the sequence resolves -
        // far too late for a peek (verified: an nvim_exec_lua issued while
        // the mark char is pending never returns until the sequence ends).
        // Restricted to those modes deliberately: in the command line " is
        // a literal quote, and in operator-pending the popup would describe
        // a selection nobody is making.
        if (InsertKeyDecision.ShouldShowRegisterPeek(mode, args.Text))
        {
            FetchPeek(session, "registers");
        }
        else if (InsertKeyDecision.ShouldShowMarkPeek(mode, args.Text))
        {
            FetchPeek(session, "marks");
        }

        ForgetPendingInsertUndoStart(mode);
        session.Input(keys);
        args.Handled = true;
        TrackWhichKey(session, keys, mode);
    }

    /// <summary>
    /// Vim's insert-mode delete-word-backward, performed Visual Studio-side.
    ///
    /// Forwarding &lt;C-w&gt; for nvim to perform cannot work here: nvim
    /// deletes backward from *its* cursor, and its cursor cannot be pushed
    /// onto the caret reliably in insert mode. A push landing while nvim's
    /// copy is a character short gets clamped (after which the dedupe
    /// refuses to re-send that position, so the cursor stalls), and a push
    /// to one-past-the-end is itself clamped the moment the next key is
    /// processed - verified against nvim 0.12: an API-set cursor at the end
    /// of the line makes i_CTRL-W leave the last character standing.
    ///
    /// So nvim's only job is the word semantics ('iskeyword' and all): it
    /// computes the byte column i_CTRL-W would stop at, and the deletion
    /// happens here, where the typed text lives. The undo step lands in
    /// Visual Studio's history, an open completion list sees an ordinary
    /// edit, and the mirror carries the span to nvim like any typed text.
    /// </summary>
    private void DeleteWordBackward(NvimSession session)
    {
        var caret = _view.Caret.Position.BufferPosition;
        var line = caret.GetContainingLine();
        int charColumn = caret.Position - line.Start.Position;

        if (charColumn == 0)
        {
            // i_CTRL-W at the start of a line eats the line break itself
            // ('backspace' includes "start"). Nothing to ask nvim about.
            if (line.LineNumber == 0)
            {
                return;
            }

            var previous = line.Snapshot.GetLineFromLineNumber(line.LineNumber - 1);
            if (previous.LineBreakLength > 0)
            {
                _view.TextBuffer.Delete(new Span(previous.End.Position, previous.LineBreakLength));
            }

            return;
        }

        int byteColumn = ColumnMapper.CharToByte(line.GetText(), charColumn);
        int lineNumber = line.LineNumber;

        var boundary = session.RequestAsync(
            "nvim_exec_lua",
            "return vsneo.word_back_boundary(...)",
            new object[] { lineNumber + 1, byteColumn });

        var dispatcher = _view.VisualElement.Dispatcher;
        _ = boundary.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                // A faulted task always carries its exception, so this
                // dereference cannot be null; Log.Write only needs the object.
                Infrastructure.Log.Write("word-back boundary request failed",
                                         t.Exception!.GetBaseException());
                return;
            }

            if (t.Result == null)
            {
                return;
            }

            int targetByte = Convert.ToInt32(t.Result);

            // Posted at KeyResponse priority for the same measured reason as
            // the caret hop in CursorSynchronizer: an unjoined
            // SwitchToMainThreadAsync queues behind Visual Studio's background
            // work (373 ms average, measured), and a deletion that lands half a
            // second late reads as a hung key.
#pragma warning disable VSTHRD001
            // The DispatcherOperation result is deliberately unobserved: the
            // callback guards everything it touches and has nothing to report.
            _ = dispatcher.BeginInvoke(Infrastructure.UiPriority.KeyResponse, new Action(() =>
            {
                // The caret owns this deletion. If it moved since the
                // keypress - the typist carried on in the few milliseconds
                // the round trip took - the span nvim computed against is
                // no longer what is on screen, and deleting it would eat
                // live text. Dropping one chord beats that.
                if (_view.IsClosed)
                {
                    return;
                }

                var now = _view.Caret.Position.BufferPosition;
                var nowLine = now.GetContainingLine();
                if (nowLine.LineNumber != lineNumber
                    || now.Position - nowLine.Start.Position != charColumn)
                {
                    return;
                }

                int targetChar = ColumnMapper.ByteToChar(nowLine.GetText(), targetByte);
                if (targetChar < 0)
                {
                    targetChar = 0;
                }

                if (targetChar >= charColumn)
                {
                    return;
                }

                _view.TextBuffer.Delete(new Span(
                    nowLine.Start.Position + targetChar,
                    charColumn - targetChar));
            }));
#pragma warning restore VSTHRD001
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>
    /// One Vim undo/redo step against the view's own history. Runs on the UI
    /// thread (the key path is), which ITextUndoHistory requires. After the
    /// edit, the mirror carries the spans to nvim and the caret push follows
    /// from PositionChanged, so nvim's buffer and cursor track without its
    /// undo tree ever being touched.
    ///
    /// One Vim step is one Visual Studio step, except for the last insert
    /// session: there it walks every Visual Studio step between the states
    /// the mirror's InsertUndoGroup recorded at insert entry and exit, so
    /// c3wXYZ&lt;Esc&gt; then u restores the three words in one press, and
    /// &lt;C-r&gt; brings the whole change back. The walk stops early when a
    /// step fails to move the state the way an undo must (states run
    /// backwards along the undo path, forwards along redo) - the one
    /// guard against a history whose version numbering does not behave
    /// as the editor's does; then a single step was taken, as before.
    /// </summary>
    private void UndoRedo(bool undo)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            // u typed right after <Esc>: the insert session's end is posted
            // on the mode push and may not have landed. End it here.
            var mirror = BufferMirror.TryGetForBuffer(_view.TextBuffer);
            mirror?.EndInsertUndoGroup();

            if (!_undoRegistry.TryGetHistory(_view.TextBuffer, out var history))
            {
                history = _undoRegistry.RegisterHistory(_view.TextBuffer);
            }

            if (history == null)
            {
                return;
            }

            int State() => _view.TextBuffer.CurrentSnapshot.Version.ReiteratedVersionNumber;
            int? target = mirror?.InsertUndo.Target(State(), undo);

            // What the step changes, for the undo flash: the buffer raises
            // Changed synchronously inside Undo/Redo, one event per edit it
            // replays. Tracking spans carry each into the final snapshot.
            var changed = new List<ITrackingSpan>();
            EventHandler<TextContentChangedEventArgs> collect = (s, e) =>
            {
                foreach (var change in e.Changes)
                {
                    changed.Add(e.After.CreateTrackingSpan(change.NewSpan, SpanTrackingMode.EdgeInclusive));
                }
            };

            _view.TextBuffer.Changed += collect;
            try
            {
                bool Step()
                {
                    if (undo ? !history.CanUndo : !history.CanRedo)
                    {
                        return false;
                    }

                    if (undo)
                    {
                        history.Undo(1);
                    }
                    else
                    {
                        history.Redo(1);
                    }

                    return true;
                }

                if (target == null)
                {
                    Step();
                }
                else
                {
                    int before = State();
                    for (int steps = 0; steps < MaxGroupedUndoSteps && before != target; steps++)
                    {
                        if (!Step())
                        {
                            break;
                        }

                        int after = State();
                        // Undo must move the state back, redo forward; anything
                        // else means the numbering is not what the walk assumes.
                        bool moved = undo ? after < before : after > before;
                        bool overshot = undo ? after < target : after > target;
                        if (!moved || overshot)
                        {
                            Infrastructure.Log.Write("grouped " + (undo ? "undo" : "redo")
                                + " stopped: state " + before + " -> " + after + ", target " + target);
                            break;
                        }

                        before = after;
                    }
                }
            }
            finally
            {
                _view.TextBuffer.Changed -= collect;
            }

            UndoFlashAdornment.For(_view)?.Flash(changed);

            // Vim's rule: the cursor goes to the start of what the undo
            // changed. Visual Studio's own caret restore is only as good as
            // the history's caret records, and edits the mirror applied
            // have none - the caret just tracked through them.
            if (changed.Count > 0)
            {
                var snapshot = _view.TextBuffer.CurrentSnapshot;
                int first = int.MaxValue;
                foreach (var span in changed)
                {
                    first = Math.Min(first, span.GetStartPoint(snapshot).Position);
                }

                _view.Caret.MoveTo(new SnapshotPoint(snapshot, first));
            }

            // The mirror's spans shift nvim's cursor (undoing dd re-inserts
            // the line at the cursor row, pushing it one line down), and a
            // caret that lands where nvim's cursor was dedupes away - nvim's
            // shifted report then dragged the caret down a line. Forced, the
            // push goes out behind the spans (sent inside Changed).
            _cursorSync.SyncCaretToNvim(force: true);
        }
        catch (Exception ex)
        {
            // The key path has nothing above it to catch this, and a thrown
            // undo must not take the editor's input pipeline down with it.
            Infrastructure.Log.Write("undo/redo failed", ex);
        }
    }

    // Visual Studio undoes typing in word-sized steps, so a long insert is
    // many steps; the state checks above are the real guard, this only
    // bounds a walk that never reaches its target.
    private const int MaxGroupedUndoSteps = 1000;

    /// <summary>
    /// The block caret is Visual Studio's overwrite mode, and overwrite
    /// changes what typed text does. CursorSynchronizer switches it off on
    /// the mode-change notification, but that is a dispatcher hop behind the
    /// mode cache, which flips on the RPC thread - a fast typist (or a busy
    /// UI thread, measured at 100+ ms) otherwise gets characters that
    /// overwrite instead of insert, exactly as though Insert had been
    /// pressed. Cleared here, on the UI thread at the passthrough point, so
    /// no keystroke can reach the editor with overwrite still on. The
    /// option lookup is in-memory, so the zero-I/O key-path invariant holds.
    /// </summary>
    private void EnsureOverwriteOff()
    {
        try
        {
            if (_view.Options.GetOptionValue(
                    Microsoft.VisualStudio.Text.Editor.DefaultTextViewOptions.OverwriteModeId))
            {
                _view.Options.SetOptionValue(
                    Microsoft.VisualStudio.Text.Editor.DefaultTextViewOptions.OverwriteModeId, false);
            }
        }
        catch
        {
            // A guard against a cosmetic option must never eat a keystroke.
        }
    }
}
