using System.Reflection;

namespace Connapse.Architecture.Tests;

/// <summary>
/// Each rule returns today's violations as stable strings. The production tests compare them to a
/// committed list; the canary tests run the same rules over planted violations in this assembly.
/// </summary>
internal static class Rules
{
    public static readonly string[] WebSurfaceNamespaces = ["Connapse.Web.Endpoints", "Connapse.Web.Mcp"];

    /// <summary>
    /// Components that reach a store, DbContext or EF context factory: through [Inject] or a
    /// constructor, bare or wrapped (Lazy, Func, IEnumerable), or resolved at runtime from a
    /// provider or scope in their code blocks or code-behind.
    /// </summary>
    public static IEnumerable<string> ComponentStoreInjections(Assembly assembly, IEnumerable<(string Path, string Source)> componentSources)
    {
        var injected = StructureScan.Components(assembly)
            .SelectMany(c => StructureScan.ComponentDependencies(c).Where(StructureScan.IsStore)
                .Select(t => $"{ComponentName(c)} -> {StructureScan.Display(t)}"));
        var resolved = componentSources.SelectMany(f =>
        {
            var code = f.Path.EndsWith(".razor", StringComparison.Ordinal)
                ? RazorSource.ParseCodeBlocks(f.Source)
                : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(f.Source).GetRoot();
            return HandlerScan.ResolvedTypes(code).Where(HandlerScan.IsStore)
                .Select(t => $"{RazorSource.ComponentName(f.Path)} -> {HandlerScan.Display(t)} (resolved)");
        });
        return injected.Concat(resolved).Distinct();
    }

    /// <summary>
    /// One entry per route handler or MCP tool and store it reaches, whether as a parameter or
    /// resolved in its body. Per handler, so a second handler using an already-listed store is new.
    /// </summary>
    public static IEnumerable<string> HandlerStoreDependencies(IEnumerable<(string Path, string Source)> files) =>
        HandlerScan.Handlers(files.ToList())
            .SelectMany(h => h.Dependencies.Where(HandlerScan.IsStore).Select(t => $"{h.Name} -> {HandlerScan.Display(t)}"))
            .Distinct();

    /// <summary>Components are listed by their path under Components, so same-named files don't collide.</summary>
    private static string ComponentName(Type component)
    {
        const string prefix = "Connapse.Web.Components.";
        string full = component.FullName ?? component.Name;
        return full.StartsWith(prefix, StringComparison.Ordinal) ? full[prefix.Length..] : full;
    }
}
