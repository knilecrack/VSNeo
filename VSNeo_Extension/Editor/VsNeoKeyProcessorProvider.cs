using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Configuration;
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

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// The earliest sanctioned interception point in the editor: WPF PreviewKeyDown,
    /// ahead of Visual Studio's command routing.
    ///
    /// Attachment is cheap and eager. Activation is async and lives in NvimSession.
    /// Nothing in this file may perform I/O or block.
    /// </summary>
    [Export(typeof(IKeyProcessorProvider))]
    [Name("VSNeoKeyProcessor")]
    [Order(Before = "VisualStudioKeyProcessor")]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class VsNeoKeyProcessorProvider : IKeyProcessorProvider
    {
        [Import]
        internal IntelliSenseGate Gate { get; set; } = null!;

        [Import]
        internal CursorSynchronizer CursorSync { get; set; } = null!;

        [Import]
        internal Microsoft.VisualStudio.Text.Operations.ITextUndoHistoryRegistry UndoRegistry { get; set; } = null!;

        public KeyProcessor GetAssociatedProcessor(IWpfTextView wpfTextView) =>
            wpfTextView.Properties.GetOrCreateSingletonProperty(
                () => new VsNeoKeyProcessor(wpfTextView, Gate, CursorSync, UndoRegistry));
    }

    internal sealed class VsNeoKeyProcessor : KeyProcessor
    {
        private readonly IWpfTextView _view;
        private readonly IntelliSenseGate _gate;
        private readonly CursorSynchronizer _cursorSync;
        private readonly Microsoft.VisualStudio.Text.Operations.ITextUndoHistoryRegistry _undoRegistry;
        // Pending mapping sequence for the which-key popup, in the exact
        // notation the keys were sent to nvim in; empty means "no prefix live".
        private string _whichKeyPrefix = string.Empty;

        // i_CTRL-O state, UI thread only: armed when <C-o> is sent from insert,
        // disarmed once the hub's mode sequence shows the i -> niI -> i round
        // trip completed. See ResolveCtrlO.
        private bool _ctrlOPending;
        private int _ctrlOArmSequence;
        private int _ctrlOArmTicks;

        public VsNeoKeyProcessor(IWpfTextView view, IntelliSenseGate gate,
            CursorSynchronizer cursorSync,
            Microsoft.VisualStudio.Text.Operations.ITextUndoHistoryRegistry undoRegistry)
        {
            _view = view;
            _gate = gate;
            _cursorSync = cursorSync;
            _undoRegistry = undoRegistry;
        }

        /// <summary>
        /// Read late, never cached. The package loads in the background and the
        /// editor is up long before it finishes, so a processor built at view
        /// creation would capture a null session and keep it for the life of the
        /// view - every key passing through to VS forever, even after nvim is
        /// connected and healthy. Reading the static costs nothing and is not I/O,
        /// so the zero-I/O invariant on this path still holds.
        /// </summary>
        private NvimSession Session => VSNeo_ExtensionPackage.Session;

        /// <summary>
        /// Named keys and modified chords: Esc, CR, arrows, &lt;C-w&gt;. Anything that
        /// has no character to speak of, or whose character would be wrong.
        /// </summary>
        public override void PreviewKeyDown(KeyEventArgs args)
        {
            // WPF key events arrive on the UI thread; SyncCaretToNvim requires
            // it, and stating it lets the analyzer prove the call below.
            ThreadHelper.ThrowIfNotOnUIThread();

            // Snapshot once: the session can be swapped out by a reconnect, and a
            // half-handled key is worse than one we never claimed.
            var session = Session;

            Infrastructure.Log.Key("PreviewKeyDown " + args.Key
                + (args.Key == Key.System ? "/" + args.SystemKey : "")
                + " mods=" + Keyboard.Modifiers
                + " mode=" + (session == null ? "<no session>" : session.State.Mode.ToString())
                + " ready=" + (session?.IsReady == true)
                + " focus=" + _view.HasAggregateFocus);

            // Ctrl+W in insert is claimed even while a completion list is open:
            // the list has no use for the chord and Visual Studio's own binding
            // is long gone (KeyBindingCleaner), so passing it through would make
            // the key do nothing at all - which is exactly what happened while
            // typing fresh text, where C# completion is almost always up.
            // Not while a <C-o> excursion is pending: there Ctrl+W is nvim's
            // window-command prefix, not delete-word-backward.
            if (session != null && session.IsReady && IsDocumentView && _view.HasAggregateFocus
                && !ForeignFocus()
                && !_ctrlOPending
                && (session.State.Mode == VimMode.Insert || session.State.Mode == VimMode.Replace)
                && args.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Infrastructure.Log.Key("  -> <C-w> delete word backward (VS-side)");
                DeleteWordBackward(session);
                args.Handled = true;
                return;
            }

            if (!ShouldIntercept(session!)) return;

            if (session is null)
            {
                Infrastructure.Log.Key("Session is null");
                return;
            }
            VimMode mode = ResolveCtrlO(session, session.State.Mode);

            // Insert mode passes through so IntelliSense, snippets, and brace
            // completion keep working. Only Escape and Ctrl+O are still claimed:
            if (mode is VimMode.Insert or VimMode.Replace)
            {
                EnsureOverwriteOff();
                if (args.Key == Key.Escape)
                {
                    // Normally the command filter sees Escape (VS turns it into
                    // a command before WPF does) and syncs the caret there; if
                    // the key ever reaches this branch it needs the same.
                    Infrastructure.Log.Key("  -> sending <Esc> to leave insert");
                    _cursorSync.SyncCaretToNvim(force: true);
                    session.Input("<Esc>");
                    args.Handled = true;
                }
                else if (args.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    // i_CTRL-O: one normal-mode command, then back to insert.
                    // File.OpenFile is already unbound (KeyBindingCleaner), so
                    // the chord reaches WPF. The arming covers the gap before
                    // the niI push lands - see ResolveCtrlO.
                    //
                    // Sync the caret first, for the same reason Escape does
                    // (VsNeoCommandFilter): Visual Studio owned the caret for
                    // the whole insert session, so nvim's cursor is a lagging
                    // echo - and the command about to run is relative to it.
                    Infrastructure.Log.Key("  -> <C-o> one normal-mode command");
                    _cursorSync.SyncCaretToNvim(force: true);
                    session.Input("<C-o>");
                    _ctrlOPending = true;
                    _ctrlOArmSequence = session.State.ModeSequence;
                    _ctrlOArmTicks = Environment.TickCount;
                    args.Handled = true;
                }
                else
                {
                    // User-declared insert mappings on named keys WPF still
                    // raises (chords VS never made a command of). Arrows,
                    // Home/End and friends take the command-filter half of
                    // this path. The pushed set holds only the user's own
                    // mappings, so an unmapped key passes through untouched.
                    var mapped = KeyEncoder.Encode(args);
                    if (mapped != null && session.State.IsInsertMapped(mapped))
                    {
                        Infrastructure.Log.Key("  -> insert map " + mapped);
                        _cursorSync.SyncCaretToNvim(force: true);
                        _cursorSync.AllowNextInsertApply();
                        session.Input(mapped);
                        args.Handled = true;
                    }
                }
                return;
            }

            // Ctrl+R is redo, performed Visual Studio-side like u (see TextInput):
            // nvim's redo tree mirrors its undo tree, and neither is authoritative.
            if (mode == VimMode.Normal
                && args.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Infrastructure.Log.Key("  -> redo (VS-side)");
                ResetWhichKey();
                UndoRedo(undo: false);
                ReleaseCtrlO(session);
                args.Handled = true;
                return;
            }

            var keys = KeyEncoder.Encode(args);
            if (keys == null) return;

            session.Input(keys);
            args.Handled = true;
            TrackWhichKey(session, keys, mode);
        }

        /// <summary>
        /// Effective routing mode while an i_CTRL-O excursion is live. nvim
        /// reports the excursion as niI, which the hub maps to Normal, so once
        /// the push lands ordinary routing takes over - but the very next key
        /// IS the command, and a fast typist beats the push by milliseconds.
        /// While armed with the cache still saying insert, route as normal
        /// anyway, or the command passes through as typed text.
        ///
        /// Disarming counts transitions, not values: the i -> niI -> i round
        /// trip is two effective-mode changes, and both pushes can land while
        /// the UI thread is busy, so by the next key the mode already reads
        /// "Insert" again - indistinguishable from "the excursion never
        /// happened" without the sequence. The tick guard drops the flag when
        /// the niI push never comes at all (an inoremap on &lt;C-o&gt;, an error
        /// swallowing the chord): without it the whole insert session would
        /// route to nvim as normal-mode keys forever.
        /// </summary>
        private VimMode ResolveCtrlO(NvimSession session, VimMode mode)
        {
            if (!_ctrlOPending) return mode;
            if (mode is not (VimMode.Insert or VimMode.Replace)) return mode;

            int sequence = session.State.ModeSequence;
            if (sequence >= _ctrlOArmSequence + 2
                || (sequence == _ctrlOArmSequence
                    && unchecked(Environment.TickCount - _ctrlOArmTicks) > 2000))
            {
                _ctrlOPending = false;
                return mode;
            }
            return VimMode.Normal;
        }

        /// <summary>
        /// A VS-side command consumed the one normal command nvim is waiting
        /// for (u, Ctrl+R): nothing will ever arrive, so release nvim from niI
        /// back into insert. Escape in niI cancels the pending command without
        /// leaving insert - exactly the semantics i_CTRL-O promises.
        /// </summary>
        private void ReleaseCtrlO(NvimSession session)
        {
            if (!_ctrlOPending) return;
            session.Input("<Esc>");
        }

        /// <summary>
        /// One undo/redo step against the view's own history. Runs on the UI
        /// thread (the key path is), which ITextUndoHistory requires. After the
        /// edit, the mirror carries the spans to nvim and the caret push follows
        /// from PositionChanged, so nvim's buffer and cursor track without its
        /// undo tree ever being touched.
        /// </summary>
        private void UndoRedo(bool undo)
        {
            try
            {
                if (!_undoRegistry.TryGetHistory(_view.TextBuffer, out var history))
                    history = _undoRegistry.RegisterHistory(_view.TextBuffer);
                if (history == null) return;

                if (undo)
                {
                    if (history.CanUndo) history.Undo(1);
                }
                else
                {
                    if (history.CanRedo) history.Redo(1);
                }
            }
            catch (Exception ex)
            {
                // The key path has nothing above it to catch this, and a thrown
                // undo must not take the editor's input pipeline down with it.
                Infrastructure.Log.Write("undo/redo failed", ex);
            }
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
                if (line.LineNumber == 0) return;
                var previous = line.Snapshot.GetLineFromLineNumber(line.LineNumber - 1);
                if (previous.LineBreakLength > 0)
                    _view.TextBuffer.Delete(new Span(previous.End.Position, previous.LineBreakLength));
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
                if (t.Result == null) return;
                int targetByte = Convert.ToInt32(t.Result);

                // Posted at Input priority for the same measured reason as the
                // caret hop in CursorSynchronizer: an unjoined SwitchToMainThreadAsync
                // queues behind Visual Studio's background work (373 ms average,
                // measured), and a deletion that lands half a second late reads as
                // a hung key.
#pragma warning disable VSTHRD001
                // The DispatcherOperation result is deliberately unobserved: the
                // callback guards everything it touches and has nothing to report.
                _ = dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    // The caret owns this deletion. If it moved since the
                    // keypress - the typist carried on in the few milliseconds
                    // the round trip took - the span nvim computed against is
                    // no longer what is on screen, and deleting it would eat
                    // live text. Dropping one chord beats that.
                    if (_view.IsClosed) return;

                    var now = _view.Caret.Position.BufferPosition;
                    var nowLine = now.GetContainingLine();
                    if (nowLine.LineNumber != lineNumber
                        || now.Position - nowLine.Start.Position != charColumn)
                        return;

                    int targetChar = ColumnMapper.ByteToChar(nowLine.GetText(), targetByte);
                    if (targetChar < 0) targetChar = 0;
                    if (targetChar >= charColumn) return;

                    _view.TextBuffer.Delete(new Span(
                        nowLine.Start.Position + targetChar,
                        charColumn - targetChar));
                }));
