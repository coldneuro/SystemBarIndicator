using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SystemBarIndicator.Monitoring;
using SystemBarIndicator.Util;

namespace SystemBarIndicator.Sensors
{
    /// <summary>
    /// CPU and GPU readings. LibreHardwareMonitor supplies the CPU temperature and any
    /// non-NVIDIA GPU; an NVIDIA GPU is read through NVML instead (<see cref="NvmlGpu"/>),
    /// because LHM's NVIDIA pass is by far the most expensive thing the bar does — and on some
    /// cards (an RTX 3090 report) its "GPU Core" load stays at 0 % while NVML reads it fine.
    /// The one NVIDIA figure NVML cannot give — the VRAM ("GPU Memory Junction") temperature —
    /// still comes from that LHM pass, which then runs only when the caller asks for it.
    /// NVML is used only with exactly one NVIDIA GPU; if it stops answering mid-run (a driver
    /// update), the GPU falls back to LHM and NVML is retried every 30 s.
    /// </summary>
    public class HardwareSensor : IDisposable
    {
        private static readonly TimeSpan NvmlRetryPeriod = TimeSpan.FromSeconds(30);

        private readonly Computer _computer;
        private NvmlGpu? _nvml;
        // False once NVML is known to be unusable for the whole run (see NvmlGpu.OpenResult).
        private bool _nvmlRetryable;
        private readonly RefreshThrottle _nvmlRetry = new(NvmlRetryPeriod);
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        // NVML readings of the current tick.
        private float? _nvmlLoad;
        private float? _nvmlTemperature;
        private (float? used, float? total) _nvmlMemory;

