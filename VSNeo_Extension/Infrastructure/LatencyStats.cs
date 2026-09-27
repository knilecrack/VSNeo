using System;

namespace VSNeo_Extension.Infrastructure
{
    /// <summary>
    /// A fixed window of latency samples, summarised as median, 95th percentile
    /// and max once full. Averages hide exactly what makes an editor feel slow -
    /// one 800 ms stall among twenty 10 ms moves averages to 50 ms, which reads
    /// as "fine" - so the log reports the tail instead.
    ///
    /// Single-threaded by contract (the caller's thread owns it). No allocation
    /// per sample; the sort happens once per window, on a copy.
    /// </summary>
    internal sealed class LatencyStats
    {
        private readonly double[] _samples;
        private readonly double[] _sorted;
        private int _count;

        public LatencyStats(int window)
        {
            if (window < 1) throw new ArgumentOutOfRangeException(nameof(window));
            _samples = new double[window];
            _sorted = new double[window];
        }

        public int Count => _count;

        /// <summary>Adds one sample; true when the window just filled.</summary>
        public bool Add(double ms)
        {
            if (_count < _samples.Length) _samples[_count++] = ms;
            return _count == _samples.Length;
        }

        /// <summary>"p50 12.3 ms, p95 45.6 ms, max 78.9 ms"; resets the window.</summary>
        public string TakeSummary()
        {
            if (_count == 0) return "no samples";

            Array.Copy(_samples, _sorted, _count);
            Array.Sort(_sorted, 0, _count);
            string summary = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "p50 {0:F1} ms, p95 {1:F1} ms, max {2:F1} ms",
                Percentile(0.50), Percentile(0.95), _sorted[_count - 1]);
            _count = 0;
            return summary;
        }

        // Nearest-rank: the smallest sample with at least p of the window at
        // or below it. With a 25-sample window p95 is the 24th - the second
        // worst - so one freak stall shows as max without owning p95 too.
        private double Percentile(double p)
        {
            int rank = (int)Math.Ceiling(p * _count);
            if (rank < 1) rank = 1;
            return _sorted[rank - 1];
        }
    }
}
