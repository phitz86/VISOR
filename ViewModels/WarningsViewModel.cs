using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using VISOR.Diagnostics;

namespace VISOR.ViewModels
{
    public class WarningsViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #region Tuning constants
        // All pace thresholds are expressed as a fraction of the healthy baseline lap time so
        // they travel across tracks (1s means nothing at the Nordschleife, everything on a short
        // oval). These are first-pass values and are expected to need on-track tuning.

        private const int MIN_BASELINE_LAPS = 3;        // clean laps before any warning can fire
        private const float BASELINE_CLUSTER_PCT = 0.015f; // laps within 1.5% of fastest define "good pace"
        private const float GRAPH_ON_PCT = 0.015f;      // deficit to raise the pace-loss graph
        private const float GRAPH_OFF_PCT = 0.008f;     // deficit to clear it (hysteresis)
        private const float OUTLIER_PCT = 0.10f;        // laps >10% over baseline are junk (spin/off)
        private const float TOPSPEED_DROP_PCT = 0.03f;  // demoted: damage-confidence booster only
        private const float ABS_FLOOR_SEC = 0.3f;       // absolute deficit floor, keeps short tracks calm
        private const int SUSTAIN_LAPS = 2;             // laps a condition must hold before it acts
        private const int STINT_WARMUP_SKIP = 1;        // ignore the cold first lap of each stint
        private const int CLEAN_LAP_POOL_CAP = 50;      // bound the baseline sample list

        // Cost side of the pit decision. PIT_COST_BASE is a road-course-ballpark pit-lane delta plus
        // minimal service; it is the least track-independent number here and the prime tuning knob.
        private const float PIT_COST_BASE_SEC = 30f;
        private const float PIT_COST_REPAIR_ADDER_SEC = 30f; // added when damage is confirmed
        private const float PIT_MARGIN = 0.15f;         // keeps PIT from flickering at the boundary
        private const int LAPS_REMAIN_SENTINEL = 9000;  // iRacing reports a huge value for "unlimited"
        #endregion

        #region Incident display (unchanged)
        private int _incidentCount = 0;
        private string _incidentDisplay = "0x";
        private Brush _incidentColor = Brushes.White;

        public string IncidentDisplay { get => _incidentDisplay; private set { _incidentDisplay = value; OnPropertyChanged(); } }
        public Brush IncidentColor { get => _incidentColor; private set { _incidentColor = value; OnPropertyChanged(); } }
        #endregion

        #region Warning indicators
        private bool _isPaceWarningVisible = false; // 📉 graph: you're losing time (any cause)
        private bool _isPersistentDotVisible = false; // • dot: confirmed damage, latched until pit
        private bool _isPitNowVisible = false;      // PIT: the time math says box now

        public bool IsPaceWarningVisible { get => _isPaceWarningVisible; private set { _isPaceWarningVisible = value; OnPropertyChanged(); } }
        public bool IsPersistentDotVisible { get => _isPersistentDotVisible; private set { _isPersistentDotVisible = value; OnPropertyChanged(); } }
        public bool IsPitNowVisible { get => _isPitNowVisible; private set { _isPitNowVisible = value; OnPropertyChanged(); } }
        public string PitNowText => "PIT";
        #endregion

        #region Pace state machine
        private enum HealthState { Warmup, Healthy, PaceLoss }
        private HealthState _state = HealthState.Warmup;

        private readonly List<float> _cleanLapTimes = new();  // healthy-lap pool for the baseline
        private readonly List<float> _recentLapTimes = new(); // smoothing window (last SUSTAIN_LAPS)
        private float _baseline = 0f;       // frozen whenever we're not in Warmup/Healthy
        private float _topSpeedBaseline = 0f;

        private int _graphOnStreak = 0;
        private int _graphOffStreak = 0;

        // Incident count as of the last genuinely-healthy lap. Frozen once pace starts dropping, so
        // any incident accrued during the decline reveals the loss was caused by contact (= damage).
        private int _incidentCountWhenHealthy = 0;
        private bool _damageLatched = false;

        private int _stintLap = 0;
        private bool _prevOnPitRoad = false;
        #endregion

