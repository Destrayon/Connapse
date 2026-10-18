using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connapse.Architecture.Tests;

/// <summary>Syntax-level checks: they read source text, so build configuration can't hide anything.</summary>
internal static class SourceScan
{
    /// <summary>
    /// Anonymous objects passed straight to a <c>Results.*</c> or <c>TypedResults.*</c> call. These
    /// reach OpenAPI as untyped bodies, which is what the shape rules exist to remove.
    /// </summary>
    public static int CountAnonymousResponses(string csharp)
    {
        var root = CSharpSyntaxTree.ParseText(csharp).GetRoot();
        return root.DescendantNodes().OfType<InvocationExpressionSyntax>().Count(call =>
            call.Expression is MemberAccessExpressionSyntax access
            && RightmostName(access.Expression) is "Results" or "TypedResults"
            && call.ArgumentList.Arguments.Any(a => a.Expression is AnonymousObjectCreationExpressionSyntax));
    }

    /// <summary>Service location written inline, which reflection over parameters can't see.</summary>
    public static IEnumerable<string> ServiceLocationCalls(string csharp)
    {
        var root = CSharpSyntaxTree.ParseText(csharp).GetRoot();
        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            string id = name.Identifier.ValueText;
            if (id is "RequestServices" or "GetService" or "GetRequiredService" or "CreateScope" or "CreateAsyncScope")
                yield return id;
        }
    }

    private static string? RightmostName(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
        AliasQualifiedNameSyntax aq => aq.Name.Identifier.ValueText,
        _ => null,
    };
}
