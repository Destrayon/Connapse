using Connapse.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Identity.Services;

/// <summary>
/// Orders a client's revoke against its refreshes (CON-189). Held until the transaction ends, so whichever runs
/// second sees what the first committed: a refresh after a revoke finds its token revoked, and a revoke after a
/// refresh finds the replacement token. A client known by its metadata URL has no registration row to lock, so the
/// lock is keyed by the client id.
/// </summary>
internal static class OAuthClientLock
{
    public static Task AcquireAsync(ConnapseIdentityDbContext db, string clientId, CancellationToken ct)
    {
        string key = "oauth-client:" + clientId;
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }
}
