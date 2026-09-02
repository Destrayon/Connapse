using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connapse.Storage.CloudScope.RolesAnywhere;

/// <summary>
/// A locally generated per-instance Roles Anywhere cert pair. The CA is registered as the trust
/// anchor (AWS requires CA:true); the leaf is what Connapse presents and signs CreateSession with
/// (AWS requires CA:false + DigitalSignature). The leaf is issued by the CA, so the trust anchor is
/// the leaf's direct issuer and no X-Amz-X509-Chain is needed.
/// </summary>
public sealed record RolesAnywhereKeyMaterial(
    string CaCertificatePem, string LeafCertificatePem, string LeafPrivateKeyPem);

/// <summary>
/// Generates the CA + leaf keypair Connapse registers as its own Roles Anywhere trust anchor (CA)
/// and signs CreateSession with (leaf). The private key never leaves the host; only the certificates
/// are uploaded to AWS.
/// </summary>
public static class RolesAnywhereKeyGenerator
{
    /// <summary>Generates an RSA-2048 CA + leaf certificate pair as PEM strings.</summary>
    public static RolesAnywhereKeyMaterial Generate(string? subjectCommonName = null, TimeProvider? timeProvider = null)
    {
        string commonName = string.IsNullOrWhiteSpace(subjectCommonName) ? "connapse-rolesanywhere" : subjectCommonName;
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        // Per-instance CA -> the trust anchor (CA:true / KeyCertSign).
        using RSA caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest($"CN={commonName}-ca", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, critical: false));
        using X509Certificate2 caCertificate = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));

        // Per-instance leaf -> the signing cert (CA:false / DigitalSignature), issued by the CA.
        using RSA leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest($"CN={commonName}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        leafRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, critical: false));

        byte[] serialNumber = RandomNumberGenerator.GetBytes(16);
        serialNumber[0] &= 0x7F;
        if (serialNumber[0] == 0) serialNumber[0] = 0x01;

        using X509Certificate2 leafCertificate = leafRequest.Create(caCertificate, now.AddDays(-1), now.AddYears(1), serialNumber);

        return new RolesAnywhereKeyMaterial(
            caCertificate.ExportCertificatePem(),
            leafCertificate.ExportCertificatePem(),
            leafKey.ExportPkcs8PrivateKeyPem());
    }
}
