using Virinco.WATS.Interface;
using Xunit;
using Xunit.Abstractions;
using WATS.Testing;

namespace Virinco.WATS.Converter.WATS.ExportWizard.Tests
{
    // One export file holds many UUT reports: ImportReport submits each one and returns null,
    // so the batch base class is used. SimulatedTDM captures the submits; nothing reaches a server.
    public class ConverterTests : TextConverterTestBase
    {
        public ConverterTests(ITestOutputHelper output) : base(output) { }
        protected override IReportConverter_v2 CreateConverter() => new ExportWizardConverter();

        [Fact, Trait("TestMode", "ConvertOnly")]
        public void ConvertOnly_AllFiles() => RunAllFiles(TestMode.ConvertOnly);

        [Fact, Trait("TestMode", "ConvertAndValidate")]
        public void ConvertAndValidate_AllFiles() => RunAllFiles(TestMode.ConvertAndValidate);

        [Fact, Trait("TestMode", "ConvertAndSimulate")]
        public void ConvertAndSimulate_AllFiles() => RunAllFiles(TestMode.ConvertAndSimulate);
    }
}
