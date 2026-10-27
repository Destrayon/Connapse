using System.Security.Claims;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Application.Connections;

/// <summary>Why a connection action didn't succeed.</summary>
public enum ConnectionStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "connection-not-found", "Connection not found")]
    NotFound,

    [Failure(400, "invalid-connection", "Invalid connection")]
    Invalid,

    [Failure(409, "connection-conflict", "Connection already exists")]
    Conflict,

    [Failure(409, "connection-in-use", "Connection in use")]
    InUse,

    [Failure(409, "connection-has-no-sources", "Connection has no sources")]
    NoSources,

    [Failure(400, "invalid-page", "Invalid page")]
    InvalidPage,

    [Failure(502, "connection-check-unavailable", "Connection check unavailable")]
    Unavailable,
}

/// <summary>
/// Names a connection by id. <see cref="Provider"/>, when set, is the only provider it may be: the
/// Atlassian site routes name a site this way, so another provider's connection reads as missing there.
/// </summary>
public sealed record ConnectionRef(Guid Id, ConnectionProvider? Provider = null)
{
    public static ConnectionRef AtlassianSite(Guid id) => new(id, ConnectionProvider.Atlassian);
}

/// <summary>
/// One page of connections, by name. <see cref="Take"/> null means every connection, which the
/// Connections page and the Atlassian site list ask for. <see cref="Provider"/> narrows it to one provider.
/// </summary>
public sealed record ListConnectionsQuery(int Skip = 0, int? Take = null, ConnectionProvider? Provider = null);

/// <summary>A connection, and whether its syncing is paused: it has sources and every one is off.</summary>
public sealed record ConnectionView(Connection Connection, bool SyncPaused);

public sealed record ConnectionList(IReadOnlyList<ConnectionView> Items, bool HasMore);

/// <summary>Whether a connection's syncing is paused now, and how many of its sources the change covered.</summary>
public sealed record ConnectionSyncState(bool SyncPaused, int Sources);

/// <summary>A new connection: its name, its provider, and its settings in the shape they are stored in.</summary>
public sealed record NewConnection(string? Name, ConnectionProvider? Provider, ConnectionSettings Settings);

/// <summary>
/// A change to a connection: a new name, new settings, or both. A null <see cref="Name"/> keeps the name when
/// settings are given; a null <see cref="Settings"/> leaves them alone.
/// </summary>
public sealed record ConnectionChange(string? Name, ConnectionSettings? Settings = null);

/// <summary>A connection just created, and anything the caller should be told about what was stored.</summary>
public sealed record CreatedConnection(Connection Connection, string? Note);

/// <summary>
/// A connection whose provider's own service has checked its settings already, as an Atlassian site is
/// checked against the site before it is saved. <see cref="AuditDetails"/> is what <c>connection.created</c> records.
/// </summary>
public sealed record CheckedConnection(string Name, ConnectionProvider Provider, string ConfigJson, string? Secret, object AuditDetails)
{
    // The generated ToString would print the secret into any log line or message it reached.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Name = {Name}, Provider = {Provider}, ConfigJson = {ConfigJson}, Secret = {(Secret is null ? "null" : "***")}");
        return true;
    }
}

internal static class ConnectionRules
{
    public const int MaxNameLength = 128;

    public static async Task<Connection?> FindAsync(IConnectionStore connections, ConnectionRef key, CancellationToken ct) =>
        await connections.GetAsync(key.Id, ct) is { } connection && (key.Provider is null || connection.Provider == key.Provider)
            ? connection
            : null;

    public static Attempt<T, ConnectionStatus> NotFound<T>(ConnectionRef key) =>
        Attempt<T, ConnectionStatus>.Fail(ConnectionStatus.NotFound,
            key.Provider == ConnectionProvider.Atlassian ? "No such Atlassian site." : $"Connection {key.Id} not found");

    /// <summary>What a message calls the connection: an Atlassian site route says "site", as it always has.</summary>
    public static string Noun(ConnectionRef key) => key.Provider == ConnectionProvider.Atlassian ? "site" : "connection";

    /// <summary>Whether a connection other than <paramref name="exceptId"/> already has the name.</summary>
    public static async Task<bool> NameTakenAsync(IConnectionStore connections, Guid? exceptId, string name, CancellationToken ct)
    {
        string trimmed = name.Trim();
        var all = await connections.ListAsync(0, int.MaxValue, ct);
        return all.Any(c => c.Id != exceptId && c.Name == trimmed);
    }

