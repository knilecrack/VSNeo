using System;
using System.Threading;
using System.Windows.Input;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
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
        internal IVsEditorAdaptersFactoryService AdapterFactory { get; set; } = null!;

        [Import]
        internal IntelliSenseGate Gate { get; set; } = null!;

        [Import]
        internal CursorSynchronizer CursorSync { get; set; } = null!;

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

        // The filter of the document view holding keyboard focus, for
        // KeyPriorityTarget, which is global and has no view of its own.
        // Written from the view's focus events (UI thread), read on the UI
        // thread too; volatile only so no stale copy outlives a focus change.
        private static volatile VsNeoCommandFilter? _focused;
        internal static VsNeoCommandFilter? Focused => _focused;

        // TickCount when KeyPriorityTarget handled this view's Escape; 0 when
        // none is outstanding. The same keystroke then reaches Exec here - unless
        // it was swallowed, or a filter ahead of this one took it - and must not
        // be sent to nvim a second time. Expires, so a claim whose keystroke
        // never arrived cannot swallow a later, unrelated Escape.
        private int _escapeClaimedAt;
        private const int EscapeClaimWindowMs = 500;

        public VsNeoCommandFilter(
            IVsTextView adapter, IWpfTextView view, IntelliSenseGate gate, CursorSynchronizer cursorSync)
        {
            _view = view;
            _gate = gate;
            _cursorSync = cursorSync;
            adapter.AddCommandFilter(this, out _next);

            if (view.HasAggregateFocus) _focused = this;
            view.GotAggregateFocus += OnGotFocus;
            view.LostAggregateFocus += OnLostFocus;
            view.Closed += OnClosed;
        }

        private void OnGotFocus(object sender, EventArgs e) => _focused = this;

        private void OnLostFocus(object sender, EventArgs e)
        {
            if (ReferenceEquals(_focused, this)) _focused = null;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            OnLostFocus(sender, e);
            _view.GotAggregateFocus -= OnGotFocus;
            _view.LostAggregateFocus -= OnLostFocus;
            _view.Closed -= OnClosed;
        }

        /// <summary>
        /// Escape as KeyPriorityTarget sees it, ahead of every command filter
        /// on the view. Claimed only in insert/replace, only on a document view
        /// whose editor surface really has focus, and never while an overlay
        /// owns the keys; everything else keeps its ordinary route through
        /// Exec. Returns true when handled; <paramref name="swallow"/> is the
        /// same decision Exec makes (a completion list still gets the key).
        /// </summary>
        /// <summary>
        /// The arrows, Home/End, PageUp/PageDown, Delete and Backspace in
        /// normal, visual and operator-pending mode, claimed from the shell's
        /// priority target - ahead of every filter in the view's chain, for
        /// the reason Escape is: a filter added after ours runs first, and one
        /// that handles RIGHT without forwarding it starves this filter of
        /// the key. c&lt;Right&gt; then left the c pending and the next letter
        /// completed it (ch, cl, ck, cc). The routes that outrank the
        /// normal-mode one in Exec keep their keys: the pager's, and the
        /// command line's history and wildmenu arrows.
        /// </summary>
        internal bool TryClaimNavigationKey(Guid group, uint id)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (NormalModeKeyFor(group, id) == null) return false;
            if (!_view.Roles.Contains(PredefinedTextViewRoles.Document) || _view.IsClosed) return false;
            if (MessagePager.OpenFor(_view) != null) return false;

            var session = VSNeo_ExtensionPackage.Session;
            if (session == null || !session.IsReady || session.State.OverlayActive) return false;

            // Mode and focus are checked inside; insert and command-line mode
            // decline, and the key takes the ordinary chain.
            if (!TryHandleNormalModeKey(group, id)) return false;
            Infrastructure.Log.Key("  (navigation key claimed by the priority target)");
            return true;
        }

        internal bool TryClaimInsertEscape(out bool swallow)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            swallow = false;

            if (!_view.Roles.Contains(PredefinedTextViewRoles.Document) || _view.IsClosed) return false;

            var focused = Keyboard.FocusedElement;
            if (focused == null || !ReferenceEquals(focused, _view.VisualElement)) return false;

            // The pager owns the keys while it is open (Escape closes it), and
            // Exec handles that - but this runs before Exec, and an insert-mode
            // pager is real: <C-o>:messages<CR> opens one and returns to insert.
            if (MessagePager.OpenFor(_view) != null) return false;

            var session = VSNeo_ExtensionPackage.Session;
            if (session == null || !session.IsReady || session.State.OverlayActive) return false;

            var mode = session.State.Mode;
            if (mode != VimMode.Insert && mode != VimMode.Replace) return false;

            if (!TryHandleEscape(out swallow)) return false;

            // The claim is only for a keystroke that will still pass through Exec
            // on its way to a completion list. A swallowed Escape never gets
            // there, so a claim for it was consumed by the *next* Escape instead -
            // forwarded to Visual Studio without reaching nvim, which left an
            // operator or count pending after <Esc>d<Esc> typed quickly.
            if (!swallow)
                Volatile.Write(ref _escapeClaimedAt, Environment.TickCount | 1);
            Infrastructure.Log.Key("  (Escape claimed by the priority target, swallow=" + swallow + ")");
            return true;
        }

        /// <summary>True, once, when this keystroke's Escape was already handled up front.</summary>
        private bool ConsumeEscapeClaim()
        {
            int at = Interlocked.Exchange(ref _escapeClaimedAt, 0);
            return at != 0 && unchecked(Environment.TickCount - at) < EscapeClaimWindowMs;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            // Visual Studio only ever routes commands to a view's filter chain on
            // the UI thread; stating it is what lets the analyzer prove every
            // main-thread-only access below.
            ThreadHelper.ThrowIfNotOnUIThread();

            // With VSNEO_TRACE_KEYS=1 this names every command Visual Studio routes
            // through the view. It is how to find out what a chord actually becomes -
            // Ctrl+D and Ctrl+U turn into commands that can be claimed here, while a
            // chord *prefix* like Ctrl+E fires nothing at all and simply waits for a
            // second key, which is a different problem needing a different fix.
            Infrastructure.Log.Key("Exec " + Describe(pguidCmdGroup, nCmdID));

            // Same eligibility rule as the key processor: only document views are
            // ours. The C# Interactive window is an editable text view too, and it
            // owns its keystrokes outright - in Normal mode we would otherwise
            // steal its paste (Ctrl+V) and every command-line key.
            if (!_view.Roles.Contains(PredefinedTextViewRoles.Document))
                return Forward(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);

            // Long command output on screen (MessagePager) takes the keys Visual
            // Studio makes commands of - Escape and Enter close it, the arrows
            // and paging keys scroll it - ahead of every other claim here.
            if (MessagePager.OpenFor(_view) is MessagePager pager)
            {
                var pagerKey = PagerKeyFor(pguidCmdGroup, nCmdID);
                if (pagerKey != null && pager.HandleKey(pagerKey))
                    return VSConstants.S_OK;
            }

            // An overlay interaction (jump labels, anything Lua drives) owns
            // the keys Visual Studio turns into commands before WPF can see
            // them, exactly as the command line does. Printable characters
            // already reach nvim through the key processor in Normal mode.
            var overlaySession = VSNeo_ExtensionPackage.Session;
            if (overlaySession != null && overlaySession.IsReady
                && overlaySession.State.OverlayActive)
            {
                if (IsCancel(pguidCmdGroup, nCmdID))
                {
                    overlaySession.Input("<Esc>");
                    return VSConstants.S_OK;
                }

                var overlayKeys = CmdLineKeyFor(pguidCmdGroup, nCmdID);
                if (overlayKeys != null)
                {
                    overlaySession.Input(overlayKeys);
                    return VSConstants.S_OK;
                }
            }

            if (TryHandleInsertMap(pguidCmdGroup, nCmdID))
                return VSConstants.S_OK;

            if (TryRouteBehindRemoteEdits(pguidCmdGroup, nCmdID))
                return VSConstants.S_OK;

            // Already sent to nvim by KeyPriorityTarget: the key is only
            // passing through now, on its way to a completion list.
            if (IsCancel(pguidCmdGroup, nCmdID) && ConsumeEscapeClaim())
                return Forward(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);

            if (IsCancel(pguidCmdGroup, nCmdID) && TryHandleEscape(out bool swallow) && swallow)
                return VSConstants.S_OK;

            if (IsPaste(pguidCmdGroup, nCmdID) && TryHandleBlockwise())
                return VSConstants.S_OK;

            // Visual Studio's own undo/redo (Ctrl+Z in insert mode): an open
            // insert-session transaction refuses them, so it is completed first.
            // The whole session then undoes as one step, as <C-o>u would in Vim.
            if (IsUndoOrRedo(pguidCmdGroup, nCmdID))
                BufferMirror.TryGetForBuffer(_view.TextBuffer)?.CloseInsertTransaction();

            if (TryHandleCmdLine(pguidCmdGroup, nCmdID))
                return VSConstants.S_OK;

            if (TryHandleNormalModeKey(pguidCmdGroup, nCmdID))
                return VSConstants.S_OK;

            return Forward(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        private static string Describe(Guid group, uint id)
        {
            if (group == VSConstants.VSStd2K)
                return "VSStd2K." + SafeName(typeof(VSConstants.VSStd2KCmdID), id);
            if (group == VSConstants.GUID_VSStandardCommandSet97)
                return "VSStd97." + SafeName(typeof(VSConstants.VSStd97CmdID), id);
            return group.ToString("D") + ":" + id;
        }

        private static string SafeName(Type enumType, uint id)
        {
            try
            {
                var name = Enum.GetName(enumType, (int)id);
                return name ?? id.ToString();
            }
            catch
            {
                return id.ToString();
            }
        }

        private static bool IsCancel(Guid group, uint id) =>
            group == VSConstants.VSStd2K && id == (uint)VSConstants.VSStd2KCmdID.CANCEL;

        private static bool IsPaste(Guid group, uint id) =>
            group == VSConstants.GUID_VSStandardCommandSet97 &&
            id == (uint)VSConstants.VSStd97CmdID.Paste;

        private static bool IsUndoOrRedo(Guid group, uint id) =>
            group == VSConstants.GUID_VSStandardCommandSet97 &&
            (id == (uint)VSConstants.VSStd97CmdID.Undo || id == (uint)VSConstants.VSStd97CmdID.Redo
             || id == (uint)VSConstants.VSStd97CmdID.MultiLevelUndo || id == (uint)VSConstants.VSStd97CmdID.MultiLevelRedo);

        /// <summary>
        /// Ctrl+V, claimed only where Vim wants it.
        ///
        /// Unlike Ctrl+E this does not need unbinding, because Paste is a real
        /// command and therefore reaches this filter - so it can be decided per
        /// keystroke rather than taken away wholesale. In normal and visual modes it
        /// starts a blockwise selection; in insert mode it stays Visual Studio's
        /// paste, which is where anyone actually reaches for it. Unbinding would have
        /// cost the paste everywhere to gain the block anywhere.
        /// </summary>
        private bool TryHandleBlockwise()
        {
            var session = VSNeo_ExtensionPackage.Session;
            if (session == null || !session.IsReady) return false;
            if (_gate.IsActive(_view)) return false;

            var mode = session.State.Mode;
            if (mode == VimMode.Insert || mode == VimMode.Replace) return false;

            session.Input("<C-v>");
            Infrastructure.Log.Key("Paste -> sent <C-v> to nvim, mode was " + mode);
            return true;
        }

        /// <summary>
        /// User-declared insert mappings on the keys Visual Studio turns into
        /// commands - arrows, Home/End, Enter, Backspace, Tab. The key
        /// processor never sees these (pre-translate routes them here), so
        /// 'imap &lt;left&gt; &lt;esc&gt;' would be dead without this claim.
        /// The pushed set holds only mappings from the user's own rc - nvim's
        /// defaults are filtered companion-side - so nothing is claimed for
        /// anyone who did not opt in. The key is fed back untouched and nvim
        /// runs the mapping itself.
        /// </summary>
        private bool TryHandleInsertMap(Guid group, uint id)
        {
            // Only reached from Exec, but the analyzer cannot see through the
            // call, so the UI-thread contract has to be restated here.
            ThreadHelper.ThrowIfNotOnUIThread();

            var session = VSNeo_ExtensionPackage.Session;
            if (session == null || !session.IsReady) return false;

            var mode = session.State.Mode;
            if (mode != VimMode.Insert && mode != VimMode.Replace) return false;

            // An open completion list or signature help owns these keys:
            // Up/Down pick an item, Enter/Tab commit it. The key processor's
            // half of this path stands down there too (ShouldIntercept).
            if (_gate.IsActive(_view)) return false;

            var keys = CmdLineKeyFor(group, id);
            if (keys == null || !session.State.IsInsertMapped(keys)) return false;

            // The rhs runs relative to nvim's cursor, which has lagged Visual
            // Studio's caret for the whole insert session - the same
            // correction Escape gets.
            _cursorSync?.SyncCaretToNvim(force: true);
            // A rhs that stays in insert can still move nvim's cursor
            // (imap <C-l> <Right>); let the next push land on the caret
            // despite the Visual-Studio-owns-the-caret rule.
            _cursorSync?.AllowNextInsertApply();
            session.Input(keys);
            Infrastructure.Log.Key("insert map -> sent " + keys + " to nvim");
            return true;
        }

        /// <summary>
        /// Enter and Backspace in the shadow of a pending remote edit, routed
        /// through nvim like the typed text around them.
        ///
        /// The key processor already routes typed characters through nvim while
        /// the mirror holds unapplied remote edits (a c-family deletion queued
        /// ahead of the mode push - see its TextInput branch). Enter and
        /// Backspace never reach that branch: Visual Studio turns them into
        /// commands first, and they land here. Left to Visual Studio they
        /// would edit the pre-deletion buffer while the letters typed before
        /// and after them go through nvim - so the queued deletion wipes them,
        /// or they land out of order. And every routed letter comes back as a
        /// remote edit, so once typing starts routing the window stays open
        /// while it continues: fast typing through a line break hits it.
        ///
        /// Same conditions as the character route: insert/replace only, only
        /// while the mirror is behind (an in-memory queue read, zero I/O), and
        /// never while an IntelliSense list owns the key (Enter commits a
        /// completion). The line break nvim inserts takes nvim's indenting,
        /// not Visual Studio's smart indent - acceptable for the few
        /// milliseconds this window lasts.
        /// </summary>
        private bool TryRouteBehindRemoteEdits(Guid group, uint id)
        {
            if (group != VSConstants.VSStd2K) return false;

            var session = VSNeo_ExtensionPackage.Session;
            if (session == null || !session.IsReady) return false;

            var mode = session.State.Mode;
            if (mode != VimMode.Insert && mode != VimMode.Replace) return false;

            // Replace mode (R, gR) is nvim's for good, as the key processor's
            // character route explains: every editing and cursor key goes with
            // the text, so <BS> restores and the cursor the text lands at is
            // nvim's own.
            string? keys;
            if (mode == VimMode.Replace)
            {
                keys = CmdLineKeyFor(group, id);
                if (keys == null) return false;
            }
            else
            {
                switch ((VSConstants.VSStd2KCmdID)id)
                {
                    case VSConstants.VSStd2KCmdID.RETURN: keys = "<CR>"; break;
                    case VSConstants.VSStd2KCmdID.BACKSPACE: keys = "<BS>"; break;
                    default: return false;
                }

                var mirror = BufferMirror.TryGetForBuffer(_view.TextBuffer);
                if (mirror == null || !mirror.HasUnappliedRemoteEdits) return false;
            }

            if (_gate.IsActive(_view)) return false;

            session.Input(keys);
            Infrastructure.Log.Key("behind remote edits -> sent " + keys + " to nvim");
            return true;
        }

        /// <summary>
        /// While the command line is open, every editing key belongs to nvim.
        ///
        /// This is the same problem Escape has, and it is why ":" could be opened and
        /// typed into but never run: Visual Studio turns Enter, Backspace, Tab and the
        /// arrows into commands in pre-translate, so the key processor is never called
        /// for them. Enter is the one that matters - without it a command line can be
        /// composed and has no way to be executed.
        ///
        /// Deliberately scoped to CmdLine mode. These are the editor's own keys
        /// everywhere else, and claiming Enter in normal mode would be a fine way to
        /// break the editor.
        ///
        /// One key is claimed but never sent: Backspace on an already-empty command
        /// line. Vim answers it by abandoning the command line, and a held Backspace
        /// then keeps arriving in normal mode. Keeping the popup open is the calmer
        /// contract - deleting stops at empty, and closing is what Escape is for.
        /// </summary>
        private bool TryHandleCmdLine(Guid group, uint id)
        {
            var session = VSNeo_ExtensionPackage.Session;
            if (session == null || !session.IsReady) return false;
            if (session.State.Mode != VimMode.CmdLine) return false;

            var keys = CmdLineKeyFor(group, id);
            if (keys == null) return false;

            if (id == (uint)VSConstants.VSStd2KCmdID.BACKSPACE
                && (session.State.CmdLine ?? string.Empty).Length == 0)
                return true;

            session.Input(keys);
            Infrastructure.Log.Key("cmdline -> sent " + keys + " to nvim");
            return true;
        }

        /// <summary>
        /// Navigation and deletion keys in the modes nvim owns: arrows,
        /// Home/End, PageUp/PageDown, Ctrl+arrows, Delete and Backspace.
        ///
        /// Visual Studio turns every one of these into a command before WPF
        /// raises a key event, so the key processor never sees them (the key
        /// trace shows VSStd2K.RIGHT and no PreviewKeyDown). Left unclaimed they
        /// ran as Visual Studio's own caret commands, and the moved caret reached
        /// nvim as a position, never as a motion. That broke every place Vim
        /// gives these keys meaning: v + arrows moved from the selection's
        /// exclusive end (VS's caret, one past nvim's cursor) and jumped;
        /// V + Down went two lines, from the start of the line after the
        /// selection; c3&lt;Right&gt; had no motion to consume; and Right at the
        /// end of a line wrapped onto the next, which Vim's default
        /// 'whichwrap' does not do. Sent as keys, nvim applies its own
        /// semantics and the caret follows as for any other motion.
        ///
        /// Backspace is here for its own reason: Vim's normal-mode &lt;BS&gt; is
        /// a motion, but Visual Studio's Edit.Backspace always deletes, and a
        /// held Backspace that closes an empty command line lands its repeats in
        /// normal mode, where they ate the buffer.
        ///
        /// Enter is not here on purpose: normal-mode Return still belongs to
        /// Visual Studio. Neither are Ctrl+Up/Down (view scrolls, which the
        /// viewport sync already carries to nvim). Insert mode is untouched -
        /// completion lists need these keys, and insert passthrough is the
        /// contract. The decision reads the cached mode and focus only; no I/O.
        /// </summary>
        private bool TryHandleNormalModeKey(Guid group, uint id)
        {
            var keys = NormalModeKeyFor(group, id);
            if (keys == null) return false;

            var session = VSNeo_ExtensionPackage.Session;
            if (session == null || !session.IsReady) return false;

            var mode = session.State.Mode;
            if (mode != VimMode.Normal && mode != VimMode.Visual && mode != VimMode.OperatorPending)
                return false;

            // A Visual Studio control hosted inside the view (Roslyn's rename
            // dashboard is a TextBox in an adornment layer) still routes its
            // editor commands through this filter; its arrows and Backspace are
            // the control's, not nvim's. Same rule as the key processor.
            var focused = Keyboard.FocusedElement;
            if (focused != null && !ReferenceEquals(focused, _view.VisualElement)) return false;

            session.Input(keys);
            Infrastructure.Log.Key("normal-mode key -> sent " + keys + " to nvim, mode was " + mode);
            return true;
        }

        /// <summary>
        /// Visual Studio's navigation commands as the keys that produced them.
        /// The _EXT variants are the Shift chords: Vim gives Shift+arrow a
        /// meaning of its own (word and page motions), so those keep the
        /// modifier; Shift+Home/End/PageUp/PageDown have none and go plain.
        /// Null for anything that is not a navigation or deletion key.
        /// </summary>
        private static string? NormalModeKeyFor(Guid group, uint id)
        {
            if (group != VSConstants.VSStd2K) return null;

            switch ((VSConstants.VSStd2KCmdID)id)
            {
                case VSConstants.VSStd2KCmdID.LEFT: return "<Left>";
                case VSConstants.VSStd2KCmdID.RIGHT: return "<Right>";
                case VSConstants.VSStd2KCmdID.UP: return "<Up>";
                case VSConstants.VSStd2KCmdID.DOWN: return "<Down>";
                case VSConstants.VSStd2KCmdID.LEFT_EXT: return "<S-Left>";
                case VSConstants.VSStd2KCmdID.RIGHT_EXT: return "<S-Right>";
                case VSConstants.VSStd2KCmdID.UP_EXT: return "<S-Up>";
                case VSConstants.VSStd2KCmdID.DOWN_EXT: return "<S-Down>";

                case VSConstants.VSStd2KCmdID.WORDPREV:
                case VSConstants.VSStd2KCmdID.WORDPREV_EXT: return "<C-Left>";
                case VSConstants.VSStd2KCmdID.WORDNEXT:
                case VSConstants.VSStd2KCmdID.WORDNEXT_EXT: return "<C-Right>";

                // Home is Edit.LineStart (BOL), or FIRSTCHAR under the smart-home
                // setting; Ctrl+Home/End are the document ends (HOME/END).
                case VSConstants.VSStd2KCmdID.BOL:
                case VSConstants.VSStd2KCmdID.BOL_EXT:
                case VSConstants.VSStd2KCmdID.FIRSTCHAR:
                case VSConstants.VSStd2KCmdID.FIRSTCHAR_EXT: return "<Home>";
                case VSConstants.VSStd2KCmdID.EOL:
                case VSConstants.VSStd2KCmdID.EOL_EXT: return "<End>";
                case VSConstants.VSStd2KCmdID.HOME:
                case VSConstants.VSStd2KCmdID.HOME_EXT: return "<C-Home>";
                case VSConstants.VSStd2KCmdID.END:
                case VSConstants.VSStd2KCmdID.END_EXT: return "<C-End>";

                case VSConstants.VSStd2KCmdID.PAGEUP:
                case VSConstants.VSStd2KCmdID.PAGEUP_EXT: return "<PageUp>";
                case VSConstants.VSStd2KCmdID.PAGEDN:
                case VSConstants.VSStd2KCmdID.PAGEDN_EXT: return "<PageDown>";

                case VSConstants.VSStd2KCmdID.DELETE: return "<Del>";
                case VSConstants.VSStd2KCmdID.BACKSPACE: return "<BS>";
                default: return null;
            }
        }

        /// <summary>
        /// The keys Visual Studio turns into commands, as the pager reads them.
        /// Backspace and Tab are listed so that they close the pager and carry
        /// on, like any key the pager has no use for.
        /// </summary>
        private static string? PagerKeyFor(Guid group, uint id)
        {
            if (group != VSConstants.VSStd2K) return null;

            switch ((VSConstants.VSStd2KCmdID)id)
            {
                case VSConstants.VSStd2KCmdID.CANCEL: return "<Esc>";
                case VSConstants.VSStd2KCmdID.RETURN: return "<CR>";
                case VSConstants.VSStd2KCmdID.UP: return "<Up>";
                case VSConstants.VSStd2KCmdID.DOWN: return "<Down>";
                case VSConstants.VSStd2KCmdID.LEFT: return "<Left>";
                case VSConstants.VSStd2KCmdID.RIGHT: return "<Right>";
                case VSConstants.VSStd2KCmdID.PAGEUP: return "<PageUp>";
                case VSConstants.VSStd2KCmdID.PAGEDN: return "<PageDown>";
                case VSConstants.VSStd2KCmdID.BOL:
                case VSConstants.VSStd2KCmdID.HOME: return "<Home>";
                case VSConstants.VSStd2KCmdID.EOL:
                case VSConstants.VSStd2KCmdID.END: return "<End>";
                case VSConstants.VSStd2KCmdID.BACKSPACE: return "<BS>";
                case VSConstants.VSStd2KCmdID.TAB: return "<Tab>";
                default: return null;
            }
        }

        /// <summary>
        /// Command-line editing keys, in nvim notation. History is <c>Up</c> and
        /// <c>Down</c>, completion is <c>Tab</c>, and the rest is ordinary line
        /// editing - which is what makes a long :%s/.../.../ correctable rather than
        /// something to be retyped from the start. Null when the command is not a
        /// command-line editing key at all.
        /// </summary>
        private static string? CmdLineKeyFor(Guid group, uint id)
        {
            if (group != VSConstants.VSStd2K) return null;

            switch ((VSConstants.VSStd2KCmdID)id)
            {
                case VSConstants.VSStd2KCmdID.RETURN: return "<CR>";
                case VSConstants.VSStd2KCmdID.BACKSPACE: return "<BS>";
                case VSConstants.VSStd2KCmdID.TAB: return "<Tab>";
                case VSConstants.VSStd2KCmdID.BACKTAB: return "<S-Tab>";
                case VSConstants.VSStd2KCmdID.DELETE: return "<Del>";
                case VSConstants.VSStd2KCmdID.LEFT: return "<Left>";
                case VSConstants.VSStd2KCmdID.RIGHT: return "<Right>";
                case VSConstants.VSStd2KCmdID.UP: return "<Up>";
                case VSConstants.VSStd2KCmdID.DOWN: return "<Down>";
                case VSConstants.VSStd2KCmdID.BOL: return "<Home>";
                case VSConstants.VSStd2KCmdID.EOL: return "<End>";
                default: return null;
            }
        }

        /// <summary>
        /// Returns false when Escape is none of our business. When it is ours,
        /// <paramref name="swallow"/> decides whether Visual Studio still gets it.
        /// </summary>
        private bool TryHandleEscape(out bool swallow)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
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

            // Opted out of the one-press rule (vim.g.vsneo_esc_closes_popup):
            // this Escape belongs to the popup alone and insert mode stays.
            // Not ours, so Exec forwards it to Visual Studio untouched and the
            // priority target does not claim it; the next Escape, with the
            // popup gone, leaves insert as usual.
            if (listOpen && session.State.EscClosesPopup
                && (mode == VimMode.Insert || mode == VimMode.Replace))
            {
                Infrastructure.Log.Key("CANCEL -> popup only (vsneo_esc_closes_popup), staying in " + mode);
                return false;
            }

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

        private int Forward(ref Guid group, uint id, uint opt, IntPtr pvaIn, IntPtr pvaOut)
        {
            // Only reached from Exec, but the analyzer cannot see through the call,
            // so the UI-thread contract has to be restated here.
            ThreadHelper.ThrowIfNotOnUIThread();
            return _next == null
                ? (int)Constants.OLECMDERR_E_NOTSUPPORTED
                : _next.Exec(ref group, id, opt, pvaIn, pvaOut);
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return _next == null
                ? (int)Constants.OLECMDERR_E_NOTSUPPORTED
                : _next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }
    }
}
