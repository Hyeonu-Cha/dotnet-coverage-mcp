using System.Text.Json;
using DotNetCoverageMcp.Services;
using FluentAssertions;

namespace DotNetCoverageMcp.Tests.Unit;

public class TestEnvironmentDetectorTests : IDisposable
{
    // Nested one level so a Directory.Build.props or global.json written by a test sits
    // above the project, as it would in a real repo.
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"env-{Guid.NewGuid():N}");
    private readonly string _projectDir;

    public TestEnvironmentDetectorTests()
    {
        _projectDir = Path.Combine(_repo, "tests", "App.Tests");
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private string Csproj(string body, string sdk = "Microsoft.NET.Sdk")
    {
        var path = Path.Combine(_projectDir, "App.Tests.csproj");
        File.WriteAllText(path, $"<Project Sdk=\"{sdk}\">{body}</Project>");
        return path;
    }

    private static string Refs(params string[] packages) =>
        "<ItemGroup>" + string.Concat(packages.Select(p => $"<PackageReference Include=\"{p}\" Version=\"1.0.0\" />")) + "</ItemGroup>";

    // Minimal project.assets.json: direct dependencies plus resolved libraries.
    private void Assets(Dictionary<string, string> direct, params string[] transitive)
    {
        var obj = Path.Combine(_projectDir, "obj");
        Directory.CreateDirectory(obj);
        var libraries = direct.Select(d => $"{d.Key}/{d.Value}").Concat(transitive)
            .ToDictionary(k => k, _ => new { type = "package" });
        File.WriteAllText(Path.Combine(obj, "project.assets.json"), JsonSerializer.Serialize(new
        {
            version = 3,
            libraries,
            project = new
            {
                frameworks = new Dictionary<string, object>
                {
                    ["net10.0"] = new { dependencies = direct.ToDictionary(d => d.Key, d => new { target = "Package", version = $"[{d.Value}, )" }) }
                }
            }
        }));
    }

    [Fact]
    public void XunitWithMoqAndFluentAssertions_FromAssets()
    {
        var csproj = Csproj("");
        Assets(new()
        {
            ["xunit"] = "2.9.3", ["xunit.runner.visualstudio"] = "4.0.1", ["Microsoft.NET.Test.Sdk"] = "18.12.0",
            ["coverlet.collector"] = "10.1.1", ["Moq"] = "4.21.0", ["FluentAssertions"] = "8.11.0",
        });

        var env = TestEnvironmentDetector.Detect(csproj);

        env.Framework.Should().Be("xunit");
        env.FrameworkVersion.Should().Be("2.9.3");
        env.Runner.Should().Be(TestEnvironmentDetector.Vstest);
        env.Coverage.Should().Be("coverlet.collector");
        env.Mocking.Should().Be("Moq");
        env.Assertions.Should().Be("FluentAssertions");
        env.Conventions!.Test.Should().Be("[Fact]");
        env.Warnings.Should().BeEmpty();
        env.Summary.Should().Be("xunit 2.9.3 on VSTest, coverlet.collector, Moq + FluentAssertions");
    }

    [Fact]
    public void NUnitWithNSubstituteAndShouldly_FromCsprojWhenNotRestored()
    {
        var env = TestEnvironmentDetector.Detect(Csproj(Refs("NUnit", "NUnit3TestAdapter", "coverlet.collector", "NSubstitute", "Shouldly")));

        env.Framework.Should().Be("NUnit");
        env.Mocking.Should().Be("NSubstitute");
        env.Assertions.Should().Be("Shouldly");
        env.Conventions!.Setup.Should().Be("[SetUp]");
        env.Conventions.Parameterized.Should().Be("[TestCase(...)]");
        env.Source.Should().Contain("not restored");
        env.Warnings.Should().ContainSingle().Which.Should().Contain("project.assets.json");
    }

    [Fact]
    public void MSTestSdk_RunsOnMtp_WithExtensionsFromTheResolvedGraph()
    {
        var csproj = Csproj("", sdk: "MSTest.Sdk/3.8.0");
        // MSTest.Sdk brings its framework and extensions transitively.
        Assets(new(), "MSTest.TestFramework/3.8.0", "Microsoft.Testing.Extensions.CodeCoverage/17.13.1",
            "Microsoft.Testing.Extensions.TrxReport/1.6.0");

        var env = TestEnvironmentDetector.Detect(csproj);

        env.Framework.Should().Be("MSTest");
        env.Runner.Should().Be(TestEnvironmentDetector.Mtp);
        env.Coverage.Should().Be("Microsoft.Testing.Extensions.CodeCoverage");
        env.MtpExtensions.Should().BeEquivalentTo("Microsoft.Testing.Extensions.CodeCoverage", "Microsoft.Testing.Extensions.TrxReport");
        env.Conventions!.TestClass.Should().Be("[TestClass]");
        env.Warnings.Should().Contain(w => w.Contains("Microsoft.Testing.Platform"));
    }

    [Fact]
    public void MSTestSdk_WithUseVSTest_StaysOnVstest() =>
        TestEnvironmentDetector.Detect(Csproj("<PropertyGroup><UseVSTest>true</UseVSTest></PropertyGroup>", sdk: "MSTest.Sdk/3.8.0"))
            .Runner.Should().Be(TestEnvironmentDetector.Vstest);

    [Theory]
    [InlineData("UseMicrosoftTestingPlatformRunner")]
    [InlineData("EnableNUnitRunner")]
    [InlineData("EnableMSTestRunner")]
    public void MtpProperty_InTheProject_SwitchesRunner(string property) =>
        TestEnvironmentDetector.Detect(Csproj($"<PropertyGroup><{property}>true</{property}></PropertyGroup>" + Refs("xunit.v3")))
            .Runner.Should().Be(TestEnvironmentDetector.Mtp);

    [Fact]
    public void MtpProperty_InDirectoryBuildProps_SwitchesRunner()
    {
        File.WriteAllText(Path.Combine(_repo, "Directory.Build.props"),
            "<Project><PropertyGroup><EnableNUnitRunner>true</EnableNUnitRunner></PropertyGroup></Project>");

        TestEnvironmentDetector.Detect(Csproj(Refs("NUnit"))).Runner.Should().Be(TestEnvironmentDetector.Mtp);
    }

    [Fact]
    public void MtpProjectWithoutGlobalJson_WarnsThatDotnetTestWillRejectIt()
    {
        var env = TestEnvironmentDetector.Detect(Csproj(
            "<PropertyGroup><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup>" +
            Refs("xunit.v3", "Microsoft.Testing.Extensions.CodeCoverage")));

        env.Runner.Should().Be(TestEnvironmentDetector.Mtp);
        env.Warnings.Should().Contain(w => w.Contains("global.json"));
    }

    [Fact]
    public void MtpWithCoverletOnly_WarnsThatMicrosoftsExtensionIsNeeded()
    {
        File.WriteAllText(Path.Combine(_repo, "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");

        var env = TestEnvironmentDetector.Detect(Csproj(Refs("xunit.v3", "coverlet.collector")));

        env.Coverage.Should().BeNull("RunTestsWithCoverage drives Microsoft's extension on MTP, not coverlet");
        env.Warnings.Should().Contain(w => w.Contains("Microsoft.Testing.Extensions.CodeCoverage"));
    }

    [Fact]
    public void GlobalJsonTestRunner_SwitchesRunner()
    {
        File.WriteAllText(Path.Combine(_repo, "global.json"),
            """{ "sdk": { "version": "10.0.100" }, "test": { "runner": "Microsoft.Testing.Platform" } }""");

        TestEnvironmentDetector.Detect(Csproj(Refs("xunit.v3"))).Runner.Should().Be(TestEnvironmentDetector.Mtp);
    }

    [Fact]
    public void XunitV3_IsReportedAsV3()
    {
        var env = TestEnvironmentDetector.Detect(Csproj(Refs("xunit.v3", "xunit.runner.visualstudio", "coverlet.collector")));

        env.Framework.Should().Be("xunit.v3");
        env.Runner.Should().Be(TestEnvironmentDetector.Vstest, "xunit.v3 stays on VSTest unless MTP is switched on");
    }

    [Fact]
    public void NoCoverageCollector_Warns() =>
        TestEnvironmentDetector.Detect(Csproj(Refs("xunit")))
            .Warnings.Should().Contain(w => w.Contains("coverlet.collector"));

    [Fact]
    public void IntegrationPackages_AreListed() =>
        TestEnvironmentDetector.Detect(Csproj(Refs("xunit", "Microsoft.AspNetCore.Mvc.Testing", "Testcontainers.PostgreSql")))
            .Integration.Should().BeEquivalentTo("Microsoft.AspNetCore.Mvc.Testing", "Testcontainers.PostgreSql");

    [Fact]
    public void NoTestFramework_HasNoConventionsAndWarns()
    {
        var env = TestEnvironmentDetector.Detect(Csproj(Refs("Microsoft.Extensions.Hosting")));

        env.Framework.Should().BeNull();
        env.Conventions.Should().BeNull();
        env.Warnings.Should().Contain(w => w.Contains("may not be a test project"));
    }

    [Fact]
    public void ThisRepositorysTestProject_IsDetectedFromItsRealAssetsFile()
    {
        // bin/<config>/<tfm>/ → the test project folder, restored by the build that ran these tests.
        var csproj = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "DotNetCoverageMcp.Tests.csproj"));

        var env = TestEnvironmentDetector.Detect(csproj);

        env.Source.Should().Be("obj/project.assets.json");
        (env.Framework, env.Runner, env.Coverage, env.Mocking, env.Assertions)
            .Should().Be(("xunit", TestEnvironmentDetector.Vstest, "coverlet.collector", "Moq", "FluentAssertions"));
    }
}
