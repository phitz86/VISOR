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

        [Fact]
        public void NoUpshiftForTopGear()
        {
            var learner = Train(runs: 3, shiftAt: _ => CarSimulator.RedLine);
            var results = learner.Solve(CarSimulator.RedLine, CarSimulator.GearRatios.Length);
            Assert.DoesNotContain(results, r => r.Gear == CarSimulator.GearRatios.Length);
        }
    }
}
