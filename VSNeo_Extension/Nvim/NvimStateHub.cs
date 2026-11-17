using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace VSNeo_Extension.Nvim;

public enum VimMode { Unknown, Normal, Insert, Visual, Replace, CmdLine, OperatorPending, Terminal }

/// <summary>
/// Consumes the redraw notification stream from nvim_ui_attach and caches the
/// things the key path needs.
///
/// This is the load-bearing piece of the design: the key handler reads
/// <see cref="Mode"/> and performs no I/O. Staleness is single-digit
/// milliseconds, and the pending-input gate in the key processor covers it.
/// </summary>
internal sealed partial class NvimStateHub
{
    private volatile int _mode = (int)VimMode.Normal;
    public KeyRoutingRules Routing { get; } = new KeyRoutingRules();
    private int _pushedMode = (int)VimMode.Normal;  // last mode the companion pushed; read thread only
    private int _modeSequence;                      // effective-mode transition count; PublishMode only
    private long _cursor = -1;
    private long _topLine = -1;

    /// <summary>
    /// The mode everyone reads. Usually the companion's push, with one
    /// override: while the redraw stream says a command line is open
    /// (cmdline_show seen, cmdline_hide not yet), the mode is CmdLine no
    /// matter what the push says. The push is a heuristic fed by autocmds;
    /// cmdline_show/hide is the fact. When they disagree - a bounced or
    /// lagging push - routing Enter, Backspace and the arrows on the push
    /// leaks them to Visual Studio while nvim is still composing: the
    /// command line stays open, the popup stays up, and Enter lands in the
    /// file as a newline.
    /// </summary>
    public VimMode Mode => (VimMode)_mode;

    /// <summary>
    /// Counts effective-mode transitions. The key path needs this for
    /// i_CTRL-O: the i -> niI -> i round trip is two transitions, and a
    /// push queue behind a busy UI thread can deliver both before the next
    /// key is read - at which point the mode alone says "Insert" again and
    /// is indistinguishable from "the excursion never happened".
    /// </summary>
    public int ModeSequence => Volatile.Read(ref _modeSequence);

    private readonly PromptState _prompt;
    private readonly SearchState _search = new();
    private readonly MarksState _marks = new();

    public NvimStateHub()
    {
        _prompt = new PromptState(() => PublishMode(null));
        _prompt.CmdLineChanged += value => CmdLineChanged?.Invoke(value);
        _prompt.MessageChanged += value => MessageChanged?.Invoke(value);
        _prompt.ModeMessageChanged += value => ModeMessageChanged?.Invoke(value);
        _prompt.ShowCmdChanged += value => ShowCmdChanged?.Invoke(value);
        _prompt.CompletionsChanged += () => CompletionsChanged?.Invoke();
        _prompt.PagerChanged += value => PagerChanged?.Invoke(value);
        _search.MatchesChanged += () => SearchMatchesChanged?.Invoke();
        _search.HighlightsChanged += () => HighlightsChanged?.Invoke();
        _search.YankFlashed += segments => YankFlashed?.Invoke(segments);
        _marks.MarksChanged += path => MarksChanged?.Invoke(path);
    }

    /// <summary>Current ext_cmdline content, or null when no command line is open.</summary>
    public string CmdLine => _prompt.CmdLine;

    public string CmdLinePrefix => _prompt.CmdLinePrefix;

    public int CmdLinePos => _prompt.CmdLinePos;

    /// <summary>Current ext_messages content, or null when no message is being shown.</summary>
    public string Message => _prompt.Message;

    /// <summary>The kind of message nvim reported ("", "error", "warning", etc.).</summary>
    public string MessageKind => _prompt.MessageKind;

    /// <summary>Current ext_messages mode text ("-- INSERT --"), or null when inactive.</summary>
    public string ModeMessage => _prompt.ModeMessage;

    /// <summary>Current ext_messages showcmd text; read on the key path.</summary>
    public string ShowCmd => _prompt.ShowCmd;

    /// <summary>Pager text, or null when closed. See PromptState.PagerText.</summary>
    public string? PagerText => _prompt.PagerText;

    /// <summary>Wildmenu items for the open command line; empty when none.</summary>
    public IReadOnlyList<string> CompletionWords => _prompt.CompletionWords;

    public int CompletionSelected => _prompt.CompletionSelected;

    internal const int MaxInlineMessageLines = PromptState.MaxInlineMessageLines;

    /// <summary>Close the pager (q, Escape, Enter or close button). Any thread.</summary>
    public void ClosePager() => _prompt.ClosePager();

    internal static int CountLines(string? text) => PromptState.CountLines(text);

    /// <summary>The current hlsearch matches; see SearchState.Matches.</summary>
    public IReadOnlyList<SearchMatch> SearchMatches => _search.Matches;

    public event Action<VimMode> ModeChanged = null!;

    /// <summary>
    /// vsneo_undo_flash: whether u / Ctrl+R flash what they changed
    /// (UndoFlashAdornment). On by default; read at flash time, so no event.
    /// </summary>
    public bool UndoFlashEnabled { get; private set; } = true;

    /// <summary>
    /// vsneo_esc_closes_popup: whether Escape with a completion list or
    /// signature help open only closes the popup, staying in insert.
    /// Off by default; read by the Escape handler, so no event.
    /// </summary>
    public bool EscClosesPopup { get; private set; }
    public event Action<string> CmdLineChanged = null!;
    public event Action<string> MessageChanged = null!;
    public event Action<string> ModeMessageChanged = null!;
    public event Action<string> ShowCmdChanged = null!;
    public event Action<string?> PagerChanged = null!;
    public event Action SearchMatchesChanged = null!;

