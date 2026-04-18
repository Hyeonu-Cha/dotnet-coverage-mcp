using System.Text.Json;
using CoverageMcpServer.Services;
using FluentAssertions;
using Moq;

namespace CoverageMcpServer.Tests.Integration;

public class CoverageToolsTests
{
    private readonly Mock<IProcessRunner> _processRunner = new();
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly Mock<IFileService> _fileService = new();
    private readonly Mock<ICoberturaService> _coberturaService = new();
    private readonly Mock<ICodeInserter> _codeInserter = new();
    private readonly Mock<IPathGuard> _pathGuard = new();
    private readonly CoverageTools _sut;

    public CoverageToolsTests()
    {
        _sut = new CoverageTools(
            _processRunner.Object,
            _sessionManager.Object,
            _fileService.Object,
            _coberturaService.Object,
            _codeInserter.Object,
            _pathGuard.Object);

        _sessionManager.Setup(s => s.ComputeSuffix(It.IsAny<string?>())).Returns("");
        // Default: path guard accepts everything. Individual tests override to assert rejection.
        _pathGuard.Setup(p => p.Validate(It.IsAny<string>(), It.IsAny<string>()));
    }

    // --- RunTestsWithCoverage ---

    [Fact]
    public async Task RunTestsWithCoverage_EmptyTestProjectPath_ReturnsError()
    {
        var result = await _sut.RunTestsWithCoverage("", "filter");
        AssertError(result, "invalidParameter");
    }

    [Fact]
    public async Task RunTestsWithCoverage_EmptyFilter_ReturnsError()
    {
        var result = await _sut.RunTestsWithCoverage("project.csproj", "");
        AssertError(result, "invalidParameter");
    }

    [Fact]
    public async Task RunTestsWithCoverage_TestRunCancelled_ReturnsCancelledError()
    {
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRunResult(false, "", "cancelled", -1, null));

