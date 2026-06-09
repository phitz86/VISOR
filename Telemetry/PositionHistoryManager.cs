using System;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Manages per-car position history buffers for all 64 possible cars.
    /// Called every telemetry frame (60Hz), records at 10Hz (every 6th frame).
    /// Handles pit road transitions, session resets, and track length configuration.
    /// </summary>
    public class PositionHistoryManager
    {
        private const int MaxCars = 64;
        private const int DecimationInterval = 6; // 60Hz / 6 = 10Hz

        private readonly PositionHistoryBuffer[] _buffers;
        private int _frameCounter = 0;
        private float _trackLengthMeters = 0f;

        private readonly bool[] _prevOnPitRoad = new bool[MaxCars];

        public PositionHistoryManager()
        {
            _buffers = new PositionHistoryBuffer[MaxCars];
            for (int i = 0; i < MaxCars; i++)
            {
                _buffers[i] = new PositionHistoryBuffer();
            }
        }

        /// <summary>
        /// Gets the position history buffer for a specific car.
        /// </summary>
        public PositionHistoryBuffer? GetBuffer(int carIdx)
        {
            if (carIdx < 0 || carIdx >= MaxCars)
                return null;
            return _buffers[carIdx];
        }

        /// <summary>
        /// Called every telemetry frame (60Hz). Handles decimation to 10Hz,
        /// pit road transitions, and buffer recording for all valid cars.
        /// </summary>
        public void Update(SVappsLABSnapshot snapshot, ISessionDataProvider dataProvider)
        {
            if (_trackLengthMeters <= 0f)
            {
                // TrackLength is coordinator-derived, not telemetry; the former snapshot-dict
                // fallback was the same coordinator value re-packed, so it has been dropped.
                if (dataProvider is SessionDataCoordinator coordinator)
                {
                    _trackLengthMeters = coordinator.GetTrackLength();
                }

                if (_trackLengthMeters > 0f)
                {
                    for (int i = 0; i < MaxCars; i++)
                        _buffers[i].SetStationaryEpsilon(_trackLengthMeters);
                }
            }

            var onPitRoad = snapshot.CarIdxOnPitRoad;
            if (onPitRoad != null)
            {
                for (int i = 0; i < Math.Min(onPitRoad.Length, MaxCars); i++)
                {
                    // Entering pit road invalidates the buffer.
                    if (onPitRoad[i] && !_prevOnPitRoad[i])
                    {
                        _buffers[i].Clear();
                    }
                    _prevOnPitRoad[i] = onPitRoad[i];
                }
            }

            _frameCounter++;
            if (_frameCounter % DecimationInterval != 0)
                return;

            var lapDistPcts = snapshot.CarIdxLapDistPct;
            double sessionTime = snapshot.SessionTime;

            if (lapDistPcts == null || sessionTime <= 0.0)
                return;

            for (int i = 0; i < Math.Min(lapDistPcts.Length, MaxCars); i++)
            {
                if (onPitRoad != null && i < onPitRoad.Length && onPitRoad[i])
                    continue;

                float pct = lapDistPcts[i];

                if (pct < 0f || pct > 1.0f)
                    continue;

                _buffers[i].Record(sessionTime, pct);
            }
        }

        /// <summary>
        /// Returns the current track length in meters, or 0 if not yet determined.
        /// Used by the display builder for distance-in-feet calculations.
        /// </summary>
        public float TrackLengthMeters => _trackLengthMeters;

        /// <summary>
        /// Flushes all buffers and resets state. Call on session transitions.
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < MaxCars; i++)
            {
                _buffers[i].Clear();
                _prevOnPitRoad[i] = false;
            }
            _frameCounter = 0;
            _trackLengthMeters = 0f;
        }
    }
}
