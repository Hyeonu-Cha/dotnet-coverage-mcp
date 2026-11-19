using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using DotNetCoverageMcp.Helpers;

namespace DotNetCoverageMcp.Services;

public interface ICoberturaService
{
    List<SummaryClass> SummarizeClasses(string coberturaXmlPath);
    FileCoverageResult GetFileCoverage(string coberturaXmlPath, string sourceFileName, double targetRate);
    UncoveredBranchesResult GetUncoveredBranches(string coberturaXmlPath, string methodName);
    DiffResult ComputeDiff(string currentXmlPath, string baselinePath);
    CoverageGapsResult GetCoverageGaps(string coberturaXmlPath, IReadOnlyList<string> sourceFiles,
        double targetRate, int maxFiles, int maxMethodsPerFile, bool includeSource);
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

public record CoverageGapsResult(
    double TargetRate, int FilesInScope, int FilesDone, int FilesRemaining,
    List<FileGap> Remaining, List<FileRates> MoreRemaining, List<string> Done, List<string> NotInReport);
public record FileGap(
    string File, double LineRate, double BranchRate, int LinesToCover, int BranchesToCover, bool ClosesGap,
    List<MethodGap> Methods,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SourceChangedSinceRun = false);
public record MethodGap(
    string Method, string Class, double LineRate, double BranchRate, string? UncoveredLines,
    List<UncoveredBranch> UncoveredBranches, string? Source);
public record FileRates(string File, double LineRate, double BranchRate);

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

    /// <summary>
    /// A class/method coverage summary built from the Cobertura XML, reusing the parsed,
    /// compiler-generated-folded <see cref="CoverageModel"/> that the per-file tools read.
    /// reportgenerator's JsonSummary carries no per-method data and uses a different schema,
    /// so the summary is sourced from the same report everything else reads, not from it.
    /// Methods are ordered worst-branch-first so a <c>methodsPerClass</c> trim keeps the ones
    /// that still need work; a class with no branches reports a branch rate of 1.
    /// </summary>
    public List<SummaryClass> SummarizeClasses(string coberturaXmlPath)
    {
        var model = Load(coberturaXmlPath);
        return model.Classes
            .Select(c => new SummaryClass(
                c.Name,
                Math.Round(c.LineRate, 4),
                Math.Round(c.BranchRate, 4),
                c.Methods
                    .Select(m => new SummaryMethod(m.Name, Math.Round(m.LineRate, 4), Math.Round(m.BranchRate, 4)))
                    .OrderBy(m => m.Branch).ThenBy(m => m.Line)
                    .ToList()))
            .ToList();
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

        var model = CoverageModel.Parse(XmlFile.Load(coberturaXmlPath));
        // ponytail: clear-all eviction; a run's working set is two reports (current + baseline).
        if (_cache.Count >= MaxCachedReports) _cache.Clear();
        _cache[info.FullName] = (info.LastWriteTimeUtc, info.Length, model);
        return model;
    }

