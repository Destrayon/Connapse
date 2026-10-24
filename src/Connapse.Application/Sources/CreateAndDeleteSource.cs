using System.Text.Json;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Application.Sources;

/// <summary>
/// What a new source asks for. A source sits on a connection, which holds the credential; only a
/// Confluence or Jira source may leave its name out, to be named after its site and space or project.
/// </summary>
public sealed record NewSource(
    string? Name,
    Guid? ConnectionId,
    string? ScopeJson = null,
    string? Description = null,
    int? SyncIntervalSeconds = null,
    ConnectionProvider? Provider = null,
    string? Secret = null)
{
    /// <summary>The page's request, as REST would send it.</summary>
    public static NewSource From(CreateSourceRequest request) =>
        new(request.Name, request.ConnectionId, request.ScopeJson, request.Description, request.SyncIntervalSeconds, request.Provider);

    // The generated ToString would print the secret into any log line or message it reached.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Name = {Name}, ConnectionId = {ConnectionId}, ScopeJson = {ScopeJson}, Description = {Description}, ")
            .Append($"SyncIntervalSeconds = {SyncIntervalSeconds}, Provider = {Provider}, Secret = {(Secret is null ? "null" : "***")}");
        return true;
    }
}

/// <summary>The source created, and a warning about its connection the caller should see, if any.</summary>
public sealed record CreatedSource(Source Source, string? Warning);

/// <summary>
/// Creates a source inside an existing connection. Administrators only: a source chooses what
/// external data gets indexed. The provider checks its scope first (<see cref="ISourceScopeRules"/>).
/// </summary>
[Operation("sources.create", Scope = "admin:sources", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class CreateSource(
    ISourceStore sources, IConnectionStore connections, ISourceCreateRules scopes, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<CreatedSource, SourceWriteStatus>> RunAsync(OperationContext context, NewSource request, CancellationToken ct = default)
    {
        static Attempt<CreatedSource, SourceWriteStatus> Fail(SourceWriteStatus status, string? detail, string? field = null) =>
            Attempt<CreatedSource, SourceWriteStatus>.Fail(status, detail, field);

        if (!await authorizer.AllowsAsync(context.User, Policies.Admin))
            return Fail(SourceWriteStatus.Forbidden, "You don't have permission to add sources.");

        // Every source authenticates through its connection, a custom one included, so a secret here
        // would be silently dropped.
        if (request.Secret is not null)
            return Fail(SourceWriteStatus.Invalid,
                "A source takes no secret: its connection holds the credential. A custom source reads with its custom connection's token.", "secret");
        if (request.Provider is ConnectionProvider.Custom)
            return Fail(SourceWriteStatus.Invalid,
                "A custom source needs a custom connection: pass its connectionId. An administrator adds one on the Connections page.", "connectionId");

        string name = request.Name?.Trim() ?? "";
        string? scopeJson = request.ScopeJson;
        string? warning = null;

        // Only a Confluence source may leave its name out: it is then named after the site and space.
        if (name.Length == 0 && request.ConnectionId is null)
            return Fail(SourceWriteStatus.Invalid, "Source name is required", "name");
        if (request.ConnectionId is not null && request.Provider is not null)
            return Fail(SourceWriteStatus.Invalid, "a source must have exactly one of a connectionId or a provider");

        if (request.ConnectionId is Guid connectionId)
        {
            // Checked before anything else touches the database: a source whose connection does not
            // exist is skipped silently by the sync, so it would look created and never sync.
            var connection = await connections.GetAsync(connectionId, ct);
            if (connection is null)
                return Fail(SourceWriteStatus.Invalid, $"Connection {connectionId} not found");

            var check = await scopes.CheckCreateAsync(connection, scopeJson, ct);
            if (!check.Ok)
            {
                var status = check.Refusal switch
                {
                    ScopeRefusal.Duplicate => SourceWriteStatus.Conflict,
                    ScopeRefusal.Unavailable => SourceWriteStatus.Unavailable,
                    _ => SourceWriteStatus.Invalid,
                };
                return Fail(status, check.Error, check.Field);
            }
            scopeJson = check.ScopeJson;
            warning = check.Warning;
            if (name.Length == 0)
                name = check.DefaultName ?? "";
            if (name.Length == 0)
                return Fail(SourceWriteStatus.Invalid, "Source name is required", "name");
        }
        else if (request.Provider is null)
        {
            return Fail(SourceWriteStatus.Invalid, "a source needs a connectionId or a provider");
        }
        else if (request.Provider is ConnectionProvider.GitHub)
        {
            // Refused rather than stored: the connector no longer reads GitHub anonymously, so such a
            // source would be created only to fail every sync.
            return Fail(SourceWriteStatus.Invalid,
                "GitHub sources need a GitHub App connection: pass the connectionId of a GitHub installation instead of a provider");
        }

        if (!string.IsNullOrWhiteSpace(scopeJson))
        {
            // Disposed: JsonDocument rents pooled buffers, and this runs per request.
            try { using var _ = JsonDocument.Parse(scopeJson); }
            catch (JsonException ex)
            { return Fail(SourceWriteStatus.Invalid, $"Invalid scope JSON: {ex.Message}"); }
        }

        if (await sources.GetByNameAsync(name, ct) is not null)
            return Fail(SourceWriteStatus.Conflict, $"Source '{name}' already exists");

        Source created;
        try
        {
            created = await sources.CreateAsync(new CreateSourceRequest(
                name, request.ConnectionId, scopeJson ?? "{}", request.Description, request.SyncIntervalSeconds, request.Provider), ct);
        }
        catch (ArgumentException ex)
        {
            return Fail(SourceWriteStatus.Invalid, ex.Message);
        }
        catch (DuplicateSourceScopeException ex)
        {
            return Fail(SourceWriteStatus.Conflict, ex.Message, "scopeId");
        }
        catch (InvalidOperationException ex)
        {
            // The store's own duplicate-name refusal, when another create got there between the check and here.
            return Fail(SourceWriteStatus.Conflict, ex.Message, "name");
        }

        await audit.LogAsync("source.created", "source", created.Id.ToString(), new { created.Name, created.ConnectionId }, ct);
        return Attempt<CreatedSource, SourceWriteStatus>.Succeed(new CreatedSource(created, warning));
    }
}

/// <summary>
/// Deletes a source and everything indexed from it; the data in the external system is untouched.
/// Administrators only, and the source's name must be typed to confirm, as the Sources page asks.
/// </summary>
[Operation("sources.delete", Scope = "admin:sources", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class DeleteSource(ISourceStore sources, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<Source, SourceWriteStatus>> RunAsync(OperationContext context, Guid sourceId, string? confirmName, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Admin))
            return Attempt<Source, SourceWriteStatus>.Fail(SourceWriteStatus.Forbidden, "You don't have permission to delete sources.");

        var source = await sources.GetAsync(sourceId, ct);
        if (source is null)
            return Attempt<Source, SourceWriteStatus>.Fail(SourceWriteStatus.NotFound, $"Source {sourceId} not found");

        if (!SourceDeletion.Confirmed(source, confirmName))
            return Attempt<Source, SourceWriteStatus>.Fail(SourceWriteStatus.Invalid, SourceDeletion.ConfirmMismatch(source), "confirmName");

        if (!await sources.DeleteAsync(sourceId, ct))
            return Attempt<Source, SourceWriteStatus>.Fail(SourceWriteStatus.NotFound, $"Source {sourceId} not found");

        await audit.LogAsync("source.deleted", "source", sourceId.ToString(), new { source.Name }, ct);
        return Attempt<Source, SourceWriteStatus>.Succeed(source);
    }
}
