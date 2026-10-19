using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Connapse.Architecture.Tests;

/// <summary>
/// The C# inside Razor components. Block boundaries are found with Roslyn's own lexer, so every
/// kind of string (verbatim, interpolated, raw with any number of quotes) and comment is handled
/// the way the compiler handles it, and an unbalanced block fails the test instead of silently
/// measuring as empty.
/// </summary>
internal static partial class RazorSource
{
    /// <summary>The bodies of every <c>@code { }</c> and <c>@functions { }</c> block.</summary>
    public static IReadOnlyList<string> CodeBlocks(string razor)
    {
        var blocks = new List<string>();
        foreach (Match m in CodeBlockStart().Matches(razor))
        {
            int open = m.Index + m.Length - 1;
            int depth = 0, end = -1;
            foreach (var token in SyntaxFactory.ParseTokens(razor[open..]))
            {
                if (token.IsKind(SyntaxKind.OpenBraceToken))
                    depth++;
                else if (token.IsKind(SyntaxKind.CloseBraceToken) && --depth == 0)
                {
                    end = open + token.SpanStart;
                    break;
                }
            }
            if (end < 0)
                throw new InvalidOperationException($"Unbalanced {m.Value.TrimEnd('{').Trim()} block at offset {m.Index}; the measurement would be wrong.");
            blocks.Add(razor[(open + 1)..end]);
        }
        return blocks;
    }

    /// <summary>A component's code blocks parsed as class members, so methods and calls can be walked.</summary>
    public static SyntaxNode ParseCodeBlocks(string razor) =>
        CSharpSyntaxTree.ParseText($"partial class __Component {{\n{string.Join("\n", CodeBlocks(razor))}\n}}").GetRoot();

    /// <summary>
    /// Types a component resolves from a provider anywhere in its file, markup included:
    /// <c>@(Scopes.CreateScope().ServiceProvider.GetRequiredService&lt;ISourceStore&gt;())</c> never
    /// reaches a code block, so the markup is searched as text. Names come back without namespaces.
    /// </summary>
    public static IEnumerable<string> ResolvedTypeNames(string razor) =>
        ResolverCall().Matches(razor).Select(m => (m.Groups["generic"].Success ? m.Groups["generic"] : m.Groups["typeof"]).Value)
            .Select(name => System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\b[\w]+\.", ""));

    /// <summary>"Components/Pages/Sources.razor" or ".razor.cs" as the component name the reflection rules use: "Pages.Sources".</summary>
    public static string ComponentName(string pathUnderWeb)
    {
        string rel = pathUnderWeb.StartsWith("Components/", StringComparison.Ordinal) ? pathUnderWeb["Components/".Length..] : pathUnderWeb;
        foreach (string ext in new[] { ".razor.cs", ".razor" })
            if (rel.EndsWith(ext, StringComparison.Ordinal))
                rel = rel[..^ext.Length];
        return rel.Replace('/', '.');
    }

    [GeneratedRegex(@"@(code|functions)\s*\{")]
    private static partial Regex CodeBlockStart();

    [GeneratedRegex(@"\bGet(?:Required)?(?:Keyed)?Services?\s*(?:<\s*(?<generic>[\w.]+(?:<[\w.,\s]+>)?)\s*>\s*\(|\(\s*typeof\s*\(\s*(?<typeof>[\w.]+)\s*\))")]
    private static partial Regex ResolverCall();
}
