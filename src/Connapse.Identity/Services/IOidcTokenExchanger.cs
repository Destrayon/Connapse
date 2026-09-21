namespace Connapse.Identity.Services;

/// <summary>
/// Redeems an Entra ID authorization code (from the OAuth 2.1 + PKCE sign-in flow) for the raw
/// id_token JWT. The id_token still needs to be validated by <see cref="AzureIdTokenValidator"/>
/// before any claim in it is trusted.
/// </summary>
public interface IOidcTokenExchanger
{
    /// <summary>Exchanges an authorization <paramref name="code"/> for a raw id_token JWT.</summary>
    Task<string> ExchangeAsync(string code, string codeVerifier, CancellationToken ct);
}
