using Microsoft.Extensions.Logging;

namespace CoverageMcpServer.Services;

public interface IPathGuard
{
    string? AllowedRoot { get; }
    bool IsWithinAllowedRoot(string path);
    void Validate(string path, string paramName);
}

/// <summary>
/// Guards every filesystem path the MCP server touches against an allowlisted root directory,
/// configured via the COVERAGE_MCP_ALLOWED_ROOT environment variable. When the variable is set,
/// any user-supplied path outside that root is rejected with UnauthorizedAccessException.
/// When unset, all paths are allowed (backward compatible) and a one-time warning is logged.
/// </summary>
public class PathGuard : IPathGuard
{
    public const string EnvVarName = "COVERAGE_MCP_ALLOWED_ROOT";
    private readonly ILogger<PathGuard> _logger;
    private readonly string? _allowedRoot;
    private bool _warnedOnce;

    public PathGuard(ILogger<PathGuard> logger)
    {
        _logger = logger;
        var raw = Environment.GetEnvironmentVariable(EnvVarName);
        _allowedRoot = string.IsNullOrWhiteSpace(raw) ? null : NormalizeRoot(raw);
    }

    public string? AllowedRoot => _allowedRoot;

    public bool IsWithinAllowedRoot(string path)
    {
        if (_allowedRoot == null) return true;
        if (string.IsNullOrWhiteSpace(path)) return false;

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { return false; }

        var normalized = NormalizeRoot(fullPath);
        return normalized.StartsWith(_allowedRoot, StringComparison.OrdinalIgnoreCase);
    }

    public void Validate(string path, string paramName)
    {
        if (_allowedRoot == null)
        {
            if (!_warnedOnce)
            {
                _logger.LogWarning(
                    "{EnvVar} is not set. Path-traversal guard is disabled; the MCP server will accept any path. " +
                    "Set {EnvVar} to your repository root to restrict file access.",
                    EnvVarName, EnvVarName);
                _warnedOnce = true;
            }
            return;
        }

        if (!IsWithinAllowedRoot(path))
        {
            throw new UnauthorizedAccessException(
                $"Path '{path}' for parameter '{paramName}' is outside the allowed root '{_allowedRoot}'. " +
                $"Set the {EnvVarName} environment variable to adjust the allowed root.");
        }
    }

    private static string NormalizeRoot(string path)
    {
        var full = Path.GetFullPath(path).Replace('/', Path.DirectorySeparatorChar);
        // Ensure trailing separator so "C:\repo" does not match "C:\repo-evil".
        if (!full.EndsWith(Path.DirectorySeparatorChar))
            full += Path.DirectorySeparatorChar;
        return full;
    }
}
