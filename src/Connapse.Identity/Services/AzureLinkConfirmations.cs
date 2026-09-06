using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Identity.Services;

/// <summary>A validated Entra id_token outcome, held until a real session can be shown to own it.</summary>
/// <param name="StartedByUserId">The Connapse user who started the sign-in at /azure/connect.</param>
/// <param name="ObjectId">The Entra object id (<c>oid</c>) the id_token resolved to.</param>
/// <param name="TenantId">The Entra tenant id (<c>tid</c>) the id_token resolved to.</param>
/// <param name="DisplayName">Display data, read from the token's <c>name</c>/<c>preferred_username</c>
/// claim. Authorizes nothing.</param>
public sealed record PendingAzureLink(
    Guid StartedByUserId, string ObjectId, string TenantId, string DisplayName);

/// <summary>
/// Holds a validated Entra id_token outcome between the callback arriving and a signed-in user
/// claiming it.
/// </summary>
/// <remarks>
/// <b>Why the link is not saved when the id_token is validated.</b> Mirrors <see
/// cref="SamlLinkConfirmations"/> and exists for the identical reason, even though the trigger is
/// different. The AWS assertion arrives on a cross-site POST that carries no session, so the
/// consumer literally cannot see who is signed in. The Entra callback is different — it is a
/// same-site top-level GET, so a session cookie may well be present — but trusting that session
/// would still be wrong: <c>state</c> only proves the callback belongs to a sign-in this
/// deployment started, not that the browser completing it is the browser that started it.
/// Anybody with a Connapse account can call /azure/connect, capture the resulting Entra
/// authorization URL without following it, and send it to a colleague. The colleague's own,
/// genuine Entra sign-in then returns to /azure/callback carrying the attacker's <c>state</c> —
/// PKCE binds the authorization code to the verifier, not to a person, and the id_token's
/// <c>nonce</c> matches because it was in the request the colleague actually completed. Every
/// check on the token passes, because the token is real; the forgery is in the pairing between
/// "who started this" and "whose identity it resolved to," exactly as with AWS.
/// <para>
/// So the outcome is parked here under a one-time code, the code goes out as an <c>HttpOnly</c>
/// cookie, and the browser is redirected to a page that requires a session. That redirect is a
/// same-site top-level GET, so both the code cookie and the session cookie are sent. Saving then
/// happens only when the session and the started-by user agree — the attacker never receives the
/// cookie (it is HttpOnly in the colleague's browser), and the colleague, who does hold it, is not
/// the user the sign-in was started by.
/// </para>
/// <para>
/// Single-process, like <see cref="SamlLinkConfirmations"/> and <see cref="AzureSignInRequests"/>.
/// </para>
/// </remarks>
public sealed class AzureLinkConfirmations(IMemoryCache cache)
{
    private const string KeyPrefix = "azure-confirm:";

    /// <summary>How long the confirmation may sit before it has to be started again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Parks <paramref name="link"/> and returns the one-time code that claims it.</summary>
    public string Start(PendingAzureLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        string code = SamlNonce.Create();
        cache.Set(KeyPrefix + code, link, Lifetime);
        return code;
    }

    /// <summary>The link <paramref name="code"/> claims, or null. Single use.</summary>
    public PendingAzureLink? Consume(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        string key = KeyPrefix + code;
        if (!cache.TryGetValue(key, out PendingAzureLink? link))
            return null;

        cache.Remove(key);
        return link;
    }
}
