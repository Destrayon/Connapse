namespace Connapse.Core.Utilities;

/// <summary>
/// Where Cognito sends a person back after they connect, and whether this deployment has an address
/// Cognito is willing to send them to at all.
/// </summary>
/// <remarks>
/// Derived from Connapse's own address rather than stored. The callback registered in the pool and
/// the <c>redirect_uri</c> sent at connect time must match exactly — Cognito rejects a difference of
/// a single character — and two values that are computed the same way cannot disagree, while a
/// stored one goes stale the moment the deployment moves and says nothing about having done so.
/// </remarks>
public static class CognitoRedirect
{
    /// <summary>The path Cognito returns to. Must match the route in <c>CloudIdentityEndpoints</c>.</summary>
    public const string CallbackPath = "/api/v1/auth/cloud/cognito/callback";

    /// <summary>
    /// The hosts Cognito will accept over plain HTTP.
    /// </summary>
    /// <remarks>
    /// Listed rather than deferred to <see cref="Uri.IsLoopback"/>, which is broader: it treats the
    /// whole 127.0.0.0/8 block as loopback, so <c>http://127.0.0.2</c> would pass here and then be
    /// refused by AWS. Matching what Cognito documents keeps the rejection on this side, where it
    /// can be explained.
    /// </remarks>
    private static readonly string[] PlainHttpHosts = ["localhost", "127.0.0.1", "::1"];

    /// <summary>
    /// Whether Cognito would accept a callback at this origin.
    /// </summary>
    /// <param name="origin">Connapse's own address, scheme and authority — "https://example.com".</param>
    /// <remarks>
    /// HTTPS anywhere, or plain HTTP on loopback. The callback carries an authorization code, so a
    /// plain-HTTP hop across a network puts a credential on the wire in cleartext; the loopback
    /// exception is safe because there is no wire. Cognito enforces this itself, so the only choice
    /// here is whether an administrator learns it now or after setting up a pool.
    /// </remarks>
    public static bool IsUsableOrigin(string? origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme == Uri.UriSchemeHttps)
            return true;

        if (uri.Scheme != Uri.UriSchemeHttp)
            return false;

        // IdnHost rather than Host: the bracketed form of an IPv6 literal arrives as "[::1]".
        return PlainHttpHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The callback URL to register in the pool's app client, or null when
    /// <see cref="IsUsableOrigin"/> is false.
    /// </summary>
    /// <remarks>
    /// Null rather than a best-effort string. The only thing an administrator does with this value
    /// is paste it into AWS, and handing them one that Cognito will refuse wastes the trip.
    /// </remarks>
    public static string? CallbackFor(string? origin) =>
        IsUsableOrigin(origin)
            ? new Uri(new Uri(origin!), CallbackPath).ToString()
            : null;
}
