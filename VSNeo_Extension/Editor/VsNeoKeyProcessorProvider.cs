using System;
using System.ComponentModel.Composition;
using System.Configuration;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
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
        internal IntelliSenseGate Gate { get; set; }

        public KeyProcessor GetAssociatedProcessor(IWpfTextView wpfTextView) =>
            wpfTextView.Properties.GetOrCreateSingletonProperty(
                () => new VsNeoKeyProcessor(wpfTextView, Gate));
    }

    internal sealed class VsNeoKeyProcessor : KeyProcessor
    {
        private readonly IWpfTextView _view;
        private readonly IntelliSenseGate _gate;

        public VsNeoKeyProcessor(IWpfTextView view, IntelliSenseGate gate)
        {
            _view = view;
            _gate = gate;
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
            if (session != null && session.IsReady && IsDocumentView && _view.HasAggregateFocus
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
            VimMode mode = session.State.Mode;

            // Insert mode passes through so IntelliSense, snippets, and brace
            // completion keep working. Only Escape is still claimed:
            if (mode is VimMode.Insert or VimMode.Replace)
            {
                if (args.Key == Key.Escape)
                {
                    Infrastructure.Log.Key("  -> sending <Esc> to leave insert");
                    session.Input("<Esc>");
                    args.Handled = true;
                }
                return;
            }

            var keys = KeyEncoder.Encode(args);
            if (keys == null) return;

            session.Input(keys);
            args.Handled = true;
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
                    Infrastructure.Log.Write("word-back boundary request failed",
                                             t.Exception?.GetBaseException());
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
                dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
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
            if (!ShouldIntercept(session)) return;

            // Insert and replace belong to Visual Studio. The typed text reaches
            // nvim through the buffer mirror instead of through the key path.
            var mode = session.State.Mode;
            if (mode == VimMode.Insert || mode == VimMode.Replace) return;

            var keys = KeyEncoder.EncodeText(args.Text);
            if (keys == null) return;

            session.Input(keys);
            args.Handled = true;
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
            if (IsIntelliSenseActive()) return false;
            return true;
        }

        /// <summary>
        /// While a completion list or signature help is open, j and k belong to
        /// that list rather than to nvim, and Escape has to be able to dismiss it.
        /// </summary>
        private bool IsIntelliSenseActive() => _gate != null && _gate.IsActive(_view);
    }
}