        public HardwareSensor()
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = false,
                IsControllerEnabled = false,
                IsNetworkEnabled = false,
                IsStorageEnabled = false
            };
            _computer.Open();
            try
            {
                InitGpuSource();
            }
            catch
            {
                // Close what Open() started (its drivers and handles) before failing.
                _nvml?.Dispose();
                _computer.Close();
                throw;
            }
        }

        private void InitGpuSource()
        {
            // NVML's device index is only known to be the card LHM reports when there is a
            // single NVIDIA card; with two, the readings could come from different GPUs.
            int nvidia = _computer.Hardware.Count(h => h.HardwareType == HardwareType.GpuNvidia);
            if (nvidia == 1)
                OpenNvml(initial: true);
            else if (nvidia > 1)
                Logger.Info($"GPU readings: LibreHardwareMonitor ({nvidia} NVIDIA GPUs, NVML not used)");
        }

        private void OpenNvml(bool initial)
        {
            switch (NvmlGpu.TryOpen(out var gpu, out string reason))
            {
                case NvmlGpu.OpenResult.Opened:
                    _nvml = gpu;
                    _nvmlRetryable = true;
                    Logger.Info(initial
                        ? "GPU readings: NVIDIA through NVML"
                        : "NVML is answering again; GPU readings are back on NVML");
                    break;

                case NvmlGpu.OpenResult.Failed:
                    // Retried quietly; only the first failure and the recovery are logged.
                    _nvmlRetryable = true;
                    _nvmlRetry.TryClaim(_clock.Elapsed);
                    if (initial)
                        Logger.Warn($"GPU readings: LibreHardwareMonitor for now — {reason}; retrying NVML every {NvmlRetryPeriod.TotalSeconds:F0} s");
                    break;

                default:
                    _nvmlRetryable = false;
                    Logger.Info($"GPU readings: LibreHardwareMonitor ({reason})");
                    break;
            }
        }

        /// <summary>
        /// Refreshes the readings once. Must be called once per tick before reading the
        /// getters — LibreHardwareMonitor does not refresh values on its own.
        /// <paramref name="includeVramTemperature"/> adds LHM's full NVIDIA pass, the only
        /// source of the VRAM temperature on NVIDIA cards; between such passes that reading
        /// keeps its last value. Without NVML every device is refreshed on every call.
        /// </summary>
        public void Update(bool includeVramTemperature)
        {
            if (_nvml == null && _nvmlRetryable && _nvmlRetry.TryClaim(_clock.Elapsed))
                OpenNvml(initial: false);

            foreach (var hw in _computer.Hardware)
            {
                if (_nvml != null && IsGpu(hw))
                {
                    // NVML answers for the GPU; other GPUs (an iGPU next to the NVIDIA card)
                    // are not shown in that case and need no refresh at all.
                    if (hw.HardwareType == HardwareType.GpuNvidia && includeVramTemperature)
                        hw.Update();
                    continue;
                }
                hw.Update();
            }

            if (_nvml == null)
                return;

            _nvmlLoad = _nvml.Load;
            if (!_nvmlLoad.HasValue)
            {
                // NVML stopped answering (a driver update or reset invalidates the session):
                // fall back to LHM until a retry succeeds, starting with this very tick.
                Logger.Warn($"NVML stopped answering; GPU readings fall back to LibreHardwareMonitor, retrying NVML every {NvmlRetryPeriod.TotalSeconds:F0} s");
                _nvml.Dispose();
                _nvml = null;
                _nvmlRetry.Reset();
                _nvmlRetry.TryClaim(_clock.Elapsed);
                foreach (var hw in GpuHardware())
                    hw.Update();
                return;
            }

            _nvmlTemperature = _nvml.Temperature;
            _nvmlMemory = _nvml.Memory;
        }

        public float? GetCpuTemperature()
        {
            // The choice between "CPU Package", "Core (Tctl/Tdie)", per-core sensors and the
            // "Distance to TjMax" look-alikes lives in Core, where it is tested.
            return CpuTemperaturePicker.Pick(_computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Cpu)
                .SelectMany(h => h.Sensors)
                .Where(s => s.SensorType == SensorType.Temperature)
                .Select(s => (s.Name, s.Value)));
        }

        public float? GetGpuUsage()
        {
            if (_nvml != null)
                return _nvmlLoad;

            var loads = GpuSensors().Where(s => s.SensorType == SensorType.Load);

            // "GPU Core" is the overall load. The looser fallback stays for cards that name it
            // differently, but it has to skip the memory sensors: "GPU Memory" is how full the
            // VRAM is and "GPU Memory Controller" is bus utilisation — neither is the load of
            // the GPU itself, and nothing guarantees which of them is enumerated first.
            return GetFirstValue(loads.Where(s => s.Name == "GPU Core"))
                ?? GetFirstValue(loads.Where(s => s.Name.Contains("GPU") && !s.Name.Contains("Memory")));
        }

        public float? GetGpuTemperature()
        {
            if (_nvml != null)
                return _nvmlTemperature;

            var temps = GpuSensors().Where(s => s.SensorType == SensorType.Temperature);

            // Prefer the core sensor and never fall back to a memory one — on cards that
            // enumerate the VRAM sensor first this would duplicate GetGpuVramTemperature().
            return GetFirstValue(temps.Where(s => s.Name.Contains("Core")))
                ?? GetFirstValue(temps.Where(s => !s.Name.Contains("Memory")));
        }

        /// <summary>VRAM temperature: "GPU Memory Junction" on NVIDIA, "GPU Memory" on AMD.</summary>
        public float? GetGpuVramTemperature()
        {
            // With NVML in use the other GPUs are never refreshed, so their sensors hold
            // whatever the first read left there: only the NVIDIA card may answer.
            var gpus = _nvml != null
                ? GpuHardware().Where(h => h.HardwareType == HardwareType.GpuNvidia)
                : GpuHardware();
            return GetFirstValue(gpus.SelectMany(h => h.Sensors)
                .Where(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Memory")));
        }

        /// <summary>
        /// How full the video memory is, in percent. NVIDIA reports it ready-made as a
        /// "GPU Memory" load sensor; elsewhere it is derived from the used/total pair. The
        /// name is compared exactly on purpose — "GPU Memory Controller" is the utilisation
        /// of the memory bus, a different number that happens to sit under a similar name.
        /// </summary>
        public float? GetGpuVramUsage()
        {
            if (_nvml != null)
                return VramLoad.Percent(_nvmlMemory.used, _nvmlMemory.total);

            float? direct = GetFirstValue(GpuSensors()
                .Where(s => s.SensorType == SensorType.Load && s.Name == "GPU Memory"));
            if (direct.HasValue)
                return direct;

            var memory = GetGpuMemory();
            return memory.HasValue ? VramLoad.Percent(memory.Value.used, memory.Value.total) : null;
        }

        /// <summary>
        /// Video memory in megabytes. Names are compared exactly: a Contains("D3D") test also
        /// matches "D3D Shared Memory Used" — system RAM lent to the GPU rather than VRAM —
        /// and since the enumeration order is not guaranteed, the wrong one could win. The
        /// dedicated D3D counter is only a fallback for cards without "GPU Memory Used".
        /// Both halves of the pair come from the same card.
        /// </summary>
        public (float? used, float? total)? GetGpuMemory()
        {
            if (_nvml != null)
                return _nvmlMemory.used.HasValue || _nvmlMemory.total.HasValue ? _nvmlMemory : null;

            foreach (var gpu in GpuHardware())
            {
                var data = gpu.Sensors.Where(s => s.SensorType == SensorType.SmallData).ToList();

                float? used = GetFirstValue(data.Where(s => s.Name == "GPU Memory Used"))
                           ?? GetFirstValue(data.Where(s => s.Name == "D3D Dedicated Memory Used"));
                float? total = GetFirstValue(data.Where(s => s.Name == "GPU Memory Total"));

                if (used.HasValue || total.HasValue)
                    return (used, total);
            }
            return null;
        }

        private static bool IsGpu(IHardware h) =>
            h.HardwareType == HardwareType.GpuNvidia ||
            h.HardwareType == HardwareType.GpuAmd ||
            h.HardwareType == HardwareType.GpuIntel;

        // NVIDIA first: LHM lists AMD GPUs before NVIDIA ones, so on a Ryzen with integrated
        // graphics next to an NVIDIA card the first-with-a-value pickers below would read the
        // iGPU. Every GPU metric uses this one order, so they never mix devices.
        private IEnumerable<IHardware> GpuHardware() => _computer.Hardware
            .Where(IsGpu)
            .OrderByDescending(h => h.HardwareType == HardwareType.GpuNvidia);

        private IEnumerable<ISensor> GpuSensors() => GpuHardware().SelectMany(h => h.Sensors);

        private static float? GetFirstValue(IEnumerable<ISensor> sensors)
        {
            foreach (var sensor in sensors)
            {
                if (sensor.Value.HasValue)
                    return sensor.Value;
            }
            return null;
        }

        public void Dispose()
        {
            _nvml?.Dispose();
            _computer.Close();
        }
    }
}
