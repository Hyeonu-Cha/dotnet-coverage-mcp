using CoverageMcpServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace CoverageMcpServer.Tests.Unit;

public class SessionManagerTests : IDisposable
{
    private readonly SessionManager _sut;
    private readonly Mock<IFileService> _fileService;
    private readonly string _tempDir;

    public SessionManagerTests()
    {
        _fileService = new Mock<IFileService>();
        var pathGuard = new Mock<IPathGuard>();
        pathGuard.Setup(g => g.IsWithinAllowedRoot(It.IsAny<string>())).Returns(true);
        var logger = new Mock<ILogger<SessionManager>>();
        _sut = new SessionManager(_fileService.Object, pathGuard.Object, logger.Object);
        _tempDir = Path.Combine(Path.GetTempPath(), $"smt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // --- ComputeKey ---

    [Fact]
    public void ComputeKey_DeterministicForSameInput()
    {
        var a = _sut.ComputeKey("test-input");
        var b = _sut.ComputeKey("test-input");
        a.Should().Be(b);
    }

    [Fact]
    public void ComputeKey_DifferentForDifferentInput()
    {
        var a = _sut.ComputeKey("alpha");
        var b = _sut.ComputeKey("beta");
        a.Should().NotBe(b);
    }

    [Fact]
    public void ComputeKey_Returns8CharLowercaseHex()
    {
        var key = _sut.ComputeKey("anything");
        key.Should().HaveLength(8);
        key.Should().MatchRegex("^[0-9a-f]{8}$");
    }

    // --- ComputeSuffix ---

    [Fact]
    public void ComputeSuffix_ReturnsEmptyWhenNull()
    {
        _sut.ComputeSuffix(null).Should().BeEmpty();
    }

    [Fact]
    public void ComputeSuffix_ReturnsDashPrefixedKey()
    {
        var suffix = _sut.ComputeSuffix("session1");
        suffix.Should().StartWith("-");
        suffix.Should().HaveLength(9); // dash + 8 hex chars
    }

    // --- ResolveCoberturaPath ---

    [Fact]
    public void ResolveCoberturaPath_ReturnsPathWhenFileExists()
    {
        var path = Path.Combine(_tempDir, "coverage.xml");
        File.WriteAllText(path, "<coverage/>");

        _sut.ResolveCoberturaPath(path, null).Should().Be(path);
    }

    [Fact]
    public void ResolveCoberturaPath_FallsBackToSessionState()
    {
        var xmlPath = Path.Combine(_tempDir, "actual.xml");
        File.WriteAllText(xmlPath, "<coverage/>");

        var stateDir = Path.Combine(_tempDir, ".mcp-coverage");
        Directory.CreateDirectory(stateDir);
        var key = _sut.ComputeKey("session1");
        File.WriteAllText(Path.Combine(stateDir, $".coverage-state-{key}"), xmlPath);

        var bogusPath = Path.Combine(_tempDir, "nonexistent.xml");
        _sut.ResolveCoberturaPath(bogusPath, "session1").Should().Be(xmlPath);
    }

    [Fact]
    public void ResolveCoberturaPath_FallsBackToGlobalState()
    {
        var xmlPath = Path.Combine(_tempDir, "actual.xml");
        File.WriteAllText(xmlPath, "<coverage/>");

        var stateDir = Path.Combine(_tempDir, ".mcp-coverage");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, ".coverage-state"), xmlPath);

        var bogusPath = Path.Combine(_tempDir, "nonexistent.xml");
        _sut.ResolveCoberturaPath(bogusPath, null).Should().Be(xmlPath);
    }

    [Fact]
    public void ResolveCoberturaPath_ReturnsNullWhenNothingFound()
    {
        var bogusPath = Path.Combine(_tempDir, "nonexistent.xml");
        _sut.ResolveCoberturaPath(bogusPath, null).Should().BeNull();
    }

    [Fact]
    public void ResolveCoberturaPath_SessionTakesPriorityOverGlobal()
    {
        var sessionXml = Path.Combine(_tempDir, "session.xml");
        var globalXml = Path.Combine(_tempDir, "global.xml");
        File.WriteAllText(sessionXml, "<coverage/>");
        File.WriteAllText(globalXml, "<coverage/>");

        var stateDir = Path.Combine(_tempDir, ".mcp-coverage");
        Directory.CreateDirectory(stateDir);
        var key = _sut.ComputeKey("s1");
        File.WriteAllText(Path.Combine(stateDir, $".coverage-state-{key}"), sessionXml);
        File.WriteAllText(Path.Combine(stateDir, ".coverage-state"), globalXml);

        var bogus = Path.Combine(_tempDir, "missing.xml");
        _sut.ResolveCoberturaPath(bogus, "s1").Should().Be(sessionXml);
    }

