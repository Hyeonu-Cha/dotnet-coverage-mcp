using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace DotNetCoverageMcp.Services;

public enum InsertionMethod { RoslynAst, StringFallback, StringFallbackNormalized, Appended }

public record InsertionResult(InsertionMethod Method, string Content);

/// <summary>The code to insert is not valid C# class-member syntax; nothing was written.</summary>
public sealed class CodeSyntaxException(string message) : Exception(message);

public interface ICodeInserter
{
    Task<InsertionResult> InsertCodeAsync(string testFilePath, string codeToAppend, string? insertAfterAnchor, string? targetClass = null);
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

    public async Task<InsertionResult> InsertCodeAsync(string testFilePath, string codeToAppend, string? insertAfterAnchor, string? targetClass = null)
    {
        // Checked before either insertion path. The string fallback exists for anchors
        // Roslyn cannot place, not for broken code: falling back on a syntax error used to
        // write the broken code into the file and report success, and the agent only found
        // out from the next build.
        var syntaxError = FindSyntaxError(codeToAppend);
        if (syntaxError != null)
            throw new CodeSyntaxException(syntaxError);

        InsertionResult? result = null;

        await _fileService.WithFileLockAsync(testFilePath, async () =>
        {
            var content = File.ReadAllText(testFilePath);

            if (TryRoslynInsert(content, codeToAppend, insertAfterAnchor, out var roslynResult, out var roslynError, targetClass))
            {
                _fileService.AtomicWriteFile(testFilePath, roslynResult);
                result = new InsertionResult(InsertionMethod.RoslynAst, roslynResult);
                return;
            }

            // The string fallback has no notion of classes, so it cannot honour targetClass.
            if (targetClass != null)
                throw new InvalidOperationException($"Could not insert into class '{targetClass}': {roslynError}.");

            _logger.LogDebug("Roslyn AST insertion failed for {File}, falling back to string-based: {Reason}",
                testFilePath, roslynError ?? "unknown");
            var fallback = InsertWithStringFallback(content.TrimEnd(), codeToAppend, insertAfterAnchor);

            // The fallback places code by text, not syntax: an anchor spanning two members, or
            // pointing inside one, lands the code mid-declaration. That used to be written and
            // reported as success, and the agent found out at the next build. A file that
            // already had errors can't be judged this way, so it keeps the old behaviour.
            if (FirstSyntaxError(content) == null && FirstSyntaxError(fallback.Content) is { } broken)
                throw new InvalidOperationException(
                    $"Inserting at that anchor would leave the file with a syntax error ({broken}), so it was not changed. " +
                    "The anchor probably spans two members or points inside one: use text from a single member, or targetClass.");

            _fileService.AtomicWriteFile(testFilePath, fallback.Content);
            result = fallback;
            await Task.CompletedTask;
        });

        return result!;
    }

    /// <summary>Builds the fallback result; the caller checks it before anything is written.</summary>
    private static InsertionResult InsertWithStringFallback(string content, string codeToAppend, string? insertAfterAnchor)
    {
        // Strip and hoist leading `using` directives — otherwise the fallback would inject them
        // inside the class body, producing invalid C#.
        var (extractedUsings, memberCode) = SplitLeadingUsings(codeToAppend);
        if (extractedUsings.Count > 0)
            content = HoistUsingsIntoContent(content, extractedUsings);
        var result = InsertByText(content, memberCode, insertAfterAnchor);

        // The file's own line endings, as on the Roslyn path; line breaks inside multi-line
        // string literals stay as written.
        var eol = content.Contains("\r\n") ? "\r\n" : "\n";
        return eol == "\n" ? result : result with { Content = WithLineEndings(result.Content, eol) };
    }

