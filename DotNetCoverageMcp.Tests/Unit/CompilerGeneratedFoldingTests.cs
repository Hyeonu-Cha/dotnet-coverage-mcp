using DotNetCoverageMcp.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetCoverageMcp.Tests.Unit;

/// <summary>
/// Compiler-generated classes and methods in a coverlet report are folded into the method
/// they came from. Every class and method name below is the shape coverlet actually writes
/// (taken from real reports of the fixture project and of this repository).
/// </summary>
public class CompilerGeneratedFoldingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fold-{Guid.NewGuid():N}");
    private readonly CoberturaService _sut = new(NullLogger<CoberturaService>.Instance);

    public CompilerGeneratedFoldingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Report(string classes, string name = "c.xml")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, $"""
            <coverage line-rate="0.5" branch-rate="0.5">
              <sources><source>C:/repo/</source></sources>
              <packages><package name="App"><classes>{classes}</classes></package></packages>
            </coverage>
            """);
        return path;
    }

    private static string Line(int n, int hits, string? cc = null, string conditions = "") =>
        cc == null
            ? $"""<line number="{n}" hits="{hits}" branch="False" />"""
            : $"""<line number="{n}" hits="{hits}" branch="True" condition-coverage="{cc}"><conditions>{conditions}</conditions></line>""";

    private static string Method(string name, string lines, string signature = "()", string lr = "1", string br = "1") =>
        $"""<method name="{Esc(name)}" signature="{signature}" line-rate="{lr}" branch-rate="{br}"><lines>{lines}</lines></method>""";

    private static string Class(string name, string methods, string lr = "1", string br = "1") =>
        $"""<class name="{Esc(name)}" filename="Calculator.cs" line-rate="{lr}" branch-rate="{br}"><methods>{methods}</methods></class>""";

    // Real reports escape the compiler's angle brackets inside attributes.
    private static string Esc(string s) => System.Security.SecurityElement.Escape(s);

    private static string Uncovered(int number) => $"""<condition number="{number}" type="jump" coverage="0%" />""";

    [Fact]
    public void AsyncMethod_IsListedUnderItsOwnName_NotMoveNext()
    {
        // Regression: GetUncoveredBranches("AddAsync") used to be noMatch.
        var path = Report(
            Class("SampleLib.Calculator", Method("Add", Line(5, 1), "(System.Int32,System.Int32)")) +
            Class("SampleLib.Calculator/<AddAsync>d__2",
                Method("MoveNext", Line(18, 1) + Line(19, 1) + Line(20, 1, "50% (1/2)", Uncovered(1)) + Line(21, 0) + Line(22, 1),
                    lr: "0.8", br: "0.5")));

        var file = _sut.GetFileCoverage(path, "Calculator.cs", 0.8);
        file.Classes.Should().ContainSingle().Which.Class.Should().Be("SampleLib.Calculator");
        file.Classes[0].Methods.Select(m => m.Name).Should().BeEquivalentTo("Add", "AddAsync");

        var branches = _sut.GetUncoveredBranches(path, "AddAsync");
        branches.Methods.Should().ContainSingle().Which.Method.Should().Be("AddAsync");
        branches.Methods[0].Class.Should().Be("SampleLib.Calculator");
        branches.Methods[0].UncoveredBranches.Should().ContainSingle().Which.Line.Should().Be(20);
    }

    [Fact]
    public void ClassRate_IncludesAsyncBodies()
    {
        // Coverlet's class-level rate leaves out the state machine; the folded class must not.
        var path = Report(
            Class("App.Svc", Method("Sync", Line(5, 1)), lr: "1", br: "1") +
            Class("App.Svc/<RunAsync>d__1", Method("MoveNext", Line(10, 0) + Line(11, 0) + Line(12, 0), lr: "0", br: "1")));

        var cls = _sut.GetFileCoverage(path, "Calculator.cs", 0.8).Classes.Single();

        cls.LineRate.Should().Be(0.25, "1 of 4 lines is covered once the async body counts");
        cls.MeetsTarget.Should().BeFalse();
    }

    [Fact]
    public void AsyncOverloads_StaySeparate_LabelledByStartLine()
    {
        var path = Report(
            Class("App.Calc/<DescribeAsync>d__3", Method("MoveNext", Line(25, 1) + Line(26, 1))) +
            Class("App.Calc/<DescribeAsync>d__4", Method("MoveNext", Line(31, 1) + Line(33, 1, "50% (1/2)", Uncovered(0)), br: "0.5")));

        var methods = _sut.GetFileCoverage(path, "Calculator.cs", 0.8).Classes.Single().Methods.Select(m => m.Name);

        methods.Should().BeEquivalentTo("DescribeAsync (line 25)", "DescribeAsync (line 31)");
        _sut.GetUncoveredBranches(path, "DescribeAsync").Methods.Should().ContainSingle()
            .Which.Method.Should().Be("DescribeAsync (line 31)");
    }

    [Fact]
    public void LocalFunction_IsFoldedIntoItsMethod()
    {
        var path = Report(Class("App.Calc",
            Method("Twice", Line(44, 1) + Line(45, 1) + Line(48, 1), "(System.Int32)") +
            Method("<Twice>g__Double|7_0", Line(47, 1, "50% (1/2)", Uncovered(0)), "(System.Int32)", br: "0.5")));

        var method = _sut.GetFileCoverage(path, "Calculator.cs", 0.8).Classes.Single().Methods.Single();

        method.Name.Should().Be("Twice");
        method.BranchRate.Should().Be(0.5);
        _sut.GetUncoveredBranches(path, "Twice").Methods.Single().UncoveredBranches.Single().Line.Should().Be(47);
    }

    [Fact]
    public void StaticLambda_AttachesToTheOverloadWithTheSameOrdinal()
    {
        // <DrainAsync>b__13_0 belongs to <DrainAsync>d__13, not to the other overload d__12.
        var path = Report(
            Class("App.Runner/<DrainAsync>d__12", Method("MoveNext", Line(219, 1))) +
            Class("App.Runner/<DrainAsync>d__13", Method("MoveNext", Line(225, 1) + Line(238, 1))) +
            Class("App.Runner/<>c", Method("<DrainAsync>b__13_0", Line(230, 1, "50% (1/2)", Uncovered(0)), br: "0.5")));

        var branches = _sut.GetUncoveredBranches(path, "DrainAsync");

        branches.Methods.Should().ContainSingle().Which.Method.Should().Be("DrainAsync (line 225)");
    }

    [Fact]
    public void AsyncLambdaInClosure_IsFoldedIntoEnclosingMethod()
    {
        var path = Report(
            Class("App.Inserter/<InsertCodeAsync>d__3", Method("MoveNext", Line(38, 1) + Line(67, 1))) +
            Class("App.Inserter/<>c__DisplayClass3_0/<<InsertCodeAsync>b__0>d",
                Method("MoveNext", Line(46, 1) + Line(50, 1, "50% (1/2)", Uncovered(1)), br: "0.5")) +
            Class("App.Inserter/<>c__DisplayClass3_0", Method("<InsertCodeAsync>b__1", Line(55, 0))));

        var method = _sut.GetFileCoverage(path, "Calculator.cs", 0.8).Classes.Single().Methods.Single();

        method.Name.Should().Be("InsertCodeAsync");
        method.LineRate.Should().Be(0.8, "5 distinct lines across method, async lambda and closure; only line 55 uncovered");
        method.BranchRate.Should().Be(0.5);
    }

    [Fact]
    public void SharedLine_IsMergedOnce_WithBranchesAdded()
    {
        // A lambda on the same line as its caller: one line, both sets of branches.
        var path = Report(
            Class("App.Svc", Method("Filter", Line(15, 1, "100% (2/2)"))) +
            Class("App.Svc/<>c__DisplayClass2_0", Method("<Filter>b__0", Line(15, 3, "50% (1/2)", Uncovered(0)), br: "0.5")));

        var method = _sut.GetFileCoverage(path, "Calculator.cs", 0.8).Classes.Single().Methods.Single();

        method.LineRate.Should().Be(1);
        method.BranchRate.Should().Be(0.75, "3 of 4 branches across the method and its lambda");
    }

    [Fact]
    public void AsyncStubWithoutLines_IsDropped()
    {
        var path = Report(
            Class("App.Svc", Method("RunAsync", "")) +
            Class("App.Svc/<RunAsync>d__1", Method("MoveNext", Line(10, 1))));

        _sut.GetFileCoverage(path, "Calculator.cs", 0.8).Classes.Single().Methods.Should().ContainSingle();
    }

    [Fact]
    public void GenericArity_IsShownAsInSource()
    {
        var path = Report(Class("App.Repo`1", Method("Get", Line(3, 1))) + Class("App.Map`2", Method("Put", Line(9, 1))));

        _sut.GetFileCoverage(path, "Calculator.cs", 0.8).Classes.Select(c => c.Class)
            .Should().BeEquivalentTo("App.Repo<T>", "App.Map<T1,T2>");
    }

    [Fact]
    public void Diff_IsStableWhenTheStateMachineOrdinalShifts()
    {
        // Adding a method above RunAsync renumbers d__1 to d__2; that is not a change in RunAsync.
        var before = Report(Class("App.Svc/<RunAsync>d__1", Method("MoveNext", Line(10, 1), lr: "1")), "before.xml");
        var after = Report(Class("App.Svc/<RunAsync>d__2", Method("MoveNext", Line(12, 1), lr: "1")), "after.xml");

        var diff = _sut.ComputeDiff(after, before);

        diff.ChangedMethods.Should().BeEmpty();
        diff.RemovedMethods.Should().BeEmpty();
        diff.UnchangedCount.Should().Be(1);
    }

    [Fact]
    public void HalfCoveredIf_IsReportedAsUncovered()
    {
        // Regression: coverlet writes a one-sided `if` as one jump condition at 50%. Only 0%
        // conditions used to be listed, so this, the most common gap, never showed up.
        var path = Report(Class("App.Calc", Method("Classify",
            Line(9, 3, "100% (2/2)", """<condition number="7" type="jump" coverage="100%" />""") +
            Line(10, 1, "50% (1/2)", """<condition number="23" type="jump" coverage="50%" />"""),
            "(System.Int32)", br: "0.75")));

        var branch = _sut.GetUncoveredBranches(path, "Classify").Methods.Single().UncoveredBranches.Single();

        branch.Line.Should().Be(10);
        branch.Missing.Single().Should().Be("condition 23 (jump, 50% covered)");
    }

    [Fact]
    public void LineTotalsWithoutConditionElements_StillReportTheGap()
    {
        var path = Report(Class("App.Calc", Method("Pick",
            """<line number="4" hits="1" branch="True" condition-coverage="66% (2/3)" />""", br: "0.66")));

        _sut.GetUncoveredBranches(path, "Pick").Methods.Single().UncoveredBranches.Single().Missing
            .Should().Equal("1 of 3 branch outcomes not covered");
    }

    [Fact]
    public void ReparsesWhenTheReportChanges()
    {
        var path = Report(Class("App.Svc", Method("Run", Line(5, 0), lr: "0"), lr: "0"));
        _sut.GetFileCoverage(path, "Calculator.cs", 0.8).Classes.Single().LineRate.Should().Be(0);

        File.WriteAllText(path, File.ReadAllText(path).Replace("line-rate=\"0\"", "line-rate=\"1\"").Replace("hits=\"0\"", "hits=\"1\""));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        _sut.GetFileCoverage(path, "Calculator.cs", 0.8).Classes.Single().LineRate.Should().Be(1);
    }
}
