using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Web.Services;

public enum ConnectionRenameOutcome { Renamed, NotFound, InvalidName, NameTaken }

public sealed record ConnectionRenameResult(ConnectionRenameOutcome Outcome, Connection? Connection = null);

public enum ConnectionDeleteOutcome { Deleted, NotFound, ConfirmMismatch, HasSources }

/// <param name="Sources">The sources still on the connection, for <see cref="ConnectionDeleteOutcome.HasSources"/>.</param>
public sealed record ConnectionDeleteResult(
    ConnectionDeleteOutcome Outcome,
    Connection? Connection = null,
    IReadOnlyList<Source>? Sources = null);

/// <summary>
/// Renaming, deleting, pausing and resuming a connection, shared by the Connections page and the
/// REST API so the two apply the same rules and write the same audit events: a delete must name the
/// connection it deletes and is refused while sources use it, and pausing disables every source on
/// the connection without deleting anything.
/// </summary>
public sealed class ConnectionAdminService(IConnectionStore connections, ISourceStore sources, IAuditLogger audit)
{
    public const int MaxNameLength = 128;

    /// <summary>
    /// Updates a connection and writes <c>connection.updated</c> with the old and new names. Never
    /// records a config or secret value, only whether each changed. Null when the connection is gone.
    /// </summary>
    public async Task<Connection?> UpdateAsync(Guid id, UpdateConnectionRequest request, CancellationToken ct = default)
    {
        Connection? before = await connections.GetAsync(id, ct);
        if (before is null)
            return null;

        Connection? updated = await connections.UpdateAsync(id, request, ct);
        if (updated is null)
            return null;

        await audit.LogAsync("connection.updated", "connection", id.ToString(), new
        {
            OldName = before.Name,
            NewName = updated.Name,
            Provider = updated.Provider.ToString(),
            ConfigChanged = request.ConfigJson is not null,
            SecretReplaced = !string.IsNullOrEmpty(request.Secret),
        }, ct);
        return updated;
    }

    /// <summary>Renames a connection, refusing a blank or over-long name and one another connection has.</summary>
    public async Task<ConnectionRenameResult> RenameAsync(Guid id, string? name, CancellationToken ct = default)
    {
        string trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > MaxNameLength)
            return new ConnectionRenameResult(ConnectionRenameOutcome.InvalidName);

        Connection? current = await connections.GetAsync(id, ct);
        if (current is null)
            return new ConnectionRenameResult(ConnectionRenameOutcome.NotFound);
        if (current.Name == trimmed)
            return new ConnectionRenameResult(ConnectionRenameOutcome.Renamed, current);

        var all = await connections.ListAsync(0, int.MaxValue, ct);
        if (all.Any(c => c.Id != id && c.Name == trimmed))
            return new ConnectionRenameResult(ConnectionRenameOutcome.NameTaken);

        Connection? updated = await UpdateAsync(id, new UpdateConnectionRequest(Name: trimmed), ct);
        return updated is null
            ? new ConnectionRenameResult(ConnectionRenameOutcome.NotFound)
            : new ConnectionRenameResult(ConnectionRenameOutcome.Renamed, updated);
    }

    /// <summary>
    /// Deletes a connection when <paramref name="confirmName"/> is its name and no source uses it,
    /// then writes <c>connection.deleted</c>. The name is the typed confirmation the page asks for.
    /// </summary>
    public async Task<ConnectionDeleteResult> DeleteAsync(Guid id, string? confirmName, CancellationToken ct = default)
    {
        Connection? connection = await connections.GetAsync(id, ct);
        if (connection is null)
            return new ConnectionDeleteResult(ConnectionDeleteOutcome.NotFound);

        if (!string.Equals(confirmName?.Trim(), connection.Name, StringComparison.Ordinal))
            return new ConnectionDeleteResult(ConnectionDeleteOutcome.ConfirmMismatch, connection);

        if (connection.SourceCount > 0)
            return new ConnectionDeleteResult(ConnectionDeleteOutcome.HasSources, connection,
                await sources.ListByConnectionAsync(id, ct));

        bool deleted;
        try
        {
            deleted = await connections.DeleteAsync(id, ct);
        }
        catch (InvalidOperationException)
        {
            // A source was added after the count above was read; the store refuses it too.
            return new ConnectionDeleteResult(ConnectionDeleteOutcome.HasSources, connection,
                await sources.ListByConnectionAsync(id, ct));
        }

        if (!deleted)
            return new ConnectionDeleteResult(ConnectionDeleteOutcome.NotFound);

        await audit.LogAsync("connection.deleted", "connection", id.ToString(),
            new { connection.Name, Provider = connection.Provider.ToString() }, ct);
        return new ConnectionDeleteResult(ConnectionDeleteOutcome.Deleted, connection);
    }

    /// <summary>Connections that have sources, every one of them disabled: paused, not deleted.</summary>
    public async Task<HashSet<Guid>> PausedConnectionIdsAsync(CancellationToken ct = default)
    {
        var all = await sources.ListAsync(take: int.MaxValue, ct: ct);
        return all
            .Where(s => s.ConnectionId is not null)
            .GroupBy(s => s.ConnectionId!.Value)
            .Where(g => g.All(s => !s.Enabled))
            .Select(g => g.Key)
            .ToHashSet();
    }

    /// <summary>Whether the connection has sources and every one of them is disabled.</summary>
    public async Task<bool> IsPausedAsync(Guid connectionId, CancellationToken ct = default)
    {
        var onConnection = await sources.ListByConnectionAsync(connectionId, ct);
        return onConnection.Count > 0 && onConnection.All(s => !s.Enabled);
    }

    /// <summary>
    /// Pauses or resumes a connection by disabling or enabling its sources (only those in
    /// <paramref name="only"/>, when given). Nothing is deleted, and each source can still be turned
    /// on or off on its own afterwards. Writes <c>connection.paused</c> or <c>connection.resumed</c>.
    /// </summary>
    /// <returns>How many sources the change covered.</returns>
    public async Task<int> SetSyncingAsync(
        Connection connection, bool enabled, IReadOnlySet<Guid>? only = null, CancellationToken ct = default)
    {
        var chosen = (await sources.ListByConnectionAsync(connection.Id, ct))
            .Where(s => only is null || only.Contains(s.Id)).ToList();
        foreach (var source in chosen.Where(s => s.Enabled != enabled))
            await sources.UpdateAsync(source.Id, new UpdateSourceRequest(Enabled: enabled), ct);

        await audit.LogAsync(enabled ? "connection.resumed" : "connection.paused", "connection", connection.Id.ToString(),
            new { connection.Name, Sources = chosen.Count }, ct);
        return chosen.Count;
    }
}
