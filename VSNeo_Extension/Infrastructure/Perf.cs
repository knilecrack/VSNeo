using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows.Threading;

namespace VSNeo_Extension.Infrastructure
{
    /// <summary>
    /// Where a keystroke's time goes, written to %TEMP%\vsneo.log.
    ///
    /// Three measurements, all cheap enough to leave on:
    /// <list type="bullet">
    /// <item>Key to caret - from sending a key to nvim until the caret lands
    /// for it: what the user feels. Reported as percentiles every 25 moves.</item>
    /// <item>Slow UI handlers - our UI-thread handlers time themselves and log
    /// only a run over <see cref="SlowUiMs"/> (a 60 Hz frame), by name. Silence
    /// means none of ours was slow.</item>
    /// <item>UI stalls - a watchdog posts a no-op to the UI thread every
    /// <see cref="ProbeIntervalMs"/> ms at the highest priority and logs when
    /// it waited over <see cref="StallMs"/> ms. A stall with no slow handler
    /// of ours logged just before it was Visual Studio's own work.</item>
    /// </list>
    ///
    /// Timing is Stopwatch ticks and field writes; the log write only happens
    /// for a slow event or a full window, and Log itself queues to a
    /// background writer, so none of this adds I/O to the UI thread.
    /// </summary>
    internal static class Perf
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static long Now => Clock.ElapsedTicks;

        public static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

        private static string F1(double ms) => ms.ToString("F1", CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------- key -> caret

        // Written by whichever thread sends a key, consumed on the UI thread.
        private static long _keySentTicks;

        // UI thread only (CaretLanded runs there).
        private static readonly LatencyStats KeyToCaret = new LatencyStats(25);

        // A key that moved nothing (i, :, a mapping that runs a VS command)
        // leaves its stamp behind; a caret landing this much later was caused
        // by something else and must not be charged to it.
        private const double StaleKeyMs = 1000;

        /// <summary>A key was just sent to nvim. Any thread; one field write.</summary>
        public static void KeySent() => Volatile.Write(ref _keySentTicks, Now);

        /// <summary>
        /// The caret just moved to a position nvim reported. UI thread. Charges
        /// the time to the latest key sent, once: holding j measures each landing
        /// against the repeat that caused it.
        /// </summary>
        public static void CaretLanded()
        {
            long sent = Interlocked.Exchange(ref _keySentTicks, 0);
            if (sent == 0) return;

            double ms = Ms(sent, Now);
            if (ms > StaleKeyMs) return;

            if (KeyToCaret.Add(ms))
                Log.Write("key->caret over 25 moves: " + KeyToCaret.TakeSummary());
        }

        // ---------------------------------------------------------------- slow handlers

        /// <summary>One 60 Hz frame. A UI-thread handler slower than this drops a frame.</summary>
        public const double SlowUiMs = 16;

        /// <summary>
        /// <c>using var perf = Perf.Time("Class.Method");</c> at the top of a
        /// UI-thread handler. A struct, so timing allocates nothing.
        /// </summary>
        public static Scope Time(string name) => new Scope(name);

        internal readonly struct Scope : IDisposable
        {
            private readonly string _name;
            private readonly long _start;

            public Scope(string name)
            {
                _name = name;
                _start = Now;
            }

            public void Dispose()
            {
                double ms = Ms(_start, Now);
                if (ms >= SlowUiMs) Log.Write("slow ui: " + _name + " took " + F1(ms) + " ms");
            }
        }

        // ---------------------------------------------------------------- stall watchdog

        public const int ProbeIntervalMs = 200;
        public const double StallMs = 100;

        private static int _watchdogStarted;
        private static int _probeOutstanding;
        private static long _probeSentTicks;
        private static Timer? _watchdog;   // held so the timer is not collected

        /// <summary>
        /// Starts the UI-stall watchdog, once per process. Call with the UI
        /// thread's dispatcher. The probe is posted at Send priority, ahead of
        /// every queued item, so its wait is how long the UI thread was stuck
        /// inside whatever was running - a lower bound, since the stall may have
        /// begun before the probe was posted.
        /// </summary>
        public static void StartWatchdog(Dispatcher dispatcher)
        {
            if (Interlocked.CompareExchange(ref _watchdogStarted, 1, 0) != 0) return;

            Action probe = () =>
            {
                double ms = Ms(Volatile.Read(ref _probeSentTicks), Now);
                Volatile.Write(ref _probeOutstanding, 0);
                if (ms >= StallMs)
                    Log.Write("ui stall: UI thread unresponsive for at least " + F1(ms) + " ms");
            };

            _watchdog = new Timer(_ =>
            {
                // One probe in flight at a time: while it waits, the stall is
                // still going on, and it is measured when the probe finally runs.
                if (Interlocked.CompareExchange(ref _probeOutstanding, 1, 0) != 0) return;
                Volatile.Write(ref _probeSentTicks, Now);
                try
                {
#pragma warning disable VSTHRD001 // the priority is the point: Send jumps the queue
                    dispatcher.BeginInvoke(DispatcherPriority.Send, probe);
#pragma warning restore VSTHRD001
                }
                catch
                {
                    // Dispatcher shut down: VS is closing. Nothing to measure.
                    Volatile.Write(ref _probeOutstanding, 0);
                }
            }, null, ProbeIntervalMs, ProbeIntervalMs);
        }
    }
}
