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
    protected SampleProjectFixture(bool mtp)
    {
        Root = Path.Combine(Path.GetTempPath(), $"coverage-mcp-e2e-{Guid.NewGuid():N}");
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SampleProject"), Root);
        if (mtp)
        {
            var mtpDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mtp");
            File.Copy(Path.Combine(mtpDir, "SampleLib.Tests.csproj"), TestProjectPath, overwrite: true);
            File.Copy(Path.Combine(mtpDir, "global.json"), Path.Combine(Root, "global.json"));
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

    private static void CopyDirectory(string source, string target)
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
