using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetCoverageMcp.Services;

public record TestTargetDescription(string File, List<TargetType> Types, List<string> ExistingTestFiles);

public record TargetType(
    string Name, string? Namespace, string? BaseTypes, string SuggestedTestType, List<string> Reasons,
    List<Dependency> Dependencies, List<TargetMethod> Methods, List<HardToTest> HardToTest);

public record Dependency(string Name, string Type, string Kind, string HowToProvide);

public record TargetMethod(string Name, string Signature, bool IsAsync, int FirstLine, int LastLine, int DecisionPoints);

public record HardToTest(int Line, string Code, string Suggestion);

/// <summary>
/// What a test for a class needs, read from syntax alone: constructor dependencies and how
/// to supply each one, the methods to cover with their decision points, and calls a test
/// cannot control. Lets the agent choose between a mock-based unit test and an integration
/// test before writing one, instead of finding out from a failed build that a sealed or
/// concrete dependency cannot be mocked.
/// </summary>
public static class TestTargetDescriber
{
    private const int MaxHardToTest = 10;
    private const int MaxTestFiles = 5;

    // Framework types with a standard test-time substitute. Matched on the name without
    // namespace and generic arguments.
    private static readonly Dictionary<string, (string Kind, string How)> KnownTypes = new(StringComparer.Ordinal)
    {
        ["ILogger"] = ("logger", "NullLogger<T>.Instance (Microsoft.Extensions.Logging.Abstractions), or a mock to assert on log calls"),
        ["ILoggerFactory"] = ("logger", "NullLoggerFactory.Instance"),
        ["IOptions"] = ("options", "Options.Create(new T { ... })"),
        ["IOptionsSnapshot"] = ("options", "a mock returning the options object from .Value"),
        ["IOptionsMonitor"] = ("options", "a mock returning the options object from .CurrentValue"),
        ["IConfiguration"] = ("configuration", "new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ... }).Build()"),
        ["HttpClient"] = ("httpClient", "new HttpClient(stubHandler) { BaseAddress = ... } with a stub HttpMessageHandler returning canned responses"),
        ["IHttpClientFactory"] = ("httpClientFactory", "a mock whose CreateClient returns an HttpClient over a stub HttpMessageHandler"),
        ["TimeProvider"] = ("timeProvider", "FakeTimeProvider (Microsoft.Extensions.TimeProvider.Testing)"),
        ["IServiceProvider"] = ("serviceProvider", "new ServiceCollection().Add...(...).BuildServiceProvider()"),
        ["IServiceScopeFactory"] = ("serviceProvider", "a real ServiceCollection's BuildServiceProvider().GetRequiredService<IServiceScopeFactory>()"),
        ["IMemoryCache"] = ("cache", "new MemoryCache(new MemoryCacheOptions())"),
        ["CancellationToken"] = ("value", "CancellationToken.None, or a CancellationTokenSource to test cancellation"),
    };

    private static readonly HashSet<string> ValueTypes =
    [
        "string", "int", "long", "short", "byte", "bool", "double", "float", "decimal", "char", "Guid", "DateTime",
        "DateTimeOffset", "TimeSpan", "Uri", "object"
    ];

    // Collections are passed as values in a test, even the interface-typed ones.
    private static readonly HashSet<string> CollectionTypes =
    [
        "List", "IList", "IReadOnlyList", "IEnumerable", "ICollection", "IReadOnlyCollection", "Dictionary",
        "IDictionary", "IReadOnlyDictionary", "HashSet", "ISet", "IReadOnlySet", "ImmutableArray", "ImmutableList"
    ];

