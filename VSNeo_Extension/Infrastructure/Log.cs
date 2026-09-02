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
    /// Writes are queued and flushed by a background thread. The mode cache the
    /// key path reads is published from the RPC read thread, and a synchronous
    /// open/write/close per mode change sat directly in front of that publish -
    /// ~0.1-0.5 ms of disk I/O, worse under AV, on the thread whose latency the
    /// whole swallow-vs-passthrough decision rides on.
    ///
    /// Lifecycle only, still. Nothing here may be called from the key path -
    /// enqueueing is cheap but not free, and the zero-I/O invariant is about
    /// never having to ask.
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();

        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Pending =
            new System.Collections.Concurrent.ConcurrentQueue<string>();
        private static readonly System.Threading.AutoResetEvent Signal =
            new System.Threading.AutoResetEvent(false);
        private static int _writerStarted;

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
                Pending.Enqueue(line);
                if (System.Threading.Interlocked.CompareExchange(ref _writerStarted, 1, 0) == 0)
                {
                    var thread = new System.Threading.Thread(Drain)
                    {
                        IsBackground = true,
                        Name = "VSNeo log writer",
                    };
                    thread.Start();
                }
                Signal.Set();
            }
            catch
            {
                // Diagnostics must never be the reason the extension fails.
            }
        }

        /// <summary>
        /// Empties the queue in batches, one file append per batch. Never exits:
        /// it is a background thread for the life of the process. The try around
        /// the whole batch keeps a throw (OOM in Append, anything) from killing
        /// the drainer silently - logging would stop for the session with no
        /// evidence, which is worse than losing one batch.
        /// </summary>
        private static void Drain()
        {
            while (true)
            {
                Signal.WaitOne();

                try
                {
                    FlushPending();
                }
                catch
                {
                    // Same rule as Write: diagnostics never take the extension down.
                }
            }
        }

        private static void FlushPending()
        {
            var batch = new StringBuilder();
            while (Pending.TryDequeue(out var line)) batch.Append(line);
            if (batch.Length == 0) return;

            lock (Gate) File.AppendAllText(Path, batch.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// Synchronous drain, for shutdown. The drainer thread is background and
        /// dies with the process, so the last lines of a session - usually the
        /// ones that matter - would otherwise be lost whenever the exit outruns
        /// the 40ms-ish batching. Called from the package's Dispose.
        /// </summary>
        public static void Flush()
        {
            try
            {
                FlushPending();
            }
            catch
            {
                // Same rule as Write.
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
        /// Release entirely. The write itself is a queue enqueue (the background
        /// drainer owns the file), so the cost when enabled is one string format
        /// per keystroke. Useful for answering "did the key reach us at all", which
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
