using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connapse.Architecture.Tests;

/// <summary>
/// Syntax-level checks: they read source text, so build configuration can't hide anything. Each
/// finding is keyed by its site (the route or member it sits in) and counted there, so adding a
/// second violation where one is listed changes the entry instead of disappearing into it.
/// </summary>
internal static class SourceScan
{
    private static readonly HashSet<string> MapMethods = ["MapGet", "MapPost", "MapPut", "MapPatch", "MapDelete"];
    private static readonly HashSet<string> ResultTypes = ["Results", "TypedResults"];
    private static readonly HashSet<string> LocatorCalls = ["RequestServices", "GetService", "GetRequiredService", "GetKeyedService", "GetRequiredKeyedService", "CreateScope", "CreateAsyncScope"];

    /// <summary>
    /// Anonymous objects reaching a response body: anywhere inside an argument to a
    /// <c>Results</c>/<c>TypedResults</c> call (so <c>Ok(list.Select(x => new { ... }))</c> counts),
    /// or through a local initialised from one. Aliases and <c>using static</c> imports are followed.
    /// </summary>
    public static IEnumerable<string> AnonymousResponseSites(string csharp)
    {
        var root = CSharpSyntaxTree.ParseText(csharp).GetRoot();
        var resultTypes = new HashSet<string>(ResultTypes);
        bool staticImport = false;
        foreach (var u in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            string target = u.NamespaceOrType.ToString();
            bool isResults = ResultTypes.Any(r => target.EndsWith(r, StringComparison.Ordinal));
            if (isResults && u.Alias is { } alias)
                resultTypes.Add(alias.Name.Identifier.ValueText);
            if (isResults && u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
                staticImport = true;
        }

        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            bool isResultCall = call.Expression switch
            {
                MemberAccessExpressionSyntax access => RightmostName(access.Expression) is { } t && resultTypes.Contains(t),
                IdentifierNameSyntax or GenericNameSyntax when staticImport => !IsMapOrLocalCall(call),
                _ => false,
            };
            if (isResultCall && call.ArgumentList.Arguments.Any(a => CarriesAnonymousObject(a.Expression)))
                yield return Site(call);
        }
    }

    /// <summary>Inline service location, as (site, call) pairs.</summary>
    public static IEnumerable<(string Site, string Call)> ServiceLocationCalls(string csharp, bool wholeFile)
    {
        var root = CSharpSyntaxTree.ParseText(csharp).GetRoot();
        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            string id = name.Identifier.ValueText;
            if (!LocatorCalls.Contains(id))
                continue;
            string site = Site(name);
            if (wholeFile || IsRouteSite(site))
                yield return (site, id);
        }
    }

    /// <summary>The route ("GET /x") or member a node sits in; nested lambdas belong to their route.</summary>
    public static string Site(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor is ArgumentSyntax { Expression: AnonymousFunctionExpressionSyntax or IdentifierNameSyntax } arg
                && arg.Parent?.Parent is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: var verb } } map
                && MapMethods.Contains(verb) && map.ArgumentList.Arguments.Count >= 2 && map.ArgumentList.Arguments[^1] == arg)
            {
                return HandlerScan.RouteKey(map);
            }
        }
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case LocalFunctionStatementSyntax f: return f.Identifier.ValueText;
                case MethodDeclarationSyntax m: return m.Identifier.ValueText;
                case ConstructorDeclarationSyntax c: return c.Identifier.ValueText;
                case PropertyDeclarationSyntax p: return p.Identifier.ValueText;
            }
        }
        return "(top level)";
    }

    private static bool IsRouteSite(string site) =>
        site.Length > 0 && site.Split(' ')[0] is "GET" or "POST" or "PUT" or "PATCH" or "DELETE";

    private static bool CarriesAnonymousObject(ExpressionSyntax arg)
    {
        if (arg.DescendantNodesAndSelf().OfType<AnonymousObjectCreationExpressionSyntax>().Any())
            return true;
        if (arg is not IdentifierNameSyntax local)
            return false;
        // A local initialised (or assigned) from an anonymous object in the same body.
        var body = arg.Ancestors().FirstOrDefault(a => a is BlockSyntax or ArrowExpressionClauseSyntax or CompilationUnitSyntax);
        if (body is null)
            return false;
        string name = local.Identifier.ValueText;
        return body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Any(v => v.Identifier.ValueText == name && v.Initializer is { } init && CarriesAnonymousObject(init.Value))
            || body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left is IdentifierNameSyntax l && l.Identifier.ValueText == name && a.Right.DescendantNodesAndSelf().OfType<AnonymousObjectCreationExpressionSyntax>().Any());
    }

    /// <summary>With `using static Results`, a bare call could be Ok(...) or an unrelated local method.</summary>
    private static bool IsMapOrLocalCall(InvocationExpressionSyntax call)
    {
        string? name = call.Expression switch { IdentifierNameSyntax i => i.Identifier.ValueText, GenericNameSyntax g => g.Identifier.ValueText, _ => null };
        if (name is null || MapMethods.Contains(name))
            return true;
        var root = call.SyntaxTree.GetRoot();
        return root.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == name)
            || root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Any(f => f.Identifier.ValueText == name);
    }

    private static string? RightmostName(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
        AliasQualifiedNameSyntax aq => aq.Name.Identifier.ValueText,
        _ => null,
    };
}
