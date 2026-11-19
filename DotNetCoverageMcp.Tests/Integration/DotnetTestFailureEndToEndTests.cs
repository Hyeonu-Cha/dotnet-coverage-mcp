using System.Text.Json;
using DotNetCoverageMcp.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetCoverageMcp.Tests.Integration;

/// <summary>
/// Real `dotnet test` runs for the outcomes that are not a clean pass. Each test adds a
/// file to its own fixture copy and removes it afterwards; tests in one class run
/// sequentially, and this class's copy is separate from DotnetTestEndToEndTests'.
/// </summary>
[Trait("Category", "EndToEnd")]
public class DotnetTestFailureEndToEndTests : IClassFixture<SampleProjectFixture>
{
    private readonly SampleProjectFixture _fixture;
    private readonly CoverageTools _sut;

    public DotnetTestFailureEndToEndTests(SampleProjectFixture fixture)
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
    public async Task FailingTest_ReturnsTestsFailedWithFailureAndCoverage()
    {
        using var _ = AddTestFile("FailingTests.cs", """
            namespace SampleLib.Tests;
            public class FailingTests
            {
                [Fact]
                public void Add_DeliberatelyWrong() => Assert.Equal(1, new Calculator().Add(1, 1));
            }
            """);

        var result = await _sut.RunTestsWithCoverage(_fixture.TestProjectPath);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("errorType").GetString().Should().Be("testsFailed", because: result);
        root.GetProperty("passed").GetInt32().Should().Be(3);
        root.GetProperty("failed").GetInt32().Should().Be(1);
        var failure = root.GetProperty("failures")[0];
        failure.GetProperty("test").GetString().Should().Be("SampleLib.Tests.FailingTests.Add_DeliberatelyWrong");
        failure.GetProperty("message").GetString().Should().Contain("Assert.Equal() Failure");
        failure.GetProperty("stack").GetString().Should().StartWith("at SampleLib.Tests.FailingTests.Add_DeliberatelyWrong");
        File.Exists(root.GetProperty("coberturaXmlPath").GetString()).Should().BeTrue(
            "coverage of the passing tests must survive a failing test");
    }

    [Fact]
    public async Task FilterMatchingNothing_ReturnsNoTestsMatched()
    {
        var result = await _sut.RunTestsWithCoverage(_fixture.TestProjectPath, "DoesNotExist");

        JsonDocument.Parse(result).RootElement.GetProperty("errorType").GetString()
            .Should().Be("noTestsMatched", because: result);
    }

    [Fact]
    public async Task CompileError_ReturnsBuildErrorWithCompilerMessage()
    {
        using var _ = AddTestFile("Broken.cs", "namespace SampleLib.Tests; public class Broken { int x = }");

        var result = await _sut.RunTestsWithCoverage(_fixture.TestProjectPath);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("errorType").GetString().Should().Be("buildError", because: result);
        doc.RootElement.GetProperty("error").GetString().Should().MatchRegex(@"Broken\.cs\(1,\d+\): error CS\d+");
    }

    [Fact]
    public async Task TestCrashingTheHost_ReturnsTestRunAbortedWithTheCrash()
    {
        // Regression: a crash before any result left total="0" in the TRX and was reported as
        // "The test project contains no tests".
        using var crashTests = AddTestFile("CrashTests.cs", """
            namespace SampleLib.Tests;
            public class CrashTests
            {
                [Fact]
                public void CrashesTheTestHost() => Environment.FailFast("simulated crash in a test");
            }
            """);

        var result = await _sut.RunTestsWithCoverage(_fixture.TestProjectPath);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("errorType").GetString().Should().Be("testRunAborted", because: result);
        root.GetProperty("reasons").GetArrayLength().Should().BeGreaterThan(0, result);
        root.GetProperty("crash").GetString().Should().Contain("simulated crash in a test").And.Contain("CrashesTheTestHost");
        root.TryGetProperty("coberturaXmlPath", out _).Should().BeFalse("a run that stopped part-way has incomplete coverage");
    }

    private TempFile AddTestFile(string name, string content) =>
        new(Path.Combine(Path.GetDirectoryName(_fixture.TestProjectPath)!, name), content);

    private sealed class TempFile : IDisposable
    {
        private readonly string _path;
        public TempFile(string path, string content) { _path = path; File.WriteAllText(path, content); }
        public void Dispose() => File.Delete(_path);
    }
}
