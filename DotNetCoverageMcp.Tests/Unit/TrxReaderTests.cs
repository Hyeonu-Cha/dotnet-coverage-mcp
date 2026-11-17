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
}
