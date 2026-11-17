using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace DotNetCoverageMcp.Services;

// Outcome of a test run, reported as a typed enum so consumers branch on a stable
// signal instead of string-matching the Error text (which used to carry sentinels).
public enum TestRunOutcome { Success, TestsFailed, NoTestsMatched, BuildError, MissingExtension, Cancelled, Timeout }

public record TestRunResult(
    TestRunOutcome Outcome, string Output, string Error, int ExitCode, string? CoverageXmlPath,
    TestSummary? Summary = null);
public record ReportResult(bool Success, string? SummaryPath, string? ErrorDetail);

public interface IProcessRunner
{
    Task<TestRunResult> RunDotnetTestAsync(
        string testProjectPath, string? filter, string resultsDir,
        string workingDir, bool forceRestore, string? includeClass, IProgress<string>? progress, CancellationToken ct);

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
        string testProjectPath, string? filter, string resultsDir,
        string workingDir, bool forceRestore, string? includeClass, IProgress<string>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        // Scope coverage to matching types when the caller opts in. coverlet.collector
        // reads its Include filter from runsettings, not from an MSBuild /p: property (an
        // earlier version emitted /p:Include, which the collector silently ignored), so we
        // materialize a temporary runsettings file and pass it with --settings.
        string? runSettingsPath = null;
        // global.json, looked up from where dotnet test starts, decides the command line.
        var mtp = TestEnvironmentDetector.DotnetTestUsesMtp(workingDir);
        try
        {
            if (!mtp && !string.IsNullOrWhiteSpace(includeClass))
                runSettingsPath = WriteIncludeRunSettings(includeClass);

            var args = mtp
                ? BuildMtpTestArguments(testProjectPath, filter, resultsDir, forceRestore, HasTrxExtension(testProjectPath))
                : BuildTestArguments(testProjectPath, filter, resultsDir, forceRestore, runSettingsPath);
            // ArgumentList instead of string concatenation to prevent injection.
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

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

            // stdout is read line by line so milestones (projects built, tests started, the
            // result line) can be reported while a long run is still going.
            var outTask = ReadLinesAsync(process.StandardOutput, progress);
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

            // Look for results whatever the exit code: a failing test exits non-zero but
            // still leaves coverage for every test that ran.
            string? coverageXmlPath = null;
            TestSummary? summary = null;
            if (Directory.Exists(resultsDir))
            {
                coverageXmlPath = FindCoverageXml(resultsDir);
                summary = TrxReader.Read(Directory.GetFiles(resultsDir, "*.trx", SearchOption.AllDirectories));
            }
            // Without the TrxReport extension, MTP's console summary still gives the counts.
            if (mtp) summary ??= ParseMtpSummary(output);

            var runOutcome = mtp ? ClassifyMtp(process.ExitCode, summary) : Classify(process.ExitCode, summary);
            return new TestRunResult(runOutcome, output, error, process.ExitCode, coverageXmlPath, summary);
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

    internal static List<string> BuildTestArguments(
        string testProjectPath, string? filter, string resultsDir, bool forceRestore, string? runSettingsPath)
    {
        var args = new List<string> { "test", testProjectPath };
        if (!forceRestore) args.Add("--no-restore");
        args.Add("--blame-hang-timeout");
        args.Add($"{HangTimeoutSeconds}s");

        // VSTest has no wildcard: `*` would be matched literally and select zero tests,
        // so "run everything" means passing no filter at all. Anything else goes through
        // unchanged. VSTest already reads a bare term as `FullyQualifiedName~term`, so
        // class names, dotted Class.Method names, `A|B` and `Category=Unit` all work.
        // (Prefixing `FullyQualifiedName=` for dotted names, as an earlier version did,
        // demanded the full namespace-qualified name and matched nothing.)
        if (!RunsAllTests(filter))
        {
            args.Add("--filter");
            args.Add(filter!.Trim());
        }

        args.Add("--collect:XPlat Code Coverage");
        // TRX gives exact counts and failure details; see TrxReader.
        args.Add("--logger");
        args.Add("trx");
        args.Add("--results-directory");
        args.Add(resultsDir);
        if (runSettingsPath != null)
        {
            args.Add("--settings");
            args.Add(runSettingsPath);
        }
        return args;
    }

    /// <summary>
    /// Command line for `dotnet test` in Microsoft.Testing.Platform mode (.NET 10 SDK+, chosen by
    /// global.json). The project is passed with --project; coverage comes from Microsoft's
    /// CodeCoverage extension. Extension options after `--` go to the test application, which
    /// rejects an option whose extension it does not reference (exit code 5), so --report-trx
    /// is only added when the TrxReport extension is there. --coverage is always added: the
    /// tool has nothing to report without it, and exit 5 names the package to add.
    /// </summary>
    internal static List<string> BuildMtpTestArguments(
        string testProjectPath, string? filter, string resultsDir, bool forceRestore, bool reportTrx)
    {
        var args = new List<string> { "test", "--project", testProjectPath };
        if (!forceRestore) args.Add("--no-restore");
        args.Add("--results-directory");
        args.Add(resultsDir);
        // MSTest, NUnit and xUnit v3 all accept the VSTest filter syntax under MTP.
        if (!RunsAllTests(filter))
        {
            args.Add("--filter");
            args.Add(filter!.Trim());
        }
        args.AddRange(["--", "--coverage", "--coverage-output-format", "cobertura"]);
        if (reportTrx) args.Add("--report-trx");
        return args;
    }

    // MTP exit codes: 0 success, 2 failed tests, 5 unknown option (extension not referenced),
    // 8 zero tests ran. 1 comes from MSBuild when the build fails.
    internal static TestRunOutcome ClassifyMtp(int exitCode, TestSummary? summary) => exitCode switch
    {
        5 => TestRunOutcome.MissingExtension,
        8 => TestRunOutcome.NoTestsMatched,
        _ when summary != null => Classify(exitCode, summary),
        0 => TestRunOutcome.Success,
        2 => TestRunOutcome.TestsFailed,
        _ => TestRunOutcome.BuildError
    };

    private static readonly System.Text.RegularExpressions.Regex MtpCountRx =
        new(@"^\s*(total|failed|succeeded|skipped):\s*(\d+)\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Counts from MTP's "Test run summary" block; null when no test ran.</summary>
    internal static TestSummary? ParseMtpSummary(string output)
    {
        var counts = MtpCountRx.Matches(output)
            .GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => int.Parse(g.Last().Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        if (!counts.TryGetValue("total", out var total)) return null;
        return new TestSummary(total, counts.GetValueOrDefault("succeeded"), counts.GetValueOrDefault("failed"),
            counts.GetValueOrDefault("skipped"), []);
    }

    // coverlet writes <guid>/coverage.cobertura.xml; Microsoft's extension writes
    // <guid>.cobertura.xml at the top and a copy deeper down. Take the shallowest.
    internal static string? FindCoverageXml(string resultsDir) =>
        Directory.GetFiles(resultsDir, "*.cobertura.xml", SearchOption.AllDirectories)
            .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
            .ThenBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();

    private static bool HasTrxExtension(string testProjectPath)
    {
        try
        {
            return TestEnvironmentDetector.Detect(testProjectPath).MtpExtensions
                .Contains("Microsoft.Testing.Extensions.TrxReport", StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    // No TRX means the run never reached test execution, i.e. the build failed. With TRX,
    // zero tests is reported on its own: dotnet test exits 0 for it and coverlet still
    // writes an all-zero report, which used to look like a successful run.
    internal static TestRunOutcome Classify(int exitCode, TestSummary? summary) => summary switch
    {
        null => exitCode == 0 ? TestRunOutcome.Success : TestRunOutcome.BuildError,
        { Total: 0 } => TestRunOutcome.NoTestsMatched,
        { Failed: > 0 } => TestRunOutcome.TestsFailed,
        // Non-zero exit with no failed test: the run was aborted, e.g. by the hang timeout.
        _ => exitCode == 0 ? TestRunOutcome.Success : TestRunOutcome.TestsFailed
    };

    private static async Task<string> ReadLinesAsync(StreamReader reader, IProgress<string>? progress)
    {
        var output = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            output.AppendLine(line);
            if (progress != null && ProgressMessage(line) is { } message)
                progress.Report(message);
        }
        return output.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex BuiltRx =
        new(@"^\s+(\S+) -> .+\.(?:dll|exe)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex TestsStartedRx =
        new(@"^(?:Test run for|Running tests from) (.+?\.(?:dll|exe))", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The progress message for a line of `dotnet test` output, or null for the many lines
    /// that are not milestones. Shapes are from real VSTest and MTP runs.
    /// </summary>
    internal static string? ProgressMessage(string line)
    {
        if (line.StartsWith("  Determining projects to restore", StringComparison.Ordinal)
            || line.StartsWith("Determining projects to restore", StringComparison.Ordinal))
            return "Restoring packages";
        if (BuiltRx.Match(line) is { Success: true } built) return $"Built {built.Groups[1].Value}";
        if (TestsStartedRx.Match(line) is { Success: true } started)
            return $"Running tests in {Path.GetFileName(started.Groups[1].Value)}";
        var trimmed = line.Trim();
        if (trimmed.StartsWith("Passed!", StringComparison.Ordinal) || trimmed.StartsWith("Failed!", StringComparison.Ordinal)
            || trimmed.StartsWith("Test run summary:", StringComparison.Ordinal))
            return trimmed.Length <= 160 ? trimmed : trimmed[..160];
        return null;
    }

    internal static bool RunsAllTests(string? filter) =>
        string.IsNullOrWhiteSpace(filter) || filter.Trim() == "*";

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
