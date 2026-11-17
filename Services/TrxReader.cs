using System.Xml.Linq;

namespace DotNetCoverageMcp.Services;

public record TestSummary(int Total, int Passed, int Failed, int Skipped, List<TestFailure> Failures);
public record TestFailure(string Test, string Message, string? Stack);

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

    /// <summary>Returns null when there are no TRX files, e.g. the build failed.</summary>
    public static TestSummary? Read(IReadOnlyCollection<string> trxPaths)
    {
        if (trxPaths.Count == 0) return null;

        int total = 0, passed = 0, failed = 0;
        var failures = new List<TestFailure>();

        foreach (var path in trxPaths)
        {
            var doc = XDocument.Load(path);
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

    private static int Count(XElement? counters, string name) =>
        int.TryParse((string?)counters?.Attribute(name), out var n) ? n : 0;

    private static string Normalize(string? s) => (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static string Truncate(string s) =>
        s.Length <= MaxMessageChars ? s : s[..MaxMessageChars] + "…";
}
