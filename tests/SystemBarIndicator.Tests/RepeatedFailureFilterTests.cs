using System;
using SystemBarIndicator.Util;
using Xunit;

namespace SystemBarIndicator.Tests
{
    public class RepeatedFailureFilterTests
    {
        [Fact]
        public void TheFirstFailureIsReported_RepeatsAreNot()
        {
            var filter = new RepeatedFailureFilter();

            Assert.True(filter.ShouldReport("gpu", "boom"));
            Assert.False(filter.ShouldReport("gpu", "boom"));
            Assert.False(filter.ShouldReport("gpu", "boom"));
        }

        [Fact]
        public void ADifferentFailureOfTheSameSourceIsReported()
        {
            var filter = new RepeatedFailureFilter();
            filter.ShouldReport("gpu", "boom");

            Assert.True(filter.ShouldReport("gpu", "bang"));
            Assert.False(filter.ShouldReport("gpu", "bang"));
        }

        [Fact]
        public void SourcesAreTrackedIndependently()
        {
            var filter = new RepeatedFailureFilter();
            filter.ShouldReport("gpu", "boom");

            Assert.True(filter.ShouldReport("ram", "boom"));
        }

        [Fact]
        public void RecoveryIsReportedOnce_AndReArmsTheNextFailure()
        {
            var filter = new RepeatedFailureFilter();
            filter.ShouldReport("gpu", "boom");

            Assert.True(filter.Recovered("gpu"));
            Assert.False(filter.Recovered("gpu"));
            Assert.True(filter.ShouldReport("gpu", "boom"));
        }

        [Fact]
        public void AHealthySourceHasNothingToRecoverFrom()
        {
            Assert.False(new RepeatedFailureFilter().Recovered("gpu"));
        }

        [Fact]
        public void SignatureOf_DistinguishesTypeAndMessage()
        {
            Assert.Equal(
                RepeatedFailureFilter.SignatureOf(new InvalidOperationException("a")),
                RepeatedFailureFilter.SignatureOf(new InvalidOperationException("a")));
            Assert.NotEqual(
                RepeatedFailureFilter.SignatureOf(new InvalidOperationException("a")),
                RepeatedFailureFilter.SignatureOf(new InvalidOperationException("b")));
            Assert.NotEqual(
                RepeatedFailureFilter.SignatureOf(new InvalidOperationException("a")),
                RepeatedFailureFilter.SignatureOf(new ArgumentException("a")));
        }
    }
}
