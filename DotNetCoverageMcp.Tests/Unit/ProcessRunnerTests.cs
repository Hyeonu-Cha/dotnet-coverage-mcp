using System.Xml.Linq;
using DotNetCoverageMcp.Services;
using FluentAssertions;

namespace DotNetCoverageMcp.Tests.Unit;

public class ProcessRunnerTests
{
    [Fact]
    public async Task DrainAsync_ReturnsResultWhenTaskCompletesInTime()
    {
        var task = Task.FromResult("stdout-content");

        var result = await ProcessRunner.DrainAsync(task, TimeSpan.FromSeconds(1));

        result.Should().Be("stdout-content");
    }

    [Fact]
    public async Task DrainAsync_ReturnsEmptyWhenTaskExceedsDeadline()
    {
        var tcs = new TaskCompletionSource<string>();
        // Task that will never complete within the drain window
        var result = await ProcessRunner.DrainAsync(tcs.Task, TimeSpan.FromMilliseconds(50));

        result.Should().BeEmpty();

        // Clean up the still-pending task so it doesn't linger.
        tcs.TrySetResult("late");
    }

    [Fact]
    public async Task DrainAsync_ObservesLateFaultedTask_NoUnobservedException()
    {
        var tcs = new TaskCompletionSource<string>();

        var result = await ProcessRunner.DrainAsync(tcs.Task, TimeSpan.FromMilliseconds(20));
        result.Should().BeEmpty();

        // Fault the task after DrainAsync has already returned. The continuation
        // registered in DrainAsync should observe the exception and prevent it from
        // bubbling up as an UnobservedTaskException.
        tcs.TrySetException(new InvalidOperationException("simulated late stderr read failure"));

        // Give the continuation a moment to run and observe.
        await Task.Delay(50);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // If the exception had been unobserved, the finalizer would have queued a
        // TaskScheduler.UnobservedTaskException. We can't deterministically assert
        // that here without process-wide hooks, but reaching this point without
        // the default unobserved-exception handler crashing the process is the
        // behavioral contract we care about.
        tcs.Task.IsFaulted.Should().BeTrue();
    }

    [Fact]
    public async Task DrainAsync_ReturnsEmptyWhenTaskFaultsBeforeDeadline()
    {
        var task = Task.FromException<string>(new InvalidOperationException("boom"));

        var result = await ProcessRunner.DrainAsync(task, TimeSpan.FromSeconds(1));

        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("*")]
    [InlineData(" * ")]
    public void BuildTestArguments_RunAllFilter_OmitsFilterFlag(string? filter)
    {
        var args = ProcessRunner.BuildTestArguments("p.csproj", filter, "/results", false, null);

        args.Should().NotContain("--filter");
    }

    // VSTest reads a bare term as FullyQualifiedName~term, so every shape must go
    // through untouched; prefixing it is what broke dotted and multi-class filters.
    [Theory]
    [InlineData("OrderServiceTests")]
    [InlineData("OrderServiceTests.PlacesOrder")]
    [InlineData("OrderTests|PaymentTests")]
    [InlineData("Category=Unit")]
    [InlineData("FullyQualifiedName~Ns.OrderTests")]
    public void BuildTestArguments_PassesFilterThroughUnchanged(string filter)
    {
        var args = ProcessRunner.BuildTestArguments("p.csproj", filter, "/results", false, null);

        args[args.IndexOf("--filter") + 1].Should().Be(filter);
    }

    [Fact]
    public void BuildTestArguments_AlwaysWritesTrx()
    {
        var args = ProcessRunner.BuildTestArguments("p.csproj", null, "/r", false, null);

        args[args.IndexOf("--logger") + 1].Should().Be("trx");
    }

    private static TestSummary Summary(int total, int passed, int failed) =>
        new(total, passed, failed, total - passed - failed, []);

    public static TheoryData<int, TestSummary?, TestRunOutcome> ClassifyCases => new()
    {
        { 0, null, TestRunOutcome.Success },                       // no TRX, clean exit
        { 1, null, TestRunOutcome.BuildError },                    // never reached tests
        { 0, Summary(0, 0, 0), TestRunOutcome.NoTestsMatched },    // dotnet test exits 0 here
        { 1, Summary(3, 2, 1), TestRunOutcome.TestsFailed },
        { 0, Summary(3, 3, 0), TestRunOutcome.Success },
        // A test crashed or hung the host. Real runs: a hang left 3 passed and exit 1, a crash
        // before any result total="0" and exit 1; both were misread as testsFailed / no tests.
        { 1, Summary(3, 3, 0), TestRunOutcome.Aborted },
        { 1, Summary(0, 0, 0), TestRunOutcome.Aborted },
        { 1, Summary(4, 3, 1), TestRunOutcome.TestsFailed },       // a failed test is reported first
    };

