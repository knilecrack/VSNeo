using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace VSNeo_Extension.Nvim
{
    public enum VimMode { Unknown, Normal, Insert, Visual, Replace, CmdLine, OperatorPending, Terminal }

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

    /// <summary>
    /// Consumes the redraw notification stream from nvim_ui_attach and caches the
    /// things the key path needs.
    ///
    /// This is the load-bearing piece of the design: the key handler reads
    /// <see cref="Mode"/> and performs no I/O. Staleness is single-digit
    /// milliseconds, and the pending-input gate in the key processor covers it.
    /// </summary>
    internal sealed class NvimStateHub
    {
        private volatile int _mode = (int)VimMode.Normal;
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

        /// <summary>Current ext_cmdline content, or null when no command line is open.</summary>
        public string CmdLine { get; private set; } = null!;

        /// <summary>
        /// Current ext_messages content, or null when no message is being shown.
        /// This is the output of commands like :w, :%s, and /search, which Vim would
        /// normally draw over the command line; with ext_messages we get it separately.
        /// </summary>
        public string Message { get; private set; } = null!;

        /// <summary>The kind of message nvim reported ("", "error", "warning", etc.).</summary>
        public string MessageKind { get; private set; } = null!;

        /// <summary>
        /// Output too long for the one-line message area - :map, :set all,
        /// :ls, :messages - or null when the pager is closed. Vim shows such
        /// output until it is dismissed (the "more" and hit-enter prompts), and
        /// with ext_messages nvim leaves that to the UI: no msg_clear ever
        /// follows a list_cmd. So only <see cref="ClosePager"/> ends it, never
        /// msg_clear; drawn by MessagePager.
        /// </summary>
        public string? PagerText { get; private set; }

        /// <summary>The pager opened, changed text, or closed (null). Raised on the RPC read thread.</summary>
        public event Action<string?>? PagerChanged;

        /// <summary>
        /// Messages longer than this many lines go to the pager. The message
        /// margin sits below the text and grows to fit what it shows, so a
        /// 300-line :map filled the editor with a pane nothing could close.
        /// </summary>
        internal const int MaxInlineMessageLines = 3;

        /// <summary>Close the pager (its q, Escape, Enter or close button). Any thread.</summary>
        public void ClosePager()
        {
            // Closed from the UI thread, opened and appended to from the read
            // thread: the swap is under a lock so a q racing new :messages
            // output neither loses the output nor resurrects the closed text.
            lock (_pagerGate)
            {
                if (PagerText == null) return;
                PagerText = null;
            }
            PagerChanged?.Invoke(null);
        }

        private readonly object _pagerGate = new object();

        private void OpenPager(string text)
        {
            lock (_pagerGate) PagerText = text;
            PagerChanged?.Invoke(text);
        }

        private string? PagerTextSnapshot()
        {
            lock (_pagerGate) return PagerText;
        }

        internal static int CountLines(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int lines = 1;
            foreach (char c in text!) if (c == '\n') lines++;
            return lines;
        }

        /// <summary>
        /// Current ext_messages mode text, or null when no mode indicator is active.
        /// This is what Vim draws as "-- INSERT --", "-- VISUAL --", etc.
        /// </summary>
        public string ModeMessage { get; private set; } = null!;

        /// <summary>
        /// Current ext_messages showcmd text, or null when no partial command is
        /// pending. This is what Vim draws bottom-right while a command is being
        /// composed: "d2", "\"ay", "ci". Not a message - it has its own display
        /// area and is never coloured as an error.
        /// </summary>
        public string ShowCmd { get; private set; } = null!;

        /// <summary>
        /// The current hlsearch matches for nvim's current buffer, in nvim
        /// coordinates. Empty when hlsearch is off or there are no matches.
        /// </summary>
        public IReadOnlyList<SearchMatch> SearchMatches { get; private set; } = Array.Empty<SearchMatch>();

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
        public event Action SearchMatchesChanged = null!;

        /// <summary>
        /// Colors the adornments draw with, straight from nvim's highlight
        /// groups: Search, CurSearch (or IncSearch), IncSearch. -1 when the
        /// group has no background; the adornments fall back to their defaults.
        /// </summary>
        public int SearchColor { get; private set; } = -1;
        public int CurrentMatchColor { get; private set; } = -1;
        public int YankColor { get; private set; } = -1;
        public event Action HighlightsChanged = null!;

        /// <summary>
        /// Something was yanked in nvim: [line, startByte, endByte] triples in
        /// nvim coordinates, like <see cref="SearchMatches"/>. Fire-and-forget;
        /// the adornment owns how long the flash stays up.
        /// </summary>
        public event Action<IReadOnlyList<SearchMatch>> YankFlashed = null!;

        /// <summary>
        /// An overlay interaction (jump labels, anything Lua drives) is
        /// collecting keys in nvim. While set, the command filter routes the
        /// keys Visual Studio turns into commands - Escape, Enter, Backspace,
        /// arrows - to nvim, exactly as in CmdLine mode. Read on the key path;
        /// there is deliberately no event.
        /// </summary>
        public bool OverlayActive { get; private set; }

        /// <summary>
        /// The labels the active overlay wants drawn, in nvim coordinates.
        /// Empty when none are active.
        /// </summary>
        public IReadOnlyList<OverlayLabel> OverlayLabels { get; private set; }
            = Array.Empty<OverlayLabel>();

        public event Action OverlayLabelsChanged = null!;

        private KeymapTable _normalKeymaps = KeymapTable.Empty;
        private KeymapTable _visualKeymaps = KeymapTable.Empty;
        private volatile HashSet<string> _insertKeymaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A pushed mapping set: the public entries, plus each lhs pre-split
        /// into normalized tokens once, so the per-keystroke prefix match never
        /// tokenizes again. Immutable after construction; the field reference is
        /// swapped whole, so key-path readers never see a half-built table.
        /// </summary>
        private sealed class KeymapTable
        {
            public static readonly KeymapTable Empty = new KeymapTable(
                Array.Empty<KeymapEntry>(), Array.Empty<string[]>());

            public readonly KeymapEntry[] Entries;
            public readonly string[][] Tokens;

            public KeymapTable(KeymapEntry[] entries, string[][] tokens)
            {
                Entries = entries;
                Tokens = tokens;
            }
        }

        private KeymapTable TableFor(VimMode mode)
        {
            if (mode == VimMode.Normal) return _normalKeymaps;
            if (mode == VimMode.Visual) return _visualKeymaps;
            return KeymapTable.Empty;
        }

        /// <summary>
        /// Is this encoded key ("&lt;Left&gt;", "&lt;C-x&gt;") the lhs of an
        /// insert-mode mapping the user declared? The companion pushes only
        /// the user's own mappings (nvim's defaults are filtered), and only
        /// named-key lhs - so an unconfigured session claims nothing in
        /// insert, and a printable lhs is never here to begin with. Read on
        /// the key path: a set lookup, no I/O.
        /// </summary>
        public bool IsInsertMapped(string keys) => _insertKeymaps.Contains(keys);

        /// <summary>
        /// The mapping table the companion pushed after the rc loaded, per mode.
        /// Read on the key path by the which-key popup: the lookup is a local
        /// list scan, so the zero-I/O invariant holds.
        /// </summary>
        public IReadOnlyList<KeymapEntry> KeymapsFor(VimMode mode) => TableFor(mode).Entries;

        /// <summary>
        /// The mappings whose lhs strictly extends <paramref name="prefix"/>
        /// (the keys typed so far, in the same notation the key processor sent
        /// them). Empty when the sequence is complete, unknown, or never
        /// started - all three mean "no hint to show". Allocates only when
        /// there is something to show; the per-keystroke question is
        /// <see cref="HasKeymapChildren"/>, which allocates nothing.
        /// </summary>
        public IReadOnlyList<KeymapEntry> KeymapChildren(VimMode mode, string prefix)
        {
            if (prefix.Length == 0) return Array.Empty<KeymapEntry>();

            var table = TableFor(mode);
            List<KeymapEntry>? result = null;
            for (int m = 0; m < table.Entries.Length; m++)
            {
                if (IsStrictPrefix(prefix, table.Tokens[m]))
                    (result ??= new List<KeymapEntry>()).Add(table.Entries[m]);
            }
            return (IReadOnlyList<KeymapEntry>?)result ?? Array.Empty<KeymapEntry>();
        }

        public bool HasKeymapChildren(VimMode mode, string prefix)
        {
            if (prefix.Length == 0) return false;

            var table = TableFor(mode);
            for (int m = 0; m < table.Tokens.Length; m++)
                if (IsStrictPrefix(prefix, table.Tokens[m])) return true;
            return false;
        }

        /// <summary>
        /// Is the token stream of <paramref name="prefix"/> a strict prefix of
        /// a mapping's pre-split tokens? Walks the prefix token by token
        /// against the stored ones, normalizing as it goes ("&lt;C-W&gt;" vs
        /// "&lt;C-w&gt;", "&lt;Space&gt;" vs a plain space), so nothing is
        /// allocated per keystroke and the two sides cannot drift.
        /// </summary>
        private static bool IsStrictPrefix(string prefix, string[] tokens)
        {
            int t = 0, i = 0;
            while (i < prefix.Length)
            {
                if (t >= tokens.Length || !MatchToken(prefix, ref i, tokens[t])) return false;
                t++;
            }
            return t < tokens.Length;
        }

        /// <summary>Consumes one token at <paramref name="i"/> and compares it
        /// against a stored normalized token.</summary>
        private static bool MatchToken(string s, ref int i, string token)
        {
            if (s[i] != '<')
                return token.Length == 1 && token[0] == s[i++];

            int close = s.IndexOf('>', i + 1);
            if (close < 0)
                return token.Length == 1 && token[0] == s[i++]; // a trailing '<' is a literal

            int innerStart = i + 1;
            int innerLength = close - innerStart;
            i = close + 1;

            // The folded space token matches only the "<Space>" spelling.
            if (token.Length == 1)
                return token[0] == ' '
                    && innerLength == 5
                    && (s[innerStart] == 's' || s[innerStart] == 'S')
                    && (s[innerStart + 1] == 'p' || s[innerStart + 1] == 'P')
                    && (s[innerStart + 2] == 'a' || s[innerStart + 2] == 'A')
                    && (s[innerStart + 3] == 'c' || s[innerStart + 3] == 'C')
                    && (s[innerStart + 4] == 'e' || s[innerStart + 4] == 'E');

            // Stored tokens are "<lowered inner>"; compare case-insensitively.
            if (token.Length != innerLength + 2 || token[0] != '<' || token[token.Length - 1] != '>')
                return false;
            for (int k = 0; k < innerLength; k++)
                if (token[k + 1] != char.ToLowerInvariant(s[innerStart + k])) return false;
            return true;
        }

        /// <summary>
        /// Splits nvim key notation into tokens: "&lt;C-w&gt;f" becomes
        /// {"&lt;c-w&gt;", "f"}. Normalized so the two sides of a comparison
        /// cannot drift apart: "&lt;...&gt;" contents are lowercased (nvim
        /// reports "&lt;C-W&gt;", KeyEncoder produces "&lt;C-w&gt;") and
        /// "&lt;Space&gt;" folds to a plain space (KeyEncoder's named key,
        /// nvim's expanded leader). A trailing "&lt;" with no close is a
        /// literal character.
        /// </summary>
        internal static List<string> SplitKeyTokens(string keys)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < keys.Length)
            {
                if (keys[i] == '<')
                {
                    int close = keys.IndexOf('>', i + 1);
                    if (close < 0)
                    {
                        tokens.Add(keys[i].ToString());
                        i++;
                        continue;
                    }
                    var inner = keys.Substring(i + 1, close - i - 1).ToLowerInvariant();
                    tokens.Add(inner == "space" ? " " : "<" + inner + ">");
                    i = close + 1;
                }
                else
                {
                    tokens.Add(keys[i].ToString());
                    i++;
                }
            }
            return tokens;
        }

        /// <summary>
        /// The register a macro is being recorded into ("q", "a", ...), or
        /// null when not recording. Driven by RecordingEnter/RecordingLeave,
        /// which bracket the recording exactly - msg_showmode never carries it.
        /// </summary>
        public string RecordingReg { get; private set; } = null!;

        public event Action RecordingChanged = null!;

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
        /// nvim's actual fold set changed (a z-command, or a 'foldopen'
        /// auto-open): flat [start, end, closed] triples - 1-based lines,
        /// closed as 0/1 - covering every fold that still EXISTS, as detected
        /// by the companion polling foldlevel()/foldclosed() on every state
        /// push. A fold absent from the list was deleted (zd);
        /// FoldSynchronizer reconciles Visual Studio's outlining from it.
        /// </summary>
        public event Action<int[]> FoldsChanged = null!;

        /// <summary>
        /// zf in nvim: create a user fold over [start, end] (1-based lines) in
        /// the document at path. The fold is made Visual Studio-side (a real
        /// outlining region) and round-trips back into nvim.
        /// </summary>
        public event Action<string, int, int> FoldCreateRequested = null!;

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

        /// <summary>
        /// The redraw events <see cref="OnNotification"/> handles below. The
        /// stream reader skips every other batch (linegrid cell runs, viewport
        /// reports, highlight definitions) without decoding it, so a name added
        /// to the switch must be added here too or its events never materialize.
        /// </summary>
        internal static bool IsHandledRedrawEvent(string name)
        {
            switch (name)
            {
                case "cmdline_show":
                case "cmdline_pos":
                case "cmdline_hide":
                case "popupmenu_show":
                case "popupmenu_select":
                case "popupmenu_hide":
                case "msg_show":
                case "msg_showmode":
                case "msg_showcmd":
                case "msg_clear":
                case "msg_history_show":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The byte-twin of <see cref="IsHandledRedrawEvent"/>: matches a batch
        /// name's raw UTF-8 bytes against the handled set (all of which are
        /// pure ASCII) and hands back the canonical string constant on a hit,
        /// null on a miss. The stream reader uses it to recognize batches
        /// without decoding a string it would immediately throw away.
        /// </summary>
        internal static string? MatchHandledRedrawEvent(byte[] buf, int offset, int length)
        {
            switch (length)
            {
                case 8:
                    return MatchName(buf, offset, "msg_show") ? "msg_show" : null;
                case 9:
                    return MatchName(buf, offset, "msg_clear") ? "msg_clear" : null;
                case 11:
                    if (MatchName(buf, offset, "cmdline_pos")) return "cmdline_pos";
                    if (MatchName(buf, offset, "msg_showcmd")) return "msg_showcmd";
                    return null;
                case 12:
                    if (MatchName(buf, offset, "cmdline_show")) return "cmdline_show";
                    if (MatchName(buf, offset, "cmdline_hide")) return "cmdline_hide";
                    if (MatchName(buf, offset, "msg_showmode")) return "msg_showmode";
                    return null;
                case 13:
                    if (MatchName(buf, offset, "popupmenu_show")) return "popupmenu_show";
                    if (MatchName(buf, offset, "popupmenu_hide")) return "popupmenu_hide";
                    return null;
                case 15:
                    return MatchName(buf, offset, "popupmenu_select") ? "popupmenu_select" : null;
                case 16:
                    return MatchName(buf, offset, "msg_history_show") ? "msg_history_show" : null;
                default:
                    return null;
            }
        }

        private static bool MatchName(byte[] buf, int offset, string name)
        {
            for (int i = 0; i < name.Length; i++)
                if (buf[offset + i] != (byte)name[i]) return false;
            return true;
        }

        /// <summary>Called from the RPC read thread. Keep it allocation-light and non-blocking.</summary>
        public void OnNotification(string method, object[] args)
        {
            // Mode and cursor arrive from the Lua companion, which reports what Vim
            // is actually doing. The redraw stream is left to describe the one thing
            // it is the only source for: the command line. A switch, not a chain:
            // redraw is the most frequent notification on the wire and was the one
            // that failed every comparison before matching.
            switch (method)
            {
                case "redraw": HandleRedraw(args); return;
                case "vsneo_state": HandleState(args); return;
                case "vsneo_buf_enter": HandleBufEnter(args); return;
                case "vsneo_marks": HandleMarks(args); return;
                case "vsneo_keymaps": HandleKeymaps(args); return;
                case "vsneo_imaps": HandleImaps(args); return;
                case "vsneo_recording": HandleRecording(args); return;
                case "vsneo_search_count": HandleSearchCount(args); return;
                case "vsneo_search_matches": HandleSearchMatches(args); return;
                case "vsneo_highlights": HandleHighlights(args); return;
                case "vsneo_linenumbers": HandleLineNumbers(args); return;
                case "vsneo_cursor_animation": HandleCursorAnimation(args); return;
                case "vsneo_cursor_style": HandleCursorStyle(args); return;
                case "vsneo_yank": HandleYank(args); return;
                case "vsneo_undo_flash": UndoFlashEnabled = args != null && args.Length > 0 && ToInt(args[0]) != 0; return;
                case "vsneo_log":
                    if (args != null && args.Length > 0) Infrastructure.Log.Write("nvim: " + AsString(args[0]));
                    return;
                case "vsneo_esc_closes_popup": EscClosesPopup = args != null && args.Length > 0 && ToInt(args[0]) != 0; return;
                case "vsneo_overlay_active": HandleOverlayActive(args); return;
                case "vsneo_overlay_labels": HandleOverlayLabels(args); return;
                case "vsneo_folds_changed": HandleFoldsChanged(args); return;
                case "vsneo_fold_create": HandleFoldCreate(args); return;
            }
        }

        private void HandleRedraw(object[] args)
        {
            foreach (var batchObj in args)
            {
                if (!(batchObj is object[] batch) || batch.Length == 0) continue;
                var name = AsString(batch[0]);

                for (int i = 1; i < batch.Length; i++)
                {
                    if (!(batch[i] is object[] evt)) continue;
                    switch (name)
                    {
                        case "cmdline_show": HandleCmdlineShow(evt); break;
                        case "cmdline_pos": HandleCmdlinePos(evt); break;
                        // null is the "no command line open" state CmdLine documents.
                        case "cmdline_hide": SetCmdLine(null!); break;
                        case "popupmenu_show": HandlePopupmenuShow(evt); break;
                        case "popupmenu_select": HandlePopupmenuSelect(evt); break;
                        case "popupmenu_hide": HandlePopupmenuHide(); break;
                        case "msg_show": HandleMsgShow(evt); break;
                        case "msg_showmode": HandleMsgShowMode(evt); break;
                        case "msg_showcmd": HandleMsgShowCmd(evt); break;
                        case "msg_clear": ClearMessages(); break;
                        case "msg_history_show": HandleMsgHistoryShow(evt); break;
                    }
                }
            }
        }

        /// <summary>
        /// One notification carrying everything the key path and the caret need:
        /// [mode, line, byteColumn]. Mode is Vim's own short code from mode(), and
        /// line is already 0-based - the companion converts it.
        ///
        /// This replaced two pieces of inference. The cursor used to be lifted out of
        /// win_viewport, where it appears as a byproduct of describing the viewport,
        /// and mode out of mode_change, whose names describe cursor *shape*. Both
        /// arrived on the redraw cycle, so both were as current as the last repaint
        /// rather than as current as Vim.
        /// </summary>
        private void HandleState(object[] args)
        {
            if (args == null || args.Length < 3) return;

            HandleStateCore(
                AsString(args[0]),
                ToInt(args[1]),
                ToInt(args[2]),
                args.Length > 3 ? ToInt(args[3]) : -1,
                args.Length > 4 ? ToInt(args[4]) : -1,
                args.Length > 5 ? ToInt(args[5]) : -1,
                args.Length > 6 && args[6] is bool b && b,
                args.Length > 7 && args[7] is bool syn && syn);
        }

        /// <summary>
        /// The zero-allocation twin of <see cref="HandleState"/>: the stream
        /// reader's fast path hands the push over as a struct, skipping the
        /// frame and args arrays entirely. Same wire shape, same handling.
        /// </summary>
        public void OnStatePush(StatePush p) =>
            HandleStateCore(p.Mode, p.Line, p.ByteColumn, p.TopLine,
                p.AnchorLine, p.AnchorColumn, p.BlockToEol, p.Synthetic);

        private void HandleStateCore(string raw, int line, int col, int topLine,
            int anchorLine, int anchorColumn, bool blockToEol, bool synthetic)
        {
            // The far end of a visual selection, and which flavour of visual it is.
            // Charwise, linewise and blockwise select completely different regions
            // from the same pair of positions, so the distinction has to survive.
            VisualAnchorLine = anchorLine;
            VisualAnchorColumn = anchorColumn;
            VisualKind = string.IsNullOrEmpty(raw) ? '\0' : raw[0];

            // $ in blockwise visual reaches the end of every line in the block.
            // The companion reads that off curswant == v:maxcol; without the flag
            // the extension can only draw the corner-to-corner rectangle.
            VisualBlockToEol = blockToEol;

            // The synthetic flag marks viewport bookkeeping: the companion
            // clamped nvim's cursor into the window while Visual Studio's caret
            // is scrolled off it (nvim windows cannot hide their cursor, and
            // H/M/L must compute against what is on screen). That position is
            // cached - it is where nvim's cursor really is - but never raised:
            // the caret here stays where the user left it.

            var mode = ParseShort(raw);

            // Modes beginning with 'r' are prompts: hit-enter, "-- more --", or a
            // :confirm query. nvim has stopped and is waiting for an answer nobody
            // can see, because nothing here renders one. Whatever caused it is a bug
            // to fix at the source rather than to key off, so say so loudly.
            if (!string.IsNullOrEmpty(raw) && raw[0] == 'r')
                Infrastructure.Log.Write(
                    "nvim is BLOCKED at a prompt (mode \"" + raw + "\") and is ignoring input");

            _pushedMode = (int)mode;
            bool modeChanged = PublishMode(raw);

            // Scrolling is reported separately from the cursor because zz, zt, zb and
            // the <C-e>/<C-y> pair change only what is visible. Handled before the
            // early return below, which a pure scroll would otherwise take.
            if (topLine >= 0 && Interlocked.Exchange(ref _topLine, topLine) != topLine)
                ViewportScrolled?.Invoke(topLine);

            if (line < 0 || col < 0) return;

            long packed = ((long)line << 32) | (uint)col;
            if (synthetic)
            {
                Interlocked.Exchange(ref _cursor, packed);
                return;
            }

            // A mode change raises even an unchanged cursor. Mode and position
            // are one push, and CursorSynchronizer applies the position paired
            // with the move into insert exactly once - with the dedupe winning
            // here, that application used whatever position was last reported,
            // which a dropped or clamped report could have left stale (the
            // "caret jumps when I press i" bug).
            long previous = Interlocked.Exchange(ref _cursor, packed);
            if (previous == packed && !modeChanged) return;

            CursorMoved?.Invoke(line, col);
        }

        /// <summary>
        /// Publishes the effective mode if it changed: the pushed mode, with
        /// cmdline visibility from the redraw stream winning while a command
        /// line is open. Called from HandleState (a push arrived) and from
        /// SetCmdLine (the command line opened or closed); either can be the
        /// one that changes the answer, depending on which stream lands first.
        /// Returns true when the effective mode actually changed.
        /// </summary>
        private bool PublishMode(string? raw)
        {
            var effective = CmdLine != null ? VimMode.CmdLine : (VimMode)_pushedMode;
            if ((VimMode)_mode == effective) return false;

            Infrastructure.Log.Write(
                "mode: " + (raw == null ? string.Empty : "\"" + raw + "\" ") + "-> " + effective);
            _mode = (int)effective;
            Interlocked.Increment(ref _modeSequence);
            ModeChanged?.Invoke(effective);
            return true;
        }

        /// <summary>
        /// Vim's own mode codes, as returned by mode(). These are not the redraw
        /// stream's names: "n" not "normal", and the distinctions that matter are in
        /// the second character - "no" is operator-pending, which is emphatically not
        /// normal as far as the key path is concerned.
        /// </summary>
        private static VimMode ParseShort(string s)
        {
            if (string.IsNullOrEmpty(s)) return VimMode.Unknown;

            switch (s[0])
            {
                case 'n':
                    // "no", "nov", "noV", "no^V" are all operator-pending.
                    return s.Length > 1 && s[1] == 'o' ? VimMode.OperatorPending : VimMode.Normal;

                case 'i': return VimMode.Insert;
                case 'R': return VimMode.Replace;

                // Visual and Select, charwise / linewise / blockwise. Select behaves
                // like Visual for our purposes: nvim owns it and we swallow keys.
                case 'v':
                case 'V':
                case '\x16':
                case 's':
                case 'S':
                case '\x13': return VimMode.Visual;

                case 'c': return VimMode.CmdLine;
                case 't': return VimMode.Terminal;

                default: return VimMode.Unknown;
            }
        }

        /// <summary>
        /// Wildmenu items for the open command line: the words nvim offers for
        /// Tab-completion. Empty when no completion menu is up. In VsNeo only
        /// the cmdline can produce these - insert-mode completion belongs to
        /// Visual Studio and never reaches nvim.
        /// </summary>
        public IReadOnlyList<string> CompletionWords { get; private set; } = Array.Empty<string>();

        /// <summary>Index into <see cref="CompletionWords"/>, -1 when nothing is selected.</summary>
        public int CompletionSelected { get; private set; } = -1;

        public event Action CompletionsChanged = null!;

        /// <summary>popupmenu_show is [items, selected, row, col, grid]; items are [word, kind, menu, info].</summary>
        private void HandlePopupmenuShow(object[] evt)
        {
            if (evt.Length == 0 || !(evt[0] is object[] items))
            {
                CompletionWords = Array.Empty<string>();
                CompletionSelected = -1;
                CompletionsChanged?.Invoke();
                return;
            }

            var words = new List<string>(items.Length);
            foreach (var item in items)
                if (item is object[] entry && entry.Length > 0)
                    words.Add(AsString(entry[0]) ?? string.Empty);

            CompletionWords = words;
            CompletionSelected = evt.Length > 1 ? ToInt(evt[1]) : -1;
            CompletionsChanged?.Invoke();
        }

        /// <summary>popupmenu_select is [selected]: only the highlight moved.</summary>
        private void HandlePopupmenuSelect(object[] evt)
        {
            if (evt.Length == 0) return;
            CompletionSelected = ToInt(evt[0]);
            CompletionsChanged?.Invoke();
        }

        private void HandlePopupmenuHide()
        {
            if (CompletionWords.Count == 0 && CompletionSelected < 0) return;
            CompletionWords = Array.Empty<string>();
            CompletionSelected = -1;
            CompletionsChanged?.Invoke();
        }

        /// <summary>
        /// vsneo_folds_changed is [flat]: a single array of [start, end, closed]
        /// triples, 1-based lines, closed 0/1 - the companion's actual fold set
        /// (existing folds only; a deleted fold is simply absent). No caching -
        /// FoldSynchronizer reconciles against Visual Studio's outlining on
        /// arrival, which is the comparison that matters.
        /// </summary>
        private void HandleFoldsChanged(object[] args)
        {
            if (args.Length == 0 || !(args[0] is object[] flat)) return;

            var triples = new int[flat.Length];
            for (int i = 0; i < flat.Length; i++)
                triples[i] = ToInt(flat[i]);

            FoldsChanged?.Invoke(triples);
        }

        /// <summary>vsneo_fold_create is [path, start, end]: the zf range.</summary>
        private void HandleFoldCreate(object[] args)
        {
            if (args.Length < 3) return;

            var path = AsString(args[0]);
            if (path.Length == 0) return;

            FoldCreateRequested?.Invoke(path, ToInt(args[1]), ToInt(args[2]));
        }

        private static int ToInt(object o)
        {
            // The reader widens every integer to long; the interface cast inside
            // Convert.ToInt32 is the slow path taken only for oddball types.
            if (o is long l) return (int)l;
            try { return o == null ? -1 : Convert.ToInt32(o); }
            catch (Exception) { return -1; }
        }

        /// <summary>
        /// cmdline_show is [content, pos, firstc, prompt, indent, level].
        ///
        /// The prompt character comes separately from the content, and it is not
        /// decoration: ":" and "/" and "?" are the same mechanism, so assuming ":"
        /// would show a search as though it were a command.
        /// </summary>
        private void HandleCmdlineShow(object[] evt)
        {
            // content is an array of [attr, text] chunks
            if (evt.Length == 0 || !(evt[0] is object[] chunks)) return;

            var sb = new StringBuilder();
            foreach (var c in chunks)
                if (c is object[] chunk && chunk.Length > 1) sb.Append(AsString(chunk[1]));

            // A UTF-8 byte offset into the content, like every other column nvim
            // reports. Run it through ColumnMapper before indexing a .NET string.
            CmdLinePos = evt.Length > 1 ? Math.Max(0, ToInt(evt[1])) : 0;

            var firstc = evt.Length > 2 ? AsString(evt[2]) : null;

            // A ":" prompt sends ":" here; an input() prompt sends an empty string
            // and puts its text in the "prompt" field instead.
            CmdLinePrefix = string.IsNullOrEmpty(firstc)
                ? (evt.Length > 3 ? AsString(evt[3]) ?? string.Empty : string.Empty)
                // IsNullOrEmpty(false) guarantees non-null, but net472's reference
                // assemblies carry no NotNullWhen annotation to prove it.
                : firstc!;

            SetCmdLine(sb.ToString());
        }

        /// <summary>":", "/" or "?" - whatever opened the command line.</summary>
        public string CmdLinePrefix { get; private set; } = string.Empty;

        /// <summary>
        /// Where the cursor sits within <see cref="CmdLine"/>, as a UTF-8 byte offset.
        /// </summary>
        public int CmdLinePos { get; private set; }

        /// <summary>
        /// cmdline_pos is [pos, level]: the cursor moved inside the command line
        /// without the content changing, which is all Left, Right, &lt;C-b&gt; and
        /// &lt;C-e&gt; do. Reported separately from cmdline_show, so a synchroniser
        /// watching only the content never sees them.
        /// </summary>
        private void HandleCmdlinePos(object[] evt)
        {
            if (evt.Length == 0) return;

            int pos = ToInt(evt[0]);
            if (pos < 0) return;

            CmdLinePos = pos;
            CmdLineChanged?.Invoke(CmdLine);
        }

        private void SetCmdLine(string value)
        {
            CmdLine = value;
            // Opening or closing the command line can change the effective mode
            // on its own, before the companion's push catches up.
            PublishMode(null);
            CmdLineChanged?.Invoke(value);
        }

        /// <summary>
        /// msg_show is [kind, content, replace_last, history, append, id, trigger]
        /// (nvim 0.12; older versions stop after replace_last).
        ///
        /// The kind distinguishes ordinary echo from errors, warnings, search counts
        /// and confirmations. Content is an array of [attr_id, text] chunks, like
        /// cmdline_show. Keeping the kind lets the margin colour an error
        /// differently. append continues the message before it, so the pair is
        /// measured together: a list built from several appended pieces is
        /// still one long output. Anything past MaxInlineMessageLines goes to
        /// the pager and leaves the margin empty.
        /// </summary>
        private void HandleMsgShow(object[] evt)
        {
            if (evt.Length == 0) return;

            var kind = AsString(evt[0]);
            var text = ChunksToText(evt.Length > 1 ? evt[1] : null);

            bool append = evt.Length > 4 && evt[4] is bool a && a;
            if (append)
            {
                var pager = PagerTextSnapshot();
                if (pager != null) text = pager + text;
                else if (Message != null) text = Message + text;
            }

            if (CountLines(text) > MaxInlineMessageLines)
            {
                SetMessage(null!, null!);
                OpenPager(text);
                return;
            }

            SetMessage(kind, text);
        }

        /// <summary>
        /// msg_history_show is [entries, prev_cmd], each entry [kind, content,
        /// append]: the whole :messages history. With ext_messages nvim sends it
        /// here instead of printing it, so :messages used to show nothing at
        /// all. It always goes to the pager, one line per entry.
        /// </summary>
        private void HandleMsgHistoryShow(object[] evt)
        {
            if (evt.Length == 0 || !(evt[0] is object[] entries)) return;

            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                if (!(e is object[] entry) || entry.Length < 2) continue;
                bool append = entry.Length > 2 && entry[2] is bool a && a;
                if (sb.Length > 0 && !append) sb.Append('\n');
                sb.Append(ChunksToText(entry[1]));
            }
            if (sb.Length == 0) return;

            OpenPager(sb.ToString());
        }

        private static string ChunksToText(object? content)
        {
            var sb = new StringBuilder();
            if (content is object[] chunks)
            {
                foreach (var c in chunks)
                    if (c is object[] chunk && chunk.Length > 1) sb.Append(AsString(chunk[1]));
            }
            return sb.ToString();
        }

        private void SetMessage(string kind, string value)
        {
            MessageKind = kind;
            Message = value;
            MessageChanged?.Invoke(value);
        }

        /// <summary>
        /// msg_showmode is [content]: "-- INSERT --", "-- VISUAL --", etc.
        ///
        /// This is the text Vim draws in the bottom-right of its screen to tell you
        /// which mode you are in. With ext_messages we render it ourselves instead.
        /// </summary>
        private void HandleMsgShowMode(object[] evt)
        {
            var sb = new StringBuilder();
            if (evt.Length > 0 && evt[0] is object[] chunks)
            {
                foreach (var c in chunks)
                    if (c is object[] chunk && chunk.Length > 1) sb.Append(AsString(chunk[1]));
            }

            var text = sb.ToString();
            ModeMessage = text;
            ModeMessageChanged?.Invoke(text);
        }

        /// <summary>
        /// msg_showcmd is [content]: the partial command being composed, drawn
        /// bottom-right in Vim. Same chunk shape as msg_showmode. An empty
        /// content array means "clear it" - the command completed or was aborted.
        /// </summary>
        private void HandleMsgShowCmd(object[] evt)
        {
            var sb = new StringBuilder();
            if (evt.Length > 0 && evt[0] is object[] chunks)
            {
                foreach (var c in chunks)
                    if (c is object[] chunk && chunk.Length > 1) sb.Append(AsString(chunk[1]));
            }

            var text = sb.Length == 0 ? null! : sb.ToString();
            ShowCmd = text;
            ShowCmdChanged?.Invoke(text);
        }

        /// <summary>
        /// msg_clear clears everything in the message area, including the mode
        /// indicator and any pending command fragments.
        /// </summary>
        private void ClearMessages()
        {
            // null is the "nothing to show" state Message and ModeMessage document.
            SetMessage(null!, null!);
            ModeMessage = null!;
            ModeMessageChanged?.Invoke(null!);
            ShowCmd = null!;
            ShowCmdChanged?.Invoke(null!);
        }

        /// <summary>
        /// vsneo_buf_enter is [path]: the full buffer name from
        /// nvim_buf_get_name, "" when the buffer is unnamed. Normalized to a
        /// full path so a "O:/x" report and a "O:\x" document compare equal.
        /// </summary>
        /// <summary>
        /// nvim's marks per file, for the scrollbar (ScrollbarMarkTagger):
        /// vsneo_marks is [path, [[line, name], ...]] - a-z of that buffer and
        /// A-Z pointing into it, 0-based lines. Paths are normalized like
        /// vsneo_buf_enter's, so they compare against CurrentBufferPath.
        /// </summary>
        public IReadOnlyList<BufferMark> MarksFor(string? path)
        {
            if (path == null) return Array.Empty<BufferMark>();
            lock (_marks)
                return _marks.TryGetValue(path, out var list) ? list : Array.Empty<BufferMark>();
        }

        /// <summary>A file's marks changed; the argument is its normalized path.</summary>
        public event Action<string> MarksChanged = null!;

        private readonly Dictionary<string, IReadOnlyList<BufferMark>> _marks =
            new Dictionary<string, IReadOnlyList<BufferMark>>(StringComparer.OrdinalIgnoreCase);

        private void HandleMarks(object[] args)
        {
            if (args == null || args.Length < 2) return;
            var path = NormalizePath(AsString(args[0]));
            if (string.IsNullOrEmpty(path)) return;

            var list = new List<BufferMark>();
            if (args[1] is object[] items)
                foreach (var item in items)
                    if (item is object[] mark && mark.Length > 1)
                        list.Add(new BufferMark(ToInt(mark[0]), AsString(mark[1]) ?? string.Empty));

            lock (_marks) _marks[path!] = list;
            MarksChanged?.Invoke(path!);
        }

        /// <summary>vsneo_buf_enter's normalization, shared so marks and the current buffer compare.</summary>
        internal static string? NormalizePath(string? raw)
        {
            if (raw == null) return null;
            string path = raw;
            if (path.Length > 0)
            {
                try { path = System.IO.Path.GetFullPath(path); }
                catch { /* nvim path syntax is not always Win32-legal; keep it raw. */ }
            }
            return path;
        }

        private void HandleBufEnter(object[] args)
        {
            var path = NormalizePath(args != null && args.Length > 0 ? AsString(args[0]) : null);
            if (path == null) return;

            if (string.Equals(CurrentBufferPath, path, StringComparison.OrdinalIgnoreCase)) return;
            CurrentBufferPath = path;
            BufferSwitched?.Invoke(path);
        }

        /// <summary>
        /// vsneo_keymaps is [mode, items]: "n" or "x", and one [lhs, desc]
        /// pair per mapping. No event is raised - the only reader (the
        /// which-key popup) pulls from the key path, and hints are never
        /// shown unprompted.
        /// </summary>
        private void HandleKeymaps(object[] args)
        {
            if (args == null || args.Length < 2 || !(args[1] is object[] items)) return;

            var entries = new List<KeymapEntry>(items.Length);
            var tokens = new List<string[]>(items.Length);
            foreach (var item in items)
            {
                if (item is object[] pair && pair.Length >= 2)
                {
                    var lhs = AsString(pair[0]);
                    var desc = AsString(pair[1]);
                    if (!string.IsNullOrEmpty(lhs))
                    {
                        entries.Add(new KeymapEntry(lhs, desc ?? string.Empty));
                        tokens.Add(SplitKeyTokens(lhs).ToArray());
                    }
                }
            }

            var table = new KeymapTable(entries.ToArray(), tokens.ToArray());
            var mode = AsString(args[0]);
            if (mode == "n") _normalKeymaps = table;
            else if (mode == "x" || mode == "v") _visualKeymaps = table;
        }

        /// <summary>
        /// vsneo_imaps is [items]: the lhs tokens of the user's own
        /// insert-mode mappings on named keys. No rhs crosses the wire - the
        /// key path feeds the lhs back through nvim_input and nvim runs the
        /// mapping itself, so string, Lua-callback and expr rhs all work.
        /// </summary>
        private void HandleImaps(object[] args)
        {
            if (args == null || args.Length < 1 || !(args[0] is object[] items)) return;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var lhs = AsString(item);
                if (!string.IsNullOrEmpty(lhs)) set.Add(lhs);
            }
            _insertKeymaps = set;
        }

        /// <summary>
        /// vsneo_recording is [register]: the register being recorded into, or
        /// "" when RecordingLeave fired (the companion pushes "" explicitly;
        /// reg_recording() itself is not yet cleared when the event fires).
        /// </summary>
        private void HandleRecording(object[] args)
        {
            // Explicit null check, not IsNullOrEmpty: net472's reference
            // assemblies carry no NotNullWhen annotation to prove the flow.
            var raw = args != null && args.Length > 0 ? AsString(args[0]) : null;
            string value;
            if (raw == null || raw.Length == 0) value = null!;
            else value = raw;
            if (RecordingReg == value) return;

            RecordingReg = value;
            RecordingChanged?.Invoke();
        }

        /// <summary>
        /// Whole-buffer figures for the [n/N] chip, from searchcount() in the
        /// companion: how many matches precede the scanned range (so the index
        /// within <see cref="SearchMatches"/> becomes an index in the buffer),
        /// the total, and whether the total is a floor (the walk timed out).
        /// Sent just before each vsneo_search_matches; -1 total means unknown.
        /// </summary>
        public int SearchMatchesBefore { get; private set; }
        public int SearchTotal { get; private set; } = -1;
        public bool SearchTotalIncomplete { get; private set; }

        private void HandleSearchCount(object[] args)
        {
            if (args == null || args.Length < 3) return;
            SearchMatchesBefore = Math.Max(0, ToInt(args[0]));
            SearchTotal = Math.Max(0, ToInt(args[1]));
            SearchTotalIncomplete = ToInt(args[2]) != 0;
        }

        /// <summary>
        /// vsneo_search_matches carries the matches computed by the Lua companion:
        /// one array of [line, startByte, endByte] triples.
        /// </summary>
        private void HandleSearchMatches(object[] args)
        {
            if (args == null || args.Length == 0 || !(args[0] is object[] items))
            {
                SearchMatchesBefore = 0;
                SearchTotal = -1;
                SearchTotalIncomplete = false;
                if (SearchMatches.Count != 0)
                {
                    SearchMatches = Array.Empty<SearchMatch>();
                    SearchMatchesChanged?.Invoke();
                }
                return;
            }

            var matches = new List<SearchMatch>(items.Length);
            foreach (var item in items)
            {
                if (item is object[] m && m.Length >= 3)
                    matches.Add(new SearchMatch(ToInt(m[0]), ToInt(m[1]), ToInt(m[2])));
            }

            // The Lua side resends the full list after every edit even when the
            // matches did not move (typing elsewhere in the buffer); re-firing
            // would rebuild the adornment for nothing.
            var current = SearchMatches;
            if (current.Count == matches.Count)
            {
                bool same = true;
                for (int i = 0; i < matches.Count; i++)
                {
                    var a = current[i];
                    var b = matches[i];
                    if (a.Line != b.Line || a.StartByte != b.StartByte || a.EndByte != b.EndByte)
                    {
                        same = false;
                        break;
                    }
                }
                if (same) return;
            }

            SearchMatches = matches;
            SearchMatchesChanged?.Invoke();
        }

        /// <summary>
        /// vsneo_highlights carries three positional colors from the companion:
        /// Search, CurSearch (or IncSearch), IncSearch - 0xRRGGBB, or -1 when
        /// the group has no background and the adornment keeps its default.
        /// </summary>
        private void HandleHighlights(object[] args)
        {
            if (args == null || args.Length < 3) return;

            SearchColor = ToInt(args[0]);
            CurrentMatchColor = ToInt(args[1]);
            YankColor = ToInt(args[2]);
            // Optional tail: an older companion sends three values, and the
            // counter stays on.
            SearchCountEnabled = args.Length < 4 || ToInt(args[3]) != 0;
            HighlightsChanged?.Invoke();
        }

        /// <summary>
        /// vsneo_search_count: whether SearchHighlightAdornment draws the
        /// [current/total] chip beside the match under the cursor. On by default.
        /// </summary>
        public bool SearchCountEnabled { get; private set; } = true;

        /// <summary>
        /// nvim's 'number' and 'relativenumber' window options, as the companion
        /// last pushed them. nvim renders nothing itself; the relative line
        /// number margin is the only consumer, and the Tools &gt; Options
        /// override can mute even that.
        /// </summary>
        public bool NvimNumber { get; private set; }
        public bool NvimRelativeNumber { get; private set; }
        public event Action LineNumbersChanged = null!;

        /// <summary>
        /// vsneo_linenumbers is [number, relativenumber], 0/1 each, pushed after
        /// the rc and on every OptionSet for either option.
        /// </summary>
        private void HandleLineNumbers(object[] args)
        {
            if (args == null || args.Length < 2) return;

            bool number = ToInt(args[0]) != 0;
            bool relative = ToInt(args[1]) != 0;
            if (number == NvimNumber && relative == NvimRelativeNumber) return;

            NvimNumber = number;
            NvimRelativeNumber = relative;
            LineNumbersChanged?.Invoke();
        }

        /// <summary>
        /// Cursor trail settings (CursorTrailAdornment), from ~/.vsneorc's
        /// vsneo_cursor_* variables - Neovide's names with a vsneo_ prefix.
        /// The defaults hold until the companion's first push. Ints only on
        /// the wire (milliseconds, per mille) so no float crosses msgpack, and
        /// each value is a single word: the RPC thread writes, the UI thread
        /// reads, no lock needed.
        /// </summary>
        public int CursorAnimationMs { get; private set; } = 130;

        /// <summary>
        /// vsneo_cursor_short_animation_length: moves of at most two columns
        /// on one line (typing, h/l). Neovide's default, 0.04 s.
        /// </summary>
        public int CursorShortAnimationMs { get; private set; } = 40;

        /// <summary>
        /// SmoothScroller: Neovide's scroll_animation_length (0 = instant) and
        /// scroll_animation_far_lines (past one screen, only this many lines
        /// at the end animate).
        /// </summary>
        public int ScrollAnimationMs { get; private set; } = 300;
        public int ScrollFarLines { get; private set; } = 1;

        /// <summary>JumpBeacon: on/off, jump threshold in lines, width in columns, duration.</summary>
        public bool BeaconEnabled { get; private set; } = true;
        public int BeaconMinJump { get; private set; } = 10;
        public int BeaconWidth { get; private set; } = 40;
        public int BeaconMs { get; private set; } = 400;

        /// <summary>
        /// vsneo_reduce_effects: -1 detect software rendering (default),
        /// 0 never reduce, 1 always reduce. See Infrastructure.RenderTier.
        /// </summary>
        public int ReduceEffects { get; private set; } = -1;
        public int CursorTrailPermille { get; private set; } = 800;
        public bool CursorAnimateInInsert { get; private set; } = true;


        /// <summary>
        /// Cursor effects (Editor/Effects): vsneo_cursor_vfx_mode as a comma-separated
        /// string (empty = off), then its numbers - opacity on Neovide's 0..255
        /// scale, lifetimes in ms, density/speed/phase/curl in per mille.
        /// </summary>
        public string CursorVfxModes { get; private set; } = string.Empty;
        public int CursorVfxOpacity { get; private set; } = 200;
        public int CursorVfxLifetimeMs { get; private set; } = 500;
        public int CursorVfxHighlightLifetimeMs { get; private set; } = 200;
        public int CursorVfxDensityPermille { get; private set; } = 700;
        public int CursorVfxSpeedPermille { get; private set; } = 10000;
        public int CursorVfxPhasePermille { get; private set; } = 1500;
        public int CursorVfxCurlPermille { get; private set; } = 1000;

        /// <summary>
        /// vsneo_cursor_animation is [lengthMs, trailPermille, animateInInsert,
        /// vfxModes, vfxOpacity, lifetimeMs, highlightLifetimeMs,
        /// densityPermille, speedPermille, phasePermille, curlPermille]. The VFX
        /// tail is optional on the wire, so an older companion still parses.
        /// </summary>
        private void HandleCursorAnimation(object[] args)
        {
            if (args == null || args.Length < 3) return;
            CursorAnimationMs = Math.Max(0, ToInt(args[0]));
            CursorTrailPermille = Math.Max(0, Math.Min(1000, ToInt(args[1])));
            CursorAnimateInInsert = ToInt(args[2]) != 0;

            if (args.Length < 11) return;
            CursorVfxModes = args[3] == null ? string.Empty : AsString(args[3]) ?? string.Empty;
            CursorVfxOpacity = Math.Max(0, Math.Min(255, ToInt(args[4])));
            CursorVfxLifetimeMs = Math.Max(0, ToInt(args[5]));
            CursorVfxHighlightLifetimeMs = Math.Max(0, ToInt(args[6]));
            CursorVfxDensityPermille = Math.Max(0, ToInt(args[7]));
            CursorVfxSpeedPermille = Math.Max(0, ToInt(args[8]));
            CursorVfxPhasePermille = ToInt(args[9]);
            CursorVfxCurlPermille = ToInt(args[10]);

            // The short-move length is a later addition to the tail.
            if (args.Length >= 12) CursorShortAnimationMs = Math.Max(0, ToInt(args[11]));

            // Smooth scroll and jump beacon, later still.
            if (args.Length >= 18)
            {
                ScrollAnimationMs = Math.Max(0, ToInt(args[12]));
                ScrollFarLines = Math.Max(0, ToInt(args[13]));
                BeaconEnabled = ToInt(args[14]) > 0;
                BeaconMinJump = Math.Max(1, ToInt(args[15]));
                BeaconWidth = Math.Max(1, ToInt(args[16]));
                BeaconMs = Math.Max(0, ToInt(args[17]));
            }

            if (args.Length >= 19)
                ReduceEffects = Math.Max(-1, Math.Min(1, ToInt(args[18])));
        }

        /// <summary>
        /// VSNeo's own cursor (CustomCursorAdornment), from ~/.vsneorc's
        /// vsneo_cursor_style / vsneo_cursor_blinking. Off until the rc sets
        /// either, so Visual Studio's caret stays untouched by default.
        /// Styles are indexed normal, insert, replace, visual, operator-pending,
        /// cmdline. The array is replaced, never mutated, so readers can cache
        /// by reference.
        /// </summary>
        public bool CursorStyleEnabled { get; private set; }
        public string[] CursorStyles { get; private set; } = new string[6];
        public string CursorBlinking { get; private set; } = "blink";

        /// <summary>
        /// vsneo_cursor_color per mode (same slots as CursorStyles) as 0xRRGGBB,
        /// -1 for "the theme's caret color". Replaced, never mutated.
        /// </summary>
        public int[] CursorColors { get; private set; } = { -1, -1, -1, -1, -1, -1 };

        /// <summary>vsneo_cursor_glow blur radius in pixels, 0 for none.</summary>
        public int CursorGlow { get; private set; }

        /// <summary>
        /// ModeLineTint: vsneo_mode_line (off by default) and the tint's
        /// opacity, vsneo_mode_line_opacity, in per mille.
        /// </summary>
        public bool ModeLineEnabled { get; private set; }
        public int ModeLineOpacityPermille { get; private set; } = 120;

        public event Action CursorStyleChanged = null!;

        /// <summary>
        /// vsneo_cursor_style is [enabled, normal, insert, replace, visual,
        /// operator, cmdline, blinking, then six colors in the same mode order
        /// and the glow radius].
        /// </summary>
        // The last push, verbatim: the companion sends the style on every
        // SourcePost, which fires for each ftplugin, indent and syntax file
        // sourced on a FileType - half a dozen pushes per opened C# file, each
        // one fanned out as a UI post to every open view. An identical push
        // changes nothing and raises nothing.
        private object[]? _lastCursorStyleArgs;

        private static bool SameArgs(object[]? a, object[] b)
        {
            if (a == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!Equals(a[i], b[i])) return false;
            return true;
        }

        private void HandleCursorStyle(object[] args)
        {
            if (args == null || args.Length < 8) return;
            if (SameArgs(_lastCursorStyleArgs, args)) return;
            _lastCursorStyleArgs = args;

            var styles = new string[6];
            for (int i = 0; i < 6; i++)
                styles[i] = args[1 + i] == null ? string.Empty : AsString(args[1 + i]) ?? string.Empty;

            CursorStyles = styles;
            CursorBlinking = args[7] == null ? "blink" : AsString(args[7]) ?? "blink";

            // Colors and glow are an optional tail, so an older companion
            // still parses.
            if (args.Length >= 15)
            {
                var colors = new int[6];
                for (int i = 0; i < 6; i++)
                {
                    int rgb = ToInt(args[8 + i]);
                    colors[i] = rgb < 0 || rgb > 0xFFFFFF ? -1 : rgb;
                }
                CursorColors = colors;
                CursorGlow = Math.Max(0, Math.Min(60, ToInt(args[14])));
            }

            if (args.Length >= 17)
            {
                ModeLineEnabled = ToInt(args[15]) > 0;
                ModeLineOpacityPermille = Math.Max(0, Math.Min(1000, ToInt(args[16])));
            }
            CursorStyleEnabled = ToInt(args[0]) > 0;
            CursorStyleChanged?.Invoke();
        }

        /// <summary>
        /// vsneo_yank carries the yanked region as [line, startByte, endByte]
        /// triples, the same shape as search matches. Purely an event: nothing
        /// here caches it, the flash is transient by definition.
        /// </summary>
        private void HandleYank(object[] args)
        {
            if (args == null || args.Length == 0 || !(args[0] is object[] items)) return;

            var segments = new List<SearchMatch>(items.Length);
            foreach (var item in items)
            {
                if (item is object[] m && m.Length >= 3)
                    segments.Add(new SearchMatch(ToInt(m[0]), ToInt(m[1]), ToInt(m[2])));
            }

            if (segments.Count > 0)
                YankFlashed?.Invoke(segments);
        }

        /// <summary>
        /// vsneo_overlay_active opens or closes an overlay interaction. Closing
        /// drops any labels with it, so a driver that crashes mid-interaction
        /// cannot leave paint behind.
        /// </summary>
        private void HandleOverlayActive(object[] args)
        {
            OverlayActive = args != null && args.Length > 0 && ToInt(args[0]) != 0;
            if (!OverlayActive && OverlayLabels.Count != 0)
            {
                OverlayLabels = Array.Empty<OverlayLabel>();
                OverlayLabelsChanged?.Invoke();
            }
        }

        /// <summary>
        /// vsneo_overlay_labels carries one array of [line, startByte, endByte,
        /// text] entries, the same coordinate convention as search matches.
        /// </summary>
        private void HandleOverlayLabels(object[] args)
        {
            var items = args != null && args.Length > 0 ? args[0] as object[] : null;
            if (items == null)
            {
                if (OverlayLabels.Count != 0)
                {
                    OverlayLabels = Array.Empty<OverlayLabel>();
                    OverlayLabelsChanged?.Invoke();
                }
                return;
            }

            var labels = new List<OverlayLabel>(items.Length);
            foreach (var item in items)
            {
                if (item is object[] m && m.Length >= 4)
                    labels.Add(new OverlayLabel(
                        ToInt(m[0]), ToInt(m[1]), ToInt(m[2]), AsString(m[3]) ?? string.Empty));
            }

            OverlayLabels = labels;
            OverlayLabelsChanged?.Invoke();
        }

        // msgpack nil decodes to null here and the callers treat null like empty.
        // The return type stays non-nullable because widening it would ripple new
        // warnings into the BufferMirror and NvimSession call sites.
        internal static string AsString(object o) =>
            o is byte[] b ? Encoding.UTF8.GetString(b) : (o as string ?? o?.ToString())!;
    }
}
