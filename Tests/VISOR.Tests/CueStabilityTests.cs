using VISOR.ViewModels;
using Xunit;

namespace VISOR.Tests
{
    public class CueStabilityTests
    {
        private const double Tick = 5;   // seconds of driving between observations

        private static CueStability Drive(int cue, bool fromLearning, double seconds)
        {
            var s = new CueStability();
            for (double t = 0; t <= seconds; t += Tick) s.Observe(cue, fromLearning, Tick);
            return s;
        }

        [Fact]
        public void Settles_AfterStableDriving()
        {
            Assert.False(Drive(7550, true, CueStability.SettleSeconds - 30).Settled);
            Assert.True(Drive(7550, true, CueStability.SettleSeconds + 10).Settled);
        }

        [Fact]
        public void Jitter_WithinBand_StillSettles()
        {
            var s = new CueStability();
            int[] cues = { 7500, 7560, 7480, 7580, 7520 };   // 1st gear in the 2-hour log
            for (int i = 0; i < 60; i++) s.Observe(cues[i % cues.Length], true, Tick);
            Assert.True(s.Settled);
        }

        [Fact]
        public void BigMove_RestartsTheClock()
        {
            var s = Drive(7200, true, CueStability.SettleSeconds + 10);
            Assert.True(s.Settled);
            s.Observe(7450, true, Tick);   // stepped up 250 RPM
            Assert.False(s.Settled);
        }

        [Fact]
        public void NotDriving_DoesNotSettle()
        {
            var s = new CueStability();
            for (int i = 0; i < 200; i++) s.Observe(7550, true, drivingSeconds: 0);   // sitting in the pits
            Assert.False(s.Settled);
        }

        [Fact]
        public void CarsOwnLight_NeverSettles()
        {
            Assert.False(Drive(7200, fromLearning: false, seconds: 3 * CueStability.SettleSeconds).Settled);

            // ...and falling back to the light un-settles a settled gear.
            var s = Drive(7550, true, CueStability.SettleSeconds + 10);
            s.Observe(7200, false, Tick);
            Assert.False(s.Settled);
        }
    }
}
