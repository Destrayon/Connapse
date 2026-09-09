using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connapse.Storage.CloudScope;

/// <summary>A generated self-signed certificate: the public certificate (uploaded to Entra) and the
/// combined PEM (certificate + private key) that Connapse stores on its host and authenticates with.</summary>
public sealed record AzureCertificateMaterial(string PublicCertificatePem, string CombinedPem);

/// <summary>
/// Generates a self-signed certificate and RSA key locally for Connapse's Azure app registrations.
/// Only <see cref="AzureCertificateMaterial.PublicCertificatePem"/> is uploaded to Entra (via the setup
/// script); the private key never leaves the host — the same property as the AWS Roles Anywhere flow.
/// Mirrors <see cref="RolesAnywhere.RolesAnywhereKeyGenerator"/>.
/// </summary>
public static class AzureCertificateGenerator
{
    /// <summary>Generates a 1-year self-signed certificate for <paramref name="commonName"/>.</summary>
    public static AzureCertificateMaterial Generate(string commonName, TimeProvider? clock = null)
    {
        DateTimeOffset now = (clock ?? TimeProvider.System).GetUtcNow();

        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // The certificate signs the client assertion Connapse presents to Entra.
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));

        using X509Certificate2 certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));

        string publicPem = certificate.ExportCertificatePem();
        string privateKeyPem = key.ExportPkcs8PrivateKeyPem();

        // One PEM holding both, which X509Certificate2.CreateFromPemFile reads back as cert + key.
        string combined = publicPem + "\n" + privateKeyPem + "\n";
        return new AzureCertificateMaterial(publicPem, combined);
    }
}
