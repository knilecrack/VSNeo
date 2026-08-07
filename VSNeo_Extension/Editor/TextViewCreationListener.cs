using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace VSNeo_Extension.Editor
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
        [Import]
        internal CursorSynchronizer CursorSync { get; set; }

        [Import]
        internal ViewportSynchronizer ViewportSync { get; set; }

        /// <summary>Resolves a text buffer back to the file on disk it came from.</summary>
        [Import]
        internal Microsoft.VisualStudio.Text.ITextDocumentFactoryService DocumentFactory { get; set; }

        /// <summary>Which document nvim's window is currently showing. UI thread only.</summary>
        private static Microsoft.VisualStudio.Text.ITextBuffer _shownBuffer;

        /// <summary>
        /// The path nvim should know this buffer by. Filetype detection, file marks
        /// and every plugin key off it, so an unnamed buffer is a buffer nvim cannot
        /// reason about. Not every buffer has one - scratch and output windows do not.
        /// </summary>
        private string PathOf(Microsoft.VisualStudio.Text.ITextBuffer buffer) =>
            DocumentFactory != null
            && DocumentFactory.TryGetTextDocument(buffer, out var document)
                ? document.FilePath
                : null;

        public void TextViewCreated(IWpfTextView textView)
        {
            var session = VSNeo_ExtensionPackage.Session;
            if (session == null) return;

            // Scope is the active editor only, so the mirror follows focus rather
            // than being built for every document that happens to open.
            textView.GotAggregateFocus += OnGotFocus;
            textView.Closed += (s, e) => textView.GotAggregateFocus -= OnGotFocus;
        }

        private void OnGotFocus(object sender, System.EventArgs e)
        {
            var view = (IWpfTextView)sender;
            var session = VSNeo_ExtensionPackage.Session;
            if (session == null || !session.IsReady)
            {
                // Focus arriving before nvim is up is normal, and refocusing the
                // view runs this again, so it self-heals. Worth a line because a
                // *permanently* unmirrored buffer looks identical from the outside.
                Infrastructure.Log.Write(
                    "focus before ready, mirror not attached (session="
                    + (session == null ? "null" : "notReady") + ")");
                return;
            }

            var buffer = view.TextBuffer;
            var mirror = buffer.Properties.GetOrCreateSingletonProperty(
                () => new BufferMirror(buffer, session, PathOf(buffer), CursorSync));

            CursorSync.SetActiveView(view);
            ViewportSync.SetActiveView(view);

            // Refocusing the document nvim is already showing costs nothing. Focus
            // bounces constantly - Solution Explorer, the find box, any tool window -
            // and each of those used to resend an entire file.
            if (ReferenceEquals(_shownBuffer, buffer))
            {
                CursorSync.SyncCaretToNvim();
                return;
            }

            // Each document has its own nvim buffer, so switching documents is now
            // just pointing nvim's window at the one that already holds this file.
            // The contents were sent once, when the buffer was created.
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    long handle = await mirror.EnsureCreatedAsync();

                    // One window, switching buffers, rather than a window per
                    // document. The jumplist and the alternate file live in the
                    // window, and in Vim they deliberately span files - a window each
                    // would give every document its own private jumplist, which is
                    // not how Vim behaves.
                    await session.RequestAsync("nvim_win_set_buf", 0, handle);

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    // Only now is nvim's window actually showing this document.
                    // Recording it earlier meant a failure here latched: the retry on
                    // the next focus was skipped, and nvim was left on the empty
                    // startup buffer for the rest of the session.
                    _shownBuffer = buffer;
                    CursorSync.SyncCaretToNvim(force: true);
                }
                catch (Exception ex)
                {
                    Infrastructure.Log.Write("could not show the document in nvim", ex);
                }
            });
        }
    }
}
