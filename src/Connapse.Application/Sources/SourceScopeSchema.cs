using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Connapse.Application.Ports;
using Connapse.Core;

namespace Connapse.Application.Sources;

/// <summary>What a typed scope in a source request is checked for before it becomes the stored JSON (#859).</summary>
public static class SourceScopeRequests
{
    public const string BothGiven = "Give the scope as \"scope\" or the deprecated \"scopeJson\", not both.";

    /// <summary>Why <paramref name="scope"/> can't be a scope of <paramref name="provider"/>'s sources, or null when it can.</summary>
    public static string? Mismatch(SourceScope scope, ConnectionProvider provider) =>
        SourceScopeCodec.ProviderOf(scope) == provider
            ? null
            : $"A \"{SourceScopeCodec.TypeOf(scope)}\" scope is for {SourceScopeCodec.ProviderOf(scope)} sources, but this source's connection is "
              + $"{provider}, whose scope types are {string.Join(", ", SourceScopeCodec.KindsFor(provider).Select(k => $"\"{k.Type}\""))}.";
}

public enum ScopeSchemaStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "provider-not-found", "Provider not found")]
    UnknownProvider,
}

/// <summary>One kind of scope a provider's sources take: its <c>type</c>, and the JSON schema of its other members.</summary>
public sealed record ScopeKindSchema(string Type, JsonNode Schema);

/// <summary>Every kind of scope a provider's sources take.</summary>
public sealed record ScopeSchema(ConnectionProvider Provider, IReadOnlyList<ScopeKindSchema> Kinds);

/// <summary>
/// The shape of the scope a provider's sources take, so an agent or a script can build one without
/// reading the code (#859). It names no source and no data, so any viewer may read it.
/// </summary>
[Operation("sources.scopeSchema", Scope = "knowledge:read", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class GetScopeSchema(IOperationAuthorizer authorizer)
{
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<Attempt<ScopeSchema, ScopeSchemaStatus>> RunAsync(OperationContext context, string provider, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Viewer))
            return Attempt<ScopeSchema, ScopeSchemaStatus>.Fail(ScopeSchemaStatus.Forbidden, "You don't have permission to view sources.");

        // Names only: a number would parse as the enum's value with that number.
        if (int.TryParse(provider, out _) || !Enum.TryParse(provider, ignoreCase: true, out ConnectionProvider named) || !Enum.IsDefined(named))
            return Attempt<ScopeSchema, ScopeSchemaStatus>.Fail(ScopeSchemaStatus.UnknownProvider,
                $"No provider '{provider}'. Providers are {string.Join(", ", Enum.GetNames<ConnectionProvider>())}.");

        var kinds = SourceScopeCodec.KindsFor(named)
            .Select(k => new ScopeKindSchema(k.Type, JsonSchemaExporter.GetJsonSchemaAsNode(Api, k.Record)))
            .ToList();
        return Attempt<ScopeSchema, ScopeSchemaStatus>.Succeed(new ScopeSchema(named, kinds));
    }
}
