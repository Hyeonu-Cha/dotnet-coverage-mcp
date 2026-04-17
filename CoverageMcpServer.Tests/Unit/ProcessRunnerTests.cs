using CoverageMcpServer.Services;
using FluentAssertions;

namespace CoverageMcpServer.Tests.Unit;

public class ProcessRunnerTests
{
    // --- EscapeProcessArg ---

    [Fact]
    public void EscapeProcessArg_QuotesSimpleArg()
    {
        ProcessRunner.EscapeProcessArg("foo").Should().Be("\"foo\"");
    }

    [Fact]
    public void EscapeProcessArg_EscapesInternalQuotes()
    {
        ProcessRunner.EscapeProcessArg("a\"b").Should().Be("\"a\\\"b\"");
    }

    [Fact]
    public void EscapeProcessArg_DoublesTrailingBackslash()
    {
        ProcessRunner.EscapeProcessArg(@"C:\path\").Should().Be("\"C:\\path\\\\\"");
    }

    [Fact]
    public void EscapeProcessArg_HandlesEmptyString()
    {
        var result = ProcessRunner.EscapeProcessArg("");
        result.Should().Be("\"\"");
    }

    [Fact]
    public void EscapeProcessArg_NoTrailingBackslash_NoDoubling()
    {
        ProcessRunner.EscapeProcessArg(@"C:\path").Should().Be("\"C:\\path\"");
    }

    [Fact]
    public void EscapeProcessArg_PathWithSpaces()
    {
        ProcessRunner.EscapeProcessArg(@"C:\my project\tests").Should().Be("\"C:\\my project\\tests\"");
    }
}
