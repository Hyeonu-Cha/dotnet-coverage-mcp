using System.Text.Json;
using DotNetCoverageMcp.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetCoverageMcp.Tests.Unit;

public class TestTargetDescriberTests : IDisposable
{
    // A small repo: .git marker, one app project with the types the target depends on, and
    // a test project with an existing test file for the target.
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"desc-{Guid.NewGuid():N}");
    private readonly string _app;

    public TestTargetDescriberTests()
    {
        _app = Path.Combine(_repo, "src", "App");
        Directory.CreateDirectory(_app);
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(Path.Combine(_repo, "tests", "App.Tests"));
        File.WriteAllText(Path.Combine(_app, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("IOrderRepository.cs", "namespace App; public interface IOrderRepository { Task SaveAsync(Order o); }");
        Write("Clock.cs", "namespace App; public sealed class Clock { public DateTime Now() => DateTime.UtcNow; }");
        Write("PricingBase.cs", "namespace App; public abstract class PricingBase { public abstract decimal Price(int qty); }");
        Write("Mailer.cs", "namespace App; public class Mailer { public virtual void Send(string to) { } }");
        Write("AppDbContext.cs", "namespace App; public class AppDbContext : DbContext { public AppDbContext(DbContextOptions o) : base(o) { } }");
        Write("Order.cs", "namespace App; public record Order(int Id, decimal Total);");
        File.WriteAllText(Path.Combine(_repo, "tests", "App.Tests", "OrderServiceTests.cs"), "public class OrderServiceTests { }");
    }

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_app, name);
        File.WriteAllText(path, text);
        return path;
    }

    private const string OrderService = """
        namespace App;

        public class OrderService
        {
            public OrderService(
                IOrderRepository repository, ILogger<OrderService> logger, IOptions<OrderSettings> options,
                Clock clock, PricingBase pricing, Mailer mailer, AppDbContext db, HttpClient http,
                int retries, IEnumerable<string> regions, IExternalGateway gateway, ThirdPartyClient thirdParty)
            { }

            public async Task<bool> PlaceAsync(Order order)
            {
                _logger.LogInformation("Writing {File} for {Directory}", order.Id, order.Total);
                await _lock.RunAsync(async () => await Task.Yield());
                if (order.Total <= 0 || order.Id == 0) return false;
                var stamp = DateTime.UtcNow;
                var id = Guid.NewGuid();
                return order.Total > 100 ? true : false;
            }

            public string Describe(Order order) => order.Id switch { 1 => "first", _ => "other" };

            private void Helper() { }

            private sealed class Nested { void M() => System.IO.File.Delete("x"); }
        }
        """;

    private TargetType DescribeOrderService() =>
        TestTargetDescriber.Describe(Write("OrderService.cs", OrderService)).Types.Single(t => t.Name == "OrderService");

    [Fact]
    public void Dependencies_AreClassifiedFromTheirDeclarationsAndKnownFrameworkTypes()
    {
        var kinds = DescribeOrderService().Dependencies.ToDictionary(d => d.Name, d => d.Kind);

        kinds.Should().Equal(new Dictionary<string, string>
        {
            ["repository"] = "interface",       // declared in the project
            ["logger"] = "logger",
            ["options"] = "options",
            ["clock"] = "sealedClass",          // can't be mocked
            ["pricing"] = "abstract",
            ["mailer"] = "class",
            ["db"] = "dbContext",               // derives from DbContext in the project
            ["http"] = "httpClient",
            ["retries"] = "value",
            ["regions"] = "value",              // a collection, even though it is an interface
            ["gateway"] = "interface",          // external, by the I-prefix convention
            ["thirdParty"] = "external",
        });
    }

    [Fact]
    public void SealedAndDbContextDependencies_ExplainWhatToDo()
    {
        var deps = DescribeOrderService().Dependencies.ToDictionary(d => d.Name, d => d.HowToProvide);

        deps["clock"].Should().Contain("can't be mocked");
        deps["db"].Should().Contain("SQLite");
        deps["logger"].Should().Contain("NullLogger");
    }

    [Fact]
    public void Methods_ArePublicAndInternalOnly_WithDecisionPoints()
    {
        var methods = DescribeOrderService().Methods;

        methods.Select(m => m.Name).Should().Equal("PlaceAsync", "Describe");
        var place = methods[0];
        place.IsAsync.Should().BeTrue();
        place.Signature.Should().Be("Task<bool> PlaceAsync(Order order)");
        place.DecisionPoints.Should().Be(3, "if, ||, ?:");
        methods[1].DecisionPoints.Should().Be(2, "two switch-expression arms");
    }

    [Fact]
    public void HardToTest_FindsStaticCalls_NotStringsLambdasOrNestedTypes()
    {
        var hard = DescribeOrderService().HardToTest;

        hard.Select(h => h.Code).Should().BeEquivalentTo("DateTime.UtcNow", "Guid.NewGuid()");
        hard.Should().OnlyContain(h => h.Suggestion.Length > 0);
    }

    [Fact]
    public void DbContextDependency_SuggestsIntegration()
    {
        var target = DescribeOrderService();

        target.SuggestedTestType.Should().Be("integration");
        target.Reasons.Should().Contain(r => r.Contains("AppDbContext"));
        target.Reasons.Should().Contain(r => r.Contains("Clock is a concrete class"));
    }

    [Fact]
    public void MockableDependenciesOnly_SuggestsUnit_WithPrimaryConstructor()
    {
        var path = Write("Pricer.cs", """
            namespace App;
            public class Pricer(IOrderRepository repository, ILogger<Pricer> logger)
            {
                public decimal Price(int qty) => qty > 10 ? 9m : 10m;
            }
            """);

        var target = TestTargetDescriber.Describe(path).Types.Single();

        target.SuggestedTestType.Should().Be("unit");
        target.Dependencies.Select(d => d.Kind).Should().Equal("interface", "logger");
        target.Reasons.Should().ContainSingle().Which.Should().Contain("mocked");
    }

    [Theory]
    [InlineData("public class OrdersController : ControllerBase { public int Get() => 1; }", "either")]
    [InlineData("public class Worker : BackgroundService { protected override Task ExecuteAsync(CancellationToken t) => Task.CompletedTask; public void Poke() { } }", "integration")]
    public void ControllersAndHostedServices(string code, string expected)
    {
        var target = TestTargetDescriber.Describe(Write("X.cs", $"namespace App; {code}")).Types.Single();

        target.SuggestedTestType.Should().Be(expected);
    }

    [Fact]
    public void RecordsAndDtosWithoutMethods_AreNotListed()
    {
        var path = Write("Mixed.cs", """
            namespace App;
            public record Row(int Id);
            public class Dto { public int Id { get; set; } }
            public class Service { public int Run() => 1; }
            """);

        TestTargetDescriber.Describe(path).Types.Select(t => t.Name).Should().Equal("Service");
    }

    [Fact]
    public void ExistingTestFiles_AreFoundAcrossTheRepository()
    {
        var description = TestTargetDescriber.Describe(Write("OrderService.cs", OrderService));

        description.ExistingTestFiles.Should().ContainSingle()
            .Which.Should().EndWith(Path.Combine("tests", "App.Tests", "OrderServiceTests.cs"));
    }

    [Fact]
    public void ExistingTestFiles_AreNotSearchedForAboveTheAllowedRoot()
    {
        // Regression: the search started at the .git folder even when that was above
        // COVERAGE_MCP_ALLOWED_ROOT, listing files the client was not allowed to read.
        var allowedRoot = Path.Combine(_repo, "src");
        var insideTest = Path.Combine(allowedRoot, "App.Tests", "OrderServiceTests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(insideTest)!);
        File.WriteAllText(insideTest, "public class OrderServiceTests { }");

        var description = TestTargetDescriber.Describe(Write("OrderService.cs", OrderService),
            p => Path.GetFullPath(p).StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase));

        // The one under tests/ is outside src/; the allowed root itself is searched.
        description.ExistingTestFiles.Should().Equal(insideTest);
    }

    [Fact]
    public void ExistingTestFiles_SkipAnUnreadableFolder_InsteadOfFindingNothing()
    {
        if (OperatingSystem.IsWindows()) return; // denying a folder needs ACLs there
        var locked = Path.Combine(_repo, "locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var description = TestTargetDescriber.Describe(Write("OrderService.cs", OrderService));

            description.ExistingTestFiles.Should().ContainSingle()
                .Which.Should().EndWith(Path.Combine("tests", "App.Tests", "OrderServiceTests.cs"));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Tool_ReturnsNoTestableTypes_ForAFileOfRecords()
    {
        var path = Write("Records.cs", "namespace App; public record A(int X); public record B(string Y);");
        var tools = new CoverageTools(Mock.Of<IProcessRunner>(), Mock.Of<ISessionManager>(),
            new FileService(NullLogger<FileService>.Instance), Mock.Of<ICoberturaService>(), Mock.Of<ICodeInserter>(), Mock.Of<IPathGuard>());

        JsonDocument.Parse(tools.DescribeTestTarget(path)).RootElement.GetProperty("errorType").GetString()
            .Should().Be("noTestableTypes");
    }

    [Fact]
    public void ThisRepositorysCodeInserter_IsDescribedCorrectly()
    {
        // Real code: bin/<config>/<tfm>/ → repository root.
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Services", "CodeInserter.cs"));

        var description = TestTargetDescriber.Describe(source);
        var inserter = description.Types.Single(t => t.Name == "CodeInserter");

        inserter.Dependencies.Select(d => (d.Name, d.Kind)).Should().Equal(("fileService", "interface"), ("logger", "logger"));
        inserter.SuggestedTestType.Should().Be("unit");
        inserter.Methods.Should().Contain(m => m.Name == "InsertCodeAsync" && m.IsAsync);
        description.ExistingTestFiles.Should().Contain(f => f.EndsWith("CodeInserterTests.cs", StringComparison.Ordinal));
    }
}
