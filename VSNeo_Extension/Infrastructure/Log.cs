using System;
using System.IO;
using System.Text;

namespace VSNeo_Extension.Infrastructure
{
    /// <summary>
    /// Lifecycle diagnostics to a plain file. Deliberately not the VS output
    /// window: the interesting events all happen during background package load,
    /// before any pane exists to write to, and the failure we most need to see is
    /// the one where the package never loads at all.
    ///
    /// Lifecycle only. Nothing here may be called from the key path - that path is
    /// required to do zero I/O, and a log write per keystroke would be exactly the
    /// stall this design exists to avoid.
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();

        // Every VS instance with the extension shares this one file, and interleaved
        // lines from two of them read exactly like a single session going mad: one
        // instance's "pipe closed" next to the other's traffic counters, with totals
        // that "reset". The pid on every line is what tells them apart.
        private static readonly int Pid = System.Diagnostics.Process.GetCurrentProcess().Id;

        public static readonly string Path =
            System.IO.Path.Combine(
                Environment.GetEnvironmentVariable("TEMP") ?? System.IO.Path.GetTempPath(),
                "vsneo.log");

        public static void Write(string message)
        {
            try
            {
                var line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + Pid + "] "
                           + message + Environment.NewLine;
                lock (Gate) File.AppendAllText(Path, line, Encoding.UTF8);
            }
            catch
            {
                // Diagnostics must never be the reason the extension fails.
            }
        }

        public static void Write(string message, Exception ex) =>
            Write(message + " -> " + (ex == null ? "(none)" : ex.GetType().Name + ": " + ex.Message));

        /// <summary>
        /// Opt in with VSNEO_TRACE_KEYS=1. Read once: an environment lookup per
        /// keystroke would be its own small tax.
        /// </summary>
        private static readonly bool TraceKeys =
            Environment.GetEnvironmentVariable("VSNEO_TRACE_KEYS") == "1";

        /// <summary>
        /// Key-path tracing, off unless explicitly switched on, and compiled out of
        /// Release entirely. This writes a file line per keystroke - roughly a
        /// quarter of a millisecond of synchronous I/O on the UI thread, holding a
        /// lock - which is precisely the stall the zero-I/O invariant exists to
        /// prevent. Useful for answering "did the key reach us at all", which
        /// nothing else can answer; not something to leave running.
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        public static void Key(string message)
        {
            if (!TraceKeys) return;
            Write("  key: " + message);
        }

        /// <summary>Marks a session boundary in the shared file.</summary>
        public static void Begin(string header)
        {
            try
            {
                lock (Gate)
                {
                    // Two instances share this file. The second one's Begin must not
                    // wipe the first's history - that is how a routine shutdown once
                    // read as a mid-session nvim crash. Only a runaway file is reset.
                    if (File.Exists(Path) && new FileInfo(Path).Length > 1024 * 1024)
                        File.WriteAllText(Path, string.Empty);

                    File.AppendAllText(
                        Path,
                        "=== " + header + " " + DateTime.Now + " (pid " + Pid + ") ==="
                        + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
