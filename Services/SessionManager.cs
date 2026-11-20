using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DotNetCoverageMcp.Services;

public interface ISessionManager
{
    string ComputeKey(string input);
    string ComputeSuffix(string? sessionId);
    string? ResolveCoberturaPath(string coberturaXmlPath, string? sessionId);
    void SaveCoverageState(string workingDir, string xmlPath, string? sessionId);
    int Cleanup(string workingDir, string? sessionId, int maxAgeMinutes);
}

public class SessionManager : ISessionManager
{
    private readonly IFileService _fileService;
    private readonly IPathGuard _pathGuard;
    private readonly ILogger<SessionManager> _logger;

    public SessionManager(IFileService fileService, IPathGuard pathGuard, ILogger<SessionManager> logger)
    {
        _fileService = fileService;
        _pathGuard = pathGuard;
        _logger = logger;
    }

    public string ComputeKey(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    public string ComputeSuffix(string? sessionId) =>
        sessionId != null ? $"-{ComputeKey(sessionId)}" : "";

    public string? ResolveCoberturaPath(string coberturaXmlPath, string? sessionId)
    {
        if (File.Exists(coberturaXmlPath) && _pathGuard.IsWithinAllowedRoot(coberturaXmlPath))
            return coberturaXmlPath;

        // Walk up from the supplied path to find .mcp-coverage/.
        // The caller may pass an old path inside a deleted TestResults-xxx/ directory,
        // but .mcp-coverage/ lives at the project root — so we search upward.
        var startDir = Path.GetDirectoryName(coberturaXmlPath) is { Length: > 0 } d
            ? d
            : Directory.GetCurrentDirectory();
        var dir = startDir;

        const int maxDepth = 20;
        for (var depth = 0; dir != null && depth < maxDepth; depth++)
        {
            var stateDir = Path.Combine(dir, ".mcp-coverage");
            if (Directory.Exists(stateDir))
            {
                var resolved = TryResolveFromStateDir(stateDir, sessionId);
                if (resolved != null)
                {
                    if (depth > 0)
                        _logger.LogInformation(
                            "Resolved cobertura path via walk-up: found state in {StateDir} (started from {StartDir})",
                            stateDir, startDir);
                    return resolved;
                }
            }
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private string? TryResolveFromStateDir(string stateDir, string? sessionId)
    {
        if (sessionId != null)
        {
            var scopedStateFile = Path.Combine(stateDir, $".coverage-state-{ComputeKey(sessionId)}");
            var candidate = ReadStateFile(scopedStateFile);
            if (candidate != null) return candidate;
        }

        return ReadStateFile(Path.Combine(stateDir, ".coverage-state"));
    }

    // State files hold arbitrary content. Treat them as untrusted: only return a path
    // that exists AND falls inside the allowed root. Otherwise a stale or poisoned
    // state file would let a caller read/write outside PathGuard's allowlist.
    private string? ReadStateFile(string stateFile)
    {
        if (!File.Exists(stateFile)) return null;
        var resolved = File.ReadAllText(stateFile).Trim();
        if (!File.Exists(resolved)) return null;
        if (!_pathGuard.IsWithinAllowedRoot(resolved))
        {
            _logger.LogWarning("Ignoring state file {State}: resolved path '{Resolved}' is outside the allowed root.",
                stateFile, resolved);
            return null;
        }
        return resolved;
    }

    // A state file keyed by the test filter was written too, and read only when a sessionId
    // happened to equal the filter text; cleanup never removed it, so they piled up.
    public void SaveCoverageState(string workingDir, string xmlPath, string? sessionId)
    {
        var stateDir = Path.Combine(workingDir, ".mcp-coverage");
        Directory.CreateDirectory(stateDir);

        if (sessionId != null)
            _fileService.AtomicWriteFile(Path.Combine(stateDir, $".coverage-state-{ComputeKey(sessionId)}"), xmlPath);

        // Global fallback
        _fileService.AtomicWriteFile(Path.Combine(stateDir, ".coverage-state"), xmlPath);
    }

    // Exactly the folder names this tool creates: TestResults-mcp for single-agent runs, and
    // TestResults-<key> / coveragereport-<key> (the latter left by versions before 2.0.1), where
    // <key> is ComputeKey's 8 lowercase hex characters. A prefix match also took folders such as
    // TestResults-ci-history or coveragereport-html that the user's own scripts write, and the
    // sweep deleted them. The bare TestResults/coveragereport names, which dotnet test, Visual
    // Studio and a hand-run reportgenerator use, never match either.
    private static readonly Regex OwnedArtifactDir =
        new(@"^(?:TestResults-(?:mcp|[0-9a-f]{8})|coveragereport-[0-9a-f]{8})$", RegexOptions.Compiled);

    public int Cleanup(string workingDir, string? sessionId, int maxAgeMinutes)
    {
        var removed = 0;

        if (sessionId != null)
        {
            var key = ComputeKey(sessionId);

            var stateDir = Path.Combine(workingDir, ".mcp-coverage");
            if (Directory.Exists(stateDir))
            {
                string[] stateFiles = [$".coverage-state-{key}", $".coverage-prev-{key}.xml"];
                foreach (var name in stateFiles)
                {
                    var filePath = Path.Combine(stateDir, name);
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            File.Delete(filePath);
                            removed++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete session file: {Path}", filePath);
                        }
                    }
                }
            }

            // Only this session's own artifacts. The unsuffixed TestResults/coveragereport dirs
            // belong to a single-agent run or to another tool (dotnet test / Visual Studio write
            // TestResults by default) — deleting them here would wipe data this session doesn't
            // own. Nothing in Cleanup touches the bare names; the sweep below only takes the
            // names this tool creates (OwnedArtifactDir).
            _fileService.SafeDelete(Path.Combine(workingDir, $"TestResults-{key}"));
            _fileService.SafeDelete(Path.Combine(workingDir, $"coveragereport-{key}"));
        }
        else
        {
            // The single-agent artifacts (TestResults-mcp, the unscoped state and baseline) belong
            // to the caller that omits sessionId, so they go whatever their age: otherwise the
            // cleanup an agent runs when it finishes left its own fresh run behind. Other sessions'
            // artifacts still go only once they are older than maxAgeMinutes.
            var stateDir = Path.Combine(workingDir, ".mcp-coverage");
            if (Directory.Exists(stateDir))
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-maxAgeMinutes);
                foreach (var file in Directory.GetFiles(stateDir))
                {
                    try
                    {
                        if (Path.GetFileName(file) is ".coverage-state" or ".coverage-prev.xml"
                            || File.GetLastWriteTimeUtc(file) < cutoff)
                        {
                            File.Delete(file);
                            removed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete stale state file: {Path}", file);
                    }
                }
            }

            if (Directory.Exists(workingDir))
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-maxAgeMinutes);
                foreach (var dir in Directory.GetDirectories(workingDir))
                {
                    var dirName = Path.GetFileName(dir);
                    if (OwnedArtifactDir.IsMatch(dirName)
                        && (dirName == "TestResults-mcp" || Directory.GetLastWriteTimeUtc(dir) < cutoff))
                    {
                        _fileService.SafeDelete(dir);
                        removed++;
                    }
                }
            }
        }

        _logger.LogInformation("Session cleanup removed {Count} artifacts", removed);
        return removed;
    }
}
