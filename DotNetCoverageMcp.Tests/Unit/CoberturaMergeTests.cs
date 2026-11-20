using System.Xml.Linq;
using DotNetCoverageMcp.Services;
using FluentAssertions;

namespace DotNetCoverageMcp.Tests.Unit;

/// <summary>Reports of one multi-targeted test project, one per target framework, merged into one.</summary>
public class CoberturaMergeTests
{
    // Coverlet's shape: class lines, then each method's lines, and rates on every level.
    private static XDocument Report(string classes) => XDocument.Parse($"""
        <coverage line-rate="0" branch-rate="0" lines-covered="0" lines-valid="0" branches-covered="0" branches-valid="0">
          <sources><source>C:/repo/</source></sources>
          <packages><package name="App" line-rate="0" branch-rate="0"><classes>{classes}</classes></package></packages>
        </coverage>
        """);

    private const string Net10 = """
        <class name="App.Calc" filename="Calc.cs" line-rate="0.5" branch-rate="0.5">
          <methods>
            <method name="Add" signature="(int,int)" line-rate="0.5" branch-rate="0.5">
              <lines>
                <line number="1" hits="2" branch="False" />
                <line number="2" hits="0" branch="True" condition-coverage="50% (1/2)"><conditions><condition number="0" type="jump" coverage="50%" /></conditions></line>
              </lines>
            </method>
          </methods>
          <lines>
            <line number="1" hits="2" branch="False" />
            <line number="2" hits="0" branch="True" condition-coverage="50% (1/2)"><conditions><condition number="0" type="jump" coverage="50%" /></conditions></line>
          </lines>
        </class>
        """;

    // The other framework's run took the other branch, and covers a method only it compiles.
    private const string Net8 = """
        <class name="App.Calc" filename="Calc.cs" line-rate="0.6667" branch-rate="1">
          <methods>
            <method name="Add" signature="(int,int)" line-rate="0.5" branch-rate="1">
              <lines>
                <line number="1" hits="0" branch="False" />
                <line number="2" hits="1" branch="True" condition-coverage="100% (2/2)"><conditions><condition number="0" type="jump" coverage="100%" /></conditions></line>
              </lines>
            </method>
            <method name="Legacy" signature="()" line-rate="1" branch-rate="1">
              <lines><line number="9" hits="1" branch="False" /></lines>
            </method>
          </methods>
          <lines>
            <line number="1" hits="0" branch="False" />
            <line number="2" hits="1" branch="True" condition-coverage="100% (2/2)"><conditions><condition number="0" type="jump" coverage="100%" /></conditions></line>
            <line number="9" hits="1" branch="False" />
          </lines>
        </class>
        <class name="App.OnlyOnNet8" filename="OnlyOnNet8.cs" line-rate="1" branch-rate="1">
          <methods><method name="Run" signature="()" line-rate="1" branch-rate="1"><lines><line number="3" hits="1" branch="False" /></lines></method></methods>
          <lines><line number="3" hits="1" branch="False" /></lines>
        </class>
        """;

    private static string Attr(XElement e, string name) => (string)e.Attribute(name)!;

    [Fact]
    public void ALineOrBranchCoveredOnAnyFramework_CountsAsCovered()
    {
        var merged = CoberturaMerge.Merge([Report(Net10), Report(Net8)]);

        var add = merged.Descendants("method").Single(m => Attr(m, "name") == "Add");
        Attr(add, "line-rate").Should().Be("1");
        Attr(add, "branch-rate").Should().Be("1");
        add.Descendants("line").Select(l => Attr(l, "hits")).Should().Equal("2", "1");
        add.Descendants("line").Last().Element("conditions")!.Elements().Single().Attribute("coverage")!.Value.Should().Be("100%");

        var calc = merged.Descendants("class").Single(c => Attr(c, "name") == "App.Calc");
        calc.Element("methods")!.Elements().Select(m => Attr(m, "name")).Should().Equal("Add", "Legacy");
        Attr(calc, "line-rate").Should().Be("1");
        merged.Descendants("class").Select(c => Attr(c, "name")).Should().Equal("App.Calc", "App.OnlyOnNet8");

        // 4 lines (1, 2, 9 and OnlyOnNet8's 3), all covered; 2 of 2 branch outcomes.
        var root = merged.Root!;
        (Attr(root, "line-rate"), Attr(root, "lines-covered"), Attr(root, "lines-valid")).Should().Be(("1", "4", "4"));
        (Attr(root, "branch-rate"), Attr(root, "branches-covered"), Attr(root, "branches-valid")).Should().Be(("1", "2", "2"));
        merged.Descendants("source").Should().ContainSingle("both reports name the same root");
    }

    [Fact]
    public void MergedReport_ReadsLikeAnyOther()
    {
        var model = CoverageModel.Parse(CoberturaMerge.Merge([Report(Net8), Report(Net10)]));

        var calc = model.Classes.Single(c => c.Name == "App.Calc");
        calc.Methods.Select(m => m.Name).Should().BeEquivalentTo(["Add", "Legacy"]);
        calc.LineRate.Should().Be(1);
        calc.BranchRate.Should().Be(1);
    }

    [Fact]
    public void OrderOfTheReports_DoesNotChangeTheRates()
    {
        var a = CoberturaMerge.Merge([Report(Net10), Report(Net8)]).Root!;
        var b = CoberturaMerge.Merge([Report(Net8), Report(Net10)]).Root!;

        (Attr(a, "line-rate"), Attr(a, "branch-rate")).Should().Be((Attr(b, "line-rate"), Attr(b, "branch-rate")));
    }

    [Fact]
    public void AReportMergedWithItself_IsUnchanged()
    {
        var merged = CoberturaMerge.Merge([Report(Net10), Report(Net10)]);

        var add = merged.Descendants("method").Single();
        (Attr(add, "line-rate"), Attr(add, "branch-rate")).Should().Be(("0.5", "0.5"));
        add.Descendants("line").Should().HaveCount(2);
    }
}
