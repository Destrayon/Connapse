using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connapse.Architecture.Tests;

/// <summary>Measures page logic in Razor components from source text.</summary>
internal static partial class RazorMetrics
{
    public sealed record ComponentMetrics(int CodeLines, int Injects, int Complexity);

    /// <summary>
    /// Lines of C# in <c>@code</c>/<c>@functions</c> blocks, <c>@inject</c> directives, and cyclomatic
    /// complexity (methods plus decision points) of that C#. Markup-level <c>@if</c> isn't counted.
    /// </summary>
    public static ComponentMetrics MeasureRazor(string razor)
    {
        int injects = InjectDirective().Matches(razor).Count;
        var blocks = CodeBlocks(razor).ToList();
        int lines = blocks.Sum(b => b.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l)));

        int complexity = 0;
        foreach (string block in blocks)
        {
            var root = CSharpSyntaxTree.ParseText($"class __Component {{\n{block}\n}}").GetRoot();
            complexity += root.DescendantNodes().Count(IsDecisionOrMember);
        }
        return new ComponentMetrics(lines, injects, complexity);
    }

    private static bool IsDecisionOrMember(SyntaxNode n) => n switch
    {
        BaseMethodDeclarationSyntax or AccessorDeclarationSyntax { Body: not null } or LocalFunctionStatementSyntax
            or AnonymousFunctionExpressionSyntax => true,
        IfStatementSyntax or ConditionalExpressionSyntax or CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax
            or SwitchExpressionArmSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax
            or DoStatementSyntax or CatchClauseSyntax => true,
        BinaryExpressionSyntax b => b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression)
            || b.IsKind(SyntaxKind.CoalesceExpression),
        _ => false,
    };

    /// <summary>The bodies of <c>@code { }</c> and <c>@functions { }</c>, matching braces outside strings and comments.</summary>
    public static IEnumerable<string> CodeBlocks(string razor)
    {
        foreach (Match m in CodeBlockStart().Matches(razor))
        {
            int open = m.Index + m.Length - 1;
            int close = MatchingBrace(razor, open);
            if (close > open)
                yield return razor[(open + 1)..close];
        }
    }

    private static int MatchingBrace(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { i = s.IndexOf('\n', i); if (i < 0) return -1; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { i = s.IndexOf("*/", i + 2, StringComparison.Ordinal) + 1; if (i <= 0) return -1; continue; }
            if (c == '"') { i = SkipString(s, i); continue; }
            if (c == '\'') { i = SkipChar(s, i); continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static int SkipString(string s, int quote)
    {
        bool verbatim = quote > 0 && (s[quote - 1] == '@' || (quote > 1 && s[quote - 1] == '$' && s[quote - 2] == '@'));
        bool raw = quote + 2 < s.Length && s[quote + 1] == '"' && s[quote + 2] == '"';
        if (raw)
        {
            int end = s.IndexOf("\"\"\"", quote + 3, StringComparison.Ordinal);
            return end < 0 ? s.Length : end + 2;
        }
        for (int i = quote + 1; i < s.Length; i++)
        {
            if (verbatim && s[i] == '"' && i + 1 < s.Length && s[i + 1] == '"') { i++; continue; }
            if (!verbatim && s[i] == '\\') { i++; continue; }
            if (s[i] == '"') return i;
        }
        return s.Length;
    }

    private static int SkipChar(string s, int quote)
    {
        if (quote + 2 >= s.Length) return quote;
        int end = s[quote + 1] == '\\' ? s.IndexOf('\'', quote + 3) : quote + 2;
        return end < 0 || end >= s.Length || s[end] != '\'' ? quote : end;
    }

    [GeneratedRegex(@"@(code|functions)\s*\{")]
    private static partial Regex CodeBlockStart();

    [GeneratedRegex(@"^\s*@inject\s", RegexOptions.Multiline)]
    private static partial Regex InjectDirective();
}
