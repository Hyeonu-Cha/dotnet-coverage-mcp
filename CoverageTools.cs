using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using DotNetCoverageMcp.Helpers;
using DotNetCoverageMcp.Services;

namespace DotNetCoverageMcp;

[McpServerToolType]
public class CoverageTools
{
    private readonly IProcessRunner _processRunner;
    private readonly ISessionManager _sessionManager;
    private readonly IFileService _fileService;
    private readonly ICoberturaService _coberturaService;
    private readonly ICodeInserter _codeInserter;
    private readonly IPathGuard _pathGuard;

    public CoverageTools(
        IProcessRunner processRunner,
        ISessionManager sessionManager,
        IFileService fileService,
        ICoberturaService coberturaService,
        ICodeInserter codeInserter,
        IPathGuard pathGuard)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(fileService);
        ArgumentNullException.ThrowIfNull(coberturaService);
        ArgumentNullException.ThrowIfNull(codeInserter);
        ArgumentNullException.ThrowIfNull(pathGuard);

        _processRunner = processRunner;
        _sessionManager = sessionManager;
        _fileService = fileService;
        _coberturaService = coberturaService;
        _codeInserter = codeInserter;
        _pathGuard = pathGuard;
    }

    // --- 1. RunTestsWithCoverage ---

    [McpServerTool(Title = "Run tests with coverage", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Run `dotnet test` with code coverage. Returns pass/fail/skip counts and the path to the Cobertura XML that the other coverage tools read. Uses coverlet (XPlat Code Coverage) on VSTest, or Microsoft's CodeCoverage extension when the repository's global.json selects Microsoft.Testing.Platform.")]
    public async Task<string> RunTestsWithCoverage(
        [Description("Absolute path to the .csproj test project. Must be inside COVERAGE_MCP_ALLOWED_ROOT when that environment variable is set.")]
        string testProjectPath,
        [Description("Optional test filter, passed to `dotnet test --filter`. A plain name matches any test whose fully qualified name contains it (e.g. 'OrderServiceTests' or 'OrderServiceTests.PlacesOrder'). Full VSTest expressions also work ('OrderTests|PaymentTests', 'Category=Unit'). Omit, or pass '*', to run every test in the project.")]
        string? filter = null,
        [Description("Working directory for the dotnet test invocation. Defaults to the test project's directory when omitted.")]
        string? workingDir = null,
        [Description("When true, omits --no-restore. Set after scaffolding a new test project or adding NuGet references; otherwise leave false for faster runs.")]
        bool forceRestore = false,
        [Description("Optional opaque token (any string) used to isolate output artifacts (TestResults-{hash}/ and the coverage state) so concurrent agents don't trample each other. Omit for single-agent use; results then go to TestResults-mcp/.")]
        string? sessionId = null,
        [Description("Optional class-name pattern, VSTest/coverlet only, that restricts coverage collection to types matching this name (coverlet Include filter, e.g. 'OrderService' or 'Order*'), applied via a generated runsettings file. Independent of `filter`; omit to collect coverage for everything the run touches. Namespace-qualified names are not supported.")]
        string? includeClass = null,
        // Bound by the SDK, not part of the tool schema: forwards progress notifications when
        // the client sent a progress token, so a long build/test run doesn't look hung.
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(testProjectPath))
            return JsonHelper.Error("invalidParameter", "testProjectPath must not be empty.");

        // Resolve to a rooted path before anything else uses it. `dotnet test` takes the project
        // as its first positional argument, so a raw value like "--help" or "-bl:evil.binlog"
        // would be parsed as a CLI/MSBuild option (and could run arbitrary build logic or write
        // files anywhere). Rooting handles the "-" forms; the existence check below handles
        // Unix, where "/p:X=y.csproj" is already rooted and dotnet still reads it as a switch.
        string resolvedProjectPath;
        try { resolvedProjectPath = Path.GetFullPath(testProjectPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return JsonHelper.Error("invalidParameter", $"testProjectPath is not a valid file path: {ex.Message}");
        }
        if (!resolvedProjectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return JsonHelper.Error("invalidParameter", "testProjectPath must be a .csproj file.");
        testProjectPath = resolvedProjectPath;

        try
        {
            _pathGuard.Validate(testProjectPath, nameof(testProjectPath));
            if (workingDir != null) _pathGuard.Validate(workingDir, nameof(workingDir));
        }
        catch (UnauthorizedAccessException ex)
        {
            return JsonHelper.Error("pathNotAllowed", ex.Message);
        }

        if (!File.Exists(testProjectPath))
            return JsonHelper.Error("fileNotFound", $"Test project not found: {testProjectPath}");

        workingDir ??= Path.GetDirectoryName(testProjectPath) ?? Directory.GetCurrentDirectory();

        // includeClass is a coverlet runsettings filter; Microsoft's extension used under MTP
        // has a different settings format, so refuse rather than silently ignore it.
        if (!string.IsNullOrWhiteSpace(includeClass) && TestEnvironmentDetector.DotnetTestUsesMtp(workingDir))
            return JsonHelper.Error("invalidParameter",
                "includeClass applies to coverlet on VSTest only; this repository runs dotnet test on Microsoft.Testing.Platform (global.json). Omit includeClass.");

        // Never the bare "TestResults": plain `dotnet test` and Visual Studio write there by
        // default, and this folder is deleted at the start of every run.
        var suffix = _sessionManager.ComputeSuffix(sessionId);
        var resultsDir = Path.Combine(workingDir, "TestResults" + (suffix.Length > 0 ? suffix : "-mcp"));

        _fileService.SafeDelete(resultsDir);

        var steps = progress == null ? null : new StepProgress(progress);
        steps?.Report($"Starting dotnet test for {Path.GetFileName(testProjectPath)}");

        var testResult = await _processRunner.RunDotnetTestAsync(
            testProjectPath, filter, resultsDir, workingDir, forceRestore, includeClass, steps, cancellationToken);

        switch (testResult.Outcome)
        {
            case TestRunOutcome.Cancelled:
                return JsonHelper.Error("cancelled", "Test run was cancelled by the client.");
            case TestRunOutcome.Timeout:
                return JsonHelper.Error("timeout", $"Test run timed out. Output (tail): {Tail(testResult.Output)}");
            case TestRunOutcome.BuildError when LockedOutputDetail(testResult.Output, testResult.Error) is { } locked:
                return JsonHelper.Error("projectBusy",
                    "The build could not write its output files because another process holds them: another " +
                    "dotnet test or build of this project, an IDE build, or a test host still running. The test " +
                    $"code is not the problem; run again once that process has finished.\n{locked}");
            case TestRunOutcome.BuildError:
                return JsonHelper.Error("buildError",
                    $"Build failed (exit code {testResult.ExitCode}).\n{BuildErrorDetail(testResult.Output, testResult.Error)}");
            case TestRunOutcome.NoTestsMatched:
                return JsonHelper.Error("noTestsMatched", ProcessRunner.RunsAllTests(filter)
                    ? "The test project contains no tests."
                    : $"No tests matched filter '{filter}'. A plain name matches any test whose fully qualified name contains it; omit filter to run every test.");
            case TestRunOutcome.MissingExtension:
                // Microsoft.Testing.Platform rejects an option whose extension the test project
                // doesn't reference, and its message names the package to add.
                return JsonHelper.Error("missingExtension", MissingExtensionDetail(testResult.Output, testResult.Error));
        }

        // The filter keys a state file; "run all" gets one stable key however it was spelled.
        var stateKey = ProcessRunner.RunsAllTests(filter) ? "*" : filter!;

        if (testResult.Outcome == TestRunOutcome.TestsFailed)
        {
            // Coverage of the tests that did run is still valid, so keep it reachable for the
            // per-file tools. The report step is skipped: the agent's next move is fixing tests.
            if (testResult.CoverageXmlPath != null)
                _sessionManager.SaveCoverageState(workingDir, testResult.CoverageXmlPath, stateKey, sessionId);
            var s = testResult.Summary;
            return JsonHelper.Serialize(new
            {
                error = s == null
                    ? $"Test run failed (exit code {testResult.ExitCode})."
                    : $"{s.Failed} of {s.Total} tests failed.",
                errorType = "testsFailed",
                passed = s?.Passed,
                failed = s?.Failed,
                skipped = s?.Skipped,
                failures = s?.Failures,
                // MTP without the TrxReport extension reports counts but no per-test details.
                hint = s is { Failed: > 0, Failures.Count: 0 }
                    ? "Add the Microsoft.Testing.Extensions.TrxReport package to the test project to get each failure's message and stack."
                    : null,
                coberturaXmlPath = testResult.CoverageXmlPath
            });
        }

        if (testResult.CoverageXmlPath == null)
            return JsonHelper.Error("noCoverage", "No coverage XML found.\n" + Tail(testResult.Output));

        _sessionManager.SaveCoverageState(workingDir, testResult.CoverageXmlPath, stateKey, sessionId);

        var counts = testResult.Summary is { } sum
            ? $" Passed: {sum.Passed}, Failed: {sum.Failed}, Skipped: {sum.Skipped}."
            : "";

        return $"Tests completed.{counts}\nCobertura XML at: {testResult.CoverageXmlPath}";
    }

    // --- 2. GetCoverageSummary ---

    [McpServerTool(Title = "Coverage summary", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Summarise class/method coverage from Cobertura XML, sorted worst-first by branch coverage. Optional belowTarget/topN/methodsPerClass filters trim the response to the classes and methods that still need work.")]
    public string GetCoverageSummary(
        [Description("Absolute path to coverage.cobertura.xml (returned by RunTestsWithCoverage). Falls back to the session-scoped path stored in .mcp-coverage/.coverage-state when the file is not found at this path.")]
        string coberturaXmlPath,
        [Description("Optional. When set (a fraction in [0,1], e.g. 0.8), return only classes whose line OR branch coverage is below this threshold — the ones that still need work. Omit to return every class.")]
        double? belowTarget = null,
        [Description("Optional. Return only the N classes with the lowest branch coverage (results are sorted worst-first). Omit to return every class.")]
        int? topN = null,
        [Description("Optional. Keep at most this many lowest-branch-coverage methods per class, trimming the rest. Omit to keep every method.")]
        int? methodsPerClass = null,
        [Description("Optional session token used to resolve the session-scoped .coverage-state-{hash} fallback. Omit for single-agent use.")]
        string? sessionId = null)
    {
        try { _pathGuard.Validate(coberturaXmlPath, nameof(coberturaXmlPath)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        if (belowTarget is < 0.0 or > 1.0)
            return JsonHelper.Error("invalidParameter", "belowTarget must be between 0.0 and 1.0.");
        if (topN is <= 0)
            return JsonHelper.Error("invalidParameter", "topN must be greater than 0.");
        if (methodsPerClass is < 0)
            return JsonHelper.Error("invalidParameter", "methodsPerClass must be non-negative.");

        var resolvedPath = _sessionManager.ResolveCoberturaPath(coberturaXmlPath, sessionId);
        if (resolvedPath == null)
            return JsonHelper.Error("fileNotFound", "Cobertura XML not found and no .coverage-state fallback available.");

        try
        {
            // Sort worst-first so topN yields the classes most in need of tests.
            IEnumerable<SummaryClass> classes = _coberturaService.SummarizeClasses(resolvedPath)
                .OrderBy(c => c.BranchCoverage).ThenBy(c => c.LineCoverage);

            if (belowTarget is double target)
                classes = classes.Where(c => c.LineCoverage < target || c.BranchCoverage < target);
            if (topN is int n)
                classes = classes.Take(n);

            var list = classes.ToList();
            if (methodsPerClass is int mpc)
                list = list.Select(c => c with { Methods = c.Methods.Take(mpc).ToList() }).ToList();

            return JsonHelper.Serialize(list);
        }
        catch (Exception ex)
        {
            return JsonHelper.Error("parseFailed", $"Failed to parse Cobertura XML: {ex.Message}");
        }
    }

    // --- 3. GetUncoveredBranches ---

    [McpServerTool(Title = "Uncovered branches of a method", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List uncovered branches for methods matching `methodName` in Cobertura XML (partial match supported).")]
    public string GetUncoveredBranches(
        [Description("Absolute path to coverage.cobertura.xml. Falls back to the session-scoped path stored in .mcp-coverage/.coverage-state when the file is not found at this path.")]
        string coberturaXmlPath,
        [Description("Method name to inspect. Partial matches are returned; pass the simple name (no parameters or return type).")]
        string methodName,
        [Description("Optional session token used to resolve the session-scoped .coverage-state-{hash} fallback. Omit for single-agent use.")]
        string? sessionId = null)
    {
        try { _pathGuard.Validate(coberturaXmlPath, nameof(coberturaXmlPath)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        var resolvedPath = _sessionManager.ResolveCoberturaPath(coberturaXmlPath, sessionId);
        if (resolvedPath == null)
            return JsonHelper.Error("fileNotFound", "Cobertura XML not found and no .coverage-state fallback available.");

        try
        {
            var result = _coberturaService.GetUncoveredBranches(resolvedPath, methodName);
            return JsonHelper.Serialize(result);
        }
        catch (KeyNotFoundException ex)
        {
            return JsonHelper.Error("noMatch", ex.Message);
        }
        catch (Exception ex)
        {
            return JsonHelper.Error("parseFailed", $"Failed to parse Cobertura XML: {ex.Message}");
        }
    }

    // --- 4. AppendTestCode ---

    [McpServerTool(Title = "Append test code", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Insert C# code into a test file via Roslyn AST (string fallback). By default the code goes into the file's test class (the last top-level class with test attributes); pass `targetClass` or `insertAfterAnchor` to choose. Code that is not valid C# member syntax is rejected with `syntaxError` and the file is left unchanged.")]
    public async Task<string> AppendTestCode(
        [Description("Absolute path to the target .cs test file. Must already exist; the file is rewritten atomically.")]
        string testFilePath,
        [Description("C# code fragment to insert. Pass member declarations (test methods/fields). Leading `using` lines are moved to the top of the file; do not include `namespace` or class declarations.")]
        string codeToAppend,
        [Description("If set, code is inserted after the last member containing this exact string (whitespace-tolerant), in whichever class holds it. When omitted, code is appended at the end of the target class.")]
        string? insertAfterAnchor = null,
        [Description("Optional simple class name to insert into (e.g. 'OrderServiceTests'), including nested classes. Omit to use the file's test class: the last top-level class with [Fact]/[Theory]/[Test]/[TestCase]/[TestMethod]/[TestFixture]/[TestClass], or the last top-level class if none has them.")]
        string? targetClass = null)
    {
        try { _pathGuard.Validate(testFilePath, nameof(testFilePath)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        // Only C# source files. Without this the string fallback would happily append "test
        // code" into any existing file the path points at (settings.json, a .csv), corrupting
        // it and reporting success. Which .cs file is a test file is still the caller's call.
        if (!testFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return JsonHelper.Error("invalidParameter", "testFilePath must be a .cs file.");

        if (!File.Exists(testFilePath))
            return JsonHelper.Error("fileNotFound", $"Test file not found: {testFilePath}");

        try
        {
            var result = await _codeInserter.InsertCodeAsync(testFilePath, codeToAppend, insertAfterAnchor,
                string.IsNullOrWhiteSpace(targetClass) ? null : targetClass.Trim());
            var methodLabel = result.Method switch
            {
                InsertionMethod.RoslynAst => "Roslyn AST",
                InsertionMethod.StringFallback => "string fallback",
                InsertionMethod.StringFallbackNormalized => "string fallback, whitespace-normalized",
                InsertionMethod.Appended => "string fallback, inside last class",
                _ => "unknown"
            };
            return $"Successfully inserted via {methodLabel} in {testFilePath}.\nNew file length: {result.Content.Length} chars";
        }
        catch (KeyNotFoundException ex)
        {
            return JsonHelper.Error("anchorNotFound", ex.Message);
        }
        catch (CodeSyntaxException ex)
        {
            return JsonHelper.Error("syntaxError", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return JsonHelper.Error("insertFailed", ex.Message);
        }
    }

    // --- 5. GetCoverageDiff ---

    [McpServerTool(Title = "Coverage diff since last call", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Diff current coverage against the saved baseline (method-level changes, including added/removed). Updates the baseline afterwards.")]
    public string GetCoverageDiff(
        [Description("Absolute path to the current coverage.cobertura.xml. Falls back to the session-scoped path stored in .mcp-coverage/.coverage-state when the file is not found at this path.")]
        string coberturaXmlPath,
        [Description("Project root used to store the baseline (.mcp-coverage/.coverage-prev.xml). Defaults to a parent directory of the coverage XML, walking past TestResults/coveragereport/guid folders.")]
        string? workingDir = null,
        [Description("Optional session token. When set, the baseline is stored as .coverage-prev-{hash}.xml so concurrent agents keep separate baselines.")]
        string? sessionId = null)
    {
        try
        {
            _pathGuard.Validate(coberturaXmlPath, nameof(coberturaXmlPath));
            if (workingDir != null) _pathGuard.Validate(workingDir, nameof(workingDir));
        }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        var resolvedPath = _sessionManager.ResolveCoberturaPath(coberturaXmlPath, sessionId);
        if (resolvedPath == null)
            return JsonHelper.Error("fileNotFound", "Cobertura XML not found and no .coverage-state fallback available.");

        if (workingDir == null)
        {
            workingDir = ResolveProjectRoot(resolvedPath);
            if (workingDir == null)
                return JsonHelper.Error("resolveFailed",
                    "Could not resolve project root from coverage XML path. Pass workingDir explicitly.");
        }

        try { _pathGuard.Validate(workingDir, nameof(workingDir)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        var stateDir = Path.Combine(workingDir, ".mcp-coverage");
        Directory.CreateDirectory(stateDir);
        var suffix = _sessionManager.ComputeSuffix(sessionId);
        var prevPath = Path.Combine(stateDir, $".coverage-prev{suffix}.xml");

        try
        {
            if (!File.Exists(prevPath))
            {
                _fileService.AtomicCopyFile(resolvedPath, prevPath);
                return JsonHelper.Serialize(new { firstRun = true });
            }

            var result = _coberturaService.ComputeDiff(resolvedPath, prevPath);
            _fileService.AtomicCopyFile(resolvedPath, prevPath);
            return JsonHelper.Serialize(result);
        }
        catch (Exception ex)
        {
            return JsonHelper.Error("parseFailed", $"Failed to parse coverage XML: {ex.Message}");
        }
    }

    // --- 6. GetFileCoverage ---

    [McpServerTool(Title = "Coverage of one file", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Per-file class/method coverage from Cobertura XML. `allMeetTarget` is true when every class meets `targetRate` for line and branch.")]
    public string GetFileCoverage(
        [Description("Absolute path to coverage.cobertura.xml. Falls back to the session-scoped path stored in .mcp-coverage/.coverage-state when the file is not found at this path.")]
        string coberturaXmlPath,
        [Description("Source file to look up: an absolute path (as returned by GetSourceFiles), a repo-relative path ('Services/ExampleService.cs'), or a bare file name ('ExampleService.cs'). Matched on whole path segments. If a bare name matches files in several folders, an `ambiguous` error lists them; pass a longer path.")]
        string sourceFileName,
        [Description("Optional session token used to resolve the session-scoped .coverage-state-{hash} fallback. Omit for single-agent use.")]
        string? sessionId = null,
        [Description("Coverage threshold as a fraction in [0.0, 1.0] (e.g. 0.8 = 80%). Pass 0.8 — not 80. Default 0.8. Out-of-range values are rejected.")]
        double targetRate = 0.8)
    {
        try { _pathGuard.Validate(coberturaXmlPath, nameof(coberturaXmlPath)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        if (targetRate < 0.0 || targetRate > 1.0)
            return JsonHelper.Error("invalidParameter", "targetRate must be between 0.0 and 1.0.");

        var resolvedPath = _sessionManager.ResolveCoberturaPath(coberturaXmlPath, sessionId);
        if (resolvedPath == null)
            return JsonHelper.Error("fileNotFound", "Cobertura XML not found and no .coverage-state fallback available.");

        try
        {
            var result = _coberturaService.GetFileCoverage(resolvedPath, sourceFileName, targetRate);
            return JsonHelper.Serialize(result);
        }
        catch (KeyNotFoundException ex)
        {
            return JsonHelper.Error("noMatch", ex.Message);
        }
        catch (AmbiguousFileException ex)
        {
            return JsonHelper.Serialize(new { error = ex.Message, errorType = "ambiguous", candidates = ex.Candidates });
        }
        catch (Exception ex)
        {
            return JsonHelper.Error("parseFailed", $"Failed to parse Cobertura XML: {ex.Message}");
        }
    }

    // --- 6b. GetCoverageGaps ---

    [McpServerTool(Title = "Coverage gaps per file", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What to test next, for a whole scope in one call. For each file in the scope, reports whether it meets `targetRate` on both lines and branches; for each file below it, returns the fewest methods whose uncovered lines and branches close that file's gap, with the uncovered line ranges, the uncovered branch conditions, and the source around them. Replaces one GetFileCoverage per file plus one GetUncoveredBranches per method in each test cycle. Non-target files (entry points, DI registration, generated code, models, test projects) are skipped as in GetSourceFiles.")]
    public string GetCoverageGaps(
        [Description("Absolute path to coverage.cobertura.xml (returned by RunTestsWithCoverage). Falls back to the session-scoped path stored in .mcp-coverage/.coverage-state when not found.")]
        string coberturaXmlPath,
        [Description("Scope: a .cs file, a folder, a .csproj, or a comma-separated list of .cs files (e.g. one batch from GetSourceFiles).")]
        string path,
        [Description("Target per file as a fraction in [0.0, 1.0], applied to both line and branch coverage. Pass 0.8, not 80. Default 0.8.")]
        double targetRate = 0.8,
        [Description("Number of below-target files returned in detail, smallest gap first. The others are listed with their rates only. Default 5.")]
        int maxFiles = 5,
        [Description("Maximum methods returned per file. Default 5.")]
        int maxMethodsPerFile = 5,
        [Description("Include the source lines around each gap (2 lines of context, at most 30 lines per method). Default true.")]
        bool includeSource = true,
        [Description("Optional session token used to resolve the session-scoped .coverage-state-{hash} fallback. Omit for single-agent use.")]
        string? sessionId = null)
    {
        if (targetRate is < 0.0 or > 1.0)
            return JsonHelper.Error("invalidParameter", "targetRate must be between 0.0 and 1.0.");
        if (maxFiles < 1 || maxMethodsPerFile < 1)
            return JsonHelper.Error("invalidParameter", "maxFiles and maxMethodsPerFile must be at least 1.");

        try { _pathGuard.Validate(coberturaXmlPath, nameof(coberturaXmlPath)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        var resolvedPath = _sessionManager.ResolveCoberturaPath(coberturaXmlPath, sessionId);
        if (resolvedPath == null)
            return JsonHelper.Error("fileNotFound", "Cobertura XML not found and no .coverage-state fallback available.");

        if (!TryDiscoverScope(path, out var scope, out var error))
            return error;

        try
        {
            var gaps = _coberturaService.GetCoverageGaps(
                resolvedPath, scope.Targets, targetRate, maxFiles, maxMethodsPerFile, includeSource);
            return JsonHelper.Serialize(new
            {
                gaps.TargetRate,
                gaps.FilesInScope,
                gaps.FilesDone,
                gaps.FilesRemaining,
                gaps.Remaining,
                gaps.MoreRemaining,
                gaps.Done,
                // In the scope but absent from the report: usually the test project doesn't
                // reference the project these files belong to, so nothing can cover them yet.
                gaps.NotInReport,
                excludedCount = scope.Excluded.Count
            });
        }
        catch (Exception ex)
        {
            return JsonHelper.Error("parseFailed", $"Failed to parse Cobertura XML: {ex.Message}");
        }
    }

    // --- 6c. GetTestEnvironment ---

    [McpServerTool(Title = "Detect test stack", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Detect a test project's stack: test framework (xUnit v2/v3, NUnit, MSTest) with the attributes to use for tests, data-driven cases, setup and teardown; runner (VSTest or Microsoft.Testing.Platform); coverage collector; mocking and assertion libraries; integration-test packages. Call once per test project before writing tests, and write every test in exactly this stack. Reads obj/project.assets.json when the project is restored, otherwise the .csproj.")]
    public string GetTestEnvironment(
        [Description("Absolute path to the test project's .csproj.")]
        string testProjectPath)
    {
        try { _pathGuard.Validate(testProjectPath, nameof(testProjectPath)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        if (!File.Exists(testProjectPath) || !testProjectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return JsonHelper.Error("fileNotFound", $"Test project not found or not a .csproj: {testProjectPath}");

        try
        {
            return JsonHelper.Serialize(TestEnvironmentDetector.Detect(testProjectPath));
        }
        catch (System.Xml.XmlException ex)
        {
            return JsonHelper.Error("parseFailed", $"Could not read the project file: {ex.Message}");
        }
    }

    // --- 6d. DescribeTestTarget ---

    [McpServerTool(Title = "Describe a class to test", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Describe what tests for a source file's classes need, without reading the whole file: constructor dependencies with how to provide each in a test (mock, NullLogger, Options.Create, in-memory SQLite DbContext, real instance for sealed/concrete classes...), public and internal methods with signatures, line ranges and decision points, calls a test cannot control (DateTime.Now, File.*, Guid.NewGuid...) with the usual seam, a suggested test type (unit / integration / either) with reasons, and existing test files for the class. Syntax-only; no build needed.")]
    public string DescribeTestTarget(
        [Description("Absolute path to the .cs source file to be tested.")]
        string sourceFilePath)
    {
        try { _pathGuard.Validate(sourceFilePath, nameof(sourceFilePath)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        if (!File.Exists(sourceFilePath) || !sourceFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return JsonHelper.Error("fileNotFound", $"Source file not found or not a .cs file: {sourceFilePath}");

        var description = TestTargetDescriber.Describe(sourceFilePath);
        return description.Types.Count == 0
            ? JsonHelper.Error("noTestableTypes", "The file declares no class, record or struct with methods or constructor dependencies.")
            : JsonHelper.Serialize(description);
    }

    // --- 7. GetSourceFiles ---

    [McpServerTool(Title = "Discover source files", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Discover .cs files from a file/folder/.csproj/comma-separated list and group into batches sized by `lineBudget`. For a folder or project, files that are not test targets (entry points, DI registration, generated code, data-only models) are left out of the batches and listed under `excluded` with the reason; files passed explicitly are always kept.")]
    public string GetSourceFiles(
        [Description("A single .cs file, a directory to scan recursively, a .csproj file, or a comma/semicolon-separated list of .cs file paths. Build/obj output is excluded automatically.")]
        string path,
        [Description("Soft maximum total source-lines per batch. Files larger than the budget get their own batch. Must be >= 1. Default 300.")]
        int lineBudget = 300)
    {
        if (lineBudget < 1)
            return JsonHelper.Error("invalidParameter", "lineBudget must be at least 1.");

        if (!TryDiscoverScope(path, out var discovered, out var error))
            return error;
        var (scope, scopePath, targets, excluded) = discovered;

        var bag = new ConcurrentBag<(string path, int lines, int methodCount)>();
        Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
        {
            var (lines, methodCount) = _fileService.GetFileMetadata(f);
            bag.Add((f, lines, methodCount));
        });

        var files = bag
            .OrderBy(f => f.lines)
            .Select(f => new { f.path, f.lines, f.methodCount })
            .ToList();

        var batches = new List<List<object>>();
        var currentBatch = new List<object>();
        var currentBatchLines = 0;

        foreach (var file in files)
        {
            if (file.lines > lineBudget)
            {
                if (currentBatch.Count > 0)
                {
                    batches.Add(currentBatch);
                    currentBatch = [];
                    currentBatchLines = 0;
                }
                batches.Add([file]);
                continue;
            }

            if (currentBatchLines + file.lines > lineBudget && currentBatch.Count > 0)
            {
                batches.Add(currentBatch);
                currentBatch = [];
                currentBatchLines = 0;
            }

            currentBatch.Add(file);
            currentBatchLines += file.lines;
        }

        if (currentBatch.Count > 0)
            batches.Add(currentBatch);

        return JsonHelper.Serialize(new
        {
            scope,
            scopePath,
            totalFiles = files.Count,
            totalLines = files.Sum(f => f.lines),
            lineBudget,
            batchCount = batches.Count,
            batches,
            excludedCount = excluded.Count,
            excluded = ExcludedSample(excluded)
        });
    }

    private const int MaxExcludedListed = 100;

    // Recursive .cs enumeration that does not descend into or return reparse points
    // (symlinks/junctions). A link inside the scope that points outside it would otherwise pull
    // foreign files into the batch — and, for a .cs link to a secret file, into GetCoverageGaps'
    // source snippets. Hidden and system entries are still scanned, as with the
    // SearchOption.AllDirectories overload this replaced (on Linux/macOS every dot-folder is
    // "hidden"). An unreadable subfolder is skipped instead of failing the whole scan.
    private static readonly EnumerationOptions NoFollowLinks = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    // Capped: a large solution can have hundreds of DTOs or test files, and the count plus a
    // sample is enough to spot a misclassified file.
    private static IEnumerable<object> ExcludedSample(List<(string Path, NonTargetReason Reason)> excluded) =>
        excluded
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxExcludedListed)
            .Select(e => (object)new
            {
                path = e.Path,
                kind = JsonNamingPolicy.CamelCase.ConvertName(e.Reason.Kind.ToString()),
                reason = e.Reason.Reason
            });

    private sealed record ScopeFiles(
        string Scope, string? ScopePath, List<string> Targets, List<(string Path, NonTargetReason Reason)> Excluded);

    /// <summary>
    /// Resolves a scope (one .cs file, a folder, a .csproj, or a comma/semicolon-separated
    /// list) to its test-target files. Shared by GetSourceFiles and GetCoverageGaps so both
    /// work on the same files. On failure, <paramref name="error"/> is the tool's error response.
    /// </summary>
    private bool TryDiscoverScope(string path, out ScopeFiles files, out string error)
    {
        files = null!;
        error = "";
        List<string> filePaths;
        string scope;
        string? scopePath = null;

        try
        {
            if (path.Contains(',') || path.Contains(';'))
            {
                scope = "list";
                var paths = path.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
                foreach (var p in paths) _pathGuard.Validate(p, nameof(path));

                filePaths = paths
                    .Where(p => File.Exists(p) && p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(f => f)
                    .ToList();

                if (filePaths.Count == 0)
                {
                    error = JsonHelper.Error("fileNotFound", "None of the files in the provided list were found or are .cs files.");
                    return false;
                }
            }
            else
            {
                _pathGuard.Validate(path, nameof(path));

                if (File.Exists(path) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    scope = "file";
                    filePaths = [Path.GetFullPath(path)];
                }
                else if (File.Exists(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    scope = "project";
                    scopePath = Path.GetFullPath(path);
                    var projectDir = Path.GetDirectoryName(scopePath) ?? Directory.GetCurrentDirectory();
                    var owners = new ProjectOwnerLookup();
                    // Projects nested under this one's folder (often its test project) own their
                    // own files; they are not part of this project's scope.
                    filePaths = Directory.GetFiles(projectDir, "*.cs", NoFollowLinks)
                        .Where(f => !_fileService.IsExcludedPath(f))
                        .Select(Path.GetFullPath)
                        .Where(f => string.Equals(owners.OwningProject(f), scopePath, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f)
                        .ToList();
                }
                else if (Directory.Exists(path))
                {
                    scope = "folder";
                    scopePath = Path.GetFullPath(path);
                    filePaths = Directory.GetFiles(path, "*.cs", NoFollowLinks)
                        .Where(f => !_fileService.IsExcludedPath(f))
                        .Select(Path.GetFullPath)
                        .OrderBy(f => f)
                        .ToList();
                }
                else
                {
                    error = JsonHelper.Error("fileNotFound", $"Path not found or not a .cs file, .csproj file, or directory: {path}");
                    return false;
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            error = JsonHelper.Error("pathNotAllowed", ex.Message);
            return false;
        }

        // Files discovered from a folder or project are narrowed to test targets (see
        // SourceFileClassifier). Files the caller names explicitly are always kept, which is
        // also the way to override the classifier for a file it got wrong.
        var classify = scope is "folder" or "project";
        var projects = new ProjectOwnerLookup();
        var targets = new ConcurrentBag<string>();
        var excluded = new ConcurrentBag<(string Path, NonTargetReason Reason)>();
        Parallel.ForEach(filePaths, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
        {
            if (classify && (projects.TestProjectReason(f) ?? ClassifyOrNull(f)) is { } reason)
                excluded.Add((f, reason));
            else
                targets.Add(f);
        });

        files = new ScopeFiles(scope, scopePath,
            targets.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(), excluded.ToList());
        return true;
    }

    /// <summary>
    /// Finds the .csproj that owns a source file (the nearest one walking up its folders),
    /// with per-directory caching since a scope's files share folders.
    /// </summary>
    private sealed class ProjectOwnerLookup
    {
        private readonly ConcurrentDictionary<string, string?> _projectByDir = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _isTestProject = new(StringComparer.OrdinalIgnoreCase);

        public string? OwningProject(string file) => ProjectIn(Path.GetDirectoryName(file));

        private string? ProjectIn(string? dir) => dir == null ? null : _projectByDir.GetOrAdd(dir, d =>
            ProjectFileIn(d) is { } project ? Path.GetFullPath(project) : ProjectIn(Path.GetDirectoryName(d)));

        private static string? ProjectFileIn(string dir)
        {
            try { return Directory.GetFiles(dir, "*.csproj").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault(); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public NonTargetReason? TestProjectReason(string file) =>
            OwningProject(file) is { } project && _isTestProject.GetOrAdd(project, SourceFileClassifier.IsTestProject)
                ? new NonTargetReason(NonTargetKind.TestCode, $"part of test project {Path.GetFileName(project)}")
                : null;
    }

    // A file that can't be read is kept as a target; it fails visibly later instead of
    // silently disappearing from the scope here.
    private static NonTargetReason? ClassifyOrNull(string path)
    {
        try { return SourceFileClassifier.Classify(path, File.ReadAllText(path)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // --- 8. CleanupSession ---

    [McpServerTool(Title = "Clean up coverage artifacts", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Remove session state files and the TestResults-* directories this tool created (never the bare TestResults that dotnet test and Visual Studio use). Pass `sessionId` to scope, or omit to clean artifacts older than `maxAgeMinutes`.")]
    public string CleanupSession(
        [Description("Project working directory containing .mcp-coverage/, TestResults*, and coveragereport* artifacts to clean. Must be inside COVERAGE_MCP_ALLOWED_ROOT when that environment variable is set.")]
        string workingDir,
        [Description("When set, removes only state files and directories whose name suffix matches this session's hash. Omit to do an age-based sweep instead.")]
        string? sessionId = null,
        [Description("When `sessionId` is omitted, removes artifacts older than this many minutes. Ignored when `sessionId` is set. Default 120.")]
        int maxAgeMinutes = 120)
    {
        try { _pathGuard.Validate(workingDir, nameof(workingDir)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        var removed = _sessionManager.Cleanup(workingDir, sessionId, maxAgeMinutes);
        return JsonHelper.Serialize(new { removed });
    }

    // --- Private helpers ---

    /// <summary>
    /// Turns step messages into MCP progress notifications with an increasing counter. Reports
    /// synchronously and in order, unlike System.Progress, which posts to the thread pool.
    /// </summary>
    private sealed class StepProgress(IProgress<ProgressNotificationValue> inner) : IProgress<string>
    {
        private int _step;
        public void Report(string message) =>
            inner.Report(new ProgressNotificationValue { Progress = Interlocked.Increment(ref _step), Message = message });
    }

    private const int MaxBuildErrors = 20;

    internal static string MissingExtensionDetail(string output, string error)
    {
        var lines = $"{output}\n{error}".Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains("Unknown option", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("is provided by", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
        return lines.Count > 0
            ? string.Join("\n", lines)
            : $"The test application rejected an option (exit code 5).\nOutput (tail): {Tail(output)}";
    }

    // MSBuild prints each compiler error twice (inline and in the summary), each with the
    // project path appended. Return each error once, without the suffix; fall back to the
    // output tail when the failure has no `error` lines (e.g. the SDK itself failed).
    internal static string BuildErrorDetail(string output, string error)
    {
        var errors = $"{output}\n{error}".Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains(": error ", StringComparison.Ordinal))
            .Select(l => ProjectSuffix.Replace(l, ""))
            .Distinct()
            .ToList();

        if (errors.Count == 0)
            return $"Error: {error}\nOutput (tail): {Tail(output)}";

        var shown = string.Join("\n", errors.Take(MaxBuildErrors));
        return errors.Count > MaxBuildErrors ? $"{shown}\n...({errors.Count - MaxBuildErrors} more errors)" : shown;
    }

    // A build whose only errors are "cannot write/copy the output, it is in use" failed because
    // another build or test host has the files open (Windows locks them), not because of the
    // code. Reported as buildError, it sent the agent off "fixing" tests that were fine.
    // CS2012/CS0016: the compiler can't open or write obj/…dll; MSB3021/MSB3027: the copy to
    // bin/ failed; other MSBuild tasks (e.g. WriteLinesToFile) only say "being used by another
    // process". ponytail: that phrase is English-only; on a localized OS only the codes match.
    internal static string? LockedOutputDetail(string output, string error)
    {
        var errors = $"{output}\n{error}".Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains(": error ", StringComparison.Ordinal))
            .Select(l => ProjectSuffix.Replace(l, ""))
            .Distinct()
            .ToList();
        return errors.Count > 0 && errors.All(IsLockError) ? string.Join("\n", errors.Take(MaxBuildErrors)) : null;
    }

    private static bool IsLockError(string line) =>
        line.Contains("error CS2012:", StringComparison.Ordinal)
        || line.Contains("error CS0016:", StringComparison.Ordinal)
        || line.Contains("error MSB3021:", StringComparison.Ordinal)
        || line.Contains("error MSB3027:", StringComparison.Ordinal)
        || line.Contains("being used by another process", StringComparison.Ordinal);

    private static readonly Regex ProjectSuffix = new(@"\s*\[[^\[\]]+\.(?:cs|vb|fs)proj(?:::[^\]]*)?\]$", RegexOptions.Compiled);

    // dotnet test stdout is large and mostly noise once a run succeeds. Callers get the
    // artifact paths on success; on failure we return only the tail, which is where the
    // build/test error almost always is, so the full log never lands in the transcript.
    private const int OutputTailChars = 1500;
    private static string Tail(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= OutputTailChars
            ? s
            : $"...(truncated {s.Length - OutputTailChars} of {s.Length} chars; re-run for full output)\n" + s[^OutputTailChars..];
    }

    /// <summary>
    /// Walk up from a coverage XML path to find the project root.
    /// Coverage XMLs live inside TestResults-xxx/guid/, so we walk up
    /// past any TestResults or coveragereport directory to avoid storing
    /// baselines inside directories that get wiped on the next run.
    /// Returns null if no sensible root is found — callers must surface a
    /// clear error rather than silently dropping state into the current
    /// working directory or drive root.
    /// </summary>
    internal static string? ResolveProjectRoot(string resolvedPath)
    {
        var dir = Path.GetDirectoryName(resolvedPath);
        const int maxDepth = 20;
        for (var depth = 0; dir != null && depth < maxDepth; depth++)
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name))
                return null; // reached the drive root without finding a project-shaped directory
            if (name.StartsWith("TestResults", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("coveragereport", StringComparison.OrdinalIgnoreCase)
                || Guid.TryParse(name, out _))
            {
                dir = Path.GetDirectoryName(dir);
                continue;
            }
            return dir;
        }
        return null;
    }
}
