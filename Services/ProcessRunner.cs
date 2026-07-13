using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace DotNetCoverageMcp.Services;

// Outcome of a test run, reported as a typed enum so consumers branch on a stable
// signal instead of string-matching the Error text (which used to carry sentinels).
public enum TestRunOutcome { Success, BuildError, Cancelled, Timeout }

public record TestRunResult(TestRunOutcome Outcome, string Output, string Error, int ExitCode, string? CoverageXmlPath);
public record ReportResult(bool Success, string? SummaryPath, string? ErrorDetail);

public interface IProcessRunner
{
    Task<TestRunResult> RunDotnetTestAsync(
        string testProjectPath, string filter, string resultsDir,
        string workingDir, bool forceRestore, string? includeClass, CancellationToken ct);

    Task<ReportResult> RunReportGeneratorAsync(
        string xmlPath, string reportDir, string workingDir, CancellationToken ct = default);
}

public class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner> _logger;
    private static readonly int ReportGeneratorTimeoutMs = ReadPositiveIntEnv("COVERAGE_MCP_REPORTGEN_TIMEOUT_MS", 60_000);
    private static readonly int DotnetTestTimeoutMs = ReadPositiveIntEnv("COVERAGE_MCP_DOTNET_TEST_TIMEOUT_MS", 600_000);
    // Per-test hang timeout passed to `dotnet test --blame-hang-timeout`. A single test
    // exceeding this aborts the whole run and writes a hang dump, so make it configurable:
    // raise it for legitimately long integration tests. Kept at 30s by default.
    private static readonly int HangTimeoutSeconds = ReadPositiveIntEnv("COVERAGE_MCP_HANG_TIMEOUT_SECONDS", 30);
    // Bounded wait for stdout/stderr to drain after the child process has been killed.
    // Long enough for the OS to flush the pipe buffer, short enough not to extend a timeout.
    private static readonly TimeSpan PostKillDrainWait = TimeSpan.FromSeconds(1);

    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        _logger = logger;
    }

    public async Task<TestRunResult> RunDotnetTestAsync(
        string testProjectPath, string filter, string resultsDir,
        string workingDir, bool forceRestore, string? includeClass, CancellationToken ct)
    {
        var filterOp = filter.Contains('.') ? "=" : "~";

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        // Use ArgumentList instead of string concatenation to prevent injection
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add(testProjectPath);
        if (!forceRestore) psi.ArgumentList.Add("--no-restore");
        psi.ArgumentList.Add("--blame-hang-timeout");
        psi.ArgumentList.Add($"{HangTimeoutSeconds}s");
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add($"FullyQualifiedName{filterOp}{filter}");
        psi.ArgumentList.Add("--collect:XPlat Code Coverage");
        psi.ArgumentList.Add("--results-directory");
        psi.ArgumentList.Add(resultsDir);

        // Scope coverage to matching types when the caller opts in. coverlet.collector
        // reads its Include filter from runsettings, not from an MSBuild /p: property (an
        // earlier version emitted /p:Include, which the collector silently ignored), so we
        // materialize a temporary runsettings file and pass it with --settings.
        string? runSettingsPath = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(includeClass))
            {
                runSettingsPath = WriteIncludeRunSettings(includeClass);
                psi.ArgumentList.Add("--settings");
                psi.ArgumentList.Add(runSettingsPath);
            }

            _logger.LogInformation("Starting dotnet test for {Project} with filter {Filter}", testProjectPath, filter);

            using var process = Process.Start(psi) ?? throw new Exception("Failed to start dotnet test");

            // Combine caller's token with an outer test timeout. Without this, a hung restore
            // or a locked NuGet cache would hang the MCP call until the client cancels.
            using var timeoutCts = new CancellationTokenSource(DotnetTestTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            // Register cancellation to kill the process immediately, before
            // awaiting stdout/stderr — otherwise ReadToEndAsync blocks until
            // the process exits and the cancellation token is never checked.
            using var ctReg = linkedCts.Token.Register(() =>
            {
                _logger.LogWarning("Test run cancelled/timed out, killing process tree for {Project}", testProjectPath);
                try { process.Kill(entireProcessTree: true); } catch { }
            });

            var outTask = process.StandardOutput.ReadToEndAsync();
            var errTask = process.StandardError.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync(linkedCts.Token);

            try
            {
                await exitTask;
            }
            catch (OperationCanceledException)
            {
                // Process already killed by ctReg callback — drain remaining output with a
                // bounded wait so we surface useful stderr without hanging on a stuck pipe.
                // The outcome is reported via TestRunOutcome; captured stderr is folded into
                // Output for diagnostics.
                var partialOut = await DrainAsync(outTask);
                var partialErr = await DrainAsync(errTask);
                var combined = string.IsNullOrEmpty(partialErr)
                    ? partialOut
                    : partialOut + "\n--- stderr ---\n" + partialErr;
                var outcome = ct.IsCancellationRequested ? TestRunOutcome.Cancelled : TestRunOutcome.Timeout;
                return new TestRunResult(outcome, combined, "", -1, null);
            }

            var output = await outTask;
            var error = await errTask;

            _logger.LogInformation("dotnet test exited with code {ExitCode} for {Project}", process.ExitCode, testProjectPath);

            string? coverageXmlPath = null;
            if (process.ExitCode == 0 && Directory.Exists(resultsDir))
            {
                var xmlPaths = Directory.GetFiles(resultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories);
                if (xmlPaths.Length > 0)
                    coverageXmlPath = xmlPaths[0];
            }

            var runOutcome = process.ExitCode == 0 ? TestRunOutcome.Success : TestRunOutcome.BuildError;
            return new TestRunResult(runOutcome, output, error, process.ExitCode, coverageXmlPath);
        }
        finally
        {
            // Best-effort cleanup of the temporary runsettings; the run is finished here.
            if (runSettingsPath != null)
            {
                try { File.Delete(runSettingsPath); } catch { }
            }
        }
    }

    // coverlet.collector reads its Include/Exclude filters from a runsettings file, not
    // from MSBuild /p: properties. To scope coverage to a class we write a minimal
    // runsettings file and pass it with --settings. Returns the temp path; the caller
    // deletes it once the run completes.
    internal static string WriteIncludeRunSettings(string includeClass)
    {
        var path = Path.Combine(Path.GetTempPath(), $"coverage-mcp-{Guid.NewGuid():N}.runsettings");
        new XDocument(
            new XElement("RunSettings",
                new XElement("DataCollectionRunSettings",
                    new XElement("DataCollectors",
                        new XElement("DataCollector",
                            new XAttribute("friendlyName", "XPlat Code Coverage"),
                            new XElement("Configuration",
                                new XElement("Include", $"[*]*{includeClass}")))))))
            .Save(path);
        return path;
    }

    private static int ReadPositiveIntEnv(string envVar, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        return int.TryParse(raw, out var v) && v > 0 ? v : defaultValue;
    }

    // Wait for a read task to complete after the process has been killed, up to the
    // bounded PostKillDrainWait. If it doesn't finish, observe its eventual exception
    // so it never surfaces as an UnobservedTaskException and return an empty string.
    internal static async Task<string> DrainAsync(Task<string> task) =>
        await DrainAsync(task, PostKillDrainWait);

    internal static async Task<string> DrainAsync(Task<string> task, TimeSpan wait)
    {
        try
        {
            return await task.WaitAsync(wait);
        }
        catch (TimeoutException)
        {
            _ = task.ContinueWith(
                t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return "";
        }
        catch
        {
            return "";
        }
    }

    public async Task<ReportResult> RunReportGeneratorAsync(
        string xmlPath, string reportDir, string workingDir, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "reportgenerator",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.ArgumentList.Add($"-reports:{xmlPath}");
        psi.ArgumentList.Add($"-targetdir:{reportDir}");
        psi.ArgumentList.Add("-reporttypes:JsonSummary");

        _logger.LogInformation("Starting reportgenerator for {XmlPath}", xmlPath);
        using var process = Process.Start(psi) ?? throw new Exception("Failed to start reportgenerator");
        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();

        // Combine caller's token with our timeout
        using var timeoutCts = new CancellationTokenSource(ReportGeneratorTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var ctReg = linkedCts.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Process was killed by ctReg — drain the read tasks so they can't surface
            // as UnobservedTaskException once `process` is disposed on return.
            await DrainAsync(outTask);
            await DrainAsync(errTask);
            return new ReportResult(false, null, "Report generation was cancelled.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("reportgenerator timed out after {Timeout}s, killing process", ReportGeneratorTimeoutMs / 1000);
            await DrainAsync(outTask);
            await DrainAsync(errTask);
            return new ReportResult(false, null, $"reportgenerator timed out after {ReportGeneratorTimeoutMs / 1000}s");
        }

        await outTask;
        await errTask;

        var summaryPath = Path.Combine(reportDir, "Summary.json");
        return File.Exists(summaryPath)
            ? new ReportResult(true, summaryPath, null)
            : new ReportResult(false, null, "Report generation failed");
    }
}
