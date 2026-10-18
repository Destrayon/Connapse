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
    /// Components that take a store, DbContext or EF context factory, through [Inject] or a
    /// constructor, bare or wrapped (Lazy, Func, IEnumerable).
    /// </summary>
    public static IEnumerable<string> ComponentStoreInjections(Assembly assembly) =>
        StructureScan.Components(assembly)
            .SelectMany(c => StructureScan.ComponentDependencies(c).Where(StructureScan.IsStore)
                .Select(t => $"{ComponentName(c)} -> {StructureScan.Display(t)}"))
            .Distinct();

    /// <summary>
    /// One entry per route handler or MCP tool and store it reaches, whether as a parameter or
    /// resolved in its body. Per handler, so a second handler using an already-listed store is new.
    /// </summary>
    public static IEnumerable<string> HandlerStoreDependencies(IEnumerable<(string Path, string Source)> files) =>
        files.SelectMany(f => HandlerScan.Handlers(f.Path, f.Source))
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
