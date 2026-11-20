using System;
using System.Diagnostics;
using System.Threading;
using SystemBarIndicator.Models;
using SystemBarIndicator.Util;

namespace SystemBarIndicator.Sensors
{
    /// <summary>
    /// Owns the sensors and reads them on a dedicated background thread, handing each
    /// finished <see cref="SensorData"/> snapshot to a callback. Reading used to happen on the
    /// UI thread inside the WinForms timer, and the hardware pass alone held that thread for
    /// ~100 ms every second (LibreHardwareMonitor's CPU pass waits on per-core reads through
    /// the driver; the NVIDIA one waits on PCIe sampling) — so clicks, drags and the menu
    /// stalled once a second.
    /// <para>
    /// Threading contract: the sensors are touched by one thread at a time — the UI thread
    /// for construction and the synchronous <see cref="ReadOnce"/> before
    /// <see cref="Start"/>, then only the polling thread until <see cref="Dispose"/> has
    /// joined it. Each snapshot is a new object, never mutated after it is handed over.
    /// </para>
    /// </summary>
    internal sealed class SensorPoller : IDisposable
    {
        // The VRAM temperature costs a full LibreHardwareMonitor pass over an NVIDIA card,
        // while memory heats and cools over seconds; see HardwareSensor.
        private static readonly TimeSpan VramTemperaturePeriod = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(3);

        private readonly CpuSensor? _cpu;
        private readonly RamSensor _ram = new();
        private readonly DiskSensor? _disk;
        private readonly NetworkSensor? _network;
        private readonly HardwareSensor? _hardware;

        private readonly RepeatedFailureFilter _failures = new();
        private readonly RefreshThrottle _vramTemperature = new(VramTemperaturePeriod);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        // Wakes the thread early: to stop, or to pick up a changed interval at once.
        private readonly AutoResetEvent _wake = new(false);

        private Thread? _thread;
        private Action<SensorData>? _onReading;
        private volatile bool _stopping;
        private volatile int _intervalMs;
        private volatile bool _wantVramTemperature;
        // Set by the UI thread when the VRAM temperature is switched on, so it shows up on
        // the next tick instead of up to a period later.
        private int _vramTemperatureRequested;
        // The first read always includes the slow pass, so every sensor gets a chance to
        // report a value and the availability latches learn what exists — even for a
        // section that is currently turned off.
        private bool _firstRead = true;
        private bool _disposed;

        public SensorPoller(int intervalMs, bool wantVramTemperature)
        {
            _intervalMs = intervalMs;
            _wantVramTemperature = wantVramTemperature;

            try { _cpu = new CpuSensor(); }
            catch (Exception ex) { Logger.Error("CpuSensor.init", ex); }

            try { _disk = new DiskSensor(); }
            catch (Exception ex)
            {
                // Expected where the performance-counter service is disabled: the section is
                // hidden rather than drawn as permanently idle.
                if (PerfCounters.IsExpectedFailure(ex))
                    Logger.Warn($"disk performance counters unavailable ({PerfCounters.Describe(ex)}); " +
                                "disk activity will not be shown");
                else
                    Logger.Error("DiskSensor.init", ex);
            }

            try { _network = new NetworkSensor(); }
            catch (Exception ex) { Logger.Error("NetworkSensor.init", ex); }

            try { _hardware = new HardwareSensor(); }
            catch (Exception ex) { Logger.Error("HardwareSensor.init", ex); }
        }

        /// <summary>The poll period; takes effect immediately.</summary>
        public int IntervalMs
        {
            set
            {
                _intervalMs = value;
                _wake.Set();
            }
        }

        /// <summary>Whether the (expensive) VRAM temperature is wanted at all.</summary>
        public bool WantVramTemperature
        {
            set
            {
                if (value && !_wantVramTemperature)
                    Interlocked.Exchange(ref _vramTemperatureRequested, 1);
                _wantVramTemperature = value;
            }
        }

        /// <summary>
        /// One synchronous read on the calling thread, so the first frame has real data.
        /// Only valid before <see cref="Start"/>.
        /// </summary>
        public SensorData ReadOnce()
        {
            if (_thread != null)
                throw new InvalidOperationException("ReadOnce is only valid before Start.");
            return Read();
        }

        /// <summary>
        /// Starts polling. <paramref name="onReading"/> runs on the polling thread and must
        /// marshal to the UI itself.
        /// </summary>
        public void Start(Action<SensorData> onReading)
        {
            ArgumentNullException.ThrowIfNull(onReading);
            if (_thread != null)
                throw new InvalidOperationException("The poller is already running.");

            _onReading = onReading;
            _thread = new Thread(Run)
            {
                IsBackground = true, // never keeps the process alive on its own
                Name = "SensorPoller"
            };
            _thread.Start();
        }

