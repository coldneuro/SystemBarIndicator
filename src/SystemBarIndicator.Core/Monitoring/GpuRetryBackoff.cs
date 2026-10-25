using System;

namespace SystemBarIndicator.Monitoring
{
    /// <summary>
    /// Bounds retries of an unavailable GPU telemetry provider without delaying
    /// the regular hardware poll or making a failed provider permanently unavailable.
    /// The caller supplies UTC timestamps so retry boundaries can be tested.
    /// </summary>
    public sealed class GpuRetryBackoff
    {
        public static readonly TimeSpan Delay = TimeSpan.FromSeconds(30);

        private DateTime _retryAfterUtc = DateTime.MinValue;

        public bool CanAttempt(DateTime utcNow) => utcNow >= _retryAfterUtc;

        public void OnFailure(DateTime utcNow) => _retryAfterUtc = utcNow + Delay;

        public void OnSuccess() => _retryAfterUtc = DateTime.MinValue;
    }
}
