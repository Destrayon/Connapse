using System.Reflection;
using System.Text.Json;
using Connapse.Integration.Tests.Authorization;
using Connapse.Integration.Tests.CrossSurface;
using FluentAssertions;

namespace Connapse.Integration.Tests.Capabilities;

/// <summary>
/// Proof test for #791 (P1, epic #783): every capability Connapse offers is one operation, has
/// its REST route, and is reachable over MCP, or says why not. capabilities.json is the manifest;
/// every route, tool or operation it expects but the server doesn't have is a gap listed in
/// known-capability-gaps.json, and a ticket that closes one deletes its entry first.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CapabilityManifestTests(SharedWebAppFixture fixture)
{
    /// <summary>The Phase 5 tools that reach any operation marked for MCP, built from the API spec.</summary>
    public static readonly string[] GenericTools = ["connapse_api_search", "connapse_api_read", "connapse_api_write"];

    [Fact]
    public async Task CapabilityManifest_EveryCapability_HasOperationRouteAndTool()
    {
        var manifest = Manifest.Value;
        var routes = AuthzExpectations.Endpoints(fixture).Keys.ToHashSet(StringComparer.Ordinal);
        await using var mcp = await Surfaces.McpAsync(fixture);
        var tools = (await mcp.ListToolsAsync()).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var operations = OperationIds();

        // What exists must be claimed: a new route or tool needs a manifest row, or it slips past this test.
        var claimedRoutes = manifest.Capabilities.SelectMany(c => c.Routes).Concat(manifest.Plumbing.Keys).ToHashSet(StringComparer.Ordinal);
        routes.Where(r => r.Contains(" /api", StringComparison.Ordinal)).Except(claimedRoutes)
            .Should().BeEmpty("every /api route needs a row in capabilities.json, or an entry under plumbing saying why it isn't a capability");
        tools.Except(manifest.Capabilities.Select(c => c.Mcp).OfType<string>()).Except(GenericTools)
            .Should().BeEmpty("every MCP tool needs a row in capabilities.json");
        manifest.Capabilities.Where(c => (c.Mcp is null) == (c.McpOff is null))
            .Select(c => c.Id).Should().BeEmpty("a row either names its MCP exposure or says why it has none");
        manifest.Capabilities.Where(c => c.Routes.Count == 0)
            .Select(c => c.Id).Should().BeEmpty("every capability has a REST route");

        var gaps = new List<string>();
        foreach (var c in manifest.Capabilities)
        {
            if (!operations.Contains(c.Id))
                gaps.Add($"operation {c.Id}: missing");
            foreach (string route in c.Routes.Where(r => !routes.Contains(r)))
                gaps.Add($"route {c.Id}: missing {route}");
            foreach (string tool in ToolsFor(c).Where(t => !tools.Contains(t)))
                gaps.Add($"mcp {c.Id}: missing {tool}");
        }

        ShouldMatchKnown(gaps);
    }

    /// <summary>The tool a row needs: its own, or for "generic" the read or write tool that reaches its operation.</summary>
    private static IEnumerable<string> ToolsFor(CapabilityRow c) => c.Mcp switch
    {
        null => [],
        "generic" => [c.Routes.All(r => r.StartsWith("GET ", StringComparison.Ordinal)) ? "connapse_api_read" : "connapse_api_write"],
        _ => [c.Mcp],
    };

    /// <summary>
    /// Ids carried by an [Operation] attribute anywhere in Connapse's assemblies. Phase 2 adds the
    /// attribute; it's matched by name and its Id property so this test needs no reference to it.
    /// </summary>
    private static HashSet<string> OperationIds() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Connapse.", StringComparison.Ordinal) == true)
            .SelectMany(a => { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>(); } })
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MemberInfo>().Prepend(t))
            .SelectMany(m => m.GetCustomAttributes(inherit: false))
            .Where(a => a.GetType().Name == "OperationAttribute")
            .Select(a => a.GetType().GetProperty("Id")?.GetValue(a) as string)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    private static void ShouldMatchKnown(IEnumerable<string> found)
    {
        var actual = found.ToHashSet(StringComparer.Ordinal);
        var known = KnownGaps.Value.Keys.ToHashSet(StringComparer.Ordinal);
        var added = actual.Except(known).Order(StringComparer.Ordinal).ToList();
        var closed = known.Except(actual).Order(StringComparer.Ordinal).ToList();
        if (added.Count == 0 && closed.Count == 0)
            return;

        var message = new System.Text.StringBuilder();
        if (added.Count > 0)
            message.AppendLine($"{added.Count} new gap(s). Close them, or list them in known-capability-gaps.json with the reason:")
                .AppendLine(string.Join("\n", added.Select(a => "  " + JsonSerializer.Serialize(a) + ": \"\",")));
        if (closed.Count > 0)
            message.AppendLine($"{closed.Count} listed gap(s) are closed. Delete their entries from known-capability-gaps.json:")
                .AppendLine(string.Join("\n", closed.Select(c => "  " + c)));
        throw new Xunit.Sdk.XunitException(message.ToString());
    }

    public sealed record CapabilityRow(string Id, string Capability, IReadOnlyList<string> Routes, string? Mcp, string? McpOff, string? Note);
    public sealed record ManifestFile(IReadOnlyList<CapabilityRow> Capabilities, IReadOnlyDictionary<string, string> Plumbing);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly Lazy<ManifestFile> Manifest = new(() =>
        JsonSerializer.Deserialize<ManifestFile>(File.ReadAllText(PathOf("capabilities.json")), Json)!);

    private static readonly Lazy<IReadOnlyDictionary<string, string>> KnownGaps = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PathOf("known-capability-gaps.json")))!);

    private static string PathOf(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
                return Path.Combine(dir.FullName, "tests", "Connapse.Integration.Tests", "Capabilities", file);
        throw new InvalidOperationException("Connapse.slnx not found above the test output.");
    }
}
