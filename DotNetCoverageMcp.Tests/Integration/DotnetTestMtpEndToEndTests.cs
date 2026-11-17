using System.Text.Json;
using DotNetCoverageMcp.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetCoverageMcp.Tests.Integration;

public sealed class SampleProjectMtpFixture() : SampleProjectFixture(mtp: true);

/// <summary>
/// The same fixture on Microsoft.Testing.Platform: xUnit v3 with Microsoft's coverage and
/// TRX extensions, and a global.json that puts `dotnet test` in MTP mode. Exercises the MTP
/// command line and Microsoft's Cobertura dialect (absolute paths, '.'-nested generated types).
/// </summary>
[Trait("Category", "EndToEnd")]
public class DotnetTestMtpEndToEndTests : IClassFixture<SampleProjectMtpFixture>
{
    private readonly SampleProjectMtpFixture _fixture;
    private readonly CoverageTools _sut;

    public DotnetTestMtpEndToEndTests(SampleProjectMtpFixture fixture)
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

    private async Task<string> Run(string? filter = null)
    {
        var result = await _sut.RunTestsWithCoverage(_fixture.TestProjectPath, filter, skipReport: true);
        const string marker = "Cobertura XML at: ";
        result.Should().Contain(marker, because: "the run should succeed; got: {0}", result);
        result.Should().Contain("Passed: ");
        return result[(result.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim();
    }

    [Fact]
    public async Task RunAll_CollectsMicrosoftCoverage_AndFoldsItsGeneratedTypes()
    {
        var xml = await Run();

        Path.GetFileName(xml).Should().EndWith(".cobertura.xml");
        using var file = JsonDocument.Parse(_sut.GetFileCoverage(xml, "Calculator.cs"));
        var classes = file.RootElement.GetProperty("classes");
        classes.GetArrayLength().Should().Be(1, because: "Calculator.<AddAsync>d__2 etc. are folded; got {0}", file.RootElement);
        classes[0].GetProperty("class").GetString().Should().Be("SampleLib.Calculator");
        var methods = classes[0].GetProperty("methods").EnumerateArray().Select(m => m.GetProperty("name").GetString()!).ToList();
        methods.Should().Contain(["AddAsync", "Twice", "CountPositive", "SumAbove"]);
        methods.Should().NotContain(m => m.Contains('<') || m == "MoveNext");
    }

    [Fact]
    public async Task ReportsProgressAndCountsFromMtpOutput()
    {
        // Calls the runner directly so a failure can show the raw console output, whose
        // shape differs between SDK versions.
        var messages = new List<string>();
        var resultsDir = Path.Combine(_fixture.Root, $"TestResults-progress-{Guid.NewGuid():N}");
        var result = await new ProcessRunner(NullLogger<ProcessRunner>.Instance).RunDotnetTestAsync(
            _fixture.TestProjectPath, null, resultsDir, Path.GetDirectoryName(_fixture.TestProjectPath)!,
            forceRestore: false, includeClass: null, progress: new SyncStringProgress(m => { lock (messages) messages.Add(m); }),
            CancellationToken.None);

        var raw = $"messages: {string.Join(" | ", messages)}\n--- stdout ---\n{Visible(result.Output)}\n--- stderr ---\n{Visible(result.Error)}";
        result.Outcome.Should().Be(TestRunOutcome.Success, because: raw);
        messages.Should().Contain("Running tests in SampleLib.Tests.dll", because: raw);
        messages.Should().Contain(m => m.StartsWith("Test run summary:", StringComparison.Ordinal)
                                       || m.StartsWith("Passed!", StringComparison.Ordinal), because: raw);
        // The counts fallback used when a project has no TrxReport extension reads the same output.
        ProcessRunner.ParseMtpSummary(result.Output).Should().NotBeNull(because: raw);
    }

    // Control characters (e.g. ANSI escapes) made visible, tail only.
    private static string Visible(string s)
    {
        var tail = s.Length > 3000 ? s[^3000..] : s;
        return string.Concat(tail.Select(c => c is '\n' or '\r' or '\t' || !char.IsControl(c) ? c.ToString() : $"\\u{(int)c:x4}"));
    }

    [Fact]
    public async Task ClassDotMethodFilter_RunsOnlyThatTest()
    {
        var result = await _sut.RunTestsWithCoverage(_fixture.TestProjectPath, "CalculatorTests.Add_ReturnsSum", skipReport: true);

        result.Should().Contain("Passed: 1, Failed: 0", because: result);
    }

    [Fact]
    public async Task FilterMatchingNothing_ReturnsNoTestsMatched()
    {
        // MTP exits with 8 (ZeroTests) here.
        var result = await _sut.RunTestsWithCoverage(_fixture.TestProjectPath, "DoesNotExist", skipReport: true);

        JsonDocument.Parse(result).RootElement.GetProperty("errorType").GetString().Should().Be("noTestsMatched", because: result);
    }

    [Fact]
    public async Task FailingTest_ReturnsTestsFailedWithDetailsFromTrx()
    {
        var path = Path.Combine(Path.GetDirectoryName(_fixture.TestProjectPath)!, "FailingTests.cs");
        File.WriteAllText(path, """
            namespace SampleLib.Tests;
            public class FailingTests { [Fact] public void Wrong() => Assert.Equal(1, new Calculator().Add(1, 1)); }
            """);
        try
        {
            var result = await _sut.RunTestsWithCoverage(_fixture.TestProjectPath, skipReport: true);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("errorType").GetString().Should().Be("testsFailed", because: result);
            doc.RootElement.GetProperty("failed").GetInt32().Should().Be(1);
            doc.RootElement.GetProperty("failures")[0].GetProperty("test").GetString().Should().Contain("Wrong");
            File.Exists(doc.RootElement.GetProperty("coberturaXmlPath").GetString()).Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CoverageGaps_WorkOnMicrosoftsReport()
    {
        var xml = await Run();

        using var doc = JsonDocument.Parse(_sut.GetCoverageGaps(xml, Path.Combine(_fixture.Root, "SampleLib")));
        var root = doc.RootElement;
        root.GetProperty("notInReport").GetArrayLength().Should().Be(0, because: root.ToString());
        root.GetProperty("filesInScope").GetInt32().Should().Be(1);
        (root.GetProperty("filesDone").GetInt32() + root.GetProperty("filesRemaining").GetInt32()).Should().Be(1);
    }

    [Fact]
    public async Task IncludeClass_IsRejectedOnMtp()
    {
        var result = await _sut.RunTestsWithCoverage(_fixture.TestProjectPath, includeClass: "Calculator", skipReport: true);

        JsonDocument.Parse(result).RootElement.GetProperty("errorType").GetString().Should().Be("invalidParameter");
    }

    [Fact]
    public void TestEnvironment_ReportsMtpAndMicrosoftCoverage()
    {
        using var doc = JsonDocument.Parse(_sut.GetTestEnvironment(_fixture.TestProjectPath));
        var root = doc.RootElement;

        root.GetProperty("framework").GetString().Should().Be("xunit.v3");
        root.GetProperty("runner").GetString().Should().Be(TestEnvironmentDetector.Mtp);
        root.GetProperty("coverage").GetString().Should().Be("Microsoft.Testing.Extensions.CodeCoverage");
        root.GetProperty("warnings").GetArrayLength().Should().Be(0, because: root.ToString());
    }
}
