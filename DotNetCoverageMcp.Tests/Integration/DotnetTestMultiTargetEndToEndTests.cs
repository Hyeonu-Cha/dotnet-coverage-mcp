using System.Text.Json;
using DotNetCoverageMcp.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetCoverageMcp.Tests.Integration;

public sealed class SampleProjectMultiTargetFixture() : SampleProjectFixture(mtp: false, multiTarget: true);

/// <summary>
/// The fixture targeting net8.0 and net10.0: `dotnet test` runs it once per framework and each
/// run writes its own coverage report. The tools used to read whichever sorted first, a random
/// pick, so a method covered on one framework only came and went between runs.
/// </summary>
[Trait("Category", "EndToEnd")]
public class DotnetTestMultiTargetEndToEndTests : IClassFixture<SampleProjectMultiTargetFixture>
{
    private readonly SampleProjectMultiTargetFixture _fixture;
    private readonly CoverageTools _sut;

    public DotnetTestMultiTargetEndToEndTests(SampleProjectMultiTargetFixture fixture)
    {
        _fixture = fixture;
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

    [Fact]
    public async Task RunTestsWithCoverage_MergesTheReportOfEachTargetFramework()
    {
        var result = await _sut.RunTestsWithCoverage(_fixture.TestProjectPath);

        const string marker = "Cobertura XML at: ";
        result.Should().Contain(marker, because: "the run should succeed; got: {0}", result);
        result.Should().Contain("targets several frameworks").And.Contain("Failed: 0");
        var xmlPath = result[(result.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim();

        using var doc = JsonDocument.Parse(_sut.GetFileCoverage(xmlPath, "Calculator.cs"));
        var methods = doc.RootElement.GetProperty("classes")[0].GetProperty("methods").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("lineRate").GetDouble());
        methods["Add"].Should().Be(1.0, "both frameworks' tests call it");
        methods["Twice"].Should().BeGreaterThan(0, "only the net8.0 build has the test that calls it");
        // A path on this machine, not one relative to the report's <source>. (On macOS the
        // report has the temp folder's resolved /private/var form, so not compared to Root.)
        var sourceFile = doc.RootElement.GetProperty("sourceFile").GetString()!;
        Path.IsPathFullyQualified(sourceFile).Should().BeTrue(sourceFile);
        File.Exists(sourceFile).Should().BeTrue(sourceFile);
    }
}
