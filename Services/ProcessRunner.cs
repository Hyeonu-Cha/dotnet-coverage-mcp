using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CoverageMcpServer.Services;

public record TestRunResult(bool Success, string Output, string Error, int ExitCode, string? CoverageXmlPath);
public record ReportResult(bool Success, string? SummaryPath, string? ErrorDetail);

public interface IProcessRunner
{
    Task<TestRunResult> RunDotnetTestAsync(
        string testProjectPath, string filter, string resultsDir,
        string workingDir, bool forceRestore, CancellationToken ct);

    Task<ReportResult> RunReportGeneratorAsync(
        string xmlPath, string reportDir, string workingDir, CancellationToken ct = default);
}

public class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner> _logger;
    private const int ReportGeneratorTimeoutMs = 60_000;

    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        _logger = logger;
    }

    public async Task<TestRunResult> RunDotnetTestAsync(
        string testProjectPath, string filter, string resultsDir,
        string workingDir, bool forceRestore, CancellationToken ct)
    {
        var filterOp = filter.Contains('.') ? "=" : "~";
        var className = StripTestSuffix(filter.Split('.').Last());

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
        psi.ArgumentList.Add("30s");
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add($"FullyQualifiedName{filterOp}{filter}");
        psi.ArgumentList.Add("--collect:XPlat Code Coverage");
        psi.ArgumentList.Add("--results-directory");
        psi.ArgumentList.Add(resultsDir);

        if (!filter.Contains('*') && !filter.Contains(','))
            psi.ArgumentList.Add($"/p:Include=[*]*{className}");

        _logger.LogInformation("Starting dotnet test for {Project} with filter {Filter}", testProjectPath, filter);
        using var process = Process.Start(psi) ?? throw new Exception("Failed to start dotnet test");

        // Register cancellation to kill the process immediately, before
        // awaiting stdout/stderr — otherwise ReadToEndAsync blocks until
        // the process exits and the cancellation token is never checked.
        using var ctReg = ct.Register(() =>
        {
            _logger.LogWarning("Test run cancelled, killing process tree for {Project}", testProjectPath);
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync(ct);

        try
        {
            await exitTask;
        }
        catch (OperationCanceledException)
        {
            // Process already killed by ctReg callback — drain remaining output
            var partialOut = outTask.IsCompleted ? await outTask : "";
            var partialErr = errTask.IsCompleted ? await errTask : "";
            return new TestRunResult(false, partialOut, "cancelled", -1, null);
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

        return new TestRunResult(process.ExitCode == 0, output, error, process.ExitCode, coverageXmlPath);
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
            return new ReportResult(false, null, "Report generation was cancelled.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("reportgenerator timed out after {Timeout}s, killing process", ReportGeneratorTimeoutMs / 1000);
            return new ReportResult(false, null, $"reportgenerator timed out after {ReportGeneratorTimeoutMs / 1000}s");
        }

        await outTask;
        await errTask;

        var summaryPath = Path.Combine(reportDir, "Summary.json");
        return File.Exists(summaryPath)
            ? new ReportResult(true, summaryPath, null)
            : new ReportResult(false, null, "Report generation failed");
    }

    internal static string EscapeProcessArg(string arg)
    {
        var escaped = arg.Replace("\"", "\\\"");
        if (escaped.EndsWith('\\'))
            escaped += "\\";
        return "\"" + escaped + "\"";
    }

    internal static string StripTestSuffix(string className)
    {
        if (string.IsNullOrEmpty(className)) return className;
        if (className.EndsWith("IntegrationTests")) return className[..^16];
        if (className.EndsWith("UnitTests")) return className[..^9];
        if (className.EndsWith("Tests")) return className[..^5];
        if (className.EndsWith("Specs")) return className[..^5];
        if (className.EndsWith("Spec")) return className[..^4];
        if (className.EndsWith("Test")) return className[..^4];
        return className;
    }
}
