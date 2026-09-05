using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Identity.Services;

/// <summary>A validated assertion, held until a real session can be shown to own it.</summary>
/// <param name="StartedByUserId">The Connapse user who began the sign-in.</param>
/// <param name="DirectoryUserId">The identity store id the assertion resolved to.</param>
/// <param name="DirectoryUserName">The directory user name the assertion carried.</param>
/// <param name="Email">Display data. Authorizes nothing.</param>
public sealed record PendingIdentityLink(
    Guid StartedByUserId, string DirectoryUserId, string DirectoryUserName, string? Email);

/// <summary>
/// Holds a validated SAML outcome between the assertion arriving and a signed-in user claiming it.
/// </summary>
/// <remarks>
/// <b>Why the link is not saved when the assertion is validated.</b> The consumer endpoint knows
/// two things: which directory user signed the assertion, and which Connapse user started the
/// sign-in — the second from a nonce in <c>RelayState</c>. Nothing ties those to the same person.
/// Anybody with a Connapse account can start a sign-in, send the resulting Identity Center URL to a
/// colleague, and have that colleague's genuine, correctly signed assertion recorded against their
/// own account — and then search with the colleague's access grants. Every check on the assertion
/// passes, because the assertion is real. The forgery is in the pairing, not the document.
/// <para>
/// So the outcome is parked here under a one-time code, the code goes out as an
/// <c>HttpOnly</c> cookie, and the browser is redirected to a page that requires a session. That
/// redirect is a same-site top-level GET, so both the code cookie and the session cookie are sent —
/// which is precisely what the cross-site POST from AWS could not carry. Saving then happens only
/// when the session and the started-by user agree.
/// </para>
/// <para>
/// The code lives in a cookie rather than the redirect URL so that it is not readable by script,
/// not in browser history, and not in anything the person might paste to somebody else.
/// </para>
/// <para>
/// Single-process, like <see cref="SamlSignInRequests"/> and <see cref="MemorySamlReplayGuard"/>.
/// A deployment running several instances would need the confirmation to return to the instance
/// that consumed the assertion, or a shared store.
/// </para>
/// </remarks>
public sealed class SamlLinkConfirmations(IMemoryCache cache)
{
    private const string KeyPrefix = "saml-confirm:";

    /// <summary>How long the confirmation may sit before it has to be started again.</summary>
    /// <remarks>
    /// One redirect, plus a sign-in if the person was not signed in to Connapse when they came
    /// back. Five minutes covers that without leaving a claimable outcome around.
    /// </remarks>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Parks <paramref name="link"/> and returns the one-time code that claims it.</summary>
    public string Start(PendingIdentityLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        string code = SamlNonce.Create();
        cache.Set(KeyPrefix + code, link, Lifetime);
        return code;
    }

    /// <summary>The link <paramref name="code"/> claims, or null. Single use.</summary>
    public PendingIdentityLink? Consume(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        string key = KeyPrefix + code;
        if (!cache.TryGetValue(key, out PendingIdentityLink? link))
            return null;

        cache.Remove(key);
        return link;
    }
}
