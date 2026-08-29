using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Identity.Services;

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
    public string Start(Guid userId)
    {
        // 32 bytes of cryptographic randomness, url-safe. It travels in a query string and comes
        // back through AWS, so it must survive that without encoding surprises.
        string nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        cache.Set(KeyPrefix + nonce, userId, Lifetime);
        return nonce;
    }

    /// <summary>
    /// The user who started the sign-in <paramref name="nonce"/> belongs to, or null. Single use.
    /// </summary>
    public Guid? Consume(string? nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
            return null;

        string key = KeyPrefix + nonce;
        if (!cache.TryGetValue(key, out Guid userId))
            return null;

        cache.Remove(key);
        return userId;
    }
}
