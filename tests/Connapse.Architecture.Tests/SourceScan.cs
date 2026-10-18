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
    private static readonly HashSet<string> LocatorCalls =
        ["RequestServices", "GetService", "GetRequiredService", "GetServices", "GetKeyedService", "GetRequiredKeyedService", "GetKeyedServices", "CreateScope", "CreateAsyncScope"];

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

        bool IsResultCall(InvocationExpressionSyntax call) => call.Expression switch
        {
            MemberAccessExpressionSyntax access => RightmostName(access.Expression) is { } t && resultTypes.Contains(t),
            IdentifierNameSyntax or GenericNameSyntax when staticImport => !IsMapOrLocalCall(call),
            _ => false,
        };

        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!IsResultCall(call))
                continue;
            int count = call.ArgumentList.Arguments.Sum(a => AnonymousObjects(a.Expression, []));
            for (int i = 0; i < count; i++)
                yield return Site(call);
        }

        // A handler can also return the anonymous object itself, which ASP.NET serializes as the body:
        // app.MapGet("/x", () => new { id = 1 }). Results calls among its returns are counted above.
        foreach (var map in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (map.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: var verb }
                || !MapMethods.Contains(verb) || map.ArgumentList.Arguments.Count < 2)
                continue;
            int count = HandlerReturns(map.ArgumentList.Arguments[^1].Expression)
                .Sum(e => DirectAnonymousObjects(e, IsResultCall));
            for (int i = 0; i < count; i++)
                yield return HandlerScan.RouteKey(map);
        }
    }

    /// <summary>The expressions a route handler returns: a lambda's, or a same-file method group's.</summary>
    private static IEnumerable<ExpressionSyntax> HandlerReturns(ExpressionSyntax handler)
    {
        if (handler is AnonymousFunctionExpressionSyntax f)
            return Returned(f);
        // A method group: Get, this.Get, or SummaryEndpoints.Get for a class declared in this file. A
        // qualified one is looked up in that class only, so another class's Get isn't mistaken for it.
        var root = handler.SyntaxTree.GetRoot();
        (string? name, SyntaxNode? scope) = handler switch
        {
            IdentifierNameSyntax i => (i.Identifier.ValueText, root),
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var n } =>
                (n.Identifier.ValueText, handler.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()),
            MemberAccessExpressionSyntax { Expression: var owner, Name: var n } =>
                (n.Identifier.ValueText, root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                    .FirstOrDefault(t => t.Identifier.ValueText == RightmostName(owner))),
            _ => ((string?)null, (SyntaxNode?)null),
        };
        if (name is null || scope is null)
            return [];
        return scope.DescendantNodes()
            .Where(n => n is MethodDeclarationSyntax m && m.Identifier.ValueText == name
                    && (scope is CompilationUnitSyntax || m.Parent == scope)
                || n is LocalFunctionStatementSyntax l && l.Identifier.ValueText == name)
            .SelectMany(Returned);
    }

    /// <summary>Expressions a lambda, method or local function returns, not those of functions nested in it.</summary>
    private static IEnumerable<ExpressionSyntax> Returned(SyntaxNode function)
    {
        var arrow = function switch
        {
            AnonymousFunctionExpressionSyntax f => f.ExpressionBody,
            MethodDeclarationSyntax m => m.ExpressionBody?.Expression,
            LocalFunctionStatementSyntax l => l.ExpressionBody?.Expression,
            _ => null,
        };
        if (arrow is not null)
            return [arrow];
        return function.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Where(r => r.Ancestors().First(a => a is AnonymousFunctionExpressionSyntax or BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax) == function)
            .Select(r => r.Expression).OfType<ExpressionSyntax>();
    }

    /// <summary>Anonymous objects a returned expression is itself, outside any Results call.</summary>
    private static int DirectAnonymousObjects(ExpressionSyntax returned, Func<InvocationExpressionSyntax, bool> isResultCall) =>
        DirectAnonymousObjects(returned, isResultCall, []);

    private static int DirectAnonymousObjects(ExpressionSyntax returned, Func<InvocationExpressionSyntax, bool> isResultCall, HashSet<SyntaxNode> helpers)
    {
        int Direct(ExpressionSyntax e) => DirectAnonymousObjects(e, isResultCall, helpers);
        switch (returned)
        {
            case ParenthesizedExpressionSyntax p: return Direct(p.Expression);
            case ConditionalExpressionSyntax c: return Direct(c.WhenTrue) + Direct(c.WhenFalse);
            case SwitchExpressionSyntax s: return s.Arms.Sum(a => Direct(a.Expression));
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } b: return Direct(b.Left) + Direct(b.Right);
            case AwaitExpressionSyntax a: return Direct(a.Expression);
            case IdentifierNameSyntax local:
                // A returned local is whatever it was set to, read the same way: a local holding a
                // helper's Results.BadRequest(...) is that result, not a raw body.
                var function = local.Ancestors().FirstOrDefault(n => n is AnonymousFunctionExpressionSyntax or BaseMethodDeclarationSyntax
                    or LocalFunctionStatementSyntax or AccessorDeclarationSyntax or CompilationUnitSyntax);
                if (function is null)
                    return 0;
                string name = local.Identifier.ValueText;
                var values = function.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .Where(v => v.Identifier.ValueText == name && v.Initializer is not null).Select(v => v.Initializer!.Value)
                    .Concat(function.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                        .Where(x => x.Left is IdentifierNameSyntax l && l.Identifier.ValueText == name).Select(x => x.Right));
                int fromLocal = 0;
                foreach (var value in values.Where(helpers.Add))
                {
                    fromLocal += Direct(value);
                    helpers.Remove(value);
                }
                return fromLocal;
            case InvocationExpressionSyntax call when IsSameFileHelper(call):
                // A same-file helper's result is what it returns, not what it was passed, read the
                // same way: a Results call it returns is counted at the helper's own site.
                int count = 0;
                foreach (var body in HelperBodies(call).Where(helpers.Add))
                {
                    count += Returned(body.Parent!).Sum(Direct);
                    helpers.Remove(body);
                }
                return count;
        }
        // Anything holding a Results call produces that result, whose body is counted at the call.
        // Any other expression (items.Select(x => new { ... }).ToList()) returns what was built in it.
        if (returned.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(isResultCall))
            return 0;
        return AnonymousObjects(returned, []);
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
                case LocalFunctionStatementSyntax f: return MappedRoute(f, f.Identifier.ValueText) ?? f.Identifier.ValueText;
                case MethodDeclarationSyntax m: return MappedRoute(m, m.Identifier.ValueText) ?? m.Identifier.ValueText;
                case ConstructorDeclarationSyntax c: return c.Identifier.ValueText;
                case PropertyDeclarationSyntax p: return p.Identifier.ValueText;
            }
        }
        return "(top level)";
    }

    /// <summary>
    /// The route a method or local function is mapped to as a method group (app.MapGet("/x", Handle)
    /// or SummaryEndpoints.Get), so its body is a route handler's like a lambda's is.
    /// </summary>
    private static string? MappedRoute(SyntaxNode function, string name)
    {
        string? owner = function.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
        foreach (var map in function.SyntaxTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (map.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: var verb }
                || !MapMethods.Contains(verb) || map.ArgumentList.Arguments.Count < 2)
                continue;
            bool names = map.ArgumentList.Arguments[^1].Expression switch
            {
                IdentifierNameSyntax i => i.Identifier.ValueText == name,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var n } => n.Identifier.ValueText == name,
                MemberAccessExpressionSyntax { Expression: var o, Name: var n } => n.Identifier.ValueText == name && RightmostName(o) == owner,
                _ => false,
            };
            if (names)
                return HandlerScan.RouteKey(map);
        }
        return null;
    }

    private static bool IsRouteSite(string site) =>
        site.Length > 0 && site.Split(' ')[0] is "GET" or "POST" or "PUT" or "PATCH" or "DELETE";

    /// <summary>
    /// How many anonymous objects an expression contributes to a response body: each one written in
    /// it (an outer body and a projection inside it are two), plus those reaching it through a local
    /// initialised or assigned from one, or through a same-file helper's return value.
    /// </summary>
    private static int AnonymousObjects(ExpressionSyntax arg, HashSet<SyntaxNode> visited)
    {
        // The nodes on the current path, not a fixed limit: a chain of any length is followed, a cycle
        // ends, and the same helper called twice counts twice.
        if (!visited.Add(arg))
            return 0;
        try
        {
            return CountAnonymousObjects(arg, visited);
        }
        finally
        {
            visited.Remove(arg);
        }
    }

    private static int CountAnonymousObjects(ExpressionSyntax arg, HashSet<SyntaxNode> visited)
    {
        int count = arg.DescendantNodesAndSelf().OfType<AnonymousObjectCreationExpressionSyntax>().Count();
        count += arg.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Sum(call => ReturnedAnonymousObjects(call, visited));
        // Locals anywhere in the argument, so Ok(flag ? body : null) follows body too.
        // The whole handler or member, so a local declared in an outer block and returned from a nested one is found.
        var body = arg.Ancestors().FirstOrDefault(a => a is AnonymousFunctionExpressionSyntax or BaseMethodDeclarationSyntax
            or LocalFunctionStatementSyntax or AccessorDeclarationSyntax or CompilationUnitSyntax);
        if (body is null)
            return count;
        foreach (string name in arg.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.ValueText).Distinct())
        {
            count += body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(v => v.Identifier.ValueText == name && v.Initializer is not null && !v.Initializer.Value.Span.Contains(arg.Span))
                .Sum(v => AnonymousObjects(v.Initializer!.Value, visited));
            count += body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.Left is IdentifierNameSyntax l && l.Identifier.ValueText == name && !a.Right.Span.Contains(arg.Span))
                .Sum(a => AnonymousObjects(a.Right, visited));
        }
        return count;
    }

    /// <summary>Anonymous objects returned by a same-file method or local function (unqualified, this. or ClassName.).</summary>
    private static int ReturnedAnonymousObjects(InvocationExpressionSyntax call, HashSet<SyntaxNode> visited)
    {
        int count = 0;
        foreach (var body in HelperBodies(call))
        {
            var returned = body is ArrowExpressionClauseSyntax arrow
                ? [arrow.Expression]
                : body.DescendantNodes().OfType<ReturnStatementSyntax>().Select(r => r.Expression).OfType<ExpressionSyntax>();
            count += returned.Sum(e => AnonymousObjects(e, visited));
        }
        return count;
    }

    /// <summary>The bodies (block or arrow) of the same-file methods and local functions a call can reach.</summary>
    private static IEnumerable<SyntaxNode> HelperBodies(InvocationExpressionSyntax call)
    {
        string? name = call.Expression switch
        {
            IdentifierNameSyntax i => i.Identifier.ValueText,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var n } => n.Identifier.ValueText,
            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver, Name: var n }
                when call.Ancestors().OfType<TypeDeclarationSyntax>().Any(t => t.Identifier.ValueText == receiver.Identifier.ValueText) => n.Identifier.ValueText,
            _ => null,
        };
        if (name is null)
            return [];
        var root = call.SyntaxTree.GetRoot();
        // Only overloads that can take this many arguments, so adding Shape(string) doesn't change
        // what Shape(1) is counted as returning.
        int args = call.ArgumentList.Arguments.Count;
        return root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == name && Accepts(m.ParameterList, args))
                .Select(m => (SyntaxNode?)m.ExpressionBody ?? m.Body)
            .Concat(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Where(f => f.Identifier.ValueText == name && Accepts(f.ParameterList, args))
                .Select(f => (SyntaxNode?)f.ExpressionBody ?? f.Body))
            .OfType<SyntaxNode>()
            .ToList();
    }

    private static bool IsSameFileHelper(InvocationExpressionSyntax call) =>
        call.Expression is IdentifierNameSyntax { Identifier.ValueText: var name }
        && call.SyntaxTree.GetRoot().DescendantNodes().Any(n =>
            n is MethodDeclarationSyntax m && m.Identifier.ValueText == name
            || n is LocalFunctionStatementSyntax l && l.Identifier.ValueText == name);

    private static bool Accepts(ParameterListSyntax parameters, int args)
    {
        var list = parameters.Parameters;
        if (list.Any(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword))))
            return args >= list.Count - 1 - list.Count(p => p.Default is not null);
        return args >= list.Count(p => p.Default is null) && args <= list.Count;
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
