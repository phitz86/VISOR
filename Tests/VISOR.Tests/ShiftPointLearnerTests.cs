using VISOR.Telemetry;
using Xunit;
using Xunit.Abstractions;

namespace VISOR.Tests
{
    public class ShiftPointLearnerTests
    {
        private readonly ITestOutputHelper _out;
        public ShiftPointLearnerTests(ITestOutputHelper output) => _out = output;

        private const int ToleranceRpm = 50;

        private static ShiftPointLearner Train(int runs, Func<int, double> shiftAt, int seed = 1, bool wheelspin = false,
            Func<double, double>? torque = null, bool cornerExits = false, bool reportLateralG = true, bool revLimiter = false)
        {
            var learner = new ShiftPointLearner();
            var sim = new CarSimulator(seed, torque: torque, revLimiter: revLimiter);
            for (int i = 0; i < runs; i++)
                foreach (var s in sim.Run(shiftAt, wheelspin, cornerExits, reportLateralG))
                    learner.AddSample(s);
            return learner;
        }

        [Fact]
        public void Simulator_HasBothKindsOfGear()
        {
            // Sanity check on the test car itself: some gears should hold to the redline and
            // some should shift earlier, so the tests below exercise both outcomes.
            var optimal = Enumerable.Range(1, 5).Select(CarSimulator.OptimalShift).ToArray();
            Assert.Contains(optimal, r => r == (int)CarSimulator.RedLine);
            Assert.Contains(optimal, r => r < CarSimulator.RedLine - 200);
        }

        [Fact]
        public void LearnsOptimalShiftPerGear_WhenDriverShiftsAtRedline()
        {
            var learner = Train(runs: 25, shiftAt: _ => CarSimulator.RedLine);
            var results = learner.Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);

            Assert.Equal(5, results.Length);
            foreach (var r in results)
            {
                int expected = CarSimulator.OptimalShift(r.Gear);
                _out.WriteLine($"gear {r.Gear}: learned {r.Rpm} ({r.Reason}, confident={r.Confident}), true optimum {expected}");
                Assert.True(r.Confident, $"gear {r.Gear} not confident: {r.Reason}");
                Assert.InRange(r.Rpm, expected - ToleranceRpm, expected + ToleranceRpm);
            }
        }

        [Fact]
        public void LearnsOptimalShift_WhenDriverShiftsAtVaryingPoints()
        {
            var rng = new Random(7);
            var learner = Train(runs: 40, shiftAt: _ => 6800 + rng.NextDouble() * 700, seed: 3);
            var results = learner.Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);
            foreach (var r in results)
                _out.WriteLine($"varying gear {r.Gear}: {r.Rpm} ({r.Reason}, confident={r.Confident})");