    /// <summary>The settings rules' refusal as the operation's: wrong settings, or a check that couldn't run.</summary>
    public static Attempt<T, ConnectionStatus> Refused<T>(SettingsCheck check) =>
        Attempt<T, ConnectionStatus>.Fail(
            check.Refusal == SettingsRefusal.Unavailable ? ConnectionStatus.Unavailable : ConnectionStatus.Invalid,
            check.Error!, check.Field);

    public static Guid? UserIdOf(OperationContext context) =>
        Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id) ? id : null;

    /// <summary>Records that the connection's settings were just checked against the live service and passed.</summary>
    public static Task<ConnectionHealth?> RecordVerifiedAsync(IConnectionStore connections, Guid id, CancellationToken ct) =>
        connections.RecordHealthAsync(id, new ConnectionHealth(
            DateTime.UtcNow, Succeeded: true, Step: null, "Reachable, token accepted.", Origin: "test"), ct);

    public static async Task<bool> IsPausedAsync(ISourceStore sources, Guid connectionId, CancellationToken ct)
    {
        var onConnection = await sources.ListByConnectionAsync(connectionId, ct);
        return onConnection.Count > 0 && onConnection.All(s => !s.Enabled);
    }
}

/// <summary>What the Connections page and the API say about a connection's name and its use.</summary>
public static class ConnectionMessages
{
    public const int MaxNameLength = ConnectionRules.MaxNameLength;

    /// <summary>
    /// Why a connection can't be deleted yet, naming every source that blocks it. The Connections
    /// page and the REST API both say this, the page with each name linking to its source.
    /// </summary>
    public static string InUse(string connectionName, IReadOnlyCollection<Source> blocking) =>
        $"'{connectionName}' can't be deleted while {blocking.Count} {(blocking.Count == 1 ? "source uses" : "sources use")} it: "
        + $"{string.Join(", ", blocking.Select(s => s.Name))}. Delete {(blocking.Count == 1 ? "that source" : "those sources")} first.";

    /// <summary>What the page and the API say when another connection already has the name.</summary>
    public static string NameTaken(string name) => $"A connection named '{name.Trim()}' already exists. Choose another name.";

    /// <summary>What the page and the API say when the name is blank or too long.</summary>
    public static readonly string InvalidName = $"Enter a name of 1 to {MaxNameLength} characters.";

    /// <summary>Why connections.create won't make an Atlassian site: only the site's own check may.</summary>
    public const string AtlassianSiteOnly =
        "Add an Atlassian site with POST /api/v1/atlassian/sites, which checks its service account before saving it.";
}

/// <summary>Lists connections by name, each with whether it is paused. Administrators only; never a secret.</summary>
[Operation("connections.list", Scope = "admin:sources", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class ListConnections(IConnectionStore connections, ISourceStore sources, IOperationAuthorizer authorizer)
{
    public const int MaxTake = 200;

    public async Task<Attempt<ConnectionList, ConnectionStatus>> RunAsync(OperationContext context, ListConnectionsQuery query, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SourcesAdmin))
            return Attempt<ConnectionList, ConnectionStatus>.Fail(ConnectionStatus.Forbidden, "You don't have permission to view connections.");
        if (query.Skip < 0)
            return Attempt<ConnectionList, ConnectionStatus>.Fail(ConnectionStatus.InvalidPage, "skip must be >= 0");
        if (query.Take is < 1)
            return Attempt<ConnectionList, ConnectionStatus>.Fail(ConnectionStatus.InvalidPage, "take must be >= 1");
        if (query.Take is > MaxTake)
            return Attempt<ConnectionList, ConnectionStatus>.Fail(ConnectionStatus.InvalidPage, $"take must be <= {MaxTake}");

        // Narrowed to a provider before paging, so a page of sites is a page of sites.
        IEnumerable<Connection> all = (await connections.ListAsync(0, int.MaxValue, ct))
            .Where(c => query.Provider is null || c.Provider == query.Provider)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Skip(query.Skip);
        // One more than the page, to know whether another follows.
        var listed = (query.Take is int take ? all.Take(take + 1) : all).ToList();
        bool hasMore = query.Take is int size && listed.Count > size;
        if (hasMore)
            listed.RemoveAt(listed.Count - 1);

        var paused = (await sources.ListAsync(take: int.MaxValue, ct: ct))
            .Where(s => s.ConnectionId is not null)
            .GroupBy(s => s.ConnectionId!.Value)
            .Where(g => g.All(s => !s.Enabled))
            .Select(g => g.Key)
            .ToHashSet();
        return Attempt<ConnectionList, ConnectionStatus>.Succeed(
            new ConnectionList(listed.Select(c => new ConnectionView(c, paused.Contains(c.Id))).ToList(), hasMore));
    }
}

