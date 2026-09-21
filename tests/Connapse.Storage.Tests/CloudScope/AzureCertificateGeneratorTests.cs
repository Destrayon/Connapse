using System.Security.Cryptography.X509Certificates;
using Connapse.Storage.CloudScope;
using FluentAssertions;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AzureCertificateGeneratorTests
{
    [Fact]
    public void Generate_PublicPem_IsACertificateWithoutThePrivateKey()
    {
        AzureCertificateMaterial material = AzureCertificateGenerator.Generate("connapse-test");

        material.PublicCertificatePem.Should().Contain("BEGIN CERTIFICATE");
        material.PublicCertificatePem.Should().NotContain("PRIVATE KEY");

        using X509Certificate2 cert = X509Certificate2.CreateFromPem(material.PublicCertificatePem);
        cert.Subject.Should().Contain("connapse-test");
        cert.HasPrivateKey.Should().BeFalse();
    }

    [Fact]
    public void Generate_CombinedPem_HasBothAndLoadsWithItsPrivateKey_LikeProduction()
    {
        AzureCertificateMaterial material = AzureCertificateGenerator.Generate("connapse-test");

        material.CombinedPem.Should().Contain("BEGIN CERTIFICATE");
        material.CombinedPem.Should().Contain("PRIVATE KEY");

        // Production loads the cert via X509Certificate2.CreateFromPemFile on a single .pem holding both.
        string path = Path.Combine(Path.GetTempPath(), $"connapse-azure-{Guid.NewGuid():N}.pem");
        try
        {
            File.WriteAllText(path, material.CombinedPem);
            using X509Certificate2 loaded = X509Certificate2.CreateFromPemFile(path);
            loaded.HasPrivateKey.Should().BeTrue();
            loaded.Subject.Should().Contain("connapse-test");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
