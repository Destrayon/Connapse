using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connapse.Storage.Tests.CloudScope.RolesAnywhere;

internal static class CertificateTestFactory
{
    public static X509Certificate2 CreateRsa()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=connapse-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    public static X509Certificate2 CreateEc()
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=connapse-test", ecdsa, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
