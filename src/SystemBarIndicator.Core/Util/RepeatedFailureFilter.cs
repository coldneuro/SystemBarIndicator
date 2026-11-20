using System;
using System.Collections.Generic;

namespace SystemBarIndicator.Util
{
    /// <summary>
    /// Decides which failures of a polled source are worth a log line. A sensor that breaks
    /// tends to break on every tick, and logging each one would write tens of thousands of
    /// identical lines a day; what matters is when it starts failing, when the failure
    /// changes, and when it recovers.
    /// </summary>
    public sealed class RepeatedFailureFilter
    {
        // Source → signature of the failure it is currently stuck in.
        private readonly Dictionary<string, string> _failing = new(StringComparer.Ordinal);

        /// <summary>
        /// Records a failure and returns true when it should be logged: the source was
        /// healthy until now, or it now fails in a different way.
        /// </summary>
        public bool ShouldReport(string source, string signature)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(signature);

            if (_failing.TryGetValue(source, out var current) && current == signature)
                return false;

            _failing[source] = signature;
            return true;
        }

        /// <summary>
        /// Records a success and returns true when the source had been failing, so the
        /// recovery can be logged once. A later failure is then reported afresh.
        /// </summary>
        public bool Recovered(string source)
        {
            ArgumentNullException.ThrowIfNull(source);
            return _failing.Remove(source);
        }

        /// <summary>A failure signature: same type and message means the same failure.</summary>
        public static string SignatureOf(Exception ex)
        {
            ArgumentNullException.ThrowIfNull(ex);
            return $"{ex.GetType().FullName}: {ex.Message}";
        }
    }
}
