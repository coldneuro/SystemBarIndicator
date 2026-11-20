using System;
using System.Globalization;

namespace SystemBarIndicator.Updates
{
    /// <summary>
    /// The last automatic install the app handed to the swap script: which version, and when.
    /// The script restarts the app whatever happens, so without this record an update that
    /// cannot take — a locked file, or a release whose assembly version was never bumped to
    /// match its tag — would be downloaded and installed again on every start, a minute
    /// apart, forever. The running version tells whether the attempt took.
    /// </summary>
    public sealed record UpdateAttempt(Version Version, DateTime AttemptedUtc)
    {
        /// <summary>How long a version whose install did not take is left alone.</summary>
        public static readonly TimeSpan RetryAfter = TimeSpan.FromDays(1);

        /// <summary>
        /// True when the attempt is resolved — the app now runs that version or a newer
        /// one — and the record can be deleted.
        /// </summary>
        public bool Succeeded(Version current) =>
            !UpdateVersion.IsNewer(current, Version);

        /// <summary>
        /// True when an automatic install of <paramref name="candidate"/> should be skipped:
        /// the same version was attempted less than <see cref="RetryAfter"/> ago and the app
        /// still runs an older one. A newer release, or a day later, is tried normally.
        /// </summary>
        public bool Suppresses(Version candidate, Version current, DateTime nowUtc) =>
            UpdateVersion.Normalize(candidate) == UpdateVersion.Normalize(Version) &&
            !Succeeded(current) &&
            nowUtc >= AttemptedUtc &&
            nowUtc - AttemptedUtc < RetryAfter;

        /// <summary>The on-disk form: one line, "version timestamp" (ISO 8601, UTC).</summary>
        public string Format() =>
            $"{UpdateVersion.Normalize(Version).ToString(3)} " +
            AttemptedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        /// <summary>
        /// Reads <see cref="Format"/>'s output; null for anything else. A damaged record
        /// must not block updates, so unreadable means "no record".
        /// </summary>
        public static UpdateAttempt? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string[] parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                return null;

            var version = UpdateVersion.ParseTag(parts[0]);
            if (version == null)
                return null;

            if (!DateTime.TryParse(parts[1], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                return null;

            return new UpdateAttempt(version, when);
        }
    }
}
