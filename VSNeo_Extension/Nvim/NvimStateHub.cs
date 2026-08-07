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
        private long _pendingCursor = -1;

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
            if (method != "redraw") return;

            foreach (var batchObj in args)
            {
                if (!(batchObj is object[] batch) || batch.Length == 0) continue;
                var name = AsString(batch[0]);

                // "flush" marks the end of a redraw cycle and carries no payload,
                // so it never reaches the per-event loop below.
                if (name == "flush") { FlushCursor(); continue; }

                for (int i = 1; i < batch.Length; i++)
                {
                    if (!(batch[i] is object[] evt)) continue;
                    switch (name)
                    {
                        case "mode_change": HandleModeChange(evt); break;
                        case "cmdline_show": HandleCmdlineShow(evt); break;
                        case "cmdline_hide": SetCmdLine(null); break;
                        case "win_viewport": HandleWinViewport(evt); break;
                    }
                }
            }
        }

        private void HandleModeChange(object[] evt)
        {
            if (evt.Length == 0) return;

            // The raw name matters as much as the parsed value: an unrecognised
            // name lands in Unknown, which is not Insert, which silently changes
            // what the key path does.
            var raw = AsString(evt[0]);
            var mode = Parse(raw);

            if ((VimMode)_mode == mode) return;

            Infrastructure.Log.Write("mode_change: \"" + raw + "\" -> " + mode);
            _mode = (int)mode;
            ModeChanged?.Invoke(mode);
        }

        /// <summary>
        /// win_viewport is [grid, win, topline, botline, curline, curcol,
        /// line_count, scroll_delta]. Without ext_multigrid nvim only sends it for
        /// the current window, which is the one we care about.
        /// </summary>
        private void HandleWinViewport(object[] evt)
        {
            if (evt.Length < 6) return;

            int line = ToInt(evt[4]);
            int col = ToInt(evt[5]);
            if (line < 0 || col < 0) return;

            // Held until "flush" so the caret moves once per redraw cycle rather
            // than once per intermediate state. Mode deliberately does not wait:
            // the key path reads it directly and wants the lowest latency it can get.
            Volatile.Write(ref _pendingCursor, ((long)line << 32) | (uint)col);
        }

        private void FlushCursor()
        {
            long pending = Volatile.Read(ref _pendingCursor);
            if (pending < 0) return;
            if (Interlocked.Exchange(ref _cursor, pending) == pending) return;

            CursorMoved?.Invoke((int)(pending >> 32), (int)(uint)pending);
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

        private static VimMode Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return VimMode.Unknown;
            if (s.StartsWith("insert", StringComparison.Ordinal)) return VimMode.Insert;
            if (s.StartsWith("visual", StringComparison.Ordinal)) return VimMode.Visual;
            if (s.StartsWith("replace", StringComparison.Ordinal)) return VimMode.Replace;
            if (s.StartsWith("cmdline", StringComparison.Ordinal)) return VimMode.CmdLine;
            if (s.StartsWith("operator", StringComparison.Ordinal)) return VimMode.OperatorPending;
            if (s.StartsWith("terminal", StringComparison.Ordinal)) return VimMode.Terminal;
            if (s.StartsWith("normal", StringComparison.Ordinal)) return VimMode.Normal;
            return VimMode.Unknown;
        }

        internal static string AsString(object o) =>
            o is byte[] b ? Encoding.UTF8.GetString(b) : o as string ?? o?.ToString();
    }
}
