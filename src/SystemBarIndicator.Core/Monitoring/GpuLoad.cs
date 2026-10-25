namespace SystemBarIndicator.Monitoring
{
    /// <summary>Select a valid GPU utilization reading without treating zero as missing.</summary>
    public static class GpuLoad
    {
        public static float? Prefer(float? nvidiaNvml, float? hardwareMonitor)
        {
            if (Valid(nvidiaNvml))
                return nvidiaNvml;
            return Valid(hardwareMonitor) ? hardwareMonitor : null;
        }

        private static bool Valid(float? value) =>
            value.HasValue && float.IsFinite(value.Value) &&
            value.Value >= 0f && value.Value <= 100f;
    }
}
