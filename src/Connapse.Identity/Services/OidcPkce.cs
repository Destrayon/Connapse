using System.Security.Cryptography;
using System.Text;

namespace Connapse.Identity.Services;

/// <summary>
/// Generates a PKCE (RFC 7636) verifier/challenge pair for the Entra authorization-code flow.
/// </summary>
/// <remarks>
/// S256 only — the challenge sent to Entra is never the plaintext verifier, so a value observed
/// in the authorization request (redirect URL, browser history, proxy logs) cannot be replayed
/// to redeem a code without also knowing the verifier held back for the token exchange.
/// </remarks>
public static class OidcPkce
{
    /// <summary>Creates a new (verifier, challenge) pair. <c>challenge = base64url(SHA256(ASCII(verifier)))</c>.</summary>
    public static (string Verifier, string Challenge) Create()
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));

        using SHA256 sha = SHA256.Create();
        string challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));

        return (verifier, challenge);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
