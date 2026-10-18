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
            var typed = HandlerScan.ResolvedTypes(code).Where(HandlerScan.IsStore).Select(HandlerScan.Display);
            // Markup expressions never reach a code block, so .razor files are also searched as text.
            var markup = f.Path.EndsWith(".razor", StringComparison.Ordinal)
                ? RazorSource.ResolvedTypeNames(f.Source).Where(IsStoreName)
                : [];
            return typed.Concat(markup).Select(t => $"{RazorSource.ComponentName(f.Path)} -> {t} (resolved)");
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

    private static bool IsStoreName(string type) =>
        System.Text.RegularExpressions.Regex.IsMatch(type, @"(Store|DbContext)\b|\bIDbContextFactory\b");

    /// <summary>
    /// Service location taken from DI: a provider or scope factory injected into a component, or
    /// taken by a route handler or MCP tool. One entry per component or handler.
    /// </summary>
    public static IEnumerable<string> ServiceLocatorDependencies(Assembly assembly, IEnumerable<(string Path, string Source)> files)
    {
        var components = StructureScan.Components(assembly)
            .SelectMany(c => StructureScan.ComponentDependencies(c).Where(StructureScan.IsServiceLocator)
                .Select(t => $"{ComponentName(c)} -> {StructureScan.Display(t)}"));
        var handlers = HandlerScan.Handlers(files.ToList())
            .SelectMany(h => h.Dependencies.Where(HandlerScan.IsServiceLocator).Select(t => $"{h.Name} -> {HandlerScan.Display(t)}"));
        return components.Concat(handlers).Distinct();
    }

    /// <summary>
    /// Inline service-location calls, counted per site: "Endpoints/X.cs: POST /api/y uses GetRequiredService x2".
    /// Surface files count wherever the call is; other files count only inside a route handler.
    /// </summary>
    public static IEnumerable<string> ServiceLocationCalls(string relativePath, string source, bool wholeFile = true,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? routes = null) =>
        SourceScan.ServiceLocationCalls(source, wholeFile, routes)
            .GroupBy(c => (c.Site, c.Call))
            .Select(g => $"{relativePath}: {g.Key.Site} uses {g.Key.Call}{(g.Count() > 1 ? $" x{g.Count()}" : "")}");

    /// <summary>Anonymous response bodies, counted per site: "Endpoints/X.cs: GET /api/y: 2".</summary>
    public static IEnumerable<string> AnonymousResponses(string relativePath, string source, IReadOnlyDictionary<string, IReadOnlyList<string>>? routes = null) =>
        SourceScan.AnonymousResponseSites(source, routes)
            .GroupBy(site => site)
            .Select(g => $"{relativePath}: {g.Key}: {g.Count()}");

    /// <summary>Components are listed by their path under Components, so same-named files don't collide.</summary>
    private static string ComponentName(Type component)
    {
        const string prefix = "Connapse.Web.Components.";
        string full = component.FullName ?? component.Name;
        return full.StartsWith(prefix, StringComparison.Ordinal) ? full[prefix.Length..] : full;
    }
}
