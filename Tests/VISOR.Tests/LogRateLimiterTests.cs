using VISOR.Diagnostics;
using Xunit;

namespace VISOR.Tests
{
    public class LogRateLimiterTests
    {
        private TimeSpan _now;
        private LogRateLimiter NewLimiter() => new(TimeSpan.FromSeconds(1), () => _now);

        [Fact]
        public void FirstLine_PassesWithNothingSuppressed()
        {
            var limiter = NewLimiter();
            Assert.True(limiter.TryPass("timeout", out int suppressed));
            Assert.Equal(0, suppressed);
        }

        [Fact]
        public void Repeats_WithinInterval_AreHeldBackAndCounted()
        {
            var limiter = NewLimiter();
            Assert.True(limiter.TryPass("timeout", out _));

            // Every 30 ms for just under a second, as while iRacing sends nothing.
            for (int i = 1; i <= 33; i++)
            {
                _now = TimeSpan.FromMilliseconds(30 * i);
                Assert.False(limiter.TryPass("timeout", out _));
            }

            _now = TimeSpan.FromSeconds(1);
            Assert.True(limiter.TryPass("timeout", out int suppressed));
            Assert.Equal(33, suppressed);

            // ...and the count starts again from there.
            _now = TimeSpan.FromSeconds(2.5);
            Assert.True(limiter.TryPass("timeout", out suppressed));
            Assert.Equal(0, suppressed);
        }

        [Fact]
        public void DifferentKeys_AreLimitedSeparately()
        {
            var limiter = NewLimiter();
            Assert.True(limiter.TryPass("timeout", out _));
            Assert.True(limiter.TryPass("deserialize", out _));
            Assert.False(limiter.TryPass("timeout", out _));
        }
    }
}
