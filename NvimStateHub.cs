using System;
using System.Text;

namespace VSNeo.Nvim
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

        public VimMode Mode => (VimMode)_mode;

        /// <summary>Current ext_cmdline content, or null when no command line is open.</summary>
        public string CmdLine { get; private set; }

        public event Action<VimMode> ModeChanged;
        public event Action<string> CmdLineChanged;

        /// <summary>Called from the RPC read thread. Keep it allocation-light and non-blocking.</summary>
        public void OnNotification(string method, object[] args)
        {
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
                        case "mode_change": HandleModeChange(evt); break;
                        case "cmdline_show": HandleCmdlineShow(evt); break;
                        case "cmdline_hide": SetCmdLine(null); break;
                    }
                }
            }
        }

        private void HandleModeChange(object[] evt)
        {
            if (evt.Length == 0) return;
            var mode = Parse(AsString(evt[0]));
            if ((VimMode)_mode == mode) return;
            _mode = (int)mode;
            ModeChanged?.Invoke(mode);
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
