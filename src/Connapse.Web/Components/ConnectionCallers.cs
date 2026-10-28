using Connapse.Application;
using Connapse.Application.Connections;
using Connapse.Application.Ports;
using Connapse.Core;
using Microsoft.AspNetCore.Components.Authorization;

namespace Connapse.Web.Components;

/// <summary>
/// How the Connections page runs the connection operations as the signed-in user: the same checks, refusals
/// and audit as the /api/v1/connections routes, with each refusal sent to where the page shows it.
/// </summary>
public static class ConnectionCallers
{
    private static OperationContext Caller(AuthenticationState authentication) => new(authentication.User, OperationSurface.Ui);

    /// <summary>
    /// Every connection, and which have syncing paused, paged through connections.list. A refusal goes to
    /// <paramref name="failed"/>, and what was listed before it comes back.
    /// </summary>
    public static async Task<(List<Connection> Connections, HashSet<Guid> Paused)> ListAllAsync(
        this ConnectionOperations operations, AuthenticationState authentication, Action<string> failed)
    {
        List<ConnectionView> all = [];
        for (bool more = true; more;)
        {
            var listed = await operations.List.RunAsync(Caller(authentication), new ListConnectionsQuery(all.Count, ListConnections.MaxTake));
            all.AddRange(listed.ResultOr(failed, new ConnectionList([], false)).Items);
            more = listed.Result?.HasMore == true;
        }
        return (all.Select(v => v.Connection).ToList(), all.Where(v => v.SyncPaused).Select(v => v.Connection.Id).ToHashSet());
    }

    /// <summary>
    /// Resumes the connection's sources (only <paramref name="only"/> when given) through connections.resume, or pauses
    /// them all through connections.pause. What changed goes to <paramref name="succeeded"/>, a refusal to <paramref name="failed"/>.
    /// </summary>
    public static async Task SetSyncingAsync(
        this ConnectionOperations operations, AuthenticationState authentication, Connection connection, bool enabled,
        IReadOnlySet<Guid>? only, Action<string> succeeded, Action<string> failed)
    {
        var changed = enabled
            ? await operations.Resume.RunAsync(Caller(authentication), new ConnectionRef(connection.Id), only?.ToList())
            : await operations.Pause.RunAsync(Caller(authentication), new ConnectionRef(connection.Id));
        if (!changed.Succeeded)
            failed(changed.Detail ?? $"Could not change syncing for '{connection.Name}'.");
        else if (enabled)
            succeeded($"Syncing resumed for {changed.Result!.Sources} source(s) on '{connection.Name}'.");
        else
            succeeded($"Syncing paused for {changed.Result!.Sources} source(s) on '{connection.Name}'. Their documents stay searchable; nothing was deleted.");
    }

    /// <summary>Creates the connection through connections.create; null, with the refusal sent on as <see cref="RefuseAsync"/> sends it.</summary>
    public static async Task<Connection?> CreateAsync(
        this ConnectionOperations operations, AuthenticationState authentication, NewConnection connection, Action<string> failed,
        Func<string, Task>? nameRefused = null, Func<string, string, Task>? fieldRefused = null)
    {
        var created = await operations.Create.RunAsync(Caller(authentication), connection);
        return await created.RefuseAsync(failed, nameRefused, fieldRefused) ? null : created.Result!.Connection;
    }

