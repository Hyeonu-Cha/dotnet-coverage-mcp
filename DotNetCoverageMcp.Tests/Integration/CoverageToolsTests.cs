using System.Text.Json;
using DotNetCoverageMcp.Services;
using FluentAssertions;
using Moq;

namespace DotNetCoverageMcp.Tests.Integration;

public class CoverageToolsTests : IDisposable
{
    // RunTestsWithCoverage requires the project file to exist; the runner itself is mocked.
    private readonly string _proj = Path.Combine(Path.GetTempPath(), $"proj-{Guid.NewGuid():N}.csproj");
    // RunTestsWithCoverage refuses a workingDir that does not exist, so the runs below need a real one.
    private readonly string _workDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"work-{Guid.NewGuid():N}")).FullName;
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
        File.WriteAllText(_proj, "<Project />");
    }

    public void Dispose()
    {
        File.Delete(_proj);
        Directory.Delete(_workDir, recursive: true);
    }

    // --- RunTestsWithCoverage ---

    [Fact]
    public async Task RunTestsWithCoverage_EmptyTestProjectPath_ReturnsError()
    {
        var result = await _sut.RunTestsWithCoverage("", "filter");
        AssertError(result, "invalidParameter");
    }

    [Theory]
    [InlineData("--help")]                       // a dotnet CLI option
    [InlineData("-bl:evil.binlog")]              // an MSBuild switch that writes a file
    [InlineData("-flp:logfile=out.txt")]         // an MSBuild file-logger switch
    [InlineData("proj.txt")]                     // not a project
    [InlineData("SomeDirectory")]                // no .csproj extension
    public async Task RunTestsWithCoverage_NonCsprojOrOptionPath_RejectedWithoutRunning(string testProjectPath)
    {
        // Regression: these used to reach `dotnet test <value>`, where the value was parsed as a
        // CLI/MSBuild option instead of a project path (option injection).
        var result = await _sut.RunTestsWithCoverage(testProjectPath);

        AssertError(result, "invalidParameter");
        _processRunner.Verify(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("missing.csproj")]
    [InlineData("/p:Foo=bar.csproj")]  // rooted on Unix, yet dotnet reads it as a property switch
    public async Task RunTestsWithCoverage_ProjectFileMissing_RejectedWithoutRunning(string testProjectPath)
    {
        var result = await _sut.RunTestsWithCoverage(testProjectPath);

        AssertError(result, "fileNotFound");
        _processRunner.Verify(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunTestsWithCoverage_ResolvesProjectPathToRooted()
    {
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRunResult(TestRunOutcome.Success, "", "", 0, "/coverage.xml"));

        await _sut.RunTestsWithCoverage(Path.GetRelativePath(Environment.CurrentDirectory, _proj), null, _workDir);

        // A relative path is passed to dotnet as a rooted path, never as a bare token that could
        // be read as an option.
        _processRunner.Verify(p => p.RunDotnetTestAsync(
            It.Is<string>(s => Path.IsPathRooted(s) && s.EndsWith(Path.GetFileName(_proj), StringComparison.Ordinal)),
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<string?>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    public async Task RunTestsWithCoverage_NoFilter_RunsAllAndKeysStateAsStar(string? filter)
    {
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRunResult(TestRunOutcome.Success, "", "", 0, "/coverage.xml"));

        var result = await _sut.RunTestsWithCoverage(_proj, filter, _workDir);

        result.Should().Contain("coverage.xml");
        _sessionManager.Verify(s => s.SaveCoverageState(_workDir, "/coverage.xml", null), Times.Once);
    }

    [Fact]
    public async Task RunTestsWithCoverage_TestRunCancelled_ReturnsCancelledError()
    {
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRunResult(TestRunOutcome.Cancelled, "", "", -1, null));

        var result = await _sut.RunTestsWithCoverage(_proj, "Ns.Class", _workDir);
        AssertError(result, "cancelled");
    }

    [Fact]
    public async Task RunTestsWithCoverage_TestRunFails_ReturnsBuildError()
    {
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRunResult(TestRunOutcome.BuildError, "output", "error", 1, null));

        var result = await _sut.RunTestsWithCoverage(_proj, "Ns.Class", _workDir);
        AssertError(result, "buildError");
    }

    private void SetupTestRun(TestRunResult result) =>
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    [Fact]
    public async Task RunTestsWithCoverage_TestsFailed_ReturnsFailuresAndKeepsCoverage()
    {
        var summary = new TestSummary(3, 2, 1, 0,
            [new TestFailure("Ns.CalcTests.Add_Fails", "Expected 1, got 2", "at Ns.CalcTests.Add_Fails()")]);
        SetupTestRun(new TestRunResult(TestRunOutcome.TestsFailed, "", "", 1, "/coverage.xml", summary));

        var result = await _sut.RunTestsWithCoverage(_proj, "Ns.Class", _workDir);

        AssertError(result, "testsFailed");
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("failed").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("failures")[0].GetProperty("test").GetString().Should().Be("Ns.CalcTests.Add_Fails");
        doc.RootElement.GetProperty("coberturaXmlPath").GetString().Should().Be("/coverage.xml");
        // Coverage of the passing tests stays usable by the per-file tools.
        _sessionManager.Verify(s => s.SaveCoverageState(_workDir, "/coverage.xml", null), Times.Once);
    }

    [Fact]
    public async Task RunTestsWithCoverage_Aborted_ReportsTheCrashAndOffersNoCoverage()
    {
        var abort = new TestRunAbort(["The active test run was aborted. Reason: Test host process crashed"],
            ["CrashTests.HangsForever"], null);
        SetupTestRun(new TestRunResult(TestRunOutcome.Aborted, "", "", 1, "/coverage.xml",
            new TestSummary(3, 3, 0, 0, []), abort));

        var result = await _sut.RunTestsWithCoverage(_proj, null, _workDir);

        AssertError(result, "testRunAborted");
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("error").GetString().Should().Contain("3 of 3 recorded tests had passed");
        root.GetProperty("reasons")[0].GetString().Should().Contain("Test host process crashed");
        root.GetProperty("unfinishedTests")[0].GetString().Should().Be("CrashTests.HangsForever");
        root.GetProperty("hint").GetString().Should().Contain("COVERAGE_MCP_HANG_TIMEOUT_SECONDS");
        // Half a run's coverage would make the rest of the suite's code look untested.
        root.TryGetProperty("coberturaXmlPath", out _).Should().BeFalse();
        _sessionManager.Verify(s => s.SaveCoverageState(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task RunTestsWithCoverage_NoTestsMatched_ReturnsErrorNamingFilter()
    {
        SetupTestRun(new TestRunResult(TestRunOutcome.NoTestsMatched, "", "", 0, "/coverage.xml", new TestSummary(0, 0, 0, 0, [])));

        var result = await _sut.RunTestsWithCoverage(_proj, "DoesNotExist", _workDir);

        AssertError(result, "noTestsMatched");
        result.Should().Contain("DoesNotExist");
        _sessionManager.Verify(s => s.SaveCoverageState(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task RunTestsWithCoverage_Success_ReportsCounts()
    {
        SetupTestRun(new TestRunResult(TestRunOutcome.Success, "", "", 0, "/coverage.xml", new TestSummary(5, 4, 0, 1, [])));

        var result = await _sut.RunTestsWithCoverage(_proj, null, _workDir);

        result.Should().Contain("Passed: 4, Failed: 0, Skipped: 1.");
    }

    [Fact]
    public void BuildErrorDetail_DeduplicatesAndStripsProjectSuffix()
    {
        const string output = """
              Determining projects to restore...
            C:\src\T\Broken.cs(2,1): error CS1002: ; expected [C:\src\T\T.csproj]
            Build FAILED.
            C:\src\T\Broken.cs(2,1): error CS1002: ; expected [C:\src\T\T.csproj]
                0 Warning(s)
            """;

        CoverageTools.BuildErrorDetail(output, "").Should().Be(@"C:\src\T\Broken.cs(2,1): error CS1002: ; expected");
    }

    // Real output of two concurrent `dotnet test` runs of one project on Windows.
    private const string LockedBuildOutput = """
        CSC : error CS2012: Cannot open 'C:\src\T\obj\Debug\net10.0\T.dll' for writing -- The process cannot access the file 'C:\src\T\obj\Debug\net10.0\T.dll' because it is being used by another process. [C:\src\T\T.csproj]
        """;

    [Fact]
    public async Task RunTestsWithCoverage_OutputFilesLocked_ReportsProjectBusyNotBuildError()
    {
        SetupTestRun(new TestRunResult(TestRunOutcome.BuildError, LockedBuildOutput, "", 1, null));

        var result = await _sut.RunTestsWithCoverage(_proj, null, _workDir);

        AssertError(result, "projectBusy");
        result.Should().Contain("CS2012").And.Contain("run again");
    }

    [Fact]
    public void LockedOutputDetail_RealCompileErrorAlongside_IsNotTreatedAsBusy()
    {
        var output = LockedBuildOutput + "\nC:\\src\\T\\Broken.cs(2,1): error CS1002: ; expected [C:\\src\\T\\T.csproj]";

        CoverageTools.LockedOutputDetail(output, "").Should().BeNull("a real compile error still needs fixing");
        CoverageTools.LockedOutputDetail("MSBUILD : error MSB3027: Could not copy \"obj\\T.dll\" to \"bin\\T.dll\". Exceeded retry count of 10. Failed.", "")
            .Should().Contain("MSB3027");
        CoverageTools.LockedOutputDetail("CSC : error CS0016: Could not write to output file 'C:\\src\\T\\obj\\T.dll' -- 'The process cannot access the file because it is being used by another process.'", "")
            .Should().Contain("CS0016");
        CoverageTools.LockedOutputDetail("C:\\sdk\\Microsoft.Common.CurrentVersion.targets(5400,5): error MSB3491: Could not write lines to file \"obj\\T.csproj.FileListAbsolute.txt\". The process cannot access the file because it is being used by another process.", "")
            .Should().Contain("MSB3491");
        CoverageTools.LockedOutputDetail("Build FAILED.", "").Should().BeNull();
    }

    [Fact]
    public void BuildErrorDetail_NoErrorLines_FallsBackToTail()
    {
        CoverageTools.BuildErrorDetail("some output", "sdk exploded").Should().Contain("sdk exploded").And.Contain("some output");
    }

    [Fact]
    public void GetFileCoverage_Ambiguous_ReturnsCandidates()
    {
        _sessionManager.Setup(s => s.ResolveCoberturaPath(It.IsAny<string>(), It.IsAny<string?>())).Returns("/c.xml");
        _coberturaService.Setup(c => c.GetFileCoverage("/c.xml", "OrderController.cs", 0.8))
            .Throws(new AmbiguousFileException("matches 2 files", ["A/OrderController.cs", "B/OrderController.cs"]));

        var result = _sut.GetFileCoverage("/c.xml", "OrderController.cs");

        AssertError(result, "ambiguous");
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("candidates").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task RunTestsWithCoverage_NoCoverageXml_ReturnsError()
    {
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRunResult(TestRunOutcome.Success, "output", "", 0, null));

        var result = await _sut.RunTestsWithCoverage(_proj, "Ns.Class", _workDir);
        AssertError(result, "noCoverage");
        // dotnet test can finish without a word (an unrestored or non-test project); say what to check.
        result.Should().Contain("coverage collector").And.Contain("get_test_environment");
    }

    [Fact]
    public async Task RunTestsWithCoverage_MissingWorkingDir_ReturnsFileNotFound_WithoutRunning()
    {
        // Regression: Process.Start threw, and the agent got the SDK's generic error.
        var result = await _sut.RunTestsWithCoverage(_proj, null, Path.Combine(_workDir, "no-such-folder"));

        AssertError(result, "fileNotFound");
        result.Should().Contain("workingDir");
        _processRunner.Verify(p => p.RunDotnetTestAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunTestsWithCoverage_DotnetCannotStart_ReturnsDotnetNotFound()
    {
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified"));

        var result = await _sut.RunTestsWithCoverage(_proj, null, _workDir);

        AssertError(result, "dotnetNotFound");
        result.Should().Contain("The system cannot find the file specified").And.Contain("PATH");
    }

    [Fact]
    public async Task RunTestsWithCoverage_HappyPath_ReturnsPathsAndOutput()
    {
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRunResult(TestRunOutcome.Success, "test output", "", 0, "/coverage.xml"));

        var result = await _sut.RunTestsWithCoverage(_proj, "Ns.Class", _workDir);

        result.Should().Be("Tests completed.\nCobertura XML at: /coverage.xml");
        _sessionManager.Verify(s => s.SaveCoverageState(_workDir, "/coverage.xml", null), Times.Once);
    }

    [Fact]
    public async Task RunTestsWithCoverage_MergedReport_SaysSoBeforeThePathLine()
    {
        var merged = $"/r/{ProcessRunner.MergedCoverageFile}";
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRunResult(TestRunOutcome.Success, "", "", 0, merged));

        var result = await _sut.RunTestsWithCoverage(_proj, null, _workDir);

        result.Should().Contain("targets several frameworks").And.EndWith($"\nCobertura XML at: {merged}");
    }

    [Fact]
    public async Task RunTestsWithCoverage_DeletesPreviousResults()
    {
        _processRunner.Setup(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestRunResult(TestRunOutcome.Cancelled, "", "", -1, null));

        await _sut.RunTestsWithCoverage(_proj, "Ns.Class", _workDir);

        // Its own folder, never the bare TestResults that dotnet test and Visual Studio use.
        _fileService.Verify(f => f.SafeDelete(Path.Combine(_workDir, "TestResults-mcp")), Times.Once);
        _fileService.Verify(f => f.SafeDelete(Path.Combine(_workDir, "TestResults")), Times.Never);
        _processRunner.Verify(p => p.RunDotnetTestAsync(
            It.IsAny<string>(), It.IsAny<string?>(), Path.Combine(_workDir, "TestResults-mcp"), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- GetCoverageSummary ---

    [Fact]
    public void GetCoverageSummary_FileNotFound_ReturnsError()
    {
        // ResolveCoberturaPath returns null (mock default) when nothing resolves.
        var result = _sut.GetCoverageSummary("/nonexistent/coverage.cobertura.xml");
        AssertError(result, "fileNotFound");
    }

    [Fact]
    public void GetCoverageSummary_BelowTarget_FiltersAndSortsWorstFirst()
    {
        const string path = "/coverage.cobertura.xml";
        _sessionManager.Setup(s => s.ResolveCoberturaPath(path, null)).Returns(path);
        _coberturaService.Setup(c => c.SummarizeClasses(path)).Returns(new List<SummaryClass>
        {
            new("HighCov", 0.95, 0.90, new List<SummaryMethod>()),
            new("LowCov", 0.40, 0.30, new List<SummaryMethod>()),
            new("MidCov", 0.85, 0.75, new List<SummaryMethod>()),
        });

        var result = _sut.GetCoverageSummary(path, belowTarget: 0.8);

        using var doc = JsonDocument.Parse(result);
        var classes = doc.RootElement.GetProperty("classes").EnumerateArray()
            .Select(e => e.GetProperty("class").GetString()).ToList();
        // HighCov (0.95/0.90) is excluded; the rest are returned worst-branch-first.
        classes.Should().Equal("LowCov", "MidCov");
        doc.RootElement.GetProperty("totalClasses").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("moreClasses").GetInt32().Should().Be(0);
    }

    [Fact]
    public void GetCoverageSummary_WithoutTopN_ReturnsTheWorst25AndCountsTheRest()
    {
        // Every class of a large solution came back by default: 100k+ tokens.
        const string path = "/coverage.cobertura.xml";
        _sessionManager.Setup(s => s.ResolveCoberturaPath(path, null)).Returns(path);
        _coberturaService.Setup(c => c.SummarizeClasses(path)).Returns(
            Enumerable.Range(0, 30).Select(i => new SummaryClass($"C{i:00}", i / 100.0, i / 100.0, [])).ToList());

        using var doc = JsonDocument.Parse(_sut.GetCoverageSummary(path));

        var classes = doc.RootElement.GetProperty("classes").EnumerateArray().ToList();
        classes.Should().HaveCount(25);
        classes[0].GetProperty("class").GetString().Should().Be("C00", "worst first");
        doc.RootElement.GetProperty("totalClasses").GetInt32().Should().Be(30);
        doc.RootElement.GetProperty("moreClasses").GetInt32().Should().Be(5);
    }

    [Fact]
    public void GetCoverageSummary_TopNAndMethodsPerClass_Trim()
    {
        const string path = "/coverage.cobertura.xml";
        _sessionManager.Setup(s => s.ResolveCoberturaPath(path, null)).Returns(path);
        _coberturaService.Setup(c => c.SummarizeClasses(path)).Returns(new List<SummaryClass>
        {
            new("A", 0.5, 0.5, new List<SummaryMethod> { new("m1", 0.1, 0.1), new("m2", 0.2, 0.2), new("m3", 0.3, 0.3) }),
            new("B", 0.9, 0.9, new List<SummaryMethod>()),
            new("C", 0.4, 0.4, new List<SummaryMethod>()),
        });

        var result = _sut.GetCoverageSummary(path, topN: 2, methodsPerClass: 1);

        using var doc = JsonDocument.Parse(result);
        var classes = doc.RootElement.GetProperty("classes").EnumerateArray().ToList();
        classes.Should().HaveCount(2);                                  // topN
        doc.RootElement.GetProperty("moreClasses").GetInt32().Should().Be(1);
        classes[0].GetProperty("class").GetString().Should().Be("C");   // worst branch first
        classes[1].GetProperty("class").GetString().Should().Be("A");
        classes[1].GetProperty("methods").GetArrayLength().Should().Be(1); // methodsPerClass
    }

    // Parameter validation happens before the path is resolved, so these need no file or mock.
    [Fact]
    public void GetCoverageSummary_InvalidBelowTarget_ReturnsError() =>
        AssertError(_sut.GetCoverageSummary("/c.cobertura.xml", belowTarget: 80), "invalidParameter");

    [Fact]
    public void GetCoverageSummary_InvalidTopN_ReturnsError()
    {
        AssertError(_sut.GetCoverageSummary("/c.cobertura.xml", topN: 0), "invalidParameter");
        AssertError(_sut.GetCoverageSummary("/c.cobertura.xml", topN: -1), "invalidParameter");
    }

    [Fact]
    public void GetCoverageSummary_InvalidMethodsPerClass_ReturnsError() =>
        AssertError(_sut.GetCoverageSummary("/c.cobertura.xml", methodsPerClass: -1), "invalidParameter");

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

    [Theory]
    [InlineData("settings.json")]
    [InlineData("notes.txt")]
    [InlineData("data.csv")]
    public async Task AppendTestCode_NonCsFile_ReturnsErrorAndDoesNotWrite(string fileName)
    {
        // Regression: the string fallback used to append "test code" into any file the path
        // pointed at — corrupting a non-.cs file and reporting success.
        var path = Path.Combine(Path.GetTempPath(), $"append-{Guid.NewGuid():N}-{fileName}");
        const string original = "{ \"keep\": true }";
        File.WriteAllText(path, original);
        try
        {
            var result = await _sut.AppendTestCode(path, "public int Pwned = 1;");

            AssertError(result, "invalidParameter");
            File.ReadAllText(path).Should().Be(original); // untouched
            _codeInserter.Verify(c => c.InsertCodeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AppendTestCode_AnchorNotFound_ReturnsError()
    {
        // Create a real temp file so File.Exists passes
        var tempFile = CreateTempCsFile();
        try
        {
            _codeInserter.Setup(c => c.InsertCodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
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
    public async Task AppendTestCode_DuplicateMember_ReturnsDuplicateMember()
    {
        var tempFile = CreateTempCsFile();
        try
        {
            _codeInserter.Setup(c => c.InsertCodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ThrowsAsync(new DuplicateMemberException("CalcTests already declares Adds() (different code)"));

            AssertError(await _sut.AppendTestCode(tempFile, "public void Adds() { }"), "duplicateMember");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AppendTestCode_RepeatedCall_SaysNothingWasInserted()
    {
        var tempFile = CreateTempCsFile();
        try
        {
            _codeInserter.Setup(c => c.InsertCodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync(new InsertionResult(InsertionMethod.AlreadyPresent, "class C { }"));

            var result = await _sut.AppendTestCode(tempFile, "public void Adds() { }");

            result.Should().StartWith("Nothing inserted").And.NotContain("Successfully");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AppendTestCode_SyntaxError_ReturnsSyntaxError()
    {
        var tempFile = CreateTempCsFile();
        try
        {
            _codeInserter.Setup(c => c.InsertCodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ThrowsAsync(new CodeSyntaxException("codeToAppend is not valid C# member code"));

            var result = await _sut.AppendTestCode(tempFile, "public void Bad( { }");

            AssertError(result, "syntaxError");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AppendTestCode_RoslynSuccess_ReturnsRoslynLabel()
    {
        var tempFile = CreateTempCsFile();
        try
        {
            _codeInserter.Setup(c => c.InsertCodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync(new InsertionResult(InsertionMethod.RoslynAst, "content"));

            var result = await _sut.AppendTestCode(tempFile, "code");
            result.Should().Contain("Roslyn AST");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(InsertionMethod.StringFallback, "via string fallback in")]
    [InlineData(InsertionMethod.StringFallbackNormalized, "whitespace-normalized")]
    [InlineData(InsertionMethod.Appended, "inside last class")]
    public async Task AppendTestCode_FallbackInsert_NamesHowTheCodeWentIn(InsertionMethod method, string label)
    {
        var tempFile = CreateTempCsFile();
        try
        {
            _codeInserter.Setup(c => c.InsertCodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync(new InsertionResult(method, "content"));

            (await _sut.AppendTestCode(tempFile, "code", "anchor")).Should().Contain(label);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void MissingExtensionDetail_QuotesTheLinesNamingThePackage_ElseTheOutputTail()
    {
        const string output = "Build succeeded.\n  Unknown option '--coverage'\n  The option is provided by Microsoft.Testing.Extensions.CodeCoverage\n";

        CoverageTools.MissingExtensionDetail(output, "").Should().Be(
            "Unknown option '--coverage'\nThe option is provided by Microsoft.Testing.Extensions.CodeCoverage");
        CoverageTools.MissingExtensionDetail("something else", "")
            .Should().StartWith("The test application rejected an option (exit code 5).").And.Contain("something else");
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

    [Fact]
    public void CleanupSession_NegativeMaxAge_IsRejected_AndDeletesNothing()
    {
        // A negative age would date the cutoff in the future and sweep every session's artifacts.
        AssertError(_sut.CleanupSession("/dir", null, -5), "invalidParameter");
        _sessionManager.Verify(s => s.Cleanup(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
    }

    // --- GetCoverageDiff workingDir resolution ---

    [Fact]
    public void GetCoverageDiff_DefaultWorkingDir_ResolvesToProjectRoot_NotTestResultsSubdir()
    {
        // Simulate resolvedPath inside TestResults-xxx/guid/ structure.
        // The baseline must NOT be stored inside TestResults (it gets wiped on the next
        // run) — GetCoverageDiff should walk up past TestResults/guid to the project root.
        var resolvedPath = Path.Combine(Path.GetTempPath(), "TestResults-abc", Guid.NewGuid().ToString(), "coverage.cobertura.xml");
        var expectedRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        _sessionManager.Setup(s => s.ResolveCoberturaPath(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(resolvedPath);
        _sessionManager.Setup(s => s.ComputeSuffix(It.IsAny<string?>())).Returns("");

        // First run (no baseline yet): the baseline is copied to the resolved project
        // root's .mcp-coverage, never inside the TestResults subdirectory.
        _sut.GetCoverageDiff("x.xml");

        _fileService.Verify(f => f.AtomicCopyFile(
            resolvedPath,
            It.Is<string>(dst =>
                dst.StartsWith(Path.Combine(expectedRoot, ".mcp-coverage"), StringComparison.OrdinalIgnoreCase)
                && !dst.Contains("TestResults", StringComparison.OrdinalIgnoreCase))),
            Times.Once);
    }

    // --- Helpers ---

    private static void AssertError(string json, string expectedErrorType)
    {
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("errorType").GetString().Should().Be(expectedErrorType);
    }

    // AppendTestCode now requires a .cs target, so these tests need a real .cs file on disk
    // (not the .tmp file that Path.GetTempFileName creates).
    private static string CreateTempCsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"append-{Guid.NewGuid():N}.cs");
        File.WriteAllText(path, "class C { }");
        return path;
    }
}
