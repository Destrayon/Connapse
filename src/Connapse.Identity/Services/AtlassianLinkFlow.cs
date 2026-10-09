using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Identity.Services;

/// <summary>What was recorded when an Atlassian sign-in was started: the PKCE verifier (when PKCE is on) and who started it.</summary>
/// <param name="RevocationGeneration">The user's unlink count when the sign-in began; the link is saved only while it is unchanged.</param>
public sealed record AtlassianPendingSignIn(string State, string? CodeVerifier, Guid UserId, DateTime ExpiresAtUtc, DateTime StartedAtUtc, long RevocationGeneration);

/// <summary>An Atlassian account the callback resolved, held until a signed-in user can be shown to own it.</summary>
/// <param name="SignInStartedAtUtc">When the sign-in began: an unlink after it refuses the link, however long the callback took.</param>
/// <param name="RevocationGeneration">Carried from <see cref="AtlassianPendingSignIn.RevocationGeneration"/>.</param>
public sealed record PendingAtlassianLink(Guid StartedByUserId, string AccountId, string DisplayName, string? Email, DateTime SignInStartedAtUtc, long RevocationGeneration);

/// <summary>
/// The in-flight state of linking an Atlassian account, the same three steps as
/// <see cref="GitHubLinkFlow"/> for the same reason: <c>state</c> proves a callback belongs to a
/// sign-in this deployment started, not that the browser completing it is the one that started it.
/// The callback only parks the outcome under a one-time code in an HttpOnly cookie, and
/// <c>/atlassian/confirm</c> — which needs a session — saves it only for the user who started it.
/// </summary>
/// <remarks>Single-process and memory-backed; each entry expires at its own deadline.</remarks>
public sealed class AtlassianLinkFlow(IMemoryCache cache)
{
    public static readonly TimeSpan SignInLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ConfirmLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RevocationLifetime = TimeSpan.FromMinutes(15);

    private const string SignInPrefix = "atlassian-signin:";
    private const string ConfirmPrefix = "atlassian-confirm:";
    private const string RevokedPrefix = "atlassian-link-revoked:";

    private sealed class Slot(PendingAtlassianLink link)
    {
        public readonly PendingAtlassianLink Link = link;
        public int Claimed;
    }

    public void AddSignIn(AtlassianPendingSignIn pending) =>
        cache.Set(SignInPrefix + pending.State, pending, new DateTimeOffset(pending.ExpiresAtUtc, TimeSpan.Zero));

    /// <summary>The pending sign-in for <paramref name="state"/>, once, unless the user unlinked since it began.</summary>
    public AtlassianPendingSignIn? TakeSignIn(string? state)
    {
        if (string.IsNullOrEmpty(state)) return null;
        string key = SignInPrefix + state;
        if (!cache.TryGetValue(key, out AtlassianPendingSignIn? pending) || pending is null)
            return null;

        cache.Remove(key);
        return WasRevokedSince(pending.UserId, pending.StartedAtUtc) ? null : pending;
    }

    /// <summary>Parks <paramref name="link"/> and returns the one-time code that claims it.</summary>
    public string Park(PendingAtlassianLink link)
    {
        string code = SamlNonce.Create();
        cache.Set(ConfirmPrefix + code, new Slot(link), ConfirmLifetime);
        return code;
    }

    /// <summary>The link <paramref name="code"/> claims, or null. Exactly one caller wins, even concurrently.</summary>
    public PendingAtlassianLink? Claim(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        string key = ConfirmPrefix + code;
        if (!cache.TryGetValue(key, out Slot? slot) || slot is null)
            return null;

        if (Interlocked.Exchange(ref slot.Claimed, 1) != 0)
            return null;

        cache.Remove(key);
        return WasRevokedSince(slot.Link.StartedByUserId, slot.Link.SignInStartedAtUtc) ? null : slot.Link;
    }

    /// <summary>Refuses every sign-in and parked link the user started before now — called on unlink.</summary>
    public void RevokeFor(Guid userId) => cache.Set(RevokedPrefix + userId, DateTime.UtcNow, RevocationLifetime);

    /// <summary>Whether the user unlinked at or after <paramref name="startedAtUtc"/>.</summary>
    public bool WasRevokedSince(Guid userId, DateTime startedAtUtc) =>
        cache.TryGetValue(RevokedPrefix + userId, out DateTime revokedAt) && revokedAt >= startedAtUtc;
}
