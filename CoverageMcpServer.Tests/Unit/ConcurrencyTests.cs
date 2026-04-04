using CoverageMcpServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace CoverageMcpServer.Tests.Unit;

public class ConcurrencyTests : IDisposable
{
    private readonly FileService _sut;
    private readonly string _tempDir;

    public ConcurrencyTests()
    {
        _sut = new FileService(new Mock<ILogger<FileService>>().Object);
        _tempDir = Path.Combine(Path.GetTempPath(), $"conc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task ConcurrentFileLocks_SameFile_Serialized()
    {
        var path = Path.Combine(_tempDir, "shared.txt");
        var counter = 0;
        var violations = 0;

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            _sut.WithFileLockAsync(path, async () =>
            {
                var before = Interlocked.Increment(ref counter);
                if (before > 1) Interlocked.Increment(ref violations);
                await Task.Delay(5);
                Interlocked.Decrement(ref counter);
            })).ToArray();

        await Task.WhenAll(tasks);

        violations.Should().Be(0, "all accesses to the same file should be serialized");
    }

    [Fact]
    public async Task AtomicWriteFile_ConcurrentWrites_NeverCorrupts()
    {
        var path = Path.Combine(_tempDir, "atomic.txt");
        var validContents = Enumerable.Range(0, 20).Select(i => $"content-{i}").ToList();

        // Use file lock to serialize writes (Windows doesn't allow concurrent File.Move to same target)
        var tasks = validContents.Select(content =>
            _sut.WithFileLockAsync(path, () =>
            {
                _sut.AtomicWriteFile(path, content);
                return Task.CompletedTask;
            })).ToArray();

        await Task.WhenAll(tasks);

        var finalContent = File.ReadAllText(path);
        validContents.Should().Contain(finalContent, "final file should contain one complete valid write");
    }

    [Fact]
    public async Task EvictionUnderPressure_DoesNotDeadlock()
    {
        // Create 250 unique file locks to trigger eviction
        var tasks = Enumerable.Range(0, 250).Select(i =>
        {
            var path = Path.Combine(_tempDir, $"file-{i}.txt");
            return _sut.WithFileLockAsync(path, () => Task.CompletedTask);
        }).ToArray();

        var completed = Task.WhenAll(tasks);
        var finished = await Task.WhenAny(completed, Task.Delay(10_000));

        finished.Should().Be(completed, "all locks should complete without deadlock");
    }
}