    private static InsertionResult InsertByText(string content, string codeToAppend, string? insertAfterAnchor)
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
                        return new InsertionResult(InsertionMethod.StringFallbackNormalized, newContent);
                    }
                }

                throw new KeyNotFoundException(
                    $"Anchor not found (tried Roslyn AST, exact match, whitespace-normalized): \"{insertAfterAnchor}\"");
            }

            {
                var insertPos = idx + insertAfterAnchor.Length;
                var newContent = content[..insertPos] + "\n\n" + codeToAppend.Trim() + "\n" + content[insertPos..] + "\n";
                return new InsertionResult(InsertionMethod.StringFallback, newContent);
            }
        }

        var bracePos = FindInnermostTypeClosingBrace(content);
        string appended;
        if (bracePos >= 0)
        {
            appended = content[..bracePos] + "\n" + codeToAppend.Trim() + "\n" + content[bracePos..] + "\n";
        }
        else
        {
            // No closing brace found (e.g. empty file or only file-scoped globals) — append at EOF.
            appended = content + "\n\n" + codeToAppend.Trim() + "\n";
        }
        return new InsertionResult(InsertionMethod.Appended, appended);
    }

    /// <summary>
    /// Finds the insertion point for a new class member when no anchor is provided.
    /// Walks back from EOF through consecutive closing braces (separated only by whitespace)
    /// and returns the position of the innermost one — i.e., the brace that closes the last
    /// type declaration. This keeps inserted code inside the class even when the file uses
    /// a block-scoped namespace. Returns -1 if no suitable brace is found.
    /// </summary>
    internal static int FindInnermostTypeClosingBrace(string content)
    {
        var i = content.Length - 1;
        while (i >= 0 && char.IsWhiteSpace(content[i])) i--;
        if (i < 0 || content[i] != '}') return -1;

        var outerBrace = i;
        var j = i - 1;
        while (j >= 0 && char.IsWhiteSpace(content[j])) j--;
        if (j >= 0 && content[j] == '}')
            return j; // inner type's closing brace inside a block-scoped namespace

        return outerBrace;
    }

    // Matches a single `using X;`, `using static X;`, `using alias = X;`, or `global using X;`
    // directive at the start of a string (after any leading whitespace/comment trivia). We strip
    // these out of codeToAppend before wrapping it in a class, since using directives are invalid
    // inside a class body and would otherwise fail the Roslyn parse.
    private static readonly Regex LeadingUsingRegex = new(
        @"^(?:global\s+)?using\s+(?:static\s+)?[\w.@=\s<>,]+;[^\S\r\n]*\r?\n?",
        RegexOptions.Compiled);

    internal static (List<string> Usings, string Remainder) SplitLeadingUsings(string code)
    {
        var usings = new List<string>();
        var remainder = code;
        while (true)
        {
            var triviaLen = ConsumeLeadingTrivia(remainder);
            var afterTrivia = remainder[triviaLen..];
            var match = LeadingUsingRegex.Match(afterTrivia);
            if (!match.Success || match.Index != 0) break;
            usings.Add(match.Value.Trim());
            // Splice out only the using, preserving leading trivia (comments/whitespace) so the
            // original structure of the remainder is not lost.
            remainder = remainder[..triviaLen] + afterTrivia[match.Length..];
        }
        return (usings, remainder);
    }

    // Advance over whitespace, line comments (//), and block comments (/* */) so SplitLeadingUsings
    // can find `using` directives that are preceded by documentation or header comments.
    private static int ConsumeLeadingTrivia(string s)
    {
        var i = 0;
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/')
            {
                i += 2;
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                if (i + 1 < s.Length) i += 2;       // consume closing */
                else i = s.Length;                  // unclosed comment: treat remainder as trivia
                continue;
            }
            break;
        }
        return i;
    }

    /// <summary>
    /// Parses the fragment the way it will be inserted (leading usings hoisted, the rest as
    /// members of a class) and describes the first syntax error, or returns null if it is valid.
    /// </summary>
    internal static string? FindSyntaxError(string codeToAppend)
    {
        var (_, memberCode) = SplitLeadingUsings(codeToAppend);
        var tree = CSharpSyntaxTree.ParseText($"class __RoslynWrapper__ {{\n{memberCode}\n}}");

        var error = tree.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (error == null)
        {
            // A stray '}' closes the wrapper early. What follows then parses as another top-level
            // declaration, which the insert dropped while reporting success.
            var root = tree.GetCompilationUnitRoot();
            if (root.Members.Count != 1)
                return "codeToAppend has a '}' that closes the class early, so the code after it would be lost. " +
                       "Pass members only, with balanced braces.";
            var members = root.DescendantNodes().OfType<TypeDeclarationSyntax>().First().Members;
            return members.Count == 0 ? "codeToAppend contains no member declarations." : null;
        }

        // Quote the offending line rather than a line number: hoisted usings and the wrapper
        // shift numbering, while the line's text is unambiguous to the caller.
        var line = tree.GetText().Lines[error.Location.GetLineSpan().StartLinePosition.Line].ToString().Trim();
        return $"codeToAppend is not valid C# member code: {error.Id}: {error.GetMessage()} Near: `{line}`. " +
               "Pass test methods or fields only; namespace and class declarations belong in the file, not in codeToAppend.";
    }

    // Attributes that mark a type as the file's test class (xUnit, NUnit, MSTest).
    private static readonly HashSet<string> TestAttributeNames =
        ["Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod", "TestFixture", "TestClass"];

    private static bool HasTestAttribute(SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(l => l.Attributes).Any(a =>
        {
            var name = a.Name.ToString();
            name = name[(name.LastIndexOf('.') + 1)..];
            if (name.EndsWith("Attribute", StringComparison.Ordinal)) name = name[..^"Attribute".Length];
            return TestAttributeNames.Contains(name);
        });

    private static bool ContainsTests(TypeDeclarationSyntax type) =>
        HasTestAttribute(type.AttributeLists)
        || type.Members.OfType<MethodDeclarationSyntax>().Any(m => HasTestAttribute(m.AttributeLists));

    /// <summary>
    /// The class new tests go into when the caller names neither a class nor an anchor:
    /// the last top-level type that contains tests, else the last top-level type. Nested
    /// types and trailing helpers (test-data records, fakes) are skipped; picking the last
    /// type of any kind is what used to put tests into those.
    /// </summary>
    internal static TypeDeclarationSyntax PickDefaultTargetType(CompilationUnitSyntax root)
    {
        var topLevel = root.DescendantNodes(n => n is not TypeDeclarationSyntax)
            .OfType<TypeDeclarationSyntax>()
            .ToList();
        return topLevel.LastOrDefault(ContainsTests) ?? topLevel.Last();
    }

    // The last member, in any candidate type, whose text contains the anchor. A nested type
    // also counts as a member of its parent, so a method inside it is preferred over the
    // whole nested type: the new code then goes next to that method, inside the nested type.
    private static (TypeDeclarationSyntax Type, MemberDeclarationSyntax Member)? FindAnchorMember(
        IEnumerable<TypeDeclarationSyntax> types, string anchor)
    {
        var candidates = types.SelectMany(t => t.Members.Select(m => (Type: t, Member: m))).ToList();

        (TypeDeclarationSyntax, MemberDeclarationSyntax)? Best(Func<MemberDeclarationSyntax, bool> matches) =>
            candidates.Where(c => matches(c.Member))
                .OrderBy(c => c.Member is BaseTypeDeclarationSyntax ? 0 : 1)
                .ThenBy(c => c.Member.SpanStart)
                .Select(c => ((TypeDeclarationSyntax, MemberDeclarationSyntax)?)c)
                .LastOrDefault();

        var normalizedAnchor = NormalizeWhitespace(anchor);
        return Best(m => m.ToFullString().Contains(anchor, StringComparison.Ordinal))
            ?? Best(m => NormalizeWhitespace(m.ToFullString()).Contains(normalizedAnchor, StringComparison.Ordinal));
    }

    internal static bool TryRoslynInsert(string content, string codeToAppend, string? insertAfterAnchor,
        out string result, out string? failureReason, string? targetClass = null)
    {
        result = "";
        failureReason = null;
        try
        {
            var (extractedUsings, memberCode) = SplitLeadingUsings(codeToAppend);

            var tree = CSharpSyntaxTree.ParseText(content);
            var root = tree.GetCompilationUnitRoot();

            var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();
            if (types.Count == 0) { failureReason = "no type declaration found in file"; return false; }

            if (targetClass != null)
            {
                types = types.Where(t => t.Identifier.Text == targetClass).ToList();
                if (types.Count == 0) { failureReason = $"class '{targetClass}' not found in file"; return false; }
            }

            TypeDeclarationSyntax targetType;
            MemberDeclarationSyntax? anchorMember = null;
            if (insertAfterAnchor != null)
            {
                // Limitation: this only matches anchors contained within a single member's
                // full text. Anchors that span two adjacent members (e.g., the closing brace
                // of method A plus the signature of method B) will not match here — the
                // string fallback path handles that case.
                var match = FindAnchorMember(types, insertAfterAnchor);
                if (match == null) { failureReason = $"anchor not found in any class member: \"{insertAfterAnchor}\""; return false; }
                (targetType, anchorMember) = match.Value;
            }
            else
            {
                targetType = targetClass != null ? types.Last() : PickDefaultTargetType(root);
            }

            memberCode = TrimBlankLines(memberCode);
            var wrapperTree = CSharpSyntaxTree.ParseText(WrapperPrefix + memberCode + "\n}");
            var wrapperRoot = wrapperTree.GetCompilationUnitRoot();
            var wrapperType = wrapperRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (wrapperType == null || wrapperType.Members.Count == 0) { failureReason = "codeToAppend produced no parseable members"; return false; }

            var wrapperDiags = wrapperTree.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            if (wrapperDiags.Count > 0) { failureReason = $"codeToAppend has syntax errors: {wrapperDiags[0].GetMessage()}"; return false; }

            // Laid out like the members around it: their indentation, the file's line endings,
            // one blank line from the previous member. The code arrives as the agent wrote it
            // (usually LF, unindented), and inserting it as-is left mixed line endings and
            // code at column 0 for every review and `dotnet format` check to trip over.
            var eolText = content.Contains("\r\n") ? "\r\n" : "\n";
            var eol = eolText == "\r\n" ? SyntaxFactory.CarriageReturnLineFeed : SyntaxFactory.LineFeed;
            var previousMember = anchorMember ?? targetType.Members.LastOrDefault();
            var indent = (previousMember != null ? LineIndent(previousMember.GetFirstToken(), content) : null)
                ?? (LineIndent(targetType.GetFirstToken(), content) ?? "") + (content.Contains("\n\t") ? "\t" : "    ");
            var formatted = Reindent(memberCode, MultiLineTokenSpans(wrapperRoot), indent, eolText);

            var newMembers = CSharpSyntaxTree.ParseText($"class __RoslynWrapper__ {{{eolText}{formatted}{eolText}}}")
                .GetCompilationUnitRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().First().Members;
            var previousToken = previousMember?.GetLastToken() ?? targetType.OpenBraceToken;
            var separator = SyntaxFactory.TriviaList();
            if (!previousToken.TrailingTrivia.Any(SyntaxKind.EndOfLineTrivia)) separator = separator.Add(eol);
            if (previousMember != null) separator = separator.Add(eol);
            var firstMember = newMembers[0];
            newMembers = newMembers.Replace(firstMember,
                firstMember.WithLeadingTrivia(separator.AddRange(firstMember.GetLeadingTrivia())));

            TypeDeclarationSyntax updatedType;

            if (anchorMember != null)
            {
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

            if (extractedUsings.Count > 0)
                newRoot = MergeUsings(newRoot, extractedUsings);

            result = newRoot.ToFullString();
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"exception: {ex.Message}";
            return false;
        }
    }

    private const string WrapperPrefix = "class __RoslynWrapper__ {\n";

    private static string TrimBlankLines(string code) =>
        Regex.Replace(code, @"\A(?:[ \t]*\r?\n)+", "").TrimEnd();

    /// <summary>The whitespace before <paramref name="token"/> on its line, or null when other
    /// code precedes it there (a one-line class, say).</summary>
    private static string? LineIndent(SyntaxToken token, string text)
    {
        var start = token.SpanStart;
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var prefix = text[lineStart..start];
        return prefix.All(c => c is ' ' or '\t') ? prefix : null;
    }

    /// <summary>Spans, relative to the code inside the wrapper, of tokens that contain a line
    /// break: verbatim, raw and interpolated multi-line strings. Their text is part of the
    /// program, so re-indenting or changing line endings there would change what a test checks.</summary>
    private static List<TextSpan> MultiLineTokenSpans(CompilationUnitSyntax wrapperRoot) =>
        wrapperRoot.DescendantTokens()
            .Where(t => t.Text.Contains('\n'))
            .Select(t => new TextSpan(t.SpanStart - WrapperPrefix.Length, t.Span.Length))
            .ToList();

    /// <summary>The first syntax error in <paramref name="text"/> as "CSxxxx: message", or null.</summary>
    internal static string? FirstSyntaxError(string text) =>
        CSharpSyntaxTree.ParseText(text).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id}: {d.GetMessage()}")
            .FirstOrDefault();

    /// <summary><paramref name="text"/> with every line break outside multi-line string literals
    /// turned into <paramref name="eol"/>; indentation is left alone.</summary>
    internal static string WithLineEndings(string text, string eol) =>
        Reindent(text, CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot().DescendantTokens()
            .Where(t => t.Text.Contains('\n')).Select(t => t.Span).ToList(), indent: null, eol);

    /// <summary>
    /// Moves <paramref name="code"/> to <paramref name="indent"/>, keeping each line's indentation
    /// relative to the least-indented line, and ends every line with <paramref name="eol"/>.
    /// Line breaks inside <paramref name="literalSpans"/>, and the lines they start, are kept as written.
    /// A null <paramref name="indent"/> keeps every line's indentation and only changes the breaks.
    /// </summary>
    internal static string Reindent(string code, IReadOnlyList<TextSpan> literalSpans, string? indent, string eol)
    {
        var lines = new List<(string Text, bool InsideLiteral, string Break)>();
        var lineStart = 0;
        var insideLiteral = false;
        for (var i = 0; i <= code.Length; i++)
        {
            if (i < code.Length && code[i] != '\n') continue;
            var end = i > lineStart && code[i - 1] == '\r' ? i - 1 : i;
            var breakInLiteral = i < code.Length && literalSpans.Any(s => s.Start <= end && end < s.End);
            lines.Add((code[lineStart..end], insideLiteral, i < code.Length ? (breakInLiteral ? code[end..(i + 1)] : eol) : ""));
            insideLiteral = breakInLiteral;
            lineStart = i + 1;
        }

        static int Leading(string s) => s.Length - s.TrimStart(' ', '\t').Length;
        var codeLines = lines.Where(l => !l.InsideLiteral && !string.IsNullOrWhiteSpace(l.Text)).ToList();
        var common = codeLines.Count == 0 ? 0 : codeLines.Min(l => Leading(l.Text));

        var sb = new StringBuilder();
        foreach (var (text, inLiteral, lineBreak) in lines)
        {
            if (inLiteral || indent == null) sb.Append(text);
            else if (!string.IsNullOrWhiteSpace(text)) sb.Append(indent).Append(text, common, text.Length - common);
            sb.Append(lineBreak);
        }
        return sb.ToString();
    }

    internal static string HoistUsingsIntoContent(string content, List<string> extractedUsings)
    {
        // Insert missing usings after the last existing `using ...;` line, or at the very top.
        // Split alias from name so keys have the same shape as the Roslyn path and are
        // insensitive to internal whitespace (`M=X` and `M = X` collapse correctly).
        var existing = new HashSet<string>();
        foreach (Match m in Regex.Matches(content, @"^\s*using\s+(static\s+)?([\w.@=\s<>,]+);", RegexOptions.Multiline))
            existing.Add(KeyFromRegexBody(m.Groups[1].Success, m.Groups[2].Value));

        var toAdd = new List<string>();
        foreach (var raw in extractedUsings)
        {
            var tokenMatch = Regex.Match(raw, @"^\s*(?:global\s+)?using\s+(static\s+)?([\w.@=\s<>,]+);");
            if (!tokenMatch.Success) continue;
            var key = KeyFromRegexBody(tokenMatch.Groups[1].Success, tokenMatch.Groups[2].Value);
            if (existing.Add(key))
                toAdd.Add(raw);
        }
        if (toAdd.Count == 0) return content;

        var lastUsing = Regex.Matches(content, @"^\s*using\s+(?:static\s+)?[\w.@=\s<>,]+;\r?\n", RegexOptions.Multiline)
            .LastOrDefault();
        var block = string.Join("\n", toAdd) + "\n";

        if (lastUsing != null)
        {
            var insertAt = lastUsing.Index + lastUsing.Length;
            return content[..insertAt] + block + content[insertAt..];
        }
        return block + "\n" + content;
    }

    private static CompilationUnitSyntax MergeUsings(CompilationUnitSyntax root, List<string> extractedUsings)
    {
        var existing = root.Usings
            .Where(u => u.Name != null)
            .Select(RoslynUsingKey)
            .ToHashSet();

        var toAdd = new List<UsingDirectiveSyntax>();
        foreach (var raw in extractedUsings)
        {
            var parsed = SyntaxFactory.ParseCompilationUnit(raw).Usings.FirstOrDefault();
            if (parsed == null || parsed.Name == null) continue;
            if (existing.Add(RoslynUsingKey(parsed)))
                toAdd.Add(parsed);
        }

        return toAdd.Count == 0 ? root : root.AddUsings([.. toAdd]);
    }

    // Keys must distinguish (a) `using X;` from `using static X;` and
    // (b) `using X;` from `using M = X;`. u.Name is only the RHS, so aliased
    // and non-aliased imports collide unless we mix the alias identifier in.
    private static string RoslynUsingKey(UsingDirectiveSyntax u)
    {
        var isStatic = u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword);
        var alias = u.Alias?.Name.Identifier.Text ?? "";
        return UsingKey(isStatic, alias, u.Name?.ToString() ?? "");
    }

    private static string UsingKey(bool isStatic, string alias, string name) =>
        (isStatic ? "static " : "") + alias.Trim() + "|" + name.Trim();

    // Split a regex-captured using body (possibly `alias = name`) into the alias/name pair
    // so the regex path produces keys shaped like the Roslyn path. This also collapses
    // whitespace variance around `=` because we trim each side.
    private static string KeyFromRegexBody(bool isStatic, string body)
    {
        var trimmed = body.Trim();
        var eqIdx = trimmed.IndexOf('=');
        if (eqIdx > 0)
        {
            var alias = trimmed[..eqIdx];
            var name = trimmed[(eqIdx + 1)..];
            return UsingKey(isStatic, alias, name);
        }
        return UsingKey(isStatic, "", trimmed);
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
