using System.Text.Json;
using DotNetCoverageMcp.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetCoverageMcp.Tests.Integration;

/// <summary>GetSourceFiles over a real folder, with the real FileService.</summary>
public class GetSourceFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gsf-{Guid.NewGuid():N}");
    private readonly CoverageTools _sut;

    public GetSourceFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Models"));
        Directory.CreateDirectory(Path.Combine(_dir, "Services"));
        Directory.CreateDirectory(Path.Combine(_dir, "Tests"));
        // The layout that broke project scope: a test project nested in the app's folder.
        Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("Tests/App.Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup></Project>");
        Write("Tests/OrderServiceTests.cs", "public class OrderServiceTests { [Fact] public void A() { if (true) { } } }");
        Write("Program.cs", "var app = WebApplication.Create(); app.Run();");
        Write("ServiceRegistration.cs", "public static class Reg { public static IServiceCollection AddApp(this IServiceCollection s) => s; }");
        Write("Services/Client.g.cs", "public class Client { public void Call() { if (true) { } } }");
        Write("Models/OrderDto.cs", "public class OrderDto { public int Id { get; set; } }");
        Write("Models/Order.cs", "public class Order { public decimal Total() => 1m; }"); // a model with logic stays
        Write("Services/OrderService.cs", "public class OrderService { public bool Place(int n) { if (n > 0) return true; return false; } }");

        var pathGuard = new Mock<IPathGuard>();
        var fileService = new FileService(NullLogger<FileService>.Instance);
        _sut = new CoverageTools(
            Mock.Of<IProcessRunner>(), Mock.Of<ISessionManager>(), fileService,
            Mock.Of<ICoberturaService>(), Mock.Of<ICodeInserter>(), pathGuard.Object);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Write(string relative, string text) => File.WriteAllText(Path.Combine(_dir, relative), text);

    private static List<string> BatchedNames(JsonElement root) =>
        root.GetProperty("batches").EnumerateArray()
            .SelectMany(b => b.EnumerateArray())
            .Select(f => Path.GetFileName(f.GetProperty("path").GetString()!))
            .OrderBy(n => n)
            .ToList();

    [Fact]
    public void Folder_BatchesOnlyTargets_AndListsExcludedWithReasons()
    {
        using var doc = JsonDocument.Parse(_sut.GetSourceFiles(_dir));
        var root = doc.RootElement;

        BatchedNames(root).Should().Equal("Order.cs", "OrderService.cs");
        root.GetProperty("totalFiles").GetInt32().Should().Be(2);
        root.GetProperty("excludedCount").GetInt32().Should().Be(5);

        var excluded = root.GetProperty("excluded").EnumerateArray()
            .ToDictionary(e => Path.GetFileName(e.GetProperty("path").GetString()!), e => e.GetProperty("kind").GetString());
        excluded.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["Program.cs"] = "entryPoint",
            ["ServiceRegistration.cs"] = "diRegistration",
            ["Client.g.cs"] = "generated",
            ["OrderDto.cs"] = "model",
            ["OrderServiceTests.cs"] = "testCode",
        });
        root.GetProperty("excluded").EnumerateArray()
            .Should().OnlyContain(e => e.GetProperty("reason").GetString()!.Length > 0);
    }

    [Fact]
    public void Project_LeavesOutFilesOfNestedProjects()
    {
        // Regression: a .csproj scope scanned the whole folder, so the nested test project's
        // files became targets the agent was asked to bring to 80%.
        using var doc = JsonDocument.Parse(_sut.GetSourceFiles(Path.Combine(_dir, "App.csproj")));
        var root = doc.RootElement;

        BatchedNames(root).Should().Equal("Order.cs", "OrderService.cs");
        root.GetProperty("excluded").EnumerateArray()
            .Select(e => Path.GetFileName(e.GetProperty("path").GetString()))
            .Should().NotContain("OrderServiceTests.cs", "it belongs to another project, not to this scope");
    }

    [Fact]
    public void ExplicitFiles_AreKeptEvenWhenTheyWouldBeExcluded()
    {
        // Naming a file is how the caller overrides the classifier.
        var list = $"{Path.Combine(_dir, "Program.cs")},{Path.Combine(_dir, "Models", "OrderDto.cs")}";

        using var doc = JsonDocument.Parse(_sut.GetSourceFiles(list));

        BatchedNames(doc.RootElement).Should().Equal("OrderDto.cs", "Program.cs");
        doc.RootElement.GetProperty("excludedCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public void SingleFile_IsKept()
    {
        using var doc = JsonDocument.Parse(_sut.GetSourceFiles(Path.Combine(_dir, "Program.cs")));

        BatchedNames(doc.RootElement).Should().Equal("Program.cs");
    }
}