    // Static members whose result a test cannot set, keyed by the type they are called on,
    // with the usual seam. Matched on syntax (receiver is the type name itself), so string
    // literals and lambda bodies passed to other calls don't trigger it.
    private static readonly Dictionary<string, (Func<string, bool> Member, string Suggestion)> StaticCalls = new()
    {
        ["DateTime"] = (m => m is "Now" or "UtcNow" or "Today", "inject TimeProvider and use FakeTimeProvider in tests"),
        ["DateTimeOffset"] = (m => m is "Now" or "UtcNow", "inject TimeProvider and use FakeTimeProvider in tests"),
        ["Guid"] = (m => m == "NewGuid", "inject an ID generator, or assert on the shape rather than the value"),
        ["File"] = (_ => true, "use a temp directory per test, or inject a file-system abstraction"),
        ["Directory"] = (_ => true, "use a temp directory per test, or inject a file-system abstraction"),
        ["Path"] = (m => m is "GetTempPath" or "GetTempFileName", "use a temp directory per test"),
        ["Environment"] = (m => m is "GetEnvironmentVariable" or "CurrentDirectory" or "MachineName" or "UserName" or "ProcessorCount" or "Exit",
            "pass the value in (options or constructor) instead of reading the environment"),
        ["Random"] = (m => m == "Shared", "inject a Random with a fixed seed"),
        ["Thread"] = (m => m == "Sleep", "inject TimeProvider so tests don't wait"),
        ["Console"] = (m => m.StartsWith("Write", StringComparison.Ordinal) || m.StartsWith("Read", StringComparison.Ordinal),
            "inject a TextWriter / TextReader"),
    };

    private static readonly Dictionary<string, string> CreatedTypes = new()
    {
        ["HttpClient"] = "inject HttpClient or IHttpClientFactory",
        ["Random"] = "inject a Random with a fixed seed",
    };

    /// <param name="isAllowed">Whether a folder may be searched for existing test files; the
    /// tool passes the allowed-root check so the search never reaches above it.</param>
    public static TestTargetDescription Describe(string sourceFilePath, Func<string, bool>? isAllowed = null)
    {
        var text = File.ReadAllText(sourceFilePath);
        var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        var declarations = new ProjectDeclarations(sourceFilePath);

        var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax)
            .Where(t => !t.Modifiers.Any(SyntaxKind.StaticKeyword) || t.Members.OfType<MethodDeclarationSyntax>().Any())
            .Select(t => DescribeType(t, declarations))
            // Records, exceptions and DTOs without methods have nothing for a test to call.
            .Where(t => t.Methods.Count > 0)
            .ToList();

