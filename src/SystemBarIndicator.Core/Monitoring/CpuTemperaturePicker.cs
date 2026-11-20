using System;
using System.Collections.Generic;

namespace SystemBarIndicator.Monitoring
{
    /// <summary>
    /// Chooses the one CPU temperature the bar shows out of the many that
    /// LibreHardwareMonitor reports. A loose "name contains Core" match is not good enough:
    /// it also matches "CPU Core #1 Distance to TjMax" — a headroom figure, not a temperature
    /// — and LHM keeps a device's sensors in a hash set, so the enumeration order that would
    /// decide between the matches is not guaranteed.
    /// </summary>
    public static class CpuTemperaturePicker
    {
        /// <summary>
        /// Exact names, best first: the package sensor on Intel; the die temperature on AMD
        /// Zen ("Tctl/Tdie" where the two coincide, otherwise "Tdie" — "Tctl" carries a
        /// fan-control offset on some models); then the aggregates LHM offers when neither
        /// exists, and the single sensor of older AMD families.
        /// </summary>
        private static readonly string[] Preferred =
        {
            "CPU Package",
            "Core (Tctl/Tdie)",
            "Core (Tdie)",
            "Core (Tctl)",
            "Core Max",
            "CPU Cores",
        };

        /// <summary>
        /// The temperature to show, in °C, or null when no usable reading exists. Sensors
        /// without a value are skipped, so a preferred sensor that is momentarily empty
        /// gives way to the next one rather than blanking the section.
        /// </summary>
        public static float? Pick(IEnumerable<(string Name, float? Value)> sensors)
        {
            ArgumentNullException.ThrowIfNull(sensors);

            var usable = new List<(string Name, float Value)>();
            foreach (var (name, value) in sensors)
            {
                if (name != null && value.HasValue && IsPlausible(value.Value) && !IsHeadroom(name))
                    usable.Add((name, value.Value));
            }

            foreach (string preferred in Preferred)
            {
                foreach (var sensor in usable)
                {
                    if (sensor.Name == preferred)
                        return sensor.Value;
                }
            }

            // Unknown layouts (per-core sensors only, a future CPU family): the hottest
            // core-like reading, which is order-independent and errs on the side of warning.
            float? hottest = null;
            foreach (var sensor in usable)
            {
                if (LooksLikeCpuTemperature(sensor.Name) && (hottest == null || sensor.Value > hottest))
                    hottest = sensor.Value;
            }
            return hottest;
        }

        private static bool IsHeadroom(string name) =>
            name.Contains("Distance", StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikeCpuTemperature(string name) =>
            name.Contains("Core", StringComparison.Ordinal) ||
            name.Contains("Package", StringComparison.Ordinal) ||
            name.Contains("CPU", StringComparison.Ordinal);

        private static bool IsPlausible(float celsius) =>
            !float.IsNaN(celsius) && !float.IsInfinity(celsius) && celsius > 0f;
    }
}
