using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connapse.Core;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class AzureOidcTokenExchangerTests
{
    private const string ClientId = "44444444-4444-4444-4444-444444444444";
    private const string TokenEndpoint = "https://login.microsoftonline.com/tenant/oauth2/v2.0/token";

    [Fact]
    public void BuildClientAssertion_SignsWithTheCertificate_AndCarriesEntraX5tHeader()
    {
        // Regression: setting x5t via AdditionalHeaderClaims throws IDX14116 at sign-in time; the
        // handler must supply it from the X509 signing credentials instead.
        (string pemPath, string expectedX5t) = WriteSelfSignedPem();
        try
        {
            var settings = new AzureAdSignInSettings { ClientId = ClientId, ClientCertificatePath = pemPath };

            string assertion = AzureOidcTokenExchanger.BuildClientAssertion(settings, TokenEndpoint);

            var jwt = new JsonWebToken(assertion);
            jwt.Alg.Should().Be(SecurityAlgorithms.RsaSha256);
            jwt.X5t.Should().Be(expectedX5t);
            jwt.Issuer.Should().Be(ClientId);
            jwt.Subject.Should().Be(ClientId);
            jwt.Audiences.Should().ContainSingle().Which.Should().Be(TokenEndpoint);
            jwt.GetClaim("jti").Value.Should().NotBeNullOrEmpty();
        }
        finally
        {
            File.Delete(pemPath);
        }
    }

    private static (string Path, string X5t) WriteSelfSignedPem()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=connapse-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        string path = Path.Combine(Path.GetTempPath(), $"connapse-signin-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, cert.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem() + "\n");
        return (path, Base64UrlEncoder.Encode(cert.GetCertHash()));
    }
}
