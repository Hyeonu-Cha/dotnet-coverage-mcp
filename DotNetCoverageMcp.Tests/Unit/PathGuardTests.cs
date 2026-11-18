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
    public void Validate_FolderDifferingOnlyInCase_IsOutsideTheRootOnLinux()
    {
        // Regression: the check ignored case everywhere, so on Linux a root of .../repo also
        // admitted .../REPO, a different folder. Windows and macOS file systems ignore case,
        // so there it is the same folder and stays allowed.
        var root = Path.Combine(_tempRoot, "repo");
        var caseVariant = Path.Combine(_tempRoot, "REPO");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(caseVariant);
        var guard = CreateGuardWithRoot(root);

        var allowed = guard.IsWithinAllowedRoot(Path.Combine(caseVariant, "file.cs"));

        allowed.Should().Be(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
    }

    [Fact]
    public void Validate_SymlinkEscapingRoot_Throws()
    {
        // A link INSIDE the root that points OUTSIDE it must not pass the guard: the lexical
        // path (root/link/secret.cs) looks contained, but it reads an arbitrary file.
        var outside = Path.Combine(Path.GetTempPath(), $"pg-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.cs"), "class S { }");
        var link = Path.Combine(_tempRoot, "link");
        try
        {
            if (!TryCreateDirectorySymlink(link, outside)) return; // no symlink privilege (e.g. Windows CI)
            var guard = CreateGuardWithRoot(_tempRoot);

            var act = () => guard.Validate(Path.Combine(link, "secret.cs"), "p");

            act.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            try { Directory.Delete(link); } catch { /* remove the link, not its target */ }
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public void Validate_SymlinkWithinRoot_Allowed()
    {
        // A link inside the root that points to another location inside the root is fine.
        var realDir = Path.Combine(_tempRoot, "real");
        Directory.CreateDirectory(realDir);
        File.WriteAllText(Path.Combine(realDir, "f.cs"), "class F { }");
        var link = Path.Combine(_tempRoot, "linkin");
        if (!TryCreateDirectorySymlink(link, realDir)) return;

        var guard = CreateGuardWithRoot(_tempRoot);

        var act = () => guard.Validate(Path.Combine(link, "f.cs"), "p");

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_SymlinkWhoseTargetPrefixIsItselfASymlink_Allowed()
    {
        // Reproduces the macOS case: the allowed root reaches the repo through a symlinked
        // ancestor (/var -> /private/var), and a link inside the root records its target via
        // that same symlinked prefix. Resolving only the link — not its target's prefix — left
        // the two sides as /var/... vs /private/var/... and wrongly rejected an in-root file.
        var baseDir = Path.Combine(Path.GetTempPath(), $"pg-prefix-{Guid.NewGuid():N}");
        var realMount = Path.Combine(baseDir, "realmount");
        Directory.CreateDirectory(Path.Combine(realMount, "pg", "real"));
        File.WriteAllText(Path.Combine(realMount, "pg", "real", "f.cs"), "class F { }");

        var mount = Path.Combine(baseDir, "mount");                     // ancestor symlink mount -> realmount
        if (!TryCreateDirectorySymlink(mount, realMount)) return;
        var linkin = Path.Combine(mount, "pg", "linkin");              // target recorded via `mount`
        try
        {
            if (!TryCreateDirectorySymlink(linkin, Path.Combine(mount, "pg", "real"))) return;
            var guard = CreateGuardWithRoot(Path.Combine(mount, "pg")); // root reached via the symlinked prefix

            var act = () => guard.Validate(Path.Combine(mount, "pg", "linkin", "f.cs"), "p");

            act.Should().NotThrow();
        }
        finally
        {
            try { Directory.Delete(linkin); } catch { }
            try { Directory.Delete(mount); } catch { }
            try { Directory.Delete(baseDir, true); } catch { }
        }
    }

    [Fact]
    public void IsWithinAllowedRoot_EmptyPath_ReturnsFalse()
    {
        var guard = CreateGuardWithRoot(_tempRoot);
        guard.IsWithinAllowedRoot("").Should().BeFalse();
    }

    // Creates a directory symlink, or returns false when the platform refuses (Windows without
    // Developer Mode / admin). Lets the symlink tests run on Linux/macOS CI and skip elsewhere.
    private static bool TryCreateDirectorySymlink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public void AllowedRoot_ReflectsEnvVar()
    {
        CreateGuardWithRoot(_tempRoot).AllowedRoot.Should().NotBeNull();
        CreateGuardWithRoot(null).AllowedRoot.Should().BeNull();
    }
}
