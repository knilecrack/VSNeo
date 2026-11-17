// The payloads the hub publishes: a state push, a keymap entry, a search match, a mark,
// and one jump-label overlay label.

namespace VSNeo_Extension.Nvim;
/// <summary>
/// One vsneo_state push, decoded without the frame and args arrays the
/// generic msgpack path would materialize: [mode, line, byteColumn,
/// topLine, anchorLine, anchorColumn, blockToEol, synthetic]. This is the
/// most frequent notification on the wire - one per keystroke.
/// </summary>
internal readonly struct StatePush
{
    public readonly string Mode;
    public readonly int Line;
    public readonly int ByteColumn;
    public readonly int TopLine;
    public readonly int AnchorLine;
    public readonly int AnchorColumn;
    public readonly bool BlockToEol;
    public readonly bool Synthetic;

    public StatePush(string mode, int line, int byteColumn, int topLine,
        int anchorLine, int anchorColumn, bool blockToEol, bool synthetic)
    {
        Mode = mode;
        Line = line;
        ByteColumn = byteColumn;
        TopLine = topLine;
        AnchorLine = anchorLine;
        AnchorColumn = anchorColumn;
        BlockToEol = blockToEol;
        Synthetic = synthetic;
    }
}

/// <summary>
/// One mapping as the companion reported it: lhs in nvim's own notation
/// (leader already expanded, "&lt;C-W&gt;" casing intact) and a human
/// description - the mapping's desc, or its rhs when it has none.
/// </summary>
public readonly struct KeymapEntry
{
    public readonly string Lhs;
    public readonly string Desc;

    public KeymapEntry(string lhs, string desc)
    {
        Lhs = lhs;
        Desc = desc;
    }
}

/// <summary>
/// One hlsearch match in nvim coordinates: 0-based line, 0-based UTF-8 byte
/// start and end columns. Run through ColumnMapper before handing to Visual Studio.
/// </summary>
public readonly struct SearchMatch
{
    public readonly int Line;
    public readonly int StartByte;
    public readonly int EndByte;

    public SearchMatch(int line, int startByte, int endByte)
    {
        Line = line;
        StartByte = startByte;
        EndByte = endByte;
    }
}

/// <summary>One nvim mark in a buffer: 0-based line and its name (a-z, A-Z).</summary>
public readonly struct BufferMark
{
    public readonly int Line;
    public readonly string Name;

    public BufferMark(int line, string name)
    {
        Line = line;
        Name = name;
    }
}

/// <summary>
/// One label an overlay interaction wants drawn: text over the given byte
/// span, in nvim coordinates. An empty text draws only the background
/// mark. Run the columns through ColumnMapper before handing to Visual
/// Studio.
/// </summary>
public readonly struct OverlayLabel
{
    public readonly int Line;
    public readonly int StartByte;
    public readonly int EndByte;
    public readonly string Text;

    public OverlayLabel(int line, int startByte, int endByte, string text)
    {
        Line = line;
        StartByte = startByte;
        EndByte = endByte;
        Text = text;
    }
}
