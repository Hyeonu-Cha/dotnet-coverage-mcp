using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ModelContextProtocol.Server;

[McpServerToolType]
public class CoverageTools
{
    [McpServerTool]
    public string RunTestsWithCoverage(
        string testProjectPath,
        string filter,
        string? workingDir = null)
    {
        workingDir ??= Path.GetDirectoryName(testProjectPath) ?? Directory.GetCurrentDirectory();
        var resultsDir = Path.Combine(workingDir, "TestResults");
        var reportDir = Path.Combine(workingDir, "coveragereport");

        SafeDelete(resultsDir);
        SafeDelete(reportDir);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            // If filter has no dots, the whole string is used; suffix stripping narrows coverage to the target class.
            Arguments = $"test \"{testProjectPath}\" --filter \"FullyQualifiedName~{filter}\" --collect:\"XPlat Code Coverage\" --results-directory \"{resultsDir}\" /p:Include=\"[*]*{StripTestSuffix(filter.Split('.').Last())}\"",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi) ?? throw new Exception("Failed to start dotnet test");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            return $"Test run failed (code {process.ExitCode}). Error: {error}\nOutput: {output}";

        var xmlPaths = Directory.GetFiles(resultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories);
        if (xmlPaths.Length == 0) return "No coverage XML found.\n" + output;

        var xmlPath = xmlPaths[0];

        var reportPsi = new ProcessStartInfo
        {
            FileName = "reportgenerator",
            Arguments = $"-reports:\"{xmlPath}\" -targetdir:\"{reportDir}\" -reporttypes:JsonSummary",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        using var rp = Process.Start(reportPsi) ?? throw new Exception("Failed to start reportgenerator");
        rp.WaitForExit();

        var summaryPath = Path.Combine(reportDir, "Summary.json");

        // UPDATED: Now returns both the Summary JSON and the Cobertura XML paths
        return File.Exists(summaryPath)
            ? $"Tests completed.\nCoverage JSON at: {summaryPath}\nCobertura XML at: {xmlPath}\nOutput: {output}"
            : "Report generation failed.\n" + output;
    }

    [McpServerTool]
    public string GetCoverageSummary(string summaryJsonPath)
    {
        if (!File.Exists(summaryJsonPath)) return $"Summary.json not found: {summaryJsonPath}";

        var json = File.ReadAllText(summaryJsonPath);
        var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool]
    public string GetUncoveredBranches(string coberturaXmlPath, string methodName)
    {
        if (!File.Exists(coberturaXmlPath))
        {
            var dir = Directory.Exists(coberturaXmlPath)
                ? coberturaXmlPath
                : Path.GetDirectoryName(coberturaXmlPath) ?? Directory.GetCurrentDirectory();
            var found = Directory.GetFiles(dir, "coverage.cobertura.xml", SearchOption.AllDirectories).FirstOrDefault();
            if (found == null) return $"Cobertura XML not found at: {coberturaXmlPath}";
            coberturaXmlPath = found;
        }

        var doc = XDocument.Load(coberturaXmlPath);
        var results = new StringBuilder();

        var methods = doc.Descendants("method")
            .Where(m => m.Attribute("name")?.Value?.Contains(methodName, StringComparison.OrdinalIgnoreCase) == true);

        if (!methods.Any())
            return $"No method matching '{methodName}' found in coverage report.";

        foreach (var method in methods)
        {
            var methodFullName = method.Attribute("name")?.Value;
            results.AppendLine($"Method: {methodFullName}");

            var branchLines = method.Descendants("line")
                .Where(l => l.Attribute("branch")?.Value == "True");

            foreach (var line in branchLines)
            {
                var lineNum = line.Attribute("number")?.Value;
                var conditionCoverage = line.Attribute("condition-coverage")?.Value;

                var uncoveredConditions = line.Descendants("condition")
                    .Where(c => c.Attribute("coverage")?.Value == "0%")
                    .Select(c => $"condition {c.Attribute("number")?.Value} ({c.Attribute("type")?.Value})")
                    .ToList();

                if (uncoveredConditions.Any())
                    results.AppendLine($"  Line {lineNum}: {conditionCoverage} — uncovered: {string.Join(", ", uncoveredConditions)}");
            }
        }

        return results.Length > 0 ? results.ToString() : $"No uncovered branches found for '{methodName}'.";
    }

    [McpServerTool]
    public string AppendTestCode(string testFilePath, string codeToAppend, string? insertAfterAnchor = null)
    {
        if (!File.Exists(testFilePath)) return $"Test file not found: {testFilePath}";

        var content = File.ReadAllText(testFilePath).TrimEnd();

        if (insertAfterAnchor != null)
        {
            var idx = content.LastIndexOf(insertAfterAnchor, StringComparison.Ordinal);
            if (idx < 0) return $"Anchor not found in file: \"{insertAfterAnchor}\"";
            var insertPos = idx + insertAfterAnchor.Length;
            var newContent = content[..insertPos] + "\n\n" + codeToAppend.Trim() + "\n" + content[insertPos..] + "\n";
            File.WriteAllText(testFilePath, newContent);
            return $"Successfully inserted after anchor in {testFilePath}.\nNew file length: {newContent.Length} chars";
        }

        var appended = content + "\n\n" + codeToAppend.Trim() + "\n";
        File.WriteAllText(testFilePath, appended);
        return $"Successfully appended to {testFilePath}.\nAdded:\n{codeToAppend}\nNew file length: {appended.Length} chars";
    }

    private static string StripTestSuffix(string className)
    {
        if (className.EndsWith("UnitTests")) return className[..^9];
        if (className.EndsWith("Tests")) return className[..^5];
        return className;
    }

    private static void SafeDelete(string path)
    {
        if (Directory.Exists(path))
        {
            try { Directory.Delete(path, true); }
            catch { /* ignore for robustness */ }
        }
    }
}
