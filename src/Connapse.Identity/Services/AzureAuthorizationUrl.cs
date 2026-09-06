using Connapse.Core;

namespace Connapse.Identity.Services;

/// <summary>Builds the Entra ID v2.0 authorization-code request URL for the sign-in flow.</summary>
public static class AzureAuthorizationUrl
{
    /// <summary>
    /// Builds the URL to redirect the browser to at Entra, requesting an authorization code with
    /// PKCE (S256) and an OIDC id token nonce.
    /// </summary>
    public static string Build(AzureAdSignInSettings settings, string state, string nonce, string codeChallenge)
    {
        string query = string.Join('&',
            $"client_id={Uri.EscapeDataString(settings.ClientId ?? string.Empty)}",
            "response_type=code",
            $"redirect_uri={Uri.EscapeDataString(settings.RedirectUri ?? string.Empty)}",
            "response_mode=query",
            $"scope={Uri.EscapeDataString("openid profile")}",
            $"state={Uri.EscapeDataString(state)}",
            $"nonce={Uri.EscapeDataString(nonce)}",
            $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
            "code_challenge_method=S256");

        return $"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/authorize?{query}";
    }
}
