using System.Security.Cryptography;
using System.Text;
using Connapse.Identity.Services;
using FluentAssertions;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class OidcPkceTests
{
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Pkce_ChallengeIsBase64UrlSha256OfVerifier()
    {
        var (verifier, challenge) = OidcPkce.Create();
        using var sha = SHA256.Create();
        var expected = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        challenge.Should().Be(expected);
        challenge.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }
}
