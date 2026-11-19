using DotNetCoverageMcp.Services;
using FluentAssertions;

namespace DotNetCoverageMcp.Tests.Unit;

public class TrxReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"trx-tests-{Guid.NewGuid():N}");

    public TrxReaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Shape taken from a real `dotnet test --logger trx` run (xUnit, .NET 10).
    private string WriteTrx(string counters, string results = "")
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.trx");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>{results}</Results>
              <ResultSummary outcome="Completed"><Counters {counters} /></ResultSummary>
            </TestRun>
            """);
        return path;
    }

    private static string FailedResult(string name, string message, string stack) => $"""
        <UnitTestResult testName="{name}" outcome="Failed">
          <Output><ErrorInfo><Message>{message}</Message><StackTrace>{stack}</StackTrace></ErrorInfo></Output>
        </UnitTestResult>
        """;

    [Fact]
    public void Read_NoFiles_ReturnsNull()
    {
        TrxReader.Read([]).Should().BeNull();
    }

    [Fact]
    public void Read_CountsAndDerivesSkipped()
    {
        // xUnit skips appear only as total - passed - failed; notExecuted stays 0.
        var path = WriteTrx("""total="3" executed="2" passed="2" failed="0" notExecuted="0" """);

        var s = TrxReader.Read([path])!;

        s.Should().BeEquivalentTo(new TestSummary(3, 2, 0, 1, []));
    }

    [Fact]
    public void Read_SumsAcrossFiles()
    {
        var a = WriteTrx("""total="2" passed="2" failed="0" """);
        var b = WriteTrx("""total="3" passed="1" failed="2" """);

        var s = TrxReader.Read([a, b])!;

        (s.Total, s.Passed, s.Failed).Should().Be((5, 3, 2));
    }

    [Fact]
    public void Read_ExtractsFailureNameMessageAndUserFrames()
    {
        var path = WriteTrx("""total="1" passed="0" failed="1" """, FailedResult(
            "Ns.CalcTests.Add_Fails",
            "Assert.Equal() Failure: Values differ&#xD;\nExpected: 1&#xD;\nActual:   2",
            "   at Ns.CalcTests.Add_Fails() in C:\\src\\CalcTests.cs:line 7&#xD;\n" +
            "   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)&#xD;\n" +
            "   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)"));

        var failure = TrxReader.Read([path])!.Failures.Single();

        failure.Test.Should().Be("Ns.CalcTests.Add_Fails");
        failure.Message.Should().Be("Assert.Equal() Failure: Values differ\nExpected: 1\nActual:   2");
        failure.Stack.Should().Be("at Ns.CalcTests.Add_Fails() in C:\\src\\CalcTests.cs:line 7");
    }

    [Fact]
    public void Read_CapsFailuresAndTruncatesLongMessages()
    {
        var results = string.Concat(Enumerable.Range(0, 15)
            .Select(i => FailedResult($"T{i}", new string('x', 2000), "   at Ns.T.M()")));
        var path = WriteTrx("""total="15" passed="0" failed="15" """, results);

        var s = TrxReader.Read([path])!;

        s.Failed.Should().Be(15, "the count stays exact even when the list is capped");
        s.Failures.Should().HaveCount(TrxReader.MaxFailures);
        s.Failures[0].Message.Length.Should().BeLessThan(600);
    }

    [Fact]
    public void TrimStack_OnlyFrameworkFrames_KeepsFirstFrame()
    {
        TrxReader.TrimStack("   at Xunit.Assert.Equal()\n   at System.Foo.Bar()")
            .Should().Be("at Xunit.Assert.Equal()");
    }

    [Fact]
    public void TrimStack_KeepsAtMostThreeUserFrames()
    {
        var stack = string.Join("\n", Enumerable.Range(0, 6).Select(i => $"   at My.Code.M{i}()"));

        TrxReader.TrimStack(stack)!.Split('\n').Should().HaveCount(3);
    }

    // --- Runs that stopped before they finished. Texts taken from real `dotnet test` runs
    // (.NET 10, xUnit) of a test calling Environment.FailFast, one that never returns, and one
    // recursing without end; the MTP one from xUnit v3 on Microsoft.Testing.Platform. ---

    private const string CrashHeader =
        @"The active test run was aborted. Reason: Test host process crashed : Process path: C:\w\SampleLib.Tests\bin\Debug\net10.0\testhost.exe";

    private string WriteAbortTrx(params (string Outcome, string Text)[] runInfos)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.trx");
        var infos = string.Concat(runInfos.Select(r =>
            $"<RunInfo computerName=\"m\" outcome=\"{r.Outcome}\" timestamp=\"2026-11-19T16:07:58\"><Text>{System.Security.SecurityElement.Escape(r.Text)}</Text></RunInfo>"));
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Failed"><Counters total="0" executed="0" passed="0" failed="0" /><RunInfos>{infos}</RunInfos></ResultSummary>
            </TestRun>
            """);
        return path;
    }

    // The Blame collector's Sequence file: tests in the order they started.
    private string WriteSequence(params (string Name, bool Completed)[] tests)
    {
        var path = Path.Combine(_dir, $"Sequence_{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, "<?xml version=\"1.0\"?><TestSequence>" + string.Concat(tests.Select(t =>
            $"<Test Name=\"{t.Name}\" DisplayName=\"{t.Name}\" Source=\"x.dll\" Completed=\"{(t.Completed ? "True" : "False")}\" />")) + "</TestSequence>");
        return path;
    }

    [Fact]
    public void ReadAbort_Crash_GivesTheReasonWithoutThePath_TheCrashAndTheUnfinishedTests()
    {
        var trx = WriteAbortTrx(("Error", CrashHeader + """

            Process terminated.
            simulated crash in a test
               at System.Environment.FailFast(System.String)
               at CrashTests.CrashesTheTestHost()
               at System.Reflection.MethodBase.Invoke(System.Object, System.Object[])
               at Xunit.Sdk.TestInvoker`1.CallTestMethod(System.Object)
            """));
        // Blame writes the same Sequence file twice (the results folder and the TRX's In folder).
        (string, bool)[] tests = [("CrashTests.CrashesTheTestHost", false), ("SampleLib.Tests.CalculatorTests.Add_ReturnsSum", true)];

        var abort = TrxReader.ReadAbort([trx], [WriteSequence(tests), WriteSequence(tests)], consoleOutput: null);

        abort.Reasons.Should().Equal("The active test run was aborted. Reason: Test host process crashed");
        abort.Crash.Should().Be("Process terminated.\nsimulated crash in a test\nat CrashTests.CrashesTheTestHost()");
        abort.UnfinishedTests.Should().Equal("CrashTests.CrashesTheTestHost");
    }

    [Fact]
    public void ReadAbort_Hang_GivesTheBlameNoticeAndTheTestThatNeverFinished()
    {
        var trx = WriteAbortTrx(
            ("Warning", "Data collector 'Blame' message: The specified inactivity time of 5 seconds has elapsed. Collecting hang dumps from testhost and its child processes."),
            ("Error", CrashHeader));

        var abort = TrxReader.ReadAbort([trx], [WriteSequence(("SampleLib.Tests.CalculatorTests.Add_ReturnsSum", true),
            ("CrashTests.HangsForever", false))], consoleOutput: null);

        abort.Reasons.Should().HaveCount(2);
        abort.Reasons[0].Should().Contain("Test host process crashed", "errors come before warnings");
        abort.Reasons[1].Should().Contain("inactivity time of 5 seconds has elapsed");
        abort.Crash.Should().BeNull("a hung host is killed without a stack");
        abort.UnfinishedTests.Should().Equal("CrashTests.HangsForever");
    }

    [Fact]
    public void CrashDetail_StackOverflow_KeepsTheRepeatNoticeAndTheUserFrames()
    {
        const string text = """
            Stack overflow.
            Repeated 24057 times:
            --------------------------------
               at CrashTests.Recurse(Int32)
            --------------------------------
               at CrashTests.OverflowsTheStack()
               at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(System.Object, IntPtr*)
               at Xunit.Sdk.XunitTestRunner.InvokeTestMethodAsync(Xunit.Sdk.ExceptionAggregator)
            """;

        TrxReader.CrashDetail(text).Should().Be(
            "Stack overflow.\nRepeated 24057 times:\nat CrashTests.Recurse(Int32)\nat CrashTests.OverflowsTheStack()");
    }

    [Fact]
    public void ReadAbort_Mtp_TakesTheCrashFromTheConsole()
    {
        // Microsoft.Testing.Platform's TRX only says the host crashed; the message is on the console.
        var trx = WriteAbortTrx(("Error", "Test host process pid: 48448 crashed."));
        const string console = """
            Running tests from C:\w\SampleLib.Tests\bin\Debug\net10.0\SampleLib.Tests.dll
            Process terminated.
            simulated crash in a test
               at System.Environment.FailFast(System.String)
               at CrashTests.CrashesTheTestHost()
               at Xunit.v3.XunitTestRunnerBaseContext`1.InvokeTest(System.Object)
            Test host crashed and no test results could be recovered from the TRX streaming sidecar; the TRX is empty.
            Test run summary: Failed!
              error: 1
              total: 0
            """;

        var abort = TrxReader.ReadAbort([trx], [], console);

        abort.Reasons.Should().Equal("Test host process pid: 48448 crashed.");
        abort.Crash.Should().Be("Process terminated.\nsimulated crash in a test\nat CrashTests.CrashesTheTestHost()");
        abort.UnfinishedTests.Should().BeEmpty();
    }

    [Fact]
    public void Read_TruncatedTrx_IsSkipped_InsteadOfThrowing()
    {
        // A test host killed mid-write can leave a cut-off TRX; it used to escape as an
        // XmlException and the agent got the SDK's generic error.
        var good = WriteTrx("""total="2" executed="2" passed="2" failed="0" """);
        var truncated = Path.Combine(_dir, "cut.trx");
        File.WriteAllText(truncated, "<?xml version=\"1.0\"?><TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results><UnitTestRes");

        TrxReader.Read([truncated]).Should().BeNull("nothing readable is the same as no TRX");
        TrxReader.Read([good, truncated])!.Passed.Should().Be(2);
        TrxReader.ReadAbort([truncated], [], null).Reasons.Should().BeEmpty();
    }

    [Fact]
    public void ReadAbort_UnreadableSequenceFile_IsSkipped()
    {
        var broken = Path.Combine(_dir, "Sequence_broken.xml");
        File.WriteAllText(broken, "<TestSequence><Test Name=");

        var abort = TrxReader.ReadAbort([], [broken], consoleOutput: "no frames here");

        abort.Reasons.Should().BeEmpty();
        abort.UnfinishedTests.Should().BeEmpty();
        abort.Crash.Should().BeNull();
    }
}
