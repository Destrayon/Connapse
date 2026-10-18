using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connapse.Architecture.Tests;

/// <summary>
/// Finds what each route handler and MCP tool depends on, one entry per route or tool, from source.
/// Keying by handler rather than by class means a second handler reaching the same store is a new
/// entry, and source text sees what reflection can't: services resolved inside a handler body, in
/// a same-file helper it calls, or in a static helper in another web file.
/// </summary>
/// <remarks>
/// Instance services a handler receives from DI are not followed. Moving data access behind a
/// service is the migration this rule exists to drive; telling a thin store wrapper from a real
/// operation needs the [Operation] metadata Phase 2 of epic #783 adds.
/// </remarks>
internal static class HandlerScan
{
    private static readonly HashSet<string> MapMethods = ["MapGet", "MapPost", "MapPut", "MapPatch", "MapDelete"];
    private static readonly HashSet<string> Resolvers =
        ["GetService", "GetRequiredService", "GetKeyedService", "GetRequiredKeyedService", "GetServices", "GetKeyedServices"];

    public sealed record Handler(string Name, IReadOnlyList<TypeSyntax> Dependencies);

    /// <summary>Every route lambda or method group and every [McpServerTool] method across the given files.</summary>
    public static IEnumerable<Handler> Handlers(IReadOnlyList<(string Path, string Source)> files)
    {
        var parsed = files.Select(f => (f.Path, Root: CSharpSyntaxTree.ParseText(f.Source).GetRoot())).ToList();
        var index = new Index(parsed.Select(p => p.Root));

        foreach (var (path, root) in parsed)
        {
            string owner = Path.GetFileNameWithoutExtension(path);
            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: var verb } || !MapMethods.Contains(verb)
                    || call.ArgumentList.Arguments.Count < 2)
                    continue;
                string name = $"{owner} {RouteKey(call)}";
                var handler = call.ArgumentList.Arguments[^1].Expression;
                if (handler is AnonymousFunctionExpressionSyntax lambda)
                    yield return new Handler(name, Dependencies(lambda, index));
                else if (handler is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax type, Name: var method })
                    foreach (var m in index.Static(type.Identifier.ValueText, method.Identifier.ValueText))
                        yield return new Handler(name, Dependencies(m, index));
                else if (RightmostName(handler) is { } target)
                    foreach (var m in index.SameFile(call, target))
                        yield return new Handler(name, Dependencies(m, index));
            }

            foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var tool = m.AttributeLists.SelectMany(a => a.Attributes).FirstOrDefault(a => RightmostName(a.Name) is "McpServerTool" or "McpServerToolAttribute");
                if (tool is null)
                    continue;
                string toolName = tool.ArgumentList?.Arguments
                    .FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "Name")?.Expression is LiteralExpressionSyntax n
                    ? n.Token.ValueText : m.Identifier.ValueText;
                yield return new Handler($"{owner} {toolName}", Dependencies(m, index));
            }
        }
    }

    /// <summary>"VERB /full/group/path/route" for a MapGet/MapPost/... call.</summary>
    public static string RouteKey(InvocationExpressionSyntax mapCall)
    {
        var access = (MemberAccessExpressionSyntax)mapCall.Expression;
        string verb = access.Name.Identifier.ValueText[3..].ToUpperInvariant();
        return $"{verb} {GroupPrefix(access.Expression, 0)}{Literal(mapCall.ArgumentList.Arguments[0].Expression)}";
    }

    /// <summary>
    /// The route-group prefix a Map call is made on: <c>var group = app.MapGroup("/api/x")</c>, then
    /// <c>group.MapGet("/", ...)</c> is "/api/x/". Without it, two groups' "GET /" share one key.
    /// </summary>
    private static string GroupPrefix(ExpressionSyntax receiver, int depth)
    {
        if (depth > 8)
            return "";
        switch (receiver)
        {
            case InvocationExpressionSyntax inv when inv.Expression is MemberAccessExpressionSyntax ma:
                return ma.Name.Identifier.ValueText == "MapGroup" && inv.ArgumentList.Arguments.Count > 0
                    ? GroupPrefix(ma.Expression, depth + 1) + Literal(inv.ArgumentList.Arguments[0].Expression)
                    : GroupPrefix(ma.Expression, depth + 1); // .WithTags(...), .RequireAuthorization(...)
            case IdentifierNameSyntax id:
                var scope = receiver.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or CompilationUnitSyntax);
                var declarator = scope?.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault(v => v.Identifier.ValueText == id.Identifier.ValueText && v.Initializer is not null);
                return declarator is null ? "" : GroupPrefix(declarator.Initializer!.Value, depth + 1);
            default:
                return "";
        }
    }

    /// <summary>
    /// Parameter types and types resolved through a service provider, in the handler and in every
    /// helper it calls, transitively: same-file methods, and static methods on any web class.
    /// </summary>
    private static List<TypeSyntax> Dependencies(SyntaxNode handler, Index index)
    {
        var types = new List<TypeSyntax>();
        var visited = new HashSet<SyntaxNode>();
        var pending = new Stack<SyntaxNode>([handler]);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!visited.Add(node))
                continue;
            types.AddRange(Parameters(node).Select(p => p.Type).OfType<TypeSyntax>());
            foreach (var call in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = CalledName(call);
                if (name is null)
                    continue;
                if (Resolvers.Contains(name.Identifier.ValueText))
                {
                    types.AddRange(ResolvedBy(call, name));
                    continue;
                }
                foreach (var helper in index.Callees(call, name.Identifier.ValueText))
                    pending.Push(helper);
            }
        }
        return types;
    }

    /// <summary>Store types a component resolves at runtime from a provider or scope in its code blocks.</summary>
    public static IEnumerable<TypeSyntax> ResolvedTypes(SyntaxNode code) =>
        code.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .SelectMany(call => CalledName(call) is { } name && Resolvers.Contains(name.Identifier.ValueText) ? ResolvedBy(call, name) : []);

    private static IEnumerable<TypeSyntax> ResolvedBy(InvocationExpressionSyntax call, SimpleNameSyntax name)
    {
        if (name is GenericNameSyntax g)
            foreach (var t in g.TypeArgumentList.Arguments)
                yield return t;
        foreach (var arg in call.ArgumentList.Arguments)
            if (arg.Expression is TypeOfExpressionSyntax t)
                yield return t.Type;
    }

    private static SimpleNameSyntax? CalledName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Name,
        SimpleNameSyntax sn => sn,
        _ => null,
    };

    private static IEnumerable<ParameterSyntax> Parameters(SyntaxNode node) => node switch
    {
        ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters,
        SimpleLambdaExpressionSyntax s => [s.Parameter],
        AnonymousMethodExpressionSyntax a => a.ParameterList?.Parameters.AsEnumerable() ?? [],
        BaseMethodDeclarationSyntax m => m.ParameterList.Parameters,
        LocalFunctionStatementSyntax f => f.ParameterList.Parameters,
        _ => [],
    };

    /// <summary>Methods a call can reach: same-file methods and local functions, and static methods on web classes.</summary>
    private sealed class Index
    {
        private readonly Dictionary<(string Type, string Method), List<MethodDeclarationSyntax>> _statics = [];

        public Index(IEnumerable<SyntaxNode> roots)
        {
            foreach (var m in roots.SelectMany(r => r.DescendantNodes().OfType<MethodDeclarationSyntax>()))
            {
                if (!m.Modifiers.Any(SyntaxKind.StaticKeyword) || m.Parent is not TypeDeclarationSyntax type)
                    continue;
                var key = (type.Identifier.ValueText, m.Identifier.ValueText);
                if (!_statics.TryGetValue(key, out var list))
                    _statics[key] = list = [];
                list.Add(m);
            }
        }

        /// <summary>
        /// Unqualified and this. calls resolve to the same file. ClassName.Method resolves to a
        /// static method on that web class, in any file. store.GetAsync() reaches nothing here.
        /// </summary>
        public IEnumerable<SyntaxNode> Callees(InvocationExpressionSyntax call, string name) => call.Expression switch
        {
            SimpleNameSyntax => SameFile(call, name),
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } => SameFile(call, name),
            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver } =>
                _statics.TryGetValue((receiver.Identifier.ValueText, name), out var list) ? list : [],
            _ => [],
        };

        public IEnumerable<SyntaxNode> Static(string type, string method) =>
            _statics.TryGetValue((type, method), out var list) ? list : [];

        public IEnumerable<SyntaxNode> SameFile(SyntaxNode from, string name)
        {
            var root = from.SyntaxTree.GetRoot();
            return root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == name).Cast<SyntaxNode>()
                .Concat(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Where(f => f.Identifier.ValueText == name));
        }
    }

    /// <summary>A store, DbContext or EF context factory, by name, anywhere in the type (Lazy&lt;ISourceStore&gt; counts).</summary>
    public static bool IsStore(TypeSyntax type) =>
        type.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Any(n =>
            n.Identifier.ValueText is var id && (id.EndsWith("Store", StringComparison.Ordinal)
                || id.EndsWith("DbContext", StringComparison.Ordinal) || id == "IDbContextFactory"));

    public static bool IsServiceLocator(TypeSyntax type) =>
        type.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Any(n =>
            n.Identifier.ValueText is "IServiceProvider" or "IServiceScopeFactory" or "IServiceScope");

    /// <summary>The type without namespaces, so entries don't depend on how a file imports it.</summary>
    public static string Display(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => Display(q.Right),
        AliasQualifiedNameSyntax a => Display(a.Name),
        GenericNameSyntax g => $"{g.Identifier.ValueText}<{string.Join(", ", g.TypeArgumentList.Arguments.Select(Display))}>",
        NullableTypeSyntax n => Display(n.ElementType) + "?",
        _ => type.ToString(),
    };

    private static string Literal(ExpressionSyntax e) => e is LiteralExpressionSyntax lit ? lit.Token.ValueText : e.ToString();

    private static string? RightmostName(SyntaxNode node) => node switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        _ => null,
    };
}