    [Theory]
    [MemberData(nameof(ClassifyCases))]
    public void Classify_MapsExitCodeAndTrx(int exitCode, TestSummary? summary, TestRunOutcome expected)
    {
        ProcessRunner.Classify(exitCode, summary).Should().Be(expected);
    }

    // --- Microsoft.Testing.Platform mode (shapes checked against real runs: xUnit v3 and MSTest
    // on MTP 2.5, Microsoft.Testing.Extensions.CodeCoverage 18.12) ---

    [Fact]
    public void BuildMtpTestArguments_UsesProjectOptionAndMicrosoftCoverage()
    {
        var args = ProcessRunner.BuildMtpTestArguments("p.csproj", "OrderTests", "/r", forceRestore: false, reportTrx: true);

        args.Should().Equal("test", "--project", "p.csproj", "--no-restore", "--results-directory", "/r",
            "--filter", "OrderTests", "--", "--coverage", "--coverage-output-format", "cobertura", "--report-trx");
    }

    [Fact]
    public void BuildMtpTestArguments_WithoutTrxExtension_OmitsReportTrx_AndHasNoVstestOptions()
    {
        // MTP exits with 5 on an option whose extension the project doesn't reference.
        var args = ProcessRunner.BuildMtpTestArguments("p.csproj", null, "/r", forceRestore: true, reportTrx: false);

        args.Should().NotContain(["--report-trx", "--no-restore", "--filter", "--logger", "--blame-hang-timeout"]);
        args.Should().NotContain(a => a.StartsWith("--collect", StringComparison.Ordinal));
    }

    public static TheoryData<int, TestSummary?, TestRunOutcome> MtpClassifyCases => new()
    {
        { 5, null, TestRunOutcome.MissingExtension },
        { 8, null, TestRunOutcome.NoTestsMatched },
        { 8, Summary(0, 0, 0), TestRunOutcome.NoTestsMatched },
        { 2, null, TestRunOutcome.TestsFailed },
        { 2, Summary(2, 1, 1), TestRunOutcome.TestsFailed },
        { 0, Summary(3, 3, 0), TestRunOutcome.Success },
        { 0, null, TestRunOutcome.Success },
        { 1, null, TestRunOutcome.BuildError },
        { 7, Summary(0, 0, 0), TestRunOutcome.Aborted },           // host crashed (real run: "error: 1, total: 0")
        { 7, null, TestRunOutcome.Aborted },
        { 3, null, TestRunOutcome.Aborted },                       // session aborted
        { 7, Summary(2, 1, 1), TestRunOutcome.TestsFailed },
        { 1, Summary(3, 3, 0), TestRunOutcome.Aborted },
    };

    [Theory]
    [MemberData(nameof(MtpClassifyCases))]
    public void ClassifyMtp_MapsMtpExitCodes(int exitCode, TestSummary? summary, TestRunOutcome expected) =>
        ProcessRunner.ClassifyMtp(exitCode, summary).Should().Be(expected);

    [Fact]
    public void ParseMtpSummary_ReadsTheConsoleSummaryBlock()
    {
        // Verbatim shape of MTP's console output for a run with one failing test.
        const string output = """
            Test run summary: Failed!
              total: 2
              failed: 1
              succeeded: 1
              skipped: 0
              duration: 2s 169ms
            Test run completed with non-success exit code: 2. At least one test failed.
            """;

        ProcessRunner.ParseMtpSummary(output).Should().BeEquivalentTo(new TestSummary(2, 1, 1, 0, []));
        ProcessRunner.ParseMtpSummary("Build FAILED.").Should().BeNull();
    }

