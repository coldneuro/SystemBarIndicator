using System;
using SystemBarIndicator.Monitoring;
using Xunit;

namespace SystemBarIndicator.Tests
{
    public class CpuTemperaturePickerTests
    {
        [Fact]
        public void Intel_PrefersThePackageWhateverTheOrder()
        {
            // Sensor names as LibreHardwareMonitor 0.9.6 reports them for an Intel CPU.
            var sensors = new (string, float?)[]
            {
                ("Core Max", 71f),
                ("Core Average", 55f),
                ("CPU Core #1", 52f),
                ("CPU Core #2", 71f),
                ("CPU Package", 68f),
                ("CPU Core #1 Distance to TjMax", 48f),
            };

            Assert.Equal(68f, CpuTemperaturePicker.Pick(sensors));
            Assert.Equal(68f, CpuTemperaturePicker.Pick(Reversed(sensors)));
        }

        [Fact]
        public void AmdZen_PrefersTheCombinedDieSensorOverTheCcds()
        {
            var sensors = new (string, float?)[]
            {
                ("CCD1 (Tdie)", 58f),
                ("CCDs Max (Tdie)", 58f),
                ("Core (Tctl/Tdie)", 63f),
            };

            Assert.Equal(63f, CpuTemperaturePicker.Pick(sensors));
        }

        [Fact]
        public void AmdZenWithOffset_ShowsTheDieTemperatureNotTheFanControlOne()
        {
            // On models with an offset, Tctl reads hotter than the die by design.
            var sensors = new (string, float?)[] { ("Core (Tctl)", 80f), ("Core (Tdie)", 60f) };

            Assert.Equal(60f, CpuTemperaturePicker.Pick(sensors));
        }

        [Fact]
        public void OlderAmd_UsesItsSingleSensor()
        {
            Assert.Equal(45f, CpuTemperaturePicker.Pick(new (string, float?)[] { ("CPU Cores", 45f) }));
        }

        [Fact]
        public void DistanceToTjMax_IsNeverMistakenForATemperature()
        {
            var sensors = new (string, float?)[]
            {
                ("CPU Core #1 Distance to TjMax", 48f),
                ("CPU Core #2 Distance to TjMax", 30f),
            };

            Assert.Null(CpuTemperaturePicker.Pick(sensors));
        }

        [Fact]
        public void PerCoreOnly_FallsBackToTheHottestCore()
        {
            var sensors = new (string, float?)[]
            {
                ("Core #1", 50f),
                ("Core #2", 64f),
                ("Core #3", 57f),
                ("Core #2 Distance to TjMax", 36f),
            };

            Assert.Equal(64f, CpuTemperaturePicker.Pick(sensors));
            Assert.Equal(64f, CpuTemperaturePicker.Pick(Reversed(sensors)));
        }

        [Fact]
        public void AnEmptyPreferredSensor_GivesWayToTheNextOne()
        {
            var sensors = new (string, float?)[] { ("CPU Package", null), ("Core Max", 70f) };

            Assert.Equal(70f, CpuTemperaturePicker.Pick(sensors));
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(0f)]
        [InlineData(-5f)]
        public void ImplausibleReadings_AreSkipped(float bogus)
        {
            var sensors = new (string, float?)[] { ("CPU Package", bogus), ("Core Max", 70f) };

            Assert.Equal(70f, CpuTemperaturePicker.Pick(sensors));
        }

        [Fact]
        public void NoReadings_GiveNull()
        {
            Assert.Null(CpuTemperaturePicker.Pick(Array.Empty<(string, float?)>()));
            Assert.Null(CpuTemperaturePicker.Pick(new (string, float?)[] { ("CPU Package", null) }));
        }

        [Fact]
        public void UnrelatedNames_AreIgnoredByTheFallback()
        {
            Assert.Null(CpuTemperaturePicker.Pick(new (string, float?)[] { ("Motherboard", 40f) }));
        }

        private static (string, float?)[] Reversed((string, float?)[] sensors)
        {
            var copy = ((string, float?)[])sensors.Clone();
            Array.Reverse(copy);
            return copy;
        }
    }
}
