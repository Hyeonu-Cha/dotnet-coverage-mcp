using System.Diagnostics;
using DotNetCoverageMcp.Services;
using FluentAssertions;

namespace DotNetCoverageMcp.Tests.Unit;

/// <summary>
/// The resolver behind the path guard's containment check. Links are junctions on Windows (no
/// privilege needed, unlike symlinks) and symlinks elsewhere.
/// </summary>
public class SymlinkResolverTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"slr-{Guid.NewGuid():N}")).FullName;
    private readonly List<string> _links = [];

    public void Dispose()
    {
        // Links first, one by one, so nothing is deleted through them.
        foreach (var link in _links) try { Directory.Delete(link); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private bool TryLink(string link, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo("cmd.exe")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
                };
                foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                p.WaitForExit(10_000);
                if (p.ExitCode != 0) return false;
            }
            else
            {
                Directory.CreateSymbolicLink(link, target);
            }
            _links.Add(link);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public void ALinkedSegment_IsReplacedByItsTarget_ThePathAfterItKept()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "outside")).FullName;
        var link = Path.Combine(_dir, "inside", "link");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!TryLink(link, outside)) return;

        // Resolved, the base itself may differ: macOS's temp folder is under /var -> /private/var.
        var real = SymlinkResolver.Resolve(outside);
        SymlinkResolver.Resolve(Path.Combine(link, "sub", "secret.cs")).Should().Be(Path.Combine(real, "sub", "secret.cs"));
        SymlinkResolver.Resolve(link).Should().Be(real);
    }

    [Fact]
    public void ADanglingLink_StillResolvesToWhereItPoints()
    {
        // A missing target used to be the way past a check that only looked at existing paths.
        var gone = Path.Combine(_dir, "gone");
        Directory.CreateDirectory(gone);
        var link = Path.Combine(_dir, "dangling");
        if (!TryLink(link, gone)) return;
        Directory.Delete(gone);

        SymlinkResolver.Resolve(Path.Combine(link, "new.cs")).Should().Be(Path.Combine(SymlinkResolver.Resolve(_dir), "gone", "new.cs"));
    }

    [Fact]
    public void LinksPointingAtEachOther_DoNotLoopForever()
    {
        var a = Path.Combine(_dir, "a");
        var b = Path.Combine(_dir, "b");
        if (!TryLink(a, b) || !TryLink(b, a)) return;

        var resolved = SymlinkResolver.Resolve(Path.Combine(a, "file.cs"));

        Path.IsPathFullyQualified(resolved).Should().BeTrue(resolved);
    }

    [Fact]
    public void APathWithoutLinks_IsItsFullPath() =>
        SymlinkResolver.Resolve(Path.Combine(_dir, ".", "x", "..", "y.cs")).Should().Be(Path.Combine(SymlinkResolver.Resolve(_dir), "y.cs"));
}
