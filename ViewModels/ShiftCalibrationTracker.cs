using System;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    /// <summary>Calibration status of the current gear's shift point (the dot by the ⚙).</summary>
    public enum ShiftCalibration
    {
        None,           // no shift point applies: hidden
        Calibrating,    // red: still on the car's light, or stepping up
        Settled         // green: learned and stable
    }

    /// <summary>
    /// The calibration dot's state for each gear: whether that gear's shift cue has stopped
    /// moving (<see cref="CueStability"/>). Fed every few seconds by
    /// <see cref="ShiftPointProvider"/>. UI thread only.
    /// </summary>
    internal sealed class ShiftCalibrationTracker
    {
        /// <summary>The cue currently in effect for a gear, and whether it comes from learning.</summary>
        public delegate int CueSource(int gear, out bool fromLearning);

        private readonly CueStability[] _stability = NewStability();

        // True for a gear whose next gear hasn't been driven (no ratio, live or saved): it's
        // effectively this driver's top gear here, so there's nothing to calibrate against.
        private readonly bool[] _nextGearUnused = NewFlags();

        private double _drivingSecondsSinceCheck;

        private static CueStability[] NewStability()
        {
            var a = new CueStability[ShiftPointLearner.MaxGears + 1];
            for (int i = 0; i < a.Length; i++) a[i] = new CueStability();
            return a;
        }

        private static bool[] NewFlags() { var a = new bool[ShiftPointLearner.MaxGears + 1]; Array.Fill(a, true); return a; }

        /// <summary>Only time spent actually driving counts toward a cue settling (not the pits/garage).</summary>
        public void AddDrivingTime(double seconds) => _drivingSecondsSinceCheck += seconds;

        /// <summary>
        /// Calibration status of the shift point for <paramref name="gear"/>: Settled once that
        /// gear's cue has stopped moving, Calibrating otherwise, None when no shift point applies
        /// (neutral, reverse, top gear, no car).
        /// </summary>
        public ShiftCalibration Get(int gear, PlayerCarInfo? car)
        {
            if (car == null || gear < 1 || gear >= _stability.Length) return ShiftCalibration.None;
            if (car.GearNumForward > 0 && gear >= car.GearNumForward) return ShiftCalibration.None;
            if (_nextGearUnused[gear]) return ShiftCalibration.None;
            return _stability[gear].Settled ? ShiftCalibration.Settled : ShiftCalibration.Calibrating;
        }

        public bool AllGearsSettled(PlayerCarInfo car)
        {
            int top = Math.Min(car.GearNumForward, _stability.Length);
            if (top < 2) return false;
            bool any = false;
            for (int g = 1; g < top; g++)
            {
                if (_nextGearUnused[g]) continue;    // e.g. 5th when 6th is never used here
                if (!_stability[g].Settled) return false;
                any = true;
            }
            return any;
        }

        /// <summary>Records, for each gear, whether the gear above it has been driven.</summary>
        public void SetNextGearUnused(Func<int, bool> nextGearUnused)
        {
            for (int g = 1; g < _nextGearUnused.Length; g++)
                _nextGearUnused[g] = nextGearUnused(g);
        }

        /// <summary>Feeds each gear's current cue, with the driving time since the last call.</summary>
        public void Observe(int gearNumForward, CueSource cueFor)
        {
            int top = gearNumForward > 0 ? Math.Min(gearNumForward, _stability.Length) : _stability.Length;
            for (int g = 1; g < top; g++)
            {
                int cue = cueFor(g, out bool fromLearning);
                _stability[g].Observe(cue, fromLearning, _drivingSecondsSinceCheck);
            }
            _drivingSecondsSinceCheck = 0;
        }

        /// <summary>A new car: every gear starts calibrating again.</summary>
        public void Reset()
        {
            foreach (var st in _stability) st.Reset();
            Array.Fill(_nextGearUnused, true);
            _drivingSecondsSinceCheck = 0;
        }
    }
}
