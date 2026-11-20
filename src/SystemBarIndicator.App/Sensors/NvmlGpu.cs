using System;
using System.Runtime.InteropServices;

namespace SystemBarIndicator.Sensors
{
    /// <summary>
    /// The handful of NVIDIA readings the bar shows, straight from NVML (nvml.dll, shipped
    /// with the driver). LibreHardwareMonitor's own NVIDIA pass refreshes everything it
    /// knows about the card — clocks, fans, power topology, voltages, D3D statistics and two
    /// PCIe throughput samples that block for ~20 ms each — which measured ~85 ms of wall
    /// time and ~35 ms of CPU per tick. The load, core temperature and memory figures below
    /// cost well under a millisecond together.
    /// </summary>
    internal sealed class NvmlGpu : IDisposable
    {
        private const int Success = 0;
        private const int TemperatureGpu = 0; // NVML_TEMPERATURE_GPU

        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlUtilization
        {
            public uint Gpu;
            public uint Memory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlMemory
        {
            public ulong Total;
            public ulong Free;
            public ulong Used;
        }

        // System32 only: the process runs elevated, and a DLL looked up next to the exe or
        // in the current directory could be planted there.
        [DllImport("nvml.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int nvmlInit_v2();

        [DllImport("nvml.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int nvmlShutdown();

        [DllImport("nvml.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport("nvml.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

        [DllImport("nvml.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint celsius);

        [DllImport("nvml.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

        private readonly IntPtr _device;
        private bool _disposed;

        private NvmlGpu(IntPtr device)
        {
            _device = device;
        }

        /// <summary>
        /// The first NVIDIA GPU through NVML, or null when NVML is not there (no NVIDIA
        /// driver, an old driver that keeps nvml.dll elsewhere) or reports no device — the
        /// caller then stays on LibreHardwareMonitor for the GPU.
        /// </summary>
        public static NvmlGpu? TryOpen()
        {
            try
            {
                if (nvmlInit_v2() != Success)
                    return null;

                if (nvmlDeviceGetHandleByIndex_v2(0, out var device) != Success)
                {
                    nvmlShutdown();
                    return null;
                }

                return new NvmlGpu(device);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return null;
            }
        }

        /// <summary>GPU load in percent, or null if the driver did not answer.</summary>
        public float? Load =>
            nvmlDeviceGetUtilizationRates(_device, out var u) == Success ? u.Gpu : null;

        /// <summary>GPU core temperature in °C, or null if the driver did not answer.</summary>
        public float? Temperature =>
            nvmlDeviceGetTemperature(_device, TemperatureGpu, out uint c) == Success ? c : null;

        /// <summary>Video memory used and total, in megabytes (the unit LHM reports too).</summary>
        public (float? used, float? total) Memory
        {
            get
            {
                if (nvmlDeviceGetMemoryInfo(_device, out var m) != Success || m.Total == 0)
                    return (null, null);
                return (m.Used / 1048576f, m.Total / 1048576f);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            nvmlShutdown();
        }
    }
}
