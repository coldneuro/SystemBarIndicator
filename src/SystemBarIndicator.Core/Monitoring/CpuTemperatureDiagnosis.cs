namespace SystemBarIndicator.Monitoring
{
    /// <summary>
    /// Why the CPU temperature is missing, in words a user can act on. It needs two things
    /// that the app cannot arrange by itself — administrator rights and the PawnIO driver —
    /// and a bare "(n/a)" left people guessing which one was missing (issue #1: PawnIO had
    /// been installed by LibreHardwareMonitor itself, the bar was simply not elevated).
    /// </summary>
    public static class CpuTemperatureDiagnosis
    {
        public static string Explain(bool elevated, bool pawnIoInstalled) => (elevated, pawnIoInstalled) switch
        {
            (false, false) => "the app is not running as administrator, and the PawnIO driver (pawnio.eu) is not installed",
            (false, true) => "the app is not running as administrator — PawnIO is installed, but it needs elevation",
            (true, false) => "the PawnIO driver (pawnio.eu) is not installed",
            (true, true) => "the CPU reported no temperature sensor, although the app is elevated and PawnIO is installed",
        };
    }
}
