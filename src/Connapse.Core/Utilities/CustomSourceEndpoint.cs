namespace Connapse.Core.Utilities;

/// <summary>
/// The rules a custom source's middle-man URL must meet before Connapse calls it: absolute, HTTPS, no
/// credentials, query or fragment in the URL. Plain HTTP is accepted only for hosts an administrator listed
/// in <c>CustomSources:AllowedHttpHosts</c> — a middle man on the same machine or Docker network.
/// </summary>
public static class CustomSourceEndpoint
{
    /// <summary>
    /// Whether two middle man addresses are the same, ignoring a trailing slash and the case of the scheme and host.
    /// The path's case counts: a server may route <c>/A</c> and <c>/a</c> to different backends.
    /// </summary>
    public static bool SameAddress(string? a, string? b)
    {
        string? x = a?.Trim().TrimEnd('/'), y = b?.Trim().TrimEnd('/');
        if (x is null || y is null) return x is null && y is null;
        if (!Uri.TryCreate(x, UriKind.Absolute, out Uri? left) || !Uri.TryCreate(y, UriKind.Absolute, out Uri? right))
            return string.Equals(x, y, StringComparison.Ordinal);
        return Uri.Compare(left, right, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0
            && string.Equals(left.PathAndQuery, right.PathAndQuery, StringComparison.Ordinal);
    }

    public const string AllowedHttpHostsKey = CustomSourceSettings.SectionName + ":AllowedHttpHosts";

    /// <param name="normalized">Scheme and host lower-cased, default port dropped, no trailing slash.</param>
    public static bool TryNormalize(
        string? url,
        IReadOnlyCollection<string> allowedHttpHosts,
        out string normalized,
        out string? error)
    {
        normalized = "";
        error = null;

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? uri))
        {
            error = "Enter the source server's full address, for example https://bridge.example.com.";
            return false;
        }

        bool https = uri.Scheme == Uri.UriSchemeHttps;
        bool allowedHttp = uri.Scheme == Uri.UriSchemeHttp
            && allowedHttpHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
        if (!https && !allowedHttp)
        {
            error = uri.Scheme == Uri.UriSchemeHttp
                ? $"Use HTTPS. Plain HTTP is allowed only for hosts listed in {AllowedHttpHostsKey}."
                : "Use an HTTPS address.";
            return false;
        }

        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "Leave credentials, query strings and fragments out of the address; the token goes in its own field.";
            return false;
        }

        string port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        normalized = $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{port}{uri.AbsolutePath.TrimEnd('/')}";
        return true;
    }
}
