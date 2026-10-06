using System;

namespace VISOR.Telemetry
{
    /// <summary>
    /// One telemetry frame as the shift-point learner sees it. Plain values only, so the learner
    /// has no dependency on the SDK or WPF and can be unit-tested with synthetic data.
    /// </summary>
    public readonly record struct ShiftSample(
        double SessionTime,     // s; used to detect gear changes and skip the post-shift transient
        int Gear,               // -1 R, 0 N, 1..n
        float Rpm,
        float Speed,            // m/s
        float LongAccel,        // m/s^2, including gravity
        float LatAccel,         // m/s^2; hard cornering is excluded from the torque fit
        float Throttle,         // 0..1
        float Brake,            // 0..1
        float Clutch,           // 0..1, 1 = fully engaged
        bool Eligible);         // caller's gate: on track, not in pits/replay/limiter, track dry

    /// <summary>
    /// Result of solving for one gear's optimal upshift.
    /// </summary>
    /// <remarks>
    /// <see cref="BandsSeen"/> / <see cref="BandsNeeded"/>: how many of the 250-RPM torque bands
    /// around the shift point (in this gear and where the next gear lands) have enough data yet.
    /// </remarks>
    public readonly record struct GearShiftEstimate(int Gear, int Rpm, bool Confident, string Reason,
        int BandsSeen = 0, int BandsNeeded = 0);

    /// <summary>Why a frame was left out of the torque model (for progress logging).</summary>
    public enum SkipReason
    {
        Ineligible,     // caller's gate: pits, off track, replay, limiter, wet
        NotInGear,
        PostShift,
        LowSpeed,
        ClutchOrBrake,
        PartThrottle,
        Cornering,
        RatioWarmup,    // gear ratio not measured yet this session
        Wheelspin,
        OutOfRange
    }

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
        private const double PostShiftSettle = 0.5;     // s ignored after any gear change
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
        public double GetRatio(int gear) =>
            gear >= 1 && gear <= MaxGears && _ratios[gear].Count >= MinRatioSamples ? _ratios[gear].Median : 0;

        public double GetBinWeight(int bin) => bin >= 0 && bin < BinCount ? _binWeight[bin] : 0;

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
            if (kg <= 0 || kn <= 0) return new GearShiftEstimate(g, 0, false, "gear ratio not measured yet");

            double rho = kn / kg;   // RPM drop factor on the upshift
            if (rho < 0.4 || rho > 0.98) return new GearShiftEstimate(g, 0, false, $"implausible ratio step {rho:F2}");

            double minRpm = 0.5 * redLine;
            double step = 10;
            double prevDiff = double.NaN, prevR = double.NaN;
            double crossover = double.NaN;

            for (double r = minRpm; r <= redLine; r += step)
            {
                // Thrust in the current gear vs in the next gear at the same road speed.
                double diff = kg * Torque(theta, r) - kn * Torque(theta, rho * r);
                if (diff <= 0)
                {
                    crossover = double.IsNaN(prevDiff) ? r : prevR + step * prevDiff / (prevDiff - diff);
                    break;
                }
                prevDiff = diff;
                prevR = r;
            }

            if (double.IsNaN(prevDiff) && !double.IsNaN(crossover))
                return new GearShiftEstimate(g, (int)Math.Round(crossover), false, "crossover below half redline");

            // Curves never crossed below the redline: hold the gear to the redline.
            bool atRedline = double.IsNaN(crossover);
            double rpm = atRedline ? redLine : crossover;

            // Only trust the answer when the torque curve is well observed where it matters: around
            // the shift point in this gear and around where the engine lands in the next gear.
            var (seenHi, neededHi) = CountCoverage(rpm - CoverageMarginRpm, Math.Min(rpm + CoverageMarginRpm, redLine));
            var (seenLo, neededLo) = CountCoverage(rho * rpm - CoverageMarginRpm, rho * rpm + CoverageMarginRpm);
            int seen = seenHi + seenLo, needed = neededHi + neededLo;
            if (seen < needed)
            {
                return new GearShiftEstimate(g, (int)Math.Round(rpm), false, "torque curve not yet observed around shift point",
                    seen, needed);
            }

