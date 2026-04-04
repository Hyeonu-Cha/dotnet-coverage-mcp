using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace CoverageMcpServer.Services;

public enum InsertionMethod { RoslynAst, StringFallback, StringFallbackNormalized, Appended }

public record InsertionResult(InsertionMethod Method, string Content);

public interface ICodeInserter
{
    Task<InsertionResult> InsertCodeAsync(string testFilePath, string codeToAppend, string? insertAfterAnchor);
}

public class CodeInserter : ICodeInserter
{
    private readonly IFileService _fileService;
    private readonly ILogger<CodeInserter> _logger;

    public CodeInserter(IFileService fileService, ILogger<CodeInserter> logger)
    {
        _fileService = fileService;
        _logger = logger;
    }

    public async Task<InsertionResult> InsertCodeAsync(string testFilePath, string codeToAppend, string? insertAfterAnchor)
    {
        InsertionResult? result = null;

        await _fileService.WithFileLockAsync(testFilePath, async () =>
        {
            var content = File.ReadAllText(testFilePath);

            if (TryRoslynInsert(content, codeToAppend, insertAfterAnchor, out var roslynResult, out var roslynError))
            {
                _fileService.AtomicWriteFile(testFilePath, roslynResult);
                result = new InsertionResult(InsertionMethod.RoslynAst, roslynResult);
                return;
            }

            _logger.LogDebug("Roslyn AST insertion failed for {File}, falling back to string-based: {Reason}",
                testFilePath, roslynError ?? "unknown");
            content = content.TrimEnd();
            result = InsertWithStringFallback(testFilePath, content, codeToAppend, insertAfterAnchor);
            await Task.CompletedTask;
        });

        return result!;
    }

    private InsertionResult InsertWithStringFallback(string testFilePath, string content, string codeToAppend, string? insertAfterAnchor)
    {
        if (insertAfterAnchor != null)
        {
            var idx = content.LastIndexOf(insertAfterAnchor, StringComparison.Ordinal);

            if (idx < 0)
            {
                var normalizedAnchor = NormalizeWhitespace(insertAfterAnchor);
                var normalizedContent = NormalizeWhitespace(content);
                var normalizedIdx = normalizedContent.LastIndexOf(normalizedAnchor, StringComparison.Ordinal);

                if (normalizedIdx >= 0)
                {
                    idx = MapNormalizedPosition(content, normalizedIdx, normalizedAnchor.Length, out var matchLength);
                    if (idx >= 0)
                    {
                        var insertPos = idx + matchLength;
                        var newContent = content[..insertPos] + "\n\n" + codeToAppend.Trim() + "\n" + content[insertPos..] + "\n";
                        _fileService.AtomicWriteFile(testFilePath, newContent);
                        return new InsertionResult(InsertionMethod.StringFallbackNormalized, newContent);
                    }
                }

                throw new KeyNotFoundException(
                    $"Anchor not found (tried Roslyn AST, exact match, whitespace-normalized): \"{insertAfterAnchor}\"");
            }

            {
                var insertPos = idx + insertAfterAnchor.Length;
                var newContent = content[..insertPos] + "\n\n" + codeToAppend.Trim() + "\n" + content[insertPos..] + "\n";
                _fileService.AtomicWriteFile(testFilePath, newContent);
                return new InsertionResult(InsertionMethod.StringFallback, newContent);
            }
        }

        var appended = content + "\n\n" + codeToAppend.Trim() + "\n";
        _fileService.AtomicWriteFile(testFilePath, appended);
        return new InsertionResult(InsertionMethod.Appended, appended);
    }

