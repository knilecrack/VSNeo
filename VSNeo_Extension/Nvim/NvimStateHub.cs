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

            var mode = ParseShort(raw);
            if ((VimMode)_mode != mode)
            {
                Infrastructure.Log.Write("mode: \"" + raw + "\" -> " + mode);
                _mode = (int)mode;
                ModeChanged?.Invoke(mode);
            }

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

        private void HandleCmdlineShow(object[] evt)
        {
            // content is an array of [attr, text] chunks
            if (evt.Length == 0 || !(evt[0] is object[] chunks)) return;
            var sb = new StringBuilder();
            foreach (var c in chunks)
                if (c is object[] chunk && chunk.Length > 1) sb.Append(AsString(chunk[1]));
            SetCmdLine(sb.ToString());
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
