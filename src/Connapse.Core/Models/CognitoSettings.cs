namespace Connapse.Core;

/// <summary>
/// The customer's Amazon Cognito user pool — the identity provider they host inside their own AWS
/// account so that Connapse can prove which AWS identity a user is.
/// </summary>
/// <remarks>
/// A mutable class with a <c>SectionName</c> rather than a record, matching <see cref="AzureAdSettings"/>
/// and every other settings category: they are bound by the options system and edited by an admin
/// form, both of which want settable properties.
/// <para>
/// <see cref="ClientSecret"/> is a secret at rest in the settings table like every other provider
/// credential here. It is not a per-user secret — one pool, one client, shared by the deployment.
/// </para>
/// </remarks>
public class CognitoSettings
{
    public const string SectionName = "Identity:Cognito";

    /// <summary>
    /// The pool's OIDC issuer, <c>https://cognito-idp.{region}.amazonaws.com/{poolId}</c>.
    /// </summary>
    /// <remarks>
    /// This exact string is what an issued token carries as its <c>iss</c> claim and what the
    /// Identity Center trusted token issuer is registered against. A mismatch between the two is
    /// rejected at exchange time with an error that names neither side.
    /// </remarks>
    public string IssuerUrl { get; set; } = string.Empty;

    /// <summary>
    /// The pool's hosted UI domain, which is where <c>/oauth2/authorize</c> lives.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="IssuerUrl"/> and genuinely needed: the token exchange against
    /// Identity Center works on a pool with no domain, but the browser redirect that starts this
    /// flow does not exist without one.
    /// </remarks>
    public string Domain { get; set; } = string.Empty;

    /// <summary>The app client id. Also the audience the Identity Center grant authorises.</summary>
    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    /// <summary>True when every field needed to complete a connection is present and usable.</summary>
    /// <remarks>
    /// The URLs must be HTTPS, with no loopback exception. Both carry an authorization code or a
    /// token, so a plain-HTTP hop puts a credential on the wire in cleartext — and a non-empty
    /// check alone would happily accept one. Unlike the OAuth redirect URI (Connapse's own
    /// address, computed from the incoming request rather than stored here), <see cref="IssuerUrl"/>
    /// and <see cref="Domain"/> are always AWS-hosted: there is no such thing as a localhost
    /// Cognito pool, so a loopback exception on these two would only widen what is accepted
    /// without buying anything. Cognito enforces HTTPS on its side too, so anything else was never
    /// going to work anyway; failing here says why, rather than leaving the operator with a
    /// rejection from AWS.
    /// </remarks>
    public bool IsConfigured =>
        IsSecureUrl(IssuerUrl)
        && IsSecureUrl(Domain)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(Region);

    /// <summary>HTTPS only — these are the provider's own endpoints, not Connapse's.</summary>
    /// <remarks>
    /// No loopback exception. That exception belongs to the OAuth redirect URI, which is
    /// Connapse's own address and is not part of this settings type at all — it is computed from
    /// the incoming request when the flow starts. <see cref="IssuerUrl"/> and <see cref="Domain"/>
    /// are always AWS-hosted (<c>https://cognito-idp.{region}.amazonaws.com/{poolId}</c> and
    /// <c>https://{prefix}.auth.{region}.amazoncognito.com</c>), so there is no equivalent
    /// single-machine case to allow for.
    /// </remarks>
    internal static bool IsSecureUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;
}
