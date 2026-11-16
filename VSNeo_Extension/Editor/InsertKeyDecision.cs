using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

/// <summary>
/// The insert-mode and typed-text routing conditions, as pure predicates.
///
/// The key processor used to spell each one inline, interleaved with the
/// side effects it gates. The order of the checks is still in
/// <see cref="VsNeoKeyProcessor"/>; these functions only answer the question,
/// so the gate scoping, the Ctrl-W/&lt;C-o&gt; exceptions and the
/// replace-mode rules can be tested without WPF or a session.
/// </summary>
internal static class InsertKeyDecision
{
    /// <summary>
    /// Every reason to hand input back to Visual Studio, and not one of them
    /// costs a round trip. Shared so the two entry points cannot drift.
    ///
    /// The gate protects Visual Studio's popup UI while *typing*, so it is
    /// scoped to insert: in normal mode a signature tooltip can be open with no
    /// typing going on (K -> QuickInfo is the stock example), and passing hjkl
    /// through would type them into the buffer. Replace mode is exempt too -
    /// every key there is nvim's, and an insert popup left open must not let
    /// typing edit VS-side. An explicit <c>:Sethandler ... i:vim</c> rule
    /// overrides the gate.
    /// </summary>
    public static bool ShouldIntercept(
        bool documentView, bool sessionReady, bool hasFocus, bool foreignFocus,
        bool forceVim, bool insertMode, bool intelliSenseActive)
    {
        if (!documentView || !sessionReady || !hasFocus || foreignFocus)
        {
            return false;
        }

        return forceVim || !insertMode || !intelliSenseActive;
    }

    /// <summary>
    /// Ctrl+W in insert: delete word backward, performed Visual Studio-side.
    /// Claimed even with a completion list open; not while a &lt;C-o&gt;
    /// excursion is pending (there it is nvim's window prefix), and not when
    /// the user assigned the key to Vim.
    /// </summary>
    public static bool ShouldDeleteWordBackward(
        bool sessionReady, bool documentView, bool hasFocus, bool foreignFocus,
        bool ctrlOPending, bool forceVim, VimMode mode, bool isCtrlW) =>
        sessionReady && documentView && hasFocus && !foreignFocus
        && !ctrlOPending && !forceVim && mode == VimMode.Insert && isCtrlW;

    /// <summary>
    /// Ctrl+N / Ctrl+P in insert walk an open completion list, or ask for one
    /// with COMPLETEWORD when none is open. A user's insert mapping of either
    /// key wins unless a popup is open (the popup owns the keys then), and a
    /// <c>:Sethandler ... i:vim</c> rule always wins.
    /// </summary>
    public static bool ShouldRouteCompletion(
        bool sessionReady, bool documentView, bool hasFocus, bool foreignFocus,
        bool ctrlOPending, bool forceVim, VimMode mode, bool isCtrlNOrP,
        bool isInsertMapped, bool intelliSenseActive) =>
        sessionReady && documentView && hasFocus && !foreignFocus
        && !ctrlOPending && !forceVim && mode == VimMode.Insert && isCtrlNOrP
        && (!isInsertMapped || intelliSenseActive);

    /// <summary>Escape leaves insert or replace through nvim.</summary>
    public static bool ShouldSendEscape(VimMode mode, bool isEscape) =>
        (mode == VimMode.Insert || mode == VimMode.Replace) && isEscape;

    /// <summary>Replace mode sends every named key to nvim (R, gR).</summary>
    public static bool ShouldSendReplaceKey(VimMode mode, bool hasEncoded) =>
        mode == VimMode.Replace && hasEncoded;

    /// <summary>i_CTRL-O: one normal command, then back to insert.</summary>
    public static bool ShouldClaimCtrlO(VimMode mode, bool isCtrlO) =>
        mode == VimMode.Insert && isCtrlO;

    /// <summary>
    /// A named key claimed from insert by the user's own insert mapping, or by
    /// <c>:Sethandler ... i:vim</c>.
    /// </summary>
    public static bool ShouldClaimInsertMap(
        VimMode mode, bool hasEncoded, bool forceVim, bool insertMapped) =>
        mode == VimMode.Insert && hasEncoded && (forceVim || insertMapped);

    /// <summary>Ctrl+R in normal mode is Visual Studio's redo.</summary>
    public static bool ShouldRedo(VimMode mode, bool isCtrlR) =>
        mode == VimMode.Normal && isCtrlR;

    /// <summary>
    /// Typed text routed through nvim instead of Visual Studio: replace mode
    /// always (VS overwrite has none of Vim's rules), and insert while the
    /// mirror still holds an unapplied remote edit (the c-family deletion race).
    /// </summary>
    public static bool ShouldRouteTypedText(
        VimMode mode, bool replace, bool hasUnappliedRemoteEdits) =>
        (mode == VimMode.Insert || mode == VimMode.Replace)
        && (replace || hasUnappliedRemoteEdits);

    /// <summary>u in normal mode is Visual Studio's undo.</summary>
    public static bool ShouldUndoText(VimMode mode, string text) =>
        mode == VimMode.Normal && text == "u";

    /// <summary>
    /// A Ctrl+Alt result that is not printable stays Visual Studio's (AltGr
    /// produces printable text on many layouts; control output is not text).
    /// </summary>
    public static bool ShouldLeaveCtrlAltText(bool ctrlAlt, bool emptyOrControlText) =>
        ctrlAlt && emptyOrControlText;

    public static bool ShouldShowRegisterPeek(VimMode mode, string text) =>
        (mode == VimMode.Normal || mode == VimMode.Visual) && text == "\"";

    public static bool ShouldShowMarkPeek(VimMode mode, string text) =>
        (mode == VimMode.Normal || mode == VimMode.Visual) && (text == "`" || text == "'");
}