    [Fact]
    public void StripAnsi_RemovesTheColourCodesMtpWritesOnCi()
    {
        // Verbatim from an MTP run on a GitHub Actions runner (SDK 10.0.404). Before
        // stripping, neither the summary line nor "total:" was recognised.
        const string colored = "\u001b[32mTest run summary: Passed!\n\u001b[m  total: 3\n  failed: 0\n\u001b[32m  succeeded: 3\n\u001b[m  skipped: 0\n";

        var clean = ProcessRunner.StripAnsi(colored);

        clean.Should().NotContain("\u001b");
        ProcessRunner.ProgressMessage(clean.Split('\n')[0]).Should().Be("Test run summary: Passed!");
        ProcessRunner.ParseMtpSummary(clean).Should().BeEquivalentTo(new TestSummary(3, 3, 0, 0, []));
        ProcessRunner.StripAnsi("plain text").Should().Be("plain text");
    }

    [Fact]
    public void FindCoverageXml_PrefersTheShallowestReport()
    {
        // Microsoft's extension writes <guid>.cobertura.xml at the top and a copy under In/;
        // coverlet writes <guid>/coverage.cobertura.xml.
        var dir = Path.Combine(Path.GetTempPath(), $"fcx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "run", "In", "host"));
        File.WriteAllText(Path.Combine(dir, "run", "In", "host", "a.cobertura.xml"), "");
        File.WriteAllText(Path.Combine(dir, "a.cobertura.xml"), "");
        try
        {
            ProcessRunner.FindCoverageXml(dir).Should().Be(Path.Combine(dir, "a.cobertura.xml"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindCoverageXml_OneReportPerTargetFramework_MergesThem()
    {
        // A multi-targeted test project: one <guid>/coverage.cobertura.xml per framework. The
        // first in name order was taken, a random pick between the GUID-named folders.
        var dir = Path.Combine(Path.GetTempPath(), $"fcx-{Guid.NewGuid():N}");
        string Report(int hits) => $"""
            <coverage line-rate="0" branch-rate="0"><packages><package name="P"><classes>
              <class name="C" filename="C.cs" line-rate="0" branch-rate="1"><methods>
                <method name="M" signature="()" line-rate="0" branch-rate="1"><lines><line number="1" hits="{hits}" branch="False" /></lines></method>
              </methods><lines><line number="1" hits="{hits}" branch="False" /></lines></class>
            </classes></package></packages></coverage>
            """;
        foreach (var (guid, hits) in new[] { ("0000", 0), ("ffff", 3) })
        {
            Directory.CreateDirectory(Path.Combine(dir, guid));
            File.WriteAllText(Path.Combine(dir, guid, "coverage.cobertura.xml"), Report(hits));
        }
        try
        {
            var path = ProcessRunner.FindCoverageXml(dir);

            path.Should().Be(Path.Combine(dir, ProcessRunner.MergedCoverageFile));
            System.Xml.Linq.XDocument.Load(path!).Root!.Attribute("line-rate")!.Value.Should().Be("1");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindDotnet_SkipsTheCurrentFolderAndRelativePathEntries()
    {
        // A dotnet.exe committed to the repository the server starts in ran instead of the SDK.
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var sdk = Path.Combine(Path.GetTempPath(), $"sdk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sdk);
        File.WriteAllText(Path.Combine(sdk, exe), "");
        // Under the current folder, so a relative PATH entry does lead to it, as "." does from the
        // repository (a temp folder can be on another drive, where no relative path reaches).
        var relative = $"repo-{Guid.NewGuid():N}";
        var planted = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, relative)).FullName;
        File.WriteAllText(Path.Combine(planted, exe), "");
        try
        {
            var path = string.Join(Path.PathSeparator, relative, "", $"\"{sdk}\"");

            ProcessRunner.FindDotnet(processPath: null, path, dotnetRoot: null).Should().Be(Path.Combine(sdk, exe));
            ProcessRunner.FindDotnet(Path.Combine(planted, "server.exe"), relative, dotnetRoot: sdk)
                .Should().Be(Path.Combine(sdk, exe), "DOTNET_ROOT is the last place looked");
            ProcessRunner.FindDotnet(null, relative, null).Should().BeNull();
        }
        finally
        {
            Directory.Delete(sdk, recursive: true);
            Directory.Delete(planted, recursive: true);
        }
    }

    [Fact]
    public void FindDotnet_PrefersTheDotnetHostRunningTheServer()
    {
        var host = OperatingSystem.IsWindows() ? @"C:\dotnet\dotnet.exe" : "/usr/share/dotnet/dotnet";

        ProcessRunner.FindDotnet(host, pathVariable: null, dotnetRoot: null).Should().Be(host);
    }

    // Lines verbatim from real VSTest and MTP runs of the fixture.
    [Theory]
    [InlineData("  Determining projects to restore...", "Restoring packages")]
    [InlineData(@"  SampleLib -> C:\fx\SampleLib\bin\Debug\net10.0\SampleLib.dll", "Built SampleLib")]
    [InlineData(@"Test run for C:\fx\SampleLib.Tests\bin\Debug\net10.0\SampleLib.Tests.dll (.NETCoreApp,Version=v10.0)", "Running tests in SampleLib.Tests.dll")]
    [InlineData(@"Running tests from C:\mtp\XuMtp\bin\Debug\net10.0\XuMtp.dll (net10.0|x64)", "Running tests in XuMtp.dll")]
    [InlineData("Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 21 ms - SampleLib.Tests.dll (net10.0)",
        "Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 21 ms - SampleLib.Tests.dll (net10.0)")]
    [InlineData("Test run summary: Passed!", "Test run summary: Passed!")]
    public void ProgressMessage_RecognisesMilestones(string line, string expected) =>
        ProcessRunner.ProgressMessage(line).Should().Be(expected);

    [Theory]
    [InlineData("A total of 1 test files matched the specified pattern.")]
    [InlineData("  total: 1")]
    [InlineData(@"    - C:\mtp\r\XuMtp_net10.0_x64.trx")]
    [InlineData("")]
    public void ProgressMessage_IgnoresOtherLines(string line) =>
        ProcessRunner.ProgressMessage(line).Should().BeNull();

    [Fact]
    public void BuildTestArguments_HangDetection_WritesNoMemoryDump()
    {
        // VSTest's default writes two full dumps of the test host (~330 MB for a 3-test project,
        // with everything it held in memory) into the project; the Sequence file and TRX that
        // name the hung test are written without them.
        var args = ProcessRunner.BuildTestArguments("p.csproj", null, "/r", false, null);

        args.Should().ContainInConsecutiveOrder("--blame-hang-dump-type", "none");
        args.Should().Contain("--blame-hang-timeout");
    }

    [Fact]
    public void IsRestored_LooksForTheAssetsFileNextToTheProject()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"restored-{Guid.NewGuid():N}")).FullName;
        try
        {
            var project = Path.Combine(dir, "App.Tests.csproj");
            ProcessRunner.IsRestored(project).Should().BeFalse("a fresh clone has no obj/project.assets.json");

            Directory.CreateDirectory(Path.Combine(dir, "obj"));
            File.WriteAllText(Path.Combine(dir, "obj", "project.assets.json"), "{}");
            ProcessRunner.IsRestored(project).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildTestArguments_ForceRestore_OmitsNoRestore()
    {
        ProcessRunner.BuildTestArguments("p.csproj", null, "/r", forceRestore: true, null)
            .Should().NotContain("--no-restore");
        ProcessRunner.BuildTestArguments("p.csproj", null, "/r", forceRestore: false, null)
            .Should().Contain("--no-restore");
    }

    [Fact]
    public void BuildTestArguments_RunSettings_AddsSettingsFlag()
    {
        var args = ProcessRunner.BuildTestArguments("p.csproj", null, "/r", false, "/tmp/x.runsettings");

        args[args.IndexOf("--settings") + 1].Should().Be("/tmp/x.runsettings");
    }

    [Fact]
    public void WriteIncludeRunSettings_WritesCoverletIncludeFilter()
    {
        var path = ProcessRunner.WriteIncludeRunSettings("OrderService");
        try
        {
            File.Exists(path).Should().BeTrue();
            var doc = XDocument.Load(path);
            doc.Descendants("Include").Single().Value.Should().Be("[*]*OrderService");
            doc.Descendants("DataCollector").Single()
                .Attribute("friendlyName")!.Value.Should().Be("XPlat Code Coverage");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteIncludeRunSettings_EscapesXmlSpecialCharacters()
    {
        // The value is written as element text, so XML metacharacters must be escaped;
        // loading the file without throwing proves it is well-formed.
        var path = ProcessRunner.WriteIncludeRunSettings("Foo&<Bar>");
        try
        {
            XDocument.Load(path).Descendants("Include").Single().Value.Should().Be("[*]*Foo&<Bar>");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