        public void UpdateIncidentCount(int newCount, int incidentLimit)
        {
            if (newCount != _incidentCount)
            {
                _incidentCount = newCount;
                IncidentDisplay = $"{newCount}x";
                IncidentColor = GetIncidentColor(newCount);
                string severity = newCount == 0 ? "clear" : newCount <= 4 ? "yellow" : newCount <= 8 ? "orange" : "red";
                Log.Debug($"[Warnings] Incident count: {newCount}x ({severity})");
            }
        }

        /// <summary>
        /// Called once per completed lap. Drives the pace-loss / damage / pit-recommendation logic.
        /// </summary>
        public void CheckPace(float lastLapTime, float lastLapTopSpeed, int lapsRemaining,
            double sessionTimeRemain, bool isOnPitRoad, bool isRacingGreen)
        {
            // A pit stop heals damage and fits fresh tires — clear the transient warnings and start
            // a new stint. The baseline is kept (same car), so fresh rubber simply pulls it down.
            if (isOnPitRoad && !_prevOnPitRoad)
                OnPitEntry();
            _prevOnPitRoad = isOnPitRoad;

            if (lastLapTime <= 0)
                return;

            _stintLap++;

            // Only clean green racing laps drive the pace math. Non-qualifying laps hold all current
            // warning states untouched (incidents are still tracked via UpdateIncidentCount).
            bool isOutlier = _baseline > 0 && lastLapTime > _baseline * (1f + OUTLIER_PCT);
            bool qualifying = !isOnPitRoad && isRacingGreen && _stintLap > STINT_WARMUP_SKIP && !isOutlier;
            if (!qualifying)
                return;

            _recentLapTimes.Add(lastLapTime);
            while (_recentLapTimes.Count > SUSTAIN_LAPS) _recentLapTimes.RemoveAt(0);
            float recentPace = _recentLapTimes.Average();

            switch (_state)
            {
                case HealthState.Warmup:
                    AddHealthyLap(lastLapTime, lastLapTopSpeed);
                    if (_cleanLapTimes.Count >= MIN_BASELINE_LAPS)
                    {
                        RecomputeBaseline();
                        _state = HealthState.Healthy;
                        Log.Debug($"[Warnings] Baseline established: {_baseline:F2}s");
                    }
                    break;

                case HealthState.Healthy:
                {
                    float deficit = recentPace - _baseline;
                    bool over = (deficit / _baseline) >= GRAPH_ON_PCT && deficit >= ABS_FLOOR_SEC;
                    if (over)
                    {
                        // Baseline + incident marker freeze here — do NOT absorb the dropping laps.
                        _graphOnStreak++;
                        if (_graphOnStreak >= SUSTAIN_LAPS)
                            EnterPaceLoss(recentPace, lastLapTopSpeed, lapsRemaining, sessionTimeRemain);
                    }
                    else
                    {
                        _graphOnStreak = 0;
                        AddHealthyLap(lastLapTime, lastLapTopSpeed);
                        RecomputeBaseline();
                    }
                    break;
                }

                case HealthState.PaceLoss:
                    EvaluatePaceLoss(recentPace, lastLapTopSpeed, lapsRemaining, sessionTimeRemain);
                    break;
            }
        }

        private void AddHealthyLap(float lapTime, float topSpeed)
        {
            _cleanLapTimes.Add(lapTime);
            if (_cleanLapTimes.Count > CLEAN_LAP_POOL_CAP) _cleanLapTimes.RemoveAt(0);
            if (topSpeed > _topSpeedBaseline) _topSpeedBaseline = topSpeed;
            _incidentCountWhenHealthy = _incidentCount;
        }

        private void RecomputeBaseline()
        {
            if (_cleanLapTimes.Count == 0) return;
            float fastest = _cleanLapTimes.Min();
            float threshold = fastest * (1f + BASELINE_CLUSTER_PCT);
            _baseline = _cleanLapTimes.Where(t => t <= threshold).Average();
        }

        private void EnterPaceLoss(float recentPace, float topSpeed, int lapsRemaining, double timeRemain)
        {
            _state = HealthState.PaceLoss;
            IsPaceWarningVisible = true;
            _graphOffStreak = 0;
            Log.Debug($"[Warnings] Pace loss: {recentPace:F2}s vs baseline {_baseline:F2}s (+{(recentPace / _baseline - 1f) * 100f:F1}%)");
            EvaluatePaceLoss(recentPace, topSpeed, lapsRemaining, timeRemain);
        }