/// <summary>Reads one connection and whether it is paused. Administrators only; never its secret.</summary>
[Operation("connections.get", Scope = "admin:sources", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class GetConnection(IConnectionStore connections, ISourceStore sources, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<ConnectionView, ConnectionStatus>> RunAsync(OperationContext context, ConnectionRef key, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SourcesAdmin))
            return Attempt<ConnectionView, ConnectionStatus>.Fail(ConnectionStatus.Forbidden, "You don't have permission to view connections.");

        return await ConnectionRules.FindAsync(connections, key, ct) is { } connection
            ? Attempt<ConnectionView, ConnectionStatus>.Succeed(
                new ConnectionView(connection, await ConnectionRules.IsPausedAsync(sources, connection.Id, ct)))
            : ConnectionRules.NotFound<ConnectionView>(key);
    }
}

/// <summary>
/// Renames a connection, changes its settings, or both. Administrators only. The name must be 1 to 128
/// characters and no other connection's, and new settings pass the same per-provider checks as a new
/// connection's. Audited as <c>connection.updated</c> with the old and new names and whether the config or
/// secret changed, never a value.
/// </summary>
[Operation("connections.update", Scope = "admin:sources", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class UpdateConnection(
    IConnectionStore connections, ISourceStore sources, IConnectionSettingsRules rules, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public Task<Attempt<ConnectionView, ConnectionStatus>> RunAsync(OperationContext context, ConnectionRef key, string? name, CancellationToken ct = default) =>
        RunAsync(context, key, new ConnectionChange(name), ct);

    public async Task<Attempt<ConnectionView, ConnectionStatus>> RunAsync(
        OperationContext context, ConnectionRef key, ConnectionChange change, CancellationToken ct = default)
    {
        static Attempt<ConnectionView, ConnectionStatus> Fail(ConnectionStatus status, string detail, string? field = null) =>
            Attempt<ConnectionView, ConnectionStatus>.Fail(status, detail, field);

        if (!await authorizer.AllowsAsync(context.User, Policies.SourcesAdmin))
            return Fail(ConnectionStatus.Forbidden, "You don't have permission to change connections.");

        ConnectionSettings? settings = change.Settings;
        string? trimmed = change.Name?.Trim();
        // A name is required unless only the settings change.
        if ((settings is null || trimmed is not null) && trimmed is not { Length: > 0 and <= ConnectionRules.MaxNameLength })
            return Fail(ConnectionStatus.Invalid, ConnectionMessages.InvalidName, "name");

        var current = await ConnectionRules.FindAsync(connections, key, ct);
        if (current is null)
            return ConnectionRules.NotFound<ConnectionView>(key);

        string name = trimmed ?? current.Name;
        bool renamed = current.Name != name;
        if (renamed && await ConnectionRules.NameTakenAsync(connections, current.Id, name, ct))
            return Fail(ConnectionStatus.Conflict, ConnectionMessages.NameTaken(name), "name");

        SettingsCheck? check = null;
        if (settings is not null)
        {
            // An Atlassian site's settings are its address and service account, changed only after its own check.
            if (current.Provider == ConnectionProvider.Atlassian)
                return Fail(ConnectionStatus.Invalid,
                    "An Atlassian site's settings change with its credentials: PUT /api/v1/atlassian/sites/{id}/credentials.", "config");
            check = await rules.CheckAsync(name, current.Provider, settings, current, ct);
            if (!check.Ok)
                return ConnectionRules.Refused<ConnectionView>(check);
        }

        Connection updated = current;
        if (renamed || check is not null)
        {
            Connection? saved;
            try
            {
                saved = await connections.UpdateAsync(current.Id, new UpdateConnectionRequest(
                    Name: renamed ? name : null, ConfigJson: check?.ConfigJson, Secret: check?.Secret,
                    ExpectedUpdatedAt: check is null ? null : current.UpdatedAt), ct);
            }
            // Another save landed after these settings were checked; writing them now could pair them with its secret.
            catch (ConnectionChangedException)
            {
                return Fail(ConnectionStatus.Conflict, "The connection changed while these settings were being checked. Reload it and try again.");
            }
            // Another session took the name between the check and the save; the unique index refused it.
            catch (DbUpdateException)
            {
                if (await ConnectionRules.NameTakenAsync(connections, current.Id, name, CancellationToken.None))
                    return Fail(ConnectionStatus.Conflict, ConnectionMessages.NameTaken(name), "name");
                throw;
            }
            if (saved is null)
                return ConnectionRules.NotFound<ConnectionView>(key);
            updated = saved;

            await audit.LogAsync("connection.updated", "connection", current.Id.ToString(), new
            {
                OldName = current.Name,
                NewName = updated.Name,
                Provider = updated.Provider.ToString(),
                ConfigChanged = check is not null,
                SecretReplaced = check?.Secret is not null,
            }, ct);

            if (check is { Verified: true } && await ConnectionRules.RecordVerifiedAsync(connections, updated.Id, ct) is { } health)
                updated = updated with { Health = health };
        }

        return Attempt<ConnectionView, ConnectionStatus>.Succeed(
            new ConnectionView(updated, await ConnectionRules.IsPausedAsync(sources, updated.Id, ct)));
    }
}

/// <summary>
/// Creates a filesystem, SFTP, S3, Azure Blob, GitHub or custom connection. Administrators only. The name must
/// be 1 to 128 characters and no other connection's, and the settings pass the Connections page's checks for
/// the provider. An Atlassian site is added through its own check instead. Audited as <c>connection.created</c>.
/// </summary>
[Operation("connections.create", Scope = "admin:sources", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class CreateConnection(
    IConnectionStore connections, IConnectionSettingsRules rules, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<CreatedConnection, ConnectionStatus>> RunAsync(OperationContext context, NewConnection request, CancellationToken ct = default)
    {
        if (await RefusedAsync<CreatedConnection>(context, request.Name, ct) is { } refused)
            return refused;
        if (request.Provider is not { } provider || !Enum.IsDefined(provider))
            return Fail<CreatedConnection>(ConnectionStatus.Invalid, "Choose the connection's provider.", "provider");
        if (provider == ConnectionProvider.Atlassian)
            return Fail<CreatedConnection>(ConnectionStatus.Invalid, ConnectionMessages.AtlassianSiteOnly, "provider");

        string name = request.Name!.Trim();
        if (await ConnectionRules.NameTakenAsync(connections, null, name, ct))
            return Fail<CreatedConnection>(ConnectionStatus.Conflict, ConnectionMessages.NameTaken(name), "name");

        SettingsCheck check = await rules.CheckAsync(name, provider, request.Settings, null, ct);
        if (!check.Ok)
            return ConnectionRules.Refused<CreatedConnection>(check);

        var stored = await StoreAsync(context,
            new CheckedConnection(name, provider, check.ConfigJson ?? "{}", check.Secret, new { Name = name, Provider = provider.ToString() }), ct);
        if (!stored.Succeeded)
            return Fail<CreatedConnection>(stored.Status, stored.Detail!, stored.Field);

        Connection created = stored.Result!;
        if (check.Verified && await ConnectionRules.RecordVerifiedAsync(connections, created.Id, ct) is { } health)
            created = created with { Health = health };
        return Attempt<CreatedConnection, ConnectionStatus>.Succeed(new CreatedConnection(created, check.Note));
    }

    /// <summary>
    /// Stores a connection whose settings its provider's own service has checked: an Atlassian site, after
    /// every probe of the site passed. The name rules, the audit and who may do it are the same as for any other.
    /// </summary>
    public async Task<Attempt<Connection, ConnectionStatus>> RunCheckedAsync(OperationContext context, CheckedConnection request, CancellationToken ct = default)
    {
        if (await RefusedAsync<Connection>(context, request.Name, ct) is { } refused)
            return refused;
        if (await ConnectionRules.NameTakenAsync(connections, null, request.Name, ct))
            return Fail<Connection>(ConnectionStatus.Conflict, ConnectionMessages.NameTaken(request.Name), "name");
        return await StoreAsync(context, request with { Name = request.Name.Trim() }, ct);
    }

    private async Task<Attempt<T, ConnectionStatus>?> RefusedAsync<T>(OperationContext context, string? name, CancellationToken ct)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SourcesAdmin))
            return Fail<T>(ConnectionStatus.Forbidden, "You don't have permission to create connections.");
        return name?.Trim() is { Length: > 0 and <= ConnectionRules.MaxNameLength }
            ? null
            : Fail<T>(ConnectionStatus.Invalid, ConnectionMessages.InvalidName, "name");
    }

    private async Task<Attempt<Connection, ConnectionStatus>> StoreAsync(OperationContext context, CheckedConnection request, CancellationToken ct)
    {
        Connection created;
        try
        {
            created = await connections.CreateAsync(
                new CreateConnectionRequest(request.Name, request.Provider, request.ConfigJson, request.Secret),
                ConnectionRules.UserIdOf(context), ct);
        }
        // Another session took the name between the check and the save: the store or its unique index refused it.
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            if (await ConnectionRules.NameTakenAsync(connections, null, request.Name, CancellationToken.None))
                return Fail<Connection>(ConnectionStatus.Conflict, ConnectionMessages.NameTaken(request.Name), "name");
            throw;
        }

        await audit.LogAsync("connection.created", "connection", created.Id.ToString(), request.AuditDetails, ct);
        return Attempt<Connection, ConnectionStatus>.Succeed(created);
    }

    private static Attempt<T, ConnectionStatus> Fail<T>(ConnectionStatus status, string detail, string? field = null) =>
        Attempt<T, ConnectionStatus>.Fail(status, detail, field);
}

