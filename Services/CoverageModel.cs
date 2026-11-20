using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotNetCoverageMcp.Services;

internal sealed record LineCov(int Number, int Hits, int BranchesCovered, int BranchesTotal, IReadOnlyList<string> Missing);

internal sealed record MethodCov(string Name, string DiffKey, int FirstLine, double LineRate, double BranchRate, IReadOnlyList<LineCov> Lines);

internal sealed record ClassCov(string Name, string File, double LineRate, double BranchRate, IReadOnlyList<MethodCov> Methods);

/// <summary>
/// A Cobertura report with compiler-generated code folded back into the methods it came
/// from. The compiler moves async and iterator bodies into state machines
/// (<c>Calc/&lt;AddAsync&gt;d__2</c> → <c>MoveNext</c>), lambdas into <c>&lt;&gt;c</c> and
/// <c>&lt;&gt;c__DisplayClassN_M</c> classes, and local functions into <c>&lt;Foo&gt;g__Bar|N_M</c>
/// methods. Coverlet reports each of those separately, so without folding an async method
/// cannot be found by name and its coverage is missing from its class's numbers.
/// </summary>
internal sealed record CoverageModel(IReadOnlyList<string> SourceRoots, double LineRate, double BranchRate, IReadOnlyList<ClassCov> Classes)
{
    // The user-visible name inside the leading angle brackets: "<AddAsync>d__2",
    // "<<InsertCodeAsync>b__0>d", "<Twice>g__Double|7_0" all give the method they belong to.
    private static readonly Regex ParentNameRx = new(@"^<+([^<>]+)>", RegexOptions.Compiled);
    private static readonly Regex StateMachineRx = new(@"^<[^<>]+>d__\d+$", RegexOptions.Compiled);
    // The member ordinal the compiler stamps into every generated name; it separates
    // overloads, which share a name.
    private static readonly Regex SegmentOrdinalRx = new(@"(?:d__|DisplayClass)(\d+)", RegexOptions.Compiled);
    private static readonly Regex MethodOrdinalRx = new(@"(?:__|\|)(\d+)_\d+$", RegexOptions.Compiled);
    private static readonly Regex ConditionCoverageRx = new(@"\((\d+)/(\d+)\)", RegexOptions.Compiled);
    private static readonly Regex GenericArityRx = new(@"`(\d+)", RegexOptions.Compiled);

    private sealed record Fragment(
        string Class, string File, string Name, bool IsPlain, bool IsStateMachine, string Signature,
        int? Ordinal, double? LineRate, double? BranchRate, IReadOnlyList<LineCov> Lines)
    {
        public bool IsPrimary => IsPlain || IsStateMachine;
        public int MinLine => Lines.Count == 0 ? int.MaxValue : Lines.Min(l => l.Number);
        public int MaxLine => Lines.Count == 0 ? int.MinValue : Lines.Max(l => l.Number);
    }

    public static CoverageModel Parse(XDocument doc)
    {
        // Any well-formed XML used to "parse" as an empty report, so get_coverage_diff took
        // whatever it was given as its baseline. Cobertura's root is <coverage> (coverlet and
        // Microsoft's CodeCoverage alike).
        if (doc.Root?.Name.LocalName != "coverage")
            throw new InvalidDataException(
                $"Not a Cobertura coverage report: the root element is <{doc.Root?.Name.LocalName}>, not <coverage>.");

        // Coverlet writes the files' common folder, which can be the drive or file-system root:
        // "C:/" on Windows, "/" elsewhere. "/" stays a root, as "" ("{root}/{file}" is then
        // "/{file}"); it used to be dropped, and the file names relative to it were passed on as
        // "tmp/…", no longer a path.
        var roots = doc.Descendants("source").Select(s => s.Value.Trim().Replace('\\', '/'))
            .Where(s => s.Length > 0)
            .Select(s => s.TrimEnd('/'))
            .ToList();

        var fragments = new List<Fragment>();
        // Rates coverlet computed for a real class, used as-is when nothing is folded into it.
        var classRates = new Dictionary<(string, string), (double?, double?)>();

        foreach (var cls in doc.Descendants("class"))
        {
            var rawName = (string?)cls.Attribute("name") ?? "";
            var file = ((string?)cls.Attribute("filename") ?? "").Replace('\\', '/');
            var (realName, generated) = SplitClassName(rawName);
            var realClass = DisplayName(realName);

            if (generated.Length == 0)
                classRates[(realClass, file)] = (Rate(cls, "line-rate"), Rate(cls, "branch-rate"));
            else
                classRates.TryAdd((realClass, file), (null, null));

            foreach (var m in cls.Descendants("method"))
            {
                var methodName = (string?)m.Attribute("name") ?? "";
                var isPlain = generated.Length == 0 && !methodName.StartsWith('<');
                var isStateMachine = generated.Length == 1 && methodName == "MoveNext" && StateMachineRx.IsMatch(generated[0]);
                var parent = isPlain ? methodName : ParentName(generated, methodName) ?? methodName;

                fragments.Add(new Fragment(realClass, file, parent, isPlain, isStateMachine,
                    (string?)m.Attribute("signature") ?? "", Ordinal(generated, methodName),
                    Rate(m, "line-rate"), Rate(m, "branch-rate"), ParseLines(m)));
            }
        }

        var byClass = fragments.ToLookup(f => (f.Class, f.File));
        var classes = classRates
            .Select(kv => BuildClass(kv.Key.Item1, kv.Key.Item2, kv.Value, byClass[kv.Key].ToList()))
            .ToList();

        return new CoverageModel(roots, Rate(doc.Root, "line-rate") ?? 0, Rate(doc.Root, "branch-rate") ?? 0, classes);
    }

