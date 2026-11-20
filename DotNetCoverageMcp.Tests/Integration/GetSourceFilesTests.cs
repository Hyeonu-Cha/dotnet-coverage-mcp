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
        // The sample is capped; the counts per kind cover every excluded file.
        root.GetProperty("excludedByKind").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32())
            .Should().BeEquivalentTo(new Dictionary<string, int>
            {
                ["diRegistration"] = 1, ["entryPoint"] = 1, ["generated"] = 1, ["model"] = 1, ["testCode"] = 1,
            });
    }

    [Fact]
    public void FileOverTheLineBudget_GetsABatchOfItsOwn()
    {
        var dir = Path.Combine(_dir, "Mixed");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Small.cs"), "public class Small { public int F(int n) => n > 0 ? 1 : 0; }");
        File.WriteAllText(Path.Combine(dir, "Big.cs"),
            "public class Big\n{\n    public int F(int n)\n    {\n        return n > 0 ? 1 : 0;\n    }\n}\n");

        using var doc = JsonDocument.Parse(_sut.GetSourceFiles(dir, lineBudget: 3));

        doc.RootElement.GetProperty("batches").EnumerateArray()
            .Select(b => b.EnumerateArray().Select(f => Path.GetFileName(f.GetProperty("path").GetString()!)).ToList())
            .Should().BeEquivalentTo(new[] { new[] { "Small.cs" }, new[] { "Big.cs" } }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void ManyBatches_ComeTenAtATime_TheSameOnEveryCall()
    {
        // Every file of a large project in one response was tens of thousands of tokens.
        var many = Path.Combine(_dir, "Many");
        Directory.CreateDirectory(many);
        for (var i = 0; i < 25; i++)
            File.WriteAllText(Path.Combine(many, $"S{i:00}.cs"), $"public class S{i:00} {{ public int F(int n) => n > {i} ? 1 : 0; }}");

        List<string> Page(int skip, out JsonElement root)
        {
            using var doc = JsonDocument.Parse(_sut.GetSourceFiles(many, lineBudget: 1, skipBatches: skip));
            root = doc.RootElement.Clone();
            return BatchedNames(root);
        }

        var first = Page(0, out var r1);
        r1.GetProperty("batchCount").GetInt32().Should().Be(25);
        r1.GetProperty("batches").GetArrayLength().Should().Be(10);
        r1.GetProperty("moreBatches").GetInt32().Should().Be(15);

        var second = Page(10, out _);
        var last = Page(20, out var r3);
        r3.GetProperty("batches").GetArrayLength().Should().Be(5);
        r3.GetProperty("moreBatches").GetInt32().Should().Be(0);
        first.Concat(second).Concat(last).Should().OnlyHaveUniqueItems("paging needs the same batches on every call")
            .And.HaveCount(25);

        JsonDocument.Parse(_sut.GetSourceFiles(many, skipBatches: -1)).RootElement
            .GetProperty("errorType").GetString().Should().Be("invalidParameter");
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

    [Fact]
    public void Folder_DoesNotFollowSymlinkedDirectoryOutOfScope()
    {
        // A junction/symlink inside the scanned folder that points outside it must not drag
        // foreign .cs files into the batches (they could be anywhere on disk).
        var outside = Path.Combine(Path.GetTempPath(), $"gsf-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Foreign.cs"),
            "public class Foreign { public int F(int n) { if (n > 0) return 1; return 0; } }");
        var link = Path.Combine(_dir, "linked");
        try
        {
            if (!TryCreateDirectorySymlink(link, outside)) return; // no symlink privilege (e.g. Windows CI)

            using var doc = JsonDocument.Parse(_sut.GetSourceFiles(_dir));

            BatchedNames(doc.RootElement).Should().NotContain("Foreign.cs");
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    private static bool TryCreateDirectorySymlink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
