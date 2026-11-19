using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotNetCoverageMcp.Helpers;

namespace DotNetCoverageMcp.Services;

public record TestConventions(
    string TestClass, string Test, string Parameterized, string Setup, string Teardown, string AsyncSetup);

public record TestEnvironment(
    string Summary,
    string? Framework,
    string? FrameworkVersion,
    string Runner,
    string? Coverage,
    string? Mocking,
    string? Assertions,
    List<string> Integration,
    List<string> MtpExtensions,
    TestConventions? Conventions,
    List<string> Warnings,
    string Source);

/// <summary>
/// Reads a test project's packages and settings to tell an agent which framework, mocking
/// and assertion libraries to write tests with, and which runner the project uses. Without
/// this, a model asked to add tests tends to default to one stack (e.g. xUnit + Moq) and
/// writes tests that don't compile in an NUnit/NSubstitute project.
/// </summary>
public static class TestEnvironmentDetector
{
    public const string Vstest = "VSTest";
    public const string Mtp = "Microsoft.Testing.Platform";

    private static readonly Dictionary<string, TestConventions> ConventionsByFramework = new()
    {
        ["xunit"] = new("plain public class, no attribute", "[Fact]", "[Theory] + [InlineData(...)]",
            "constructor", "IDisposable.Dispose()", "IAsyncLifetime.InitializeAsync()"),
        ["nunit"] = new("[TestFixture]", "[Test]", "[TestCase(...)]", "[SetUp]", "[TearDown]", "[SetUp] public async Task ..."),
        ["mstest"] = new("[TestClass]", "[TestMethod]", "[TestMethod] + [DataRow(...)]", "[TestInitialize]", "[TestCleanup]",
            "[TestInitialize] public async Task ..."),
    };

    private static readonly (string Package, string Name)[] Mocking =
        [("Moq", "Moq"), ("NSubstitute", "NSubstitute"), ("FakeItEasy", "FakeItEasy")];

    private static readonly (string Package, string Name)[] Assertions =
        [("FluentAssertions", "FluentAssertions"), ("AwesomeAssertions", "AwesomeAssertions"), ("Shouldly", "Shouldly")];

    private static readonly string[] IntegrationPackages =
    [
        "Microsoft.AspNetCore.Mvc.Testing", "Microsoft.EntityFrameworkCore.InMemory", "Microsoft.EntityFrameworkCore.Sqlite",
        "WireMock.Net", "Respawn"
    ];

    private static readonly string[] CoveragePackages =
        ["coverlet.collector", "coverlet.msbuild", "coverlet.MTP", "Microsoft.Testing.Extensions.CodeCoverage"];

    private static readonly string[] MtpExtensionPackages =
    [
        "Microsoft.Testing.Extensions.CodeCoverage", "Microsoft.Testing.Extensions.TrxReport", "Microsoft.Testing.Extensions.HangDump"
    ];

    // Properties that switch a project to Microsoft.Testing.Platform.
    private static readonly string[] MtpProperties = ["EnableMSTestRunner", "EnableNUnitRunner", "UseMicrosoftTestingPlatformRunner"];

    public static TestEnvironment Detect(string csprojPath)
    {
        var warnings = new List<string>();
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
        var csproj = XmlFile.Load(csprojPath);
        var sdks = Sdks(csproj);

        // Direct references say what the tests are written with; the full resolved graph
        // also shows what came in transitively (e.g. MTP extensions an SDK brings).
        Dictionary<string, string?> direct;
        Dictionary<string, string> resolved;
        string source;
        var assetsPath = Path.Combine(projectDir, "obj", "project.assets.json");
        if (File.Exists(assetsPath) && ReadAssets(assetsPath) is { } assets)
        {
            (direct, resolved) = assets;
            source = "obj/project.assets.json";
        }
        else
        {
            direct = PackageReferences(csproj);
            resolved = direct.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase);
            source = ".csproj (not restored)";
            warnings.Add("obj/project.assets.json not found, so packages were read from the .csproj only and centrally managed versions are unknown. Restore the project for exact results.");
        }

