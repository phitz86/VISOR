using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VISOR.Diagnostics;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    /// <summary>
    /// What the ⚙ gear symbol shows.
    /// </summary>
    public enum ShiftState
    {
        Normal,     // light gray
        Approach,   // solid amber: shift point coming up
        ShiftNow,   // flashing red/white: shift now
        Limiter     // solid red: at redline / on the rev limiter
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
        private int _fitRunning;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _lastFit;
        private TimeSpan _lastSave;
        private long _lastFitSampleCount = -1;

#if DEBUG
        private ShiftPointLogger? _debugLogger;
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

            int rpm;
            var learned = _learnedRpm;
            if (gear < learned.Length && learned[gear] > 0) rpm = learned[gear];
            else if (snapshot.PlayerCarSLShiftRPM > 0f) rpm = (int)snapshot.PlayerCarSLShiftRPM;
            else rpm = car.SLShiftRPM > 0f ? (int)car.SLShiftRPM : 0;

            // Keep the shift point a little under the redline. When a gear is best held to the
            // redline, this lets the flash show (and leaves time to react) before the solid-red
            // limiter state takes over.
            if (rpm > 0 && car.RedLine > 0)
                rpm = Math.Min(rpm, (int)(car.RedLine - Math.Max(RedLineMarginMinRpm, RedLineMarginFraction * car.RedLine)));
            return rpm;
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
#endif
            }

            var now = _clock.Elapsed;
            if (now - _lastFit >= FitInterval)
            {
                _lastFit = now;
                ScheduleFit();
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

            int gen;
            lock (_lock)
            {
                _carKey = key;
                _car = car;
                _learner = null;
                gen = ++_generation;
                _lastFitSampleCount = -1;
                _learnedRpm = new int[ShiftPointLearner.MaxGears + 1];
                for (int i = 0; i < _recentFits.Length; i++) _recentFits[i] = Array.Empty<int>();
#if DEBUG
                _debugLogger?.Dispose();
                _debugLogger = new ShiftPointLogger(car.CarPath);
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
                        ApplyEstimates(learner.Solve(car.RedLine, car.GearNumForward), car);
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
        private void ApplyEstimates(GearShiftEstimate[] estimates, PlayerCarInfo car)
        {
            var next = (int[])_learnedRpm.Clone();
            foreach (var e in estimates)
            {
                if (e.Gear < 1 || e.Gear >= next.Length) continue;

                var recent = _recentFits[e.Gear];
                if (!e.Confident)
                {
                    // Keep an already-learned value; a not-yet-confident fit just doesn't add to it.
                    _recentFits[e.Gear] = Array.Empty<int>();
                }
                else
                {
                    var updated = new int[Math.Min(recent.Length + 1, StableFits)];
                    Array.Copy(recent, Math.Max(0, recent.Length - (updated.Length - 1)), updated, 0, updated.Length - 1);
                    updated[^1] = e.Rpm;
                    _recentFits[e.Gear] = updated;

                    if (updated.Length == StableFits)
                    {
                        int min = int.MaxValue, max = int.MinValue; long sum = 0;
                        foreach (var r in updated) { min = Math.Min(min, r); max = Math.Max(max, r); sum += r; }
                        if (max - min <= StableSpreadRpm)
                        {
                            int value = (int)(Math.Round(sum / (double)updated.Length / 10.0) * 10);
                            int old = next[e.Gear];
                            next[e.Gear] = value;
                            if (old == 0 || Math.Abs(old - value) > LogChangeRpm)
                            {
                                Log.Info($"[ShiftPoint] {Describe(car)} gear {e.Gear}: iRacing {car.SLShiftRPM:F0} -> learned {value} RPM ({e.Reason})");
                            }
                        }
                    }
                }

#if DEBUG
                _debugLogger?.LogEstimate(e);
#endif
            }

            _learnedRpm = next;
        }

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

        private static string Describe(PlayerCarInfo car) =>
            string.IsNullOrEmpty(car.CarScreenName) ? car.CarPath : car.CarScreenName;

        #endregion
    }
}
