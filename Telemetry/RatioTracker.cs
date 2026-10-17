using System;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Rolling median of a gear's RPM/speed ratio. A median rather than a mean so brief
    /// wheelspin or a locked wheel doesn't drag the estimate.
    /// </summary>
    internal sealed class RatioTracker
    {
        private const int Capacity = 240;   // ~4 s at 60 Hz
        private const int RecomputeEvery = 30;
        private readonly double[] _buf = new double[Capacity];
        private readonly double[] _scratch = new double[Capacity];
        private int _next;
        private int _sinceMedian = RecomputeEvery;
        private double _median;

        public int Count { get; private set; }

        public void Add(double v)
        {
            _buf[_next] = v;
            _next = (_next + 1) % Capacity;
            if (Count < Capacity) Count++;
            if (_sinceMedian < RecomputeEvery) _sinceMedian++;
        }

        public double Median
        {
            get
            {
                // Recompute at most every RecomputeEvery adds; the ratio doesn't change mid-gear.
                if (_sinceMedian >= RecomputeEvery && Count > 0)
                {
                    Array.Copy(_buf, _scratch, Count);
                    Array.Sort(_scratch, 0, Count);
                    _median = _scratch[Count / 2];
                    _sinceMedian = 0;
                }
                return _median;
            }
        }
    }
}
