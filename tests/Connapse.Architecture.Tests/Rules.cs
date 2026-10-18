using System.Reflection;

namespace Connapse.Architecture.Tests;

/// <summary>
/// Each rule returns today's violations as stable strings. The production tests compare them to a
/// committed list; the canary tests run the same rules over planted violations in this assembly.
/// </summary>
internal static class Rules
{
    public static readonly string[] WebSurfaceNamespaces = ["Connapse.Web.Endpoints", "Connapse.Web.Mcp"];

    /// <summary>Components that inject a store, a DbContext or an EF context factory.</summary>
    public static IEnumerable<string> ComponentStoreInjections(Assembly assembly) =>
        StructureScan.Components(assembly)
            .SelectMany(c => StructureScan.InjectedTypes(c).Where(StructureScan.IsStore)
                .Select(t => $"{ComponentName(c)} -> {StructureScan.Display(t)}"))
            .Distinct();

    /// <summary>Endpoint and MCP tool classes whose handlers take a store, DbContext or factory.</summary>
    public static IEnumerable<string> HandlerStoreDependencies(Assembly assembly, string[] namespaces) =>
        StructureScan.SurfaceHandlers(assembly, namespaces)
            .SelectMany(h => StructureScan.HandlerParameterTypes(h).Where(StructureScan.IsStore)
                .Select(t => $"{h.Name} -> {StructureScan.Display(t)}"))
            .Distinct();

    /// <summary>Components are listed by their path under Components, so same-named files don't collide.</summary>
    private static string ComponentName(Type component)
    {
        const string prefix = "Connapse.Web.Components.";
        string full = component.FullName ?? component.Name;
        return full.StartsWith(prefix, StringComparison.Ordinal) ? full[prefix.Length..] : full;
    }
}
