using System;
using System.Text;
using System.Threading;

namespace VSNeo_Extension.Nvim
{
    public enum VimMode { Unknown, Normal, Insert, Visual, Replace, CmdLine, OperatorPending, Terminal }

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
        private long _cursor = -1;
        private long _topLine = -1;

        public VimMode Mode => (VimMode)_mode;

        /// <summary>Current ext_cmdline content, or null when no command line is open.</summary>
        public string CmdLine { get; private set; }

        public event Action<VimMode> ModeChanged;
        public event Action<string> CmdLineChanged;

        /// <summary>
        /// Cursor moved in nvim: 0-based buffer line, and a column that is a UTF-8
        /// <em>byte</em> offset into that line. Run it through ColumnMapper before
        /// handing it to anything in Visual Studio.
        /// </summary>
        public event Action<int, int> CursorMoved;

        /// <summary>
        /// nvim scrolled its window: the new first visible line, 0-based. Distinct
        /// from <see cref="CursorMoved"/> because zz, zt, zb and &lt;C-e&gt; move the
        /// window without moving the cursor at all.
        /// </summary>
        public event Action<int> ViewportScrolled;

        /// <summary>
        /// The end of the visual selection the cursor is not at, 0-based line and
        /// UTF-8 byte column, or -1 when nothing is selected.
        /// </summary>
        public int VisualAnchorLine { get; private set; } = -1;
        public int VisualAnchorColumn { get; private set; } = -1;

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
                        case "cmdline_hide": SetCmdLine(null); break;
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

            var mode = ParseShort(raw);

            // Modes beginning with 'r' are prompts: hit-enter, "-- more --", or a
            // :confirm query. nvim has stopped and is waiting for an answer nobody
            // can see, because nothing here renders one. Whatever caused it is a bug
            // to fix at the source rather than to key off, so say so loudly.
            if (!string.IsNullOrEmpty(raw) && raw[0] == 'r')
                Infrastructure.Log.Write(
                    "nvim is BLOCKED at a prompt (mode \"" + raw + "\") and is ignoring input");
            if ((VimMode)_mode != mode)
            {
                Infrastructure.Log.Write("mode: \"" + raw + "\" -> " + mode);
                _mode = (int)mode;
                ModeChanged?.Invoke(mode);
            }

            // Scrolling is reported separately from the cursor because zz, zt, zb and
            // the <C-e>/<C-y> pair change only what is visible. Handled before the
            // early return below, which a pure scroll would otherwise take.
            if (topLine >= 0 && Interlocked.Exchange(ref _topLine, topLine) != topLine)
                ViewportScrolled?.Invoke(topLine);

            if (line < 0 || col < 0) return;

            long packed = ((long)line << 32) | (uint)col;
            if (Interlocked.Exchange(ref _cursor, packed) == packed) return;

            CursorMoved?.Invoke(line, col);
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
                : firstc;

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
            CmdLineChanged?.Invoke(value);
        }

        internal static string AsString(object o) =>
            o is byte[] b ? Encoding.UTF8.GetString(b) : o as string ?? o?.ToString();
    }
}
