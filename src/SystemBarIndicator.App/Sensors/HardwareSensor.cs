using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Linq;
using SystemBarIndicator.Monitoring;

namespace SystemBarIndicator.Sensors
{
    public class HardwareSensor : IDisposable
    {
        private readonly Computer _computer;
        private readonly NvidiaGpuUsage? _nvidiaGpuUsage;

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

            // NVML is an independent NVIDIA GPU-busy counter, unlike LHM's NVAPI load.
            // Never assume NVML index 0 matches an NVAPI index if either detects two cards.
            if (GpuHardware().Count(h => h.HardwareType == HardwareType.GpuNvidia) == 1)
                _nvidiaGpuUsage = new NvidiaGpuUsage();
        }

        /// <summary>
        /// Refreshes every device's readings once. Must be called once per tick before
        /// reading the getters — LibreHardwareMonitor does not refresh values on its own.
        /// </summary>
        public void Update()
        {
            foreach (var hw in _computer.Hardware)
                hw.Update();
        }

        public float? GetCpuTemperature()
        {
            return GetFirstValue(_computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Cpu)
                .SelectMany(h => h.Sensors)
                .Where(s => s.SensorType == SensorType.Temperature &&
                            (s.Name.Contains("Core") || s.Name.Contains("Package") || s.Name.Contains("CPU"))));
        }

        public float? GetGpuUsage()
        {
            var loads = GpuSensors().Where(s => s.SensorType == SensorType.Load);

            // "GPU Core" is the overall load. The looser fallback stays for cards that name it
            // differently, but it has to skip the memory sensors: "GPU Memory" is how full the
            // VRAM is and "GPU Memory Controller" is bus utilisation — neither is the load of
            // the GPU itself, and nothing guarantees which of them is enumerated first.
            float? lhm = GetFirstValue(loads.Where(s => s.Name == "GPU Core"))
                ?? GetFirstValue(loads.Where(s => s.Name.Contains("GPU") && !s.Name.Contains("Memory")));
            return GpuLoad.Prefer(_nvidiaGpuUsage?.Read(), lhm);
        }

        public float? GetGpuTemperature()
        {
            var temps = GpuSensors().Where(s => s.SensorType == SensorType.Temperature);

            // Prefer the core sensor and never fall back to a memory one — on cards that
            // enumerate the VRAM sensor first this would duplicate GetGpuVramTemperature().
            return GetFirstValue(temps.Where(s => s.Name.Contains("Core")))
                ?? GetFirstValue(temps.Where(s => !s.Name.Contains("Memory")));
        }

        /// <summary>VRAM temperature: "GPU Memory Junction" on NVIDIA, "GPU Memory" on AMD.</summary>
        public float? GetGpuVramTemperature()
        {
            return GetFirstValue(GpuSensors()
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

        // Prefer the discrete NVIDIA device over an integrated AMD/Intel adapter when both
        // exist; every GPU metric uses the same precedence rather than mixing device order.
        private IEnumerable<IHardware> GpuHardware() => _computer.Hardware
            .Where(h => h.HardwareType == HardwareType.GpuNvidia ||
                        h.HardwareType == HardwareType.GpuAmd ||
                        h.HardwareType == HardwareType.GpuIntel)
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
            _nvidiaGpuUsage?.Dispose();
            _computer.Close();
        }
    }
}