        private void EvaluatePaceLoss(float recentPace, float topSpeed, int lapsRemaining, double timeRemain)
        {
            float deficit = recentPace - _baseline;
            float deficitPct = deficit / _baseline;

            // Damage: any incident since we were last healthy means the pace loss came from contact.
            if (!_damageLatched && _incidentCount > _incidentCountWhenHealthy)
            {
                _damageLatched = true;
                IsPersistentDotVisible = true;
                bool topSpeedDown = _topSpeedBaseline > 0 && topSpeed < _topSpeedBaseline * (1f - TOPSPEED_DROP_PCT);
                Log.Debug($"[Warnings] Damage confirmed (incidents +{_incidentCount - _incidentCountWhenHealthy}, top speed {(topSpeedDown ? "down" : "normal")})");
            }

            // PIT economics: would the time clawed back over the rest of the race beat a stop?
            int lapsRem = EffectiveLapsRemaining(lapsRemaining, timeRemain);
            if (lapsRem > 0 && deficit > 0)
            {
                float pitCost = PIT_COST_BASE_SEC + (_damageLatched ? PIT_COST_REPAIR_ADDER_SEC : 0f);
                float projectedLoss = deficit * lapsRem;
                if (projectedLoss > pitCost * (1f + PIT_MARGIN)) SetPitNow(true);
                else if (projectedLoss < pitCost) SetPitNow(false);
            }
            else
            {
                SetPitNow(false);
            }

            // Recovery: pace back to baseline clears the graph and PIT, and resumes baseline updates.
            // The dot stays — damage doesn't heal without a pit stop.
            if (deficitPct <= GRAPH_OFF_PCT)
            {
                _graphOffStreak++;
                if (_graphOffStreak >= SUSTAIN_LAPS)
                    ExitPaceLoss();
            }
            else
            {
                _graphOffStreak = 0;
            }
        }

        private void ExitPaceLoss()
        {
            _state = HealthState.Healthy;
            IsPaceWarningVisible = false;
            SetPitNow(false);
            _graphOnStreak = 0;
            _graphOffStreak = 0;
            _incidentCountWhenHealthy = _incidentCount;
            Log.Debug("[Warnings] Pace recovered to baseline");
        }

        private void OnPitEntry()
        {
            _damageLatched = false;
            IsPersistentDotVisible = false;
            IsPaceWarningVisible = false;
            SetPitNow(false);
            _state = (_baseline > 0) ? HealthState.Healthy : HealthState.Warmup;
            _graphOnStreak = 0;
            _graphOffStreak = 0;
            _recentLapTimes.Clear();
            _stintLap = 0;
            _incidentCountWhenHealthy = _incidentCount;
            Log.Debug("[Warnings] Pit stop — pace warnings reset for new stint");
        }

        private int EffectiveLapsRemaining(int lapsRemaining, double timeRemain)
        {
            if (lapsRemaining > 0 && lapsRemaining < LAPS_REMAIN_SENTINEL)
                return lapsRemaining;
            // Timed race (or unreliable lap count): estimate from time left and baseline pace.
            if (timeRemain > 0 && _baseline > 0)
                return (int)(timeRemain / _baseline);
            return 0;
        }

        private void SetPitNow(bool on)
        {
            if (on == IsPitNowVisible) return;
            IsPitNowVisible = on;
            Log.Debug(on ? "[Warnings] PIT recommended" : "[Warnings] PIT cleared");
        }

        private Brush GetIncidentColor(int count)
        {
            if (count == 0) return Brushes.White;
            if (count <= 4) return Brushes.Yellow;
            if (count <= 8) return Brushes.Orange;
            return Brushes.Red;
        }

        public void Reset()
        {
            _incidentCount = 0;
            IncidentDisplay = "0x";
            IncidentColor = Brushes.White;

            _cleanLapTimes.Clear();
            _recentLapTimes.Clear();
            _baseline = 0f;
            _topSpeedBaseline = 0f;
            _state = HealthState.Warmup;
            _graphOnStreak = 0;
            _graphOffStreak = 0;
            _incidentCountWhenHealthy = 0;
            _damageLatched = false;
            _stintLap = 0;
            _prevOnPitRoad = false;

            IsPaceWarningVisible = false;
            IsPersistentDotVisible = false;
            IsPitNowVisible = false;
        }
    }
}
