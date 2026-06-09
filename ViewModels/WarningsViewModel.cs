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

        // Damage = contact. iRacing scores contact as 4x; off-tracks (1x) and clean spins (2x)
        // aren't damage, so only an incident jump this large flags the damage dot.
        private const int DAMAGE_INCIDENT_JUMP = 4;
        // Pace loss must persist this many laps inside the loss state before PIT can arm — stops a
        // still-settling deficit from screaming PIT early in a race when laps-remaining is huge.
        private const int PIT_CONFIRM_LAPS = 2;
        // After a contact, the confirming lap must be "just as slow or slower" — at least this
        // fraction of the lap that first raised the graph — before PIT fires.
        private const float PACELOSS_NOT_IMPROVING_FRAC = 0.9f;

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

        // Incident count at the previous completed lap, to detect a lap that was contaminated by a
        // spin/off/contact (those laps are not clean pace samples and are excluded).
        private int _incidentCountPrevLap = 0;
        // Consecutive qualifying laps spent in the pace-loss state (gates the PIT recommendation).
        private int _paceLossLaps = 0;

        // A contact (4x) "arms" a fast-track: the spotter analogue — react to the hit, not just lap
        // times — so the first complete lap after contact can raise the graph on its own.
        private bool _contactArmed = false;
        // Per-lap deficit on the lap that first raised the graph, for the "not improving" PIT gate.
        private float _paceLossOnsetDeficit = 0f;

        private int _stintLap = 0;
        private bool _prevOnPitRoad = false;
        #endregion

        public void UpdateIncidentCount(int newCount, int incidentLimit)
        {
            if (newCount != _incidentCount)
            {
                _incidentCount = newCount;
                IncidentDisplay = $"{newCount}x";
                var severity = GetIncidentSeverity(newCount, incidentLimit);
                IncidentColor = SeverityToBrush(severity);
                Log.Debug($"[Warnings] Incident count: {newCount}x / {(incidentLimit > 0 ? incidentLimit.ToString() : "unlimited")} ({severity})");
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

            // A lap on which the incident count rose was contaminated by a spin/off/contact — not a
            // clean pace sample. Detect it before the qualifying gate (and keep the per-lap marker
            // current for every completed lap, qualifying or not).
            int lapIncidentJump = _incidentCount - _incidentCountPrevLap;
            bool incidentThisLap = lapIncidentJump > 0;
            _incidentCountPrevLap = _incidentCount;

            // A contact this lap (4x) arms the fast-track. The contact lap itself is excluded below;
            // the next clean lap alone can then raise the graph.
            if (lapIncidentJump >= DAMAGE_INCIDENT_JUMP)
                _contactArmed = true;

            // Only clean green racing laps drive the pace math. Non-qualifying laps hold all current
            // warning states untouched (incidents are still tracked via UpdateIncidentCount).
            bool isOutlier = _baseline > 0 && lastLapTime > _baseline * (1f + OUTLIER_PCT);
            bool qualifying = !isOnPitRoad && isRacingGreen && _stintLap > STINT_WARMUP_SKIP
                              && !isOutlier && !incidentThisLap;
            if (!qualifying)
                return;

            _recentLapTimes.Add(lastLapTime);
            while (_recentLapTimes.Count > SUSTAIN_LAPS) _recentLapTimes.RemoveAt(0);
            float recentPace = _recentLapTimes.Average();

            // The pace-loss streak keys on each lap *individually* clearing the threshold, not on a
            // smoothed average — so a single slow lap (e.g. a spin you recover from) can never build
            // a streak, while a genuine sustained loss produces back-to-back slow laps.
            float lapDeficit = lastLapTime - _baseline;
            bool lapOver = _baseline > 0 && (lapDeficit / _baseline) >= GRAPH_ON_PCT && lapDeficit >= ABS_FLOOR_SEC;

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
                    if (lapOver)
                    {
                        // Baseline + incident marker freeze here — do NOT absorb the dropping laps.
                        // After a contact, a single slow lap is enough; otherwise it takes two.
                        _graphOnStreak++;
                        int graphLapsNeeded = _contactArmed ? 1 : SUSTAIN_LAPS;
                        if (_graphOnStreak >= graphLapsNeeded)
                            EnterPaceLoss(lastLapTime, recentPace, lastLapTopSpeed, lapsRemaining, sessionTimeRemain);
                    }
                    else
                    {
                        // The first clean lap after a contact means the car is fine — stand down.
                        _graphOnStreak = 0;
                        _contactArmed = false;
                        AddHealthyLap(lastLapTime, lastLapTopSpeed);
                        RecomputeBaseline();
                    }
                    break;

                case HealthState.PaceLoss:
                    _paceLossLaps++;
                    EvaluatePaceLoss(lastLapTime, recentPace, lastLapTopSpeed, lapsRemaining, sessionTimeRemain);
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

        private void EnterPaceLoss(float lastLapTime, float recentPace, float topSpeed, int lapsRemaining, double timeRemain)
        {
            _state = HealthState.PaceLoss;
            IsPaceWarningVisible = true;
            _graphOffStreak = 0;
            _paceLossLaps = 1;
            _paceLossOnsetDeficit = lastLapTime - _baseline;
            _contactArmed = false; // fast-track has done its job (the graph is up)
            Log.Debug($"[Warnings] Pace loss: {recentPace:F2}s vs baseline {_baseline:F2}s (+{(recentPace / _baseline - 1f) * 100f:F1}%)");
            EvaluatePaceLoss(lastLapTime, recentPace, topSpeed, lapsRemaining, timeRemain);
        }

        private void EvaluatePaceLoss(float lastLapTime, float recentPace, float topSpeed, int lapsRemaining, double timeRemain)
        {
            float deficit = recentPace - _baseline;

            // Damage = contact. Only an incident jump of DAMAGE_INCIDENT_JUMP (4x) since we were last
            // healthy counts; a 1x off-track or a clean 2x spin is not damage. Latched until a pit.
            if (!_damageLatched && (_incidentCount - _incidentCountWhenHealthy) >= DAMAGE_INCIDENT_JUMP)
            {
                _damageLatched = true;
                IsPersistentDotVisible = true;
                bool topSpeedDown = _topSpeedBaseline > 0 && topSpeed < _topSpeedBaseline * (1f - TOPSPEED_DROP_PCT);
                Log.Debug($"[Warnings] Damage confirmed (incidents +{_incidentCount - _incidentCountWhenHealthy}, top speed {(topSpeedDown ? "down" : "normal")})");
            }

            // PIT economics: would the time clawed back over the rest of the race beat a stop? Only
            // once the loss has held for PIT_CONFIRM_LAPS (so a still-settling deficit can't trip it)
            // and the confirming lap is "just as slow or slower" than the onset (not recovering).
            int lapsRem = EffectiveLapsRemaining(lapsRemaining, timeRemain);
            float thisLapDeficit = lastLapTime - _baseline;
            bool notImproving = thisLapDeficit >= _paceLossOnsetDeficit * PACELOSS_NOT_IMPROVING_FRAC;
            if (_paceLossLaps >= PIT_CONFIRM_LAPS && notImproving && lapsRem > 0 && deficit > 0)
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

            // Recovery keys on the latest lap individually returning to baseline (mirrors the per-lap
            // trigger). The graph and PIT clear; the dot stays — damage doesn't heal without a pit.
            float lapDeficitPct = (lastLapTime - _baseline) / _baseline;
            if (lapDeficitPct <= GRAPH_OFF_PCT)
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
            _paceLossLaps = 0;
            _paceLossOnsetDeficit = 0f;
            _contactArmed = false;
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
            _paceLossLaps = 0;
            _paceLossOnsetDeficit = 0f;
            _contactArmed = false;
            _recentLapTimes.Clear();
            _stintLap = 0;
            _incidentCountWhenHealthy = _incidentCount;
            _incidentCountPrevLap = _incidentCount;
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

        private enum IncidentSeverity { Clear, Caution, Danger }

        // Colour by how close the count is to the session's incident limit, not by an absolute
        // count — so the meaning ("getting dangerous") holds whether the cap is 4x, 17x or 25x.
        // Yellow at 30% of the limit, red at 60% (roughly one worst-case incident from a DQ at a
        // 17x limit). Unlimited sessions (limit 0) have no DQ stakes, so fall back to a gentle
        // absolute scale that's purely informational.
        private const float INCIDENT_CAUTION_FRACTION = 0.30f;
        private const float INCIDENT_DANGER_FRACTION = 0.60f;

        private static IncidentSeverity GetIncidentSeverity(int count, int limit)
        {
            if (count <= 0) return IncidentSeverity.Clear;

            if (limit > 0)
            {
                float fraction = (float)count / limit;
                if (fraction >= INCIDENT_DANGER_FRACTION) return IncidentSeverity.Danger;
                if (fraction >= INCIDENT_CAUTION_FRACTION) return IncidentSeverity.Caution;
                return IncidentSeverity.Clear;
            }

            // Unlimited: no cap to measure against.
            if (count > 12) return IncidentSeverity.Danger;
            if (count > 4) return IncidentSeverity.Caution;
            return IncidentSeverity.Clear;
        }

        private static Brush SeverityToBrush(IncidentSeverity severity) => severity switch
        {
            IncidentSeverity.Danger => Brushes.Red,
            IncidentSeverity.Caution => Brushes.Yellow,
            _ => Brushes.White
        };

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
            _incidentCountPrevLap = 0;
            _paceLossLaps = 0;
            _paceLossOnsetDeficit = 0f;
            _contactArmed = false;
            _damageLatched = false;
            _stintLap = 0;
            _prevOnPitRoad = false;

            IsPaceWarningVisible = false;
            IsPersistentDotVisible = false;
            IsPitNowVisible = false;
        }
    }
}
