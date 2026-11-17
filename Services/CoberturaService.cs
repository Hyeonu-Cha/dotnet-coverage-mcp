using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using DotNetCoverageMcp.Helpers;

namespace DotNetCoverageMcp.Services;

public interface ICoberturaService
{
    List<SummaryClass> ParseSummary(string summaryJsonPath);
    FileCoverageResult GetFileCoverage(string coberturaXmlPath, string sourceFileName, double targetRate);
    UncoveredBranchesResult GetUncoveredBranches(string coberturaXmlPath, string methodName);
    DiffResult ComputeDiff(string currentXmlPath, string baselinePath);
}

// --- Result types ---

public record SummaryClass(string Class, double LineCoverage, double BranchCoverage, List<SummaryMethod> Methods);
public record SummaryMethod(string Name, double Line, double Branch);

public record FileCoverageResult(string SourceFile, bool AllMeetTarget, List<FileCoverageClass> Classes);
public record FileCoverageClass(string Class, double LineRate, double BranchRate, bool MeetsTarget, List<FileCoverageMethod> Methods);
public record FileCoverageMethod(string Name, double LineRate, double BranchRate);

public record UncoveredBranchesResult(int MatchCount, List<UncoveredMethod> Methods);
public record UncoveredMethod(string Method, string Class, List<UncoveredBranch> UncoveredBranches);
public record UncoveredBranch(int Line, List<string> Missing);

/// <summary>A file name matched several files in the report; nothing was merged.</summary>
public sealed class AmbiguousFileException(string message, IReadOnlyList<string> candidates) : Exception(message)
{
    public IReadOnlyList<string> Candidates { get; } = candidates;
}

public record DiffResult(
    bool FirstRun,
    DiffDelta? CycleImprovement,
    List<MethodDiff>? ChangedMethods,
    List<MethodDiff>? RemovedMethods,
    int UnchangedCount);

public record DiffDelta(double LineDelta, double BranchDelta);

public record MethodDiff(string Name, double LineBefore, double LineAfter, double BranchBefore, double BranchAfter);

// --- Implementation ---

public class CoberturaService : ICoberturaService
{
    private readonly ILogger<CoberturaService> _logger;
    // Cap uncovered-branch results so a broad partial method-name match can't balloon the payload.
    private const int MaxUncoveredMethods = 25;

    public CoberturaService(ILogger<CoberturaService> logger)
    {
        _logger = logger;
    }

    public List<SummaryClass> ParseSummary(string summaryJsonPath)
    {
        var json = File.ReadAllText(summaryJsonPath);
        var root = JsonNode.Parse(json);
        var assemblies = root?["coverage"]?["assemblies"]?.AsArray()
            ?? throw new InvalidOperationException("Unexpected Summary.json structure — could not find coverage.assemblies.");

        var result = new List<SummaryClass>();

        foreach (var assembly in assemblies)
        {
            var classes = assembly?["classes"]?.AsArray();
            if (classes == null) continue;

            foreach (var cls in classes)
            {
                var methods = cls?["methods"]?.AsArray();
                var methodList = new List<SummaryMethod>();

                if (methods != null)
                {
                    foreach (var method in methods)
                    {
                        var linePct = method?["linecoverage"]?.GetValue<double>() ?? 0;
                        var branchPct = method?["branchcoverage"]?.GetValue<double>() ?? 0;
                        methodList.Add(new SummaryMethod(
                            method?["name"]?.GetValue<string>() ?? "",
                            Math.Round(linePct / 100.0, 4),
                            Math.Round(branchPct / 100.0, 4)));
                    }
                    methodList = methodList.OrderBy(m => m.Branch).ToList();
                }

                var classLinePct = cls?["linecoverage"]?.GetValue<double>() ?? 0;
                var classBranchPct = cls?["branchcoverage"]?.GetValue<double>() ?? 0;

                result.Add(new SummaryClass(
                    cls?["name"]?.GetValue<string>() ?? "",
                    Math.Round(classLinePct / 100.0, 4),
                    Math.Round(classBranchPct / 100.0, 4),
                    methodList));
            }
        }

        return result;
    }

