namespace SystemBarIndicator.Models
{
    public class SensorData
    {
        public float CpuUsage { get; set; }
        public float CpuTemperature { get; set; }
        public float GpuUsage { get; set; }
        public float GpuTemperature { get; set; }
        public float GpuVramTemperature { get; set; }
        public float GpuVramUsage { get; set; }
        public float GpuMemoryUsed { get; set; }
        public float GpuMemoryTotal { get; set; }
        public float RamUsage { get; set; }
        public long RamUsed { get; set; }
        public long RamTotal { get; set; }
        public float DiskRead { get; set; }
        public float DiskWrite { get; set; }
        /// <summary>False once the disk counters have proved unusable; see DiskSensor.</summary>
        public bool DiskAvailable { get; set; }
        public float NetDown { get; set; }
        public float NetUp { get; set; }
    }
}
