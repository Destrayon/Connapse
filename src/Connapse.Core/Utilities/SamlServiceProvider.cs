namespace Connapse.Core.Utilities;

/// <summary>
/// The two values an administrator types into the IAM Identity Center console to describe Connapse,
/// and whether this deployment has an address worth typing at all.
/// </summary>
/// <remarks>
/// Derived from Connapse's own address rather than stored. What is registered in the application and
/// what Connapse checks each assertion against must match exactly — the destination and audience
/// checks in <c>SamlAssertionValidator</c> are ordinal comparisons — and two values computed the
/// same way cannot disagree, while a stored one goes stale the moment the deployment moves and says
/// nothing about having done so.
/// <para>
/// The consequence is worth stating plainly on the setup page: moving Connapse to a new address
/// means editing the application in AWS. Nothing else in the product has that property, which is
/// why it is easy to forget.
/// </para>
/// </remarks>
public static class SamlServiceProvider
{
    /// <summary>
    /// The path Identity Center posts the assertion to. Must match the route in
    /// <c>CloudIdentityEndpoints</c>.
    /// </summary>
    public const string AcsPath = "/api/v1/auth/cloud/aws/acs";

    /// <summary>The path that distinguishes Connapse's entity id from its bare origin.</summary>
    /// <remarks>
    /// An entity id is an opaque identifier rather than an address, and nothing fetches it. It is
    /// built from the origin anyway so that two Connapse deployments in one directory cannot
    /// collide, which they would if the id were a constant.
    /// </remarks>
    public const string EntityIdPath = "/saml/connapse";

    /// <summary>
    /// The hosts an assertion may be posted to over plain HTTP.
    /// </summary>
    /// <remarks>
    /// Listed rather than deferred to <see cref="Uri.IsLoopback"/>, which is broader: it treats the
    /// whole 127.0.0.0/8 block as loopback, so <c>http://127.0.0.2</c> would pass here.
    /// </remarks>
    private static readonly string[] PlainHttpHosts = ["localhost", "127.0.0.1", "::1"];

    /// <summary>
    /// Whether an assertion could safely be posted back to this origin.
    /// </summary>
    /// <param name="origin">Connapse's own address, scheme and authority — "https://example.com".</param>
    /// <remarks>
    /// HTTPS anywhere, or plain HTTP on loopback. A signed assertion is a bearer credential until it
    /// expires: anyone who reads one off the wire can post it themselves and be taken for the person
    /// it names. The loopback exception is safe because there is no wire.
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
    /// The assertion consumer service URL to register, or null when <see cref="IsUsableOrigin"/> is
    /// false.
    /// </summary>
    /// <remarks>
    /// Null rather than a best-effort string. The only thing an administrator does with this value
    /// is paste it into AWS, and handing them one that would carry an assertion in cleartext is
    /// worse than handing them nothing.
    /// </remarks>
    public static string? AcsFor(string? origin) =>
        IsUsableOrigin(origin)
            ? new Uri(new Uri(origin!), AcsPath).ToString()
            : null;

    /// <summary>The audience to register, or null when the origin is unusable.</summary>
    public static string? EntityIdFor(string? origin) =>
        IsUsableOrigin(origin)
            ? new Uri(new Uri(origin!), EntityIdPath).ToString()
            : null;
}