    public FileCoverageResult GetFileCoverage(string coberturaXmlPath, string sourceFileName, double targetRate)
    {
        var doc = XDocument.Load(coberturaXmlPath);
        var sourceRoots = doc.Descendants("source").Select(s => NormalizePath(s.Value)).Where(s => s.Length > 0).ToList();
        var requested = NormalizePath(sourceFileName);

        var candidates = doc.Descendants("class")
            .Select(c => (Element: c, File: NormalizePath(c.Attribute("filename")?.Value ?? "")))
            .Where(c => PathMatches(c.File, requested, sourceRoots))
            .ToList();

        if (candidates.Count == 0)
            throw new KeyNotFoundException($"No classes found for source file '{sourceFileName}' in coverage report.");

        // A bare name like "OrderController.cs" can match the same name in several folders.
        // Merging them would report one file's coverage as another's, so ask the caller to
        // choose unless exactly one candidate is the path they gave.
        var files = candidates.Select(c => c.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count > 1)
        {
            var exact = files.Where(f => IsExactMatch(f, requested, sourceRoots)).ToList();
            if (exact.Count != 1)
                throw new AmbiguousFileException(
                    $"'{sourceFileName}' matches {files.Count} files in the coverage report. Pass a longer path to pick one.",
                    files);
            candidates = candidates.Where(c => string.Equals(c.File, exact[0], StringComparison.OrdinalIgnoreCase)).ToList();
            files = exact;
        }

        var matchedClasses = candidates.Select(c => c.Element).ToList();
        // Report the path that matched, so the caller sees which file the numbers are for.
        var matchedFile = files[0];

        var classes = new List<FileCoverageClass>();
        var allMeetTarget = true;

        foreach (var cls in matchedClasses)
        {
            var className = cls.Attribute("name")?.Value ?? "";
            var lineRate = double.TryParse(cls.Attribute("line-rate")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lr) ? lr : 0;
            var branchRate = double.TryParse(cls.Attribute("branch-rate")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var br) ? br : 0;
            var meetsTarget = lineRate >= targetRate && branchRate >= targetRate;
            if (!meetsTarget) allMeetTarget = false;

            var methods = cls.Descendants("method")
                .Select(m => new FileCoverageMethod(
                    Name: m.Attribute("name")?.Value ?? "",
                    LineRate: double.TryParse(m.Attribute("line-rate")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mlr) ? Math.Round(mlr, 4) : 0,
                    BranchRate: double.TryParse(m.Attribute("branch-rate")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mbr) ? Math.Round(mbr, 4) : 0))
                .OrderBy(m => m.BranchRate)
                .ToList();

            classes.Add(new FileCoverageClass(className, Math.Round(lineRate, 4), Math.Round(branchRate, 4), meetsTarget, methods));
        }

        return new FileCoverageResult(matchedFile, allMeetTarget, classes);
    }

    internal static string NormalizePath(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');

    // Checked by hand rather than with Path.IsPathRooted: reports written on Windows are
    // read on Linux in CI and the other way round, so both shapes must be recognised anywhere.
    private static bool IsAbsolute(string path) =>
        path.StartsWith('/') || (path.Length > 2 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '/');

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // True when `path` ends with all of `tail`'s segments, so "Service.cs" does not match
    // "Services/CoberturaService.cs" the way a plain EndsWith did.
    private static bool EndsWithSegments(string path, string tail) =>
        path.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase);

    private static bool IsExactMatch(string file, string requested, IReadOnlyList<string> sourceRoots) =>
        SamePath(file, requested)
        || (!IsAbsolute(file) && sourceRoots.Any(root => SamePath($"{root}/{file}", requested)));

    /// <summary>
    /// Whether a report path (relative to a &lt;source&gt; root, or absolute) is the file the
    /// caller asked for. The caller may pass an absolute path (what GetSourceFiles returns),
    /// a repo-relative path, or a bare file name; matching is on whole path segments.
    /// </summary>
    internal static bool PathMatches(string file, string requested, IReadOnlyList<string> sourceRoots)
    {
        if (file.Length == 0 || requested.Length == 0) return false;

        // With the report's roots known, an absolute request can be compared exactly.
        if (IsAbsolute(requested) && !IsAbsolute(file) && sourceRoots.Count > 0)
            return IsExactMatch(file, requested, sourceRoots);

        return SamePath(file, requested) || EndsWithSegments(file, requested) || EndsWithSegments(requested, file);
    }

