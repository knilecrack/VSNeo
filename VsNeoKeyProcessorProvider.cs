using System.ComponentModel.Composition;
using System.Windows.Input;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo.Nvim;

namespace VSNeo.Editor
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
        public KeyProcessor GetAssociatedProcessor(IWpfTextView wpfTextView) =>
            wpfTextView.Properties.GetOrCreateSingletonProperty(
                () => new VsNeoKeyProcessor(wpfTextView, VSNeoPackage.Session));
    }

    internal sealed class VsNeoKeyProcessor : KeyProcessor
    {
        private readonly IWpfTextView _view;
        private readonly NvimSession _session;

        public VsNeoKeyProcessor(IWpfTextView view, NvimSession session)
        {
            _view = view;
            _session = session;
        }

        public override void PreviewKeyDown(KeyEventArgs args)
        {
            // Ordering matters. Each of these is a reason to hand the key straight
            // back to Visual Studio, and none of them costs a round trip.
            if (_session == null || !_session.IsReady) return;
            if (!_view.HasAggregateFocus) return;
            if (IsIntelliSenseActive()) return;

            var mode = _session.State.Mode;

            // Insert mode passes through so IntelliSense, snippets, and brace
            // completion keep working. Escape is the one key we still claim.
            if (mode == VimMode.Insert || mode == VimMode.Replace)
            {
                if (args.Key != Key.Escape) return;
                _session.Input("<Esc>");
                args.Handled = true;
                return;
            }

            var keys = KeyEncoder.Encode(args);
            if (keys == null) return;

            _session.Input(keys);
            args.Handled = true;
        }

        /// <summary>
        /// Placeholder for milestone 1. Wire this to ICompletionBroker and
        /// ISignatureHelpBroker before the first person tries to accept a
        /// completion with j and k.
        /// </summary>
        private bool IsIntelliSenseActive() => false;
    }
}
