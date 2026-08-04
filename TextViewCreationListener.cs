using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace VSNeo.Editor
{
    /// <summary>
    /// This ran eagerly and froze Visual Studio once already. The rule now: this
    /// method does bookkeeping only. No service resolution, no process start, no
    /// RPC, no JoinableTaskFactory.Run, no .Result, no .Wait().
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class TextViewCreationListener : IWpfTextViewCreationListener
    {
        public void TextViewCreated(IWpfTextView textView)
        {
            var session = VSNeoPackage.Session;
            if (session == null) return;

            // Scope is the active editor only, so the mirror follows focus rather
            // than being built for every document that happens to open.
            textView.GotAggregateFocus += OnGotFocus;
            textView.Closed += (s, e) => textView.GotAggregateFocus -= OnGotFocus;
        }

        private void OnGotFocus(object sender, System.EventArgs e)
        {
            var view = (IWpfTextView)sender;
            var session = VSNeoPackage.Session;
            if (session == null || !session.IsReady) return;

            var mirror = view.TextBuffer.Properties.GetOrCreateSingletonProperty(
                () => new BufferMirror(view.TextBuffer, session));

            _ = mirror.PrimeAsync();
        }
    }
}
