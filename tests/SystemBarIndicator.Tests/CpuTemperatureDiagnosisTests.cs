using SystemBarIndicator.Monitoring;
using Xunit;

namespace SystemBarIndicator.Tests
{
    public class CpuTemperatureDiagnosisTests
    {
        [Fact]
        public void NotElevated_SaysSoEvenWhenPawnIoIsThere()
        {
            // The situation in issue #1: the driver was installed, the bar was not elevated.
            string text = CpuTemperatureDiagnosis.Explain(elevated: false, pawnIoInstalled: true);

            Assert.Contains("not running as administrator", text);
            Assert.DoesNotContain("not installed", text);
        }

        [Fact]
        public void MissingDriver_NamesPawnIo()
        {
            string text = CpuTemperatureDiagnosis.Explain(elevated: true, pawnIoInstalled: false);

            Assert.Contains("PawnIO", text);
            Assert.Contains("not installed", text);
            Assert.DoesNotContain("administrator", text);
        }

        [Fact]
        public void BothMissing_NamesBoth()
        {
            string text = CpuTemperatureDiagnosis.Explain(elevated: false, pawnIoInstalled: false);

            Assert.Contains("administrator", text);
            Assert.Contains("PawnIO", text);
        }

        [Fact]
        public void NothingMissing_BlamesTheSensorNotTheSetup()
        {
            string text = CpuTemperatureDiagnosis.Explain(elevated: true, pawnIoInstalled: true);

            Assert.Contains("no temperature sensor", text);
        }
    }
}
