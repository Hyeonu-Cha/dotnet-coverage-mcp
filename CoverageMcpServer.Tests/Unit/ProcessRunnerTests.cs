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

    // --- StripTestSuffix ---

    [Theory]
    [InlineData("FooTests", "Foo")]
    [InlineData("FooTest", "Foo")]
    [InlineData("FooUnitTests", "Foo")]
    [InlineData("FooIntegrationTests", "Foo")]
    [InlineData("FooSpecs", "Foo")]
    [InlineData("FooSpec", "Foo")]
    [InlineData("FooService", "FooService")]
    [InlineData("Tests", "")]
    public void StripTestSuffix_RemovesCorrectSuffix(string input, string expected)
    {
        ProcessRunner.StripTestSuffix(input).Should().Be(expected);
    }

    [Fact]
    public void StripTestSuffix_IntegrationTestsHasPriority()
    {
        // "FooIntegrationTests" should strip "IntegrationTests", not just "Tests"
        ProcessRunner.StripTestSuffix("FooIntegrationTests").Should().Be("Foo");
    }

    [Fact]
    public void StripTestSuffix_EmptyString_ReturnsEmpty()
    {
        ProcessRunner.StripTestSuffix("").Should().Be("");
    }

    [Fact]
    public void StripTestSuffix_NullString_ReturnsNull()
    {
        ProcessRunner.StripTestSuffix(null!).Should().BeNull();
    }
}