            return new GearShiftEstimate(g, (int)Math.Round(rpm), true, atRedline ? "holds to redline" : "crossover",
                seen, needed);
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

        // How many of the RPM bands spanning [rpmLo, rpmHi] have enough data (MinBinWeight).
        private (int Seen, int Needed) CountCoverage(double rpmLo, double rpmHi)
        {
            int lo = Math.Clamp((int)Math.Floor(rpmLo / BinWidthRpm), 0, BinCount - 1);
            int hi = Math.Clamp((int)Math.Floor(rpmHi / BinWidthRpm), 0, BinCount - 1);
            int seen = 0;
            for (int i = lo; i <= hi; i++)
                if (_binWeight[i] >= MinBinWeight) seen++;
            return (seen, hi - lo + 1);
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

            return CholeskySolve(m, rhs, n);
        }

        private static double[]? CholeskySolve(double[] a, double[] b, int n)
        {
            // In-place lower-triangular factorization: a = L L^T.
            for (int j = 0; j < n; j++)
            {
                double sum = a[j * n + j];
                for (int k = 0; k < j; k++) sum -= a[j * n + k] * a[j * n + k];
                if (sum <= 0 || !double.IsFinite(sum)) return null;
                double ljj = Math.Sqrt(sum);
                a[j * n + j] = ljj;
                for (int i = j + 1; i < n; i++)
                {
                    double s = a[i * n + j];
                    for (int k = 0; k < j; k++) s -= a[i * n + k] * a[j * n + k];
                    a[i * n + j] = s / ljj;
                }
            }
            var y = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = b[i];
                for (int k = 0; k < i; k++) s -= a[i * n + k] * y[k];
                y[i] = s / a[i * n + i];
            }
            var x = new double[n];
            for (int i = n - 1; i >= 0; i--)
            {
                double s = y[i];
                for (int k = i + 1; k < n; k++) s -= a[k * n + i] * x[k];
                x[i] = s / a[i * n + i];
            }
            foreach (var v in x) if (!double.IsFinite(v)) return null;
            return x;
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
            return new ShiftModelState(upper, (double[])_atb.Clone(), (double[])_binWeight.Clone(), _sampleCount);
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
        }

        #endregion

        /// <summary>
        /// Rolling median of a gear's RPM/speed ratio. A median rather than a mean so brief
        /// wheelspin or a locked wheel doesn't drag the estimate.
        /// </summary>
        private sealed class RatioTracker
        {
            private const int Capacity = 240;   // ~4 s at 60 Hz
            private const int RecomputeEvery = 30;
            private readonly double[] _buf = new double[Capacity];
            private readonly double[] _scratch = new double[Capacity];
            private int _next;
            private int _sinceMedian = RecomputeEvery;
            private double _median;

            public int Count { get; private set; }

            public void Add(double v)
            {
                _buf[_next] = v;
                _next = (_next + 1) % Capacity;
                if (Count < Capacity) Count++;
                if (_sinceMedian < RecomputeEvery) _sinceMedian++;
            }

            public double Median
            {
                get
                {
                    // Recompute at most every RecomputeEvery adds; the ratio doesn't change mid-gear.
                    if (_sinceMedian >= RecomputeEvery && Count > 0)
                    {
                        Array.Copy(_buf, _scratch, Count);
                        Array.Sort(_scratch, 0, Count);
                        _median = _scratch[Count / 2];
                        _sinceMedian = 0;
                    }
                    return _median;
                }
            }
        }
    }

    /// <summary>The persisted torque statistics of a <see cref="ShiftPointLearner"/>.</summary>
    public sealed record ShiftModelState(double[] AtaUpper, double[] Atb, double[] BinWeights, long SampleCount);
}
