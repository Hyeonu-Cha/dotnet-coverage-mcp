using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotNetCoverageMcp.Services;

/// <summary>
/// One Cobertura report from several. `dotnet test` on a multi-targeted test project runs the
/// tests once per target framework, and each run writes its own report. Merged, a line or branch
/// outcome counts as covered when any framework's run covered it, and code compiled for one
/// framework only (<c>#if NET8_0</c>) comes from that framework's report. Rates are recomputed
/// from the merged lines, the way coverlet computes them.
/// </summary>
internal static class CoberturaMerge
{
    private static readonly Regex ConditionCoverageRx = new(@"\((\d+)/(\d+)\)", RegexOptions.Compiled);

    public static XDocument Merge(IReadOnlyList<XDocument> reports)
    {
        var merged = new XDocument(reports[0]);
        var root = merged.Root!;
        var sources = Child(root, "sources");
        var packages = Child(root, "packages");
        var classes = new Dictionary<(string?, string?), XElement>();
        foreach (var cls in root.Descendants("class")) classes.TryAdd(ClassKey(cls), cls);

        foreach (var report in reports.Skip(1))
        {
            foreach (var source in report.Descendants("source"))
                if (!sources.Elements("source").Any(s => s.Value.Trim() == source.Value.Trim()))
                    sources.Add(new XElement(source));

            foreach (var cls in report.Descendants("class"))
            {
                if (!classes.TryGetValue(ClassKey(cls), out var target))
                {
                    var package = cls.Ancestors("package").FirstOrDefault();
                    var name = (string?)package?.Attribute("name");
                    var into = packages.Elements("package").FirstOrDefault(p => (string?)p.Attribute("name") == name);
                    if (into == null)
                    {
                        into = new XElement("package", package?.Attributes().Select(a => new XAttribute(a)));
                        packages.Add(into);
                    }
                    var copy = new XElement(cls);
                    Child(into, "classes").Add(copy);
                    classes[ClassKey(cls)] = copy;
                    continue;
                }

                MergeLines(Child(target, "lines"), cls.Element("lines"));
                var methods = Child(target, "methods");
                foreach (var method in cls.Elements("methods").Elements("method"))
                {
                    var same = methods.Elements("method").FirstOrDefault(m => MethodKey(m) == MethodKey(method));
                    if (same == null) methods.Add(new XElement(method));
                    else MergeLines(Child(same, "lines"), method.Element("lines"));
                }
            }
        }

        foreach (var method in root.Descendants("method")) SetRates(method, method.Elements("lines").Elements("line"));
        foreach (var cls in root.Descendants("class")) SetRates(cls, ClassLines(cls));
        foreach (var package in root.Descendants("package")) SetRates(package, package.Descendants("class").SelectMany(ClassLines));
        SetRates(root, root.Descendants("class").SelectMany(ClassLines), totals: true);
        return merged;
    }

    private static (string?, string?) ClassKey(XElement cls) => ((string?)cls.Attribute("name"), (string?)cls.Attribute("filename"));

    private static (string?, string?) MethodKey(XElement method) => ((string?)method.Attribute("name"), (string?)method.Attribute("signature"));

    private static XElement Child(XElement parent, string name)
    {
        var child = parent.Element(name);
        if (child == null) parent.Add(child = new XElement(name));
        return child;
    }

    // Each line once: the class's own <lines> when the writer gives them, else its methods' lines.
    private static IEnumerable<XElement> ClassLines(XElement cls)
    {
        var own = cls.Elements("lines").Elements("line").ToList();
        return own.Count > 0
            ? own
            : cls.Elements("methods").Elements("method").Elements("lines").Elements("line")
                .GroupBy(l => (string?)l.Attribute("number")).Select(g => g.MaxBy(l => Branches(l).Covered)!);
    }

    // A line both reports have keeps the higher hit count and the better-covered branch data.
    // Branch outcomes have no identity across reports, so they can't be unioned one by one.
    private static void MergeLines(XElement target, XElement? source)
    {
        if (source == null) return;
        var byNumber = new Dictionary<string, XElement>();
        foreach (var line in target.Elements("line")) byNumber.TryAdd((string?)line.Attribute("number") ?? "", line);

        foreach (var line in source.Elements("line"))
        {
            var number = (string?)line.Attribute("number") ?? "";
            if (!byNumber.TryGetValue(number, out var mine))
            {
                var copy = new XElement(line);
                target.Add(copy);
                byNumber[number] = copy;
                continue;
            }
            if (Hits(line) > Hits(mine)) mine.SetAttributeValue("hits", Hits(line).ToString(CultureInfo.InvariantCulture));
            if (Branches(line).Covered > Branches(mine).Covered)
            {
                mine.SetAttributeValue("branch", (string?)line.Attribute("branch"));
                mine.SetAttributeValue("condition-coverage", (string?)line.Attribute("condition-coverage"));
                mine.Elements("conditions").Remove();
                mine.Add(line.Elements("conditions").Select(c => new XElement(c)));
            }
        }
    }

    private static int Hits(XElement line) =>
        int.TryParse((string?)line.Attribute("hits"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? h : 0;

    // As CoverageModel reads them: "50% (1/2)", else one outcome per <condition>.
    private static (int Covered, int Total) Branches(XElement line)
    {
        var cc = ConditionCoverageRx.Match((string?)line.Attribute("condition-coverage") ?? "");
        if (cc.Success)
            return (int.Parse(cc.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(cc.Groups[2].Value, CultureInfo.InvariantCulture));
        var conditions = line.Descendants("condition").ToList();
        return (conditions.Count(c => (string?)c.Attribute("coverage") != "0%"), conditions.Count);
    }

    private static void SetRates(XElement element, IEnumerable<XElement> lines, bool totals = false)
    {
        var list = lines.ToList();
        if (list.Count == 0) return; // keep what the writer said about an element without lines
        var covered = list.Count(l => Hits(l) > 0);
        int branchesCovered = 0, branchesTotal = 0;
        foreach (var (c, t) in list.Select(Branches)) { branchesCovered += c; branchesTotal += t; }

        element.SetAttributeValue("line-rate", Rate(covered, list.Count));
        element.SetAttributeValue("branch-rate", branchesTotal == 0 ? "1" : Rate(branchesCovered, branchesTotal));
        if (!totals) return;
        element.SetAttributeValue("lines-covered", covered);
        element.SetAttributeValue("lines-valid", list.Count);
        element.SetAttributeValue("branches-covered", branchesCovered);
        element.SetAttributeValue("branches-valid", branchesTotal);
    }

    private static string Rate(int part, int whole) =>
        Math.Round((double)part / whole, 4).ToString(CultureInfo.InvariantCulture);
}
