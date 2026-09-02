using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connapse.Storage.CloudScope.RolesAnywhere;

/// <summary>A locally generated Roles Anywhere keypair: the public certificate and its private key, as PEM.</summary>
public sealed record RolesAnywhereKeyMaterial(string CertificatePem, string PrivateKeyPem);

/// <summary>
/// Generates the self-signed keypair Connapse registers as its own Roles Anywhere trust anchor and signs
/// CreateSession with. The private key never leaves the host; only the certificate is uploaded to AWS.
/// </summary>
public static class RolesAnywhereKeyGenerator
{
    /// <summary>Generates an RSA-2048 self-signed certificate + private key as PEM strings.</summary>
    public static RolesAnywhereKeyMaterial Generate(
        string? subjectCommonName = null, TimeProvider? timeProvider = null)
    {
        string commonName = string.IsNullOrWhiteSpace(subjectCommonName) ? "connapse-rolesanywhere" : subjectCommonName;
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // Self-signed cert used as BOTH the trust-anchor CA and the end-entity signing cert:
        // mark it a CA and allow both cert-signing and digital-signature so AWS accepts it in either role.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, critical: true));

        using X509Certificate2 certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
        return new RolesAnywhereKeyMaterial(certificate.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }
}
