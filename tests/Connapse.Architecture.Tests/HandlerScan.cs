using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connapse.Architecture.Tests;

/// <summary>
/// Finds what each route handler and MCP tool depends on, one entry per route or tool, from source.
/// Keying by handler rather than by class means a second handler reaching the same store is a new
/// entry, and source text sees what reflection can't: services resolved inside a handler body or
/// inside a helper method the handler calls.
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
        var methods = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>().ToList();
        var locals = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().ToList();

        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: var verb } access || !MapMethods.Contains(verb))
                continue;
            var args = call.ArgumentList.Arguments;
            if (args.Count < 2)
                continue;
            string route = GroupPrefix(access.Expression, 0) + Literal(args[0].Expression);
            string name = $"{owner} {verb[3..].ToUpperInvariant()} {route}";
            var handler = args[^1].Expression;
            if (handler is AnonymousFunctionExpressionSyntax lambda)
                yield return new Handler(name, Dependencies(lambda, methods, locals));
            else if (RightmostName(handler) is { } target)
                foreach (var m in Named(methods, locals, target))
                    yield return new Handler(name, Dependencies(m, methods, locals));
        }

        foreach (var m in methods.OfType<MethodDeclarationSyntax>())
        {
            var tool = m.AttributeLists.SelectMany(a => a.Attributes).FirstOrDefault(a => RightmostName(a.Name) is "McpServerTool" or "McpServerToolAttribute");
            if (tool is null)
                continue;
            string toolName = tool.ArgumentList?.Arguments
                .FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "Name")?.Expression is LiteralExpressionSyntax n
                ? n.Token.ValueText : m.Identifier.ValueText;
            yield return new Handler($"{owner} {toolName}", Dependencies(m, methods, locals));
        }
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
    /// method of the same file it calls, transitively.
    /// </summary>
    private static List<TypeSyntax> Dependencies(SyntaxNode handler, List<BaseMethodDeclarationSyntax> methods, List<LocalFunctionStatementSyntax> locals)
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
                var name = call.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name,
                    SimpleNameSyntax sn => sn,
                    _ => null,
                };
                if (name is null)
                    continue;
                if (Resolvers.Contains(name.Identifier.ValueText))
                {
                    if (name is GenericNameSyntax g)
                        types.AddRange(g.TypeArgumentList.Arguments);
                    foreach (var arg in call.ArgumentList.Arguments)
                        if (arg.Expression is TypeOfExpressionSyntax t)
                            types.Add(t.Type);
                }
                else if (CallsOwnMethod(call))
                {
                    foreach (var helper in Named(methods, locals, name.Identifier.ValueText))
                        pending.Push(helper);
                }
            }
        }
        return types;
    }

    /// <summary>Unqualified, this., or ClassName. calls; store.GetAsync() must not match a helper named GetAsync.</summary>
    private static bool CallsOwnMethod(InvocationExpressionSyntax call) => call.Expression switch
    {
        SimpleNameSyntax => true,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } => true,
        MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver } =>
            call.Ancestors().OfType<TypeDeclarationSyntax>().Any(t => t.Identifier.ValueText == receiver.Identifier.ValueText),
        _ => false,
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

    private static IEnumerable<SyntaxNode> Named(List<BaseMethodDeclarationSyntax> methods, List<LocalFunctionStatementSyntax> locals, string name) =>
        methods.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == name).Cast<SyntaxNode>()
            .Concat(locals.Where(f => f.Identifier.ValueText == name));

    /// <summary>A store, DbContext or EF context factory, by name, anywhere in the type (Lazy&lt;ISourceStore&gt; counts).</summary>
    public static bool IsStore(TypeSyntax type) =>
        type.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Any(n =>
            n.Identifier.ValueText is var id && (id.EndsWith("Store", StringComparison.Ordinal)
                || id.EndsWith("DbContext", StringComparison.Ordinal) || id == "IDbContextFactory"));

    /// <summary>Store types a component resolves at runtime from a provider or scope in its code blocks.</summary>
    public static IEnumerable<TypeSyntax> ResolvedTypes(SyntaxNode code) =>
        code.DescendantNodes().OfType<InvocationExpressionSyntax>().SelectMany(call =>
        {
            var name = call.Expression switch { MemberAccessExpressionSyntax ma => ma.Name, SimpleNameSyntax sn => sn, _ => null };
            if (name is null || !Resolvers.Contains(name.Identifier.ValueText))
                return [];
            var found = new List<TypeSyntax>();
            if (name is GenericNameSyntax g)
                found.AddRange(g.TypeArgumentList.Arguments);
            found.AddRange(call.ArgumentList.Arguments.Select(a => a.Expression).OfType<TypeOfExpressionSyntax>().Select(t => t.Type));
            return found;
        });

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
