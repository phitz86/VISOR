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
            Func<double, double>? torque = null)
        {
            var learner = new ShiftPointLearner();
            var sim = new CarSimulator(seed, torque: torque);
            for (int i = 0; i < runs; i++)
                foreach (var s in sim.Run(shiftAt, wheelspin))
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
        public void RejectsIneligibleAndTransientFrames()
        {
            var learner = Train(runs: 2, shiftAt: _ => CarSimulator.RedLine);
            long before = learner.SampleCount;
            double t = 10_000;
            double k = CarSimulator.K(3);

            // Establish gear 3 well past the post-shift settle time.
            for (int i = 0; i < 120; i++, t += CarSimulator.Dt)
                learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 1, 0, 1, Eligible: true));
            long settled = learner.SampleCount;
            Assert.True(settled > before);

            Assert.False(learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 1, 0, 1, Eligible: false)));
            Assert.False(learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 0.5f, 0, 1, true)));     // part throttle
            Assert.False(learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 1, 0.5f, 1, true)));     // braking
            Assert.False(learner.AddSample(new ShiftSample(t, 3, (float)(k * 40), 40, 3f, 1, 0, 0.5f, true)));     // clutch slipping
            Assert.False(learner.AddSample(new ShiftSample(t, 3, (float)(k * 40 * 1.1), 40, 3f, 1, 0, 1, true)));  // wheelspin
            Assert.False(learner.AddSample(new ShiftSample(t, 4, (float)(CarSimulator.K(4) * 40), 40, 3f, 1, 0, 1, true))); // just shifted
            Assert.False(learner.AddSample(new ShiftSample(t, 0, 3000, 40, 0f, 1, 0, 1, true)));                   // neutral
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
