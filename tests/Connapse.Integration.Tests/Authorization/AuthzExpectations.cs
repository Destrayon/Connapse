using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests.Authorization;

/// <summary>
/// What one route or MCP tool should demand. <see cref="Requires"/> is who may call it:
/// <c>anonymous</c>, <c>user</c> (any signed-in person, not an agent), a minimum role (<c>viewer</c>,
/// <c>editor</c>, <c>admin</c>, <c>owner</c>), or <c>mcp</c> (the MCP transport: an admin, an owner or
/// an agent). <see cref="Scope"/> is the scope a scoped token needs; null means no scoped token may
/// call it. <see cref="Risk"/> is <c>read</c>, <c>write</c>, <c>destructive</c> or <c>credential</c>.
/// </summary>
public sealed record AuthzExpectation(string? Requires, string? Scope, string Risk);

public sealed record AuthzTable(
    IReadOnlyDictionary<string, AuthzExpectation> Routes,
    IReadOnlyDictionary<string, AuthzExpectation> Tools);

/// <summary>
/// The committed expectations table and known-violations list, and the live endpoints they are
/// checked against. Both files are the proof: an endpoint missing from the table fails, and a
/// listed violation that no longer happens fails too, so the list can only shrink deliberately.
/// </summary>
public static class AuthzExpectations
{
    public static readonly string[] Levels = ["anonymous", "user", "viewer", "editor", "admin", "owner", "mcp"];
    public static readonly string[] Risks = ["read", "write", "destructive", "credential"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static readonly Lazy<AuthzTable> Table = new(() =>
        JsonSerializer.Deserialize<AuthzTable>(File.ReadAllText(PathOf("authorization-expectations.json")), Json)!);

    /// <summary>Every known violation, each with the reason it is accepted for now.</summary>
    public static readonly Lazy<IReadOnlyDictionary<string, string>> KnownViolations = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PathOf("known-authz-violations.json")))!);

    /// <summary>
    /// The host's routes keyed as "METHODS pattern", with static files left out: those are served
    /// to anyone by design and carry no authorization metadata to check.
    /// </summary>
    public static IReadOnlyDictionary<string, RouteEndpoint> Endpoints(SharedWebAppFixture fixture) =>
        fixture.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => !IsStaticFile(e))
            .ToDictionary(Key, e => e, StringComparer.Ordinal);

    public static string Key(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        return $"{(methods is { Count: > 0 } ? string.Join(",", methods) : "*")} {endpoint.RoutePattern.RawText}";
    }

    /// <summary>
    /// What the endpoint's metadata demands, in the table's words. No authorization metadata and
    /// [AllowAnonymous] both read as anonymous, since either lets anyone in.
    /// </summary>
    public static string Declared(Endpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return "anonymous";

        var data = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        var policies = endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>();
        if (data.Count == 0 && policies.Count == 0)
            return "anonymous";

        var named = data.Select(d => d.Policy).OfType<string>().Distinct().Order().ToList();
        if (named.Count == 0 && policies.Count == 0 && data.All(d => string.IsNullOrEmpty(d.Roles)))
            return "user";

        return string.Join("+", named.Select(p => p switch
        {
            "RequirePerson" => "user",
            // A person or their token, never an agent key: the matrix's "user" (#865).
            "RequireAccount" => "user",
            "RequireViewer" => "viewer",
            "RequireEditor" => "editor",
            "RequireAdmin" => "admin",
            // An administrator, whose token also needs admin:audit (#880).
            "RequireAuditReader" => "admin",
            "RequireOwner" => "owner",
            "RequireAgent" => "mcp",
            _ => p,
        }).Concat(data.Select(d => d.Roles).OfType<string>().Where(r => r.Length > 0).Select(r => $"roles:{r}"))
          .Concat(policies.Count > 0 ? ["inline-policy"] : []));
    }

    /// <summary>
    /// A file MapStaticAssets serves from the build's asset manifest. Recognised by the descriptor
    /// it attaches, not by the path's shape, so an application route that looks like a file name
    /// still has to be in the table.
    /// </summary>
    private static bool IsStaticFile(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.StaticAssets.StaticAssetDescriptor>() is not null;

    private static string PathOf(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
                return Path.Combine(dir.FullName, "tests", "Connapse.Integration.Tests", "Authorization", file);
        throw new InvalidOperationException("Connapse.slnx not found above the test output.");
    }

    /// <summary>
    /// Fails unless the violations found equal the listed ones with <paramref name="prefix"/>,
    /// naming the new ones (fix them or list them) and the stale ones (delete their entries).
    /// </summary>
    public static void ShouldMatchKnown(IEnumerable<string> found, string prefix)
    {
        var actual = found.ToHashSet(StringComparer.Ordinal);
        var known = KnownViolations.Value.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);

        var added = actual.Except(known).Order(StringComparer.Ordinal).ToList();
        var fixedOnes = known.Except(actual).Order(StringComparer.Ordinal).ToList();

        if (added.Count == 0 && fixedOnes.Count == 0)
            return;

        var message = new System.Text.StringBuilder();
        if (added.Count > 0)
            message.AppendLine($"{added.Count} new violation(s). Fix them, or list them in known-authz-violations.json with the reason:")
                .AppendLine(string.Join("\n", added.Select(a => "  " + JsonSerializer.Serialize(a) + ": \"\",")));
        if (fixedOnes.Count > 0)
            message.AppendLine($"{fixedOnes.Count} listed violation(s) no longer happen. Delete their entries from known-authz-violations.json:")
                .AppendLine(string.Join("\n", fixedOnes.Select(f => "  " + f)));
        throw new Xunit.Sdk.XunitException(message.ToString());
    }
}
