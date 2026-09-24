using Connapse.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Identity.Services;

/// <summary>What was recorded when a GitHub sign-in was started: the PKCE verifier and who started it.</summary>
public sealed record GitHubPendingSignIn(string State, string CodeVerifier, Guid UserId, DateTime ExpiresAtUtc, DateTime StartedAtUtc);

/// <summary>A GitHub account the callback resolved, held until a signed-in user can be shown to own it.</summary>
public sealed record PendingGitHubLink(Guid StartedByUserId, long GitHubUserId, string Login);

/// <summary>
/// The in-flight state of linking a GitHub account, mirroring the Entra flow
/// (<see cref="AzureSignInRequests"/>, <see cref="AzureLinkConfirmations"/>) for the same reason:
/// <c>state</c> proves a callback belongs to a sign-in this deployment started, not that the browser
/// completing it is the one that started it. Anyone could start a sign-in, send the GitHub URL to a
/// colleague, and have the colleague's real account linked to their own Connapse user. So the callback
/// only parks the outcome under a one-time code in an HttpOnly cookie, and <c>/github/confirm</c> —
/// which needs a session — saves it only for the user who started it.
/// </summary>
/// <remarks>Single-process and memory-backed; each entry expires at its own deadline.</remarks>
public sealed class GitHubLinkFlow(IMemoryCache cache)
{
    public static readonly TimeSpan SignInLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ConfirmLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RevocationLifetime = TimeSpan.FromMinutes(15);

    private const string SignInPrefix = "github-signin:";
    private const string ConfirmPrefix = "github-confirm:";
    private const string RevokedPrefix = "github-link-revoked:";

    private sealed class Slot(PendingGitHubLink link)
    {
        public readonly PendingGitHubLink Link = link;
        public readonly DateTime StartedAtUtc = DateTime.UtcNow;
        public int Claimed;
    }

    public void AddSignIn(GitHubPendingSignIn pending) =>
        cache.Set(SignInPrefix + pending.State, pending, new DateTimeOffset(pending.ExpiresAtUtc, TimeSpan.Zero));

    /// <summary>The pending sign-in for <paramref name="state"/>, once, unless the user unlinked since it began.</summary>
    public GitHubPendingSignIn? TakeSignIn(string? state)
    {
        if (string.IsNullOrEmpty(state)) return null;
        string key = SignInPrefix + state;
        if (!cache.TryGetValue(key, out GitHubPendingSignIn? pending) || pending is null)
            return null;

        cache.Remove(key);
        return WasRevokedSince(pending.UserId, pending.StartedAtUtc) ? null : pending;
    }

    /// <summary>Parks <paramref name="link"/> and returns the one-time code that claims it.</summary>
    public string Park(PendingGitHubLink link)
    {
        string code = SamlNonce.Create();
        cache.Set(ConfirmPrefix + code, new Slot(link), ConfirmLifetime);
        return code;
    }

    /// <summary>The link <paramref name="code"/> claims, or null. Exactly one caller wins, even concurrently.</summary>
    public PendingGitHubLink? Claim(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        string key = ConfirmPrefix + code;
        if (!cache.TryGetValue(key, out Slot? slot) || slot is null)
            return null;

        if (Interlocked.Exchange(ref slot.Claimed, 1) != 0)
            return null;

        cache.Remove(key);
        return WasRevokedSince(slot.Link.StartedByUserId, slot.StartedAtUtc) ? null : slot.Link;
    }

    /// <summary>Refuses every sign-in and parked link the user started before now — called on unlink.</summary>
    public void RevokeFor(Guid userId) => cache.Set(RevokedPrefix + userId, DateTime.UtcNow, RevocationLifetime);

    private bool WasRevokedSince(Guid userId, DateTime startedAtUtc) =>
        cache.TryGetValue(RevokedPrefix + userId, out DateTime revokedAt) && revokedAt >= startedAtUtc;
}
