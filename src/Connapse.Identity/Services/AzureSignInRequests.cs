using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Identity.Services;

/// <summary>What was recorded when an Entra sign-in was started.</summary>
/// <remarks>
/// The PKCE verifier and nonce travel with the state so the callback can redeem the
/// authorization code and validate the id token's nonce against the request that asked for it.
/// </remarks>
public sealed record AzurePendingSignIn(
    string State,
    string CodeVerifier,
    string Nonce,
    Guid UserId,
    DateTime ExpiresAtUtc);

/// <summary>
/// Remembers who started an Entra sign-in, keyed by the OAuth <c>state</c> value, so the
/// callback can attribute the returned authorization code and validate it belongs to a request
/// this process actually issued.
/// </summary>
/// <remarks>
/// Backed by <see cref="IMemoryCache"/>, like <see cref="SamlSignInRequests"/> and for the same
/// two reasons. First, the cache expires each entry at the sign-in's own deadline, so a sign-in
/// that is started and never completed — the browser closed at the Entra prompt, the redirect
/// URL captured but never followed — does not linger: abandoned entries are evicted on expiry
/// rather than accumulating for the life of the process. A raw dictionary that only removed on
/// redemption would grow without bound under repeated abandoned sign-ins. Second, single-process
/// by design: it spans one redirect to Entra and back, and several instances behind a load
/// balancer would need the callback to land on the instance that started the sign-in, or a
/// shared store.
/// </remarks>
public sealed class AzureSignInRequests(IMemoryCache cache)
{
    private const string KeyPrefix = "azure-signin:";

    /// <summary>Records that a sign-in was started for <paramref name="pending"/>'s state.</summary>
    /// <remarks>
    /// The entry is set to expire at the request's own <see cref="AzurePendingSignIn.ExpiresAtUtc"/>,
    /// so it is reclaimed automatically whether or not the callback ever arrives.
    /// </remarks>
    public void Add(AzurePendingSignIn pending) =>
        cache.Set(KeyPrefix + pending.State, pending, new DateTimeOffset(pending.ExpiresAtUtc, TimeSpan.Zero));

    /// <summary>
    /// Removes and returns the pending sign-in for <paramref name="state"/>, or null if it does
    /// not exist or has expired. Single use.
    /// </summary>
    public AzurePendingSignIn? TakeByState(string state)
    {
        string key = KeyPrefix + state;
        if (!cache.TryGetValue(key, out AzurePendingSignIn? pending) || pending is null)
            return null;

        cache.Remove(key);
        return pending;
    }
}