        var names = types.Select(t => SimpleName(t.Name)).Append(Path.GetFileNameWithoutExtension(sourceFilePath)).Distinct().ToList();
        return new TestTargetDescription(sourceFilePath, types, FindTestFiles(sourceFilePath, names, isAllowed ?? (_ => true)));
    }

    private static TargetType DescribeType(TypeDeclarationSyntax type, ProjectDeclarations declarations)
    {
        var name = type.Identifier.Text + (type.TypeParameterList?.ToString() ?? "");
        var ns = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        var baseTypes = type.BaseList?.Types.Select(b => b.Type.ToString()).ToList() ?? [];

        // DI picks the constructor with the most parameters; a primary constructor counts too.
        var ctorParameters = type.Members.OfType<ConstructorDeclarationSyntax>()
            .Where(c => !c.Modifiers.Any(SyntaxKind.StaticKeyword))
            .Select(c => c.ParameterList)
            .Append(type.ParameterList)
            .Where(p => p != null)
            .MaxBy(p => p!.Parameters.Count)?.Parameters ?? default;

        var dependencies = ctorParameters
            .Select(p => DescribeDependency(p.Identifier.Text, p.Type?.ToString() ?? "?", declarations))
            .ToList();

        var methods = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword) || m.Modifiers.Any(SyntaxKind.InternalKeyword))
            .Select(m =>
            {
                var span = m.GetLocation().GetLineSpan();
                return new TargetMethod(
                    m.Identifier.Text,
                    Signature(m),
                    m.Modifiers.Any(SyntaxKind.AsyncKeyword) || m.ReturnType.ToString() is var rt
                        && (rt.StartsWith("Task", StringComparison.Ordinal) || rt.StartsWith("ValueTask", StringComparison.Ordinal)),
                    span.StartLinePosition.Line + 1,
                    span.EndLinePosition.Line + 1,
                    DecisionPoints(m));
            })
            .ToList();

        var hardToTest = FindHardToTest(type);
        var (suggested, reasons) = Suggest(type, baseTypes, dependencies, hardToTest);

        return new TargetType(name, ns, baseTypes.Count == 0 ? null : string.Join(", ", baseTypes),
            suggested, reasons, dependencies, methods, hardToTest);
    }

    private static Dependency DescribeDependency(string name, string typeText, ProjectDeclarations declarations)
    {
        var simple = SimpleName(typeText);

        if (KnownTypes.TryGetValue(simple, out var known))
            return new Dependency(name, typeText, known.Kind, known.How);
        if (ValueTypes.Contains(simple.TrimEnd('?')) || typeText.EndsWith("[]", StringComparison.Ordinal)
            || typeText.TrimStart().StartsWith('(') || CollectionTypes.Contains(simple))
            return new Dependency(name, typeText, "value", "pass a value");
        if (simple is "DbContext" || simple.EndsWith("DbContext", StringComparison.Ordinal)
            || declarations.BaseTypesOf(simple)?.Contains("DbContext", StringComparison.Ordinal) == true)
            return new Dependency(name, typeText, "dbContext",
                "a real context on EF Core's SQLite in-memory provider (closer to a real database than UseInMemoryDatabase); mocking a DbContext is not practical");

        switch (declarations.KindOf(simple))
        {
            case "interface":
                return new Dependency(name, typeText, "interface", "mock it with the project's mocking library");
            case "abstract":
                return new Dependency(name, typeText, "abstract", "mock it (abstract and virtual members only), or a small hand-written subclass");
            case "sealed":
                return new Dependency(name, typeText, "sealedClass",
                    "construct a real instance: sealed classes can't be mocked by Moq, NSubstitute or FakeItEasy. If that is hard, introduce an interface for it");
            case "record" or "struct" or "enum":
                return new Dependency(name, typeText, "value", "construct a value");
            case "class":
                return new Dependency(name, typeText, "class",
                    "construct a real instance (with its own dependencies); mocking only works for its virtual members");
        }

        // Declared outside this project: fall back on the I-prefix convention for interfaces.
        return simple.Length > 1 && simple[0] == 'I' && char.IsUpper(simple[1])
            ? new Dependency(name, typeText, "interface", "mock it with the project's mocking library")
            : new Dependency(name, typeText, "external", "construct it, or check whether it has an interface or a test double in its package");
    }

    private static (string Suggested, List<string> Reasons) Suggest(
        TypeDeclarationSyntax type, List<string> baseTypes, List<Dependency> dependencies, List<HardToTest> hardToTest)
    {
        var reasons = new List<string>();
        var integration = false;
        var either = false;

        var baseSimple = baseTypes.Select(SimpleName).ToList();
        if (baseSimple.Any(b => b is "BackgroundService" or "IHostedService"))
        {
            integration = true;
            reasons.Add("hosted/background service: start it in a host (or call ExecuteAsync with a cancellable token) and observe its effects");
        }
        if (baseSimple.Any(b => b is "Controller" or "ControllerBase") || type.Identifier.Text.EndsWith("Controller", StringComparison.Ordinal))
        {
            either = true;
            reasons.Add("ASP.NET controller: unit-test actions with mocked services, or use WebApplicationFactory (Microsoft.AspNetCore.Mvc.Testing) to cover routing, binding and filters");
        }
        foreach (var d in dependencies.Where(d => d.Kind == "dbContext"))
        {
            integration = true;
            reasons.Add($"depends on {d.Type}: test against a real context on an in-memory SQLite database");
        }
        foreach (var d in dependencies.Where(d => d.Kind is "sealedClass" or "class"))
            reasons.Add($"{d.Type} is a concrete class: tests need a real instance, so its own dependencies come along");
        if (hardToTest.Count > 0)
            reasons.Add($"{hardToTest.Count} call(s) a test cannot control (see hardToTest); branches that depend on them may stay uncovered without a seam");
        if (reasons.Count == 0)
            reasons.Add(dependencies.Count == 0
                ? "no constructor dependencies: instantiate it directly"
                : "every dependency can be mocked or given a test value");

        return (integration ? "integration" : either ? "either" : "unit", reasons);
    }

    // Return type, name and parameters without attributes, which can be long (e.g. [Description]).
    private static string Signature(MethodDeclarationSyntax m) =>
        $"{m.ReturnType} {m.Identifier}{m.TypeParameterList}(" + string.Join(", ", m.ParameterList.Parameters.Select(p =>
            string.Join(" ", new[] { p.Modifiers.ToString(), p.Type?.ToString(), p.Identifier.Text, p.Default?.ToString() }
                .Where(x => !string.IsNullOrEmpty(x))))) + ")";

    // Each construct that adds a path through the method; a rough guide to how many tests it needs.
    private static int DecisionPoints(MethodDeclarationSyntax method) =>
        method.DescendantNodes().Count(n => n.Kind() is
            SyntaxKind.IfStatement or SyntaxKind.ConditionalExpression or SyntaxKind.CaseSwitchLabel or
            SyntaxKind.CasePatternSwitchLabel or SyntaxKind.SwitchExpressionArm or SyntaxKind.CoalesceExpression or
            SyntaxKind.CoalesceAssignmentExpression or SyntaxKind.ConditionalAccessExpression or SyntaxKind.LogicalAndExpression or
            SyntaxKind.LogicalOrExpression or SyntaxKind.CatchClause or SyntaxKind.ForStatement or SyntaxKind.ForEachStatement or
            SyntaxKind.WhileStatement or SyntaxKind.DoStatement);

    private static List<HardToTest> FindHardToTest(TypeDeclarationSyntax type)
    {
        var found = new List<HardToTest>();
        // This type's own code only; nested types are described on their own.
        foreach (var node in type.DescendantNodes(n => n == type || n is not TypeDeclarationSyntax))
        {
            string? suggestion = null;
            SyntaxNode shown = node;
            switch (node)
            {
                case MemberAccessExpressionSyntax ma when ReceiverTypeName(ma.Expression) is { } receiver
                                                          && StaticCalls.TryGetValue(receiver, out var call)
                                                          && call.Member(ma.Name.Identifier.Text):
                    suggestion = call.Suggestion;
                    if (ma.Parent is InvocationExpressionSyntax invocation) shown = invocation;
                    break;
                case ObjectCreationExpressionSyntax creation
                    when CreatedTypes.TryGetValue(SimpleName(creation.Type.ToString()), out var s):
                    suggestion = s;
                    break;
            }
            if (suggestion == null) continue;

            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (found.Any(f => f.Line == line)) continue;
            var code = Regex.Replace(shown.ToString(), @"\s+", " ");
            found.Add(new HardToTest(line, code.Length <= 80 ? code : code[..80] + "…", suggestion));
            if (found.Count >= MaxHardToTest) break;
        }
        return found;
    }

    // "File" for `File.X` and `System.IO.File.X`; null when the receiver is an instance.
    private static string? ReceiverTypeName(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax { Name: IdentifierNameSyntax last } ma when ma.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax
            => last.Identifier.Text,
        _ => null
    };

    // "ILogger<Foo>" → "ILogger", "Ns.IRepo" → "IRepo", "IRepo?" → "IRepo"
    private static string SimpleName(string typeText)
    {
        var t = typeText.Trim().TrimEnd('?');
        var lt = t.IndexOf('<');
        if (lt >= 0) t = t[..lt];
        return t[(t.LastIndexOf('.') + 1)..];
    }

    // Like the GetSourceFiles scan: links are not followed out of the tree, and an unreadable
    // folder is skipped instead of ending the search with no results at all.
    private static readonly EnumerationOptions SearchOptions = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    private static List<string> FindTestFiles(string sourceFilePath, List<string> typeNames, Func<string, bool> isAllowed)
    {
        var names = typeNames.SelectMany(n => new[] { $"{n}Tests.cs", $"{n}Test.cs", $"{n}IntegrationTests.cs" }).ToList();
        try
        {
            var repoRoot = RepoRoot(Path.GetDirectoryName(Path.GetFullPath(sourceFilePath))!, isAllowed);
            return Directory.EnumerateFiles(repoRoot, "*.cs", SearchOptions)
                .Where(f => names.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                .Where(f => !IsBuildOutput(f))
                .Take(MaxTestFiles)
                .Select(Path.GetFullPath)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // The nearest folder holding a solution or .git, but never above the allowed root: the
    // search would list files there the client may not read. With no marker below it, the
    // allowed root itself; with no allowed root and no marker, the source file's folder.
    private static string RepoRoot(string start, Func<string, bool> isAllowed)
    {
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || dir.EnumerateFiles("*.sln").Any() || dir.EnumerateFiles("*.slnx").Any())
                return dir.FullName;
            if (dir.Parent != null && !isAllowed(dir.Parent.FullName))
                return dir.FullName;
        }
        return start;
    }

    private static bool IsBuildOutput(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/bin/", StringComparison.OrdinalIgnoreCase) || p.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How types are declared in the source file's project, found by text search so only
    /// the target file needs a syntax tree. Reads each file once, lazily.
    /// </summary>
    private sealed class ProjectDeclarations(string sourceFilePath)
    {
        private List<string>? _texts;

        private static readonly string Modifiers = @"(?<mods>(?:(?:public|internal|private|protected|file|sealed|abstract|static|partial|readonly|ref|unsafe|new)\s+)*)";

        private IEnumerable<string> Texts => _texts ??= LoadTexts();

        private List<string> LoadTexts()
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(sourceFilePath))!;
            for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
            {
                if (!d.EnumerateFiles("*.csproj").Any()) continue;
                try
                {
                    return Directory.EnumerateFiles(d.FullName, "*.cs", SearchOption.AllDirectories)
                        .Where(f => !IsBuildOutput(f))
                        .Select(f => { try { return File.ReadAllText(f); } catch (IOException) { return ""; } })
                        .ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
            }
            return [File.ReadAllText(sourceFilePath)];
        }

        private Match? Find(string simpleName)
        {
            var rx = new Regex($@"{Modifiers}\b(?<kind>interface|class|record|struct|enum)\s+{Regex.Escape(simpleName)}\b(?<rest>[^{{;]*)");
            foreach (var text in Texts)
                if (rx.Match(text) is { Success: true } m) return m;
            return null;
        }

        /// <summary>"interface", "abstract", "sealed", "class", "record", "struct", "enum", or null when not declared here.</summary>
        public string? KindOf(string simpleName)
        {
            if (Find(simpleName) is not { } m) return null;
            var kind = m.Groups["kind"].Value;
            var mods = m.Groups["mods"].Value;
            if (kind == "class" && mods.Contains("abstract", StringComparison.Ordinal)) return "abstract";
            if (kind == "class" && (mods.Contains("sealed", StringComparison.Ordinal) || mods.Contains("static", StringComparison.Ordinal))) return "sealed";
            return kind;
        }

        public string? BaseTypesOf(string simpleName) =>
            Find(simpleName) is { } m && m.Groups["rest"].Value.IndexOf(':') is var colon and >= 0
                ? m.Groups["rest"].Value[(colon + 1)..]
                : null;
    }
}
