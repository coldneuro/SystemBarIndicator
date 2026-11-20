using System;

namespace SystemBarIndicator.Util
{
    /// <summary>
    /// Lets an expensive refresh run at most once per period. Used for sensors that cost far
    /// more than they change: the VRAM temperature can only be had through a full
    /// LibreHardwareMonitor pass over the GPU (tens of milliseconds of CPU), while the memory
    /// it measures heats and cools over seconds, not ticks.
    /// </summary>
    public sealed class RefreshThrottle
    {
        private readonly TimeSpan _period;
        private TimeSpan? _last;

        public RefreshThrottle(TimeSpan period)
        {
            if (period <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(period), period, "The period must be positive.");
            _period = period;
        }

        public TimeSpan Period => _period;

        /// <summary>
        /// True when a refresh is due at <paramref name="now"/> — the first call, or a full
        /// period after the last refresh — and records <paramref name="now"/> as that
        /// refresh. <paramref name="now"/> is a monotonic timestamp; should it ever go
        /// backwards, the refresh is treated as due rather than postponed indefinitely.
        /// </summary>
        public bool TryClaim(TimeSpan now)
        {
            if (_last.HasValue && now >= _last.Value && now - _last.Value < _period)
                return false;

            _last = now;
            return true;
        }

        /// <summary>Makes the next <see cref="TryClaim"/> succeed, whatever the time.</summary>
        public void Reset() => _last = null;
    }
}