        bool Has(string package) => direct.ContainsKey(package);
        bool HasAnywhere(string package) => direct.ContainsKey(package) || resolved.ContainsKey(package);
        string? Version(string package) =>
            resolved.TryGetValue(package, out var v) ? v : direct.TryGetValue(package, out var d) ? CleanRange(d) : null;

        var hasMstestSdk = sdks.Any(s => s.StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase));
        var frameworks = new List<(string Name, string Display, string? Version)>();
        if (Has("xunit.v3") || direct.Keys.Any(k => k.StartsWith("xunit.v3", StringComparison.OrdinalIgnoreCase)))
            frameworks.Add(("xunit", "xunit.v3", Version("xunit.v3") ?? Version("xunit.v3.core")));
        else if (Has("xunit") || Has("xunit.core"))
            frameworks.Add(("xunit", "xunit", Version("xunit") ?? Version("xunit.core")));
        if (Has("NUnit"))
            frameworks.Add(("nunit", "NUnit", Version("NUnit")));
        if (hasMstestSdk || Has("MSTest") || Has("MSTest.TestFramework"))
            frameworks.Add(("mstest", "MSTest", Version("MSTest") ?? Version("MSTest.TestFramework") ?? SdkVersion(sdks, "MSTest.Sdk")));

        if (frameworks.Count == 0)
            warnings.Add("No xUnit, NUnit or MSTest reference found; this may not be a test project.");
        else if (frameworks.Count > 1)
            warnings.Add($"Several test frameworks are referenced ({string.Join(", ", frameworks.Select(f => f.Display))}); match the attributes used in the existing test files.");

        var runner = UsesMtp(csprojPath, projectDir, hasMstestSdk) ? Mtp : Vstest;
        // Under MTP only Microsoft's extension is driven by RunTestsWithCoverage; under VSTest, coverlet.
        var coverage = runner == Mtp
            ? (HasAnywhere("Microsoft.Testing.Extensions.CodeCoverage") ? "Microsoft.Testing.Extensions.CodeCoverage" : null)
            : CoveragePackages.FirstOrDefault(HasAnywhere);
        if (coverage == null)
            warnings.Add(runner == Vstest
                ? "No coverage collector is referenced. Add the coverlet.collector package, or RunTestsWithCoverage will return noCoverage."
                : "Microsoft.Testing.Extensions.CodeCoverage is not referenced. Add it; RunTestsWithCoverage collects coverage on Microsoft.Testing.Platform through that extension.");
        if (runner == Mtp && !DotnetTestUsesMtp(projectDir))
            warnings.Add("This project uses Microsoft.Testing.Platform, but no global.json sets \"test\": { \"runner\": \"Microsoft.Testing.Platform\" }. On the .NET 10 SDK and later, `dotnet test` rejects such projects in its default VSTest mode; add that setting to the repository's global.json.");

        var framework = frameworks.FirstOrDefault();
        var mocking = Mocking.FirstOrDefault(m => Has(m.Package)).Name;
        var assertions = Assertions.FirstOrDefault(a => Has(a.Package)).Name;
        var integration = IntegrationPackages.Where(Has)
            .Concat(direct.Keys.Where(k => k.StartsWith("Testcontainers", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var extensions = MtpExtensionPackages.Where(HasAnywhere).ToList();

        var summary = string.Join(", ", new[]
        {
            (framework.Display == null ? "no test framework" : $"{framework.Display} {framework.Version}".Trim()) + $" on {runner}",
            coverage ?? "no coverage collector",
            mocking == null && assertions == null ? null : string.Join(" + ", new[] { mocking, assertions }.Where(x => x != null)),
        }.Where(x => x != null));

        return new TestEnvironment(
            summary,
            framework.Display,
            framework.Version,
            runner,
            coverage,
            mocking,
            assertions ?? (framework.Name == null ? null : "built-in Assert"),
            integration,
            extensions,
            framework.Name == null ? null : ConventionsByFramework[framework.Name],
            warnings,
            source);
    }

    private static bool UsesMtp(string csprojPath, string projectDir, bool hasMstestSdk)
    {
        // Project file and every Directory.Build.props above it, nearest first.
        var files = new List<string> { csprojPath };
        for (var dir = new DirectoryInfo(projectDir); dir != null; dir = dir.Parent)
        {
            var props = Path.Combine(dir.FullName, "Directory.Build.props");
            if (File.Exists(props)) files.Add(props);
        }

        foreach (var file in files)
        {
            var text = SafeRead(file);
            if (Regex.IsMatch(text, @"<UseVSTest>\s*true\s*</UseVSTest>", RegexOptions.IgnoreCase)) return false;
            if (MtpProperties.Any(p => Regex.IsMatch(text, $@"<{p}>\s*true\s*</{p}>", RegexOptions.IgnoreCase))) return true;
        }

        // MSTest.Sdk runs on MTP unless the project opts back into VSTest (checked above).
        return hasMstestSdk || DotnetTestUsesMtp(projectDir);
    }

    /// <summary>
    /// Whether `dotnet test` started in this directory runs in Microsoft.Testing.Platform mode:
    /// the nearest global.json sets test.runner to it (.NET 10 SDK and later). This, not the
    /// project's packages, decides the command line, because on SDK 10+ an MTP project run in
    /// the default VSTest mode fails with "Testing with VSTest target is no longer supported".
    /// </summary>
    public static bool DotnetTestUsesMtp(string workingDir)
    {
        for (var dir = new DirectoryInfo(workingDir); dir != null; dir = dir.Parent)
        {
            var globalJson = Path.Combine(dir.FullName, "global.json");
            if (!File.Exists(globalJson)) continue;
            try
            {
                using var doc = JsonDocument.Parse(SafeRead(globalJson),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return doc.RootElement.TryGetProperty("test", out var test)
                       && test.TryGetProperty("runner", out var r)
                       && string.Equals(r.GetString(), Mtp, StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException) { return false; }
        }
        return false;
    }

    private static (Dictionary<string, string?> Direct, Dictionary<string, string> Resolved)? ReadAssets(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var direct = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("project", out var project) && project.TryGetProperty("frameworks", out var frameworks))
                foreach (var tfm in frameworks.EnumerateObject())
                    if (tfm.Value.TryGetProperty("dependencies", out var deps))
                        foreach (var dep in deps.EnumerateObject())
                            direct.TryAdd(dep.Name, dep.Value.TryGetProperty("version", out var v) ? v.GetString() : null);

            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("libraries", out var libraries))
                foreach (var lib in libraries.EnumerateObject())
                {
                    var slash = lib.Name.IndexOf('/');
                    if (slash > 0) resolved.TryAdd(lib.Name[..slash], lib.Name[(slash + 1)..]);
                }
            return (direct, resolved);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<string, string?> PackageReferences(XDocument csproj) =>
        csproj.Descendants().Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (Name: (string?)e.Attribute("Include"),
                Version: (string?)e.Attribute("Version") ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == "Version")?.Value))
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .GroupBy(p => p.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Version, StringComparer.OrdinalIgnoreCase);

    // <Project Sdk="MSTest.Sdk/3.6.4"> and <Sdk Name="MSTest.Sdk" Version="..." />
    private static List<string> Sdks(XDocument csproj)
    {
        var sdks = ((string?)csproj.Root?.Attribute("Sdk") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        sdks.AddRange(csproj.Descendants().Where(e => e.Name.LocalName == "Sdk")
            .Select(e => $"{(string?)e.Attribute("Name")}/{(string?)e.Attribute("Version")}"));
        return sdks;
    }

    private static string? SdkVersion(List<string> sdks, string name) =>
        sdks.FirstOrDefault(s => s.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase)) is { } s
            ? s[(name.Length + 1)..].NullIfEmpty()
            : null;

    // "[8.11.0, )" → "8.11.0"
    private static string? CleanRange(string? range) =>
        range?.Trim('[', ']', '(', ')', ' ').Split(',')[0].Trim().NullIfEmpty();

    private static string? NullIfEmpty(this string s) => s.Length == 0 ? null : s;

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
