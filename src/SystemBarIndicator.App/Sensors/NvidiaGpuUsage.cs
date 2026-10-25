using System;
using System.IO;
using System.Runtime.InteropServices;
using SystemBarIndicator.Monitoring;

namespace SystemBarIndicator.Sensors
{
    /// <summary>
    /// NVIDIA GPU busy percentage from NVML (the interface also used by nvidia-smi).
    /// Only binds when exactly one NVIDIA device is visible to NVML; indices from NVML
    /// and LibreHardwareMonitor/NVAPI are NOT interchangeable on multi-GPU systems.
    /// All DLL paths are absolute so the elevated application never loads a local
    /// nvml.dll from its working directory.
    /// </summary>
    internal sealed class NvidiaGpuUsage : IDisposable
    {
        private IntPtr _library;
        private IntPtr _device;
        private NvmlShutdown? _shutdown;
        private NvmlGetUtilization? _readUtilization;
        private bool _initialized;
        private bool _disabled;
        private bool _failureLogged;
        private bool _sourceLogged;
        private readonly GpuRetryBackoff _retry = new();

        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlUtilization
        {
            public uint Gpu;
            public uint Memory;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlInit();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlShutdown();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlGetCount(out uint count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlGetHandle(uint index, out IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlGetUtilization(IntPtr device, out NvmlUtilization utilization);

        public float? Read()
        {
            if (_disabled || !_retry.CanAttempt(DateTime.UtcNow))
                return null;

            if (_device == IntPtr.Zero && !TryOpen())
                return null;

            try
            {
                int status = _readUtilization!(_device, out var utilization);
                if (status != 0)
                {
                    RetryLater($"NVML utilization query failed (status {status})");
                    Close();
                    return null;
                }

                if (utilization.Gpu > 100)
                {
                    RetryLater($"NVML returned invalid GPU usage {utilization.Gpu}");
                    Close();
                    return null;
                }

                _retry.OnSuccess();
                if (!_sourceLogged)
                {
                    Logger.Info("GPU core usage source: NVIDIA NVML (one NVIDIA GPU detected)");
                    _sourceLogged = true;
                }
                else if (_failureLogged)
                {
                    Logger.Info("NVIDIA NVML GPU utilization recovered");
                }
                _failureLogged = false;
                return utilization.Gpu;
            }
            catch (Exception ex) when (ex is SEHException or AccessViolationException)
            {
                ReportFailure($"NVML utilization call failed ({ex.GetType().Name}); using LibreHardwareMonitor");
                Close();
                _disabled = true;
                return null;
            }
        }

        private bool TryOpen()
        {
            try
            {
                string system32 = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll");
                string nvsmi = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "NVIDIA Corporation", "NVSMI", "nvml.dll");

                if (!NativeLibrary.TryLoad(system32, out _library) &&
                    !NativeLibrary.TryLoad(nvsmi, out _library))
                {
                    Disable("NVML library not found in NVIDIA's documented Windows locations");
                    return false;
                }

                var init = Export<NvmlInit>("nvmlInit_v2");
                _shutdown = Export<NvmlShutdown>("nvmlShutdown");
                var countDevices = Export<NvmlGetCount>("nvmlDeviceGetCount_v2");
                var getHandle = Export<NvmlGetHandle>("nvmlDeviceGetHandleByIndex_v2");
                _readUtilization = Export<NvmlGetUtilization>("nvmlDeviceGetUtilizationRates");

                int status = init();
                if (status != 0)
                {
                    RetryLater($"NVML initialization failed (status {status})");
                    Close();
                    return false;
                }

                _initialized = true;
                status = countDevices(out uint count);
                if (status != 0)
                {
                    RetryLater($"NVML device enumeration failed (status {status})");
                    Close();
                    return false;
                }

                if (count != 1)
                {
                    Disable($"NVML found {count} NVIDIA GPUs; refusing an ambiguous device-index mapping");
                    Close();
                    return false;
                }

                status = getHandle(0, out _device);
                if (status != 0 || _device == IntPtr.Zero)
                {
                    RetryLater($"NVML device handle failed (status {status})");
                    Close();
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or
                                       EntryPointNotFoundException or
                                       BadImageFormatException)
            {
                Disable($"NVML unavailable ({ex.GetType().Name}); using LibreHardwareMonitor");
                Close();
                return false;
            }
        }

        private T Export<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

        // Fail over immediately; avoid unloading/reopening NVML at the 0.5-5s poll rate.
        // Log a failure once until the next valid NVML sample.
        private void RetryLater(string reason)
        {
            _retry.OnFailure(DateTime.UtcNow);
            ReportFailure($"{reason}; using LibreHardwareMonitor (NVML retry in 30 seconds)");
        }

        private void ReportFailure(string message)
        {
            if (_failureLogged)
                return;
            Logger.Warn(message);
            _failureLogged = true;
        }

        private void Disable(string message)
        {
            ReportFailure(message);
            _disabled = true;
        }

        private void Close()
        {
            if (_initialized)
            {
                try { _shutdown?.Invoke(); }
                catch (Exception ex) when (ex is SEHException or AccessViolationException)
                {
                    // A driver reset may invalidate the native session.
                }
            }

            _initialized = false;
            _device = IntPtr.Zero;
            _shutdown = null;
            _readUtilization = null;
            if (_library != IntPtr.Zero)
            {
                NativeLibrary.Free(_library);
                _library = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            _disabled = true;
            Close();
        }
    }
}
