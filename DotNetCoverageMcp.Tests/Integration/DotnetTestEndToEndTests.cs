using System.Globalization;
using System.Xml.Linq;
using DotNetCoverageMcp.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetCoverageMcp.Tests.Integration;

/// <summary>
/// Runs RunTestsWithCoverage against a real project with real `dotnet test`, so the
/// command line the server builds is exercised, not mocked. The fixture's tests are
/// CalculatorTests.Add_ReturnsSum (covers Add) and
/// CalculatorTests.Classify_Negative_ReturnsNegative (covers part of Classify), so the
/// per-method line rates in the coverage XML show exactly which tests ran.
/// </summary>
[Trait("Category", "EndToEnd")]
public class DotnetTestEndToEndTests : IClassFixture<SampleProjectFixture>
{
    private readonly SampleProjectFixture _fixture;
    private readonly CoverageTools _sut;

    public DotnetTestEndToEndTests(SampleProjectFixture fixture)
    {
        _fixture = fixture;

        // Allow every path. The real PathGuard reads COVERAGE_MCP_ALLOWED_ROOT, which
        // PathGuardTests sets while running in parallel with this class.
        var pathGuard = new Mock<IPathGuard>();
        pathGuard.Setup(p => p.IsWithinAllowedRoot(It.IsAny<string>())).Returns(true);

        var fileService = new FileService(NullLogger<FileService>.Instance);
        _sut = new CoverageTools(
            new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            new SessionManager(fileService, pathGuard.Object, NullLogger<SessionManager>.Instance),
            fileService,
            new CoberturaService(NullLogger<CoberturaService>.Instance),
            new CodeInserter(fileService, NullLogger<CodeInserter>.Instance),
            pathGuard.Object);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    public async Task RunTestsWithCoverage_RunAll_RunsEveryTest(string? filter)
    {
        var xml = await RunAndGetCoverageXml(filter);

        MethodLineRate(xml, "Add").Should().Be(1.0);
        MethodLineRate(xml, "Classify").Should().BeGreaterThan(0.0);
    }

    [Fact]
    public async Task RunTestsWithCoverage_ClassDotMethodFilter_RunsOnlyThatTest()
    {
        var xml = await RunAndGetCoverageXml("CalculatorTests.Add_ReturnsSum");

        MethodLineRate(xml, "Add").Should().Be(1.0);
        MethodLineRate(xml, "Classify").Should().Be(0.0);
    }

    [Fact]
    public async Task RunTestsWithCoverage_PipeFilter_RunsBothTests()
    {
        var xml = await RunAndGetCoverageXml("Add_ReturnsSum|Classify_Negative");

        MethodLineRate(xml, "Add").Should().Be(1.0);
        MethodLineRate(xml, "Classify").Should().BeGreaterThan(0.0);
    }

    private async Task<XDocument> RunAndGetCoverageXml(string? filter)
    {
        var result = await _sut.RunTestsWithCoverage(_fixture.TestProjectPath, filter, skipReport: true);

        const string marker = "Cobertura XML at: ";
        result.Should().Contain(marker, because: "the run should succeed; got: {0}", result);
        var path = result[(result.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim();
        return XDocument.Load(path);
    }

    private static double MethodLineRate(XDocument xml, string method) =>
        double.Parse(
            xml.Descendants("method").Single(m => m.Attribute("name")?.Value == method)
                .Attribute("line-rate")!.Value,
            CultureInfo.InvariantCulture);
}
