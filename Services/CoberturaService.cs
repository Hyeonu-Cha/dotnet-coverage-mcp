using System.Collections.Concurrent;
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

    // Parsed reports keyed by full path. An agent's loop queries the same report many times
    // (one GetFileCoverage per file, then GetUncoveredBranches per method); parsing a large
    // report once per run instead of once per call is the difference that matters.
    private readonly ConcurrentDictionary<string, (DateTime WriteTime, long Length, CoverageModel Model)> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCachedReports = 8;

    internal CoverageModel Load(string coberturaXmlPath)
    {
        var info = new FileInfo(coberturaXmlPath);
        if (_cache.TryGetValue(info.FullName, out var hit)
            && hit.WriteTime == info.LastWriteTimeUtc && hit.Length == info.Length)
            return hit.Model;

        var model = CoverageModel.Parse(XDocument.Load(coberturaXmlPath));
        // ponytail: clear-all eviction; a run's working set is two reports (current + baseline).
        if (_cache.Count >= MaxCachedReports) _cache.Clear();
        _cache[info.FullName] = (info.LastWriteTimeUtc, info.Length, model);
        return model;
    }

    public FileCoverageResult GetFileCoverage(string coberturaXmlPath, string sourceFileName, double targetRate)
    {
        var model = Load(coberturaXmlPath);
        var sourceRoots = model.SourceRoots;
        var requested = NormalizePath(sourceFileName);

        var candidates = model.Classes
            .Where(c => PathMatches(NormalizePath(c.File), requested, sourceRoots))
            .ToList();

        if (candidates.Count == 0)
            throw new KeyNotFoundException($"No classes found for source file '{sourceFileName}' in coverage report.");

        // A bare name like "OrderController.cs" can match the same name in several folders.
        // Merging them would report one file's coverage as another's, so ask the caller to
        // choose unless exactly one candidate is the path they gave.
        var files = candidates.Select(c => NormalizePath(c.File)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count > 1)
        {
            var exact = files.Where(f => IsExactMatch(f, requested, sourceRoots)).ToList();
            if (exact.Count != 1)
                throw new AmbiguousFileException(
                    $"'{sourceFileName}' matches {files.Count} files in the coverage report. Pass a longer path to pick one.",
                    files);
            candidates = candidates.Where(c => string.Equals(NormalizePath(c.File), exact[0], StringComparison.OrdinalIgnoreCase)).ToList();
            files = exact;
        }

        var classes = candidates.Select(c => new FileCoverageClass(
                c.Name,
                Math.Round(c.LineRate, 4),
                Math.Round(c.BranchRate, 4),
                c.LineRate >= targetRate && c.BranchRate >= targetRate,
                c.Methods
                    .Select(m => new FileCoverageMethod(m.Name, Math.Round(m.LineRate, 4), Math.Round(m.BranchRate, 4)))
                    .OrderBy(m => m.BranchRate)
                    .ToList()))
            .ToList();

        // Report the path that matched, so the caller sees which file the numbers are for.
        return new FileCoverageResult(files[0], classes.All(c => c.MeetsTarget), classes);
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
        // Method names here are the folded ones, so an async method is found by its own name
        // rather than as a MoveNext inside a <Name>d__N class.
        var matched = Load(coberturaXmlPath).Classes
            .SelectMany(c => c.Methods.Select(m => (Class: c, Method: m)))
            .Where(x => x.Method.Name.Contains(methodName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matched.Count == 0)
            throw new KeyNotFoundException($"No method matching '{methodName}' found in coverage report.");

        var results = matched
            .Select(x => new UncoveredMethod(
                x.Method.Name,
                x.Class.Name,
                x.Method.Lines
                    .Where(l => l.Missing.Count > 0)
                    .Select(l => new UncoveredBranch(l.Number, l.Missing.ToList()))
                    .ToList()))
            // Drop methods whose partial name matched but have no uncovered branches — pure
            // noise — and cap the rest so a broad match can't blow up the response.
            .Where(m => m.UncoveredBranches.Count > 0)
            .Take(MaxUncoveredMethods)
            .ToList();

        return new UncoveredBranchesResult(matched.Count, results);
    }

    public DiffResult ComputeDiff(string currentXmlPath, string baselinePath)
    {
        var current = Load(currentXmlPath);
        var previous = Load(baselinePath);

        static Dictionary<string, MethodCov> ByKey(CoverageModel model)
        {
            var map = new Dictionary<string, MethodCov>();
            foreach (var m in model.Classes.SelectMany(c => c.Methods))
                map.TryAdd(m.DiffKey, m);
            return map;
        }

        var prevMethods = ByKey(previous);
        var curMethods = ByKey(current);

        var changedMethods = new List<MethodDiff>();
        var unchangedCount = 0;

        foreach (var (key, cur) in curMethods)
        {
            if (prevMethods.TryGetValue(key, out var prev))
            {
                if (Math.Abs(cur.LineRate - prev.LineRate) > 0.001 || Math.Abs(cur.BranchRate - prev.BranchRate) > 0.001)
                    changedMethods.Add(new MethodDiff(cur.Name, Math.Round(prev.LineRate, 4), Math.Round(cur.LineRate, 4),
                        Math.Round(prev.BranchRate, 4), Math.Round(cur.BranchRate, 4)));
                else
                    unchangedCount++;
            }
            else
            {
                changedMethods.Add(new MethodDiff(cur.Name, 0.0, Math.Round(cur.LineRate, 4), 0.0, Math.Round(cur.BranchRate, 4)));
            }
        }

        var removedMethods = prevMethods
            .Where(kv => !curMethods.ContainsKey(kv.Key))
            .Select(kv => new MethodDiff(kv.Value.Name, Math.Round(kv.Value.LineRate, 4), 0.0, Math.Round(kv.Value.BranchRate, 4), 0.0))
            .ToList();

        return new DiffResult(
            FirstRun: false,
            CycleImprovement: new DiffDelta(Math.Round(current.LineRate - previous.LineRate, 4),
                Math.Round(current.BranchRate - previous.BranchRate, 4)),
            ChangedMethods: changedMethods,
            RemovedMethods: removedMethods,
            UnchangedCount: unchangedCount);
    }
}