            foreach (var r in results.Where(r => r.Confident))
            {
                int expected = CarSimulator.OptimalShift(r.Gear);
                _out.WriteLine($"gear {r.Gear}: learned {r.Rpm}, true optimum {expected}");
                Assert.InRange(r.Rpm, expected - ToleranceRpm, expected + ToleranceRpm);
            }
            Assert.Contains(results, r => r.Confident);
        }

        [Fact]
        public void LearnsEarlyShift_ForPeakyEngine()
        {
            // The driver still runs to the redline, as a late shift light would tell them to.
            var learner = Train(runs: 25, shiftAt: _ => CarSimulator.RedLine, torque: CarSimulator.PeakyTorque);
            var results = learner.Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);

            foreach (var r in results)
            {
                int expected = CarSimulator.OptimalShift(r.Gear, CarSimulator.PeakyTorque);
                _out.WriteLine($"peaky gear {r.Gear}: learned {r.Rpm} ({r.Reason}, confident={r.Confident}), true optimum {expected}");
                Assert.True(expected < CarSimulator.RedLine - 300, "test car should want an early shift");
                Assert.True(r.Confident, $"gear {r.Gear} not confident: {r.Reason}");
                Assert.InRange(r.Rpm, expected - ToleranceRpm, expected + ToleranceRpm);
            }
        }

        [Fact]
        public void DoesNotGuess_WhenDriverNeverRevsPastShiftPoint()
        {
            // The driver always lifts or short-shifts at 6000 (top gear included), so the torque
            // curve above 6000 is never seen. The learner must not claim a confident answer in
            // that unobserved range; the overlay then keeps using iRacing's shift light.
            var learner = Train(runs: 25, shiftAt: _ => 6000, torque: CarSimulator.PeakyTorque);
            var results = learner.Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);

            foreach (var r in results)
                _out.WriteLine($"short-shift gear {r.Gear}: {r.Rpm} ({r.Reason}, confident={r.Confident})");
            Assert.DoesNotContain(results, r => r.Confident && r.Rpm > 6000 + 500);
        }

        [Fact]
        public void IsNotConfident_WithLittleData()
        {
            var learner = new ShiftPointLearner();
            var sim = new CarSimulator();
            foreach (var s in sim.Run(_ => CarSimulator.RedLine).Take(300))   // ~5 s
                learner.AddSample(s);

            var results = learner.Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);
            Assert.All(results, r => Assert.False(r.Confident));
        }

        [Fact]
        public void WheelspinDoesNotSkewResult()
        {
            var clean = Train(runs: 25, shiftAt: _ => CarSimulator.RedLine, seed: 5)
                .Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);
            var spinning = Train(runs: 25, shiftAt: _ => CarSimulator.RedLine, seed: 5, wheelspin: true)
                .Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);

            for (int i = 0; i < clean.Length; i++)
            {
                _out.WriteLine($"gear {clean[i].Gear}: clean {clean[i].Rpm}, with wheelspin {spinning[i].Rpm}");
                Assert.True(spinning[i].Confident);
                Assert.InRange(spinning[i].Rpm, clean[i].Rpm - ToleranceRpm, clean[i].Rpm + ToleranceRpm);
            }
        }

        [Fact]
        public void CorneringDoesNotSkewResult()
        {
            var clean = Train(runs: 30, shiftAt: _ => CarSimulator.RedLine, seed: 11)
                .Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);
            var cornering = Train(runs: 30, shiftAt: _ => CarSimulator.RedLine, seed: 11, cornerExits: true)
                .Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);

            for (int i = 0; i < clean.Length; i++)
            {
                int expected = CarSimulator.OptimalShift(clean[i].Gear);
                _out.WriteLine($"gear {clean[i].Gear}: clean {clean[i].Rpm}, with corner exits {cornering[i].Rpm}, true optimum {expected}");
                Assert.True(cornering[i].Confident, cornering[i].Reason);
                Assert.InRange(cornering[i].Rpm, expected - ToleranceRpm, expected + ToleranceRpm);
            }
        }

        [Fact]
        public void CorneringWouldSkewTorqueCurveWithoutGate()
        {
            // Same driving, but the corner-exit frames claim zero lateral g so they slip past the
            // gate. Proves the scenario really distorts the fit, i.e. the gate is doing real work.
            var gated = Train(runs: 30, shiftAt: _ => CarSimulator.RedLine, seed: 11, cornerExits: true);
            var ungated = Train(runs: 30, shiftAt: _ => CarSimulator.RedLine, seed: 11, cornerExits: true, reportLateralG: false);

            // Torque shape compared as a ratio (the fit's absolute scale is arbitrary).
            const double lo = 4500, hi = 6500;
            double truth = CarSimulator.Torque(lo) / CarSimulator.Torque(hi);
            double gatedRatio = gated.RelativeTorqueAt(lo)!.Value / gated.RelativeTorqueAt(hi)!.Value;
            double ungatedRatio = ungated.RelativeTorqueAt(lo)!.Value / ungated.RelativeTorqueAt(hi)!.Value;
            _out.WriteLine($"T({lo})/T({hi}): truth {truth:F3}, gated {gatedRatio:F3}, ungated {ungatedRatio:F3}");

            Assert.InRange(gatedRatio, truth * 0.97, truth * 1.03);
            Assert.True(Math.Abs(ungatedRatio / truth - 1) > 0.05,
                $"expected cornering to distort the ungated fit by >5%, got {ungatedRatio / truth - 1:P1}");
        }

        [Fact]
        public void RejectsIneligibleAndTransientFrames()
        {
            var learner = Train(runs: 2, shiftAt: _ => CarSimulator.RedLine);
            long before = learner.SampleCount;
            double t = 10_000;
            double k = CarSimulator.K(3);

            // Establish gear 3 well past the post-shift settle time.
            for (int i = 0; i < 120; i++, t += CarSimulator.Dt)
                learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 0f, 1, 0, 1, Eligible: true));
            long settled = learner.SampleCount;
            Assert.True(settled > before);

            Assert.False(learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 0f, 1, 0, 1, Eligible: false)));
            Assert.False(learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 0f, 0.5f, 0, 1, true)));     // part throttle
            Assert.False(learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 0f, 1, 0.5f, 1, true)));     // braking
            Assert.False(learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 0f, 1, 0, 0.5f, true)));     // clutch slipping
            Assert.False(learner.AddSample(new ShiftSample(t, 3, (float)(k * 40 * 1.1), 40, 3f, 0f, 1, 0, 1, true)));  // wheelspin
            Assert.False(learner.AddSample(new ShiftSample(t, 4, (float)(CarSimulator.K(4) * 40), 40, 3f, 0f, 1, 0, 1, true))); // just shifted
            Assert.False(learner.AddSample(new ShiftSample(t, 0, 3000, 40, 0f, 0f, 1, 0, 1, true)));                   // neutral
            for (int i = 0; i < 60; i++, t += CarSimulator.Dt)                                                        // settle in gear 3
                learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 0f, 1, 0, 1, true));
            Assert.False(learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 8f, 1, 0, 1, true)));       // hard cornering
            Assert.False(learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, -8f, 1, 0, 1, true)));      // ...either direction
            Assert.True(learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 1f, 1, 0, 1, true)));        // gentle curve is fine
        }

        [Fact]
        public void SkipCounters_AttributeRejections()
        {
            var learner = Train(runs: 2, shiftAt: _ => CarSimulator.RedLine);
            learner.TakeCounters();   // discard training counts
            double t = 20_000, k = CarSimulator.K(3);
            for (int i = 0; i < 120; i++, t += CarSimulator.Dt)
                learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 0f, 1, 0, 1, true));
            learner.TakeCounters();

            learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 8f, 1, 0, 1, true));       // cornering
            learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 0f, 0.5f, 0, 1, true));    // part throttle
            learner.AddSample(new ShiftSample(t, 3, (float)(k * 40 * 1.1), 40, 3f, 0f, 1, 0, 1, true)); // wheelspin
            learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 0f, 1, 0, 1, false));      // ineligible
            learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 0f, 1, 0, 1, true));       // kept

            var (kept, skipped) = learner.TakeCounters();
            Assert.Equal(1, kept);
            Assert.Equal(1, skipped[(int)SkipReason.Cornering]);
            Assert.Equal(1, skipped[(int)SkipReason.PartThrottle]);
            Assert.Equal(1, skipped[(int)SkipReason.Wheelspin]);
            Assert.Equal(1, skipped[(int)SkipReason.Ineligible]);

            var (kept2, skipped2) = learner.TakeCounters();
            Assert.Equal(0, kept2);
            Assert.All(skipped2, c => Assert.Equal(0, c));
        }

        [Fact]
        public void ReportsBandCoverage_WhileWaiting_AndWhenConfident()
        {
            var few = Train(runs: 2, shiftAt: _ => CarSimulator.RedLine).Solve(CarSimulator.RedLine, 6);
            Assert.Contains(few, e => !e.Confident && e.BandsNeeded > 0 && e.BandsSeen < e.BandsNeeded);

            var many = Train(runs: 25, shiftAt: _ => CarSimulator.RedLine).Solve(CarSimulator.RedLine, 6);
            Assert.All(many, e => { Assert.True(e.Confident); Assert.Equal(e.BandsNeeded, e.BandsSeen); });
        }

        [Theory]
        [InlineData(7350)]
        [InlineData(7500)]
        public void LearnsShiftAtRedline_WithRevLimiter_WhenDriverShiftsBeforeIt(double driverShiftRpm)
        {
            // Regression: the GR86 at Indy sat at "7/8 RPM bands seen" in every gear for 30 min.
            // When the best shift is at the redline, the coverage check demanded the band centered
            // above the redline (7625), which only limiter-adjacent frames could ever fill.
            var learner = Train(runs: 25, shiftAt: _ => driverShiftRpm, revLimiter: true);
            var results = learner.Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);

            foreach (var r in results)
            {
                int expected = CarSimulator.OptimalShift(r.Gear);
                _out.WriteLine($"limiter, shift@{driverShiftRpm} gear {r.Gear}: {r.Rpm} ({r.Reason}, confident={r.Confident}, " +
                               $"bands {r.BandsSeen}/{r.BandsNeeded}, missing [{r.MissingBands}]), true optimum {expected}");
            }

            var gear1 = results[0];
            Assert.Equal((int)CarSimulator.RedLine, CarSimulator.OptimalShift(1));   // the case under test
            Assert.True(gear1.Confident, $"gear 1 stuck: {gear1.Reason} ({gear1.BandsSeen}/{gear1.BandsNeeded}, missing {gear1.MissingBands})");
            Assert.InRange(gear1.Rpm, CarSimulator.RedLine - ToleranceRpm, CarSimulator.RedLine);
        }

        private static double AnalyticMarginPct(int gear, Func<double, double> torque)
        {
            double kg = CarSimulator.K(gear), kn = CarSimulator.K(gear + 1), r = CarSimulator.RedLine;
            return 100 * kn * torque(kn / kg * r) / (kg * torque(r));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MarginAtRedline_MatchesPhysics(bool peaky)
        {
            Func<double, double> torque = peaky ? CarSimulator.PeakyTorque : CarSimulator.Torque;
            var results = Train(runs: 25, shiftAt: _ => CarSimulator.RedLine, torque: peaky ? torque : null)
                .Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);

            foreach (var r in results)
            {
                double expected = AnalyticMarginPct(r.Gear, torque);
                _out.WriteLine($"{(peaky ? "peaky" : "smooth")} gear {r.Gear}: {r.Reason}, next gear at redline " +
                               $"{r.NextGearThrustAtRedlinePct:F1}% (true {expected:F1}%)");
                // The margin is only meaningful near 100% (a close call). Far from it - e.g. a peaky
                // engine whose power has collapsed by the redline - the ratio of two small numbers
                // is noisy, but it can't matter: the shift happens well before the redline.
                if (expected > 80 && expected < 120)
                    Assert.InRange(r.NextGearThrustAtRedlinePct, expected - 2.5, expected + 2.5);
                // Consistent with the decision: below 100% <=> holding to the redline.
                Assert.Equal(r.Reason == "holds to redline", r.NextGearThrustAtRedlinePct < 100);
            }
        }

        [Fact]
        public void Curve_IsScaledToPeak_AndTracksTrueShape()
        {
            var learner = Train(runs: 25, shiftAt: _ => CarSimulator.RedLine);
            var curve = learner.GetCurve(CarSimulator.RedLine);
            Assert.NotNull(curve);
            Assert.Equal(100, curve!.Max(c => c.RelPower), 3);
            Assert.True(curve.Last().Rpm <= CarSimulator.RedLine);

            // Within the well-observed range, relative power tracks the true curve's shape.
            double truePeak = curve.Max(c => CarSimulator.Torque(c.Rpm) * c.Rpm);
            foreach (var c in curve.Where(c => c.Weight >= ShiftPointLearner.MinBinWeight))
            {
                double truth = 100 * CarSimulator.Torque(c.Rpm) * c.Rpm / truePeak;
                Assert.InRange(c.RelPower, truth - 4, truth + 4);
            }
        }

        // The cue VISOR would show for a gear: learned value, else a provisional step if it's later
        // than the car's light, else the light.
        private static int Cue(GearShiftEstimate e, int baseline) =>
            e.Confident ? e.Rpm : e.Provisional && e.Rpm > baseline ? e.Rpm : baseline;

        [Fact]
        public void ClosedLoop_CueStepsUpFromEarlyLight_AndConverges()
        {
            // The car's light says 6700, but every gear's true optimum is 7100-7500. The driver
            // shifts wherever VISOR cues (top gear lifts at the light too, so no free data from
            // long straights), and the rev limiter hides the redline. Before provisional steps,
            // this loop could never leave 6700.
            const int Baseline = 6700;
            int gears = CarSimulator.GearRatios.Length;
            var cues = Enumerable.Repeat(Baseline, gears + 1).ToArray();
            var learner = new ShiftPointLearner();
            var sim = new CarSimulator(seed: 21, revLimiter: true);

            for (int session = 1; session <= 40; session++)
            {
                for (int run = 0; run < 3; run++)
                    foreach (var smp in sim.Run(g => cues[g]))
                        learner.AddSample(smp);

                var results = learner.Solve(CarSimulator.RedLine, gears);
                foreach (var e in results)
                {
                    cues[e.Gear] = Cue(e, Baseline);
                    // Never cue meaningfully past the true optimum: at most one step + fit noise.
                    Assert.True(cues[e.Gear] <= CarSimulator.OptimalShift(e.Gear) + ShiftPointLearner.ProvisionalStepRpm + 60,
                        $"session {session} gear {e.Gear}: cue {cues[e.Gear]} overshoots optimum {CarSimulator.OptimalShift(e.Gear)}");
                }
                if (session % 5 == 0 || session == 1)
                    _out.WriteLine($"session {session}: " + string.Join("  ", results.Select(e =>
                        $"g{e.Gear} {cues[e.Gear]}{(e.Confident ? "*" : e.Provisional ? $"(p{e.ProvenRpm})" : "")}")));
            }

            for (int g = 1; g < gears; g++)
            {
                int expected = CarSimulator.OptimalShift(g);
                // Holds-to-redline gears cue just under it (VISOR caps the cue below the redline anyway).
                Assert.InRange(cues[g], expected - 75, expected + ToleranceRpm);
            }
        }

        [Fact]
        public void Provisional_NeverWhenConfident_AndNeverBelowEvidence()
        {
            var learner = Train(runs: 6, shiftAt: _ => 6900, revLimiter: true);
            foreach (var e in learner.Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length))
            {
                Assert.False(e.Confident && e.Provisional);
                if (e.Provisional)
                {
                    Assert.True(e.ProvenRpm > 0 && e.Rpm > e.ProvenRpm);
                    Assert.True(e.Rpm - e.ProvenRpm <= ShiftPointLearner.ProvisionalStepRpm);
                    // Sound: the true optimum really is at least the proven point.
                    Assert.True(CarSimulator.OptimalShift(e.Gear) >= e.ProvenRpm - 30,
                        $"gear {e.Gear}: proven {e.ProvenRpm} but true optimum {CarSimulator.OptimalShift(e.Gear)}");
                }
            }
        }

        // A learner restored from a saved model, then driven only in 3rd gear this session at the
        // given ratio scale (1.0 = same gearing as when saved).
        private static ShiftPointLearner RestoredThenDriveThird(double ratioScale)
        {
            var saved = Train(runs: 3, shiftAt: _ => CarSimulator.RedLine).ExportState();
            var learner = new ShiftPointLearner();
            learner.ImportState(saved);
            double k = CarSimulator.K(3) * ratioScale;
            double t = 50_000;
            for (int i = 0; i < 200; i++, t += CarSimulator.Dt)
                learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 2f, 0f, 1, 0, 1, true));
            return learner;
        }

        [Fact]
        public void SavedRatio_UsedForUndrivenGear_WhenGearingMatches()
        {
            var learner = RestoredThenDriveThird(1.0);
            Assert.InRange(learner.GetRatio(6), CarSimulator.K(6) * 0.99, CarSimulator.K(6) * 1.01);
        }

        [Fact]
        public void SavedRatio_Rejected_WhenGearingChanged()
        {
            var learner = RestoredThenDriveThird(1.05);   // new setup: 5% shorter 3rd
            Assert.Equal(0, learner.GetRatio(6));
        }

        [Fact]
        public void SavedRatio_NotUsed_BeforeAnyGearIsMeasured()
        {
            var learner = new ShiftPointLearner();
            learner.ImportState(Train(runs: 3, shiftAt: _ => CarSimulator.RedLine).ExportState());
            Assert.Equal(0, learner.GetRatio(6));
        }

        // A "realistic" driver: no low-RPM pulls. Mostly corner exits that start in gears 2-5 just
        // below where the engine lands after an upshift, then wind it out to the shift point;
        // an occasional standing start in 1st. Top gear lifts at the shift point too (no free
        // data from long straights), and the rev limiter is on.
        private static ShiftPointLearner TrainRealistic(int runs, double shiftRpm, Func<double, double>? torque = null,
            int seed = 31, double exitSpreadRpm = 300)
        {
            var learner = new ShiftPointLearner();
            var sim = new CarSimulator(seed, torque: torque, revLimiter: true);
            var rng = new Random(seed);
            for (int i = 0; i < runs; i++)
            {
                int startGear = i % 10 == 0 ? 1 : 2 + rng.Next(4);
                double startRpm = startGear == 1 ? 0
                    : shiftRpm * CarSimulator.K(startGear) / CarSimulator.K(startGear - 1) - rng.NextDouble() * exitSpreadRpm;
                foreach (var s in sim.Run(_ => shiftRpm, startGear: startGear, startRpm: startRpm))
                    learner.AddSample(s);
            }
            return learner;
        }

        [Fact]
        public void RealisticDriving_LearnsAllGears_WithoutLowRpmPulls()
        {
            var results = TrainRealistic(runs: 60, shiftRpm: 7400).Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);
            foreach (var r in results)
            {
                int expected = CarSimulator.OptimalShift(r.Gear);
                _out.WriteLine($"realistic gear {r.Gear}: {r.Rpm} ({r.Reason}, confident={r.Confident}, missing [{r.MissingBands}]), true {expected}");
                Assert.True(r.Confident, $"gear {r.Gear}: {r.Reason} (missing {r.MissingBands})");
                Assert.InRange(r.Rpm, expected - ToleranceRpm, expected + ToleranceRpm);
            }
        }

        [Fact]
        public void RealisticDriving_PeakyEngine_NeverConfidentlyWrong_WhenLandingRpmUnseen()
        {
            // The driver winds it out to 7400 (a late light); the true best shifts are ~6750-6950,
            // which land lower in the next gear than this driver ever runs it. For those gears the
            // learner can't know the next gear's pull, so it must hold off (cue stays on the car's
            // light) rather than guess. Gears whose landing RPM is seen are learned.
            var results = TrainRealistic(runs: 60, shiftRpm: 7400, torque: CarSimulator.PeakyTorque)
                .Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);
            foreach (var r in results)
            {
                int expected = CarSimulator.OptimalShift(r.Gear, CarSimulator.PeakyTorque);
                _out.WriteLine($"realistic peaky gear {r.Gear}: {r.Rpm} ({r.Reason}, confident={r.Confident}, missing [{r.MissingBands}]), true {expected}");
                if (r.Confident)
                    Assert.InRange(r.Rpm, expected - ToleranceRpm, expected + ToleranceRpm);
                else
                    Assert.NotEqual("", r.MissingBands);
            }
            Assert.True(results.Count(r => r.Confident) >= 3, "upper gears should learn from corner exits alone");
        }

        [Fact]
        public void RealisticDriving_FindsEarlyShifts_ForPeakyEngine_WithVariedCornerExits()
        {
            // Same driver, but corner exits vary as they do on a real track (a hairpin drops
            // 2nd gear lower than a fast sweeper): still no low-RPM pulls, and every gear learns.
            var results = TrainRealistic(runs: 60, shiftRpm: 7400, torque: CarSimulator.PeakyTorque, exitSpreadRpm: 1000)
                .Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);
            foreach (var r in results)
            {
                int expected = CarSimulator.OptimalShift(r.Gear, CarSimulator.PeakyTorque);
                _out.WriteLine($"varied peaky gear {r.Gear}: {r.Rpm} ({r.Reason}, confident={r.Confident}, missing [{r.MissingBands}]), true {expected}");
                Assert.True(r.Confident, $"gear {r.Gear}: {r.Reason} (missing {r.MissingBands})");
                Assert.InRange(r.Rpm, expected - ToleranceRpm, expected + ToleranceRpm);
            }
        }

        [Fact]
        public void TorqueDip_DoesNotTriggerEarlyShift()
        {
            // Sanity: the dip really does create an early crossing that a first-crossing search
            // would stop at, for at least one gear.
            int g = 5;
            Assert.True(CarSimulator.FirstCrossing(g, CarSimulator.DipTorque) < 5500,
                "test curve should have an early (false) crossing");

            var results = Train(runs: 25, shiftAt: _ => CarSimulator.RedLine, torque: CarSimulator.DipTorque)
                .Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);
            foreach (var r in results)
            {
                int expected = CarSimulator.OptimalShift(r.Gear, CarSimulator.DipTorque);
                _out.WriteLine($"dip gear {r.Gear}: {r.Rpm} ({r.Reason}), true {expected}, first crossing {CarSimulator.FirstCrossing(r.Gear, CarSimulator.DipTorque)}");
                Assert.True(r.Confident, r.Reason);
                Assert.InRange(r.Rpm, expected - ToleranceRpm, expected + ToleranceRpm);
            }
        }

        [Fact]
        public void NoUpshiftForTopGear()
        {
            var learner = Train(runs: 3, shiftAt: _ => CarSimulator.RedLine);
            var results = learner.Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);
            Assert.DoesNotContain(results, r => r.Gear == CarSimulator.GearRatios.Length);
        }
    }
}
