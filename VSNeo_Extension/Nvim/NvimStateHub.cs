using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace VSNeo_Extension.Nvim
{
    public enum VimMode { Unknown, Normal, Insert, Visual, Replace, CmdLine, OperatorPending, Terminal }

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

        /// <summary>The cached cursor, for code that needs position without an event subscription.</summary>
        public int CursorLine
        {
            get { var c = Interlocked.Read(ref _cursor); return c < 0 ? -1 : (int)(c >> 32); }
        }

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

        /// <summary>Called from the RPC read thread. Keep it allocation-light and non-blocking.</summary>
        public void OnNotification(string method, object[] args)
        {
            // Mode and cursor arrive from the Lua companion, which reports what Vim
            // is actually doing. The redraw stream is left to describe the one thing
            // it is the only source for: the command line.
            if (method == "vsneo_state") { HandleState(args); return; }
            if (method == "vsneo_search_matches") { HandleSearchMatches(args); return; }
            if (method == "vsneo_highlights") { HandleHighlights(args); return; }
            if (method == "vsneo_yank") { HandleYank(args); return; }
            if (method == "vsneo_overlay_active") { HandleOverlayActive(args); return; }
            if (method == "vsneo_overlay_labels") { HandleOverlayLabels(args); return; }
            if (method != "redraw") return;

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

            var raw = AsString(args[0]);
            int line = ToInt(args[1]);
            int col = ToInt(args[2]);
            int topLine = args.Length > 3 ? ToInt(args[3]) : -1;

            // The far end of a visual selection, and which flavour of visual it is.
            // Charwise, linewise and blockwise select completely different regions
            // from the same pair of positions, so the distinction has to survive.
            VisualAnchorLine = args.Length > 4 ? ToInt(args[4]) : -1;
            VisualAnchorColumn = args.Length > 5 ? ToInt(args[5]) : -1;
            VisualKind = string.IsNullOrEmpty(raw) ? '\0' : raw[0];

            // $ in blockwise visual reaches the end of every line in the block.
            // The companion reads that off curswant == v:maxcol; without the flag
            // the extension can only draw the corner-to-corner rectangle.
            VisualBlockToEol = args.Length > 6 && args[6] is bool b && b;

            // Viewport bookkeeping: the companion clamped nvim's cursor into the
            // window while Visual Studio's caret is scrolled off it (nvim windows
            // cannot hide their cursor, and H/M/L must compute against what is on
            // screen). That position is cached - it is where nvim's cursor really
            // is - but never raised: the caret here stays where the user left it.
            bool synthetic = args.Length > 7 && args[7] is bool syn && syn;

            var mode = ParseShort(raw);

            // Modes beginning with 'r' are prompts: hit-enter, "-- more --", or a
            // :confirm query. nvim has stopped and is waiting for an answer nobody
            // can see, because nothing here renders one. Whatever caused it is a bug
            // to fix at the source rather than to key off, so say so loudly.
            if (!string.IsNullOrEmpty(raw) && raw[0] == 'r')
                Infrastructure.Log.Write(
                    "nvim is BLOCKED at a prompt (mode \"" + raw + "\") and is ignoring input");

            _pushedMode = (int)mode;
            PublishMode(raw);

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

            if (Interlocked.Exchange(ref _cursor, packed) == packed) return;

            CursorMoved?.Invoke(line, col);
        }

        /// <summary>
        /// Publishes the effective mode if it changed: the pushed mode, with
        /// cmdline visibility from the redraw stream winning while a command
        /// line is open. Called from HandleState (a push arrived) and from
        /// SetCmdLine (the command line opened or closed); either can be the
        /// one that changes the answer, depending on which stream lands first.
        /// </summary>
        private void PublishMode(string? raw)
        {
            var effective = CmdLine != null ? VimMode.CmdLine : (VimMode)_pushedMode;
            if ((VimMode)_mode == effective) return;

            Infrastructure.Log.Write(
                "mode: " + (raw == null ? string.Empty : "\"" + raw + "\" ") + "-> " + effective);
            _mode = (int)effective;
            ModeChanged?.Invoke(effective);
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

        private static int ToInt(object o)
        {
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
        /// msg_show is [kind, content, replace_last].
        ///
        /// The kind distinguishes ordinary echo from errors, warnings, search counts
        /// and confirmations. Content is an array of [attr_id, text] chunks, like
        /// cmdline_show. replace_last is not useful for a single-line display, but
        /// keeping the kind lets the margin colour an error differently.
        /// </summary>
        private void HandleMsgShow(object[] evt)
        {
            if (evt.Length == 0) return;

            var kind = AsString(evt[0]);

            var sb = new StringBuilder();
            if (evt.Length > 1 && evt[1] is object[] chunks)
            {
                foreach (var c in chunks)
                    if (c is object[] chunk && chunk.Length > 1) sb.Append(AsString(chunk[1]));
            }

            SetMessage(kind, sb.ToString());
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
        /// vsneo_search_matches carries the matches computed by the Lua companion:
        /// one array of [line, startByte, endByte] triples.
        /// </summary>
        private void HandleSearchMatches(object[] args)
        {
            if (args == null || args.Length == 0 || !(args[0] is object[] items))
            {
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
            HighlightsChanged?.Invoke();
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
