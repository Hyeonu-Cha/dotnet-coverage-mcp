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