    /// <summary>Colors the adornments draw with; see SearchState.</summary>
    public int SearchColor => _search.SearchColor;
    public int CurrentMatchColor => _search.CurrentMatchColor;
    public int YankColor => _search.YankColor;
    public event Action HighlightsChanged = null!;

    /// <summary>Yank flash segments; see SearchState.YankFlashed.</summary>
    public event Action<IReadOnlyList<SearchMatch>> YankFlashed = null!;

    /// <summary>The cached cursor, for code that needs position without an event subscription.</summary>
    public int CursorLine
    {
        get { var c = Interlocked.Read(ref _cursor); return c < 0 ? -1 : (int)(c >> 32); }
    }

    /// <summary>
    /// Full path of the buffer nvim's window is showing, as the companion's
    /// vsneo_buf_enter last reported it; null until the first report. Usually
    /// this is the document Visual Studio has focused - the exception is the
    /// whole reason it exists: file-mark jumps, cross-file &lt;C-o&gt;, :b and
    /// gf move nvim's window without Visual Studio asking, and every cursor or
    /// scroll report from that moment describes a buffer that is not on
    /// screen. Synchronizers compare against it before applying anything.
    /// </summary>
    public string CurrentBufferPath { get; private set; } = null!;

    /// <summary>
    /// nvim's window switched to another buffer; the argument is its full
    /// path, "" for an unnamed buffer. Raised for Visual Studio-initiated
    /// switches too - the listener that asked for the switch recognises
    /// those by the path.
    /// </summary>
    public event Action<string> BufferSwitched = null!;

    public int CursorColumnByte
    {
        get { var c = Interlocked.Read(ref _cursor); return c < 0 ? -1 : (int)(c & 0xFFFFFFFF); }
    }

    /// <summary>nvim's first visible line (0-based) as last reported; -1 until the first report.</summary>
    public int TopLine => (int)Interlocked.Read(ref _topLine);

    /// <summary>
    /// Cursor moved in nvim: 0-based buffer line, and a column that is a UTF-8
    /// <em>byte</em> offset into that line. Run it through ColumnMapper before
    /// handing it to anything in Visual Studio.
    /// </summary>
    public event Action<int, int> CursorMoved = null!;

    /// <summary>
    /// nvim scrolled its window: the new first visible line, 0-based. Distinct
    /// from <see cref="CursorMoved"/> because zz, zt, zb and &lt;C-e&gt; move the
    /// window without moving the cursor at all.
    /// </summary>
    public event Action<int> ViewportScrolled = null!;

    /// <summary>
    /// The end of the visual selection the cursor is not at, 0-based line and
    /// UTF-8 byte column, or -1 when nothing is selected.
    /// </summary>
    public int VisualAnchorLine { get; private set; } = -1;
    public int VisualAnchorColumn { get; private set; } = -1;

    /// <summary>
    /// True while a blockwise visual selection runs to the end of every line
    /// (the $ case). The block is then ragged rather than rectangular, which
    /// CursorSynchronizer draws as one selection per line.
    /// </summary>
    public bool VisualBlockToEol { get; private set; }

    /// <summary>
    /// Vim's own mode letter: 'v' charwise, 'V' linewise, 0x16 blockwise. The
    /// parsed <see cref="VimMode"/> collapses all three into Visual, which is
    /// right for the key path and useless for drawing the selection.
    /// </summary>
    public char VisualKind { get; private set; }

    public event Action CompletionsChanged = null!;

    internal static int ToInt(object o)
    {
        // The reader widens every integer to long; the interface cast inside
        // Convert.ToInt32 is the slow path taken only for oddball types.
        if (o is long l)
        {
            return (int)l;
        }

        try
        { return o == null ? -1 : Convert.ToInt32(o); }
        catch (Exception) { return -1; }
    }

    /// <summary>
    /// vsneo_buf_enter is [path]: the full buffer name from
    /// nvim_buf_get_name, "" when the buffer is unnamed. Normalized to a
    /// full path so a "O:/x" report and a "O:\x" document compare equal.
    /// </summary>
    /// <summary>nvim's marks per file, for the scrollbar; see MarksState.</summary>
    public IReadOnlyList<BufferMark> MarksFor(string? path) => _marks.MarksFor(path);

    /// <summary>A file's marks changed; the argument is its normalized path.</summary>
    public event Action<string> MarksChanged = null!;

    /// <summary>vsneo_buf_enter's normalization, shared so marks and the current buffer compare.</summary>
    internal static string? NormalizePath(string? raw)
    {
        if (raw == null)
        {
            return null;
        }

        string path = raw;
        if (path.Length > 0)
        {
            try
            { path = System.IO.Path.GetFullPath(path); }
            catch { /* nvim path syntax is not always Win32-legal; keep it raw. */ }
        }

        return path;
    }

    /// <summary>Whole-buffer search count for the [n/N] chip; see SearchState.</summary>
    public int SearchMatchesBefore => _search.MatchesBefore;
    public int SearchTotal => _search.Total;
    public bool SearchTotalIncomplete => _search.TotalIncomplete;

    /// <summary>Whether SearchHighlightAdornment draws the [current/total] chip.</summary>
    public bool SearchCountEnabled => _search.CountEnabled;

    // msgpack nil decodes to null here and the callers treat null like empty.
    // The return type stays non-nullable because widening it would ripple new
    // warnings into the BufferMirror and NvimSession call sites.
    internal static string AsString(object o) =>
        o is byte[] b ? Encoding.UTF8.GetString(b) : (o as string ?? o?.ToString())!;
}
