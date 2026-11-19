using System.Xml.Linq;

namespace DotNetCoverageMcp.Services;

public record TestSummary(int Total, int Passed, int Failed, int Skipped, List<TestFailure> Failures);
public record TestFailure(string Test, string Message, string? Stack);
/// <summary>Why a run stopped before it finished; see <see cref="TrxReader.ReadAbort"/>.</summary>
public record TestRunAbort(List<string> Reasons, List<string> UnfinishedTests, string? Crash);

/// <summary>
/// Reads the TRX files written by `dotnet test --logger trx` into counts plus a short
/// description of each failure, so a run can be classified without matching console
/// text (which is localized) and the agent sees the failure instead of a log tail.
/// </summary>
public static class TrxReader
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    // Enough to act on without flooding the response when a change breaks many tests.
    internal const int MaxFailures = 10;
    private const int MaxMessageChars = 500;
    private const int MaxStackFrames = 3;

    // Frames from the runtime and test/assertion/mocking libraries say nothing about where
    // the test went wrong; the useful frames are the ones in the user's code.
    private static readonly string[] FrameworkFramePrefixes =
    [
        "System.", "Microsoft.", "Xunit.", "NUnit.", "FluentAssertions.", "Shouldly.",
        "Moq.", "NSubstitute.", "FakeItEasy.", "Castle."
    ];

    /// <summary>Returns null when there are no readable TRX files, e.g. the build failed.</summary>
    public static TestSummary? Read(IReadOnlyCollection<string> trxPaths)
    {
        var docs = trxPaths.Select(TryLoad).OfType<XDocument>().ToList();
        if (docs.Count == 0) return null;

        int total = 0, passed = 0, failed = 0;
        var failures = new List<TestFailure>();

        foreach (var doc in docs)
        {
            var counters = doc.Descendants(Ns + "Counters").FirstOrDefault();
            total += Count(counters, "total");
            passed += Count(counters, "passed");
            failed += Count(counters, "failed");

            foreach (var result in doc.Descendants(Ns + "UnitTestResult")
                         .Where(r => (string?)r.Attribute("outcome") == "Failed"))
            {
                if (failures.Count >= MaxFailures) break;
                var error = result.Element(Ns + "Output")?.Element(Ns + "ErrorInfo");
                failures.Add(new TestFailure(
                    (string?)result.Attribute("testName") ?? "",
                    Truncate(Normalize(error?.Element(Ns + "Message")?.Value)),
                    TrimStack(error?.Element(Ns + "StackTrace")?.Value)));
            }
        }

        // "total" counts everything discovered; skipped tests are neither passed nor failed.
        return new TestSummary(total, passed, failed, Math.Max(0, total - passed - failed), failures);
    }

    internal static string? TrimStack(string? stack)
    {
        if (string.IsNullOrWhiteSpace(stack)) return null;

        var frames = Normalize(stack).Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("at ", StringComparison.Ordinal))
            .ToList();
        var userFrames = frames
            .Where(f => !FrameworkFramePrefixes.Any(p => f.AsSpan(3).StartsWith(p, StringComparison.Ordinal)))
            .Take(MaxStackFrames)
            .ToList();

        // A failure thrown entirely inside framework code still deserves one frame.
        if (userFrames.Count == 0) userFrames = frames.Take(1).ToList();
        return userFrames.Count == 0 ? null : string.Join("\n", userFrames);
    }

    private const int MaxAbortReasons = 3;
    private const int MaxUnfinishedTests = 5;

    /// <summary>
    /// Why a run stopped before it finished, from what dotnet test leaves behind when a test
    /// crashes or hangs the test host. Nothing here depends on the console's language:
    /// <list type="bullet">
    /// <item>Reasons: the TRX run messages, errors first. VSTest writes "The active test run was
    /// aborted. Reason: Test host process crashed" and, for a hang, the Blame collector's
    /// inactivity notice; Microsoft.Testing.Platform writes "Test host process pid: N crashed."</item>
    /// <item>Unfinished tests: the Blame collector's Sequence files (VSTest) list the tests in the
    /// order they started; those still Completed="False" had not finished when the host died.
    /// They are candidates, not a verdict: tests running in parallel are listed too, and a host
    /// that dies at once (a stack overflow) may die before the culprit's start is recorded.
    /// VSTest's own "test running when the crash occurred" has the same blind spot.</item>
    /// <item>Crash: the crash's message and the user's frames, which VSTest puts in the TRX run
    /// message and Microsoft.Testing.Platform prints on the console.</item>
    /// </list>
    /// </summary>
    public static TestRunAbort ReadAbort(IEnumerable<string> trxPaths, IEnumerable<string> sequencePaths, string? consoleOutput)
    {
        var runInfos = trxPaths
            .Select(TryLoad).OfType<XDocument>()
            .SelectMany(d => d.Descendants(Ns + "RunInfo"))
            .Select(r => (Error: (string?)r.Attribute("outcome") == "Error", Text: Normalize(r.Element(Ns + "Text")?.Value)))
            .Where(r => r.Text.Length > 0)
            .OrderByDescending(r => r.Error)
            .ToList();

        var reasons = runInfos.Select(r => Reason(r.Text)).Distinct().Take(MaxAbortReasons).ToList();
        // The first line of a VSTest run message is the reason itself, reported above.
        var crash = runInfos.Where(r => r.Error).Select(r => CrashDetail(SkipFirstLine(r.Text))).FirstOrDefault(c => c != null)
            ?? CrashDetail(consoleOutput);
        var unfinished = sequencePaths.SelectMany(UnfinishedTests).Distinct().Take(MaxUnfinishedTests).ToList();
        return new TestRunAbort(reasons, unfinished, crash);
    }

    // "…Reason: Test host process crashed : Process path: C:\…\testhost.exe" → the reason alone:
    // the path is long and says nothing the agent can act on.
    private static string Reason(string text)
    {
        var line = text.Split('\n')[0].Trim();
        var path = line.IndexOf(" Process path:", StringComparison.Ordinal);
        return Truncate(path > 0 ? line[..path].TrimEnd(' ', ':') : line);
    }

    private static string SkipFirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return newline < 0 ? "" : text[(newline + 1)..];
    }

    private static IEnumerable<string> UnfinishedTests(string sequencePath)
    {
        try
        {
            return XDocument.Load(sequencePath).Descendants("Test")
                .Where(t => (string?)t.Attribute("Completed") == "False")
                .Select(t => (string?)t.Attribute("Name") ?? "")
                .Where(n => n.Length > 0)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [];
        }
    }

    /// <summary>
    /// The message and the user's frames of a crash: the last block of stack frames and the two
    /// lines before it. That is "Process terminated." and the message for Environment.FailFast,
    /// "Stack overflow." and "Repeated N times:" for a stack overflow, the exception for an
    /// unhandled one. The dashed lines a stack overflow puts between frames belong to the block.
    /// Null when the text has no stack frames.
    /// </summary>
    internal static string? CrashDetail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = Normalize(text).Split('\n').Select(l => l.Trim()).ToList();
        static bool InFrames(string l) => l.StartsWith("at ", StringComparison.Ordinal) || (l.Length >= 3 && l.All(c => c == '-'));

        var end = lines.FindLastIndex(l => l.StartsWith("at ", StringComparison.Ordinal));
        if (end < 0) return null;
        var start = end;
        while (start > 0 && InFrames(lines[start - 1])) start--;

        var message = lines.Take(start).Where(l => l.Length > 0).TakeLast(2);
        var frames = TrimStack(string.Join("\n", lines.Skip(start).Take(end - start + 1)));
        return Truncate(string.Join("\n", frames == null ? message : message.Append(frames)));
    }

    // A TRX can be cut short when the test host is killed while writing it. Skip it rather than
    // let the XmlException fail the whole tool call with the SDK's generic error.
    private static XDocument? TryLoad(string path)
    {
        try { return XDocument.Load(path); }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static int Count(XElement? counters, string name) =>
        int.TryParse((string?)counters?.Attribute(name), out var n) ? n : 0;

    private static string Normalize(string? s) => (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static string Truncate(string s) =>
        s.Length <= MaxMessageChars ? s : s[..MaxMessageChars] + "…";
}
