#if DEBUG
using System;
using System.Collections.Generic;
using VISOR.Telemetry;

namespace VISOR.Diagnostics
{
    /// <summary>
    /// DEBUG-only migration tripwire. During the Option Y typed-snapshot migration the legacy
    /// dictionary (GetValue&lt;T&gt;) and the new typed accessors coexist on every snapshot. This
    /// compares the two for every migrated field each frame and logs a [TypedDiff] warning the
    /// first time any field disagrees — proving the typed accessors return values identical to the
    /// dict path across every session state that can't be watched by eye. A periodic heartbeat
    /// confirms it is actively checking and clean, so silence means "verified", not "never ran".
    /// Deleted alongside the dict in the final cleanup step.
    /// </summary>
    public class SnapshotConsistencyChecker
    {
        private const float FloatTolerance = 1e-3f;
        private const int HeartbeatFrames = 3600; // ~60s at 60Hz

        private readonly HashSet<string> _reported = new();
        private long _framesChecked;
        private long _framesSinceHeartbeat;
        private int _mismatchFields;

        public SnapshotConsistencyChecker()
        {
            Log.Info("[TypedDiff] consistency checker active (DEBUG) — comparing typed accessors against the dict each frame");
        }

        public void Check(SVappsLABSnapshot s)
        {
            if (s == null) return;

            // Defaults below mirror each typed accessor's fallback so a null frame compares
            // apples-to-apples; a mismatch then means a genuine typed-vs-dict divergence.
            CompareInt(s, "PlayerCarIdx", s.PlayerCarIdx, -1);
            CompareFloat(s, "FuelLevel", s.FuelLevel, 0f);
            CompareFloat(s, "FuelUsePerHour", s.FuelUsePerHour, 0f);
            CompareInt(s, "Lap", s.Lap, 0);
            CompareInt(s, "Gear", s.Gear, 0);
            CompareFloat(s, "Speed", s.Speed, 0f);
            CompareFloat(s, "RPM", s.RPM, 0f);
            CompareFloat(s, "LapCurrentLapTime", s.LapCurrentLapTime, 0f);
            CompareFloat(s, "LapLastLapTime", s.LapLastLapTime, 0f);
            CompareFloat(s, "LapBestLapTime", s.LapBestLapTime, 0f);
            CompareFloat(s, "LapDeltaToBestLap", s.LapDeltaToBestLap, 0f);
            CompareFloat(s, "LapDeltaToOptimalLap", s.LapDeltaToOptimalLap, 0f);
            CompareFloat(s, "LapDeltaToSessionBestLap", s.LapDeltaToSessionBestLap, 0f);
            CompareDouble(s, "SessionTime", s.SessionTime, 0.0);
            CompareDouble(s, "SessionTimeRemain", s.SessionTimeRemain, 0.0);
            CompareInt(s, "SessionLapsRemain", s.SessionLapsRemain, 0);
            CompareInt(s, "SessionLapsTotal", s.SessionLapsTotal, 0);
            CompareInt(s, "SessionNum", s.SessionNum, 0);
            CompareInt(s, "SessionState", s.SessionState, -1);
            CompareInt(s, "SessionFlags", s.SessionFlags, 0);

            _framesChecked++;
            if (++_framesSinceHeartbeat >= HeartbeatFrames)
            {
                Log.Info($"[TypedDiff] {_framesChecked} frames checked, {_mismatchFields} field(s) ever mismatched");
                _framesSinceHeartbeat = 0;
            }
        }

        private void CompareInt(SVappsLABSnapshot s, string field, int typed, int dictDefault)
        {
            int dict = s.GetValue<int>(field, dictDefault);
            if (typed != dict) Report(field, typed.ToString(), dict.ToString());
        }

        private void CompareFloat(SVappsLABSnapshot s, string field, float typed, float dictDefault)
        {
            float dict = s.GetValue<float>(field, dictDefault);
            if (Math.Abs(typed - dict) > FloatTolerance) Report(field, typed.ToString("R"), dict.ToString("R"));
        }

        private void CompareDouble(SVappsLABSnapshot s, string field, double typed, double dictDefault)
        {
            double dict = s.GetValue<double>(field, dictDefault);
            if (Math.Abs(typed - dict) > FloatTolerance) Report(field, typed.ToString("R"), dict.ToString("R"));
        }

        private void Report(string field, string typed, string dict)
        {
            if (_reported.Add(field))
            {
                _mismatchFields++;
                Log.Warning($"[TypedDiff] {field} MISMATCH: typed={typed} dict={dict} (logged once)");
            }
        }
    }
}
#endif
