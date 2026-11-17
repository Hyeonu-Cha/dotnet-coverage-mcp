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
    public static string Resolve(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? "";
            var current = root;
            foreach (var part in full[root.Length..].Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget != null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = target.FullName;
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
