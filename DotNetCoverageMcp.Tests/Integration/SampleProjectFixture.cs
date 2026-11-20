using System.Diagnostics;

namespace DotNetCoverageMcp.Tests.Integration;

/// <summary>
/// Copies Fixtures/SampleProject to a fresh temp directory and restores it once per test
/// class. Building in a copy keeps bin/obj out of the repo and gives each test class its own
/// build outputs: two `dotnet test` runs on the same project at once collide on obj/.
/// </summary>
public class SampleProjectFixture : IDisposable
{
    public string Root { get; }
    public string TestProjectPath => Path.Combine(Root, "SampleLib.Tests", "SampleLib.Tests.csproj");

    public SampleProjectFixture() : this(mtp: false) { }

    /// <param name="mtp">
    /// Switch the copy to Microsoft.Testing.Platform: the test project from Fixtures/Mtp and a
    /// global.json at the copy's root that puts `dotnet test` in MTP mode.
    /// </param>
    /// <param name="multiTarget">
    /// Target net8.0 as well as net10.0, with one test that only the net8.0 build compiles, so
    /// each framework's run covers something the other's doesn't. Needs the .NET 8 runtime.
    /// </param>
    protected SampleProjectFixture(bool mtp, bool multiTarget = false)
    {
        Root = Path.Combine(Path.GetTempPath(), $"coverage-mcp-e2e-{Guid.NewGuid():N}");
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SampleProject"), Root);
        if (mtp)
        {
            var mtpDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mtp");
            File.Copy(Path.Combine(mtpDir, "SampleLib.Tests.csproj"), TestProjectPath, overwrite: true);
            File.Copy(Path.Combine(mtpDir, "global.json"), Path.Combine(Root, "global.json"));
        }
        if (multiTarget)
        {
            foreach (var project in new[] { TestProjectPath, Path.Combine(Root, "SampleLib", "SampleLib.csproj") })
                File.WriteAllText(project, File.ReadAllText(project).Replace(
                    "<TargetFramework>net10.0</TargetFramework>", "<TargetFrameworks>net8.0;net10.0</TargetFrameworks>"));
            File.WriteAllText(Path.Combine(Root, "SampleLib.Tests", "Net8OnlyTests.cs"), """
                namespace SampleLib.Tests;

                public class Net8OnlyTests
                {
                #if NET8_0
                    [Fact]
                    public void Twice_OnNet8() => Assert.Equal(4, new Calculator().Twice(2));
                #endif
                }
                """);
        }
        RunDotnet("restore", TestProjectPath);
    }

    private static void RunDotnet(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {string.Join(' ', args)} timed out");
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"dotnet {string.Join(' ', args)} failed ({p.ExitCode}):\n{stdout.Result}\n{stderr.Result}");
    }

    internal static void CopyDirectory(string source, string target)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, target));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, target));
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }
}

/// <summary>Reports on the calling thread, so a test sees every report before the call returns.</summary>
public sealed class SyncProgress(Action<ModelContextProtocol.ProgressNotificationValue> onReport)
    : IProgress<ModelContextProtocol.ProgressNotificationValue>
{
    public void Report(ModelContextProtocol.ProgressNotificationValue value) => onReport(value);
}

public sealed class SyncStringProgress(Action<string> onReport) : IProgress<string>
{
    public void Report(string value) => onReport(value);
}
