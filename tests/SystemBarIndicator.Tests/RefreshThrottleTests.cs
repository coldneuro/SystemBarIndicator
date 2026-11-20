using System;
using SystemBarIndicator.Util;
using Xunit;

namespace SystemBarIndicator.Tests
{
    public class RefreshThrottleTests
    {
        private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

        [Fact]
        public void TheFirstRefreshIsAlwaysDue()
        {
            Assert.True(new RefreshThrottle(S(5)).TryClaim(S(123)));
        }

        [Fact]
        public void RefreshesAtMostOncePerPeriod()
        {
            var throttle = new RefreshThrottle(S(5));
            Assert.True(throttle.TryClaim(S(0)));

            Assert.False(throttle.TryClaim(S(1)));
            Assert.False(throttle.TryClaim(S(4.999)));
            Assert.True(throttle.TryClaim(S(5)));
            Assert.False(throttle.TryClaim(S(9)));
            Assert.True(throttle.TryClaim(S(10)));
        }

        [Fact]
        public void ThePeriodCountsFromTheLastRefresh_NotFromAFixedGrid()
        {
            // A poll tick that lands late must not make the next refresh come early.
            var throttle = new RefreshThrottle(S(5));
            throttle.TryClaim(S(0));
            Assert.True(throttle.TryClaim(S(7)));

            Assert.False(throttle.TryClaim(S(10)));
            Assert.True(throttle.TryClaim(S(12)));
        }

        [Fact]
        public void Reset_MakesTheNextRefreshDue()
        {
            var throttle = new RefreshThrottle(S(5));
            throttle.TryClaim(S(0));

            throttle.Reset();

            Assert.True(throttle.TryClaim(S(1)));
            Assert.False(throttle.TryClaim(S(2)));
        }

        [Fact]
        public void AClockGoingBackwards_DoesNotPostponeTheRefreshForever()
        {
            var throttle = new RefreshThrottle(S(5));
            throttle.TryClaim(S(100));

            Assert.True(throttle.TryClaim(S(1)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ThePeriodMustBePositive(double seconds)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RefreshThrottle(S(seconds)));
        }
    }
}
