using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using DotNetCoverageMcp.Helpers;

namespace DotNetCoverageMcp.Services;

public interface ICoberturaService
{
    List<object> ParseSummary(string summaryJsonPath);
    FileCoverageResult GetFileCoverage(string coberturaXmlPath, string sourceFileName, double targetRate);
    UncoveredBranchesResult GetUncoveredBranches(string coberturaXmlPath, string methodName);
    DiffResult ComputeDiff(string currentXmlPath, string baselinePath);
}

// --- Result types ---

public record FileCoverageResult(string SourceFile, bool AllMeetTarget, List<FileCoverageClass> Classes);
public record FileCoverageClass(string Class, double LineRate, double BranchRate, bool MeetsTarget, List<FileCoverageMethod> Methods);
public record FileCoverageMethod(string Name, double LineRate, double BranchRate);

public record UncoveredBranchesResult(int MatchCount, List<UncoveredMethod> Methods);
public record UncoveredMethod(string Method, string Class, List<UncoveredBranch> UncoveredBranches);
public record UncoveredBranch(int Line, List<string> Missing);

public record DiffResult(
    bool FirstRun,
    DiffDelta? CycleImprovement,
    List<MethodDiff>? ChangedMethods,
    List<MethodDiff>? RemovedMethods,
    List<string>? Unchanged);

public record DiffDelta(double LineDelta, double BranchDelta);

public record MethodDiff(string Name, double LineBefore, double LineAfter, double BranchBefore, double BranchAfter);

// --- Implementation ---

public class CoberturaService : ICoberturaService
{
    private readonly ILogger<CoberturaService> _logger;

    public CoberturaService(ILogger<CoberturaService> logger)
    {
        _logger = logger;
    }

    public List<object> ParseSummary(string summaryJsonPath)
    {
        var json = File.ReadAllText(summaryJsonPath);
        var root = JsonNode.Parse(json);
        var assemblies = root?["coverage"]?["assemblies"]?.AsArray()
            ?? throw new InvalidOperationException("Unexpected Summary.json structure — could not find coverage.assemblies.");

        var result = new List<object>();

        foreach (var assembly in assemblies)
        {
            var classes = assembly?["classes"]?.AsArray();
            if (classes == null) continue;

            foreach (var cls in classes)
            {
                var methods = cls?["methods"]?.AsArray();
                var methodList = new List<(string name, double line, double branch)>();

                if (methods != null)
                {
                    foreach (var method in methods)
                    {
                        var linePct = method?["linecoverage"]?.GetValue<double>() ?? 0;
                        var branchPct = method?["branchcoverage"]?.GetValue<double>() ?? 0;
                        methodList.Add((
                            name: method?["name"]?.GetValue<string>() ?? "",
                            line: Math.Round(linePct / 100.0, 4),
                            branch: Math.Round(branchPct / 100.0, 4)));
                    }
                    methodList = methodList.OrderBy(m => m.branch).ToList();
                }

                var classLinePct = cls?["linecoverage"]?.GetValue<double>() ?? 0;
                var classBranchPct = cls?["branchcoverage"]?.GetValue<double>() ?? 0;

                result.Add(new
                {
                    @class = cls?["name"]?.GetValue<string>() ?? "",
                    lineCoverage = Math.Round(classLinePct / 100.0, 4),
                    branchCoverage = Math.Round(classBranchPct / 100.0, 4),
                    methods = methodList.Select(m => new { m.name, m.line, m.branch })
                });
            }
        }

        return result;
    }

    public FileCoverageResult GetFileCoverage(string coberturaXmlPath, string sourceFileName, double targetRate)
    {
        var doc = XDocument.Load(coberturaXmlPath);

        var matchedClasses = doc.Descendants("class")
            .Where(c =>
            {
                var filename = c.Attribute("filename")?.Value ?? "";
                return filename.EndsWith(sourceFileName, StringComparison.OrdinalIgnoreCase)
                    || filename.EndsWith(sourceFileName.Replace("/", "\\"), StringComparison.OrdinalIgnoreCase)
                    || filename.EndsWith(sourceFileName.Replace("\\", "/"), StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (matchedClasses.Count == 0)
            throw new KeyNotFoundException($"No classes found for source file '{sourceFileName}' in coverage report.");

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

        return new FileCoverageResult(sourceFileName, allMeetTarget, classes);
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
        }).ToList();

        return new UncoveredBranchesResult(results.Count, results);
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
        var unchangedMethods = new List<string>();
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
                    unchangedMethods.Add(name);
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
            Unchanged: unchangedMethods);
    }

}