/// <summary>
/// Pauses a connection by turning off every source on it. Nothing is deleted, the documents stay
/// searchable, and each source can still be turned on alone. Audited as <c>connection.paused</c>.
/// </summary>
[Operation("connections.pause", Scope = "admin:sources", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class PauseConnection(IConnectionStore connections, ISourceStore sources, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<ConnectionSyncState, ConnectionStatus>> RunAsync(OperationContext context, ConnectionRef key, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SourcesAdmin))
            return Attempt<ConnectionSyncState, ConnectionStatus>.Fail(ConnectionStatus.Forbidden, "You don't have permission to pause connections.");

        var connection = await ConnectionRules.FindAsync(connections, key, ct);
        if (connection is null)
            return ConnectionRules.NotFound<ConnectionSyncState>(key);

        var onConnection = await sources.ListByConnectionAsync(connection.Id, ct);
        if (onConnection.Count == 0)
            return Attempt<ConnectionSyncState, ConnectionStatus>.Fail(ConnectionStatus.NoSources,
                $"This {ConnectionRules.Noun(key)} has no sources, so there is no syncing to pause.");

        int count = await ConnectionSyncing.SetAsync(sources, audit, connection, onConnection, enabled: false, ct);
        return Attempt<ConnectionSyncState, ConnectionStatus>.Succeed(new ConnectionSyncState(true, count));
    }
}

