using System;
using System.Diagnostics;

namespace SystemBarIndicator.Sensors
{
    /// <summary>
    /// Disk throughput across all physical disks, read from the <c>_Total</c> instance of the
    /// PhysicalDisk counters. Per-disk instances are deliberately not used: their names are
    /// renumbered whenever a drive comes or goes (a USB disk, a resume from sleep), and a
    /// counter bound to a vanished instance throws "Instance 'N' does not exist" — which used
    /// to switch the section off for the rest of the run. <c>_Total</c> survives that.
    /// </summary>
    public class DiskSensor : IDisposable
    {
        private const string Category = "PhysicalDisk";
        private const string TotalInstance = "_Total";

        private PerformanceCounter? _read;
        private PerformanceCounter? _write;

        /// <summary>
        /// False once the counters are known to be unusable — at construction or later.
        /// One-way: the bar hides the disk section on it, and a section that came and went
        /// as samples failed would make the layout jump.
        /// </summary>
        public bool Available { get; private set; }

        public DiskSensor()
        {
            try
            {
                _read = new PerformanceCounter(Category, "Disk Read Bytes/sec", TotalInstance);
                _write = new PerformanceCounter(Category, "Disk Write Bytes/sec", TotalInstance);

                // The first reading primes a rate counter and is always 0.
                _read.NextValue();
                _write.NextValue();
            }
            catch
            {
                // Whatever was built before the failure must still be released.
                DisposeCounters();
                throw;
            }

            Available = true;
        }

        public (float readMBps, float writeMBps) GetInfo()
        {
            if (!Available || _read == null || _write == null)
                return (0, 0);

            try
            {
                return (_read.NextValue() / 1048576f, _write.NextValue() / 1048576f);
            }
            catch (Exception ex) when (PerfCounters.IsExpectedFailure(ex))
            {
                // As with the CPU counter: letting this escape would abort the entire
                // polling tick every second instead of losing just the disk section.
                Logger.Warn($"Disk performance counters failed while running ({PerfCounters.Describe(ex)}); " +
                            "disk activity will not be shown");
                Available = false;
                DisposeCounters();
                return (0, 0);
            }
        }

        private void DisposeCounters()
        {
            _read?.Dispose();
            _write?.Dispose();
            _read = null;
            _write = null;
        }

        public void Dispose()
        {
            Available = false;
            DisposeCounters();
        }
    }
}
