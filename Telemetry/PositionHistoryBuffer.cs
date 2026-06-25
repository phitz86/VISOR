using System;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Fixed-size circular buffer storing (SessionTime, LapDistPct) samples for a single car.
    /// Supports backward search to find when the car crossed a given track position,
    /// with linear interpolation for sub-tick precision.
    ///
    /// Buffer is designed for 10Hz sampling. Its depth sets the maximum gap that can be resolved:
    /// the relative display shows the nearest cars ahead/behind, which may be up to half a lap away,
    /// and half a lap on the longest tracks (Le Mans, Nordschleife) runs to a few minutes. Sized for
    /// 4 minutes of history so those gaps measure correctly instead of saturating at the depth.
    /// Memory per buffer: 2400 × 16 bytes ≈ 38 KB (≈ 2.4 MB across all 64 cars).
    /// </summary>
    public class PositionHistoryBuffer
    {
        /// <summary>
        /// A single timestamped track position sample.
        /// </summary>
        public struct Entry
        {
            public double SessionTime;
            public float LapDistPct;
        }

        private const int SampleRateHz = 10;
        private const int HistorySeconds = 240; // 4 min — covers a half-lap on long tracks (Le Mans, Nordschleife)
        private const int BufferSize = SampleRateHz * HistorySeconds; // 2400 entries
        private const float TeleportThreshold = 0.25f;
        private const float SFWrapHighThreshold = 0.9f;
        private const float SFWrapLowThreshold = 0.1f;
        private const double DiscontinuityThreshold = 0.3; // seconds

        private readonly Entry[] _entries = new Entry[BufferSize];
        private int _head = 0;
        private int _count = 0;

        // Overwritten by SetStationaryEpsilon once track length is known.
        private float _stationaryEpsilon = 0.001f;

        /// <summary>
        /// Sets the stationary detection epsilon based on track length.
        /// Epsilon = 3.35m / trackLengthMeters (equivalent to ~15mph over 0.5s).
        /// </summary>
        public void SetStationaryEpsilon(float trackLengthMeters)
        {
            if (trackLengthMeters > 0)
                _stationaryEpsilon = 3.35f / trackLengthMeters;
        }

        /// <summary>
        /// Number of valid entries currently in the buffer.
        /// </summary>
        public int Count => _count;

        /// <summary>
        /// Records a new position sample. Detects teleports and flushes the buffer if one is found.
        /// Returns false if a teleport was detected (caller should skip this tick's entry).
        /// </summary>
        public bool Record(double sessionTime, float lapDistPct)
        {
            if (_count > 0)
            {
                var prev = GetEntry(_count - 1);
                float delta = Math.Abs(lapDistPct - prev.LapDistPct);

                // Large jump that isn't an S/F crossing means the car teleported (tow, reset).
                if (delta > TeleportThreshold && !IsSFWrap(prev.LapDistPct, lapDistPct))
                {
                    Clear();
                    return false;
                }
            }

            _entries[_head] = new Entry { SessionTime = sessionTime, LapDistPct = lapDistPct };
            _head = (_head + 1) % BufferSize;
            if (_count < BufferSize)
                _count++;

            return true;
        }

        /// <summary>
        /// Clears all entries from the buffer (used on teleport, pit entry, session transition).
        /// </summary>
        public void Clear()
        {
            _head = 0;
            _count = 0;
        }

        /// <summary>
        /// Checks if the car is stationary: total LapDistPct movement over the last 0.5s
        /// (5 entries at 10Hz) is less than epsilon.
        /// </summary>
        public bool IsStationary()
        {
            const int stationaryWindowEntries = 5; // 0.5s at 10Hz
            if (_count < stationaryWindowEntries)
                return false;

            var oldest = GetEntry(_count - stationaryWindowEntries);
            var newest = GetEntry(_count - 1);

            float totalMovement = Math.Abs(newest.LapDistPct - oldest.LapDistPct);

            // S/F wrap.
            if (totalMovement > 0.5f)
                totalMovement = 1.0f - totalMovement;

            return totalMovement < _stationaryEpsilon;
        }

        /// <summary>
        /// Searches backward through the buffer to find when this car crossed the given
        /// track position. Returns the interpolated SessionTime, or null if no valid
        /// crossing is found within the buffer history.
        ///
        /// The search finds the MOST RECENT crossing (scanning newest → oldest).
        /// </summary>
        public double? FindCrossingTime(float targetPct)
        {
            if (_count < 2)
                return null;

            // Walk newest → oldest looking for two adjacent entries that bracket targetPct.
            for (int i = _count - 1; i > 0; i--)
            {
                var current = GetEntry(i);
                var previous = GetEntry(i - 1);

                // Skip samples with a telemetry gap — interpolation would be unreliable.
                if (current.SessionTime - previous.SessionTime > DiscontinuityThreshold)
                    continue;

                float prevPct = previous.LapDistPct;
                float currPct = current.LapDistPct;

                bool isSFCrossing = IsSFWrap(prevPct, currPct);

                if (isSFCrossing)
                {
                    // Unwrap so both samples lie on the same 0..2 scale, then try the target in both frames.
                    if (currPct < prevPct)
                        currPct += 1.0f;
                    else
                        prevPct += 1.0f;

                    float target = targetPct;
                    float targetAlt = targetPct + 1.0f;

                    double? result = TryInterpolate(prevPct, currPct, previous.SessionTime, current.SessionTime, target);
                    if (result.HasValue) return result;

                    result = TryInterpolate(prevPct, currPct, previous.SessionTime, current.SessionTime, targetAlt);
                    if (result.HasValue) return result;
                }
                else
                {
                    double? result = TryInterpolate(prevPct, currPct, previous.SessionTime, current.SessionTime, targetPct);
                    if (result.HasValue) return result;
                }
            }

            return null;
        }

        /// <summary>
        /// Attempts linear interpolation if target falls between pctA and pctB.
        /// Returns interpolated session time, or null if target is not bracketed.
        /// </summary>
        private static double? TryInterpolate(float pctA, float pctB, double timeA, double timeB, float target)
        {
            float min = Math.Min(pctA, pctB);
            float max = Math.Max(pctA, pctB);

            if (target < min || target > max)
                return null;

            float range = pctB - pctA;
            if (Math.Abs(range) < 1e-9f)
                return timeB;

            float t = (target - pctA) / range;
            return timeA + t * (timeB - timeA);
        }

        /// <summary>
        /// Returns true if the transition from pctA to pctB is an S/F line crossing
        /// (one value near 1.0 and the other near 0.0).
        /// </summary>
        private static bool IsSFWrap(float pctA, float pctB)
        {
            return (pctA > SFWrapHighThreshold && pctB < SFWrapLowThreshold) ||
                   (pctB > SFWrapHighThreshold && pctA < SFWrapLowThreshold);
        }

        /// <summary>
        /// Gets the entry at the given logical index (0 = oldest, count-1 = newest).
        /// </summary>
        private Entry GetEntry(int logicalIndex)
        {
            int actualIndex = (_head - _count + logicalIndex + BufferSize) % BufferSize;
            return _entries[actualIndex];
        }
    }
}
