using Azure.Core;
using Azure.Identity;
using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AzureCredentialChainFactoryTests
{
    private static X509Certificate2 SelfSigned() =>
        new CertificateRequest("CN=connapse-test",
            System.Security.Cryptography.ECDsa.Create(),
            HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

    [Fact]
    public void Create_WithClientIdAndCert_UsesCertificateCredential()
    {
        var settings = new AzureProviderSettings { TenantId = "t", ClientId = "c" };
        var cred = AzureCredentialChainFactory.Create(settings, _ => SelfSigned());
        cred.Should().BeOfType<ChainedTokenCredential>();
        // First source is the cert credential when configured.
        FirstSource(cred).Should().BeOfType<ClientCertificateCredential>();
    }

    [Fact]
    public void Create_NoCert_SystemAssignedManagedIdentity()
    {
        var cred = AzureCredentialChainFactory.Create(new AzureProviderSettings(), _ => null);
        FirstSource(cred).Should().BeOfType<ManagedIdentityCredential>();
    }

    [Fact]
    public void Create_NoCert_UserAssignedManagedIdentity()
    {
        var settings = new AzureProviderSettings { UserAssignedManagedIdentityClientId = "mi-client" };
        var cred = AzureCredentialChainFactory.Create(settings, _ => null);
        FirstSource(cred).Should().BeOfType<ManagedIdentityCredential>();
    }

    [Fact]
    public void Create_ClientIdSetButCertMissing_Throws()
    {
        var settings = new AzureProviderSettings { TenantId = "t", ClientId = "c" };
        var act = () => AzureCredentialChainFactory.Create(settings, _ => null);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*certificate*");
    }

    // Reads the private _sources array ChainedTokenCredential stores, to assert ordering.
    private static TokenCredential FirstSource(TokenCredential chain)
    {
        var field = typeof(ChainedTokenCredential)
            .GetField("_sources", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var sources = (TokenCredential[])field.GetValue(chain)!;
        return sources[0];
    }
}
