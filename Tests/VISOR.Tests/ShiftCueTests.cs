using VISOR.ViewModels;
using Xunit;

namespace VISOR.Tests
{
    public class ShiftCueTests
    {
        private const float RedLine = 7500;
        private const int Shift = 7000;
        private const int Approach = 6500;
        private const double Dt = 1.0 / 60.0;

        // Climbs RPM at a steady rate in one gear, returning the RPM at which ShiftNow first shows.
        private static double FirstShiftNowRpm(double rpmPerSecond, double startRpm = 5000)
        {
            var cue = new ShiftCue();
            double t = 100, rpm = startRpm;
            while (rpm < RedLine)
            {
                var state = cue.Update(t, 2, (float)rpm, active: true, revLimiterActive: false, RedLine, Shift, Approach);
                if (state == ShiftState.ShiftNow) return rpm;
                t += Dt;
                rpm += rpmPerSecond * Dt;
            }
            return double.NaN;
        }

        [Fact]
        public void LeadsShiftPoint_ByReactionTime_WhenRpmClimbsFast()
        {
            // 3000 RPM/s (a low gear): cue should come ~0.15 s * 3000 = ~450 RPM early.
            double at = FirstShiftNowRpm(3000);
            Assert.InRange(at, Shift - 3000 * ShiftCue.ReactionLeadSeconds - 60, Shift - 3000 * ShiftCue.ReactionLeadSeconds + 60);
        }

        [Fact]
        public void BarelyLeads_WhenRpmClimbsSlowly()
        {
            // 200 RPM/s (top-gear-like): ~30 RPM early at most.
            double at = FirstShiftNowRpm(200);
            Assert.InRange(at, Shift - 40, Shift);
        }

        [Fact]
        public void NoLead_AtSteadyRpm()
        {
            var cue = new ShiftCue();
            for (int i = 0; i < 120; i++)
                Assert.NotEqual(ShiftState.ShiftNow, cue.Update(100 + i * Dt, 3, Shift - 20, true, false, RedLine, Shift, Approach));
        }

        [Fact]
        public void GearChange_ResetsRate()
        {
            var cue = new ShiftCue();
            double t = 100;
            for (int i = 0; i < 60; i++, t += Dt)
                cue.Update(t, 2, (float)(5000 + i * 50), true, false, RedLine, Shift, Approach);
            Assert.True(cue.RpmRate > 1000);

            cue.Update(t, 3, 5200, true, false, RedLine, Shift, Approach);
            Assert.Equal(0, cue.RpmRate);
        }

        [Fact]
        public void StaleFrame_IsIgnored()
        {
            var cue = new ShiftCue();
            cue.Update(100.0, 2, 6000, true, false, RedLine, Shift, Approach);
            cue.Update(100.1, 2, 6100, true, false, RedLine, Shift, Approach);
            double rate = cue.RpmRate;

            // An older frame arriving late must not disturb the rate or the state.
            var state = cue.Update(100.05, 2, 9000, true, false, RedLine, Shift, Approach);
            Assert.Equal(rate, cue.RpmRate);
            Assert.NotEqual(ShiftState.Limiter, state);
        }

        [Fact]
        public void LimiterTakesPriority_AndUsesActualRpm()
        {
            var cue = new ShiftCue();
            Assert.Equal(ShiftState.Limiter, cue.Update(100, 2, RedLine, true, false, RedLine, Shift, Approach));
            Assert.Equal(ShiftState.Limiter, cue.Update(101, 2, 5000, true, revLimiterActive: true, RedLine, Shift, Approach));
        }

        [Fact]
        public void TopGear_OnlyShowsLimiter()
        {
            var cue = new ShiftCue();
            Assert.Equal(ShiftState.Normal, cue.Update(100, 6, 7200, true, false, RedLine, shiftRpm: 0, approachRpm: 0));
            Assert.Equal(ShiftState.Limiter, cue.Update(100.1, 6, RedLine, true, false, RedLine, 0, 0));
        }

        [Fact]
        public void Hysteresis_HoldsState_ThenReleases()
        {
            var cue = new ShiftCue();
            double t = 100;
            Assert.Equal(ShiftState.ShiftNow, cue.Update(t += 1, 2, Shift, true, false, RedLine, Shift, Approach));
            // RPM dips a little: still ShiftNow (inside hysteresis band). Big frame gaps keep the rate at 0.
            Assert.Equal(ShiftState.ShiftNow, cue.Update(t += 1, 2, Shift - 100, true, false, RedLine, Shift, Approach));
            // Dips past the band: falls back to amber.
            Assert.Equal(ShiftState.Approach, cue.Update(t += 1, 2, Shift - 200, true, false, RedLine, Shift, Approach));
        }

        [Fact]
        public void Inactive_IsAlwaysNormal()
        {
            var cue = new ShiftCue();
            Assert.Equal(ShiftState.Normal, cue.Update(100, 2, RedLine, active: false, false, RedLine, Shift, Approach));
        }
    }
}
