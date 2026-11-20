using System;
using SystemBarIndicator.Updates;
using Xunit;

namespace SystemBarIndicator.Tests
{
    public class UpdateAttemptTests
    {
        private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        private static readonly Version V130 = new(1, 3, 0);
        private static readonly Version V131 = new(1, 3, 1);
        private static readonly Version V140 = new(1, 4, 0);

        [Fact]
        public void AnAttemptThatDidNotTake_SuppressesTheSameVersionForADay()
        {
            var attempt = new UpdateAttempt(V131, T0);

            Assert.True(attempt.Suppresses(V131, current: V130, T0.AddMinutes(1)));
            Assert.True(attempt.Suppresses(V131, current: V130, T0.AddHours(23)));
            Assert.False(attempt.Suppresses(V131, current: V130, T0.Add(UpdateAttempt.RetryAfter)));
        }

        [Fact]
        public void ANewerReleaseIsNotHeldBackByAFailedOne()
        {
            var attempt = new UpdateAttempt(V131, T0);

            Assert.False(attempt.Suppresses(V140, current: V130, T0.AddMinutes(1)));
        }

        [Fact]
        public void AnAttemptThatTook_SuppressesNothing()
        {
            var attempt = new UpdateAttempt(V131, T0);

            Assert.True(attempt.Succeeded(V131));
            Assert.True(attempt.Succeeded(V140));
            Assert.False(attempt.Suppresses(V131, current: V131, T0.AddMinutes(1)));
        }

        [Fact]
        public void AnAttemptThatDidNotTake_IsNotASuccess()
        {
            Assert.False(new UpdateAttempt(V131, T0).Succeeded(V130));
        }

        [Fact]
        public void ATagThatNeverMatchesTheAssembly_IsRetriedOnlyDaily()
        {
            // The release says 1.3.1 but the binary still reports 1.3.0: every install
            // "works", yet the app keeps finding 1.3.1 newer. The record breaks the loop.
            var attempt = new UpdateAttempt(V131, T0);

            Assert.True(attempt.Suppresses(V131, current: V130, T0.AddMinutes(2)));
        }

        [Fact]
        public void AClockSetBackwards_DoesNotSuppressForever()
        {
            var attempt = new UpdateAttempt(V131, T0);

            Assert.False(attempt.Suppresses(V131, current: V130, T0.AddDays(-3)));
        }

        [Fact]
        public void VersionsCompareRegardlessOfTrailingZeros()
        {
            var attempt = new UpdateAttempt(new Version(1, 3, 1, 0), T0);

            Assert.True(attempt.Suppresses(V131, current: new Version(1, 3, 0, 0), T0.AddMinutes(1)));
            Assert.True(attempt.Succeeded(new Version(1, 3, 1, 0)));
        }

        [Fact]
        public void FormatAndParse_RoundTrip()
        {
            var attempt = new UpdateAttempt(V131, T0);

            var parsed = UpdateAttempt.Parse(attempt.Format());

            Assert.NotNull(parsed);
            // Versions are kept normalised (1.3.1 → 1.3.1.0), as everywhere in the updater.
            Assert.Equal(UpdateVersion.Normalize(V131), parsed!.Version);
            Assert.Equal(T0, parsed.AttemptedUtc);
            Assert.Equal(DateTimeKind.Utc, parsed.AttemptedUtc.Kind);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("1.3.1")]
        [InlineData("1.3.1 not-a-date")]
        [InlineData("v1.3.1-beta 2026-11-20T12:00:00.0000000Z")]
        [InlineData("garbage 2026-11-20T12:00:00.0000000Z")]
        [InlineData("1.3.1 2026-11-20T12:00:00Z extra")]
        public void Parse_TreatsAnythingUnreadableAsNoRecord(string? text)
        {
            Assert.Null(UpdateAttempt.Parse(text));
        }
    }
}