    private static ClassCov BuildClass(string name, string file, (double? Line, double? Branch) rates, List<Fragment> fragments)
    {
        var primaries = fragments.Where(f => f.IsPrimary).ToList();
        // An async method's own stub carries no lines; its body is in the state machine.
        primaries.RemoveAll(p => p.IsPlain && p.Lines.Count == 0
                                 && primaries.Any(q => q.IsStateMachine && q.Name == p.Name));

        var groups = primaries.Select(p => new List<Fragment> { p }).ToList();
        foreach (var fragment in fragments.Where(f => !f.IsPrimary))
        {
            var owner = PickOwner(groups, fragment);
            if (owner != null) owner.Add(fragment);
            else groups.Add([fragment]);
        }

        var methods = groups.Select(BuildMethod).ToList();

        // Overloads share a name; label them by where they start so they can be told apart.
        var duplicated = methods.GroupBy(m => m.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        methods = methods
            .Select(m => duplicated.Contains(m.Name) && m.FirstLine > 0
                ? m with { Name = $"{m.Name} (line {m.FirstLine})" }
                : m)
            .ToList();

        var anyFolded = fragments.Any(f => !f.IsPlain);
        var (lineRate, branchRate) = !anyFolded && rates.Line != null
            ? (rates.Line.Value, rates.Branch ?? 1)
            : RatesOf(MergeLines(methods.SelectMany(m => m.Lines)), rates.Line ?? 0, rates.Branch ?? 1);

        return new ClassCov(name, file, lineRate, branchRate, methods);
    }

    private static List<Fragment>? PickOwner(List<List<Fragment>> groups, Fragment fragment)
    {
        var sameName = groups.Where(g => g[0].Name == fragment.Name).ToList();
        if (sameName.Count <= 1) return sameName.FirstOrDefault();

        if (fragment.Ordinal is int ordinal
            && sameName.FirstOrDefault(g => g[0].IsStateMachine && g[0].Ordinal == ordinal) is { } byOrdinal)
            return byOrdinal;

        var line = fragment.MinLine;
        return sameName.FirstOrDefault(g => g[0].MinLine <= line && line <= g[0].MaxLine)
               ?? sameName.Where(g => g[0].MinLine <= line).MaxBy(g => g[0].MinLine)
               ?? sameName[0];
    }

    private static MethodCov BuildMethod(List<Fragment> group)
    {
        var primary = group[0];
        var lines = MergeLines(group.SelectMany(f => f.Lines));
        // A method with nothing folded in keeps coverlet's own numbers exactly.
        var (lineRate, branchRate) = group.Count == 1 && primary.LineRate != null
            ? (primary.LineRate.Value, primary.BranchRate ?? 1)
            : RatesOf(lines, primary.LineRate ?? 0, primary.BranchRate ?? 1);

        // The state machine's ordinal shifts when methods are added above it, so it must not
        // be part of the key a coverage diff matches methods by.
        var diffKey = $"{primary.Class}.{primary.Name}{(primary.IsPlain ? primary.Signature : "")}";
        return new MethodCov(primary.Name, diffKey, lines.Count == 0 ? 0 : lines[0].Number, lineRate, branchRate, lines);
    }

    // One entry per line number. A lambda's line also appears in the method that creates it;
    // the two carry different branches, so branch counts add up while hits take the maximum.
    internal static List<LineCov> MergeLines(IEnumerable<LineCov> lines) =>
        lines.GroupBy(l => l.Number)
            .Select(g => new LineCov(g.Key, g.Max(l => l.Hits), g.Sum(l => l.BranchesCovered),
                g.Sum(l => l.BranchesTotal), g.SelectMany(l => l.Missing).ToList()))
            .OrderBy(l => l.Number)
            .ToList();

    private static (double Line, double Branch) RatesOf(List<LineCov> lines, double fallbackLine, double fallbackBranch)
    {
        if (lines.Count == 0) return (fallbackLine, fallbackBranch);
        var total = lines.Sum(l => l.BranchesTotal);
        return ((double)lines.Count(l => l.Hits > 0) / lines.Count,
            total == 0 ? 1 : (double)lines.Sum(l => l.BranchesCovered) / total);
    }

    private static IReadOnlyList<LineCov> ParseLines(XElement method) =>
        method.Descendants("line").Select(l =>
        {
            var conditions = l.Descendants("condition").ToList();
            // Anything short of 100% has an outcome no test reached. Coverlet writes an `if`
            // that only ever went one way as a single jump condition at 50%, not 0%, so
            // listing only 0% conditions (as an earlier version did) missed the most common gap.
            var missing = conditions
                .Where(c => (string?)c.Attribute("coverage") is { } cov && cov != "100%")
                .Select(c => $"condition {(string?)c.Attribute("number")} ({(string?)c.Attribute("type")}, {(string?)c.Attribute("coverage")} covered)")
                .ToList();

            int covered, total;
            var cc = ConditionCoverageRx.Match((string?)l.Attribute("condition-coverage") ?? "");
            if (cc.Success)
            {
                covered = int.Parse(cc.Groups[1].Value, CultureInfo.InvariantCulture);
                total = int.Parse(cc.Groups[2].Value, CultureInfo.InvariantCulture);
                // Some writers give only the line's totals, without <condition> elements.
                if (missing.Count == 0 && covered < total)
                    missing.Add($"{total - covered} of {total} branch outcomes not covered");
            }
            else
            {
                total = conditions.Count;
                covered = conditions.Count(c => (string?)c.Attribute("coverage") != "0%");
            }

            return new LineCov(
                int.TryParse((string?)l.Attribute("number"), out var n) ? n : 0,
                int.TryParse((string?)l.Attribute("hits"), out var h) ? h : 0,
                covered, total, missing);
        }).ToList();

    /// <summary>
    /// Splits a report class name into the source class and the compiler-generated types
    /// nested in it. Coverlet writes "Ns.Calc/&lt;AddAsync&gt;d__2", Microsoft's collector
    /// "Ns.Calc.&lt;AddAsync&gt;d__2". A cut is made only at a '.' or '/' outside angle
    /// brackets that directly precedes '&lt;', because generated names can contain dots
    /// ("&lt;&lt;.ctor&gt;b__0_0&gt;d") and real nested classes ("Outer/Inner") must stay whole.
    /// </summary>
    internal static (string Real, string[] Generated) SplitClassName(string name)
    {
        var cuts = new List<int>();
        var depth = 0;
        for (var i = 0; i < name.Length; i++)
        {
            switch (name[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case '.' or '/' when depth == 0 && i + 1 < name.Length && name[i + 1] == '<':
                    cuts.Add(i);
                    break;
            }
        }
        if (cuts.Count == 0) return (name, []);

        var generated = new string[cuts.Count];
        for (var k = 0; k < cuts.Count; k++)
        {
            var start = cuts[k] + 1;
            var end = k + 1 < cuts.Count ? cuts[k + 1] : name.Length;
            generated[k] = name[start..end];
        }
        return (name[..cuts[0]], generated);
    }

    private static string? ParentName(string[] generatedSegments, string methodName)
    {
        foreach (var segment in generatedSegments)
            if (ParentNameRx.Match(segment) is { Success: true } m) return m.Groups[1].Value;
        return ParentNameRx.Match(methodName) is { Success: true } mm ? mm.Groups[1].Value : null;
    }

    private static int? Ordinal(string[] generatedSegments, string methodName)
    {
        foreach (var segment in generatedSegments)
            if (SegmentOrdinalRx.Match(segment) is { Success: true } m) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return MethodOrdinalRx.Match(methodName) is { Success: true } mm ? int.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    // "Repo`1" → "Repo<T>", "Map`2" → "Map<T1,T2>": the names as they appear in source.
    private static string DisplayName(string className) =>
        GenericArityRx.Replace(className, m =>
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return n == 1 ? "<T>" : $"<{string.Join(",", Enumerable.Range(1, n).Select(i => $"T{i}"))}>";
        });

    private static double? Rate(XElement? element, string attribute) =>
        double.TryParse((string?)element?.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