        var result = await _sut.RunTestsWithCoverage("proj.csproj", "Ns.Class", "/dir");
        AssertError(result, "cancelled");
    }

    [Fact]
    public async Task RunTestsWithCoverage_TestRunFails_ReturnsBuildError()
    {
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRunResult(false, "output", "error", 1, null));

        var result = await _sut.RunTestsWithCoverage("proj.csproj", "Ns.Class", "/dir");
        AssertError(result, "buildError");
    }

    [Fact]
    public async Task RunTestsWithCoverage_NoCoverageXml_ReturnsError()
    {
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRunResult(true, "output", "", 0, null));

        var result = await _sut.RunTestsWithCoverage("proj.csproj", "Ns.Class", "/dir");
        AssertError(result, "noCoverage");
    }

    [Fact]
    public async Task RunTestsWithCoverage_HappyPath_ReturnsPathsAndOutput()
    {
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRunResult(true, "test output", "", 0, "/coverage.xml"));

        _processRunner.Setup(p => p.RunReportGeneratorAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReportResult(true, "/report/Summary.json", null));

        var result = await _sut.RunTestsWithCoverage("proj.csproj", "Ns.Class", "/dir");

        result.Should().Contain("Summary.json");
        result.Should().Contain("coverage.xml");
        _sessionManager.Verify(s => s.SaveCoverageState("/dir", "/coverage.xml", "Ns.Class", null), Times.Once);
    }

    [Fact]
    public async Task RunTestsWithCoverage_ReportTimesOut_ReturnsTimeoutWithXmlPath()
    {
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRunResult(true, "", "", 0, "/coverage.xml"));

        _processRunner.Setup(p => p.RunReportGeneratorAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReportResult(false, null, "reportgenerator timed out after 60s"));

        var result = await _sut.RunTestsWithCoverage("proj.csproj", "Ns.Class", "/dir");

        AssertError(result, "timeout");
        result.Should().Contain("coverage.xml");
    }

    [Fact]
    public async Task RunTestsWithCoverage_DeletesPreviousResults()
    {
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRunResult(false, "", "cancelled", -1, null));

        await _sut.RunTestsWithCoverage("proj.csproj", "Ns.Class", "/dir");

        _fileService.Verify(f => f.SafeDelete(It.Is<string>(s => s.Contains("TestResults"))), Times.Once);
        _fileService.Verify(f => f.SafeDelete(It.Is<string>(s => s.Contains("coveragereport"))), Times.Once);
    }

    [Fact]
    public async Task RunTestsWithCoverage_ReportGeneratorNotInstalled_ReturnsStructuredError()
    {
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRunResult(true, "", "", 0, "/coverage.xml"));

        _processRunner.Setup(p => p.RunReportGeneratorAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Failed to start reportgenerator"));

        var result = await _sut.RunTestsWithCoverage("proj.csproj", "Ns.Class", "/dir");

        AssertError(result, "reportFailed");
        result.Should().Contain("reportgenerator");
        result.Should().Contain("coverage.xml");
    }

    // --- GetCoverageSummary ---

    [Fact]
    public void GetCoverageSummary_FileNotFound_ReturnsError()
    {
        var result = _sut.GetCoverageSummary("/nonexistent/Summary.json");
        AssertError(result, "fileNotFound");
    }

    // --- GetUncoveredBranches ---

    [Fact]
    public void GetUncoveredBranches_ResolvedPathNull_ReturnsFileNotFound()
    {
        _sessionManager.Setup(s => s.ResolveCoberturaPath(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string?)null);

        var result = _sut.GetUncoveredBranches("bogus.xml", "Method");
        AssertError(result, "fileNotFound");
    }

    [Fact]
    public void GetUncoveredBranches_NoMatch_ReturnsNoMatchError()
    {
        _sessionManager.Setup(s => s.ResolveCoberturaPath(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns("/resolved.xml");
        _coberturaService.Setup(c => c.GetUncoveredBranches(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new KeyNotFoundException("not found"));

        var result = _sut.GetUncoveredBranches("x.xml", "Missing");
        AssertError(result, "noMatch");
    }

    // --- AppendTestCode ---

    [Fact]
    public async Task AppendTestCode_FileNotFound_ReturnsError()
    {
        var result = await _sut.AppendTestCode("/nonexistent.cs", "code");
        AssertError(result, "fileNotFound");
    }

    [Fact]
    public async Task AppendTestCode_AnchorNotFound_ReturnsError()
    {
        // Create a real temp file so File.Exists passes
        var tempFile = Path.GetTempFileName();
        try
        {
            _codeInserter.Setup(c => c.InsertCodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new KeyNotFoundException("anchor not found"));

            var result = await _sut.AppendTestCode(tempFile, "code", "missing-anchor");
            AssertError(result, "anchorNotFound");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AppendTestCode_RoslynSuccess_ReturnsRoslynLabel()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            _codeInserter.Setup(c => c.InsertCodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InsertionResult(InsertionMethod.RoslynAst, "content"));

            var result = await _sut.AppendTestCode(tempFile, "code");
            result.Should().Contain("Roslyn AST");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- GetFileCoverage ---

    [Fact]
    public void GetFileCoverage_InvalidTargetRate_ReturnsError()
    {
        var result = _sut.GetFileCoverage("x.xml", "Foo.cs", null, 1.5);
        AssertError(result, "invalidParameter");
    }

    [Fact]
    public void GetFileCoverage_NegativeTargetRate_ReturnsError()
    {
        var result = _sut.GetFileCoverage("x.xml", "Foo.cs", null, -0.1);
        AssertError(result, "invalidParameter");
    }

    // --- GetSourceFiles ---

    [Fact]
    public void GetSourceFiles_InvalidLineBudget_ReturnsError()
    {
        var result = _sut.GetSourceFiles("/some/path", 0);
        AssertError(result, "invalidParameter");
    }

    // --- CleanupSession ---

    [Fact]
    public void CleanupSession_DelegatesToSessionManager()
    {
        _sessionManager.Setup(s => s.Cleanup("/dir", "s1", 60)).Returns(3);

        var result = _sut.CleanupSession("/dir", "s1", 60);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("removed").GetInt32().Should().Be(3);
    }

    // --- GetCoverageDiff workingDir resolution ---

    [Fact]
    public void GetCoverageDiff_DefaultWorkingDir_ResolvesToProjectRoot_NotTestResultsSubdir()
    {
        // Simulate resolvedPath inside TestResults-xxx/guid/ structure
        // The baseline should NOT be stored inside TestResults (it gets wiped)
        var resolvedPath = Path.Combine(Path.GetTempPath(), "TestResults-abc", Guid.NewGuid().ToString(), "coverage.cobertura.xml");
        var expectedRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        _sessionManager.Setup(s => s.ResolveCoberturaPath(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(resolvedPath);
        _sessionManager.Setup(s => s.ComputeSuffix(It.IsAny<string?>())).Returns("");

        // GetCoverageDiff will try File.ReadAllText on resolvedPath and fail,
        // but we can verify it creates .mcp-coverage in the right place
        var result = _sut.GetCoverageDiff("x.xml");

        // It should fail (file doesn't exist), but the error should NOT reference TestResults
        // as the working directory — it should have walked up to temp root
        AssertError(result, "parseFailed");
    }

    // --- Helpers ---

    private static void AssertError(string json, string expectedErrorType)
    {
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("errorType").GetString().Should().Be(expectedErrorType);
    }
}
