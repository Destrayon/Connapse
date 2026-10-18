using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connapse.TestSuite.Tests;

/// <summary>Reads the test suite's own source, so every check here works without building or loading it.</summary>
internal static class TestSources
{
    private static readonly Lazy<string> RootPath = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"No Connapse.slnx above {AppContext.BaseDirectory}");
    });

    public static string Root => RootPath.Value;

    public static string Baseline(string fileName) =>
        Path.Combine(Root, "tests", "Connapse.TestSuite.Tests", "Baselines", fileName);

    private static readonly Lazy<IReadOnlyList<(string RelativePath, SyntaxNode Root)>> Parsed = new(() => Load().AsParallel().AsOrdered().Select(f => (f.RelativePath, CSharpSyntaxTree.ParseText(f.Text, path: f.Path).GetRoot())).ToList());

    /// <summary>Every C# file under tests/, minus build output and folders the solution doesn't build. Parsed once.</summary>
    public static IReadOnlyList<(string RelativePath, SyntaxNode Root)> Files() => Parsed.Value;

    private static readonly Lazy<IReadOnlyList<(string RelativePath, SyntaxNode Root)>> ParsedTests = new(() =>
        Load().Where(f => f.Text.Contains("[Fact", StringComparison.Ordinal) || f.Text.Contains("[Theory", StringComparison.Ordinal))
            .AsParallel().AsOrdered()
            .Select(f => (f.RelativePath, CSharpSyntaxTree.ParseText(f.Text, path: f.Path).GetRoot())).ToList());

    /// <summary>
    /// Only files that declare tests, found by a text search before parsing so this suite stays in
    /// the Small tier. A file whose only tests use another spelling would be missed, so the guard
    /// test checks known test files are included.
    /// </summary>
    public static IReadOnlyList<(string RelativePath, SyntaxNode Root)> TestFiles() => ParsedTests.Value;

    private static IEnumerable<(string RelativePath, string Path, string Text)> Load()
    {
        string tests = Path.Combine(Root, "tests");
        foreach (string file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(Root, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.Contains("/TestResults/") || !InSolution(rel))
                continue;
            yield return (rel, file, File.ReadAllText(file));
        }
    }

    /// <summary>tests/AIKnowledge.* predate the rename and aren't in Connapse.slnx, so nothing runs them.</summary>
    private static bool InSolution(string rel) => !rel.StartsWith("tests/AIKnowledge.", StringComparison.Ordinal);

    public static bool IsTestMethod(MethodDeclarationSyntax m) =>
        m.AttributeLists.SelectMany(a => a.Attributes).Any(a => AttributeName(a) is "Fact" or "Theory");

    /// <summary>The (name, value) pairs of [Trait("name", "value")] attributes with literal arguments.</summary>
    public static IEnumerable<(string Name, string Value)> Traits(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (var attr in lists.SelectMany(a => a.Attributes))
        {
            if (AttributeName(attr) != "Trait" || attr.ArgumentList is not { Arguments.Count: 2 } args)
                continue;
            if (args.Arguments[0].Expression is LiteralExpressionSyntax name && args.Arguments[1].Expression is LiteralExpressionSyntax value)
                yield return (name.Token.ValueText, value.Token.ValueText);
            else
                yield return ("<non-literal>", attr.ToString());
        }
    }

    private static string AttributeName(AttributeSyntax a)
    {
        string name = a.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            SimpleNameSyntax s => s.Identifier.ValueText,
            _ => a.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }
}
