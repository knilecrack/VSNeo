using System;
using System.Threading;
using System.Windows.Input;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// Insert/replace claims and routing behind unapplied remote edits.
internal sealed partial class VsNeoCommandFilter
{
    /// <summary>
    /// The Paste command, which is two gestures: the Ctrl+V chord, and the
    /// paste a person asks for by other means (the context menu, Shift+Insert,
    /// <c>:Vsc Edit.Paste</c>). See <see cref="PasteDecision"/> for why the
    /// chord state is what separates them.
    ///
    /// Unlike Ctrl+E neither needs unbinding: Paste is a real command, so it
    /// reaches this filter and can be decided per use rather than taken away
    /// wholesale. In insert mode both stay Visual Studio's paste, which is where
    /// anyone actually reaches for it; unbinding would have cost the paste
    /// everywhere to gain the block anywhere.
    /// </summary>
    private bool TryHandlePaste(bool ctrlDown)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || !session.IsReady)
        {
            return false;
        }

        if (_gate.IsActive(_view))
        {
            return false;
        }

        // Only where Vim gives the key a meaning worth claiming. In CmdLine
        // <C-v> is literal-next - nothing pasted and the next Enter became ^M -
        // so paste there is TryHandleCmdLine's. Insert and replace keep Visual
        // Studio's, for the same reason typed text does.
        var mode = session.State.Mode;
        if (!PasteDecision.IsVimPaste(mode))
        {
            return false;
        }

        if (ctrlDown)
        {
            session.Input(PasteDecision.BlockwiseChord);
            Infrastructure.Log.Key("Paste -> sent " + PasteDecision.BlockwiseChord
                + " to nvim, mode was " + mode);
            return true;
        }

        // The command's paste goes through nvim, at nvim's cursor: in visual
        // mode that replaces the selection, which is what a right-click Paste
        // after a mouse drag means, and in operator-pending it cancels the
        // operator first, as Vim's p does. Visual Studio's own paste would
        // write at a caret nvim never hears about - the same reason replace
        // mode does not use it.
        return PasteThroughNvim(session, "paste");
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
        if (session == null || !session.IsReady)
        {
            return false;
        }

        var mode = session.State.Mode;
        if (mode != VimMode.Insert && mode != VimMode.Replace)
        {
            return false;
        }

        // An open completion list or signature help owns these keys:
        // Up/Down pick an item, Enter/Tab commit it. The key processor's
        // half of this path stands down there too (ShouldIntercept).
        if (_gate.IsActive(_view))
        {
            return false;
        }

        var keys = CmdLineKeyFor(group, id);
        if (keys == null || !session.State.IsInsertMapped(keys))
        {
            return false;
        }

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
    /// Same conditions as the character route: insert only (replace mode
    /// routes everything, see TryHandleReplaceKey), only
    /// while the mirror is behind (an in-memory queue read, zero I/O), and
    /// never while an IntelliSense list owns the key (Enter commits a
    /// completion). The line break nvim inserts takes nvim's indenting,
    /// not Visual Studio's smart indent - acceptable for the few
    /// milliseconds this window lasts.
    /// </summary>
    private bool TryRouteBehindRemoteEdits(Guid group, uint id)
    {
        if (group != VSConstants.VSStd2K)
        {
            return false;
        }

        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || !session.IsReady)
        {
            return false;
        }

        if (session.State.Mode != VimMode.Insert)
        {
            return false;
        }

        string keys;
        switch ((VSConstants.VSStd2KCmdID)id)
        {
            case VSConstants.VSStd2KCmdID.RETURN:
                keys = "<CR>";
                break;
            case VSConstants.VSStd2KCmdID.BACKSPACE:
                keys = "<BS>";
                break;
            default:
                return false;
        }

        var mirror = BufferMirror.TryGetForBuffer(_view.TextBuffer);
        if (mirror == null || !mirror.HasUnappliedRemoteEdits)
        {
            return false;
        }

        // Enter commits a completion.
        if (_gate.IsActive(_view))
        {
            return false;
        }

        session.Input(keys);
        Infrastructure.Log.Key("behind remote edits -> sent " + keys + " to nvim");
        return true;
    }

    /// <summary>
    /// Replace mode (R, gR) is nvim's for good, as the key processor's
    /// character route explains: every editing and cursor key goes with the
    /// text, so &lt;BS&gt; restores and the cursor the text lands at is
    /// nvim's own. The caret is never echoed back to nvim there, so any
    /// command left to Visual Studio that moves it (Ctrl+Right, PageDown,
    /// smart Home) split the two cursors, and the next character replaced
    /// at nvim's stale one. The commands with no Vim key are claimed too:
    /// Ctrl+Backspace is &lt;C-w&gt;, Ctrl+Delete does nothing (Vim has no
    /// forward word delete in replace), and Paste goes through nvim_paste.
    /// </summary>
    private bool TryHandleReplaceKey(Guid group, uint id)
    {
        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || !session.IsReady || session.State.Mode != VimMode.Replace)
        {
            return false;
        }

        // A control hosted in the view (Roslyn's inline rename box) owns
        // its keys; same rule as TryHandleNormalModeKey.
        var focused = Keyboard.FocusedElement;
        if (focused != null && !ReferenceEquals(focused, _view.VisualElement))
        {
            return false;
        }

        string? keys;
        if (IsPaste(group, id))
        {
            return PasteThroughNvim(session, "replace paste");
        }
        else if (group != VSConstants.VSStd2K)
        {
            return false;
        }
        else if (id == (uint)VSConstants.VSStd2KCmdID.DELETEWORDLEFT)
        {
            keys = "<C-w>";
        }
        else if (id == (uint)VSConstants.VSStd2KCmdID.DELETEWORDRIGHT)
        {
            return true;
        }
        else
        {
            keys = CmdLineKeyFor(group, id) ?? NormalModeKeyFor(group, id);
        }

        if (keys == null)
        {
            return false;
        }

        session.Input(keys);
        Infrastructure.Log.Key("replace -> sent " + keys + " to nvim");
        return true;
    }

    /// <summary>
    /// Paste as nvim_paste: the replace-mode paste (Ctrl+V, Edit > Paste, the
    /// context menu) and the command's paste in normal, visual and
    /// operator-pending mode. vim.paste replaces characters in R mode,
    /// replaces the selection in visual mode, and cancels a pending operator
    /// before inserting - Visual Studio's paste would write at a caret nvim
    /// never hears about. Vim's literal-next (&lt;C-v&gt;) pasted nothing and
    /// turned the next Escape into a raw 0x1B in the file.
    ///
    /// The clipboard is Visual Studio's, so it is read here: this predates
    /// nvim 0.10's own Windows clipboard support, and reading it here keeps
    /// the paste right on any nvim build.
    /// </summary>
    private static bool PasteThroughNvim(NvimSession session, string reason)
    {
        string text;
        try
        { text = System.Windows.Clipboard.GetText(); }
        catch (Exception ex)
        {
            // Held open by another process; nothing to paste this time.
            Infrastructure.Log.Write(reason + ": clipboard unreadable", ex);
            return true;
        }

        if (text.Length == 0)
        {
            return true;
        }

        _ = session.RequestAsync("nvim_paste", text, true, -1).ContinueWith(
            t => { _ = t.Exception; },
            CancellationToken.None,
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted,
            System.Threading.Tasks.TaskScheduler.Default);
        Infrastructure.Log.Key(reason + " -> nvim_paste " + text.Length + " chars");
        return true;
    }
}
