using System.Security.Cryptography;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Connections;

public sealed class PostgresCustomLinkRemovalStore(
    IDbContextFactory<KnowledgeDbContext> factory,
    IDataProtectionProvider dataProtection) : ICustomLinkRemovalStore
{
    private const int MaxErrorLength = 1000;

    // The provider token's ciphertext is copied as is, so the same purpose reads it back.
    private IDataProtector Protector => dataProtection.CreateProtector("CustomProvider.v1");

    public async Task<int> EnqueueForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        // A link secret means linking was used, even if the middle man has since stopped offering it: links
        // made before then are still in its storage.
        var providers = await context.CustomProviders.AsNoTracking()
            .Where(p => p.LinkUrl != null || p.LinkSecretProtected != null)
            .Select(p => new { p.Id, p.BaseUrl, p.TokenProtected })
            .ToListAsync(ct);
        HashSet<Guid> queued = [.. await context.CustomLinkRemovals.AsNoTracking()
            .Where(r => r.UserId == userId)
            .Select(r => r.ProviderId)
            .ToListAsync(ct)];

        DateTime now = DateTime.UtcNow;

        // A retried deletion owns the entries an earlier attempt left waiting: restart their clock, so the
        // job's one-day cleanup cannot remove them while this attempt runs.
        await context.CustomLinkRemovals.Where(r => r.UserId == userId && r.NextAttemptAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedAt, now), ct);

        List<CustomLinkRemovalEntity> added = providers.Where(p => !queued.Contains(p.Id)).Select(p => new CustomLinkRemovalEntity
        {
            Id = Guid.NewGuid(),
            ProviderId = p.Id,
            UserId = userId,
            BaseUrl = p.BaseUrl,
            TokenProtected = p.TokenProtected,
            CreatedAt = now,
        }).ToList();
        context.CustomLinkRemovals.AddRange(added);
        await context.SaveChangesAsync(ct);
        return added.Count;
    }

    public async Task ActivateForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        DateTime now = DateTime.UtcNow;
        await context.CustomLinkRemovals.Where(r => r.UserId == userId && r.NextAttemptAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.NextAttemptAt, now), ct);
    }

    public async Task CancelForUserAsync(Guid userId, DateTime? queuedBefore = null, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        await context.CustomLinkRemovals
            .Where(r => r.UserId == userId && r.NextAttemptAt == null && (queuedBefore == null || r.CreatedAt < queuedBefore))
            .ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> WaitingUsersAsync(DateTime queuedBefore, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        return await context.CustomLinkRemovals.AsNoTracking()
            .Where(r => r.NextAttemptAt == null && r.CreatedAt < queuedBefore)
            .Select(r => r.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CustomLinkRemoval>> DueAsync(DateTime now, int take, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        List<CustomLinkRemovalEntity> due = await context.CustomLinkRemovals.AsNoTracking()
            .Where(r => r.NextAttemptAt != null && r.NextAttemptAt <= now)
            .OrderBy(r => r.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
        return due.Select(r => new CustomLinkRemoval(
            r.Id, r.ProviderId, r.UserId, r.BaseUrl, Unprotect(r.TokenProtected), r.CreatedAt, r.Attempts, r.AlertedAt)).ToList();
    }

    public async Task CompleteAsync(Guid id, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        await context.CustomLinkRemovals.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task RetryLaterAsync(Guid id, DateTime nextAttemptAt, string error, bool alerted, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        string truncated = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        DateTime now = DateTime.UtcNow;
        await context.CustomLinkRemovals.Where(r => r.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Attempts, r => r.Attempts + 1)
            .SetProperty(r => r.NextAttemptAt, nextAttemptAt)
            .SetProperty(r => r.LastError, truncated)
            .SetProperty(r => r.AlertedAt, r => alerted && r.AlertedAt == null ? now : r.AlertedAt), ct);
    }

    private string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try
        {
            return Protector.Unprotect(ciphertext);
        }
        catch (CryptographicException)
        {
            return null; // the job then tries the provider's current token, or keeps failing and reports it
        }
    }
}
