using SystemBarIndicator.Monitoring;
using Xunit;

namespace SystemBarIndicator.Tests
{
    public class GpuLoadTests
    {
        [Fact]
        public void NvmlIsPreferredEvenWhenHardwareMonitorReportsZero()
        {
            Assert.Equal(73f, GpuLoad.Prefer(73f, 0f));
        }

        [Fact]
        public void ARealZeroFromNvmlIsNotMissing()
        {
            Assert.Equal(0f, GpuLoad.Prefer(0f, 73f));
        }

        [Fact]
        public void MissingNvmlFallsBackToHardwareMonitor()
        {
            Assert.Equal(42f, GpuLoad.Prefer(null, 42f));
        }

        [Fact]
        public void InvalidValuesCannotMasqueradeAsUsage()
        {
            Assert.Equal(55f, GpuLoad.Prefer(float.NaN, 55f));
            Assert.Equal(55f, GpuLoad.Prefer(101f, 55f));
            Assert.Null(GpuLoad.Prefer(-1f, float.PositiveInfinity));
            Assert.Null(GpuLoad.Prefer(null, null));
        }
    }
}
