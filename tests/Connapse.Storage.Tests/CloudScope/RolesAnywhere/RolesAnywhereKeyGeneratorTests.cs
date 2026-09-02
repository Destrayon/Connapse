using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Connapse.Storage.CloudScope.RolesAnywhere;
using FluentAssertions;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope.RolesAnywhere;

[Trait("Category", "Unit")]
public class RolesAnywhereKeyGeneratorTests
{
    [Fact]
    public void Generate_ProducesPemPairLoadableAsACertificateWithPrivateKey()
    {
        RolesAnywhereKeyMaterial material = RolesAnywhereKeyGenerator.Generate();

        material.CertificatePem.Should().StartWith("-----BEGIN CERTIFICATE-----");
        material.PrivateKeyPem.Should().StartWith("-----BEGIN PRIVATE KEY-----");

        using X509Certificate2 cert = X509Certificate2.CreateFromPem(material.CertificatePem, material.PrivateKeyPem);
        cert.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    public void Generate_ProducesACertificateTheSignerCanSignWith()
    {
        RolesAnywhereKeyMaterial material = RolesAnywhereKeyGenerator.Generate();
        using X509Certificate2 cert = X509Certificate2.CreateFromPem(material.CertificatePem, material.PrivateKeyPem);

        byte[] signature = RolesAnywhereSigner.SignBytes(
            cert, RolesAnywhereSigner.RsaAlgorithm, Encoding.UTF8.GetBytes("string-to-sign"));

        using RSA pub = cert.GetRSAPublicKey()!;
        pub.VerifyData(Encoding.UTF8.GetBytes("string-to-sign"), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
           .Should().BeTrue();
    }

    [Fact]
    public void Generate_SelfSignedWithGivenCommonNameAndAboutAYearValidity()
    {
        var fixedNow = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        RolesAnywhereKeyMaterial material = RolesAnywhereKeyGenerator.Generate(
            "connapse-instance-a", new FakeTimeProvider(fixedNow));

        using X509Certificate2 cert = X509Certificate2.CreateFromPem(material.CertificatePem);
        cert.Subject.Should().Contain("CN=connapse-instance-a");
        cert.Subject.Should().Be(cert.Issuer); // self-signed
        cert.NotAfter.ToUniversalTime().Should().BeCloseTo(fixedNow.AddYears(1).UtcDateTime, TimeSpan.FromDays(1));
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
