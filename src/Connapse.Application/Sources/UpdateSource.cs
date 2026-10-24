using System.Text.Json;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Application.Sources;

/// <summary>Why changing a source didn't succeed.</summary>
public enum SourceWriteStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "source-not-found", "Source not found")]
    NotFound,

    [Failure(400, "invalid-source", "Invalid source settings")]
    Invalid,

    [Failure(409, "source-conflict", "Source already exists")]
    Conflict,

    [Failure(502, "provider-unavailable", "The provider didn't answer")]
    Unavailable,
}

/// <summary>
/// Changes a source's name, description, scope, sync interval or enabled state. Administrators only:
/// a source chooses what external data is indexed. What a scope may change in place is the
/// provider's rule (<see cref="ISourceScopeRules"/>); a source never holds a secret.
/// </summary>
[Operation("sources.update", Scope = "admin:sources", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class UpdateSource(ISourceStore sources, ISourceScopeRules scopes, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<Source, SourceWriteStatus>> RunAsync(OperationContext context, Guid sourceId, UpdateSourceRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Admin))
            return Attempt<Source, SourceWriteStatus>.Fail(SourceWriteStatus.Forbidden, "You don't have permission to change sources.");

        if (!string.IsNullOrWhiteSpace(request.ScopeJson))
        {
            // Disposed: JsonDocument rents pooled buffers, and this runs per request.
            try { using var _ = JsonDocument.Parse(request.ScopeJson); }
            catch (JsonException ex)
            { return Attempt<Source, SourceWriteStatus>.Fail(SourceWriteStatus.Invalid, $"Invalid scope JSON: {ex.Message}"); }
        }

        if (request.ScopeJson is not null && await sources.GetAsync(sourceId, ct) is { } current)
        {
            var check = await scopes.CheckUpdateAsync(current, request.ScopeJson, ct);
            if (!check.Ok)
                return Attempt<Source, SourceWriteStatus>.Fail(SourceWriteStatus.Invalid, check.Error, check.Field);
            request = request with { ScopeJson = check.ScopeJson };
        }

        // A source holds no token: a custom source reads with its connection's, which is replaced there.
        if (request.Secret is not null)
            return Attempt<Source, SourceWriteStatus>.Fail(SourceWriteStatus.Invalid,
                "A source takes no secret. Replace a custom source's token on its connection, or on its custom provider.", "secret");

        var updated = await sources.UpdateAsync(sourceId, request, ct);
        if (updated is null)
            return Attempt<Source, SourceWriteStatus>.Fail(SourceWriteStatus.NotFound, $"Source {sourceId} not found");

        // Turning a source on or off is recorded as such, so the log says who paused syncing.
        object details = request.Enabled is bool enabled ? new { updated.Name, Enabled = enabled } : new { updated.Name };
        await audit.LogAsync("source.updated", "source", sourceId.ToString(), details, ct);
        return Attempt<Source, SourceWriteStatus>.Succeed(updated);
    }
}
