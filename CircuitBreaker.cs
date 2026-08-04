using System;
using System.Threading;

namespace VSNeo.Infrastructure
{
    /// <summary>
    /// Session-level enable/disable. Fallback is never a per-keystroke decision:
    /// half-swallowed input leaves the two buffers drifting apart and is worse
    /// than being switched off. When this trips, VSNeo stops intercepting
    /// entirely and Visual Studio input goes back to normal.
    /// </summary>
    internal sealed class CircuitBreaker
    {
        private readonly int _threshold;
        private readonly TimeSpan _cooldown;
        private int _failures;
        private int _open;
        private DateTime _openedAtUtc;

        public CircuitBreaker(int threshold = 3, TimeSpan? cooldown = null)
        {
            _threshold = threshold;
            _cooldown = cooldown ?? TimeSpan.FromSeconds(30);
        }

        public bool IsClosed
        {
            get
            {
                if (Volatile.Read(ref _open) == 0) return true;
                if (DateTime.UtcNow - _openedAtUtc < _cooldown) return false;
                Reset();
                return true;
            }
        }

        public Exception LastFault { get; private set; }

        /// <summary>Raised with true when VSNeo is live, false when it has fallen back.</summary>
        public event Action<bool> StateChanged;

        public void Trip(Exception ex)
        {
            LastFault = ex;
            if (Interlocked.Increment(ref _failures) < _threshold) return;
            if (Interlocked.Exchange(ref _open, 1) == 1) return;
            _openedAtUtc = DateTime.UtcNow;
            StateChanged?.Invoke(false);
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _failures, 0);
            if (Interlocked.Exchange(ref _open, 0) == 1) StateChanged?.Invoke(true);
        }
    }
}
