using System.Globalization;
using DotNetCoverageMcp.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace DotNetCoverageMcp.Tests.Unit;

public class CoberturaServiceTests : IDisposable
{
    private readonly CoberturaService _sut;
    private readonly string _tempDir;

    public CoberturaServiceTests()
    {
        var logger = new Mock<ILogger<CoberturaService>>();
        _sut = new CoberturaService(logger.Object);
        _tempDir = Path.Combine(Path.GetTempPath(), $"cst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // --- ParseSummary ---

    [Fact]
    public void ParseSummary_ParsesClassesAndMethods()
    {
        var path = WriteSummaryJson(@"{
  ""coverage"": {
    ""assemblies"": [{
      ""classes"": [{
        ""name"": ""MyApp.Foo"",
        ""linecoverage"": 80.0,
        ""branchcoverage"": 60.0,
        ""methods"": [{
          ""name"": ""DoWork"",
          ""linecoverage"": 100.0,
          ""branchcoverage"": 50.0
        }]
      }]
    }]
  }
}");
        var result = _sut.ParseSummary(path);

        result.Should().HaveCount(1);
    }

    [Fact]
    public void ParseSummary_ThrowsOnMalformedJson()
    {
        var path = WriteSummaryJson(@"{ ""bad"": true }");

        var act = () => _sut.ParseSummary(path);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ParseSummary_HandlesEmptyAssemblies()
    {
        var path = WriteSummaryJson(@"{ ""coverage"": { ""assemblies"": [] } }");
        _sut.ParseSummary(path).Should().BeEmpty();
    }

    // --- GetFileCoverage ---

    [Fact]
    public void GetFileCoverage_MatchesByFileName()
    {
        var path = WriteCoberturaXml(@"
<coverage>
  <packages><package><classes>
    <class name=""MyApp.Foo"" filename=""src/MyApp/Foo.cs"" line-rate=""0.9"" branch-rate=""0.85"">
      <methods>
        <method name=""DoWork"" line-rate=""1.0"" branch-rate=""0.8"" />
      </methods>
    </class>
  </classes></package></packages>
</coverage>");

        var result = _sut.GetFileCoverage(path, "Foo.cs", 0.8);

        result.AllMeetTarget.Should().BeTrue();
        result.Classes.Should().HaveCount(1);
        result.Classes[0].Class.Should().Be("MyApp.Foo");
    }

    [Fact]
    public void GetFileCoverage_BackslashNormalization()
    {
        var path = WriteCoberturaXml(@"
<coverage>
  <packages><package><classes>
    <class name=""Foo"" filename=""src\MyApp\Foo.cs"" line-rate=""0.9"" branch-rate=""0.9"">
      <methods/>
    </class>
  </classes></package></packages>
</coverage>");

        var result = _sut.GetFileCoverage(path, "MyApp/Foo.cs", 0.8);
        result.Classes.Should().HaveCount(1);
    }

    [Fact]
    public void GetFileCoverage_AllMeetTarget_False()
    {
        var path = WriteCoberturaXml(@"
<coverage>
  <packages><package><classes>
    <class name=""Foo"" filename=""Foo.cs"" line-rate=""0.5"" branch-rate=""0.3"">
      <methods/>
    </class>
  </classes></package></packages>
</coverage>");

        var result = _sut.GetFileCoverage(path, "Foo.cs", 0.8);
        result.AllMeetTarget.Should().BeFalse();
    }

    [Fact]
    public void GetFileCoverage_ThrowsWhenNoClassFound()
    {
        var path = WriteCoberturaXml(@"
<coverage>
  <packages><package><classes>
    <class name=""Foo"" filename=""Foo.cs"" line-rate=""1"" branch-rate=""1"">
      <methods/>
    </class>
  </classes></package></packages>
</coverage>");

        var act = () => _sut.GetFileCoverage(path, "NonExistent.cs", 0.8);
        act.Should().Throw<KeyNotFoundException>();
    }

    // --- GetUncoveredBranches ---

    [Fact]
    public void GetUncoveredBranches_FindsUncoveredConditions()
    {
        var path = WriteCoberturaXml(@"
<coverage>
  <packages><package><classes>
    <class name=""Foo"">
      <methods>
        <method name=""Calculate"">
          <lines>
            <line number=""10"" branch=""True"">
              <conditions>
                <condition number=""1"" type=""jump"" coverage=""0%""/>
                <condition number=""2"" type=""jump"" coverage=""100%""/>
              </conditions>
            </line>
          </lines>
        </method>
      </methods>
    </class>
  </classes></package></packages>
</coverage>");

        var result = _sut.GetUncoveredBranches(path, "Calculate");

        result.MatchCount.Should().Be(1);
        result.Methods[0].UncoveredBranches.Should().HaveCount(1);
        result.Methods[0].UncoveredBranches[0].Line.Should().Be(10);
        result.Methods[0].UncoveredBranches[0].Missing.Should().ContainSingle()
            .Which.Should().Contain("condition 1");
    }

    [Fact]
    public void GetUncoveredBranches_CaseInsensitiveMatch()
    {
        var path = WriteCoberturaXml(@"
<coverage>
  <packages><package><classes>
    <class name=""Foo"">
      <methods>
        <method name=""DoWork"">
          <lines/>
        </method>
      </methods>
    </class>
  </classes></package></packages>
</coverage>");

        var result = _sut.GetUncoveredBranches(path, "dowork");
        result.MatchCount.Should().Be(1);
    }

    [Fact]
    public void GetUncoveredBranches_ThrowsWhenNoMethodFound()
    {
        var path = WriteCoberturaXml(@"
<coverage>
  <packages><package><classes>
    <class name=""Foo"">
      <methods>
        <method name=""Existing""><lines/></method>
      </methods>
    </class>
  </classes></package></packages>
</coverage>");

        var act = () => _sut.GetUncoveredBranches(path, "NonExistent");
        act.Should().Throw<KeyNotFoundException>();
    }

    // --- ComputeDiff ---

    [Fact]
    public void ComputeDiff_DetectsChangedMethods()
    {
        var current = WriteCoberturaXml(@"
<coverage line-rate=""0.9"" branch-rate=""0.8"">
  <packages><package><classes>
    <class name=""Foo"">
      <methods>
        <method name=""Work"" signature=""()"" line-rate=""0.9"" branch-rate=""0.8""/>
      </methods>
    </class>
  </classes></package></packages>
</coverage>", "current.xml");

        var prev = WriteCoberturaXml(@"
<coverage line-rate=""0.5"" branch-rate=""0.4"">
  <packages><package><classes>
    <class name=""Foo"">
      <methods>
        <method name=""Work"" signature=""()"" line-rate=""0.5"" branch-rate=""0.4""/>
      </methods>
    </class>
  </classes></package></packages>
</coverage>", "prev.xml");

        var result = _sut.ComputeDiff(current, prev);

        result.FirstRun.Should().BeFalse();
        result.ChangedMethods.Should().HaveCount(1);
        result.ChangedMethods![0].Name.Should().Be("Work");
        result.CycleImprovement!.LineDelta.Should().BeApproximately(0.4, 0.001);
    }

    [Fact]
    public void ComputeDiff_DetectsNewMethods()
    {
        var current = WriteCoberturaXml(@"
<coverage line-rate=""0.8"" branch-rate=""0.7"">
  <packages><package><classes>
    <class name=""Foo"">
      <methods>
        <method name=""New"" signature=""()"" line-rate=""1.0"" branch-rate=""1.0""/>
      </methods>
    </class>
  </classes></package></packages>
</coverage>", "current2.xml");

        var prev = WriteCoberturaXml(@"
<coverage line-rate=""0.0"" branch-rate=""0.0"">
  <packages><package><classes>
    <class name=""Foo""><methods/></class>
  </classes></package></packages>
</coverage>", "prev2.xml");

        var result = _sut.ComputeDiff(current, prev);

        result.ChangedMethods.Should().HaveCount(1);
        result.ChangedMethods![0].LineBefore.Should().Be(0.0);
    }

    [Fact]
    public void ComputeDiff_DetectsRemovedMethods()
    {
        var current = WriteCoberturaXml(@"
<coverage line-rate=""0.8"" branch-rate=""0.7"">
  <packages><package><classes>
    <class name=""Foo""><methods/></class>
  </classes></package></packages>
</coverage>", "current3.xml");

        var prev = WriteCoberturaXml(@"
<coverage line-rate=""0.5"" branch-rate=""0.5"">
  <packages><package><classes>
    <class name=""Foo"">
      <methods>
        <method name=""Removed"" signature=""()"" line-rate=""0.5"" branch-rate=""0.5""/>
      </methods>
    </class>
  </classes></package></packages>
</coverage>", "prev3.xml");

        var result = _sut.ComputeDiff(current, prev);

        result.RemovedMethods.Should().HaveCount(1);
        result.RemovedMethods![0].LineAfter.Should().Be(0.0);
    }

    // --- Culture invariance ---

    [Fact]
    public void GetFileCoverage_ParsesCorrectlyUnderCommaDecimalCulture()
    {
        var path = WriteCoberturaXml(@"
<coverage>
  <packages><package><classes>
    <class name=""Foo"" filename=""Foo.cs"" line-rate=""0.85"" branch-rate=""0.75"">
      <methods>
        <method name=""Do"" line-rate=""0.95"" branch-rate=""0.65"" />
      </methods>
    </class>
  </classes></package></packages>
</coverage>");

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            // German uses comma as decimal separator
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var result = _sut.GetFileCoverage(path, "Foo.cs", 0.8);

            result.Classes[0].LineRate.Should().BeApproximately(0.85, 0.001);
            result.Classes[0].BranchRate.Should().BeApproximately(0.75, 0.001);
            result.Classes[0].Methods[0].LineRate.Should().BeApproximately(0.95, 0.001);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void ComputeDiff_ParsesCorrectlyUnderCommaDecimalCulture()
    {
        var current = WriteCoberturaXml(@"
<coverage line-rate=""0.9"" branch-rate=""0.8"">
  <packages><package><classes>
    <class name=""Foo"">
      <methods>
        <method name=""Work"" signature=""()"" line-rate=""0.9"" branch-rate=""0.8""/>
      </methods>
    </class>
  </classes></package></packages>
</coverage>", "culture-cur.xml");

        var prev = WriteCoberturaXml(@"
<coverage line-rate=""0.5"" branch-rate=""0.4"">
  <packages><package><classes>
    <class name=""Foo"">
      <methods>
        <method name=""Work"" signature=""()"" line-rate=""0.5"" branch-rate=""0.4""/>
      </methods>
    </class>
  </classes></package></packages>
</coverage>", "culture-prev.xml");

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var result = _sut.ComputeDiff(current, prev);

            result.CycleImprovement!.LineDelta.Should().BeApproximately(0.4, 0.001);
            result.ChangedMethods.Should().HaveCount(1);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    // --- Helpers ---

    private string WriteSummaryJson(string json)
    {
        var path = Path.Combine(_tempDir, $"Summary-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private string WriteCoberturaXml(string xml, string? name = null)
    {
        name ??= $"cobertura-{Guid.NewGuid():N}.xml";
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, xml);
        return path;
    }
}
