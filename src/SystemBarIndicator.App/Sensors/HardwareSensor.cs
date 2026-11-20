using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Linq;
using SystemBarIndicator.Monitoring;

namespace SystemBarIndicator.Sensors
{
    /// <summary>
    /// CPU and GPU readings. LibreHardwareMonitor supplies the CPU temperature and any
    /// non-NVIDIA GPU; an NVIDIA GPU is read through NVML instead (<see cref="NvmlGpu"/>),
    /// because LHM's NVIDIA pass is by far the most expensive thing the bar does. The one
    /// NVIDIA figure NVML cannot give — the VRAM ("GPU Memory Junction") temperature — still
    /// comes from that LHM pass, which then runs only when the caller asks for it.
    /// </summary>
    public class HardwareSensor : IDisposable
    {
        private readonly Computer _computer;
        private readonly NvmlGpu? _nvml;

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

            // Only when LHM found an NVIDIA card: NVML then answers for the same GPU that
            // LHM would have picked, since NVIDIA hardware is enumerated first.
            if (_computer.Hardware.Any(h => h.HardwareType == HardwareType.GpuNvidia))
                _nvml = NvmlGpu.TryOpen();
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

            if (_nvml != null)
            {
                _nvmlLoad = _nvml.Load;
                _nvmlTemperature = _nvml.Temperature;
                _nvmlMemory = _nvml.Memory;
            }
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

        private IEnumerable<IHardware> GpuHardware() => _computer.Hardware.Where(IsGpu);

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
