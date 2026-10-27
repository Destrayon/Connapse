using Connapse.Application.Connections;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Web.Services;

public enum ConnectionRenameOutcome { Renamed, NotFound, InvalidName, NameTaken }

public sealed record ConnectionRenameResult(ConnectionRenameOutcome Outcome, Connection? Connection = null);

/// <summary>
/// Saving a connection's settings and secret together, as the Connections page's create and edit forms
/// do. Renaming, deleting, pausing and resuming run on the connection operations in
/// <c>Connapse.Application.Connections</c>; a settings or secret change moves there with connections.create
/// and connections.replaceSecret.
/// </summary>
public sealed class ConnectionAdminService(IConnectionStore connections, IAuditLogger audit)
{
    public const int MaxNameLength = ConnectionMessages.MaxNameLength;

    /// <summary>
    /// Why a connection can't be deleted yet, naming every source that blocks it. The Connections
    /// page and the REST API both say this, the page with each name linking to its source.
    /// </summary>
    public static string InUseMessage(string connectionName, IReadOnlyCollection<Source> blocking) =>
        ConnectionMessages.InUse(connectionName, blocking);

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

    /// <summary>What the page and the API say when another connection already has the name.</summary>
    public static string NameTakenMessage(string name) => ConnectionMessages.NameTaken(name);

    /// <summary>What the page says when the name is blank or too long.</summary>
    public static readonly string InvalidNameMessage = ConnectionMessages.InvalidName;

    /// <summary>Whether a connection other than <paramref name="exceptId"/> already has the name.</summary>
    public async Task<bool> NameTakenAsync(Guid? exceptId, string name, CancellationToken ct = default)
    {
        string trimmed = name.Trim();
        var all = await connections.ListAsync(0, int.MaxValue, ct);
        return all.Any(c => c.Id != exceptId && c.Name == trimmed);
    }

    /// <summary>
    /// Saves an edit to a connection, its name checked as connections.update checks it, so a
    /// taken or invalid name comes back as an outcome rather than as the database's exception. The
    /// Connections page saves every provider's edits through this.
    /// </summary>
    public async Task<ConnectionRenameResult> SaveAsync(Guid id, UpdateConnectionRequest request, CancellationToken ct = default)
    {
        string trimmed = request.Name?.Trim() ?? "";
        if (trimmed.Length is 0 or > MaxNameLength)
            return new ConnectionRenameResult(ConnectionRenameOutcome.InvalidName);

        Connection? current = await connections.GetAsync(id, ct);
        if (current is null)
            return new ConnectionRenameResult(ConnectionRenameOutcome.NotFound);
        if (current.Name != trimmed && await NameTakenAsync(id, trimmed, ct))
            return new ConnectionRenameResult(ConnectionRenameOutcome.NameTaken);

        Connection? updated;
        try
        {
            updated = await UpdateAsync(id, request with { Name = trimmed }, ct);
        }
        // Another session took the name between the check and the save; the unique index refused it.
        catch (DbUpdateException)
        {
            if (await NameTakenAsync(id, trimmed, CancellationToken.None))
                return new ConnectionRenameResult(ConnectionRenameOutcome.NameTaken);
            throw;
        }

        return updated is null
            ? new ConnectionRenameResult(ConnectionRenameOutcome.NotFound)
            : new ConnectionRenameResult(ConnectionRenameOutcome.Renamed, updated);
    }
}
