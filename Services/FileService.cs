using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace CoverageMcpServer.Services;

public interface IFileService
{
    void AtomicWriteFile(string targetPath, string content);
    void SafeDelete(string directoryPath);
    Task WithFileLockAsync(string filePath, Func<Task> action);
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

    public async Task WithFileLockAsync(string filePath, Func<Task> action)
    {
        var normalizedPath = Path.GetFullPath(filePath).ToLowerInvariant();
        var entry = _fileLocks.AddOrUpdate(
            normalizedPath,
            _ => (new SemaphoreSlim(1, 1), DateTime.UtcNow),
            (_, existing) => (existing.Lock, DateTime.UtcNow));

        EvictStaleLocks();
        await entry.Lock.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    public (int Lines, int MethodCount) GetFileMetadata(string filePath)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            var lines = text.Split('\n').Length;
            var tree = CSharpSyntaxTree.ParseText(text);
            var root = tree.GetCompilationUnitRoot();
            var methodCount = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Count(m => m.Modifiers.Any(SyntaxKind.PublicKeyword));
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

        var candidates = _fileLocks
            .Where(kv => kv.Value.Lock.CurrentCount == 1)
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
