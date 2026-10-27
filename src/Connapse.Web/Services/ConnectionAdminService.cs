using Connapse.Application;
using Connapse.Application.Connections;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Components;
using Microsoft.AspNetCore.Components.Authorization;
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
public sealed class ConnectionAdminService(
    IConnectionStore connections, IAuditLogger audit, ConnectionOperations operations, AuthenticationStateProvider authentication)
{
    public const int MaxNameLength = ConnectionMessages.MaxNameLength;

    /// <summary>
    /// Every connection, and which have syncing paused, paged through connections.list as the signed-in user.
    /// A refusal goes to <paramref name="failed"/>, and what was listed before it comes back.
    /// </summary>
    public async Task<(List<Connection> Connections, HashSet<Guid> Paused)> ListAllAsync(Action<string> failed, CancellationToken ct = default)
    {
        OperationContext context = await authentication.OperationContextAsync();
        List<ConnectionView> all = [];
        for (bool more = true; more;)
        {
            var listed = await operations.List.RunAsync(context, new ListConnectionsQuery(all.Count, ListConnections.MaxTake), ct);
            all.AddRange(listed.ResultOr(failed, new ConnectionList([], false)).Items);
            more = listed.Result?.HasMore == true;
        }
        return (all.Select(v => v.Connection).ToList(), all.Where(v => v.SyncPaused).Select(v => v.Connection.Id).ToHashSet());
    }

    /// <summary>
    /// Resumes the connection's sources (only <paramref name="only"/> when given) through connections.resume, or pauses
    /// them all through connections.pause. What changed goes to <paramref name="succeeded"/>, a refusal to <paramref name="failed"/>.
    /// </summary>
    public async Task SetSyncingAsync(
        Connection connection, bool enabled, IReadOnlySet<Guid>? only, Action<string> succeeded, Action<string> failed, CancellationToken ct = default)
    {
        OperationContext context = await authentication.OperationContextAsync();
        var changed = enabled
            ? await operations.Resume.RunAsync(context, new ConnectionRef(connection.Id), only?.ToList(), ct)
            : await operations.Pause.RunAsync(context, new ConnectionRef(connection.Id), ct);
        if (!changed.Succeeded)
            failed(changed.Detail ?? $"Could not change syncing for '{connection.Name}'.");
        else if (enabled)
            succeeded($"Syncing resumed for {changed.Result!.Sources} source(s) on '{connection.Name}'.");
        else
            succeeded($"Syncing paused for {changed.Result!.Sources} source(s) on '{connection.Name}'. Their documents stay searchable; nothing was deleted.");
    }

    /// <summary>
    /// Creates the connection through connections.create as the signed-in user: the same settings checks and
    /// audit as POST /api/v1/connections. A refused name goes to <paramref name="nameRefused"/>, a refused custom
    /// address or token to <paramref name="fieldRefused"/> with the field, and any other refusal (or one with no
    /// handler given) to <paramref name="failed"/>; null comes back for all of them.
    /// </summary>
    public async Task<Connection?> CreateAsync(
        NewConnection connection, Action<string> failed, Func<string, Task>? nameRefused = null,
        Func<string, string, Task>? fieldRefused = null, CancellationToken ct = default)
    {
        var created = await operations.Create.RunAsync(await authentication.OperationContextAsync(), connection, ct);
        if (created.Succeeded)
            return created.Result!.Connection;
        if (created.Field == "name" && nameRefused is not null)
            await nameRefused(created.Detail!);
        else if (created.Field is "baseUrl" or "secret" && fieldRefused is not null)
            await fieldRefused(created.Detail!, created.Field);
        else
            failed(created.Detail!);
        return null;
    }

    /// <summary>
    /// Deletes the connection through connections.delete, confirmed by its own name. Any refusal but
    /// <see cref="ConnectionStatus.InUse"/>, which the page shows with the sources blocking it, goes to <paramref name="failed"/>.
    /// </summary>
    public async Task<ConnectionStatus> DeleteAsync(Connection connection, Action<string> failed, CancellationToken ct = default)
    {
        var deleted = await operations.Delete.RunAsync(
            await authentication.OperationContextAsync(), new ConnectionRef(connection.Id), connection.Name, ct);
        if (deleted.Status == ConnectionStatus.NotFound)
            failed($"Connection '{connection.Name}' no longer exists — it was deleted elsewhere.");
        else if (deleted.Status is not (ConnectionStatus.Success or ConnectionStatus.InUse))
            failed(deleted.Detail ?? $"Connection '{connection.Name}' could not be deleted.");
        return deleted.Status;
    }

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
