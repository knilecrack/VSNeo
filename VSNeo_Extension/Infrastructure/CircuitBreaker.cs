using System;
using System.Threading;

namespace VSNeo_Extension.Infrastructure
{
    /// <summary>
    /// Session-level enable/disable. Fallback is never a per-keystroke decision:
    /// half-swallowed input leaves the two buffers drifting apart and is worse
    /// than being switched off. When this trips, VSNeo stops intercepting
    /// entirely and Visual Studio input goes back to normal.
    ///
    /// Opening is one-way for the session. There is no reconnect path, so a
    /// cooldown that closed the breaker again only ever produced a phantom
    /// "ready" - and it ran from <see cref="IsClosed"/>, which the key handler
    /// reads: the first keystroke after the cooldown raised StateChanged,
    /// fanned out to every subscriber and posted a status-bar update, from
    /// inside PreviewKeyDown. The getter is a plain read now; only
    /// <see cref="Reset"/> closes it, and only a successful start calls that.
    /// </summary>
    internal sealed class CircuitBreaker
    {
        private readonly int _threshold;
        private int _failures;
        private int _open;

        public CircuitBreaker(int threshold = 3)
        {
            _threshold = threshold;
        }

        /// <summary>Read on the key path: a volatile load, nothing else.</summary>
        public bool IsClosed => Volatile.Read(ref _open) == 0;

        public Exception? LastFault { get; private set; }

        /// <summary>Raised with true when VSNeo is live, false when it has fallen back.</summary>
        public event Action<bool>? StateChanged;

        public void Trip(Exception ex)
        {
            LastFault = ex;
            if (Interlocked.Increment(ref _failures) < _threshold) return;
            if (Interlocked.Exchange(ref _open, 1) == 1) return;
            StateChanged?.Invoke(false);
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _failures, 0);
            if (Interlocked.Exchange(ref _open, 0) == 1) StateChanged?.Invoke(true);
        }
    }
}
