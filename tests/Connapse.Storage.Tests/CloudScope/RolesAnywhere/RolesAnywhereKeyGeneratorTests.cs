using System.Linq;
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
    public void Generate_LeafLoadsWithItsKeyAndSignsViaTheSigner()
    {
        RolesAnywhereKeyMaterial material = RolesAnywhereKeyGenerator.Generate();

        using X509Certificate2 leaf = X509Certificate2.CreateFromPem(material.LeafCertificatePem, material.LeafPrivateKeyPem);
        leaf.HasPrivateKey.Should().BeTrue();

        byte[] signature = RolesAnywhereSigner.SignBytes(
            leaf, RolesAnywhereSigner.RsaAlgorithm, Encoding.UTF8.GetBytes("string-to-sign"));

        using RSA pub = leaf.GetRSAPublicKey()!;
        pub.VerifyData(Encoding.UTF8.GetBytes("string-to-sign"), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
           .Should().BeTrue();
    }

    [Fact]
    public void Generate_LeafIsCaFalseWithDigitalSignature()
    {
        RolesAnywhereKeyMaterial material = RolesAnywhereKeyGenerator.Generate();
        using X509Certificate2 leaf = X509Certificate2.CreateFromPem(material.LeafCertificatePem, material.LeafPrivateKeyPem);

        X509BasicConstraintsExtension basicConstraints = leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        X509KeyUsageExtension keyUsage = leaf.Extensions.OfType<X509KeyUsageExtension>().Single();

        basicConstraints.CertificateAuthority.Should().BeFalse();
        keyUsage.KeyUsages.Should().HaveFlag(X509KeyUsageFlags.DigitalSignature);
    }

    [Fact]
    public void Generate_CaIsCaTrueWithKeyCertSign()
    {
        RolesAnywhereKeyMaterial material = RolesAnywhereKeyGenerator.Generate();
        using X509Certificate2 ca = X509Certificate2.CreateFromPem(material.CaCertificatePem);

        X509BasicConstraintsExtension basicConstraints = ca.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        X509KeyUsageExtension keyUsage = ca.Extensions.OfType<X509KeyUsageExtension>().Single();

        basicConstraints.CertificateAuthority.Should().BeTrue();
        keyUsage.KeyUsages.Should().HaveFlag(X509KeyUsageFlags.KeyCertSign);
    }

    [Fact]
    public void Generate_LeafIsIssuedByTheCa()
    {
        RolesAnywhereKeyMaterial material = RolesAnywhereKeyGenerator.Generate();
        using X509Certificate2 ca = X509Certificate2.CreateFromPem(material.CaCertificatePem);
        using X509Certificate2 leaf = X509Certificate2.CreateFromPem(material.LeafCertificatePem);

        leaf.Issuer.Should().Be(ca.Subject);

        using X509Chain chain = new();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;

        bool builds = chain.Build(leaf);
        builds.Should().BeTrue(because: string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation)));
    }

    [Fact]
    public void Generate_UsesGivenCommonName()
    {
        RolesAnywhereKeyMaterial material = RolesAnywhereKeyGenerator.Generate("connapse-instance-a");

        using X509Certificate2 leaf = X509Certificate2.CreateFromPem(material.LeafCertificatePem);
        using X509Certificate2 ca = X509Certificate2.CreateFromPem(material.CaCertificatePem);

        leaf.Subject.Should().Contain("CN=connapse-instance-a");
        ca.Subject.Should().Contain("CN=connapse-instance-a-ca");
    }
}
