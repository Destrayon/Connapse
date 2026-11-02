using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connapse.Architecture.Tests;

/// <summary>
/// One parse per source and one walk per tree, shared by every rule. The scanners look up a
/// helper, a method group or a map call many times per file; walking the whole tree for each
/// lookup made the two surface-wide rules too slow for the Small tier on CI.
/// </summary>
internal sealed class TreeIndex
{
    private static readonly ConcurrentDictionary<string, Lazy<SyntaxNode>> Parsed = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<SyntaxNode, TreeIndex> Indexes = new();

    private TreeIndex(SyntaxNode root)
    {
        var nodes = root.DescendantNodes().ToList();
        Invocations = nodes.OfType<InvocationExpressionSyntax>().ToList();
        Methods = nodes.OfType<MethodDeclarationSyntax>().ToList();
        MapCalls = Invocations.Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var verb }
            && SourceScan.MapMethods.Contains(verb) && i.ArgumentList.Arguments.Count >= 2).ToList();
        Types = nodes.OfType<TypeDeclarationSyntax>().ToList();
        MethodsByName = Methods.ToLookup(m => m.Identifier.ValueText, StringComparer.Ordinal);
        LocalFunctionsByName = nodes.OfType<LocalFunctionStatementSyntax>().ToLookup(f => f.Identifier.ValueText, StringComparer.Ordinal);
    }

    public IReadOnlyList<InvocationExpressionSyntax> Invocations { get; }
    /// <summary>MapGet, MapPost, MapPut, MapPatch and MapDelete calls with a handler argument.</summary>
    public IReadOnlyList<InvocationExpressionSyntax> MapCalls { get; }
    public IReadOnlyList<MethodDeclarationSyntax> Methods { get; }
    public IReadOnlyList<TypeDeclarationSyntax> Types { get; }
    public ILookup<string, MethodDeclarationSyntax> MethodsByName { get; }
    public ILookup<string, LocalFunctionStatementSyntax> LocalFunctionsByName { get; }

    public bool DeclaresFunction(string name) => MethodsByName.Contains(name) || LocalFunctionsByName.Contains(name);

    /// <summary>The root of a source, parsed once however many rules read it, even from tests running at once.</summary>
    public static SyntaxNode Parse(string source) =>
        Parsed.GetOrAdd(source, s => new Lazy<SyntaxNode>(() => CSharpSyntaxTree.ParseText(s, Options).GetRoot())).Value;

    /// <summary>Doc comments are kept as plain trivia: no rule reads them, and parsing their XML is most of the cost.</summary>
    private static readonly CSharpParseOptions Options = CSharpParseOptions.Default.WithDocumentationMode(DocumentationMode.None);

    private static readonly ConditionalWeakTable<SyntaxNode, LocalWrites> Writes = new();

    /// <summary>A function body's local initialisers and assignments, by the name they set.</summary>
    public sealed record LocalWrites(ILookup<string, ExpressionSyntax> Initialisers, ILookup<string, ExpressionSyntax> Assignments);

    /// <summary>Where each local in a body is given a value, walked once per body rather than once per name.</summary>
    public static LocalWrites WritesIn(SyntaxNode body) => Writes.GetValue(body, b => new LocalWrites(
        b.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(v => v.Initializer is not null)
            .ToLookup(v => v.Identifier.ValueText, v => v.Initializer!.Value, StringComparer.Ordinal),
        b.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.Left is IdentifierNameSyntax)
            .ToLookup(a => ((IdentifierNameSyntax)a.Left).Identifier.ValueText, a => a.Right, StringComparer.Ordinal)));

    /// <summary>The index of the tree a node belongs to.</summary>
    public static TreeIndex Of(SyntaxNode node) => Indexes.GetValue(node.SyntaxTree.GetRoot(), r => new TreeIndex(r));
}
