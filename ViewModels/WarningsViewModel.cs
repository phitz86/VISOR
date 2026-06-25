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
        // All pace thresholds are expressed as a fraction of the healthy baseline so they travel
        // across tracks (1s means nothing at the Nordschleife, everything on a short oval). These
        // are first-pass values and are expected to need on-track tuning.

        private const int MIN_BASELINE_LAPS = 3;        // clean laps before any warning can fire
        private const float BASELINE_CLUSTER_PCT = 0.015f; // laps within 1.5% of fastest define "good pace"
        private const float GRAPH_ON_PCT = 0.020f;      // deficit to raise the pace-loss graph
        private const float GRAPH_OFF_PCT = 0.012f;     // deficit to clear it (hysteresis)
        private const float OUTLIER_PCT = 0.10f;        // laps >10% over baseline are junk (spin/off)
        private const float TOPSPEED_DROP_PCT = 0.03f;  // top-speed loss that corroborates damage
        private const float ABS_FLOOR_SEC = 0.5f;       // absolute lap deficit floor, keeps short tracks calm
        private const int STINT_WARMUP_SKIP = 1;        // ignore the cold first lap of each stint
        private const int CLEAN_LAP_POOL_CAP = 50;      // bound the baseline sample list

        // --- Sub-lap segment checkpoints (the responsive pace clock) -------------------------------
        // The lap is split into N equal LapDistPct segments so a deficit is caught partway round the
        // lap instead of only at the line. N scales with lap *time* (not distance): we target a roughly
        // constant seconds-per-segment so a 22s oval gets ~2 checkpoints and the 7-min Nords gets ~18.
        private const float TARGET_SEGMENT_SECONDS = 25f; // desired wall-time between checkpoints
        private const int MIN_SEGMENTS = 2;             // never fewer than this (short ovals)
        private const int MAX_SEGMENTS = 24;            // cap the bookkeeping on very long tracks
        private const int MIN_SEGMENT_SAMPLES = 2;      // clean samples before a segment baseline is usable
        private const int SEGMENTS_TO_RAISE = 2;        // consecutive over-threshold checkpoints to raise the graph
        private const int SEGMENTS_TO_CLEAR = 2;        // consecutive recovered checkpoints to clear it (hysteresis)

        // Damage = contact. iRacing scores contact as 4x; off-tracks (1x) and clean spins (2x) aren't
        // damage. But a 4x is only *contact*, not proof your car is hurt — someone tagging you in a
        // braking zone scores you 4x too. So a contact does not light anything; it opens a short
        // corroboration window during which a follow-on pace/top-speed symptom can latch the dot.
        private const int DAMAGE_INCIDENT_JUMP = 4;
        private const int CONTACT_WINDOW_CHECKPOINTS = 2; // checkpoints a contact stays "armed" awaiting corroboration
        private const int CONTACT_DEFICIT_SEGMENTS = 2;   // over-threshold checkpoints that corroborate damage by pace alone

        // Pace loss must persist this many completed laps before PIT can arm — stops a still-settling
        // deficit from screaming PIT early in a race when laps-remaining is huge.
        private const int PIT_CONFIRM_LAPS = 2;
        // After onset, the confirming lap must be "just as slow or slower" — at least this fraction of
        // the onset lap's deficit — before PIT fires.
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

        #region Pace state
        // Two clocks share this state. The per-frame clock (segments + contact) drives the responsive
        // graph and the damage dot; the per-lap clock (gated to one tick per completed lap) owns the
        // baseline and the PIT economics, which genuinely need full-lap numbers.
        private enum HealthState { Warmup, Monitoring }
        private HealthState _state = HealthState.Warmup;

        private readonly List<float> _cleanLapTimes = new();  // healthy-lap pool for the lap baseline
        private float _baseline = 0f;
        private float _topSpeedBaseline = 0f;

        // Segment (sub-lap) tracking.
        private int _segmentCount = 0;
        private List<float>[] _segmentSamples = Array.Empty<List<float>>(); // clean per-segment times -> baseline = min
        private readonly List<(int idx, float time)> _pendingSegments = new(); // this lap's segment times, committed at the line
        private int _segIndex = -1;          // segment currently being timed this lap (-1 = not started)
        private float _segStartLapTime = 0f; // LapCurrentLapTime when the current segment began

        // Checkpoint streaks drive the graph (a checkpoint is a ready segment, or a whole lap before
        // any segment baselines exist).
        private int _overStreak = 0;
        private int _underStreak = 0;

        // Top speed achieved on the lap in progress (max of per-frame speed), for damage corroboration.
        private float _lapTopSpeed = 0f;

        // Damage.
        private bool _damageLatched = false;
        private int _incidentCountWhenHealthy = 0; // incident count as of the last genuinely-healthy lap

        // Contact corroboration window.
        private bool _contactArmed = false;
        private int _contactWindowRemaining = 0;

        // PIT (per-lap).
        private int _paceLossLapCount = 0;       // completed laps spent with the graph up
        private float _paceLossOnsetDeficit = 0f; // lap deficit when PIT confirmation began

        // Lap/stint bookkeeping.
        private int _lastLap = -1;               // player lap number of the last processed lap-close
        private int _stintLap = 0;
        private int _incidentCountAtLapStart = 0; // to flag a lap contaminated by a spin/off/contact
        private int _incidentCountPrevFrame = 0;  // per-frame contact-jump detection
        private bool _firstUpdate = true;         // skip contact detection on the very first frame
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
        /// Called once per telemetry frame. Internally splits into a per-frame clock (contact arming +
        /// sub-lap segment checkpoints) and a per-lap clock (baseline + PIT economics) that only ticks
        /// when a new completed lap is detected.
        /// </summary>
        public void Update(int currentLap, float lapDistPct, float currentLapTime, float lastLapTime,
            float currentSpeed, int lapsRemaining, double sessionTimeRemain,
            bool isOnPitRoad, bool isRacingGreen)
        {
            // A pit stop heals damage and fits fresh tires — clear the transient warnings and start a
            // new stint. The baseline (and learned segments) are kept; fresh rubber just pulls it down.
            if (isOnPitRoad && !_prevOnPitRoad)
                OnPitEntry();
            _prevOnPitRoad = isOnPitRoad;

            // Track this lap's top speed regardless of state.
            if (currentSpeed > _lapTopSpeed) _lapTopSpeed = currentSpeed;

            // --- Per-frame clock: contact detection (instant) ---------------------------------------
            if (_firstUpdate)
            {
                // Seed the per-frame baselines so a rejoin with pre-existing incidents can't false-arm.
                _incidentCountPrevFrame = _incidentCount;
                _incidentCountAtLapStart = _incidentCount;
                _lastLap = currentLap;
                _firstUpdate = false;
            }

            int incJump = _incidentCount - _incidentCountPrevFrame;
            _incidentCountPrevFrame = _incidentCount;
            if (incJump >= DAMAGE_INCIDENT_JUMP && isRacingGreen && !isOnPitRoad)
            {
                // Contact: arm a corroboration window. Nothing lights yet.
                _contactArmed = true;
                _contactWindowRemaining = CONTACT_WINDOW_CHECKPOINTS;
                Log.Debug($"[Warnings] Contact (+{incJump}x) — watching {CONTACT_WINDOW_CHECKPOINTS} checkpoints for pace/top-speed symptoms");
            }

            // --- New completed lap? -----------------------------------------------------------------
            if (currentLap > _lastLap)
            {
                OnLapCompleted(lastLapTime, currentLap, lapsRemaining, sessionTimeRemain, isOnPitRoad, isRacingGreen);
                return;
            }

            // --- Per-frame clock: sub-lap segment checkpoints ---------------------------------------
            if (_state == HealthState.Monitoring && _segmentCount > 0 && isRacingGreen
                && !isOnPitRoad && _stintLap > STINT_WARMUP_SKIP)
            {
                UpdateSegments(lapDistPct, currentLapTime);
            }
        }

        private void UpdateSegments(float lapDistPct, float currentLapTime)
        {
            if (lapDistPct < 0f) lapDistPct = 0f;
            else if (lapDistPct >= 1f) lapDistPct = 0.999999f;

            int seg = (int)(lapDistPct * _segmentCount);
            if (seg < 0) seg = 0;
            else if (seg >= _segmentCount) seg = _segmentCount - 1;

            if (_segIndex < 0)
            {
                // First sample of the lap: start timing the current segment from here.
                _segIndex = seg;
                _segStartLapTime = currentLapTime;
                return;
            }
            if (seg == _segIndex) return;  // still inside the same segment
            if (seg < _segIndex) return;   // went backwards / wrapped — the line rollover is handled at lap close

            // Crossed into a later segment: close the one we were timing.
            float segTime = currentLapTime - _segStartLapTime;
            if (segTime > 0f) CloseSegment(_segIndex, segTime);
            _segIndex = seg;
            _segStartLapTime = currentLapTime;
        }

        private void CloseSegment(int idx, float segTime)
        {
            if (idx < 0 || idx >= _segmentCount || idx >= _segmentSamples.Length) return;

            // Bank for possible commit to the baseline at the line (only if the whole lap stays clean).
            _pendingSegments.Add((idx, segTime));

            // Score against this segment's baseline if it has enough clean samples to be trusted.
            var samples = _segmentSamples[idx];
            if (samples.Count >= MIN_SEGMENT_SAMPLES)
            {
                float segBaseline = samples.Min();
                if (segBaseline <= 0f) return;
                float deficit = segTime - segBaseline;
                float segFloor = ABS_FLOOR_SEC / _segmentCount; // proportional floor for a fraction of the lap
                OnCheckpoint(deficit / segBaseline, deficit, segFloor);
            }
        }

        /// <summary>
        /// A pace observation at a checkpoint (a ready segment, or a whole lap during segment warmup).
        /// Drives the graph streaks, the contact corroboration, and the contact window countdown.
        /// </summary>
        private void OnCheckpoint(float deficitPct, float deficit, float floor)
        {
            bool over = deficitPct >= GRAPH_ON_PCT && deficit >= floor;
            bool under = deficitPct <= GRAPH_OFF_PCT;

            if (over) { _overStreak++; _underStreak = 0; }
            else if (under) { _underStreak++; _overStreak = 0; }
            else { _overStreak = 0; } // in the hysteresis band: stop arming, but don't clear either

            if (!IsPaceWarningVisible)
            {
                // A live contact fast-tracks the graph: one over-threshold checkpoint is enough.
                int needed = _contactArmed ? 1 : SEGMENTS_TO_RAISE;
                if (_overStreak >= needed) RaiseGraph();
            }
            else if (_underStreak >= SEGMENTS_TO_CLEAR)
            {
                ClearGraph();
            }

            // Contact corroboration: a sustained deficit after a hit is damage even with normal top
            // speed (the Suzuka case — corner time only). Top-speed corroboration is checked at the line.
            if (_contactArmed)
            {
                if (!_damageLatched && _overStreak >= CONTACT_DEFICIT_SEGMENTS)
                    LatchDamage("sustained pace deficit after contact");

                _contactWindowRemaining--;
                if (_contactWindowRemaining <= 0 && !_damageLatched)
                {
                    _contactArmed = false;
                    Log.Debug("[Warnings] Contact window expired clean — no damage, standing down");
                }
            }
        }

        private void RaiseGraph()
        {
            IsPaceWarningVisible = true;
            _underStreak = 0;
            _paceLossLapCount = 0; // counted at lap closes from here
            Log.Debug($"[Warnings] Pace loss raised (baseline {_baseline:F2}s)");
        }

        private void ClearGraph()
        {
            IsPaceWarningVisible = false;
            SetPitNow(false);
            _overStreak = 0;
            _underStreak = 0;
            _paceLossLapCount = 0;
            _paceLossOnsetDeficit = 0f;
            // The dot stays: physical damage doesn't heal without a pit.
            Log.Debug("[Warnings] Pace recovered to baseline");
        }

        private void LatchDamage(string reason)
        {
            if (_damageLatched) return;
            _damageLatched = true;
            IsPersistentDotVisible = true;
            _contactArmed = false; // corroborated — close the window
            Log.Debug($"[Warnings] Damage confirmed ({reason})");
        }

        /// <summary>Per-lap clock: runs exactly once per newly completed lap.</summary>
        private void OnLapCompleted(float lastLapTime, int currentLap, int lapsRemaining,
            double timeRemain, bool isOnPitRoad, bool isRacingGreen)
        {
            // Snapshot then reset the per-lap top speed.
            float lapTopSpeed = _lapTopSpeed;
            _lapTopSpeed = 0f;

            // Close the final segment of the lap that just ended (LapCurrentLapTime has reset, so use
            // the completed lap total).
            if (_state == HealthState.Monitoring && _segmentCount > 0 && _segIndex >= 0
                && lastLapTime > 0f && lastLapTime > _segStartLapTime)
            {
                CloseSegment(_segIndex, lastLapTime - _segStartLapTime);
            }

            _stintLap++;
            bool incidentThisLap = _incidentCount > _incidentCountAtLapStart;
            bool isOutlier = _baseline > 0 && lastLapTime > _baseline * (1f + OUTLIER_PCT);
            bool qualifying = lastLapTime > 0f && !isOnPitRoad && isRacingGreen
                              && _stintLap > STINT_WARMUP_SKIP && !isOutlier && !incidentThisLap;

            // Commit this lap's segment times to the baseline pools only if the lap was clean AND we
            // weren't already losing pace (don't poison segment baselines with compromised laps).
            if (qualifying && !IsPaceWarningVisible)
            {
                foreach (var (idx, time) in _pendingSegments)
                {
                    if (idx < 0 || idx >= _segmentSamples.Length) continue;
                    var pool = _segmentSamples[idx];
                    pool.Add(time);
                    if (pool.Count > CLEAN_LAP_POOL_CAP) pool.RemoveAt(0);
                }
            }

            // Reset per-lap segment tracking for the new lap.
            _pendingSegments.Clear();
            _segIndex = -1;
            _segStartLapTime = 0f;
            _lastLap = currentLap;
            _incidentCountAtLapStart = _incidentCount;

            // --- Warmup: build the lap baseline from clean laps ---
            if (_state == HealthState.Warmup)
            {
                if (qualifying)
                {
                    AddBaselineLap(lastLapTime, lapTopSpeed);
                    if (_cleanLapTimes.Count >= MIN_BASELINE_LAPS)
                    {
                        RecomputeBaseline();
                        RecomputeSegmentCount();
                        _state = HealthState.Monitoring;
                        Log.Debug($"[Warnings] Baseline established: {_baseline:F2}s");
                    }
                }
                return;
            }

            // --- Monitoring ---

            // Top-speed corroboration of damage (the fast aero/mechanical signature).
            if (_contactArmed && !_damageLatched && _topSpeedBaseline > 0f && lapTopSpeed > 0f
                && lapTopSpeed < _topSpeedBaseline * (1f - TOPSPEED_DROP_PCT))
            {
                LatchDamage("top-speed drop after contact");
            }

            // Backstop while segment baselines are still warming: treat the whole lap as one checkpoint.
            if (SegmentsReadyCount() == 0 && _baseline > 0f && qualifying)
            {
                float deficit = lastLapTime - _baseline;
                OnCheckpoint(deficit / _baseline, deficit, ABS_FLOOR_SEC);
            }

            if (IsPaceWarningVisible)
            {
                _paceLossLapCount++;
                EvaluatePit(lastLapTime, lapsRemaining, timeRemain);
            }
            else
            {
                _paceLossLapCount = 0;
                SetPitNow(false);
                // Healthy clean lap: let the baseline drift (fresh tires, fuel burn-off).
                if (qualifying)
                {
                    AddBaselineLap(lastLapTime, lapTopSpeed);
                    RecomputeBaseline();
                    RecomputeSegmentCount();
                    _incidentCountWhenHealthy = _incidentCount;
                }
            }
        }

        private void EvaluatePit(float lastLapTime, int lapsRemaining, double timeRemain)
        {
            float lapDeficit = lastLapTime - _baseline;
            if (_paceLossLapCount == 1) _paceLossOnsetDeficit = lapDeficit; // onset reference

            int lapsRem = EffectiveLapsRemaining(lapsRemaining, timeRemain);
            bool notImproving = lapDeficit >= _paceLossOnsetDeficit * PACELOSS_NOT_IMPROVING_FRAC;

            if (_paceLossLapCount >= PIT_CONFIRM_LAPS && notImproving && lapsRem > 0 && lapDeficit > 0f)
            {
                float pitCost = PIT_COST_BASE_SEC + (_damageLatched ? PIT_COST_REPAIR_ADDER_SEC : 0f);
                float projectedLoss = lapDeficit * lapsRem;
                if (projectedLoss > pitCost * (1f + PIT_MARGIN)) SetPitNow(true);
                else if (projectedLoss < pitCost) SetPitNow(false);
            }
            else
            {
                SetPitNow(false);
            }
        }

        private void AddBaselineLap(float lapTime, float topSpeed)
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

        // Segment count scales with lap *time* so the seconds-between-checkpoints stays roughly constant
        // across tracks. Changing it invalidates the learned per-segment baselines (the boundaries moved).
        private void RecomputeSegmentCount()
        {
            if (_baseline <= 0f) return;
            int n = (int)Math.Round(_baseline / TARGET_SEGMENT_SECONDS);
            n = Math.Clamp(n, MIN_SEGMENTS, MAX_SEGMENTS);
            if (n == _segmentCount) return;

            _segmentCount = n;
            _segmentSamples = new List<float>[n];
            for (int i = 0; i < n; i++) _segmentSamples[i] = new List<float>();
            _pendingSegments.Clear();
            _segIndex = -1;
            _overStreak = 0;
            _underStreak = 0;
            Log.Debug($"[Warnings] Segments = {n} (~{_baseline / n:F1}s each at {_baseline:F1}s/lap)");
        }

        private int SegmentsReadyCount()
        {
            int ready = 0;
            for (int i = 0; i < _segmentSamples.Length; i++)
                if (_segmentSamples[i].Count >= MIN_SEGMENT_SAMPLES) ready++;
            return ready;
        }

        private void OnPitEntry()
        {
            _damageLatched = false;
            IsPersistentDotVisible = false;
            IsPaceWarningVisible = false;
            SetPitNow(false);
            _state = (_baseline > 0f) ? HealthState.Monitoring : HealthState.Warmup;
            _overStreak = 0;
            _underStreak = 0;
            _paceLossLapCount = 0;
            _paceLossOnsetDeficit = 0f;
            _contactArmed = false;
            _contactWindowRemaining = 0;
            _lapTopSpeed = 0f;
            _segIndex = -1;
            _segStartLapTime = 0f;
            _pendingSegments.Clear();
            _stintLap = 0;
            _incidentCountWhenHealthy = _incidentCount;
            _incidentCountAtLapStart = _incidentCount;
            _incidentCountPrevFrame = _incidentCount;
            Log.Debug("[Warnings] Pit stop — pace warnings reset for new stint");
        }

        private int EffectiveLapsRemaining(int lapsRemaining, double timeRemain)
        {
            if (lapsRemaining > 0 && lapsRemaining < LAPS_REMAIN_SENTINEL)
                return lapsRemaining;
            // Timed race (or unreliable lap count): estimate from time left and baseline pace.
            if (timeRemain > 0 && _baseline > 0f)
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

        // Colour by how close the count is to the session's incident limit, not by an absolute count —
        // so the meaning ("getting dangerous") holds whether the cap is 4x, 17x or 25x. Yellow at 30%
        // of the limit, red at 60%. Unlimited sessions (limit 0) have no DQ stakes, so fall back to a
        // gentle absolute scale that's purely informational.
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
            _baseline = 0f;
            _topSpeedBaseline = 0f;
            _state = HealthState.Warmup;

            _segmentCount = 0;
            _segmentSamples = Array.Empty<List<float>>();
            _pendingSegments.Clear();
            _segIndex = -1;
            _segStartLapTime = 0f;
            _overStreak = 0;
            _underStreak = 0;
            _lapTopSpeed = 0f;

            _damageLatched = false;
            _incidentCountWhenHealthy = 0;

            _contactArmed = false;
            _contactWindowRemaining = 0;

            _paceLossLapCount = 0;
            _paceLossOnsetDeficit = 0f;

            _lastLap = -1;
            _stintLap = 0;
            _incidentCountAtLapStart = 0;
            _incidentCountPrevFrame = 0;
            _firstUpdate = true;
            _prevOnPitRoad = false;

            IsPaceWarningVisible = false;
            IsPersistentDotVisible = false;
            IsPitNowVisible = false;
        }
    }
}
