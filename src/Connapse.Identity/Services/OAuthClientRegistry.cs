using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Identity.Services;

/// <summary>Reads and revokes OAuth clients over the registration, refresh-token and auth-code tables.</summary>
public sealed class OAuthClientRegistry(IDbContextFactory<ConnapseIdentityDbContext> factory, TimeProvider time) : IOAuthClientRegistry
{
    public async Task<IReadOnlyList<OAuthClientSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        DateTime now = time.GetUtcNow().UtcDateTime;
        var grants = (await db.RefreshTokens.AsNoTracking()
                .Where(t => t.ClientId != null && t.RevokedAt == null && t.ExpiresAt > now)
                .GroupBy(t => t.ClientId!)
                .Select(g => new { ClientId = g.Key, Count = g.Count(), Users = g.Select(t => t.UserId).Distinct().Count(), Last = g.Max(t => t.CreatedAt) })
                .ToListAsync(ct))
            .ToDictionary(g => g.ClientId, StringComparer.Ordinal);

        var registered = await db.OAuthClients.AsNoTracking().OrderBy(c => c.CreatedAt).ToListAsync(ct);
        var clients = registered.Select(c =>
        {
            var g = grants.GetValueOrDefault(c.ClientId);
            return new OAuthClientSummary(c.ClientId, c.ClientName, c.ApplicationType,
                JsonSerializer.Deserialize<List<string>>(c.RedirectUris) ?? [], true, c.CreatedAt, c.RegisteredFromIp,
                g?.Count ?? 0, g?.Users ?? 0, g?.Last);
        }).ToList();

        // A client known by its metadata URL has no registration; its grants are all that shows it has access.
        var ids = registered.Select(c => c.ClientId).ToHashSet(StringComparer.Ordinal);
        clients.AddRange(grants.Values.Where(g => !ids.Contains(g.ClientId)).OrderBy(g => g.ClientId, StringComparer.Ordinal)
            .Select(g => new OAuthClientSummary(g.ClientId, g.ClientId, "native", [], false, null, null, g.Count, g.Users, g.Last)));
        return clients;
    }

    public async Task<OAuthClientRevocation?> RevokeAsync(string clientId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        DateTime now = time.GetUtcNow().UtcDateTime;

        // Refused first, then the registration: a client mid-refresh finds its token already revoked.
        int tokens = await db.RefreshTokens
            .Where(t => t.ClientId == clientId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        int codes = await db.OAuthAuthCodes.Where(c => c.ClientId == clientId).ExecuteDeleteAsync(ct);
        bool removed = await db.OAuthClients.Where(c => c.ClientId == clientId).ExecuteDeleteAsync(ct) > 0;
        await transaction.CommitAsync(ct);

        return removed || tokens > 0 || codes > 0 ? new OAuthClientRevocation(clientId, removed, tokens, codes) : null;
    }
}