    public FileCoverageResult GetFileCoverage(string coberturaXmlPath, string sourceFileName, double targetRate)
    {
        var model = Load(coberturaXmlPath);
        var sourceRoots = model.SourceRoots;
        var candidates = ClassesFor(model, sourceFileName, out var requested);

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

    /// <summary>
    /// The path with the symlinked segments on it followed to their target. Coverlet records
    /// the path the compiler saw, so this is used to line a queried path up with the one in the
    /// report. It intentionally does NOT keep canonicalising a target's own prefix the way
    /// <see cref="SymlinkResolver"/> (the security boundary) does: the report stores the path in
    /// the form the link resolves to, and over-resolving it would stop the two from matching.
    /// </summary>
    internal static string ResolveSymlinks(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? "";
            var current = root;
            foreach (var part in full[root.Length..].Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget != null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = target.FullName;
            }
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }

    // Report classes for a requested path; for an absolute path that matches nothing as
    // given, retries with symlinks resolved (see ResolveSymlinks).
    private static List<ClassCov> ClassesFor(CoverageModel model, string sourcePath, out string requested)
    {
        requested = NormalizePath(sourcePath);
        var req = requested;
        var classes = model.Classes.Where(c => PathMatches(NormalizePath(c.File), req, model.SourceRoots)).ToList();
        if (classes.Count > 0 || !IsAbsolute(requested)) return classes;

        var resolved = NormalizePath(ResolveSymlinks(sourcePath));
        if (resolved == requested) return classes;
        requested = resolved;
        return model.Classes.Where(c => PathMatches(NormalizePath(c.File), resolved, model.SourceRoots)).ToList();
    }

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

    private const int MaxSourceLinesPerMethod = 30;
    private const int SourceContext = 2;
    private const int MaxFileList = 100;

    /// <summary>
    /// For each file, whether it meets the target on lines and branches, and for the files
    /// that don't, the fewest methods whose uncovered code closes that file's gap. The goal is
    /// "every file reaches the target", so each file's gap is closed on its own.
    /// </summary>
    public CoverageGapsResult GetCoverageGaps(string coberturaXmlPath, IReadOnlyList<string> sourceFiles,
        double targetRate, int maxFiles, int maxMethodsPerFile, bool includeSource)
    {
        var model = Load(coberturaXmlPath);
        var done = new List<string>();
        var notInReport = new List<string>();
        var below = new List<(string SourcePath, string ReportFile, List<ClassCov> Classes, double Line, double Branch,
            int LinesToCover, int BranchesToCover)>();

        foreach (var sourcePath in sourceFiles)
        {
            var classes = ClassesFor(model, sourcePath, out _);
            if (classes.Count == 0)
            {
                notInReport.Add(sourcePath);
                continue;
            }

            var reportFile = NormalizePath(classes[0].File);
            var lines = CoverageModel.MergeLines(classes.SelectMany(c => c.Methods).SelectMany(m => m.Lines));
            if (lines.Count == 0)
            {
                // No line data: fall back to the class-level rates the report carries.
                if (classes.All(c => c.LineRate >= targetRate && c.BranchRate >= targetRate)) done.Add(reportFile);
                else below.Add((sourcePath, reportFile, classes, classes.Min(c => c.LineRate), classes.Min(c => c.BranchRate), 0, 0));
                continue;
            }

            int totalLines = lines.Count, coveredLines = lines.Count(l => l.Hits > 0);
            int totalBranches = lines.Sum(l => l.BranchesTotal), coveredBranches = lines.Sum(l => l.BranchesCovered);
            // Additional lines / branch outcomes that must be covered to reach the target.
            var linesToCover = Math.Max(0, CeilFraction(targetRate, totalLines) - coveredLines);
            var branchesToCover = totalBranches == 0 ? 0 : Math.Max(0, CeilFraction(targetRate, totalBranches) - coveredBranches);

            if (linesToCover == 0 && branchesToCover == 0) done.Add(reportFile);
            else below.Add((sourcePath, reportFile, classes, (double)coveredLines / totalLines,
                totalBranches == 0 ? 1 : (double)coveredBranches / totalBranches, linesToCover, branchesToCover));
        }

        // Smallest gaps first: they finish in one cycle and leave the rest of the batch shorter.
        var ordered = below.OrderBy(b => b.LinesToCover + b.BranchesToCover).ThenBy(b => b.ReportFile).ToList();

        // The report's line numbers describe each file as it was when the tests ran. A file edited
        // since then (a seam added for testing, say) would show the wrong lines as uncovered, so it
        // is flagged and its source left out until the tests run again.
        var reportWrittenUtc = File.GetLastWriteTimeUtc(coberturaXmlPath);
        var detailed = ordered.Take(maxFiles).Select(b =>
        {
            var changed = File.Exists(b.SourcePath) && File.GetLastWriteTimeUtc(b.SourcePath) > reportWrittenUtc;
            var (methods, closes) = PickMethods(b.Classes, b.LinesToCover, b.BranchesToCover, maxMethodsPerFile,
                includeSource && !changed ? ReadLines(b.SourcePath) : null);
            return new FileGap(b.ReportFile, Math.Round(b.Line, 4), Math.Round(b.Branch, 4),
                b.LinesToCover, b.BranchesToCover, closes, methods, changed);
        }).ToList();

        var more = ordered.Skip(maxFiles).Take(MaxFileList)
            .Select(b => new FileRates(b.ReportFile, Math.Round(b.Line, 4), Math.Round(b.Branch, 4)))
            .ToList();

        return new CoverageGapsResult(targetRate, sourceFiles.Count, done.Count, below.Count, detailed, more,
            done.Order(StringComparer.OrdinalIgnoreCase).Take(MaxFileList).ToList(),
            notInReport.Take(MaxFileList).ToList());
    }

    // ceil(fraction * total), robust to 0.8 * 5 = 4.000000000000001.
    private static int CeilFraction(double fraction, int total) => (int)Math.Ceiling(fraction * total - 1e-9);

    private static (List<MethodGap> Methods, bool ClosesGap) PickMethods(
        List<ClassCov> classes, int linesToCover, int branchesToCover, int max, string[]? source)
    {
        // Rank by what each method can add towards the metrics still below target, in
        // absolute counts: a long half-covered method holds more of the gap than a tiny
        // helper at 0%, which is what ranking by rate used to prefer.
        var candidates = classes
            .SelectMany(c => c.Methods.Select(m => (Class: c, Method: m,
                Lines: m.Lines.Count(l => l.Hits == 0),
                Branches: m.Lines.Sum(l => l.BranchesTotal - l.BranchesCovered))))
            .Select(x => (x.Class, x.Method, x.Lines, x.Branches,
                Gain: (linesToCover > 0 ? x.Lines : 0) + (branchesToCover > 0 ? x.Branches : 0)))
            .Where(x => x.Gain > 0)
            .OrderByDescending(x => x.Gain)
            .ThenBy(x => x.Method.FirstLine)
            .ToList();

        var picked = new List<MethodGap>();
        int needLines = linesToCover, needBranches = branchesToCover;
        foreach (var c in candidates)
        {
            if ((needLines <= 0 && needBranches <= 0) || picked.Count >= max) break;
            needLines -= c.Lines;
            needBranches -= c.Branches;

            var uncoveredLines = c.Method.Lines.Where(l => l.Hits == 0).Select(l => l.Number).ToList();
            var branches = c.Method.Lines.Where(l => l.Missing.Count > 0)
                .Select(l => new UncoveredBranch(l.Number, l.Missing.ToList())).ToList();
            picked.Add(new MethodGap(c.Method.Name, c.Class.Name, Math.Round(c.Method.LineRate, 4), Math.Round(c.Method.BranchRate, 4),
                uncoveredLines.Count == 0 ? null : Ranges(uncoveredLines),
                branches,
                source == null ? null : Snippet(source, uncoveredLines, branches.Select(b => b.Line))));
        }

        return (picked, needLines <= 0 && needBranches <= 0);
    }

    private static string[]? ReadLines(string path)
    {
        try { return File.ReadAllLines(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // [10, 11, 12, 15] → "10-12, 15"
    internal static string Ranges(IReadOnlyList<int> sortedLines)
    {
        var parts = new List<string>();
        for (var i = 0; i < sortedLines.Count; i++)
        {
            var start = sortedLines[i];
            while (i + 1 < sortedLines.Count && sortedLines[i + 1] == sortedLines[i] + 1) i++;
            parts.Add(start == sortedLines[i] ? $"{start}" : $"{start}-{sortedLines[i]}");
        }
        return string.Join(", ", parts);
    }

    // The source around each place a test still has to reach: every uncovered branch line and
    // the start of every uncovered stretch, two lines either side. Saves a file read per method.
    internal static string? Snippet(string[] source, IReadOnlyList<int> uncoveredLines, IEnumerable<int> branchLines)
    {
        var starts = uncoveredLines.Where((n, i) => i == 0 || uncoveredLines[i - 1] != n - 1);
        var wanted = new SortedSet<int>();
        foreach (var point in starts.Concat(branchLines))
            for (var n = point - SourceContext; n <= point + SourceContext; n++)
                if (n >= 1 && n <= source.Length) wanted.Add(n);
        if (wanted.Count == 0) return null;

        var output = new List<string>();
        var previous = 0;
        foreach (var n in wanted.Take(MaxSourceLinesPerMethod))
        {
            if (previous != 0 && n != previous + 1) output.Add("…");
            output.Add($"{n}: {source[n - 1].TrimEnd()}");
            previous = n;
        }
        if (wanted.Count > MaxSourceLinesPerMethod) output.Add("…");
        return string.Join("\n", output);
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
