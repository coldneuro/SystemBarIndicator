using System;
using SystemBarIndicator.Monitoring;
using Xunit;

namespace SystemBarIndicator.Tests
{
    public class GpuRetryBackoffTests
    {
        [Fact]
        public void FailureDefersPollingUntilThirtySecondsHaveElapsed()
        {
            var retry = new GpuRetryBackoff();
            var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(retry.CanAttempt(now));
            retry.OnFailure(now);
            Assert.False(retry.CanAttempt(now));
            Assert.False(retry.CanAttempt(now.AddSeconds(29)));
            Assert.True(retry.CanAttempt(now.Add(GpuRetryBackoff.Delay)));
        }

        [Fact]
        public void AnotherFailureRestartsCooldownAndSuccessClearsIt()
        {
            var retry = new GpuRetryBackoff();
            var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

            retry.OnFailure(now);
            retry.OnFailure(now.AddSeconds(30));
            Assert.False(retry.CanAttempt(now.AddSeconds(45)));
            retry.OnSuccess();
            Assert.True(retry.CanAttempt(now.AddSeconds(45)));
        }
    }
}