    internal static bool TryRoslynInsert(string content, string codeToAppend, string? insertAfterAnchor, out string result, out string? failureReason)
    {
        result = "";
        failureReason = null;
        try
        {
            var tree = CSharpSyntaxTree.ParseText(content);
            var root = tree.GetCompilationUnitRoot();

            var targetType = root.DescendantNodes().OfType<TypeDeclarationSyntax>().LastOrDefault();
            if (targetType == null) { failureReason = "no type declaration found in file"; return false; }

            var wrapperTree = CSharpSyntaxTree.ParseText($"class __RoslynWrapper__ {{\n{codeToAppend}\n}}");
            var wrapperRoot = wrapperTree.GetCompilationUnitRoot();
            var wrapperType = wrapperRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (wrapperType == null || wrapperType.Members.Count == 0) { failureReason = "codeToAppend produced no parseable members"; return false; }

            var wrapperDiags = wrapperTree.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            if (wrapperDiags.Count > 0) { failureReason = $"codeToAppend has syntax errors: {wrapperDiags[0].GetMessage()}"; return false; }

            var newMembers = wrapperType.Members;
            var eol = content.Contains("\r\n") ? SyntaxFactory.CarriageReturnLineFeed : SyntaxFactory.LineFeed;
            var firstMember = newMembers[0];
            var leadingTrivia = SyntaxFactory.TriviaList(eol, eol);
            newMembers = newMembers.Replace(firstMember,
                firstMember.WithLeadingTrivia(leadingTrivia.AddRange(firstMember.GetLeadingTrivia())));

            TypeDeclarationSyntax updatedType;

            if (insertAfterAnchor != null)
            {
                var anchorMember = targetType.Members
                    .LastOrDefault(m => m.ToFullString().Contains(insertAfterAnchor, StringComparison.Ordinal));

                if (anchorMember == null)
                {
                    var normalizedAnchor = NormalizeWhitespace(insertAfterAnchor);
                    anchorMember = targetType.Members
                        .LastOrDefault(m => NormalizeWhitespace(m.ToFullString()).Contains(normalizedAnchor, StringComparison.Ordinal));
                }

                if (anchorMember == null) { failureReason = $"anchor not found in any class member: \"{insertAfterAnchor}\""; return false; }

                var insertIndex = targetType.Members.IndexOf(anchorMember) + 1;
                var membersList = targetType.Members.ToList();
                membersList.InsertRange(insertIndex, newMembers);
                updatedType = targetType.WithMembers(SyntaxFactory.List(membersList));
            }
            else
            {
                updatedType = targetType.AddMembers([.. newMembers]);
            }

            var newRoot = root.ReplaceNode(targetType, updatedType);
            result = newRoot.ToFullString();
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"exception: {ex.Message}";
            return false;
        }
    }

    internal static string NormalizeWhitespace(string input) =>
        Regex.Replace(input, @"\s+", " ").Trim();

    internal static int MapNormalizedPosition(string original, int normalizedStart, int normalizedLength, out int originalMatchLength)
    {
        originalMatchLength = 0;

        var map = BuildNormalizedToOriginalMap(original);

        if (normalizedStart >= map.Count || normalizedStart < 0)
            return -1;

        var normalizedEnd = normalizedStart + normalizedLength - 1;
        if (normalizedEnd >= map.Count)
            return -1;

        var originalStart = map[normalizedStart];
        var originalEnd = map[normalizedEnd];

        while (originalEnd + 1 < original.Length && char.IsWhiteSpace(original[originalEnd + 1])
               && (normalizedEnd + 1 >= map.Count || map[normalizedEnd + 1] != originalEnd + 1))
        {
            originalEnd++;
        }

        originalMatchLength = originalEnd - originalStart + 1;
        return originalStart;
    }

    private static List<int> BuildNormalizedToOriginalMap(string original)
    {
        var map = new List<int>();
        var inWhitespace = false;

        for (var i = 0; i < original.Length; i++)
        {
            if (char.IsWhiteSpace(original[i]))
            {
                if (!inWhitespace)
                {
                    map.Add(i);
                    inWhitespace = true;
                }
            }
            else
            {
                map.Add(i);
                inWhitespace = false;
            }
        }

        return map;
    }
}