/// <summary>
/// Resumes a connection by turning its sources back on: those in <c>sourceIds</c>, each of them on
/// the connection, or all of them when it is left out. Audited as <c>connection.resumed</c>.
/// </summary>
[Operation("connections.resume", Scope = "admin:sources", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class ResumeConnection(IConnectionStore connections, ISourceStore sources, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<ConnectionSyncState, ConnectionStatus>> RunAsync(
        OperationContext context, ConnectionRef key, IReadOnlyCollection<Guid>? sourceIds = null, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SourcesAdmin))
            return Attempt<ConnectionSyncState, ConnectionStatus>.Fail(ConnectionStatus.Forbidden, "You don't have permission to resume connections.");

        var connection = await ConnectionRules.FindAsync(connections, key, ct);
        if (connection is null)
            return ConnectionRules.NotFound<ConnectionSyncState>(key);

        var onConnection = await sources.ListByConnectionAsync(connection.Id, ct);
        if (onConnection.Count == 0)
            return Attempt<ConnectionSyncState, ConnectionStatus>.Fail(ConnectionStatus.NoSources,
                $"This {ConnectionRules.Noun(key)} has no sources, so there is no syncing to resume.");

        var chosen = onConnection;
        if (sourceIds is not null)
        {
            var only = sourceIds.ToHashSet();
            if (only.Count == 0 || !only.IsSubsetOf(onConnection.Select(s => s.Id)))
                return Attempt<ConnectionSyncState, ConnectionStatus>.Fail(ConnectionStatus.Invalid,
                    $"sourceIds must name at least one source, each of them on this {ConnectionRules.Noun(key)}.", "sourceIds");
            chosen = onConnection.Where(s => only.Contains(s.Id)).ToList();
        }

        int count = await ConnectionSyncing.SetAsync(sources, audit, connection, chosen, enabled: true, ct);
        return Attempt<ConnectionSyncState, ConnectionStatus>.Succeed(
            new ConnectionSyncState(await ConnectionRules.IsPausedAsync(sources, connection.Id, ct), count));
    }
}

