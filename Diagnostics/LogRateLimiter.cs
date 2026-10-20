using System.Diagnostics;

namespace VISOR.Diagnostics
{
    /// <summary>
    /// Lets a repeating log line through at most once per interval, per key, and counts the
    /// repeats it held back so the next line that gets through can say how many there were.
    /// Thread-safe.
    /// </summary>
    internal sealed class LogRateLimiter
    {
        private readonly TimeSpan _interval;
        private readonly Func<TimeSpan> _now;
        private readonly object _lock = new();
        private readonly Dictionary<string, (TimeSpan LastShown, int Suppressed)> _byKey = new();

        public LogRateLimiter(TimeSpan interval)
            : this(interval, StartClock())
        {
        }

        /// <param name="now">Elapsed-time clock; tests pass their own.</param>
        public LogRateLimiter(TimeSpan interval, Func<TimeSpan> now)
        {
            _interval = interval;
            _now = now;
        }

        private static Func<TimeSpan> StartClock()
        {
            var sw = Stopwatch.StartNew();
            return () => sw.Elapsed;
        }

        /// <summary>
        /// True if a line with this key should be logged now; <paramref name="suppressed"/> is how
        /// many were held back since the last one that was. False (and counted) otherwise.
        /// </summary>
        public bool TryPass(string key, out int suppressed)
        {
            var now = _now();
            lock (_lock)
            {
                if (_byKey.TryGetValue(key, out var entry) && now - entry.LastShown < _interval)
                {
                    _byKey[key] = (entry.LastShown, entry.Suppressed + 1);
                    suppressed = 0;
                    return false;
                }
                suppressed = entry.Suppressed;   // 0 for a key not seen before
                _byKey[key] = (now, 0);
                return true;
            }
        }
    }
}
