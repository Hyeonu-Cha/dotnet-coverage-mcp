using System.Text.Json;
using DotNetCoverageMcp.Helpers;
using FluentAssertions;

namespace DotNetCoverageMcp.Tests.Unit;

public class JsonHelperTests
{
    [Fact]
    public void IsError_RecognisesEveryErrorShapeAndNothingElse()
    {
        JsonHelper.IsError(JsonHelper.Error("fileNotFound", "x")).Should().BeTrue();
        // The tools' own richer errors (testsFailed, ambiguous, testRunAborted) start the same way.
        JsonHelper.IsError(JsonHelper.Serialize(new { error = "2 of 5 tests failed.", errorType = "testsFailed", failed = 2 })).Should().BeTrue();
        JsonHelper.IsError(JsonHelper.Serialize(new { firstRun = true })).Should().BeFalse();
        JsonHelper.IsError("Tests completed.\nCobertura XML at: /x.xml").Should().BeFalse();
    }

    [Fact]
    public void Error_ReturnsJsonWithErrorAndType()
    {
        var json = JsonHelper.Error("fileNotFound", "File missing");
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("error").GetString().Should().Be("File missing");
        doc.RootElement.GetProperty("errorType").GetString().Should().Be("fileNotFound");
    }

    [Fact]
    public void Serialize_ProducesCompactJson()
    {
        var json = JsonHelper.Serialize(new { name = "test", value = 42 });

        json.Should().NotContain("\n");
        json.Should().NotContain("  ");
    }

    [Fact]
    public void Serialize_HandlesAnonymousTypes()
    {
        var json = JsonHelper.Serialize(new { foo = "bar", count = 3 });
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("foo").GetString().Should().Be("bar");
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(3);
    }

    [Fact]
    public void Serialize_KeepsCodeAndNonAsciiReadable_EscapingOnlyWhatJsonRequires()
    {
        // Regression: the default encoder turned `if (n < 0) return "negative";` into
        // `if (n < 0) return "negative";` and each Korean character into \uXXXX.
        const string snippet = "if (n < 0) return \"negative\"; List<T> a && b; it's 테스트";

        var json = JsonHelper.Serialize(new { source = snippet });

        json.Should().Be("{\"source\":\"if (n < 0) return \\\"negative\\\"; List<T> a && b; it's 테스트\"}");
        JsonDocument.Parse(json).RootElement.GetProperty("source").GetString().Should().Be(snippet);
    }
}
