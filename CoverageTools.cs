using System.Collections.Concurrent;
using System.ComponentModel;
using ModelContextProtocol.Server;
using DotNetCoverageMcp.Helpers;
using DotNetCoverageMcp.Services;

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

    [McpServerTool]
    [Description("Run `dotnet test` with XPlat Code Coverage and generate a JSON summary. Returns paths to Summary.json and Cobertura XML.")]
    public async Task<string> RunTestsWithCoverage(
        [Description("Absolute path to the .csproj test project. Must be inside COVERAGE_MCP_ALLOWED_ROOT when that environment variable is set.")]
        string testProjectPath,
        [Description("Test filter matched against FullyQualifiedName. Pass a class or method name (e.g. 'OrderServiceTests' or 'OrderServiceTests.PlacesOrder'); '*' runs everything. Do not pass full VSTest filter expressions like 'Category=Unit'.")]
        string filter,
        [Description("Working directory for the dotnet test invocation. Defaults to the test project's directory when omitted.")]
        string? workingDir = null,
        [Description("When true, omits --no-restore. Set after scaffolding a new test project or adding NuGet references; otherwise leave false for faster runs.")]
        bool forceRestore = false,
        [Description("Optional opaque token (any string) used to isolate output artifacts (TestResults-{hash}/, coveragereport-{hash}/) so concurrent agents don't trample each other. Omit for single-agent use.")]
        string? sessionId = null,
        [Description("Optional class-name pattern forwarded to coverlet's Include filter (e.g. 'OrderService' or 'Order*'). Always honored when set, independent of `filter`. Namespace-qualified names are not supported.")]
        string? includeClass = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(testProjectPath))
            return JsonHelper.Error("invalidParameter", "testProjectPath must not be empty.");
        if (string.IsNullOrWhiteSpace(filter))
            return JsonHelper.Error("invalidParameter", "filter must not be empty.");

        try
        {
            _pathGuard.Validate(testProjectPath, nameof(testProjectPath));
            if (workingDir != null) _pathGuard.Validate(workingDir, nameof(workingDir));
        }
        catch (UnauthorizedAccessException ex)
        {
            return JsonHelper.Error("pathNotAllowed", ex.Message);
        }

        workingDir ??= Path.GetDirectoryName(testProjectPath) ?? Directory.GetCurrentDirectory();
        var suffix = _sessionManager.ComputeSuffix(sessionId);
        var resultsDir = Path.Combine(workingDir, $"TestResults{suffix}");
        var reportDir = Path.Combine(workingDir, $"coveragereport{suffix}");

        _fileService.SafeDelete(resultsDir);
        _fileService.SafeDelete(reportDir);

        var testResult = await _processRunner.RunDotnetTestAsync(
            testProjectPath, filter, resultsDir, workingDir, forceRestore, includeClass, cancellationToken);

        if (testResult.Error == "cancelled")
            return JsonHelper.Error("cancelled", "Test run was cancelled by the client.");

        if (testResult.Error == "timeout")
            return JsonHelper.Error("timeout", $"Test run timed out. Output: {testResult.Output}");

        if (!testResult.Success)
            return JsonHelper.Error("buildError", $"Test run failed (code {testResult.ExitCode}). Error: {testResult.Error}\nOutput: {testResult.Output}");

        if (testResult.CoverageXmlPath == null)
            return JsonHelper.Error("noCoverage", "No coverage XML found.\n" + testResult.Output);

        _sessionManager.SaveCoverageState(workingDir, testResult.CoverageXmlPath, filter, sessionId);

        ReportResult reportResult;
        try
        {
            reportResult = await _processRunner.RunReportGeneratorAsync(
                testResult.CoverageXmlPath, reportDir, workingDir, cancellationToken);
        }
        catch (Exception ex)
        {
            return JsonHelper.Error("reportFailed",
                $"Could not launch reportgenerator: {ex.Message}\nCobertura XML is still available at: {testResult.CoverageXmlPath}");
        }

        if (!reportResult.Success)
        {
            return reportResult.ErrorDetail?.Contains("timed out") == true
                ? JsonHelper.Error("timeout", $"{reportResult.ErrorDetail}. Cobertura XML is still available at: {testResult.CoverageXmlPath}")
                : JsonHelper.Error("reportFailed", $"{reportResult.ErrorDetail}\n{testResult.Output}");
        }

        return $"Tests completed.\nCoverage JSON at: {reportResult.SummaryPath}\nCobertura XML at: {testResult.CoverageXmlPath}\nOutput: {testResult.Output}";
    }

    // --- 2. GetCoverageSummary ---

    [McpServerTool]
    [Description("Parse Summary.json into class/method coverage data, sorted by branch coverage ascending.")]
    public string GetCoverageSummary(
        [Description("Absolute path to the Summary.json produced by reportgenerator (returned by RunTestsWithCoverage).")]
        string summaryJsonPath)
    {
        try { _pathGuard.Validate(summaryJsonPath, nameof(summaryJsonPath)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        if (!File.Exists(summaryJsonPath))
            return JsonHelper.Error("fileNotFound", $"Summary.json not found: {summaryJsonPath}");

        try
        {
            var result = _coberturaService.ParseSummary(summaryJsonPath);
            return JsonHelper.Serialize(result);
        }
        catch (InvalidOperationException ex)
        {
            return JsonHelper.Error("parseFailed", ex.Message);
        }
        catch (Exception ex)
        {
            return JsonHelper.Error("parseFailed", $"Failed to parse Summary.json: {ex.Message}");
        }
    }

    // --- 3. GetUncoveredBranches ---

    [McpServerTool]
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

    [McpServerTool]
    [Description("Insert C# code into a test file via Roslyn AST (string fallback). Pass `insertAfterAnchor` to target a position; omit to append inside the last class.")]
    public async Task<string> AppendTestCode(
        [Description("Absolute path to the target .cs test file. Must already exist; the file is rewritten atomically.")]
        string testFilePath,
        [Description("C# code fragment to insert. Pass member declarations (test methods/fields) — do not include `namespace` or `using` lines unless intentionally adding them at the appropriate scope.")]
        string codeToAppend,
        [Description("If set, code is inserted after the last occurrence of this exact string (whitespace-tolerant fallback). When omitted, code is appended before the closing brace of the last class in the file.")]
        string? insertAfterAnchor = null)
    {
        try { _pathGuard.Validate(testFilePath, nameof(testFilePath)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        if (!File.Exists(testFilePath))
            return JsonHelper.Error("fileNotFound", $"Test file not found: {testFilePath}");

        try
        {
            var result = await _codeInserter.InsertCodeAsync(testFilePath, codeToAppend, insertAfterAnchor);
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
    }

    // --- 5. GetCoverageDiff ---

    [McpServerTool]
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
                _fileService.AtomicWriteFile(prevPath, File.ReadAllText(resolvedPath));
                return JsonHelper.Serialize(new { firstRun = true });
            }

            var result = _coberturaService.ComputeDiff(resolvedPath, prevPath);
            _fileService.AtomicWriteFile(prevPath, File.ReadAllText(resolvedPath));
            return JsonHelper.Serialize(result);
        }
        catch (Exception ex)
        {
            return JsonHelper.Error("parseFailed", $"Failed to parse coverage XML: {ex.Message}");
        }
    }

    // --- 6. GetFileCoverage ---

    [McpServerTool]
    [Description("Per-file class/method coverage from Cobertura XML. `allMeetTarget` is true when every class meets `targetRate` for line and branch.")]
    public string GetFileCoverage(
        [Description("Absolute path to coverage.cobertura.xml. Falls back to the session-scoped path stored in .mcp-coverage/.coverage-state when the file is not found at this path.")]
        string coberturaXmlPath,
        [Description("Source file name to look up (e.g. 'ExampleService.cs'). File-name match against the <class filename=\"...\"> entries in the Cobertura XML.")]
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
        catch (Exception ex)
        {
            return JsonHelper.Error("parseFailed", $"Failed to parse Cobertura XML: {ex.Message}");
        }
    }

    // --- 7. GetSourceFiles ---

    [McpServerTool]
    [Description("Discover .cs files from a file/folder/.csproj/comma-separated list and group into batches sized by `lineBudget`.")]
    public string GetSourceFiles(
        [Description("A single .cs file, a directory to scan recursively, a .csproj file, or a comma/semicolon-separated list of .cs file paths. Build/obj output is excluded automatically.")]
        string path,
        [Description("Soft maximum total source-lines per batch. Files larger than the budget get their own batch. Must be >= 1. Default 300.")]
        int lineBudget = 300)
    {
        if (lineBudget < 1)
            return JsonHelper.Error("invalidParameter", "lineBudget must be at least 1.");

        List<string> filePaths;
        string scope;
        string? scopePath = null;

        if (path.Contains(',') || path.Contains(';'))
        {
            scope = "list";
            var paths = path.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries);
            try
            {
                foreach (var p in paths) _pathGuard.Validate(p.Trim(), nameof(path));
            }
            catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

            filePaths = paths
                .Select(p => p.Trim())
                .Where(p => File.Exists(p) && p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f)
                .ToList();

            if (filePaths.Count == 0)
                return JsonHelper.Error("fileNotFound", "None of the files in the provided list were found or are .cs files.");
        }
        else
        {
            try { _pathGuard.Validate(path, nameof(path)); }
            catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

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
                filePaths = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !_fileService.IsExcludedPath(f))
                    .Select(Path.GetFullPath)
                    .OrderBy(f => f)
                    .ToList();
            }
            else if (Directory.Exists(path))
            {
                scope = "folder";
                scopePath = Path.GetFullPath(path);
                filePaths = Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !_fileService.IsExcludedPath(f))
                    .Select(Path.GetFullPath)
                    .OrderBy(f => f)
                    .ToList();
            }
            else
            {
                return JsonHelper.Error("fileNotFound", $"Path not found or not a .cs file, .csproj file, or directory: {path}");
            }
        }

        var bag = new ConcurrentBag<(string path, int lines, int methodCount)>();
        Parallel.ForEach(filePaths, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
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
                batches.Add([(object)file]);
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
            files
        });
    }

    // --- 8. CleanupSession ---

    [McpServerTool]
    [Description("Remove session state files and TestResults/coveragereport directories. Pass `sessionId` to scope, or omit to clean artifacts older than `maxAgeMinutes`.")]
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