#pragma warning restore VSTHRD001
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

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
            var session = Session;
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
            var mode = ResolveCtrlO(session, session.State.Mode);
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
                var mirror = BufferMirror.TryGetForBuffer(_view.TextBuffer);
                if (mirror != null && mirror.HasUnappliedRemoteEdits)
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
            if (mode == VimMode.Normal && args.Text == "u")
            {
                Infrastructure.Log.Key("  -> undo (VS-side)");
                ResetWhichKey();
                UndoRedo(undo: true);
                ReleaseCtrlO(session);
                args.Handled = true;
                return;
            }

            // Ctrl+Alt is AltGr on many layouts, and WPF raises TextInput for the
            // character it produces. KeyEncoder never claims Ctrl+Alt(+Shift)
            // chords, and the same rule has to hold here: the produced character
            // belongs to Visual Studio, not to nvim.
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt))
                    == (ModifierKeys.Control | ModifierKeys.Alt))
                return;

            var keys = KeyEncoder.EncodeText(args.Text);
            if (keys == null) return;

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
            if (mode == VimMode.Normal || mode == VimMode.Visual)
            {
                if (args.Text == "\"") FetchPeek(session, "registers");
                else if (args.Text == "`" || args.Text == "'") FetchPeek(session, "marks");
            }

            session.Input(keys);
            args.Handled = true;
            TrackWhichKey(session, keys, mode);
        }

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
                    _view.Options.SetOptionValue(
                        Microsoft.VisualStudio.Text.Editor.DefaultTextViewOptions.OverwriteModeId, false);
            }
            catch
            {
                // A guard against a cosmetic option must never eat a keystroke.
            }
        }

        /// <summary>
        /// Feeds the which-key popup. The key just went to nvim; if the sequence
        /// typed so far is a strict prefix of some mapping, the popup's delay
        /// timer (re)arms, and a completed or dead sequence cancels it. The
        /// lookup is a scan of the hub's cached mapping table - no I/O, which
        /// keeps the key-path invariant intact.
        ///
        /// Counts and operators muddy the picture deliberately little: a count
        /// digit or a d/y/c that leads nowhere simply fails the prefix test and
        /// resets, which is the right answer - the hint is about mappings, not
        /// about Vim grammar.
        /// </summary>
        private void TrackWhichKey(NvimSession session, string keys, VimMode mode)
        {
            // A sent key resolves any pending mark peek (the mark letter, or
            // Escape aborting it); the popup ignores this when none is up.
            if (_view.Properties.TryGetProperty(typeof(PeekPopup), out PeekPopup peek))
                peek.OnKeySent();

            if (mode != VimMode.Normal && mode != VimMode.Visual)
            {
                _whichKeyPrefix = string.Empty;
                return;
            }

            var candidate = _whichKeyPrefix + keys;
            bool hasChildren;
            try
            {
                hasChildren = session.State.HasKeymapChildren(mode, candidate);
            }
            catch (Exception ex)
            {
                // A hint lookup must never eat a keystroke's follow-through.
                Infrastructure.Log.Write("which-key lookup failed", ex);
                hasChildren = false;
            }

            if (hasChildren)
            {
                _whichKeyPrefix = candidate;
                if (_view.Properties.TryGetProperty(typeof(WhichKeyPopup), out WhichKeyPopup popup))
                    popup.Track(candidate, mode);
            }
            else
            {
                ResetWhichKey();
            }
        }

        private void ResetWhichKey()
        {
            _whichKeyPrefix = string.Empty;
            if (_view.Properties.TryGetProperty(typeof(WhichKeyPopup), out WhichKeyPopup popup))
                popup.Cancel();
        }

        /// <summary>
        /// Fire-and-forget fetch for a peek (registers or marks). The decision
        /// to fetch is local (cached mode, typed character); the round trip is
        /// async and its result is informational, so the zero-I/O key-path
        /// invariant holds. Dismissal is not tracked here at all: nvim clears
        /// showcmd when the pending prefix resolves, and the popup hears
        /// ShowCmdChanged itself.
        /// </summary>
        private void FetchPeek(NvimSession session, string what)
        {
            var requestedAt = Environment.TickCount;
            var request = session.RequestAsync(
                "nvim_exec_lua",
                "return vsneo." + what + "()",
                Array.Empty<object>());

            var dispatcher = _view.VisualElement.Dispatcher;
            _ = request.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    // A faulted task always carries its exception, so this
                    // dereference cannot be null; Log.Write only needs the object.
                    Infrastructure.Log.Write(what + " request failed",
                                             t.Exception!.GetBaseException());
                    return;
                }
                if (!(t.Result is object[] rows)) return;

                var parsed = new List<string[]>(rows.Length);
                foreach (var row in rows)
                {
                    if (row is object[] pair && pair.Length >= 2)
                    {
                        var name = AsString(pair[0]);
                        var preview = AsString(pair[1]);
                        if (name != null && preview != null)
                            parsed.Add(new[] { name, preview });
                    }
                }
                if (parsed.Count == 0) return;

