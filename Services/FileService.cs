using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CoverageMcpServer.Services;

public interface IFileService
{
    void AtomicWriteFile(string targetPath, string content);
    void SafeDelete(string directoryPath);
    Task WithFileLockAsync(string filePath, Func<Task> action, CancellationToken ct = default);
    (int Lines, int MethodCount) GetFileMetadata(string filePath);
    bool IsExcludedPath(string filePath);
}

public class FileService : IFileService
{
    private readonly ILogger<FileService> _logger;
    private readonly ConcurrentDictionary<string, (SemaphoreSlim Lock, DateTime LastAccess)> _fileLocks = new();
    private const int MaxFileLocks = 200;

    public FileService(ILogger<FileService> logger)
    {
        _logger = logger;
    }

    public void AtomicWriteFile(string targetPath, string content)
    {
        var dir = Path.GetDirectoryName(targetPath) ?? Directory.GetCurrentDirectory();
        var tempPath = Path.Combine(dir, $".tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public void SafeDelete(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            try { Directory.Delete(directoryPath, true); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete directory: {Path}", directoryPath);
            }
        }
    }

    public async Task WithFileLockAsync(string filePath, Func<Task> action, CancellationToken ct = default)
    {
        var normalizedPath = NormalizeLockKey(filePath);
        const int maxRetries = 5;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var entry = _fileLocks.AddOrUpdate(
                normalizedPath,
                _ => (new SemaphoreSlim(1, 1), DateTime.UtcNow),
                (_, existing) => (existing.Lock, DateTime.UtcNow));

            EvictStaleLocks();
            await entry.Lock.WaitAsync(ct);

            // Eviction runs without holding the lock, so the entry we acquired
            // may have been removed between AddOrUpdate and WaitAsync. If that
            // happened, another caller may have created a different semaphore
            // for the same path — release ours and retry with the canonical one.
            if (_fileLocks.TryGetValue(normalizedPath, out var current)
                && ReferenceEquals(current.Lock, entry.Lock))
            {
                try { await action(); return; }
                finally { entry.Lock.Release(); }
            }

            entry.Lock.Release();
            _logger.LogDebug("File lock for {Path} was evicted mid-acquire, retrying (attempt {Attempt})", filePath, attempt + 1);
        }

        throw new InvalidOperationException(
            $"Could not acquire a stable file lock for '{filePath}' after {maxRetries} attempts.");
    }

    private static string NormalizeLockKey(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        // Case-insensitive on Windows; preserve case on Unix (different paths = different files).
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    // Approximate — matches a line starting with `public` that ends with `(`, covering
    // the common C# shapes (methods, generic methods, constructors). Trades exactness
    // for speed so we don't parse every file in the repo with Roslyn just to build batches.
    // The return-type group is optional so constructors (`public MyClass()`) are counted too.
    private static readonly Regex PublicMethodRegex = new(
        @"^\s*public\s+(?:[^;{}()]+\s+)?\w+\s*(?:<[^<>]+>)?\s*\(",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public (int Lines, int MethodCount) GetFileMetadata(string filePath)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            var lines = text.Split('\n').Length;
            var methodCount = PublicMethodRegex.Matches(text).Count;
            return (Math.Max(lines, 1), methodCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read metadata for {FilePath}, returning defaults", filePath);
            return (1, 0);
        }
    }

    public bool IsExcludedPath(string filePath)
    {
        var normalized = filePath.Replace("\\", "/");
        string[] excludedSegments = ["/obj/", "/bin/", "/Migrations/", "/.mcp-coverage/"];
        if (excludedSegments.Any(seg => normalized.Contains(seg, StringComparison.OrdinalIgnoreCase)))
            return true;

        var parts = normalized.Split('/');
        return parts.Any(p => p.StartsWith("TestResults", StringComparison.OrdinalIgnoreCase)
                           || p.StartsWith("coveragereport", StringComparison.OrdinalIgnoreCase));
    }

    private void EvictStaleLocks()
    {
        if (_fileLocks.Count <= MaxFileLocks) return;

        // Grace period: never evict an entry that was touched in the last 5 seconds.
        // This narrows the race window where a caller has just obtained a reference
        // via AddOrUpdate but has not yet called WaitAsync.
        var graceCutoff = DateTime.UtcNow.AddSeconds(-5);

        var candidates = _fileLocks
            .Where(kv => kv.Value.Lock.CurrentCount == 1 && kv.Value.LastAccess < graceCutoff)
            .OrderBy(kv => kv.Value.LastAccess)
            .Take(_fileLocks.Count - MaxFileLocks + 20)
            .Select(kv => kv.Key)
            .ToList();

        var evicted = 0;
        foreach (var key in candidates)
        {
            // Don't Dispose the semaphore — another thread may still hold a reference
            // from AddOrUpdate. SemaphoreSlim without AvailableWaitHandle has no
            // unmanaged resources, so GC will collect it safely.
            if (_fileLocks.TryRemove(key, out _))
                evicted++;
        }

        if (evicted > 0)
            _logger.LogDebug("Evicted {Count} stale file locks, {Remaining} remaining", evicted, _fileLocks.Count);
    }
}