    /// <summary>
    /// Saves an edit to <paramref name="editing"/>. A new secret with the name and settings unchanged is replaced through
    /// connections.replaceSecret, anything else saved through connections.update; any administrator may do
    /// either. A refusal but <see cref="ConnectionStatus.NotFound"/>, which the page reports itself, is sent on as
    /// <see cref="RefuseAsync"/> sends it.
    /// </summary>
    public static async Task<Attempt<ConnectionView, ConnectionStatus>> SaveAsync(
        this ConnectionOperations operations, AuthenticationState authentication, Connection editing, string name,
        ConnectionSettings? settings, Action<string> failed, Func<string, Task> nameRefused, Func<string, string, Task> fieldRefused)
    {
        bool secretOnly = settings is not null && name.Trim() == editing.Name && settings.ConfigJson == editing.ConfigJson
            && !(string.IsNullOrEmpty(settings.PrivateKey) && string.IsNullOrEmpty(settings.Token));
        var saved = secretOnly
            ? await operations.ReplaceSecret.RunAsync(Caller(authentication), new ConnectionRef(editing.Id),
                new ConnectionSecret(settings!.PrivateKey, settings.Passphrase, settings.Token))
            : await operations.Update.RunAsync(Caller(authentication), new ConnectionRef(editing.Id), new ConnectionChange(name, settings));
        if (saved.Status != ConnectionStatus.NotFound)
            await saved.RefuseAsync(failed, nameRefused, fieldRefused);
        return saved;
    }

    /// <summary>
    /// Tests the form's settings through connections.test: those of <paramref name="editing"/> when given (null
    /// <paramref name="settings"/> tests it as stored), otherwise an unsaved connection's.
    /// </summary>
    public static Task<Attempt<ConnectionTested, ConnectionStatus>> TestAsync(
        this ConnectionOperations operations, AuthenticationState authentication, Connection? editing, string name,
        ConnectionProvider? provider, ConnectionSettings? settings, string? probeTarget) =>
        editing is not null
            ? operations.Test.RunAsync(Caller(authentication), new ConnectionRef(editing.Id), new ConnectionTestRequest(settings, probeTarget))
            : operations.Test.RunDraftAsync(Caller(authentication),
                new DraftConnectionTest(name, provider, settings ?? new ConnectionSettings("{}"), probeTarget));

    /// <summary>Forgets the SFTP server's pinned host key through connections.forgetHostKey; a refusal goes to <paramref name="failed"/>.</summary>
    public static async Task<bool> ForgetHostKeyAsync(
        this ConnectionOperations operations, AuthenticationState authentication, Connection connection, Action<string> failed)
    {
        var forgotten = await operations.ForgetHostKey.RunAsync(Caller(authentication), new ConnectionRef(connection.Id));
        if (!forgotten.Succeeded)
            failed(forgotten.Detail ?? $"The host key of '{connection.Name}' could not be forgotten.");
        return forgotten.Succeeded;
    }

    /// <summary>
    /// Deletes the connection through connections.delete, confirmed by its own name. Any refusal but
    /// <see cref="ConnectionStatus.InUse"/>, which the page shows with the sources blocking it, goes to <paramref name="failed"/>.
    /// </summary>
    public static async Task<ConnectionStatus> DeleteAsync(
        this ConnectionOperations operations, AuthenticationState authentication, Connection connection, Action<string> failed)
    {
        var deleted = await operations.Delete.RunAsync(Caller(authentication), new ConnectionRef(connection.Id), connection.Name);
        if (deleted.Status == ConnectionStatus.NotFound)
            failed($"Connection '{connection.Name}' no longer exists — it was deleted elsewhere.");
        else if (deleted.Status is not (ConnectionStatus.Success or ConnectionStatus.InUse))
            failed(deleted.Detail ?? $"Connection '{connection.Name}' could not be deleted.");
        return deleted.Status;
    }

    /// <summary>
    /// Whether the attempt was refused. A refused name goes to <paramref name="nameRefused"/>, a refused custom address or
    /// token to <paramref name="fieldRefused"/> with the field, and any other refusal (or one with no handler given) to
    /// <paramref name="failed"/>.
    /// </summary>
    private static async Task<bool> RefuseAsync<T>(
        this Attempt<T, ConnectionStatus> attempt, Action<string> failed, Func<string, Task>? nameRefused, Func<string, string, Task>? fieldRefused)
    {
        if (attempt.Succeeded)
            return false;
        if (attempt.Field == "name" && nameRefused is not null)
            await nameRefused(attempt.Detail!);
        else if (attempt.Field is "baseUrl" or "secret" && fieldRefused is not null)
            await fieldRefused(attempt.Detail!, attempt.Field);
        else
            failed(attempt.Detail ?? "That didn't work.");
        return true;
    }
}
