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
                : TreeIndex.Parse(f.Source);
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

    /// <summary>
    /// Operations no web route reaches: the route handler, or a helper it calls, takes the operation's class. The web
    /// API is the surface every client can script, so an action the MCP tools or pages run without a route is one a
    /// script can't. An exemption names the operation id and why it has no route; an exemption with no reason, for an
    /// operation that is routed after all, or for an id no operation has, is reported too, so the list can't rot.
    /// </summary>
    public static IEnumerable<string> UnroutedOperations(
        IEnumerable<Type> types, IEnumerable<(string Path, string Source)> files, IReadOnlyDictionary<string, string> exempt)
    {
        var routed = HandlerScan.Handlers(files.ToList())
            .Where(h => h.IsRoute)
            .SelectMany(h => h.Dependencies.Select(HandlerScan.Display))
            .Select(t => t.TrimEnd('?'))
            .ToHashSet(StringComparer.Ordinal);
        // Only a route taking the operation itself counts. One operation calling another doesn't: memory.copy calls
        // memory.remember to save the copy, and that never let a script save a fact of its own.
        var operations = types
            .Where(t => !t.IsAbstract)
            .Select(t => (Type: t, Attribute: t.GetCustomAttribute<Connapse.Application.OperationAttribute>(inherit: false)))
            .Where(o => o.Attribute is not null)
            .Select(o => (Id: o.Attribute!.Id, Routed: routed.Contains(o.Type.Name)))
            .ToList();

        foreach (var (id, isRouted) in operations.OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            if (!exempt.TryGetValue(id, out string? reason))
            {
                if (!isRouted)
                    yield return $"{id}: no web route";
            }
            else if (string.IsNullOrWhiteSpace(reason))
                yield return $"{id}: exempt without a reason";
            else if (isRouted)
                yield return $"{id}: exempt, but a web route reaches it";
        }
        foreach (string id in exempt.Keys.Where(k => operations.All(o => o.Id != k)).Order(StringComparer.Ordinal))
            yield return $"{id}: exempt, but no operation has this id";
    }

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

    /// <summary>
    /// MCP tool parameters that are neither an operation nor transport plumbing: a tool translates
    /// its arguments, calls the operation the API also calls, and renders the outcome, so anything
    /// else it takes from DI is a rule the API doesn't share.
    /// </summary>
    public static IEnumerable<string> McpToolDependencies(Assembly assembly) =>
        StructureScan.McpTools(assembly)
            .SelectMany(tool => tool.Method.GetParameters()
                .Select(p => p.ParameterType)
                .Where(t => !IsMcpToolParameter(t))
                .Select(t => $"{tool.Method.DeclaringType!.Name} {tool.Name} -> {StructureScan.Display(t)}"))
            .Distinct()
            .Order(StringComparer.Ordinal);

    private static bool IsMcpToolParameter(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t.IsArray)
            return t.GetElementType() is { } element && (element.IsPrimitive || element == typeof(string) || element == typeof(Guid));
        return t.IsDefined(typeof(Connapse.Application.OperationAttribute), inherit: false)
            || t.IsPrimitive || t.IsEnum
            || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid)
            || t == typeof(CancellationToken)
            || t.FullName == "Microsoft.AspNetCore.Http.IHttpContextAccessor"
            // A JSON argument, and the dispatcher the generic connapse_api_* tools call: it runs the
            // API's own route, so those tools share every rule the API applies (CON-20).
            || t == typeof(System.Text.Json.JsonElement)
            || t.FullName == "Connapse.Web.Services.GenericApi.ApiDispatcher";
    }
}
