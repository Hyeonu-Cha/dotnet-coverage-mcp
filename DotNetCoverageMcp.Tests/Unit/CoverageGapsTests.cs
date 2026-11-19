using System.Text.Json;
using DotNetCoverageMcp.Helpers;
using DotNetCoverageMcp.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetCoverageMcp.Tests.Unit;

/// <summary>GetCoverageGaps over real source files and a report whose &lt;source&gt; is their folder.</summary>
public class CoverageGapsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gaps-{Guid.NewGuid():N}");
    private readonly CoberturaService _sut = new(NullLogger<CoberturaService>.Instance);

    public CoverageGapsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Src(string name, int lineCount)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, Enumerable.Range(1, lineCount).Select(i => $"    code line {i};"));
        return path;
    }

    private string Report(string classes)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, $"""
            <coverage line-rate="0" branch-rate="0">
              <sources><source>{_dir.Replace('\\', '/')}/</source></sources>
              <packages><package name="App"><classes>{classes}</classes></package></packages>
            </coverage>
            """);
        return path;
    }

    // hits per line: "10:1 11:0" ; branches: "12:1/2" marks line 12 as a half-covered jump.
    private static string Method(string name, string hits, string branches = "")
    {
        var b = branches.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split(':')).ToDictionary(x => int.Parse(x[0]), x => x[1]);
        var lines = hits.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x =>
        {
            var (n, h) = (int.Parse(x.Split(':')[0]), x.Split(':')[1]);
            if (!b.TryGetValue(n, out var cc))
                return $"""<line number="{n}" hits="{h}" branch="False" />""";
            var (c, t) = (int.Parse(cc.Split('/')[0]), int.Parse(cc.Split('/')[1]));
            var cov = c == t ? "100%" : c == 0 ? "0%" : "50%";
            return $"""<line number="{n}" hits="{h}" branch="True" condition-coverage="{c * 100 / t}% ({cc})"><conditions><condition number="1" type="jump" coverage="{cov}" /></conditions></line>""";
        });
        return $"""<method name="{System.Security.SecurityElement.Escape(name)}" signature="()" line-rate="0" branch-rate="0"><lines>{string.Concat(lines)}</lines></method>""";
    }

    private static string Class(string name, string file, string methods) =>
        $"""<class name="{name}" filename="{file}" line-rate="0" branch-rate="0"><methods>{methods}</methods></class>""";

    [Fact]
    public void FileAtTarget_IsDone_FileBelow_GetsTheMethodsThatCloseItsGap()
    {
        var good = Src("Good.cs", 20);
        var bad = Src("Bad.cs", 30);
        var report = Report(
            Class("App.Good", "Good.cs", Method("Run", "1:1 2:1 3:1 4:1 5:1")) +
            Class("App.Bad", "Bad.cs",
                Method("Big", "10:1 11:0 12:0 13:0 14:0 15:0") +     // 5 uncovered
                Method("Small", "20:0") +                             // 1 uncovered, 0%
                Method("Covered", "25:1 26:1 27:1 28:1")));

        var result = _sut.GetCoverageGaps(report, [good, bad], 0.8, 5, 5, includeSource: false);

        result.FilesDone.Should().Be(1);
        result.Done.Should().Equal("Good.cs");
        var file = result.Remaining.Single();
        file.File.Should().Be("Bad.cs");
        // 11 lines, 5 covered; 80% needs ceil(8.8) = 9, so 4 more.
        file.LinesToCover.Should().Be(4);
        // Ranked by absolute gain: the long half-covered method first, and it alone closes the gap.
        file.Methods.Select(m => m.Method).Should().Equal("Big");
        file.ClosesGap.Should().BeTrue();
        file.Methods[0].UncoveredLines.Should().Be("11-15");
    }

    [Fact]
    public void OnlyBranchesBelowTarget_RanksByUncoveredBranches()
    {
        var src = Src("Calc.cs", 40);
        var report = Report(Class("App.Calc", "Calc.cs",
            Method("ManyLines", "1:1 2:1 3:1 4:1 5:1 6:1 7:1 8:1 9:1 10:1") +
            Method("Branchy", "20:1 21:1", branches: "20:1/2 21:1/2") +
            Method("OneBranch", "30:1", branches: "30:1/2")));

        var file = _sut.GetCoverageGaps(report, [src], 0.8, 5, 5, false).Remaining.Single();

        file.LinesToCover.Should().Be(0, "every line is covered");
        file.BranchesToCover.Should().BeGreaterThan(0);
        file.Methods[0].Method.Should().Be("Branchy");
        file.Methods.Should().NotContain(m => m.Method == "ManyLines");
    }

    [Fact]
    public void MethodCap_ReportsThatTheGapIsNotClosed()
    {
        var src = Src("Wide.cs", 60);
        var report = Report(Class("App.Wide", "Wide.cs",
            string.Concat(Enumerable.Range(0, 6).Select(i => Method($"M{i}", $"{i * 10 + 1}:0 {i * 10 + 2}:0")))));

        var file = _sut.GetCoverageGaps(report, [src], 0.8, 5, maxMethodsPerFile: 2, includeSource: false).Remaining.Single();

        file.Methods.Should().HaveCount(2);
        file.ClosesGap.Should().BeFalse();
    }

    [Fact]
    public void FileMissingFromReport_IsListedAsNotInReport()
    {
        var missing = Src("Orphan.cs", 5);
        var report = Report(Class("App.Other", "Other.cs", Method("Run", "1:1")));

        var result = _sut.GetCoverageGaps(report, [missing], 0.8, 5, 5, false);

        result.NotInReport.Should().Equal(missing);
        result.FilesRemaining.Should().Be(0);
    }

    [Fact]
    public void SmallestGapFirst_AndOverflowListedWithRatesOnly()
    {
        var a = Src("A.cs", 10);
        var b = Src("B.cs", 10);
        var report = Report(
            Class("App.A", "A.cs", Method("Run", "1:0 2:0 3:0 4:0 5:0")) +      // needs 4
            Class("App.B", "B.cs", Method("Run", "1:1 2:1 3:1 4:0 5:0")));      // needs 1

        var result = _sut.GetCoverageGaps(report, [a, b], 0.8, maxFiles: 1, 5, false);

        result.Remaining.Single().File.Should().Be("B.cs");
        result.MoreRemaining.Single().File.Should().Be("A.cs");
        result.FilesRemaining.Should().Be(2);
    }

    [Fact]
    public void IncludeSource_ShowsLinesAroundEachGap()
    {
        var path = Path.Combine(_dir, "Order.cs");
        File.WriteAllLines(path,
        [
            "public class Order",                       // 1
            "{",                                        // 2
            "    public string Status(int n)",          // 3
            "    {",                                    // 4
            "        if (n < 0) return \"bad\";",       // 5
            "        return \"ok\";",                   // 6
            "    }",                                    // 7
            "}",                                        // 8
        ]);
        var report = Report(Class("App.Order", "Order.cs", Method("Status", "4:1 5:1 6:1 7:1", branches: "5:1/2")));

        var source = _sut.GetCoverageGaps(report, [path], 0.8, 5, 5, includeSource: true).Remaining.Single().Methods.Single().Source;

        source.Should().Contain("5:         if (n < 0) return \"bad\";");
        source!.Split('\n').Should().HaveCount(5, "two lines either side of line 5");
    }

    [Fact]
    public void SourceEditedAfterTheRun_IsFlagged_AndItsSourceLeftOut()
    {
        // Regression: the report's line numbers were applied to the file as it is now, so after
        // an edit the "uncovered" lines and the source around them pointed at other code.
        var edited = Path.Combine(_dir, "Edited.cs");
        var untouched = Path.Combine(_dir, "Untouched.cs");
        foreach (var p in new[] { edited, untouched })
            File.WriteAllLines(p, ["public class X", "{", "    public int M(int n)", "    {", "        if (n < 0) return 0;", "        return n;", "    }", "}"]);
        var report = Report(
            Class("App.Edited", "Edited.cs", Method("M", "4:1 5:1 6:0 7:1", branches: "5:1/2")) +
            Class("App.Untouched", "Untouched.cs", Method("M", "4:1 5:1 6:0 7:1", branches: "5:1/2")));
        File.SetLastWriteTimeUtc(report, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(untouched, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(edited, DateTime.UtcNow);

        var files = _sut.GetCoverageGaps(report, [edited, untouched], 0.8, 5, 5, includeSource: true).Remaining;

        var e = files.Single(f => f.File.EndsWith("Edited.cs", StringComparison.Ordinal));
        e.SourceChangedSinceRun.Should().BeTrue();
        e.Methods.Should().OnlyContain(m => m.Source == null);
        var u = files.Single(f => f.File.EndsWith("Untouched.cs", StringComparison.Ordinal));
        u.SourceChangedSinceRun.Should().BeFalse();
        u.Methods.Should().Contain(m => m.Source != null);

        // Only a flagged file carries the property, so the usual response stays as short as before.
        JsonHelper.Serialize(u).Should().NotContain("sourceChangedSinceRun");
        JsonHelper.Serialize(e).Should().Contain("\"sourceChangedSinceRun\":true");
    }

    [Theory]
    [InlineData(new[] { 10, 11, 12, 15 }, "10-12, 15")]
    [InlineData(new[] { 3 }, "3")]
    [InlineData(new[] { 1, 3, 4 }, "1, 3-4")]
    public void Ranges_CompressesContiguousLines(int[] lines, string expected) =>
        CoberturaService.Ranges(lines).Should().Be(expected);

    [Fact]
    public void Tool_SkipsNonTargetFilesAndValidatesParameters()
    {
        // Real C#: the scope is classified, and placeholder lines would parse as top-level statements.
        File.WriteAllText(Path.Combine(_dir, "OrderService.cs"), """
            public class OrderService
            {
                public bool Place(int n) => n > 0;
            }
            """);
        File.WriteAllText(Path.Combine(_dir, "Program.cs"), "var app = WebApplication.Create(); app.Run();");
        var report = Report(
            Class("App.OrderService", "OrderService.cs", Method("Place", "1:1 2:0")) +
            Class("Program", "Program.cs", Method("<Main>$", "1:0")));
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.ResolveCoberturaPath(report, null)).Returns(report);
        var tools = new CoverageTools(Mock.Of<IProcessRunner>(), sessions.Object,
            new FileService(NullLogger<FileService>.Instance), _sut, Mock.Of<ICodeInserter>(), Mock.Of<IPathGuard>());

        using var doc = JsonDocument.Parse(tools.GetCoverageGaps(report, _dir, includeSource: false));
        var root = doc.RootElement;
        root.TryGetProperty("filesInScope", out _).Should().BeTrue(root.ToString());
        root.GetProperty("filesInScope").GetInt32().Should().Be(1, "Program.cs is an entry point");
        root.GetProperty("excludedCount").GetInt32().Should().Be(1);
        root.GetProperty("remaining")[0].GetProperty("file").GetString().Should().Be("OrderService.cs");

        JsonDocument.Parse(tools.GetCoverageGaps(report, _dir, targetRate: 80)).RootElement
            .GetProperty("errorType").GetString().Should().Be("invalidParameter");
    }

    [Fact]
    public void Tool_CapsMaxFilesAndMaxMethodsPerFile()
    {
        // An unbounded maxFiles (1000) with source on could fill the agent's context.
        File.WriteAllText(Path.Combine(_dir, "A.cs"), "public class A { public int M() => 1; }");
        var report = Report(Class("App.A", "A.cs", Method("M", "1:0")));
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.ResolveCoberturaPath(report, null)).Returns(report);
        var cobertura = new Mock<ICoberturaService>();
        cobertura.Setup(c => c.GetCoverageGaps(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<double>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns(new CoverageGapsResult(0.8, 1, 0, 0, [], [], [], []));
        var tools = new CoverageTools(Mock.Of<IProcessRunner>(), sessions.Object,
            new FileService(NullLogger<FileService>.Instance), cobertura.Object, Mock.Of<ICodeInserter>(), Mock.Of<IPathGuard>());

        tools.GetCoverageGaps(report, _dir, maxFiles: 1000, maxMethodsPerFile: 500);
        tools.GetCoverageGaps(report, _dir, maxFiles: 3, maxMethodsPerFile: 7);

        cobertura.Verify(c => c.GetCoverageGaps(report, It.IsAny<IReadOnlyList<string>>(), 0.8, 20, 20, true), Times.Once);
        cobertura.Verify(c => c.GetCoverageGaps(report, It.IsAny<IReadOnlyList<string>>(), 0.8, 3, 7, true), Times.Once);
    }
}
