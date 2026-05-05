using System.Text.Json;
using DotNetCoverageMcp.Helpers;
using FluentAssertions;

namespace DotNetCoverageMcp.Tests.Unit;

public class JsonHelperTests
{
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
}
