using System;
using System.Windows.Input;
using Microsoft.VisualStudio;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// Command-line, pager and normal-mode command keys.
internal sealed partial class VsNeoCommandFilter
{
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
        if (session == null || !session.IsReady)
        {
            return false;
        }

        if (session.State.Mode != VimMode.CmdLine)
        {
            return false;
        }

        if (IsPaste(group, id))
        {
            return PasteIntoCmdLine(session);
        }

        var keys = CmdLineKeyFor(group, id);
        if (keys == null)
        {
            return false;
        }

        if (id == (uint)VSConstants.VSStd2KCmdID.BACKSPACE
            && (session.State.CmdLine ?? string.Empty).Length == 0)
        {
            return true;
        }

        session.Input(keys);
        Infrastructure.Log.Key("cmdline -> sent " + keys + " to nvim");
        return true;
    }

    /// <summary>
    /// Paste into the command line, as typed text. First line only, which is
    /// vim.paste's own rule for cmdline mode: a line break sent as a key would
    /// run the command half-pasted. Tabs go in literally (&lt;C-v&gt;&lt;Tab&gt;)
    /// rather than starting completion. Clipboard read here for the same
    /// reason as PasteThroughNvim.
    /// </summary>
    private static bool PasteIntoCmdLine(NvimSession session)
    {
        string text;
        try
        { text = System.Windows.Clipboard.GetText(); }
        catch (Exception ex)
        {
            Infrastructure.Log.Write("cmdline paste: clipboard unreadable", ex);
            return true;
        }

        int lineEnd = text.IndexOfAny(new[] { '\r', '\n' });
        if (lineEnd >= 0)
        {
            text = text.Substring(0, lineEnd);
        }

        var keys = KeyEncoder.EncodeText(text)?.Replace("\t", "<C-v><Tab>");
        if (keys == null)
        {
            return true;
        }

        session.Input(keys);
        Infrastructure.Log.Key("cmdline -> pasted " + text.Length + " chars");
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
        if (keys == null)
        {
            return false;
        }

        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || !session.IsReady)
        {
            return false;
        }

        var mode = session.State.Mode;
        if (mode != VimMode.Normal && mode != VimMode.Visual && mode != VimMode.OperatorPending)
        {
            return false;
        }

        // A Visual Studio control hosted inside the view (Roslyn's rename
        // dashboard is a TextBox in an adornment layer) still routes its
        // editor commands through this filter; its arrows and Backspace are
        // the control's, not nvim's. Same rule as the key processor.
        var focused = Keyboard.FocusedElement;
        if (focused != null && !ReferenceEquals(focused, _view.VisualElement))
        {
            return false;
        }

        // <Del> is x: a later i must not inherit its insert-undo start.
        if (mode != VimMode.OperatorPending)
        {
            BufferMirror.TryGetForBuffer(_view.TextBuffer)?.ForgetPendingInsertUndoStart();
        }

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
    private static string? NormalModeKeyFor(Guid group, uint id) =>
        CommandKeyTable.NormalModeKeyFor(group, id);

    /// <summary>
    /// The keys Visual Studio turns into commands, as the pager reads them.
    /// Backspace and Tab are listed so that they close the pager and carry
    /// on, like any key the pager has no use for.
    /// </summary>
    private static string? PagerKeyFor(Guid group, uint id) =>
        CommandKeyTable.PagerKeyFor(group, id);

    /// <summary>
    /// Command-line editing keys, in nvim notation. History is <c>Up</c> and
    /// <c>Down</c>, completion is <c>Tab</c>, and the rest is ordinary line
    /// editing - which is what makes a long :%s/.../.../ correctable rather than
    /// something to be retyped from the start. Null when the command is not a
    /// command-line editing key at all.
    /// </summary>
    private static string? CmdLineKeyFor(Guid group, uint id) =>
        CommandKeyTable.CmdLineKeyFor(group, id);
}
