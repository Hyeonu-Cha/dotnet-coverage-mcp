using Microsoft.Extensions.Logging;

namespace DotNetCoverageMcp.Services;

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
        // Resolve the root through any symlinks too, so a resolved candidate path is compared
        // against a resolved root (e.g. macOS /var vs /private/var), not a half-resolved mix.
        _allowedRoot = string.IsNullOrWhiteSpace(raw) ? null : NormalizeRoot(SymlinkResolver.Resolve(raw));
    }

    public string? AllowedRoot => _allowedRoot;

    public bool IsWithinAllowedRoot(string path)
    {
        if (_allowedRoot == null) return true;
        if (string.IsNullOrWhiteSpace(path)) return false;

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { return false; }

        // Resolve symlinks/junctions before the containment check. Without this, a link inside
        // the root that points outside it (root/link -> /etc) passes the lexical test while
        // reading or writing an arbitrary location.
        var normalized = NormalizeRoot(SymlinkResolver.Resolve(fullPath));
        return normalized.StartsWith(_allowedRoot, PathComparison);
    }

    // Windows and macOS file systems ignore case by default, so "C:\Repo" is "C:\repo" there.
    // On Linux they are different folders, and ignoring case let "/home/u/Repo" past a root of
    // "/home/u/repo".
    // ponytail: a case-sensitive APFS volume on macOS is still compared ignoring case.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
