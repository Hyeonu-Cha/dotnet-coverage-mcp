using DotNetCoverageMcp.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace DotNetCoverageMcp.Tests.Unit;

public class PathGuardTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _originalEnvVar;

    public PathGuardTests()
    {
        _originalEnvVar = Environment.GetEnvironmentVariable(PathGuard.EnvVarName);
        _tempRoot = Path.Combine(Path.GetTempPath(), $"pg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVarName, _originalEnvVar);
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, true);
    }

    private PathGuard CreateGuardWithRoot(string? root)
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVarName, root);
        return new PathGuard(new Mock<ILogger<PathGuard>>().Object);
    }

    [Fact]
    public void Validate_NoEnvVar_AllowsAnyPath()
    {
        var guard = CreateGuardWithRoot(null);

        var act = () => guard.Validate(@"C:\Windows\System32\evil.exe", "p");

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_PathInsideRoot_Allowed()
    {
        var guard = CreateGuardWithRoot(_tempRoot);
        var inside = Path.Combine(_tempRoot, "sub", "file.cs");

        var act = () => guard.Validate(inside, "p");

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_PathOutsideRoot_Throws()
    {
        var guard = CreateGuardWithRoot(_tempRoot);
        var outside = Path.Combine(Path.GetTempPath(), "some-other-dir", "file.cs");

        var act = () => guard.Validate(outside, "testFilePath");

        act.Should().Throw<UnauthorizedAccessException>()
            .WithMessage("*testFilePath*");
    }

    [Fact]
    public void Validate_TraversalAttempt_Blocked()
    {
        var guard = CreateGuardWithRoot(_tempRoot);
        // Attempt to escape the root via ..
        var traversal = Path.Combine(_tempRoot, "..", "..", "Windows", "System32");

        var act = () => guard.Validate(traversal, "p");

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Validate_PrefixCollisionIsRejected()
    {
        // Ensure "C:\repo" does not match "C:\repo-evil"
        var rootName = Path.GetFileName(_tempRoot);
        var sibling = Path.Combine(Path.GetDirectoryName(_tempRoot)!, rootName + "-evil");
        Directory.CreateDirectory(sibling);
        try
        {
            var guard = CreateGuardWithRoot(_tempRoot);
            var act = () => guard.Validate(Path.Combine(sibling, "file.cs"), "p");
            act.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            Directory.Delete(sibling, true);
        }
    }

    [Fact]
    public void IsWithinAllowedRoot_EmptyPath_ReturnsFalse()
    {
        var guard = CreateGuardWithRoot(_tempRoot);
        guard.IsWithinAllowedRoot("").Should().BeFalse();
    }

    [Fact]
    public void AllowedRoot_ReflectsEnvVar()
    {
        CreateGuardWithRoot(_tempRoot).AllowedRoot.Should().NotBeNull();
        CreateGuardWithRoot(null).AllowedRoot.Should().BeNull();
    }
}
