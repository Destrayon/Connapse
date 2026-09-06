using System.Collections.Concurrent;

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
/// Single-process by design, like <see cref="SamlSignInRequests"/> and for the same reason: it
/// spans one redirect to Entra and back, and several instances behind a load balancer would need
/// the callback to land on the instance that started the sign-in, or a shared store.
/// </remarks>
public sealed class AzureSignInRequests
{
    private readonly ConcurrentDictionary<string, AzurePendingSignIn> _pending = new();

    /// <summary>Records that a sign-in was started for <paramref name="pending"/>'s state.</summary>
    public void Add(AzurePendingSignIn pending) => _pending[pending.State] = pending;

    /// <summary>
    /// Removes and returns the pending sign-in for <paramref name="state"/>, or null if it does
    /// not exist or has expired. Single use.
    /// </summary>
    public AzurePendingSignIn? TakeByState(string state)
    {
        if (!_pending.TryRemove(state, out AzurePendingSignIn? pending))
            return null;

        return pending.ExpiresAtUtc < DateTime.UtcNow ? null : pending;
    }
}
