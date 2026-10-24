using System.Text.Json;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

// Usage: RoslynIndex <repo-root> <solution> <out.json>
//
// Every type, method, property, field and event a C# solution declares, with where it is declared, the
// lines its declaration spans, every line that names it, and the in-repo symbols its body names.
// References are what Roslyn binds each name to, so an overload or a same-named member elsewhere
// is never confused with it. A multi-targeted project loads once per framework; duplicates are merged.
if (args.Length != 3)
{
    Console.Error.WriteLine("usage: RoslynIndex <repo-root> <solution> <out.json>");
    return 2;
}

MSBuildLocator.RegisterDefaults();
return await Indexer.RunAsync(Path.GetFullPath(args[0]), args[1], args[2]);

internal static class Indexer
{
    private sealed class Entry
    {
        public required string Kind { get; init; }
        public required string Qualified { get; init; }
        public required string Noun { get; init; }
        public required string Path { get; init; }
        public required int Line { get; init; }
        public required int[] Body { get; init; }
        public SortedSet<(string Path, int Line)> Refs { get; } = [];
        public SortedSet<string> Uses { get; } = new(StringComparer.Ordinal);
    }

    public static async Task<int> RunAsync(string root, string solutionFile, string output)
    {
        using var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e => Console.Error.WriteLine($"workspace: {e.Diagnostic.Message}"));
        var solution = await workspace.OpenSolutionAsync(System.IO.Path.Combine(root, solutionFile));

        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var errorFiles = new SortedSet<string>(StringComparer.Ordinal);
        var models = new List<(Document Document, SemanticModel Model, SyntaxNode Root)>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            var errors = compilation?.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList() ?? [];
            Console.Error.WriteLine($"{project.Name}: {project.Documents.Count()} documents, {errors.Count} compile errors");
            foreach (var error in errors.GroupBy(e => e.Id).OrderByDescending(g => g.Count()).Take(3))
                Console.Error.WriteLine($"  {error.Count()} x {error.Key}: {error.First().GetMessage()}");
            // A name in a file that doesn't compile may not bind, so its uses there can be missing.
            foreach (var error in errors)
                if (Relative(root, error.Location.SourceTree?.FilePath) is { } file)
                    errorFiles.Add(file);
            foreach (var document in project.Documents)
            {
                if (Relative(root, document.FilePath) is not { } path || path.Contains("/obj/") || path.Contains("/bin/"))
                    continue;
                var model = await document.GetSemanticModelAsync();
                var syntax = await document.GetSyntaxRootAsync();
                if (model is null || syntax is null)
                    continue;
                models.Add((document, model, syntax));
                foreach (var node in syntax.DescendantNodes().Where(IsDeclaration))
                {
                    if (model.GetDeclaredSymbol(node) is not { } symbol || Id(symbol) is not { } id || Describe(symbol) is not var (kind, noun))
                        continue;
                    var span = node.GetLocation().GetLineSpan();
                    entries.TryAdd(id, new Entry
                    {
                        Kind = kind,
                        Noun = noun,
                        Qualified = symbol.ContainingType is { } owner ? $"{owner.Name}.{symbol.Name}" : symbol.Name,
                        Path = path,
                        Line = span.StartLinePosition.Line + 1,
                        Body = [span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1],
                    });
                }
            }
        }

        // One pass over every name: what it binds to, and the declared member whose body it sits in.
        foreach (var (document, model, syntax) in models)
        {
            string path = Relative(root, document.FilePath)!;
            foreach (var name in syntax.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                // `var` binds to the type it infers, but the code never names that type there.
                if (name is IdentifierNameSyntax { IsVar: true })
                    continue;
                var info = model.GetSymbolInfo(name);
                var target = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
                if (target is IMethodSymbol { ReducedFrom: { } extension })
                    target = extension;
                if (target is null || Id(target.OriginalDefinition) is not { } id || !entries.TryGetValue(id, out var entry))
                    continue;
                entry.Refs.Add((path, name.GetLocation().GetLineSpan().StartLinePosition.Line + 1));

                var owner = name.Ancestors().FirstOrDefault(IsDeclaration);
                if (owner is not null && model.GetDeclaredSymbol(owner) is { } user && Id(user) is { } userId
                    && userId != id && entries.TryGetValue(userId, out var userEntry)
                    && !SymbolEqualityComparer.Default.Equals(target.OriginalDefinition, user.ContainingType))
                    userEntry.Uses.Add(id);
            }
        }

        var records = entries.Select(e => new
        {
            symbol = e.Key, kind = e.Value.Kind, qualified = e.Value.Qualified, noun = e.Value.Noun,
            path = e.Value.Path, line = e.Value.Line, body = e.Value.Body,
            refs = e.Value.Refs.Select(r => new object[] { r.Path, r.Line }), uses = e.Value.Uses,
        }).OrderBy(r => r.symbol, StringComparer.Ordinal);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { symbols = records, errorFiles }));
        Console.Error.WriteLine($"{entries.Count} symbols written to {output}");
        return 0;
    }

    private static bool IsDeclaration(SyntaxNode node) => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax
        or MethodDeclarationSyntax or PropertyDeclarationSyntax or EventDeclarationSyntax or VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax };

    private static (string Kind, string Noun)? Describe(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol => ("type", "type"),
        IMethodSymbol { MethodKind: MethodKind.Ordinary } => ("callable", "method"),
        IPropertySymbol or IFieldSymbol or IEventSymbol => ("term", "member"),
        _ => null,
    };

    // Unique per declaration, overloads included; null for symbols outside the solution's source.
    private static string? Id(ISymbol symbol) =>
        symbol.Locations.Any(l => l.IsInSource) ? symbol.GetDocumentationCommentId() : null;

    private static string? Relative(string root, string? file) =>
        file is null ? null : System.IO.Path.GetRelativePath(root, file).Replace('\\', '/') is var p && !p.StartsWith("..", StringComparison.Ordinal) ? p : null;
}
