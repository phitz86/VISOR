using System;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Fixed-size circular buffer storing (SessionTime, LapDistPct) samples for a single car.
    /// Supports backward search to find when the car crossed a given track position,
    /// with linear interpolation for sub-tick precision.
    ///
    /// Buffer is designed for 10Hz sampling with 30 seconds of history (300 entries).
    /// Memory per buffer: 300 × 12 bytes = 3.6 KB.
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

        private const int BufferSize = 300; // 30 seconds at 10Hz
        private const float TeleportThreshold = 0.25f;
        private const float SFWrapHighThreshold = 0.9f;
        private const float SFWrapLowThreshold = 0.1f;
        private const double DiscontinuityThreshold = 0.3; // seconds

        private readonly Entry[] _entries = new Entry[BufferSize];
        private int _head = 0;   // Next write position
        private int _count = 0;  // Number of valid entries

        // Stationary detection
        private float _stationaryEpsilon = 0.001f; // Default; set via SetStationaryEpsilon

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
            // Teleport detection: compare against most recent entry
            if (_count > 0)
            {
                var prev = GetEntry(_count - 1);
                float delta = Math.Abs(lapDistPct - prev.LapDistPct);

                // Check for teleport: large jump that isn't an S/F crossing
                if (delta > TeleportThreshold && !IsSFWrap(prev.LapDistPct, lapDistPct))
                {
                    Clear();
                    return false; // Signal teleport — skip recording this tick
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

            // Compare the oldest entry in the window to the newest
            var oldest = GetEntry(_count - stationaryWindowEntries);
            var newest = GetEntry(_count - 1);

            float totalMovement = Math.Abs(newest.LapDistPct - oldest.LapDistPct);

            // Handle S/F wrap in the movement check
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

            // Scan backward from newest to oldest, looking for two adjacent entries
            // that bracket the target position
            for (int i = _count - 1; i > 0; i--)
            {
                var current = GetEntry(i);
                var previous = GetEntry(i - 1);

                // Discontinuity check: if too much time between samples, don't interpolate
                if (current.SessionTime - previous.SessionTime > DiscontinuityThreshold)
                    continue;

                // Determine the movement from previous → current
                float prevPct = previous.LapDistPct;
                float currPct = current.LapDistPct;

                // Handle S/F wrap: if the car crossed the start/finish line between these two samples
                bool isSFCrossing = IsSFWrap(prevPct, currPct);

                if (isSFCrossing)
                {
                    // Unwrap: shift the lower value up by 1.0 so both are on the same scale
                    if (currPct < prevPct)
                        currPct += 1.0f;
                    else
                        prevPct += 1.0f;

                    // The target might also need unwrapping to fall within range
                    float target = targetPct;
                    float targetAlt = targetPct + 1.0f;

                    // Try both the original and unwrapped target
                    double? result = TryInterpolate(prevPct, currPct, previous.SessionTime, current.SessionTime, target);
                    if (result.HasValue) return result;

                    result = TryInterpolate(prevPct, currPct, previous.SessionTime, current.SessionTime, targetAlt);
                    if (result.HasValue) return result;
                }
                else
                {
                    // Normal case: no wrap
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
            // Check if target is bracketed (between pctA and pctB, inclusive of direction)
            float min = Math.Min(pctA, pctB);
            float max = Math.Max(pctA, pctB);

            if (target < min || target > max)
                return null;

            float range = pctB - pctA;
            if (Math.Abs(range) < 1e-9f)
                return timeB; // Essentially the same position, return the newer time

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
