using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Identity.Services;

/// <summary>What was recorded when a SAML sign-in was started.</summary>
/// <remarks>
/// The AuthnRequest id travels with it so the assertion that comes back can be checked against the
/// request that asked for it, rather than merely against a nonce anybody holding the redirect URL
/// could present.
/// </remarks>
public readonly record struct StartedSignIn(Guid UserId, string AuthnRequestId);

/// <summary>
/// Remembers who started a SAML sign-in, so the assertion that comes back can be attributed.
/// </summary>
/// <remarks>
/// This exists because the assertion arrives on a cross-site POST from IAM Identity Center, and a
/// session cookie set <c>SameSite=Lax</c> is not sent on one. The consumer endpoint therefore cannot
/// read who is signed in, and must be told — by a nonce carried out in the SAML <c>RelayState</c>
/// and matched back here.
/// <para>
/// The nonce is the only thing that travels. It names nothing on its own: a person who obtains one
/// learns a random string, and the user it belongs to never leaves this process. Consuming is
/// single-use, so a replayed RelayState resolves to nobody even before the assertion is examined.
/// </para>
/// <para>
/// <b>What this is not.</b> It does not prove the browser completing the sign-in is the browser
/// that started it, and it never could: anybody who can start a sign-in can send the resulting
/// Identity Center URL to somebody else, whose genuine assertion then comes back carrying this
/// nonce. That is why nothing is persisted at the consumer — see
/// <see cref="SamlLinkConfirmations"/>, which binds the outcome to a real session before it is
/// saved.
/// </para>
/// <para>
/// Short-lived, because it spans one redirect to AWS and back. Single-process, like
/// <see cref="MemorySamlReplayGuard"/> and for the same reason — several instances behind a load
/// balancer would need the sign-in to return to the one that started it, or a shared store.
/// </para>
/// </remarks>
public sealed class SamlSignInRequests(IMemoryCache cache)
{
    private const string KeyPrefix = "saml-signin:";

    /// <summary>How long a started sign-in may take before it has to be started again.</summary>
    /// <remarks>
    /// Ten minutes covers signing in at Identity Center, including a multi-factor prompt, without
    /// leaving a usable nonce lying around for an afternoon.
    /// </remarks>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Records that <paramref name="userId"/> started a sign-in, and returns the nonce.</summary>
    /// <param name="authnRequestId">
    /// The id of the AuthnRequest being sent, which the assertion must name in
    /// <c>InResponseTo</c>.
    /// </param>
    public string Start(Guid userId, string authnRequestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authnRequestId);

        string nonce = SamlNonce.Create();
        cache.Set(KeyPrefix + nonce, new StartedSignIn(userId, authnRequestId), Lifetime);
        return nonce;
    }

    /// <summary>
    /// The sign-in <paramref name="nonce"/> belongs to, or null. Single use.
    /// </summary>
    public StartedSignIn? Consume(string? nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
            return null;

        string key = KeyPrefix + nonce;
        if (!cache.TryGetValue(key, out StartedSignIn started))
            return null;

        cache.Remove(key);
        return started;
    }
}

/// <summary>Makes the random, url-safe values this flow passes through a browser.</summary>
internal static class SamlNonce
{
    /// <summary>
    /// 32 bytes of cryptographic randomness, url-safe.
    /// </summary>
    /// <remarks>
    /// These travel in a query string or a cookie and come back, so they must survive that without
    /// encoding surprises.
    /// </remarks>
    public static string Create() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
