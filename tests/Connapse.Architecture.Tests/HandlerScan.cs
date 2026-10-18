using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connapse.Architecture.Tests;

/// <summary>
/// Finds what each route handler and MCP tool depends on, one entry per route or tool, from source.
/// Keying by handler rather than by class means a second handler reaching the same store is a new
/// entry, and source text sees what reflection can't: services resolved inside a handler body.
/// </summary>
internal static class HandlerScan
{
    private static readonly HashSet<string> MapMethods = ["MapGet", "MapPost", "MapPut", "MapPatch", "MapDelete"];
    private static readonly HashSet<string> Resolvers = ["GetService", "GetRequiredService", "GetKeyedService", "GetRequiredKeyedService"];

    public sealed record Handler(string Name, IReadOnlyList<TypeSyntax> Dependencies);

    /// <summary>Every route lambda or method group and every [McpServerTool] method in one file.</summary>
    public static IEnumerable<Handler> Handlers(string fileName, string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        string owner = Path.GetFileNameWithoutExtension(fileName);
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: var verb } || !MapMethods.Contains(verb))
                continue;
            var args = call.ArgumentList.Arguments;
            if (args.Count < 2)
                continue;
            string route = args[0].Expression is LiteralExpressionSyntax lit ? lit.Token.ValueText : args[0].Expression.ToString();
            string name = $"{owner} {verb[3..].ToUpperInvariant()} {route}";
            var handler = args[^1].Expression;
            if (handler is AnonymousFunctionExpressionSyntax lambda)
                yield return new Handler(name, Dependencies(lambda));
            else if (RightmostName(handler) is { } target)
                foreach (var m in methods.Where(m => m.Identifier.ValueText == target))
                    yield return new Handler(name, Dependencies(m));
        }

        foreach (var m in methods)
        {
            var tool = m.AttributeLists.SelectMany(a => a.Attributes).FirstOrDefault(a => RightmostName(a.Name) is "McpServerTool" or "McpServerToolAttribute");
            if (tool is null)
                continue;
            string toolName = tool.ArgumentList?.Arguments
                .FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "Name")?.Expression is LiteralExpressionSyntax n
                ? n.Token.ValueText : m.Identifier.ValueText;
            yield return new Handler($"{owner} {toolName}", Dependencies(m));
        }
    }

    /// <summary>Parameter types, plus types resolved in the body through a service provider.</summary>
    private static List<TypeSyntax> Dependencies(SyntaxNode handler)
    {
        var parameters = handler switch
        {
            ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters.AsEnumerable(),
            SimpleLambdaExpressionSyntax s => [s.Parameter],
            AnonymousMethodExpressionSyntax a => a.ParameterList?.Parameters.AsEnumerable() ?? [],
            BaseMethodDeclarationSyntax m => m.ParameterList.Parameters.AsEnumerable(),
            _ => [],
        };
        var types = parameters.Select(p => p.Type).OfType<TypeSyntax>().ToList();

        foreach (var call in handler.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = call.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name,
                SimpleNameSyntax sn => sn,
                _ => null,
            };
            if (name is null || !Resolvers.Contains(name.Identifier.ValueText))
                continue;
            if (name is GenericNameSyntax g)
                types.AddRange(g.TypeArgumentList.Arguments);
            foreach (var arg in call.ArgumentList.Arguments)
                if (arg.Expression is TypeOfExpressionSyntax t)
                    types.Add(t.Type);
        }
        return types;
    }

    /// <summary>A store, DbContext or EF context factory, by name, anywhere in the type (Lazy&lt;ISourceStore&gt; counts).</summary>
    public static bool IsStore(TypeSyntax type) =>
        type.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Any(n =>
            n.Identifier.ValueText is var id && (id.EndsWith("Store", StringComparison.Ordinal)
                || id.EndsWith("DbContext", StringComparison.Ordinal) || id == "IDbContextFactory"));

    /// <summary>The type without namespaces, so entries don't depend on how a file imports it.</summary>
    public static string Display(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => Display(q.Right),
        AliasQualifiedNameSyntax a => Display(a.Name),
        GenericNameSyntax g => $"{g.Identifier.ValueText}<{string.Join(", ", g.TypeArgumentList.Arguments.Select(Display))}>",
        NullableTypeSyntax n => Display(n.ElementType) + "?",
        _ => type.ToString(),
    };

    private static string? RightmostName(SyntaxNode node) => node switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        _ => null,
    };
}
