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

    // --- Untrusted input (SECURITY.md lists coverage XML as such) ---

    [Fact]
    public void Load_DoesNotExpandTheDocumentsOwnEntities()
    {
        // Regression: a 700-byte report whose class name expanded through nested entities came
        // back as a million-character response.
        var entities = string.Join(" ", new[] { $"<!ENTITY e0 \"{new string('A', 100)}\">" }
            .Concat(Enumerable.Range(1, 4).Select(i => $"<!ENTITY e{i} \"{string.Concat(Enumerable.Repeat($"&e{i - 1};", 10))}\">")));
        var path = WriteCoberturaXml($"""
            <?xml version="1.0"?>
            <!DOCTYPE coverage [ {entities} ]>
            <coverage line-rate="1" branch-rate="1"><packages><package><classes>
              <class name="&e4;" filename="x.cs" line-rate="1" branch-rate="1"><methods /><lines /></class>
            </classes></package></packages></coverage>
            """);

        var act = () => _sut.SummarizeClasses(path);

        act.Should().Throw<System.Xml.XmlException>().WithMessage("*undeclared entity*");
    }

    [Fact]
    public void Load_ReportWithCoberturasOwnDoctype_StillLoads_WithoutFetchingIt()
    {
        // Java Cobertura and some converters emit this DOCTYPE; it is skipped, never downloaded.
        var path = WriteCoberturaXml("""
            <?xml version="1.0"?>
            <!DOCTYPE coverage SYSTEM "http://cobertura.sourceforge.net/xml/coverage-04.dtd">
            <coverage line-rate="1" branch-rate="1"><packages><package><classes>
              <class name="App.Foo" filename="Foo.cs" line-rate="1" branch-rate="1"><methods /><lines /></class>
            </classes></package></packages></coverage>
            """);

        _sut.SummarizeClasses(path).Should().ContainSingle(c => c.Class == "App.Foo");
    }

    [Fact]
    public void Load_XmlThatIsNotACoverageReport_IsRejected()
    {
        var path = WriteCoberturaXml("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup /></Project>");

        var act = () => _sut.SummarizeClasses(path);

        act.Should().Throw<InvalidDataException>().WithMessage("*root element is <Project>*");
    }

    [Fact]
    public void GetCoverageDiff_FirstCallWithANonCoverageFile_FailsAndWritesNoBaseline()
    {
        // Regression: the first call copied any file (a .txt holding a token, in the review)
        // into .mcp-coverage/.coverage-prev.xml and answered firstRun.
        var secret = Path.Combine(_tempDir, "secret.txt");
        File.WriteAllText(secret, "TOP-SECRET-TOKEN=abc123");
        var fileService = new FileService(Microsoft.Extensions.Logging.Abstractions.NullLogger<FileService>.Instance);
        var guard = new Mock<IPathGuard>();
        guard.Setup(g => g.IsWithinAllowedRoot(It.IsAny<string>())).Returns(true);
        var tools = new CoverageTools(Mock.Of<IProcessRunner>(),
            new SessionManager(fileService, guard.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionManager>.Instance),
            fileService, _sut, Mock.Of<ICodeInserter>(), guard.Object);

        var result = tools.GetCoverageDiff(secret, _tempDir);

        result.Should().Contain("\"errorType\":\"parseFailed\"");
        File.Exists(Path.Combine(_tempDir, ".mcp-coverage", ".coverage-prev.xml")).Should().BeFalse();
    }

    // --- SummarizeClasses ---

    [Fact]
    public void SummarizeClasses_ReturnsClassesAndMethods()
    {
        // Built from the Cobertura XML, not reportgenerator's Summary.json (which has no
        // per-method data and a different schema). The per-file tools read the same report.
        var path = WriteCoberturaXml(@"
<coverage line-rate=""0.8"" branch-rate=""0.6"">
  <packages><package><classes>
    <class name=""MyApp.Foo"" filename=""Foo.cs"" line-rate=""0.8"" branch-rate=""0.6"">
      <methods>
        <method name=""DoWork"" signature=""()"" line-rate=""1.0"" branch-rate=""0.5"" />
      </methods>
    </class>
  </classes></package></packages>
</coverage>");
        var result = _sut.SummarizeClasses(path);

        result.Should().HaveCount(1);
        result[0].Class.Should().Be("MyApp.Foo");
        result[0].LineCoverage.Should().Be(0.8);
        result[0].BranchCoverage.Should().Be(0.6);
        result[0].Methods.Should().ContainSingle(m => m.Name == "DoWork");
        result[0].Methods[0].Branch.Should().Be(0.5);
    }

    [Fact]
    public void SummarizeClasses_ThrowsOnMalformedXml()
    {
        var path = WriteCoberturaXml(@"<coverage><bad");

        var act = () => _sut.SummarizeClasses(path);
        act.Should().Throw<System.Xml.XmlException>();
    }

    [Fact]
    public void SummarizeClasses_HandlesEmptyReport()
    {
        var path = WriteCoberturaXml(@"<coverage line-rate=""1"" branch-rate=""1""><packages /></coverage>");
        _sut.SummarizeClasses(path).Should().BeEmpty();
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

    // Shape of a real coverlet report: paths relative to <source>, forward slashes.
    private string WriteScopedReport(string sourceRoot) => WriteCoberturaXml($"""
        <coverage>
          <sources><source>{sourceRoot}</source></sources>
          <packages><package name="App"><classes>
            <class name="App.Services.CoberturaService" filename="Services/CoberturaService.cs" line-rate="0.9" branch-rate="0.9"><methods/></class>
            <class name="App.Services.FileService" filename="Services/FileService.cs" line-rate="0.5" branch-rate="0.5"><methods/></class>
            <class name="App.Controllers.OrderController" filename="Controllers/OrderController.cs" line-rate="0.9" branch-rate="0.9"><methods/></class>
            <class name="App.Controllers.AdminOrderController" filename="Controllers/AdminOrderController.cs" line-rate="0.1" branch-rate="0.1"><methods/></class>
            <class name="App.Areas.Shop.OrderController" filename="Areas/Shop/OrderController.cs" line-rate="0.2" branch-rate="0.2"><methods/></class>
          </classes></package></packages>
        </coverage>
        """);

    [Theory]
    [InlineData(@"C:\repo\Services\FileService.cs")]   // absolute, as GetSourceFiles returns it
    [InlineData("C:/repo/Services/FileService.cs")]
    [InlineData("Services/FileService.cs")]
    [InlineData(@"Services\FileService.cs")]
    [InlineData("FileService.cs")]
    public void GetFileCoverage_AcceptsAbsoluteRelativeAndBareNames(string requested)
    {
        var path = WriteScopedReport("C:/repo/");

        var result = _sut.GetFileCoverage(path, requested, 0.8);

        result.SourceFile.Should().Be("Services/FileService.cs");
        result.Classes.Select(c => c.Class).Should().Equal("App.Services.FileService");
    }

    [Fact]
    public void GetFileCoverage_AbsolutePathOnLinuxRoot()
    {
        var path = WriteScopedReport("/home/runner/work/repo/");

        var result = _sut.GetFileCoverage(path, "/home/runner/work/repo/Services/FileService.cs", 0.8);

        result.SourceFile.Should().Be("Services/FileService.cs");
    }

    [Theory]
    [InlineData("Service.cs")]          // used to match CoberturaService.cs and FileService.cs
    [InlineData("rController.cs")]
    public void GetFileCoverage_DoesNotMatchPartialFileNames(string requested)
    {
        var path = WriteScopedReport("C:/repo/");

        var act = () => _sut.GetFileCoverage(path, requested, 0.8);

        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void GetFileCoverage_DoesNotPullInFileWithLongerName()
    {
        // Regression: "OrderController.cs" also matched AdminOrderController.cs, whose 10%
        // coverage then made allMeetTarget false for a file that was at 90%.
        var path = WriteScopedReport("C:/repo/");

        var result = _sut.GetFileCoverage(path, "Controllers/OrderController.cs", 0.8);

        result.Classes.Select(c => c.Class).Should().Equal("App.Controllers.OrderController");
        result.AllMeetTarget.Should().BeTrue();
    }

    [Fact]
    public void GetFileCoverage_BareNameInTwoFolders_ThrowsAmbiguousWithCandidates()
    {
        var path = WriteScopedReport("C:/repo/");

        var act = () => _sut.GetFileCoverage(path, "OrderController.cs", 0.8);

        act.Should().Throw<AmbiguousFileException>()
            .Which.Candidates.Should().BeEquivalentTo("Controllers/OrderController.cs", "Areas/Shop/OrderController.cs");
    }

    [Fact]
    public void GetFileCoverage_AbsolutePathPicksExactFileAmongSameNames()
    {
        var path = WriteScopedReport("C:/repo/");

        var result = _sut.GetFileCoverage(path, @"C:\repo\Areas\Shop\OrderController.cs", 0.8);

        result.SourceFile.Should().Be("Areas/Shop/OrderController.cs");
        result.Classes.Should().ContainSingle();
    }

    [Fact]
    public void GetFileCoverage_AbsolutePathThroughASymlink_ResolvesToTheReportedPath()
    {
        // macOS: /var/folders/... is a link to /private/var/folders/..., and coverlet records
        // the resolved path. A repo under any symlinked folder behaves the same way.
        var real = Path.Combine(_tempDir, "real");
        var link = Path.Combine(_tempDir, "link");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "Foo.cs"), "class Foo { }");
        try { Directory.CreateSymbolicLink(link, real); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Windows without symlink privilege; the CI runners on Linux and macOS cover this.
        }

        var path = WriteCoberturaXml($"""
            <coverage>
              <sources><source>{real.Replace('\\', '/')}/</source></sources>
              <packages><package><classes>
                <class name="App.Foo" filename="Foo.cs" line-rate="1" branch-rate="1"><methods/></class>
              </classes></package></packages>
            </coverage>
            """);

        _sut.GetFileCoverage(path, Path.Combine(link, "Foo.cs"), 0.8).SourceFile.Should().Be("Foo.cs");
    }

    [Fact]
    public void GetFileCoverage_PartialClassAcrossOneFile_IsNotAmbiguous()
    {
        // Several <class> entries for one file (nested/generated types) are one file, not many.
        var path = WriteCoberturaXml("""
            <coverage><packages><package><classes>
              <class name="App.Foo" filename="Foo.cs" line-rate="1" branch-rate="1"><methods/></class>
              <class name="App.Foo/&lt;Run&gt;d__1" filename="Foo.cs" line-rate="1" branch-rate="1"><methods/></class>
            </classes></package></packages></coverage>
            """);

        // The state machine is folded into App.Foo, so this is one class with method "Run".
        var result = _sut.GetFileCoverage(path, "Foo.cs", 0.8);
        result.Classes.Should().ContainSingle().Which.Class.Should().Be("App.Foo");
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

    [Fact]
    public void GetUncoveredBranches_OmitsFullyCoveredMatches()
    {
        // Both methods match the partial name "Calc", but only CalcA has an uncovered branch.
        // The fully-covered match must be dropped from Methods while MatchCount still counts both.
        var path = WriteCoberturaXml(@"
<coverage>
  <packages><package><classes>
    <class name=""Foo"">
      <methods>
        <method name=""CalcA"">
          <lines>
            <line number=""10"" branch=""True"">
              <conditions><condition number=""1"" type=""jump"" coverage=""0%""/></conditions>
            </line>
          </lines>
        </method>
        <method name=""CalcB"">
          <lines>
            <line number=""20"" branch=""True"">
              <conditions><condition number=""1"" type=""jump"" coverage=""100%""/></conditions>
            </line>
          </lines>
        </method>
      </methods>
    </class>
  </classes></package></packages>
</coverage>");

        var result = _sut.GetUncoveredBranches(path, "Calc");

        result.MatchCount.Should().Be(2);
        result.Methods.Should().ContainSingle().Which.Method.Should().Be("CalcA");
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
    public void ComputeDiff_CountsUnchangedMethods()
    {
        var current = WriteCoberturaXml(@"
<coverage line-rate=""0.9"" branch-rate=""0.8"">
  <packages><package><classes>
    <class name=""Foo"">
      <methods>
        <method name=""Changed"" signature=""()"" line-rate=""0.9"" branch-rate=""0.8""/>
        <method name=""Same"" signature=""()"" line-rate=""1.0"" branch-rate=""1.0""/>
      </methods>
    </class>
  </classes></package></packages>
</coverage>", "curUnchanged.xml");

        var prev = WriteCoberturaXml(@"
<coverage line-rate=""0.5"" branch-rate=""0.4"">
  <packages><package><classes>
    <class name=""Foo"">
      <methods>
        <method name=""Changed"" signature=""()"" line-rate=""0.5"" branch-rate=""0.4""/>
        <method name=""Same"" signature=""()"" line-rate=""1.0"" branch-rate=""1.0""/>
      </methods>
    </class>
  </classes></package></packages>
</coverage>", "prevUnchanged.xml");

        var result = _sut.ComputeDiff(current, prev);

        result.ChangedMethods.Should().ContainSingle().Which.Name.Should().Be("Changed");
        result.UnchangedCount.Should().Be(1);
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

    private string WriteCoberturaXml(string xml, string? name = null)
    {
        name ??= $"cobertura-{Guid.NewGuid():N}.xml";
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, xml);
        return path;
    }
}
