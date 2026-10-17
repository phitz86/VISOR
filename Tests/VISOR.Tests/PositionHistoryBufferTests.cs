using VISOR.Telemetry;
using Xunit;

namespace VISOR.Tests
{
    public class PositionHistoryBufferTests
    {
        private const double Tick = 0.1;   // the buffer is fed at 10 Hz

        // Records a car moving at a constant pace (lap fraction per second) from a start point.
        private static PositionHistoryBuffer Driven(double startTime, float startPct, float pctPerSecond, int samples)
        {
            var buffer = new PositionHistoryBuffer();
            for (int i = 0; i < samples; i++)
            {
                float pct = (startPct + pctPerSecond * (float)(i * Tick)) % 1f;
                buffer.Record(startTime + i * Tick, pct);
            }
            return buffer;
        }

        [Fact]
        public void FindCrossingTime_InterpolatesBetweenSamples()
        {
            var buffer = new PositionHistoryBuffer();
            buffer.Record(10.0, 0.40f);
            buffer.Record(10.1, 0.42f);

            double? t = buffer.FindCrossingTime(0.41f);

            Assert.NotNull(t);
            Assert.Equal(10.05, t!.Value, 3);
        }

        [Fact]
        public void FindCrossingTime_HandlesTheStartFinishLine()
        {
            var buffer = new PositionHistoryBuffer();
            buffer.Record(20.0, 0.98f);
            buffer.Record(20.1, 0.02f);

            Assert.Equal(20.05, buffer.FindCrossingTime(0.0f)!.Value, 3);
            Assert.Equal(20.025, buffer.FindCrossingTime(0.99f)!.Value, 3);
            Assert.Equal(20.075, buffer.FindCrossingTime(0.01f)!.Value, 3);
        }

        [Fact]
        public void FindCrossingTime_ReturnsTheMostRecentCrossing()
        {
            // Two laps at 0.1 lap/s: 0.5 is passed at t = 5 s and again at t = 15 s.
            var buffer = Driven(startTime: 0, startPct: 0f, pctPerSecond: 0.1f, samples: 200);

            Assert.Equal(15.0, buffer.FindCrossingTime(0.5f)!.Value, 2);
        }

        [Fact]
        public void FindCrossingTime_SkipsTelemetryGaps()
        {
            var buffer = new PositionHistoryBuffer();
            buffer.Record(10.0, 0.40f);
            buffer.Record(10.5, 0.42f);   // 0.5 s gap: interpolating across it would be a guess

            Assert.Null(buffer.FindCrossingTime(0.41f));
        }

        [Fact]
        public void FindCrossingTime_NeedsTwoSamples()
        {
            var buffer = new PositionHistoryBuffer();
            Assert.Null(buffer.FindCrossingTime(0.5f));

            buffer.Record(1.0, 0.5f);
            Assert.Null(buffer.FindCrossingTime(0.5f));
        }

        [Fact]
        public void Record_TreatsAJumpAsATeleport_AndStartsOver()
        {
            var buffer = new PositionHistoryBuffer();
            buffer.Record(1.0, 0.30f);
            buffer.Record(1.1, 0.31f);

            bool kept = buffer.Record(1.2, 0.70f);   // tow or reset: a 0.39-lap jump in 0.1 s

            Assert.False(kept);
            Assert.Equal(0, buffer.Count);
        }

        [Fact]
        public void Record_StartFinishWrap_IsNotATeleport()
        {
            var buffer = new PositionHistoryBuffer();
            buffer.Record(1.0, 0.97f);

            Assert.True(buffer.Record(1.1, 0.01f));
            Assert.Equal(2, buffer.Count);
        }

        [Fact]
        public void Count_IsCappedAtFourMinutesOfHistory()
        {
            // 300 s at 0.01 lap/s: 0.5 is passed at 50, 150 and 250 s.
            var buffer = Driven(startTime: 0, startPct: 0f, pctPerSecond: 0.01f, samples: 3000);

            Assert.Equal(2400, buffer.Count);
            // After the ring has wrapped it still finds the most recent crossing.
            Assert.Equal(250.0, buffer.FindCrossingTime(0.5f)!.Value, 1);
        }

        [Fact]
        public void IsStationary_WhenBarelyMovingOverHalfASecond()
        {
            var buffer = new PositionHistoryBuffer();
            buffer.SetStationaryEpsilon(5000f);   // 5 km track: 3.35 m is 0.00067 of a lap
            for (int i = 0; i < 5; i++)
                buffer.Record(i * Tick, 0.5f + i * 0.0001f);

            Assert.True(buffer.IsStationary());
        }

        [Fact]
        public void IsNotStationary_WhenMoving_OrWithTooFewSamples()
        {
            var moving = Driven(startTime: 0, startPct: 0.2f, pctPerSecond: 0.01f, samples: 10);
            moving.SetStationaryEpsilon(5000f);
            Assert.False(moving.IsStationary());

            var fresh = new PositionHistoryBuffer();
            fresh.Record(0, 0.5f);
            Assert.False(fresh.IsStationary());
        }

        [Fact]
        public void IsStationary_AcrossTheStartFinishLine()
        {
            var buffer = new PositionHistoryBuffer();
            buffer.SetStationaryEpsilon(5000f);
            float[] pcts = { 0.99990f, 0.99995f, 0.0f, 0.00005f, 0.0001f };   // creeping over the line
            for (int i = 0; i < pcts.Length; i++)
                buffer.Record(i * Tick, pcts[i]);

            Assert.True(buffer.IsStationary());
        }
    }
}