internal static class ConnectionSyncing
{
    /// <summary>Turns the chosen sources on or off and audits it; how many sources the change covered.</summary>
    public static async Task<int> SetAsync(
        ISourceStore sources, IAuditLogger audit, Connection connection, IReadOnlyList<Source> chosen, bool enabled, CancellationToken ct)
    {
        foreach (var source in chosen.Where(s => s.Enabled != enabled))
            await sources.UpdateAsync(source.Id, new UpdateSourceRequest(Enabled: enabled), ct);

        await audit.LogAsync(enabled ? "connection.resumed" : "connection.paused", "connection", connection.Id.ToString(),
            new { connection.Name, Sources = chosen.Count }, ct);
        return chosen.Count;
    }
}

/// <summary>
/// Deletes a connection when <c>confirmName</c> is its name and no source uses it. Administrators
/// only; the name is the typed confirmation the page asks for. Audited as <c>connection.deleted</c>.
/// </summary>
[Operation("connections.delete", Scope = "admin:sources", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class DeleteConnection(IConnectionStore connections, ISourceStore sources, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<Connection, ConnectionStatus>> RunAsync(OperationContext context, ConnectionRef key, string? confirmName, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SourcesAdmin))
            return Attempt<Connection, ConnectionStatus>.Fail(ConnectionStatus.Forbidden, "You don't have permission to delete connections.");

        var connection = await ConnectionRules.FindAsync(connections, key, ct);
        if (connection is null)
            return ConnectionRules.NotFound<Connection>(key);

        if (!string.Equals(confirmName?.Trim(), connection.Name, StringComparison.Ordinal))
            return Attempt<Connection, ConnectionStatus>.Fail(ConnectionStatus.Invalid,
                $"confirmName must be the {ConnectionRules.Noun(key)}'s name, '{connection.Name}'.", "confirmName");

        if (connection.SourceCount > 0)
            return await InUseAsync(connection, ct);

        bool deleted;
        try
        {
            deleted = await connections.DeleteAsync(connection.Id, ct);
        }
        catch (InvalidOperationException)
        {
            // A source was added after the count above was read; the store refuses it too.
            return await InUseAsync(connection, ct);
        }
        if (!deleted)
            return ConnectionRules.NotFound<Connection>(key);

        await audit.LogAsync("connection.deleted", "connection", connection.Id.ToString(),
            new { connection.Name, Provider = connection.Provider.ToString() }, ct);
        return Attempt<Connection, ConnectionStatus>.Succeed(connection);
    }

    private async Task<Attempt<Connection, ConnectionStatus>> InUseAsync(Connection connection, CancellationToken ct) =>
        Attempt<Connection, ConnectionStatus>.Fail(ConnectionStatus.InUse,
            ConnectionMessages.InUse(connection.Name, await sources.ListByConnectionAsync(connection.Id, ct)));
}

/// <summary>Every connection operation in one place, for the pages that offer several of them.</summary>
public sealed class ConnectionOperations(
    ListConnections list, GetConnection get, CreateConnection create, UpdateConnection update,
    PauseConnection pause, ResumeConnection resume, DeleteConnection delete)
{
    public ListConnections List { get; } = list;

    public GetConnection Get { get; } = get;

    public CreateConnection Create { get; } = create;

    public UpdateConnection Update { get; } = update;

    public PauseConnection Pause { get; } = pause;

    public ResumeConnection Resume { get; } = resume;

    public DeleteConnection Delete { get; } = delete;
}