#pragma warning disable VSTHRD001
                // The DispatcherOperation result is deliberately unobserved:
                // the show methods guard staleness themselves and have nothing
                // to report.
                _ = dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (_view.IsClosed) return;
                    if (_view.Properties.TryGetProperty(typeof(PeekPopup), out PeekPopup popup))
                    {
                        if (what == "marks") popup.ShowMarks(parsed, requestedAt);
                        else popup.ShowRows(parsed, requestedAt);
                    }
                }));
#pragma warning restore VSTHRD001
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        /// <summary>msgpack strings may arrive as byte[]; the hub's AsString twin.</summary>
        private static string AsString(object value)
        {
            if (value is string s) return s;
            if (value is byte[] b) return System.Text.Encoding.UTF8.GetString(b);
            return null!;
        }

        /// <summary>
        /// Only document views are ours. The C# Interactive window is an editable
        /// text view too, and it owns its keystrokes outright: with the mode cache
        /// sitting at Normal, every character typed into the REPL was swallowed
        /// and forwarded to nvim. Mirrors attach under this same condition (the
        /// creation listener exports Document-role only), so the two cannot drift.
        /// </summary>
        private bool IsDocumentView =>
            _view.Roles.Contains(PredefinedTextViewRoles.Document);

        /// <summary>
        /// Every reason to hand input straight back to Visual Studio, and not one of
        /// them costs a round trip. Shared so the two entry points cannot drift.
        /// </summary>
        private bool ShouldIntercept(NvimSession session)
        {
            if (!IsDocumentView) return false;
            if (session == null || !session.IsReady) return false;
            if (!_view.HasAggregateFocus) return false;
            if (ForeignFocus()) return false;

            // The gate protects Visual Studio's popup UI while *typing*, so it
            // is scoped to insert/replace. In normal mode a signature tooltip
            // can be open with no typing going on at all - K mapped to
            // Edit.QuickInfo + Edit.ParameterInfo is the stock example - and
            // passing hjkl through there typed them into the buffer instead of
            // moving the caret. Escape is unaffected by the scoping: it never
            // reaches this processor, the command filter consults the gate
            // itself.
            if (session.State.Mode is VimMode.Insert or VimMode.Replace
                && IsIntelliSenseActive()) return false;
            return true;
        }

        /// <summary>
        /// True when focus sits in a Visual Studio control hosted inside the view
        /// rather than on the editor surface itself. HasAggregateFocus cannot tell
        /// the two apart: Roslyn's rename dashboard is a TextBox in an adornment
        /// layer, so the view still reports aggregate focus while it is open.
        ///
        /// It matters because PreviewKeyDown tunnels through the view visual -
        /// where this processor runs - before the focused control ever sees the
        /// key. Intercepting there ate the rename dashboard's Enter and fed nvim
        /// a &lt;CR&gt;, which in normal mode just moves the caret down. TextInput
        /// bubbles instead, so the TextBox already wins for typed characters;
        /// named keys were the ones being stolen.
        /// </summary>
        private bool ForeignFocus()
        {
            var focused = Keyboard.FocusedElement;
            return focused != null && !ReferenceEquals(focused, _view.VisualElement);
        }

        /// <summary>
        /// While a completion list or signature help is open, j and k belong to
        /// that list rather than to nvim, and Escape has to be able to dismiss it.
        /// Callers scope this to insert/replace; in normal mode an open tooltip
        /// does not suspend Vim semantics (see ShouldIntercept).
        /// </summary>
        private bool IsIntelliSenseActive() => _gate != null && _gate.IsActive(_view);
    }
}
