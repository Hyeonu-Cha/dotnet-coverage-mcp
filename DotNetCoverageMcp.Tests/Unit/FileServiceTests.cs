using DotNetCoverageMcp.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace DotNetCoverageMcp.Tests.Unit;

public class FileServiceTests : IDisposable
{
    private readonly FileService _sut;
    private readonly string _tempDir;

    public FileServiceTests()
    {
        var logger = new Mock<ILogger<FileService>>();
        _sut = new FileService(logger.Object);
        _tempDir = Path.Combine(Path.GetTempPath(), $"fst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // --- AtomicWriteFile ---

    // What antivirus or the search indexer does for a few milliseconds after a write: open
    // the file without FILE_SHARE_DELETE. On Windows, replacing it then fails with "Access to
    // the path is denied" (seen intermittently in ConcurrencyTests and possible for any
    // AppendTestCode call). On Linux and macOS the replace succeeds anyway.
    [Fact]
    public void AtomicWriteFile_TargetBrieflyHeldOpen_RetriesInsteadOfFailing()
    {
        var path = Path.Combine(_tempDir, "held.txt");
        File.WriteAllText(path, "old");
        var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // A dedicated thread, not a thread-pool continuation: with test classes running in
        // parallel on a small CI runner, a pool callback can start long after 100 ms, past
        // the retry budget (it did on windows-latest).
        var releaser = new Thread(() => { Thread.Sleep(100); handle.Dispose(); }) { IsBackground = true };
        releaser.Start();

        _sut.AtomicWriteFile(path, "new");
        releaser.Join();

        File.ReadAllText(path).Should().Be("new");
        Directory.GetFiles(_tempDir, ".tmp-*").Should().BeEmpty("the temp file is moved, not left behind");
    }

    [Fact]
    public void AtomicWriteFile_TargetHeldOpenForGood_StillFailsAndCleansUp()
    {
        if (!OperatingSystem.IsWindows()) return; // the replace isn't blocked elsewhere
        var path = Path.Combine(_tempDir, "locked.txt");
        File.WriteAllText(path, "old");
        using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var act = () => _sut.AtomicWriteFile(path, "new");

        var thrown = act.Should().Throw<Exception>().Which;
        (thrown is IOException or UnauthorizedAccessException).Should().BeTrue(thrown.ToString());
        Directory.GetFiles(_tempDir, ".tmp-*").Should().BeEmpty();
    }

    // Regression: every rewrite used UTF-8 without a BOM, so AppendTestCode stripped the BOM
    // Visual Studio puts on C# files.
    [Theory]
    [InlineData("utf-8-bom")]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    public void AtomicWriteFile_KeepsTheReplacedFilesEncoding(string kind)
    {
        var path = Path.Combine(_tempDir, "Tests.cs");
        var encoding = kind switch
        {
            "utf-8-bom" => new System.Text.UTF8Encoding(true),
            "utf-8" => new System.Text.UTF8Encoding(false),
            _ => System.Text.Encoding.Unicode,
        };
        File.WriteAllText(path, "class A { }", encoding);

        _sut.AtomicWriteFile(path, "class A { void B() { } } // é");

        var expected = encoding.GetPreamble().Concat(encoding.GetBytes("class A { void B() { } } // é")).ToArray();
        File.ReadAllBytes(path).Should().Equal(expected);
    }

    [Fact]
    public void AtomicWriteFile_NewFile_IsUtf8WithoutBom()
    {
        var path = Path.Combine(_tempDir, "new.txt");

        _sut.AtomicWriteFile(path, "x");

        File.ReadAllBytes(path).Should().Equal((byte)'x');
    }

    [Fact]
    public void AtomicWriteFile_WritesContentCorrectly()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        _sut.AtomicWriteFile(path, "hello world");

        File.ReadAllText(path).Should().Be("hello world");
    }

    [Fact]
    public void AtomicWriteFile_OverwritesExistingFile()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(path, "old content");

        _sut.AtomicWriteFile(path, "new content");

        File.ReadAllText(path).Should().Be("new content");
    }

    [Fact]
    public void AtomicWriteFile_NoTempFileLeftBehind()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        _sut.AtomicWriteFile(path, "content");

