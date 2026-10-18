using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connapse.Architecture.Tests;

/// <summary>Measures page logic in Razor components from source text.</summary>
internal static partial class RazorMetrics
{
    /// <param name="CodeLines">Non-blank lines of C# in code and @{ } blocks, code-behind and a web base class.</param>
    /// <param name="Injects">@inject directives.</param>
    /// <param name="Complexity">Methods plus decision points in that C#.</param>
    /// <param name="MarkupLogic">Control flow, lambdas, conditions and ternaries written in the markup itself.</param>
    public sealed record ComponentMetrics(int CodeLines, int Injects, int Complexity, int MarkupLogic)
    {
        public bool IsEmpty => CodeLines == 0 && Injects == 0 && Complexity == 0 && MarkupLogic == 0;
    }

    /// <summary>
    /// Measures a component: its <c>@code</c>/<c>@functions</c> blocks (found with Roslyn's lexer, so
    /// raw strings of any length are handled and an unbalanced block fails), any extra C# that
    /// belongs to it (a <c>.razor.cs</c> code-behind, a web base class named by <c>@inherits</c>),
    /// and the logic left in its markup.
    /// </summary>
    public static ComponentMetrics MeasureRazor(string razor, IEnumerable<string>? componentCSharp = null)
    {
        int injects = InjectDirective().Matches(razor).Count;
        var blocks = RazorSource.CodeBlocks(razor);
        var statements = RazorSource.StatementBlocks(razor);
        var extra = componentCSharp?.ToList() ?? [];

        int lines = blocks.Concat(statements).Concat(extra).Sum(b => b.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l)));
        int complexity = blocks.Sum(b => Complexity(CSharpSyntaxTree.ParseText($"class __Component {{\n{b}\n}}").GetRoot()))
            + statements.Sum(b => Complexity(CSharpSyntaxTree.ParseText($"class __Component {{ void __Render() {{\n{b}\n}} }}").GetRoot()) - 1)
            + extra.Sum(c => Complexity(CSharpSyntaxTree.ParseText(c).GetRoot()));
        // C# written in markup: @( ) expressions and handlers in attribute values, such as
        // ValueChanged="@((string? v) => { a = v; b = null; })" or @onclick="() => { Save(); }".
        string markupText = RazorSource.WithoutCodeBlocks(razor);
        var markupCSharp = RazorSource.ExpressionBlocks(razor)
            .Concat(AttributeLambda().Matches(markupText).Select(x => x.Groups["lambda"].Value))
            .Concat(SingleQuotedAttributeLambda().Matches(markupText).Select(x => x.Groups["lambda"].Value))
            .Select(e => SyntaxFactory.ParseExpression(e))
            .ToList();
        int ternaries = markupCSharp.Sum(e => e.DescendantNodesAndSelf().OfType<ConditionalExpressionSyntax>().Count());
        var lambdaBodies = markupCSharp.SelectMany(e => e.DescendantNodesAndSelf().OfType<AnonymousFunctionExpressionSyntax>())
            .Select(l => l.Body).ToList();
        // A markup handler's statements count as lines and its decisions as complexity, so a body
        // that grows moves the budget even though its single "=>" doesn't. An expression body has
        // no statements, so each call or assignment in it counts as the line it would be in a block:
        // () => DeleteAsync(id).ContinueWith(Notify) is two.
        lines += lambdaBodies.Sum(b => b is ExpressionSyntax
            ? b.DescendantNodesAndSelf().Count(n => n is InvocationExpressionSyntax or AssignmentExpressionSyntax)
            : b.DescendantNodesAndSelf().OfType<StatementSyntax>().Count(s => s is not BlockSyntax));
        complexity += lambdaBodies.Sum(b => b.DescendantNodesAndSelf().Count(IsDecisionOrMember));
        // C# statements written between markup inside @if/@foreach bodies: bool confirmed = ...;
        // and if (scope.Optional) { confirmed = false; } are code, though no @code block holds them.
        var inline = InlineStatements(markupText);
        lines += inline.Count;
        if (inline.Count > 0)
            complexity += Complexity(CSharpSyntaxTree.ParseText($"class __Component {{ void __Render() {{\n{string.Join("\n", inline)}\n}} }}").GetRoot()) - 1;
        int markup = MarkupLogicPattern().Matches(markupText).Count + ternaries;
        return new ComponentMetrics(lines, injects, complexity, markup);
    }

    /// <summary>The web base class a component names with @inherits, if any (framework bases have none to measure).</summary>
    public static string? InheritedClass(string razor) =>
        InheritsDirective().Match(razor) is { Success: true } m ? m.Groups[1].Value.Split('.')[^1].Split('<')[0] : null;

    /// <summary>
    /// Lines of markup text that are C# statements: a declaration, assignment or call ending in ";",
    /// or a bare if/for/foreach/while/switch (inside a Razor block, control flow needs no "@").
    /// Style, script and comment content is skipped, since CSS rules also end in ";".
    /// </summary>
    internal static List<string> InlineStatements(string markupText)
    {
        string text = NonMarkupContent().Replace(markupText, "");
        var statements = new List<string>();
        foreach (string line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal)))
        {
            if (line[0] is not ('<' or '@' or '}' or '{'))
            {
                if (InlineStatement().IsMatch(line) && !HtmlEntityEnd().IsMatch(line))
                    statements.Add(line);
                continue;
            }
            // A statement sharing a line with a Razor block or markup: @if (saving) { AuditSave(); <span>
            // or } else { count = 0; </p>. The code runs from each "{" to the next tag or brace.
            // Attribute values are left out: a handler there is measured as a lambda already.
            foreach (Match code in BlockCode().Matches(AttributeValue().Replace(line, "")))
                foreach (string piece in code.Groups["code"].Value.Split(';').SkipLast(1).Select(p => p.Trim() + ";"))
                    if (InlineStatement().IsMatch(piece) && !HtmlEntityEnd().IsMatch(piece))
                        statements.Add(piece);
        }
        return statements;
    }

    private static int Complexity(SyntaxNode root) => root.DescendantNodes().Count(IsDecisionOrMember);

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

    /// <summary>A lambda written directly as an attribute value (not inside @( ), which is parsed separately).</summary>
    [GeneratedRegex(@"=""(?<lambda>\s*(?:async\s+)?(?:\([^""()]*\)|\w+)\s*=>[^""]*)""")]
    private static partial Regex AttributeLambda();

    /// <summary>The same, in a single-quoted value: @onclick='() => activeTab = "embedding"'.</summary>
    [GeneratedRegex(@"='(?<lambda>\s*(?:async\s+)?(?:\([^'()]*\)|\w+)\s*=>[^']*)'")]
    private static partial Regex SingleQuotedAttributeLambda();

    /// <summary>A declaration (bool x = ...;), assignment (x = ...; x++;), call (Do(...);), or bare control statement.</summary>
    [GeneratedRegex(@"^(?:(?:if|for|foreach|while|switch)\s*\(|(?:var|bool|int|long|double|decimal|string|[A-Z]\w*(?:<[^>]*>)?\??)\s+\w+\s*=.*;$|[A-Za-z_][\w.\[\]]*\s*(?:[-+*/?]?=(?!=)|\+\+|--).*;$|(?:await\s+)?[A-Za-z_][\w.]*\(.*\)\s*;$)")]
    private static partial Regex InlineStatement();

    [GeneratedRegex(@"=\s*(?:""[^""]*""|'[^']*')")]
    private static partial Regex AttributeValue();

    [GeneratedRegex(@"\{(?<code>[^{}<]*)")]
    private static partial Regex BlockCode();

    /// <summary>Text ending in an HTML entity such as &amp;nbsp; is markup, not a statement.</summary>
    [GeneratedRegex(@"&#?\w+;$")]
    private static partial Regex HtmlEntityEnd();

    [GeneratedRegex(@"<style\b[\s\S]*?</style>|<script\b[\s\S]*?</script>|<!--[\s\S]*?-->|@\*[\s\S]*?\*@", RegexOptions.IgnoreCase)]
    private static partial Regex NonMarkupContent();

    [GeneratedRegex(@"^\s*@inject\s", RegexOptions.Multiline)]
    private static partial Regex InjectDirective();

    [GeneratedRegex(@"^\s*@inherits\s+([\w.<>]+)", RegexOptions.Multiline)]
    private static partial Regex InheritsDirective();

    /// <summary>
    /// Markup control flow (@if, else if, @foreach, @for, @while, @switch, case), lambdas
    /// (event handlers written inline) and boolean or null-coalescing conditions.
    /// </summary>
    [GeneratedRegex(@"@if\b|\belse\s+if\b|@foreach\b|@for\b|@while\b|@switch\b|\bcase\b|=>|&&|\|\||\?\?")]
    private static partial Regex MarkupLogicPattern();
}
