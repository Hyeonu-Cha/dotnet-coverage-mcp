namespace DotNetCoverageMcp.Services;

/// <summary>
/// Resolves every symlinked or junctioned segment of a path to its real on-disk location.
/// A purely lexical containment check (does the path start with the allowed root?) can be
/// fooled by a link inside the root that points outside it — <c>root/link/secret.cs</c> looks
/// contained but reads an arbitrary file. Resolving links first closes that gap, and it also
/// fixes legitimate cases where the report or the repo sits under a link (macOS
/// <c>/var</c> → <c>/private/var</c>, a repo checked out under a symlinked folder).
/// </summary>
internal static class SymlinkResolver
{
    /// <summary>
    /// The path with every existing symlinked/junctioned segment resolved to its final target.
    /// Non-existent trailing segments (a file about to be written, a folder not yet created) are
    /// kept lexically. Returns a best-effort fully-qualified path if resolution can't complete,
    /// never throwing — callers use it to compare paths, not as a source of truth about existence.
    /// </summary>
    public static string Resolve(string path) => Resolve(path, 0);

    // A link's target can itself sit under a symlinked prefix (macOS /var -> /private/var, or a
    // link pointing through another link), so following a link is not enough on its own: once we
    // jump to a target we re-resolve the whole thing — the target plus the segments not yet
    // walked — from scratch. The depth guard stops a symlink cycle from looping forever.
    private static string Resolve(string path, int depth)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (depth > 40) return full;

            var root = Path.GetPathRoot(full) ?? "";
            var parts = full[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget != null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    var combined = i + 1 < parts.Length
                        ? Path.Combine([target.FullName, .. parts[(i + 1)..]])
                        : target.FullName;
                    return Resolve(combined, depth + 1);
                }
            }
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }
    }
}
