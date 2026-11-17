using System;

namespace VSNeo_Extension.Editor;

/// <summary>
/// The Visual Studio commands each interception point turns back into Vim keys,
/// as a pure table. No VS SDK types: the filter passes its
/// <c>VSConstants.VSStd2K</c> group and the numeric command id, and the table
/// answers with nvim notation (or null). That is what makes the routing
/// decisions testable without devenv.
///
/// The command ids are <c>VSConstants.VSStd2KCmdID</c>/<c>VSStd97CmdID</c>
/// members; the ones used here have fixed values in the SDK. A Debug build
/// asserts them against the SDK enums (see VsNeoCommandFilter), so a typo
/// cannot silently reroute a key.
/// </summary>
internal static class CommandKeyTable
{
    /// <summary>VSConstants.VSStd2K.</summary>
    public static readonly Guid VsStd2K = new("1496a755-94de-11d0-8c3f-00c04fc2aae2");

    /// <summary>VSConstants.GUID_VSStandardCommandSet97.</summary>
    public static readonly Guid VsStd97 = new("5efc7975-14bc-11cf-9b2b-00aa00573819");

    // VSStd2KCmdID
    public const uint Backspace = 2;
    public const uint Return = 3;
    public const uint Tab = 4;
    public const uint BackTab = 5;
    public const uint Delete = 6;
    public const uint Left = 7;
    public const uint LeftExt = 8;
    public const uint Right = 9;
    public const uint RightExt = 10;
    public const uint Up = 11;
    public const uint UpExt = 12;
    public const uint Down = 13;
    public const uint DownExt = 14;
    public const uint Home = 15;
    public const uint HomeExt = 16;
    public const uint End = 17;
    public const uint EndExt = 18;
    public const uint Bol = 19;
    public const uint BolExt = 20;
    public const uint FirstChar = 21;
    public const uint FirstCharExt = 22;
    public const uint Eol = 23;
    public const uint EolExt = 24;
    public const uint PageUp = 27;
    public const uint PageUpExt = 28;
    public const uint PageDown = 29;
    public const uint PageDownExt = 30;
    public const uint WordPrev = 93;
    public const uint WordPrevExt = 94;
    public const uint WordNext = 96;
    public const uint WordNextExt = 97;
    public const uint Cancel = 103;

    // VSStd97CmdID
    public const uint Paste = 26;

    /// <summary>Escape, from either the editor's Cancel command or Ctrl+[.</summary>
    public static bool IsCancel(Guid group, uint id) =>
        group == VsStd2K && id == Cancel;

    /// <summary>Paste, which blockwise visual mode handles itself.</summary>
    public static bool IsPaste(Guid group, uint id) =>
        group == VsStd97 && id == Paste;

    /// <summary>
    /// Navigation and deletion commands as the keys that produced them, for
    /// normal, visual and operator-pending mode. The _EXT variants are the
    /// Shift chords: Vim gives Shift+arrow a meaning of its own (word and page
    /// motions), so those keep the modifier; Shift+Home/End/PageUp/PageDown
    /// have none and go plain. Null for anything else.
    /// </summary>
    public static string? NormalModeKeyFor(Guid group, uint id)
    {
        if (group != VsStd2K)
        {
            return null;
        }

        switch (id)
        {
            case Left:
                return "<Left>";
            case Right:
                return "<Right>";
            case Up:
                return "<Up>";
            case Down:
                return "<Down>";
            case LeftExt:
                return "<S-Left>";
            case RightExt:
                return "<S-Right>";
            case UpExt:
                return "<S-Up>";
            case DownExt:
                return "<S-Down>";

            case WordPrev:
            case WordPrevExt:
                return "<C-Left>";
            case WordNext:
            case WordNextExt:
                return "<C-Right>";

            // Home is Edit.LineStart (BOL), or FIRSTCHAR under the smart-home
            // setting; Ctrl+Home/End are the document ends (HOME/END).
            case Bol:
            case BolExt:
            case FirstChar:
            case FirstCharExt:
                return "<Home>";
            case Eol:
            case EolExt:
                return "<End>";
            case Home:
            case HomeExt:
                return "<C-Home>";
            case End:
            case EndExt:
                return "<C-End>";

            case PageUp:
            case PageUpExt:
                return "<PageUp>";
            case PageDown:
            case PageDownExt:
                return "<PageDown>";

            case Delete:
                return "<Del>";
            case Backspace:
                return "<BS>";

            // Left to Visual Studio they ran Edit.InsertTab / Edit.TabLeft
            // and edited the file in normal mode; nvim's <Tab> is <C-i>.
            case Tab:
                return "<Tab>";
            case BackTab:
                return "<S-Tab>";
            default:
                return null;
        }
    }

    /// <summary>
    /// The keys Visual Studio turns into commands, as the pager reads them.
    /// Backspace and Tab are listed so that they close the pager and carry
    /// on, like any key the pager has no use for.
    /// </summary>
    public static string? PagerKeyFor(Guid group, uint id)
    {
        if (group != VsStd2K)
        {
            return null;
        }

        switch (id)
        {
            case Cancel:
                return "<Esc>";
            case Return:
                return "<CR>";
            case Up:
                return "<Up>";
            case Down:
                return "<Down>";
            case Left:
                return "<Left>";
            case Right:
                return "<Right>";
            case PageUp:
                return "<PageUp>";
            case PageDown:
                return "<PageDown>";
            case Bol:
            case Home:
                return "<Home>";
            case Eol:
            case End:
                return "<End>";
            case Backspace:
                return "<BS>";
            case Tab:
                return "<Tab>";
            default:
                return null;
        }
    }

    /// <summary>
    /// Command-line editing keys, in nvim notation. History is <c>Up</c> and
    /// <c>Down</c>, completion is <c>Tab</c>, and the rest is ordinary line
    /// editing - which is what makes a long :%s/.../.../ correctable rather
    /// than something to be retyped from the start. Null when the command is
    /// not a command-line editing key at all.
    /// </summary>
    public static string? CmdLineKeyFor(Guid group, uint id)
    {
        if (group != VsStd2K)
        {
            return null;
        }

        switch (id)
        {
            case Return:
                return "<CR>";
            case Backspace:
                return "<BS>";
            case Tab:
                return "<Tab>";
            case BackTab:
                return "<S-Tab>";
            case Delete:
                return "<Del>";
            case Left:
                return "<Left>";
            case Right:
                return "<Right>";
            case Up:
                return "<Up>";
            case Down:
                return "<Down>";
            case Bol:
                return "<Home>";
            case Eol:
                return "<End>";
            default:
                return null;
        }
    }
}