        Directory.GetFiles(_tempDir, ".tmp-*").Should().BeEmpty();
    }

    // --- AtomicCopyFile ---

    [Fact]
    public void AtomicCopyFile_CopiesContentCorrectly()
    {
        var src = Path.Combine(_tempDir, "src.xml");
        var dst = Path.Combine(_tempDir, "dst.xml");
        File.WriteAllText(src, "<coverage/>");

        _sut.AtomicCopyFile(src, dst);

        File.ReadAllText(dst).Should().Be("<coverage/>");
    }

    [Fact]
    public void AtomicCopyFile_OverwritesExistingTarget()
    {
        var src = Path.Combine(_tempDir, "src.xml");
        var dst = Path.Combine(_tempDir, "dst.xml");
        File.WriteAllText(src, "new");
        File.WriteAllText(dst, "old");

        _sut.AtomicCopyFile(src, dst);

        File.ReadAllText(dst).Should().Be("new");
    }

    [Fact]
    public void AtomicCopyFile_NoTempFileLeftBehind()
    {
        var src = Path.Combine(_tempDir, "src.xml");
        var dst = Path.Combine(_tempDir, "dst.xml");
        File.WriteAllText(src, "content");

        _sut.AtomicCopyFile(src, dst);

        Directory.GetFiles(_tempDir, ".tmp-*").Should().BeEmpty();
    }

    [Fact]
    public void AtomicCopyFile_MissingSource_Throws()
    {
        var src = Path.Combine(_tempDir, "does-not-exist.xml");
        var dst = Path.Combine(_tempDir, "dst.xml");

        Action act = () => _sut.AtomicCopyFile(src, dst);

        act.Should().Throw<FileNotFoundException>();
        Directory.GetFiles(_tempDir, ".tmp-*").Should().BeEmpty();
    }

    // --- SafeDelete ---

    [Fact]
    public void SafeDelete_RemovesExistingDirectory()
    {
        var dir = Path.Combine(_tempDir, "to-delete");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "file.txt"), "data");

        _sut.SafeDelete(dir);

        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public void SafeDelete_NoOpForMissingDirectory()
    {
        var dir = Path.Combine(_tempDir, "nonexistent");

        var act = () => _sut.SafeDelete(dir);

        act.Should().NotThrow();
    }

    // --- WithFileLockAsync ---

    [Fact]
    public async Task WithFileLockAsync_SerializesConcurrentAccess()
    {
        var path = Path.Combine(_tempDir, "locked.txt");
        var counter = 0;
        var maxConcurrent = 0;
        var current = 0;

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            _sut.WithFileLockAsync(path, async () =>
            {
                var c = Interlocked.Increment(ref current);
                if (c > 1) Interlocked.Exchange(ref maxConcurrent, c);
                await Task.Delay(10);
                Interlocked.Increment(ref counter);
                Interlocked.Decrement(ref current);
            })).ToArray();

        await Task.WhenAll(tasks);

        counter.Should().Be(10);
        maxConcurrent.Should().Be(0, "no concurrent execution should occur on the same path");
    }

    [Fact]
    public async Task WithFileLockAsync_AllowsParallelOnDifferentPaths()
    {
        var path1 = Path.Combine(_tempDir, "a.txt");
        var path2 = Path.Combine(_tempDir, "b.txt");
        var t1Entered = new TaskCompletionSource();
        var t2Entered = new TaskCompletionSource();

        var t1 = _sut.WithFileLockAsync(path1, async () =>
        {
            t1Entered.SetResult();
            await t2Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });

        await t1Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var t2 = _sut.WithFileLockAsync(path2, async () =>
        {
            t2Entered.SetResult();
            await Task.CompletedTask;
        });

        await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(10));
    }

    // --- GetFileMetadata ---

    [Fact]
    public void GetFileMetadata_ReturnsLineAndMethodCount()
    {
        var path = Path.Combine(_tempDir, "Sample.cs");
        File.WriteAllText(path, @"
public class Foo
{
    public void Method1() { }
    public void Method2() { }
    private void Secret() { }
}
");
        var (lines, methods) = _sut.GetFileMetadata(path);

        lines.Should().BeGreaterThan(1);
        methods.Should().Be(2);
    }

    [Fact]
    public void GetFileMetadata_CountsOnlyPublicMethods()
    {
        var path = Path.Combine(_tempDir, "Internal.cs");
        File.WriteAllText(path, @"
public class Bar
{
    internal void A() { }
    protected void B() { }
    private void C() { }
    public void D() { }
}
");
        var (_, methods) = _sut.GetFileMetadata(path);
        methods.Should().Be(1);
    }

    [Fact]
    public void GetFileMetadata_ReturnsDefaultsOnError()
    {
        var (lines, methods) = _sut.GetFileMetadata(Path.Combine(_tempDir, "nonexistent.cs"));

        lines.Should().Be(1);
        methods.Should().Be(0);
    }

    // --- IsExcludedPath ---

    [Theory]
    [InlineData("/src/obj/Debug/file.cs", true)]
    [InlineData("/src/bin/Release/file.cs", true)]
    [InlineData("/src/Migrations/001.cs", true)]
    [InlineData("/src/.mcp-coverage/state", true)]
    [InlineData("/src/TestResults-abc/file.xml", true)]
    [InlineData("/src/coveragereport-abc/index.html", true)]
    [InlineData("/src/Models/User.cs", false)]
    [InlineData("C:\\src\\OBJ\\file.cs", true)]
    [InlineData("C:\\src\\BIN\\file.cs", true)]
    public void IsExcludedPath_ClassifiesCorrectly(string path, bool expected)
    {
        _sut.IsExcludedPath(path).Should().Be(expected);
    }
}