        private void Run()
        {
            TimeSpan lastTick = _clock.Elapsed;
            while (!_stopping)
            {
                TimeSpan due = lastTick + TimeSpan.FromMilliseconds(_intervalMs);
                TimeSpan wait = due - _clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    // Woken early on a stop or an interval change: re-evaluate either way.
                    _wake.WaitOne(wait);
                    continue;
                }

                // Keep the cadence, but never try to catch up on ticks missed while the
                // machine was busy or asleep: that would fire a burst of reads.
                TimeSpan now = _clock.Elapsed;
                lastTick = now - due > TimeSpan.FromMilliseconds(_intervalMs) ? now : due;

                var reading = Read();
                try
                {
                    _onReading!(reading);
                }
                catch (Exception ex)
                {
                    Report("SensorPoller.publish", ex);
                }
            }
        }

        private SensorData Read()
        {
            var r = new SensorData();

            Sample("CpuSensor", () => r.CpuUsage = _cpu?.GetUsage() ?? 0f);

            if (_hardware != null)
            {
                bool slow = SlowPassDue();
                Sample("HardwareSensor", () =>
                {
                    _hardware.Update(includeVramTemperature: slow);
                    r.CpuTemperature = _hardware.GetCpuTemperature() ?? 0f;
                    r.GpuUsage = _hardware.GetGpuUsage() ?? 0f;
                    r.GpuTemperature = _hardware.GetGpuTemperature() ?? 0f;
                    r.GpuVramTemperature = _hardware.GetGpuVramTemperature() ?? 0f;
                    r.GpuVramUsage = _hardware.GetGpuVramUsage() ?? 0f;

                    var gpuMem = _hardware.GetGpuMemory();
                    if (gpuMem.HasValue)
                    {
                        r.GpuMemoryUsed = gpuMem.Value.used ?? 0f;
                        r.GpuMemoryTotal = gpuMem.Value.total ?? 0f;
                    }
                });
            }

            Sample("RamSensor", () =>
            {
                var ram = _ram.GetInfo();
                r.RamUsage = ram.usagePercent;
                r.RamUsed = ram.usedBytes;
                r.RamTotal = ram.totalBytes;
            });

            if (_disk != null)
            {
                Sample("DiskSensor", () =>
                {
                    var disk = _disk.GetInfo();
                    r.DiskRead = disk.readMBps;
                    r.DiskWrite = disk.writeMBps;
                });
            }
            // The sensor gives up permanently if its counters break mid-run.
            r.DiskAvailable = _disk?.Available == true;

            if (_network != null)
            {
                Sample("NetworkSensor", () =>
                {
                    var net = _network.GetInfo();
                    r.NetDown = net.downloadKBps;
                    r.NetUp = net.uploadKBps;
                });
            }

            return r;
        }

        private bool SlowPassDue()
        {
            if (_firstRead)
            {
                _firstRead = false;
                _vramTemperature.TryClaim(_clock.Elapsed);
                return true;
            }

            if (Interlocked.Exchange(ref _vramTemperatureRequested, 0) == 1)
                _vramTemperature.Reset();

            return _wantVramTemperature && _vramTemperature.TryClaim(_clock.Elapsed);
        }

        /// <summary>
        /// Runs one source's read in isolation: a source that throws (a GPU driver reset,
        /// say) leaves its fields at zero instead of taking the rest of the snapshot with it.
        /// Failures are logged when they start, change or stop, not on every tick.
        /// </summary>
        private void Sample(string source, Action read)
        {
            try
            {
                read();
                if (_failures.Recovered(source))
                    Logger.Warn($"{source} is reading again");
            }
            catch (Exception ex)
            {
                Report(source, ex);
            }
        }

        private void Report(string source, Exception ex)
        {
            if (_failures.ShouldReport(source, RepeatedFailureFilter.SignatureOf(ex)))
                Logger.Error(source, ex);
        }

        /// <summary>
        /// Stops the thread and releases the sensors. If the thread does not finish in time
        /// (a driver call that hangs), the sensors are left alone rather than disposed under
        /// its feet — the process is exiting anyway.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _stopping = true;
            _wake.Set();
            if (_thread != null && !_thread.Join(JoinTimeout))
            {
                Logger.Warn("the sensor thread did not stop in time; leaving the sensors open");
                return;
            }

            _cpu?.Dispose();
            _disk?.Dispose();
            _network?.Dispose();
            _hardware?.Dispose();
            _wake.Dispose();
        }
    }
}