    public UncoveredBranchesResult GetUncoveredBranches(string coberturaXmlPath, string methodName)
    {
        var doc = XDocument.Load(coberturaXmlPath);

        var matchedMethods = doc.Descendants("method")
            .Where(m => m.Attribute("name")?.Value?.Contains(methodName, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (matchedMethods.Count == 0)
            throw new KeyNotFoundException($"No method matching '{methodName}' found in coverage report.");

        var results = matchedMethods.Select(method =>
        {
            var methodFullName = method.Attribute("name")?.Value ?? methodName;
            var className = method.Parent?.Parent?.Attribute("name")?.Value ?? "";

            var uncoveredBranches = method.Descendants("line")
                .Where(l => l.Attribute("branch")?.Value == "True")
                .Select(l => new UncoveredBranch(
                    Line: int.TryParse(l.Attribute("number")?.Value, out var n) ? n : 0,
                    Missing: l.Descendants("condition")
                        .Where(c => c.Attribute("coverage")?.Value == "0%")
                        .Select(c => $"condition {c.Attribute("number")?.Value} ({c.Attribute("type")?.Value})")
                        .ToList()))
                .Where(b => b.Missing.Count > 0)
                .ToList();

            return new UncoveredMethod(methodFullName, className, uncoveredBranches);
        })
        // Drop methods whose partial name matched but have no uncovered branches — pure
        // noise — and cap the rest so a broad match can't blow up the response.
        .Where(m => m.UncoveredBranches.Count > 0)
        .Take(MaxUncoveredMethods)
        .ToList();

        return new UncoveredBranchesResult(matchedMethods.Count, results);
    }

    public DiffResult ComputeDiff(string currentXmlPath, string baselinePath)
    {
        var currentDoc = XDocument.Load(currentXmlPath);
        var prevDoc = XDocument.Load(baselinePath);

        double ParseRate(XDocument d, string attr) =>
            double.TryParse(d.Root?.Attribute(attr)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

        var prevLineRate = ParseRate(prevDoc, "line-rate");
        var prevBranchRate = ParseRate(prevDoc, "branch-rate");
        var curLineRate = ParseRate(currentDoc, "line-rate");
        var curBranchRate = ParseRate(currentDoc, "branch-rate");

        string MethodKey(XElement m) =>
            $"{m.Parent?.Parent?.Attribute("name")?.Value}.{m.Attribute("name")?.Value}({m.Attribute("signature")?.Value})";

        var prevMethods = new Dictionary<string, (double LineRate, double BranchRate)>();
        foreach (var m in prevDoc.Descendants("method"))
        {
            var key = MethodKey(m);
            prevMethods.TryAdd(key, (
                double.TryParse(m.Attribute("line-rate")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lr) ? lr : 0,
                double.TryParse(m.Attribute("branch-rate")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var br) ? br : 0));
        }

        var changedMethods = new List<MethodDiff>();
        var unchangedCount = 0;
        var seenKeys = new HashSet<string>();

        foreach (var method in currentDoc.Descendants("method"))
        {
            var key = MethodKey(method);
            if (!seenKeys.Add(key)) continue;

            var name = method.Attribute("name")?.Value ?? key;
            var curLine = double.TryParse(method.Attribute("line-rate")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cl) ? cl : 0;
            var curBranch = double.TryParse(method.Attribute("branch-rate")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cb) ? cb : 0;

            if (prevMethods.TryGetValue(key, out var prev))
            {
                if (Math.Abs(curLine - prev.LineRate) > 0.001 || Math.Abs(curBranch - prev.BranchRate) > 0.001)
                    changedMethods.Add(new MethodDiff(name, Math.Round(prev.LineRate, 4), Math.Round(curLine, 4), Math.Round(prev.BranchRate, 4), Math.Round(curBranch, 4)));
                else
                    unchangedCount++;
            }
            else
            {
                changedMethods.Add(new MethodDiff(name, 0.0, Math.Round(curLine, 4), 0.0, Math.Round(curBranch, 4)));
            }
        }

        var removedMethods = prevMethods.Keys
            .Where(k => !seenKeys.Contains(k))
            .Select(k => new MethodDiff(k, Math.Round(prevMethods[k].LineRate, 4), 0.0, Math.Round(prevMethods[k].BranchRate, 4), 0.0))
            .ToList();

        return new DiffResult(
            FirstRun: false,
            CycleImprovement: new DiffDelta(Math.Round(curLineRate - prevLineRate, 4), Math.Round(curBranchRate - prevBranchRate, 4)),
            ChangedMethods: changedMethods,
            RemovedMethods: removedMethods,
            UnchangedCount: unchangedCount);
    }

}
