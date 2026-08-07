using System.ComponentModel.Composition;
using System.Windows.Input;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
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
                + " ready=" + (session != null && session.IsReady)
                + " focus=" + _view.HasAggregateFocus);

            if (!ShouldIntercept(session)) return;

            var mode = session.State.Mode;

            // Insert mode passes through so IntelliSense, snippets, and brace
            // completion keep working. Escape is the one key we still claim.
            if (mode == VimMode.Insert || mode == VimMode.Replace)
            {
                if (args.Key != Key.Escape) return;
                Infrastructure.Log.Key("  -> sending <Esc> to leave insert");
                session.Input("<Esc>");
                args.Handled = true;
                return;
            }

            var keys = KeyEncoder.Encode(args);
            if (keys == null) return;

            session.Input(keys);
            args.Handled = true;
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
        /// Every reason to hand input straight back to Visual Studio, and not one of
        /// them costs a round trip. Shared so the two entry points cannot drift.
        /// </summary>
        private bool ShouldIntercept(NvimSession session)
        {
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
