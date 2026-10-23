using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connapse.TestSuite.Tests;

/// <summary>Reads the test suite's own source, so every check here works without building or loading it.</summary>
internal static partial class TestSources
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

    /// <summary>
    /// Class-level traits of each partial class, combined across all its declarations in a test
    /// project. Files are found by text ("partial"), so declarations without tests are included.
    /// </summary>
    public static readonly Lazy<IReadOnlyDictionary<string, List<(string Name, string Value)>>> PartialClassTraits = new(() =>
        PartialTraitsOf(Load().Where(f => f.Text.Contains("partial", StringComparison.Ordinal))
            .Select(f => (f.RelativePath, CSharpSyntaxTree.ParseText(f.Text, path: f.Path).GetRoot()))));

    public static IReadOnlyDictionary<string, List<(string Name, string Value)>> PartialTraitsOf(IEnumerable<(string RelativePath, SyntaxNode Root)> files)
    {
        var combined = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
        foreach (var (rel, root) in files)
            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Modifiers.Any(m => m.ValueText == "partial")))
            {
                string key = ClassKey(rel, cls);
                if (!combined.TryGetValue(key, out var list))
                    combined[key] = list = [];
                list.AddRange(Traits(cls.AttributeLists));
            }
        return combined;
    }

    /// <summary>"Project|Namespace.Outer.Class": one compiled class, however many files declare it.</summary>
    public static string ClassKey(string relativePath, ClassDeclarationSyntax cls)
    {
        string project = relativePath.Split('/') is { Length: > 1 } parts ? parts[1] : relativePath;
        string ns = cls.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "";
        var names = cls.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Select(t => t.Identifier.ValueText).Reverse();
        return $"{project}|{ns}.{string.Join(".", names)}";
    }

    public static bool IsTestMethod(MethodDeclarationSyntax m, IReadOnlyCollection<string> testAttributes) =>
        m.AttributeLists.SelectMany(a => a.Attributes).Select(a => Strip(RightmostName(a.Name)))
            .Any(n => testAttributes.Contains(n) || n.EndsWith("Fact", StringComparison.Ordinal) || n.EndsWith("Theory", StringComparison.Ordinal));

    private static string RightmostName(NameSyntax name) => name switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => name.ToString(),
    };

    /// <summary>
    /// Fact and Theory, anything named ...Fact or ...Theory (SkippableFact), and every attribute class in
    /// the test sources that derives from one of those, however it is named.
    /// </summary>
    public static readonly Lazy<HashSet<string>> TestAttributeNames = new(() =>
    {
        // Found by text, not by parsing every file, so this stays cheap.
        var derived = Load().SelectMany(f => ClassWithBase().Matches(f.Text))
            .Select(m => (Name: Strip(m.Groups["name"].Value), Bases: m.Groups["bases"].Value.Split(',').Select(b => Strip(b.Trim().Split('.')[^1].Split('<')[0])).ToList()))
            .ToList();
        var names = new HashSet<string>(StringComparer.Ordinal) { "Fact", "Theory" };
        bool added = true;
        while (added)
        {
            added = false;
            foreach (var (name, bases) in derived)
                if (bases.Any(b => names.Contains(b) || b.EndsWith("Fact", StringComparison.Ordinal) || b.EndsWith("Theory", StringComparison.Ordinal)) && names.Add(name))
                    added = true;
        }
        return names;
    });

    private static readonly Lazy<IReadOnlyList<(string RelativePath, SyntaxNode Root)>> ParsedTests = new(() =>
        Load().Where(f => f.Text.Contains("Fact", StringComparison.Ordinal) || f.Text.Contains("Theory", StringComparison.Ordinal)
                || TestAttributeNames.Value.Any(n => f.Text.Contains(n, StringComparison.Ordinal)))
            .AsParallel().AsOrdered()
            .Select(f => (f.RelativePath, CSharpSyntaxTree.ParseText(f.Text, path: f.Path).GetRoot())).ToList());

    /// <summary>
    /// Files that may declare tests, found by a text search for "Fact" or "Theory" before parsing so
    /// this suite stays fast. Every test attribute's name contains one of the two, whether written
    /// [Fact], [Xunit.Fact], [SkippableFact] or a subclass named after them; a subclass named
    /// otherwise is found by <see cref="TestAttributeNames"/> and its file is parsed too.
    /// </summary>
    public static IReadOnlyList<(string RelativePath, SyntaxNode Root)> TestFiles() => ParsedTests.Value;

    [System.Text.RegularExpressions.GeneratedRegex(@"\bclass\s+(?<name>\w+)\s*:\s*(?<bases>[\w.<>,\s]+?)\s*(?:\{|where\b)")]
    private static partial System.Text.RegularExpressions.Regex ClassWithBase();

    private static string Strip(string name) => name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;

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
        return Strip(name);
    }
}