    [Fact]
    public void ResolveCoberturaPath_RejectsStateFilePointingOutsideAllowedRoot()
    {
        // Simulate a poisoned/stale state file that points to an allowed-root-escape.
        // The path guard should cause the resolver to skip it instead of returning the
        // escape path to the caller.
        var outsideXml = Path.Combine(_tempDir, "outside.xml");
        File.WriteAllText(outsideXml, "<coverage/>");

        var stateDir = Path.Combine(_tempDir, ".mcp-coverage");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, ".coverage-state"), outsideXml);

        var guard = new Mock<IPathGuard>();
        guard.Setup(g => g.IsWithinAllowedRoot(outsideXml)).Returns(false);
        guard.Setup(g => g.IsWithinAllowedRoot(It.Is<string>(s => s != outsideXml))).Returns(true);
        var logger = new Mock<ILogger<SessionManager>>();
        var sut = new SessionManager(_fileService.Object, guard.Object, logger.Object);

        var bogus = Path.Combine(_tempDir, "missing.xml");
        sut.ResolveCoberturaPath(bogus, null).Should().BeNull();
    }

    [Fact]
    public void ResolveCoberturaPath_WalksUpFromDeletedTestResultsDir()
    {
        // State files are at project root, but caller passes a path
        // inside a (now deleted) TestResults-xxx/guid/ directory
        var xmlPath = Path.Combine(_tempDir, "actual.xml");
        File.WriteAllText(xmlPath, "<coverage/>");

        var stateDir = Path.Combine(_tempDir, ".mcp-coverage");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, ".coverage-state"), xmlPath);

        // The bogus path is deep inside a TestResults subdir that no longer exists
        var deletedSubdir = Path.Combine(_tempDir, "TestResults-abc", Guid.NewGuid().ToString());
        var stalePath = Path.Combine(deletedSubdir, "coverage.cobertura.xml");

        // .mcp-coverage/ does NOT exist under TestResults-abc/, only at _tempDir
        _sut.ResolveCoberturaPath(stalePath, null).Should().Be(xmlPath);
    }

    // --- SaveCoverageState ---

    [Fact]
    public void SaveCoverageState_WritesFilterKeyedAndGlobalAlways()
    {
        _sut.SaveCoverageState(_tempDir, "/path/to/xml", "Ns.MyFilter", null);

        // Filter-keyed state file
        _fileService.Verify(f => f.AtomicWriteFile(
            It.Is<string>(s => s.Contains(".coverage-state-")),
            "/path/to/xml"), Times.Once);
        // Global state file
        _fileService.Verify(f => f.AtomicWriteFile(
            It.Is<string>(s => s.EndsWith(".coverage-state")),
            "/path/to/xml"), Times.Once);
    }

    [Fact]
    public void SaveCoverageState_WritesFilterAndSessionKeyedWhenSessionDiffersFromFilter()
    {
        _sut.SaveCoverageState(_tempDir, "/path/to/xml", "Ns.MyFilter", "sess1");

        // Filter-keyed + session-keyed (different hashes) + global = 3 writes
        _fileService.Verify(f => f.AtomicWriteFile(
            It.Is<string>(s => s.Contains(".coverage-state-")),
            "/path/to/xml"), Times.Exactly(2));
        _fileService.Verify(f => f.AtomicWriteFile(
            It.Is<string>(s => s.EndsWith(".coverage-state")),
            "/path/to/xml"), Times.Once);
    }

    [Fact]
    public void SaveCoverageState_SkipsDuplicateWhenSessionEqualsFilter()
    {
        // When sessionId == filter, their keys match — only 2 writes (keyed + global)
        _sut.SaveCoverageState(_tempDir, "/path/to/xml", "same", "same");

        _fileService.Verify(f => f.AtomicWriteFile(
            It.Is<string>(s => s.Contains(".coverage-state-")),
            "/path/to/xml"), Times.Once);
        _fileService.Verify(f => f.AtomicWriteFile(
            It.Is<string>(s => s.EndsWith(".coverage-state")),
            "/path/to/xml"), Times.Once);
    }

    // --- Cleanup ---

    [Fact]
    public void Cleanup_WithSessionId_DeletesSessionArtifacts()
    {
        var stateDir = Path.Combine(_tempDir, ".mcp-coverage");
        Directory.CreateDirectory(stateDir);
        var key = _sut.ComputeKey("cleanup-test");

        File.WriteAllText(Path.Combine(stateDir, $".coverage-state-{key}"), "data");
        File.WriteAllText(Path.Combine(stateDir, $".coverage-prev-{key}.xml"), "data");

        var removed = _sut.Cleanup(_tempDir, "cleanup-test", 120);

        removed.Should().Be(2);
        File.Exists(Path.Combine(stateDir, $".coverage-state-{key}")).Should().BeFalse();
    }

    [Fact]
    public void Cleanup_SessionIsolation_DoesNotTouchOtherSessions()
    {
        var stateDir = Path.Combine(_tempDir, ".mcp-coverage");
        Directory.CreateDirectory(stateDir);
        var keyA = _sut.ComputeKey("sessionA");
        var keyB = _sut.ComputeKey("sessionB");

        File.WriteAllText(Path.Combine(stateDir, $".coverage-state-{keyA}"), "a");
        File.WriteAllText(Path.Combine(stateDir, $".coverage-state-{keyB}"), "b");

        _sut.Cleanup(_tempDir, "sessionA", 120);

        File.Exists(Path.Combine(stateDir, $".coverage-state-{keyA}")).Should().BeFalse();
        File.Exists(Path.Combine(stateDir, $".coverage-state-{keyB}")).Should().BeTrue();
    }
}
