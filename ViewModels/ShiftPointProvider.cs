using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VISOR.Diagnostics;
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
    /// Decides the shift point for the current gear and runs the shift-point learner.
    ///
    /// Shift RPM comes from, in order: the learned optimum for this gear (once the learner is
    /// confident), iRacing's live PlayerCarSLShiftRPM, then the session YAML's DriverCarSLShiftRPM.
    ///
    /// <see cref="Update"/> runs on the UI thread at 60 Hz and stays O(1): it hands the frame to
    /// the learner under a lock. Solving the model and file I/O run on background tasks.
    /// </summary>
    public sealed class ShiftPointProvider
    {
        private static readonly TimeSpan FitInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(60);
        private const int StableFits = 3;               // consecutive confident fits required
        private const int StableSpreadRpm = 150;        // ...agreeing within this many RPM
        private const int LogChangeRpm = 100;
        private const int MaxTrackWetnessForLearning = 2;   // 0 unknown, 1 dry, 2 mostly dry
        private const float RedLineMarginMinRpm = 100f;
        private const float RedLineMarginFraction = 0.015f;

        private readonly object _lock = new();
        private readonly ShiftModelStore _store;
        private ShiftPointLearner? _learner;            // null while no car / loading
        private string _carKey = string.Empty;
        private PlayerCarInfo? _car;
        private int _generation;                        // bumps on every car change; stale tasks check it

        // Learned RPM per gear (index = gear), 0 = not confident. Replaced wholesale, read lock-free.
        private volatile int[] _learnedRpm = new int[ShiftPointLearner.MaxGears + 1];
        private readonly int[][] _recentFits = NewRecentFits();

        // Provisional ("stepping up") cue per gear, 0 = none. Used only when it's later than the
        // car's own shift light; see ShiftPointLearner's GearShiftEstimate.Provisional.
        private volatile int[] _provisionalRpm = new int[ShiftPointLearner.MaxGears + 1];
        private readonly int[][] _recentProvisional = NewRecentFits();
        private int _fitRunning;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _lastFit;
        private TimeSpan _lastSave;
        private TimeSpan _lastProgress;
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(60);
        private GearShiftEstimate[] _lastEstimates = Array.Empty<GearShiftEstimate>();
        private long _lastFitSampleCount = -1;

#if DEBUG
        private ShiftPointLogger? _debugLogger;
        private WetResearchLogger? _wetLogger;
#endif

        public ShiftPointProvider()
            : this(new ShiftModelStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VISOR", "ShiftModels")))
        {
        }

        public ShiftPointProvider(ShiftModelStore store)
        {
            _store = store;
        }

        private static int[][] NewRecentFits()
        {
            var a = new int[ShiftPointLearner.MaxGears + 1][];
            for (int i = 0; i < a.Length; i++) a[i] = Array.Empty<int>();
            return a;
        }

        #region Shift point lookup

        /// <summary>
        /// Optimal upshift RPM for <paramref name="gear"/>, or 0 when there is nothing to shift to
        /// (reverse, neutral, top gear) or no shift-light data at all.
        /// </summary>
        public int GetShiftRpm(int gear, SVappsLABSnapshot snapshot, PlayerCarInfo? car)
        {
            if (car == null || gear < 1) return 0;
            if (car.GearNumForward > 0 && gear >= car.GearNumForward) return 0;

            int baseline = snapshot.PlayerCarSLShiftRPM > 0f ? (int)snapshot.PlayerCarSLShiftRPM
                         : car.SLShiftRPM > 0f ? (int)car.SLShiftRPM : 0;

            // Learned value first. Otherwise a provisional step, but only to move the cue later
            // than the car's light (earlier shifts need a confident learned crossover).
            // Otherwise the light.
            int rpm;
            var learned = _learnedRpm;
            var provisional = _provisionalRpm;
            if (gear < learned.Length && learned[gear] > 0) rpm = learned[gear];
            else if (gear < provisional.Length && provisional[gear] > baseline) rpm = provisional[gear];
            else rpm = baseline;

            // Keep the shift point a little under the redline. When a gear is best held to the
            // redline, this lets the flash show (and leaves time to react) before the solid-red
            // limiter state takes over.
            if (rpm > 0 && car.RedLine > 0)
                rpm = Math.Min(rpm, (int)(car.RedLine - Math.Max(RedLineMarginMinRpm, RedLineMarginFraction * car.RedLine)));
            return rpm;
        }

        /// <summary>
        /// Calibration status of the shift point for <paramref name="gear"/>, for the HUD dot:
        /// Settled once learned; Calibrating while on the car's light or stepping up; None when no
        /// shift point applies (neutral, reverse, top gear, no car).
        /// </summary>
        public ShiftCalibration GetCalibration(int gear, PlayerCarInfo? car)
        {
            if (car == null || gear < 1) return ShiftCalibration.None;
            if (car.GearNumForward > 0 && gear >= car.GearNumForward) return ShiftCalibration.None;
            var learned = _learnedRpm;
            return gear < learned.Length && learned[gear] > 0 ? ShiftCalibration.Settled : ShiftCalibration.Calibrating;
        }

        /// <summary>
        /// Where amber starts: the same RPM gap below the shift point as the car's own lights
        /// (shift-light "shift" minus "first"), so it moves with a learned shift point.
        /// </summary>
        public static int GetApproachRpm(int shiftRpm, SVappsLABSnapshot snapshot, PlayerCarInfo car)
        {
            if (shiftRpm <= 0) return 0;
            float gap = 0f;
            if (snapshot.PlayerCarSLShiftRPM > 0f && snapshot.PlayerCarSLFirstRPM > 0f)
                gap = snapshot.PlayerCarSLShiftRPM - snapshot.PlayerCarSLFirstRPM;
            else if (car.SLShiftRPM > 0f && car.SLFirstRPM > 0f)
                gap = car.SLShiftRPM - car.SLFirstRPM;

            // Missing or odd data: fall back to 5% below the shift point.
            if (gap <= 0f || gap > 0.25f * shiftRpm) gap = 0.05f * shiftRpm;
            return (int)(shiftRpm - gap);
        }

        #endregion

        #region Learning

        /// <summary>Feeds one telemetry frame. UI thread, 60 Hz.</summary>
        public void Update(SVappsLABSnapshot s, PlayerCarInfo? car, bool onPitRoad)
        {
            if (car == null) return;
            EnsureCar(car);

            var sample = new ShiftSample(
                SessionTime: s.SessionTime,
                Gear: s.Gear,
                Rpm: s.RPM,
                Speed: s.Speed,
                LongAccel: s.LongAccel,
                LatAccel: s.LatAccel,
                Throttle: s.Throttle,
                Brake: s.Brake,
                Clutch: s.Clutch,
                Eligible: s.IsOnTrack && !s.IsReplayPlaying && !onPitRoad
                          && !s.PitLimiterOn && !s.RevLimiterActive
                          && s.TrackWetness <= MaxTrackWetnessForLearning);

            lock (_lock)
            {
                if (_learner == null) return;
                bool added = _learner.AddSample(sample);
#if DEBUG
                if (added) _debugLogger?.LogSample(sample, _learner.GetRatio(sample.Gear));
                if (WetResearchLogger.ShouldLog(s, onPitRoad)) _wetLogger?.Write(s, _learner.GetRatio(s.Gear));
#endif
            }

            var now = _clock.Elapsed;
            if (now - _lastFit >= FitInterval)
            {
                _lastFit = now;
                ScheduleFit();
            }
            if (now - _lastProgress >= ProgressInterval)
            {
                _lastProgress = now;
                LogProgress();
            }
            if (now - _lastSave >= SaveInterval)
            {
                _lastSave = now;
                ScheduleSave();
            }
        }

        private void EnsureCar(PlayerCarInfo car)
        {
            // Fast path: the record instance only changes when session info is re-parsed.
            if (ReferenceEquals(car, _car)) return;

            string key = car.CarPath + "|" + car.CarVersion;
            if (key == _carKey)
            {
                _car = car;     // refresh redline / shift-light values from the latest parse
                return;
            }

            // Car changed (or first car): save the old model, start fresh, load the new one.
            Flush();
            Log.Info($"[ShiftPoint] Car detected: {Describe(car)} ({car.CarPath}, build {car.CarVersion}), " +
                     $"iRacing lights first/shift/last/blink {car.SLFirstRPM:F0}/{car.SLShiftRPM:F0}/{car.SLLastRPM:F0}/{car.SLBlinkRPM:F0}, " +
                     $"redline {car.RedLine:F0}, {car.GearNumForward} gears");

            int gen;
            lock (_lock)
            {
                _carKey = key;
                _car = car;
                _learner = null;
                gen = ++_generation;
                _lastFitSampleCount = -1;
                _learnedRpm = new int[ShiftPointLearner.MaxGears + 1];
                _provisionalRpm = new int[ShiftPointLearner.MaxGears + 1];
                for (int i = 0; i < _recentProvisional.Length; i++) _recentProvisional[i] = Array.Empty<int>();
                _lastEstimates = Array.Empty<GearShiftEstimate>();
                for (int i = 0; i < _recentFits.Length; i++) _recentFits[i] = Array.Empty<int>();
#if DEBUG
                _debugLogger?.Dispose();
                _debugLogger = new ShiftPointLogger(car.CarPath);
                _wetLogger?.Dispose();
                _wetLogger = new WetResearchLogger(car.CarPath);
#endif
            }

            Task.Run(() =>
            {
                try
                {
                    var learner = new ShiftPointLearner();
                    var state = _store.Load(car.CarPath, car.CarVersion, out string reason);
                    if (state != null)
                    {
                        learner.ImportState(state);
                        Log.Info($"[ShiftPoint] {Describe(car)}: loaded learned model ({state.SampleCount} full-throttle samples)");
                    }
                    else
                    {
                        Log.Info($"[ShiftPoint] {Describe(car)}: starting from iRacing shift lights ({reason}); " +
                                 $"iRacing shift {car.SLShiftRPM:F0}, redline {car.RedLine:F0}");
                    }

                    lock (_lock)
                    {
                        if (gen == _generation) _learner = learner;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("[ShiftPoint] model load failed", ex);
                    lock (_lock)
                    {
                        if (gen == _generation) _learner = new ShiftPointLearner();
                    }
                }
            });
        }

        private void ScheduleFit()
        {
            if (Interlocked.CompareExchange(ref _fitRunning, 1, 0) != 0) return;

            ShiftPointLearner? learner;
            PlayerCarInfo? car;
            int gen;
            bool hasNewData;
            lock (_lock)
            {
                learner = _learner;
                car = _car;
                gen = _generation;
                // Only refit on new data, so "consecutive agreeing fits" means agreement across
                // fresh driving rather than the same data solved repeatedly.
                hasNewData = learner != null && learner.SampleCount != _lastFitSampleCount;
                if (hasNewData) _lastFitSampleCount = learner!.SampleCount;
            }
            if (learner == null || car == null || car.RedLine <= 0 || !hasNewData)
            {
                Interlocked.Exchange(ref _fitRunning, 0);
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    // Holds the lock for the ~100x100 Cholesky (around a millisecond); the UI
                    // thread's AddSample just waits that long at most.
                    lock (_lock)
                    {
                        if (gen != _generation) return;
                        ApplyEstimates(learner.Solve(car.RedLine, car.GearNumForward), car, learner);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("[ShiftPoint] fit failed", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _fitRunning, 0);
                }
            });
        }

        // Promotes a gear's estimate to "learned" only after StableFits consecutive confident fits
        // that agree within StableSpreadRpm, so one noisy fit can't move the shift point.
        // Caller holds _lock.
        private void ApplyEstimates(GearShiftEstimate[] estimates, PlayerCarInfo car, ShiftPointLearner learner)
        {
            var next = (int[])_learnedRpm.Clone();
            var nextProv = (int[])_provisionalRpm.Clone();
#if DEBUG
            bool changed = false;   // a learned value was promoted or moved: dump the curve to the CSV
#endif
            foreach (var e in estimates)
            {
                if (e.Gear < 1 || e.Gear >= next.Length) continue;

                if (e.Confident)
                {
                    _recentProvisional[e.Gear] = Array.Empty<int>();
                    nextProv[e.Gear] = 0;   // learned supersedes any provisional step
                    if (TryStabilize(_recentFits, e.Gear, e.Rpm, out int value))
                    {
                        int old = next[e.Gear];
                        next[e.Gear] = value;
                        if (old == 0 || Math.Abs(old - value) > LogChangeRpm)
                        {
                            Log.Info($"[ShiftPoint] {Describe(car)} gear {e.Gear}: iRacing {car.SLShiftRPM:F0} -> learned {value} RPM ({Explain(e, car)})");
#if DEBUG
                            changed = true;
#endif
                        }
                    }
                }
                else if (e.Provisional)
                {
                    // Keep an already-learned value; a provisional fit just doesn't add to it.
                    _recentFits[e.Gear] = Array.Empty<int>();
                    if (TryStabilize(_recentProvisional, e.Gear, e.Rpm, out int value))
                    {
                        int old = nextProv[e.Gear];
                        nextProv[e.Gear] = value;
                        if (next[e.Gear] == 0 && value > car.SLShiftRPM && (old == 0 || Math.Abs(old - value) > LogChangeRpm))
                            Log.Info($"[ShiftPoint] {Describe(car)} gear {e.Gear}: stepping up {(old > 0 ? old : (int)car.SLShiftRPM)} -> {value} RPM (proven to {e.ProvenRpm})");
                    }
                }
                else
                {
                    // Not confident: an already-learned or provisional value stays; fits restart.
                    _recentFits[e.Gear] = Array.Empty<int>();
                    _recentProvisional[e.Gear] = Array.Empty<int>();
                }

#if DEBUG
                _debugLogger?.LogEstimate(e);
#endif
            }

            _provisionalRpm = nextProv;
            _learnedRpm = next;
            _lastEstimates = estimates;

#if DEBUG
            if (changed)
            {
                var curve = learner.GetCurve(car.RedLine);
                if (curve != null) _debugLogger?.LogCurve(curve);
            }
#endif
        }

        // Appends a fit to the gear's recent list; true (with the averaged value, rounded to 10 RPM)
        // once StableFits consecutive fits agree within StableSpreadRpm.
        private static bool TryStabilize(int[][] recentFits, int gear, int rpm, out int value)
        {
            var recent = recentFits[gear];
            var updated = new int[Math.Min(recent.Length + 1, StableFits)];
            Array.Copy(recent, Math.Max(0, recent.Length - (updated.Length - 1)), updated, 0, updated.Length - 1);
            updated[^1] = rpm;
            recentFits[gear] = updated;

            value = 0;
            if (updated.Length < StableFits) return false;
            int min = int.MaxValue, max = int.MinValue; long sum = 0;
            foreach (var r in updated) { min = Math.Min(min, r); max = Math.Max(max, r); sum += r; }
            if (max - min > StableSpreadRpm) return false;
            value = (int)(Math.Round(sum / (double)updated.Length / 10.0) * 10);
            return true;
        }

        // "holds to redline; at 7500, 5th would pull 4% less" / "crossover; 3rd pulls equal at 7210"
        private static string Explain(GearShiftEstimate e, PlayerCarInfo car)
        {
            string next = Ordinal(e.Gear + 1);
            if (e.Reason == "holds to redline" && !double.IsNaN(e.NextGearThrustAtRedlinePct))
                return $"holds to redline; at {car.RedLine:F0}, {next} would pull {100 - e.NextGearThrustAtRedlinePct:F0}% less";
            if (e.Reason == "crossover")
                return $"crossover; {next} pulls equal at {e.Rpm}";
            return e.Reason;
        }

        private static string Ordinal(int n) => n switch
        {
            1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{n}th"
        };

        private void ScheduleSave()
        {
            string carPath, carVersion;
            ShiftModelState state;
            lock (_lock)
            {
                if (_learner == null || _car == null || !_learner.TakeDirty()) return;
                carPath = _car.CarPath;
                carVersion = _car.CarVersion;
                state = _learner.ExportState();
            }
            Task.Run(() => SaveState(carPath, carVersion, state));
        }

        private void SaveState(string carPath, string carVersion, ShiftModelState state)
        {
            if (!_store.Save(carPath, carVersion, state, out string reason))
                Log.Warning($"[ShiftPoint] could not save model for {carPath}: {reason}");
        }

        /// <summary>
        /// Saves the current car's model now (synchronously) if it has unsaved samples. Called on
        /// car change, disconnect and app exit.
        /// </summary>
        public void Flush()
        {
#if DEBUG
            // Close the current wet-research file (disconnect/exit); a new one opens on the next wet frame.
            lock (_lock)
            {
                _wetLogger?.Dispose();
                _wetLogger = _car != null ? new WetResearchLogger(_car.CarPath) : null;
            }
#endif
            try
            {
                string carPath, carVersion;
                ShiftModelState state;
                lock (_lock)
                {
                    if (_learner == null || _car == null || !_learner.TakeDirty()) return;
                    carPath = _car.CarPath;
                    carVersion = _car.CarVersion;
                    state = _learner.ExportState();
                }
                SaveState(carPath, carVersion, state);
            }
            catch (Exception ex)
            {
                Log.Error("[ShiftPoint] flush failed", ex);
            }
        }

        /// <summary>
        /// Once a minute: how many frames the learner kept vs skipped (and why), and what each
        /// gear is waiting on. Silent while nothing but ineligible frames (garage, pits) arrive.
        /// </summary>
        private void LogProgress()
        {
            string line;
            lock (_lock)
            {
                if (_learner == null || _car == null) return;
                var (kept, skipped) = _learner.TakeCounters();
                long active = kept;
                for (int i = 0; i < skipped.Length; i++)
                    if (i != (int)SkipReason.Ineligible) active += skipped[i];
                if (active == 0) return;

                var sb = new System.Text.StringBuilder();
                sb.Append($"[ShiftPoint] progress ({Describe(_car)}): {kept} frames kept, skipped:");
                for (int i = 0; i < skipped.Length; i++)
                    if (skipped[i] > 0) sb.Append($" {(SkipReason)i} {skipped[i]},");
                if (sb[^1] == ',') sb.Length--;
                else sb.Append(" none");
                sb.Append(" | total model samples ").Append(_learner.SampleCount);

                var learned = _learnedRpm;
                foreach (var e in _lastEstimates)
                {
                    sb.Append(" | g").Append(e.Gear).Append(' ');
                    if (e.Gear < learned.Length && learned[e.Gear] > 0)
                    {
                        sb.Append("learned ").Append(learned[e.Gear]);
                        // Only for holds-to-redline gears, where it says how close the call was.
                        if (e.Reason == "holds to redline" && !double.IsNaN(e.NextGearThrustAtRedlinePct))
                            sb.Append($" (next gear {e.NextGearThrustAtRedlinePct - 100:+0;-0}% at redline)");
                    }
                    else if (e.Gear < _provisionalRpm.Length && _provisionalRpm[e.Gear] > 0)
                    {
                        sb.Append($"stepping up {_provisionalRpm[e.Gear]}");
                        if (e.Provisional) sb.Append($" (proven to {e.ProvenRpm})");
                    }
                    else if (e.BandsNeeded > 0)
                    {
                        sb.Append($"waiting (est {e.Rpm}): {e.BandsSeen}/{e.BandsNeeded} RPM bands seen");
                        if (e.MissingBands.Length > 0) sb.Append($" (missing {e.MissingBands})");
                    }
                    else sb.Append("waiting: ").Append(e.Reason);
                }
                if (_lastEstimates.Length == 0) sb.Append(" | no fit yet");
                line = sb.ToString();
            }
            Log.Info(line);
        }

        private static string Describe(PlayerCarInfo car) =>
            string.IsNullOrEmpty(car.CarScreenName) ? car.CarPath : car.CarScreenName;

        #endregion
    }
}
