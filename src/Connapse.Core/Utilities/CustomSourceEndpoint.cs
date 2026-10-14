namespace Connapse.Core.Utilities;

/// <summary>
/// The rules a custom source's middle-man URL must meet before Connapse calls it: absolute, HTTPS, no
/// credentials, query or fragment in the URL. Plain HTTP is accepted only for hosts an administrator listed
/// in <c>CustomSources:AllowedHttpHosts</c> — a middle man on the same machine or Docker network.
/// </summary>
public static class CustomSourceEndpoint
{
    public const string AllowedHttpHostsKey = "CustomSources:AllowedHttpHosts";

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
            error = "Enter the middle man's full address, for example https://bridge.example.com.";
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
