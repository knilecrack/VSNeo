using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// The second interception point, and the one that gets Escape.
    ///
    /// The KeyProcessor sees WPF key events. Visual Studio never raises one for
    /// Escape: the shell translates it into VSStd2K CANCEL in the message pump and
    /// routes it through the IOleCommandTarget chain, so PreviewKeyDown is simply
    /// never called. The key trace shows this plainly - not one Escape event while
    /// the mode cache sat in Insert forever. Ctrl+[ is the same story, and CLAUDE.md
    /// already lists it.
    ///
    /// So there are two interception points by necessity, not by preference:
    /// WPF for characters and chords, the command filter for keys VS has already
    /// claimed as commands. Everything not explicitly claimed here is forwarded
    /// untouched.
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class VsNeoCommandFilterProvider : IVsTextViewCreationListener
    {
        [Import]
        internal IVsEditorAdaptersFactoryService AdapterFactory { get; set; }

        [Import]
        internal IntelliSenseGate Gate { get; set; }

        [Import]
        internal CursorSynchronizer CursorSync { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            var view = AdapterFactory.GetWpfTextView(textViewAdapter);
            if (view == null) return;

            // AddCommandFilter must happen on the UI thread; VsTextViewCreated is
            // already there. Bookkeeping only - no service resolution, no RPC.
            view.Properties.GetOrCreateSingletonProperty(
                () => new VsNeoCommandFilter(textViewAdapter, view, Gate, CursorSync));
        }
    }

    internal sealed class VsNeoCommandFilter : IOleCommandTarget
    {
        private readonly IWpfTextView _view;
        private readonly IntelliSenseGate _gate;
        private readonly CursorSynchronizer _cursorSync;
        private readonly IOleCommandTarget _next;

        public VsNeoCommandFilter(
            IVsTextView adapter, IWpfTextView view, IntelliSenseGate gate, CursorSynchronizer cursorSync)
        {
            _view = view;
            _gate = gate;
            _cursorSync = cursorSync;
            adapter.AddCommandFilter(this, out _next);
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (IsCancel(pguidCmdGroup, nCmdID) && TryHandleEscape(out bool swallow) && swallow)
                return VSConstants.S_OK;

            return Forward(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        private static bool IsCancel(Guid group, uint id) =>
            group == VSConstants.VSStd2K && id == (uint)VSConstants.VSStd2KCmdID.CANCEL;

        /// <summary>
        /// Returns false when Escape is none of our business. When it is ours,
        /// <paramref name="swallow"/> decides whether Visual Studio still gets it.
        /// </summary>
        private bool TryHandleEscape(out bool swallow)
        {
            swallow = false;

            var session = VSNeo_ExtensionPackage.Session;
            if (session == null || !session.IsReady) return false;

            var mode = session.State.Mode;

            // A completion list has to be closable with Escape - but closing it and
            // consuming the key are different things. Handing Escape to IntelliSense
            // and stopping there is why leaving insert mode sometimes took two
            // presses: C# completion opens constantly while typing, so the first
            // Escape went to the list and only the second reached nvim. One Escape
            // should always land you in normal mode. nvim is told either way; VS is
            // additionally allowed to see the key when there is a list to dismiss,
            // so both things happen on the one press.
            bool listOpen = _gate.IsActive(_view);

            // Tell nvim where the caret actually is before asking it to leave insert.
            // Visual Studio handled every keystroke of that insert session on its
            // own, so nvim's cursor is only as current as the last push it managed to
            // apply - and Escape moves the cursor one column left relative to
            // wherever nvim thinks it is. Correcting the position first is what makes
            // that land on the character you were actually typing next to.
            if (mode == VimMode.Insert || mode == VimMode.Replace)
                _cursorSync?.SyncCaretToNvim(force: true);

            session.Input("<Esc>");
            Infrastructure.Log.Key(
                "CANCEL -> sent <Esc> to nvim, mode was " + mode + ", completion open=" + listOpen);

            // Swallowed only when nobody else needs it. Normal mode keeps Visual
            // Studio's own Escape behaviours - peek windows, light bulbs - because
            // there Escape is a Vim no-op that merely clears a pending count.
            swallow = !listOpen && mode != VimMode.Normal;
            return true;
        }

        private int Forward(ref Guid group, uint id, uint opt, IntPtr pvaIn, IntPtr pvaOut) =>
            _next == null
                ? (int)Constants.OLECMDERR_E_NOTSUPPORTED
                : _next.Exec(ref group, id, opt, pvaIn, pvaOut);

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) =>
            _next == null
                ? (int)Constants.OLECMDERR_E_NOTSUPPORTED
                : _next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
    }
}
