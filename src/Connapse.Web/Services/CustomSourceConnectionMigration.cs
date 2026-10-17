using Connapse.Core;
using Connapse.Core.Interfaces;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Web.Services;

/// <summary>
/// Moves custom sources created without a connection onto custom connections that hold their middle man's
/// address and token: one connection per address and token, so sources that shared a middle man share a
/// connection. The sources keep their documents and sync cursors; nothing is re-read. Run at startup and
/// safe to run again: a source already on a connection is left alone, and a connection that already holds
/// the same address and token is reused rather than duplicated.
/// </summary>
public sealed class CustomSourceConnectionMigration(
    ISourceStore sources,
    IConnectionStore connections,
    IAuditLogger audit,
    ILogger<CustomSourceConnectionMigration> logger)
{
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        List<Source> all = (await sources.ListAsync(take: int.MaxValue, ct: ct)).ToList();
        await ClearConnectedTokensAsync(all, ct);

        List<Source> legacy = all.Where(s => s.ConnectionId is null && s.Provider == ConnectionProvider.Custom).ToList();
        if (legacy.Count == 0) return 0;

        List<Connection> existing = (await connections.ListAsync(take: int.MaxValue, ct: ct)).ToList();
        HashSet<string> names = existing.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<(string BaseUrl, string? Token), Connection> byMiddleMan = [];
        foreach (Connection connection in existing)
        {
            if (connection.Provider != ConnectionProvider.Custom
                || CustomConnectionConfig.BaseUrlOf(connection.ConfigJson) is not { } baseUrl) continue;
            try
            {
                string? token = connection.HasSecret ? await connections.GetSecretAsync(connection.Id, ct) : null;
                byMiddleMan.TryAdd((baseUrl, token), connection);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException)
            {
                // Not reusable, but no reason to stop moving the sources: a new connection is made instead.
                logger.LogWarning(ex, "Connection {ConnectionId}'s token cannot be read; it is not reused for moved custom sources", connection.Id);
            }
        }

        int moved = 0;
        foreach (Source source in legacy)
        {
            if (CustomSourceScope.TryParse(source.ScopeJson) is not { } scope) continue;

            string? token;
            try
            {
                token = source.HasSecret ? await sources.GetSecretAsync(source.Id, ct) : null;
            }
            catch (SourceSecretUnavailableException ex)
            {
                // Left as it is: its token cannot be read, so it could not sync on a connection either.
                logger.LogWarning(ex, "Custom source {SourceId} was not moved onto a connection: its token cannot be decrypted", source.Id);
                continue;
            }

            if (!Uri.TryCreate(scope.BaseUrl, UriKind.Absolute, out Uri? address))
            {
                logger.LogWarning("Custom source {SourceId} was not moved onto a connection: its address cannot be read", source.Id);
                continue;
            }

            try
            {
                if (!byMiddleMan.TryGetValue((scope.BaseUrl, token), out Connection? target))
                {
                    string name = UniqueName(address.Host, names);
                    target = await connections.CreateAsync(new CreateConnectionRequest(
                        name, ConnectionProvider.Custom, CustomConnectionConfig.ForMiddleMan(scope.BaseUrl), token), createdByUserId: null);
                    names.Add(name);
                    byMiddleMan[(scope.BaseUrl, token)] = target;
                    await audit.LogAsync("connection.created", "connection", target.Id.ToString(),
                        new { target.Name, Provider = target.Provider.ToString(), Reason = "custom sources moved onto a connection" }, ct);
                }

                if (await sources.MoveOntoConnectionAsync(source.Id, target.Id, ct))
                {
                    moved++;
                    await audit.LogAsync("source.updated", "source", source.Id.ToString(),
                        new { source.Name, ConnectionId = target.Id, Reason = "moved onto a custom connection" }, ct);
                    logger.LogInformation("Moved custom source {SourceId} ({Name}) onto connection {ConnectionId}",
                        source.Id, Sanitize(source.Name), target.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Left as it is, still reading with its own token; the next start tries again.
                logger.LogWarning(ex, "Custom source {SourceId} was not moved onto a connection", source.Id);
            }
        }

        return moved;
    }

    /// <summary>
    /// Custom sources made on a custom connection before sources stopped holding tokens read with their own; they
    /// now read with their connection's (or its provider's). The token they held is no longer used, so it is
    /// removed rather than left stored, and each source is logged so an administrator can see what changed.
    /// </summary>
    private async Task ClearConnectedTokensAsync(List<Source> all, CancellationToken ct)
    {
        foreach (Source source in all.Where(s => s.ConnectionId is not null && s.HasSecret
                     && CustomSourceScope.TryParse(s.ScopeJson) is not null))
        {
            try
            {
                if (!await sources.ClearSecretAsync(source.Id, ct)) continue;
                await audit.LogAsync("source.updated", "source", source.Id.ToString(),
                    new { source.Name, Reason = "reads with its connection's token; its own token was removed" }, ct);
                logger.LogWarning(
                    "Custom source {SourceId} ({Name}) now reads with its connection's token; the token it held was removed. "
                    + "If its connection's token cannot read the scope, the next sync shows Token refused.",
                    source.Id, Sanitize(source.Name));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Custom source {SourceId}'s own token could not be removed", source.Id);
            }
        }
    }

    private static string UniqueName(string host, HashSet<string> taken)
    {
        // Within the store's 128-character limit, with room for a " (n)" suffix.
        host = host.Length > 120 ? host[..120] : host;
        string name = host;
        for (int n = 2; taken.Contains(name); n++)
            name = $"{host} ({n})";
        return name;
    }
}
