using System;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Learns a car's optimal upshift RPM per gear from the driver's own full-throttle telemetry.
    ///
    /// Physics: at the same road speed, drag and rolling resistance are identical in either gear,
    /// so the best upshift from gear g is where thrust in g+1 overtakes thrust in g. Thrust at the
    /// wheels is proportional to T(rpm) * k_g, where T is the engine's torque curve (the same in
    /// every gear) and k_g is the gear's overall ratio expressed as RPM per m/s of road speed.
    ///
    /// Model, fit by least squares over full-throttle samples from all gears pooled together:
    ///     LongAccel = k_g * T(rpm) - beta * v^2 - gamma
    /// T is a piecewise-linear curve over fixed RPM bins (mass and wheel radius fold into T's
    /// scale, which cancels out of the crossover). Pooling across gears is what makes the fit
    /// work: the same RPM bin is seen at different road speeds in different gears, which
    /// separates the engine's torque from aerodynamic drag, and full-throttle runs in one gear
    /// fill in the torque curve for the others.
    ///
    /// The normal equations (A^T A, A^T b) are accumulated incrementally — each sample touches a
    /// handful of cells — and persisted per car, so learning carries across sessions. Gear ratios
    /// are deliberately NOT persisted: they're re-measured each session, so a setup with different
    /// gearing is handled automatically.
    ///
    /// Not thread-safe; the owner (ShiftPointProvider) serializes access.
    /// </summary>
    public sealed class ShiftPointLearner
    {
        // --- Model shape (part of the persisted format; bump ShiftModelStore.SchemaVersion if changed) ---
        public const int BinWidthRpm = 250;
        public const int BinCount = 96;                 // covers 0..24,000 RPM
        public const int ParamCount = BinCount + 2;     // + drag (v^2) + constant
        private const int DragIdx = BinCount;
        private const int ConstIdx = BinCount + 1;

        // Feature scaling keeps the matrix well conditioned. k is ~100-600 RPM per m/s, v^2 is up
        // to ~10,000.
        private const double KScale = 0.01;
        private const double V2Scale = 0.001;

        // --- Sample gating ---
        private const float MinSpeed = 8f;              // m/s; below this RPM/speed is noisy (launches, clutch slip)
        private const float FullThrottle = 0.98f;
        private const float MaxBrake = 0.02f;
        private const float ClutchEngaged = 0.99f;
        private const double PostShiftSettle = 0.25;    // s ignored after any gear change (clutch and wheelspin checks catch the rest)
        private const double RatioSlipTolerance = 0.03; // sample's RPM/speed must be within 3% of the gear's ratio
        private const float MaxAbsAccel = 30f;

        // Cornering: while the tires carry side load, part of their grip goes to turning and
        // forward acceleration drops, which the model would otherwise blame on the engine.
        // Corner exits happen at similar RPMs every lap, so that bias would pile up in one part
        // of the torque curve rather than averaging out. Lateral g is the gate rather than
        // steering angle (steering ratio and lock differ per car) or yaw rate (the same rate
        // means very different cornering at different speeds): it measures the side load
        // directly, in the same units for every car. 3 m/s^2 ≈ 0.3 g.
        private const float MaxLatAccel = 3f;

        // --- Confidence ---
        public const double MinBinWeight = 60;          // ≈ 1 s of full-throttle data per 250-RPM bin
        public const int MinRatioSamples = 60;
        private const double CoverageMarginRpm = 500;

        public const int MaxGears = 10;

        // How far a provisional cue may step past the proven point (one band).
        public const int ProvisionalStepRpm = BinWidthRpm;

        private readonly double[] _ata = new double[ParamCount * ParamCount];
        private readonly double[] _atb = new double[ParamCount];
        private readonly double[] _binWeight = new double[BinCount];
        private long _sampleCount;

        private readonly RatioTracker[] _ratios = new RatioTracker[MaxGears + 1];

        private int _lastGear = int.MinValue;
        private double _lastGearChangeTime = double.NegativeInfinity;
        private double _lastSessionTime = double.NegativeInfinity;

        public ShiftPointLearner()
        {
            for (int i = 0; i < _ratios.Length; i++) _ratios[i] = new RatioTracker();
        }

        /// <summary>Total full-throttle samples in the torque model (includes persisted data).</summary>
        public long SampleCount => _sampleCount;

        /// <summary>Samples added since the last call to <see cref="TakeDirty"/>.</summary>
        private long _dirtySamples;
        public bool TakeDirty() { bool d = _dirtySamples > 0; _dirtySamples = 0; return d; }

        /// <summary>The gear's measured overall ratio this session (RPM per m/s), or 0 if not yet known.</summary>
        public double GetRatio(int gear)
        {
            if (gear < 1 || gear > MaxGears) return 0;
            double live = MeasuredRatio(gear);
            if (live > 0) return live;
            // Not driven enough in this gear yet this session: use the ratio saved last time,
            // but only once the gears measured so far prove the gearing hasn't changed.
            return _savedRatios[gear] > 0 && SavedRatiosStillValid() ? _savedRatios[gear] : 0;
        }

        private double MeasuredRatio(int gear) =>
            _ratios[gear].Count >= MinRatioSamples ? _ratios[gear].Median : 0;

        // Saved ratios (from the persisted model) are trusted only when at least one gear measured
        // this session has a saved ratio, and every such gear matches it within 1%: evidence the
        // setup's gearing is the same as last time. A setup with different gearing fails this,
        // and every ratio is re-measured live.
        public const double SavedRatioTolerance = 0.01;
        private readonly double[] _savedRatios = new double[MaxGears + 1];

        private bool SavedRatiosStillValid()
        {
            bool anyMatch = false;
            for (int g = 1; g <= MaxGears; g++)
            {
                double live = MeasuredRatio(g), saved = _savedRatios[g];
                if (live <= 0 || saved <= 0) continue;
                if (Math.Abs(live - saved) > saved * SavedRatioTolerance) return false;
                anyMatch = true;
            }
            return anyMatch;
        }

        /// <summary>
        /// Feeds one frame. Updates the gear-ratio estimate whenever the drivetrain is locked up,
        /// and adds a torque-model sample when the frame is a clean full-throttle frame.
        /// Returns true when the frame was added to the torque model.
        /// </summary>
        private readonly long[] _skipCounts = new long[Enum.GetValues<SkipReason>().Length];
        private long _keptCount;

        /// <summary>
        /// Kept-sample count and per-reason skip counts since the last call, then resets them.
        /// </summary>
        public (long Kept, long[] Skipped) TakeCounters()
        {
            var skipped = (long[])_skipCounts.Clone();
            long kept = _keptCount;
            Array.Clear(_skipCounts);
            _keptCount = 0;
            return (kept, skipped);
        }

        private bool Skip(SkipReason reason)
        {
            _skipCounts[(int)reason]++;
            return false;
        }

        public bool AddSample(in ShiftSample s)
        {
            // Session clock went backwards (reset to pits, new session): restart shift tracking.
            if (s.SessionTime < _lastSessionTime)
            {
                _lastGear = int.MinValue;
                _lastGearChangeTime = double.NegativeInfinity;
            }
            _lastSessionTime = s.SessionTime;

            if (s.Gear != _lastGear)
            {
                _lastGear = s.Gear;
                _lastGearChangeTime = s.SessionTime;
            }

            if (!s.Eligible) return Skip(SkipReason.Ineligible);
            if (s.Gear < 1 || s.Gear > MaxGears) return Skip(SkipReason.NotInGear);
            if (s.SessionTime - _lastGearChangeTime < PostShiftSettle) return Skip(SkipReason.PostShift);
            if (s.Speed < MinSpeed || s.Rpm <= 0f) return Skip(SkipReason.LowSpeed);
            if (s.Clutch < ClutchEngaged || s.Brake > MaxBrake) return Skip(SkipReason.ClutchOrBrake);
            if (!float.IsFinite(s.Rpm) || !float.IsFinite(s.Speed) || !float.IsFinite(s.LongAccel) || !float.IsFinite(s.LatAccel)) return Skip(SkipReason.OutOfRange);

            double ratioNow = s.Rpm / s.Speed;
            var tracker = _ratios[s.Gear];
            tracker.Add(ratioNow);

            if (s.Throttle < FullThrottle) return Skip(SkipReason.PartThrottle);
            if (Math.Abs(s.LongAccel) > MaxAbsAccel) return Skip(SkipReason.OutOfRange);
            // After the ratio update on purpose: cornering frames still measure the gear ratio
            // (valid while the tires grip), they're just kept out of the torque fit.
            if (Math.Abs(s.LatAccel) > MaxLatAccel) return Skip(SkipReason.Cornering);
            if (tracker.Count < MinRatioSamples) return Skip(SkipReason.RatioWarmup);

            // Wheelspin (or locking) shows up as RPM/speed drifting off the gear's true ratio.
            double k = tracker.Median;
            if (Math.Abs(ratioNow - k) > k * RatioSlipTolerance) return Skip(SkipReason.Wheelspin);

            if (!TryGetBinWeights(s.Rpm, out int j, out double w)) return Skip(SkipReason.OutOfRange);

            // Feature row (4 non-zeros): hat-function weights on two adjacent torque bins,
            // then the drag and constant terms.
            double ks = k * KScale;
            Span<int> idx = stackalloc int[4] { j, j + 1, DragIdx, ConstIdx };
            Span<double> val = stackalloc double[4] { ks * (1 - w), ks * w, -(double)s.Speed * s.Speed * V2Scale, -1.0 };
            double y = s.LongAccel;

            for (int a = 0; a < 4; a++)
            {
                _atb[idx[a]] += val[a] * y;
                for (int b = 0; b < 4; b++)
                    _ata[idx[a] * ParamCount + idx[b]] += val[a] * val[b];
            }
            _binWeight[j] += 1 - w;
            _binWeight[j + 1] += w;
            _sampleCount++;
            _dirtySamples++;
            _keptCount++;
            return true;
        }

        // Hat basis: bin i is centered at (i + 0.5) * BinWidth. Returns the lower bin and the
        // weight of the upper one.
        private static bool TryGetBinWeights(double rpm, out int j, out double w)
        {
            double x = rpm / BinWidthRpm - 0.5;
            j = (int)Math.Floor(x);
            w = x - j;
            if (j < 0) { j = 0; w = 0; }
            return j + 1 < BinCount;
        }

        /// <summary>
        /// Solves the torque model and computes each gear's optimal upshift.
        /// </summary>
        /// <param name="redLine">The car's redline (session YAML); crossovers are clamped to it.</param>
        /// <param name="gearCount">Forward gears; no upshift is computed for the top gear.</param>
        public GearShiftEstimate[] Solve(float redLine, int gearCount)
        {
            gearCount = Math.Clamp(gearCount, 0, MaxGears);
            var results = new GearShiftEstimate[Math.Max(gearCount - 1, 0)];
            for (int g = 1; g < gearCount; g++)
                results[g - 1] = new GearShiftEstimate(g, 0, false, "no fit");

            if (redLine <= 0 || gearCount < 2) return results;

            double[]? theta = SolveTorqueCurve();
            if (theta == null)
            {
                for (int g = 1; g < gearCount; g++)
                    results[g - 1] = new GearShiftEstimate(g, 0, false, "not enough data");
                return results;
            }

            for (int g = 1; g < gearCount; g++)
                results[g - 1] = SolveGear(theta, g, redLine);
            return results;
        }

        private GearShiftEstimate SolveGear(double[] theta, int g, float redLine)
        {
            double kg = GetRatio(g), kn = GetRatio(g + 1);
            if (kg <= 0) return new GearShiftEstimate(g, 0, false, "gear ratio not measured yet");
            if (kn <= 0) return new GearShiftEstimate(g, 0, false, $"gear {g + 1} not driven yet");

            double rho = kn / kg;   // RPM drop factor on the upshift
            if (rho < 0.4 || rho > 0.98) return new GearShiftEstimate(g, 0, false, $"implausible ratio step {rho:F2}");

            // The best upshift is the RPM after which the next gear pulls harder all the way to the
            // redline. Scan DOWN from the redline for the highest RPM where this gear is still the
            // stronger one: the answer then rests on the top of the rev range, which normal driving
            // covers every lap, and a wiggle in the fitted curve down in rarely-driven low RPM can't
            // stop the search early (an upward "first crossing" search could, and would also shift
            // too early on an engine whose power dips and recovers).
            double minRpm = 0.5 * redLine;
            const double step = 10;
            double Diff(double r) => kg * Torque(theta, r) - kn * Torque(theta, rho * r);

            double crossover = double.NaN;   // NaN = this gear is still stronger at the redline
            bool crossesBelowHalf = false;
            double dTop = Diff(redLine);
            if (dTop <= 0)
            {
                double prevD = dTop;
                for (double r = redLine - step; r >= minRpm; r -= step)
                {
                    double d = Diff(r);
                    if (d > 0)
                    {
                        crossover = r + step * d / (d - prevD);   // interpolate between r and r + step
                        break;
                    }
                    prevD = d;
                }
                if (double.IsNaN(crossover)) crossesBelowHalf = true;   // next gear stronger everywhere we look
            }

            if (crossesBelowHalf)
                return new GearShiftEstimate(g, (int)Math.Round(minRpm), false, "crossover below half redline");

            // How decisive the answer is: next gear's thrust vs this gear's, if shifting at the redline.
            double tRed = kg * Torque(theta, redLine);
            double marginPct = tRed > 0 ? 100.0 * kn * Torque(theta, rho * redLine) / tRed : double.NaN;

            // Curves never crossed below the redline: hold the gear to the redline.
            bool atRedline = double.IsNaN(crossover);
            double rpm = atRedline ? redLine : crossover;

            // Proven point: the highest RPM at or below the answer where "this gear still pulls
            // harder" rests on observed data in both gears. The best shift is at least that high
            // (the answer is the LAST point where this gear is stronger), whatever the curve does
            // lower down.
            double proven = double.NaN;
            for (double r = Math.Floor(rpm / step) * step; r >= minRpm; r -= step)
            {
                if (Diff(r) <= 0) continue;
                if (IsObserved(r) && IsObserved(rho * r)) { proven = r; break; }
            }

            // Only trust the answer when the torque curve is well observed where it matters: around
            // the shift point in this gear and around where the engine lands in the next gear.
            var missing = new System.Collections.Generic.List<int>();
            var (seenHi, neededHi) = CountCoverage(rpm - CoverageMarginRpm, Math.Min(rpm + CoverageMarginRpm, redLine), missing);
            // Next gear: from where the engine lands after the upshift, upward. After shifting at
            // rpm the engine is never below rho*rpm in normal driving, so data lower than that
            // can't affect the answer, and real drivers rarely provide it. (Half a band below the
            // landing point keeps the band the landing RPM interpolates from.)
            var (seenLo, neededLo) = CountCoverage(rho * rpm - BinWidthRpm / 2.0, rho * rpm + CoverageMarginRpm, missing);
            int seen = seenHi + seenLo, needed = neededHi + neededLo;
            if (seen < needed)
            {
                missing.Sort();
                string missingText = string.Join(" ", missing);

                // Not confident, but if the data proves this gear still out-pulls the next up to
                // some RPM, the best shift is at least that high: cue one step past it (capped at
                // the estimate) so the driver revs high enough to confirm the next step.
                if (!double.IsNaN(proven) && proven < rpm)
                {
                    int cue = (int)Math.Round(Math.Min(proven + ProvisionalStepRpm, rpm));
                    return new GearShiftEstimate(g, cue, false, $"stepping up: proven to {proven:F0}",
                        seen, needed, missingText, marginPct, Provisional: true, ProvenRpm: (int)Math.Round(proven));
                }

                return new GearShiftEstimate(g, (int)Math.Round(rpm), false, "torque curve not yet observed around shift point",
                    seen, needed, missingText, marginPct);
            }

            return new GearShiftEstimate(g, (int)Math.Round(rpm), true, atRedline ? "holds to redline" : "crossover",
                seen, needed, "", marginPct);
        }

        /// <summary>
        /// The fitted torque curve's relative value at <paramref name="rpm"/> (arbitrary scale,
        /// only ratios between RPMs are meaningful), or null if there isn't enough data to fit.
        /// For diagnostics and tests; solves the model on each call.
        /// </summary>
        public double? RelativeTorqueAt(double rpm)
        {
            var theta = SolveTorqueCurve();
            return theta == null ? null : Torque(theta, rpm);
        }

        /// <summary>
        /// The fitted curve per 250-RPM band center up to <paramref name="redLine"/>: relative
        /// torque and power (scaled so peak power in range = 100) and the band's sample weight.
        /// Null if there isn't enough data to fit. For the debug CSV; solves the model.
        /// </summary>
        public (int Rpm, double RelTorque, double RelPower, double Weight)[]? GetCurve(float redLine)
        {
            var theta = SolveTorqueCurve();
            if (theta == null || redLine <= 0) return null;

            var rows = new System.Collections.Generic.List<(int, double, double, double)>();
            double peakPower = 0;
            for (int i = 0; i < BinCount && BinCenterRpm(i) <= redLine; i++)
            {
                int rpm = BinCenterRpm(i);
                double t = Torque(theta, rpm);
                peakPower = Math.Max(peakPower, t * rpm);
                rows.Add((rpm, t, t * rpm, _binWeight[i]));
            }
            if (peakPower <= 0) return null;
            double peakTorque = 0;
            foreach (var r in rows) peakTorque = Math.Max(peakTorque, r.Item2);
            var result = new (int, double, double, double)[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                var (rpm, t, p, w) = rows[i];
                result[i] = (rpm, 100.0 * t / peakTorque, 100.0 * p / peakPower, w);
            }
            return result;
        }

        // How many of the RPM bands spanning [rpmLo, rpmHi] have enough data (MinBinWeight).
        // Bands are hat functions centered at (i + 0.5) * BinWidth, so the bands that describe the
        // curve over [rpmLo, rpmHi] are the ones whose centers fall inside it. (Selecting by
        // floor(rpm / BinWidth) instead pulled in the band centered *above* the top of the range;
        // with the range capped at the redline, that band - centered 125 RPM over the redline -
        // could only be filled by frames right at the rev limiter, so gears near the redline
        // never became confident.)
        private (int Seen, int Needed) CountCoverage(double rpmLo, double rpmHi, System.Collections.Generic.List<int> missing)
        {
            int lo = Math.Max((int)Math.Ceiling(rpmLo / BinWidthRpm - 0.5), 0);
            int hi = Math.Min((int)Math.Floor(rpmHi / BinWidthRpm - 0.5), BinCount - 1);
            int seen = 0;
            for (int i = lo; i <= hi; i++)
            {
                if (_binWeight[i] >= MinBinWeight) seen++;
                else missing.Add(BinCenterRpm(i));
            }
            return (seen, Math.Max(hi - lo + 1, 0));
        }

        private static int BinCenterRpm(int bin) => (int)((bin + 0.5) * BinWidthRpm);

        // The fitted torque at this RPM rests on observed data: both bands it interpolates
        // between have enough samples.
        private bool IsObserved(double rpm)
        {
            TryGetBinWeights(rpm, out int j, out double w);
            if (j + 1 >= BinCount) return false;
            return _binWeight[j] >= MinBinWeight && (w == 0 || _binWeight[j + 1] >= MinBinWeight);
        }

        private static double Torque(double[] theta, double rpm)
        {
            TryGetBinWeights(rpm, out int j, out double w);
            if (j + 1 >= BinCount) return theta[BinCount - 1];
            return theta[j] * (1 - w) + theta[j + 1] * w;
        }

        /// <summary>
        /// Solves the regularized normal equations. A light second-difference penalty keeps T
        /// smooth and gives unobserved bins sensible (interpolated) values; a tiny ridge keeps the
        /// system positive definite. Returns null when there's nothing to fit.
        /// </summary>
        private double[]? SolveTorqueCurve()
        {
            if (_sampleCount < 200) return null;

            int n = ParamCount;
            var m = (double[])_ata.Clone();
            var rhs = (double[])_atb.Clone();

            double diagSum = 0; int diagN = 0;
            for (int i = 0; i < BinCount; i++)
            {
                double d = m[i * n + i];
                if (d > 0) { diagSum += d; diagN++; }
            }
            if (diagN == 0) return null;
            double avgDiag = diagSum / diagN;

            double smooth = 0.02 * avgDiag;
            ReadOnlySpan<double> c = stackalloc double[3] { 1, -2, 1 };
            for (int i = 1; i < BinCount - 1; i++)
            {
                // Penalty smooth * (T[i-1] - 2 T[i] + T[i+1])^2
                for (int a = 0; a < 3; a++)
                    for (int b = 0; b < 3; b++)
                        m[(i - 1 + a) * n + (i - 1 + b)] += smooth * c[a] * c[b];
            }
            double ridge = 1e-6 * avgDiag + 1e-9;
            for (int i = 0; i < n; i++) m[i * n + i] += ridge;

            return LinearSolver.CholeskySolve(m, rhs, n);
        }

        #region Persistence

        /// <summary>Copies the persisted part of the model (torque statistics, not gear ratios).</summary>
        public ShiftModelState ExportState()
        {
            int n = ParamCount;
            var upper = new double[n * (n + 1) / 2];
            int p = 0;
            for (int i = 0; i < n; i++)
                for (int j = i; j < n; j++)
                    upper[p++] = _ata[i * n + j];
            // Ratios to remember: measured this session where available; otherwise keep the saved
            // value, but only if this session's gearing matched it (else it may be stale).
            bool savedValid = SavedRatiosStillValid();
            var ratios = new double[MaxGears + 1];
            for (int g = 1; g <= MaxGears; g++)
            {
                double live = MeasuredRatio(g);
                ratios[g] = live > 0 ? live : savedValid ? _savedRatios[g] : 0;
            }
            return new ShiftModelState(upper, (double[])_atb.Clone(), (double[])_binWeight.Clone(), _sampleCount, ratios);
        }

        /// <summary>Replaces the torque statistics with previously persisted ones. Caller validates shape.</summary>
        public void ImportState(ShiftModelState state)
        {
            int n = ParamCount;
            int p = 0;
            for (int i = 0; i < n; i++)
                for (int j = i; j < n; j++)
                {
                    double v = state.AtaUpper[p++];
                    _ata[i * n + j] = v;
                    _ata[j * n + i] = v;
                }
            Array.Copy(state.Atb, _atb, n);
            Array.Copy(state.BinWeights, _binWeight, BinCount);
            _sampleCount = state.SampleCount;
            _dirtySamples = 0;

            Array.Clear(_savedRatios);
            if (state.GearRatios != null)
                for (int g = 1; g < state.GearRatios.Length && g <= MaxGears; g++)
                    _savedRatios[g] = state.GearRatios[g];
        }

        #endregion
    }
}
