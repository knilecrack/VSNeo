using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

/// <summary>
/// Two gestures arrive as one command. Visual Studio routes the Ctrl+V chord
/// and the context menu's Paste item (and Shift+Insert, and
/// <c>:Vsc Edit.Paste</c>) through <c>VSStd97CmdID.Paste</c>, so the command
/// alone cannot say which one the user meant.
///
/// What it costs to guess wrong: claiming the menu's paste as the chord put
/// nvim into blockwise visual instead of pasting - and the mouse selection
/// that made someone reach for the menu in the first place is exactly what
/// leaves nvim in visual mode, so right-click, Paste did nothing at all.
///
/// The chord state is the discriminator. With Ctrl down it is the Ctrl+V
/// chord; without, it is a command, and in normal, visual or operator-pending
/// mode that is nvim's paste. <see cref="RoutedKey"/> names it <c>p</c> for
/// the <c>:Sethandler</c> lookup - so <c>:Sethandler p n:ide</c> still hands
/// the menu's paste back to Visual Studio - and the filter then pastes through
/// <c>nvim_paste</c> rather than sending the key, so the text lands at nvim's
/// cursor and both sides agree where it went. Insert, replace and the command
/// line keep Visual Studio's paste: that is where typing is its own.
/// </summary>
internal static class PasteDecision
{
    public const string BlockwiseChord = "<C-v>";

    /// <summary>The key a command-level paste stands for when :Sethandler asks.</summary>
    public const string CommandKey = "p";

    /// <summary>
    /// The key a Paste command stands for, or null when it is not ours to route
    /// at all (insert, replace, command line). Null also leaves the command to
    /// the :Sethandler rules, which still get to hand it to Visual Studio.
    /// </summary>
    public static string? RoutedKey(bool ctrlDown, VimMode mode) =>
        ctrlDown ? BlockwiseChord
        : IsVimPaste(mode) ? CommandKey
        : null;

    /// <summary>
    /// Where a paste is nvim's either way: normal and visual, plus
    /// operator-pending, where <c>nvim_paste</c> cancels the operator before
    /// inserting, as Vim's <c>p</c> does.
    /// </summary>
    public static bool IsVimPaste(VimMode mode) =>
        mode == VimMode.Normal || mode == VimMode.Visual || mode == VimMode.OperatorPending;
}
