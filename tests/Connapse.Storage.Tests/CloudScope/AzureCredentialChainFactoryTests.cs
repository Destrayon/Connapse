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

    [Fact]
    public void Create_TenantIdAndCertPathSetButClientIdBlank_Throws()
    {
        // Partial service-principal config: ClientId lost/blank must not silently
        // fall through to managed identity (a broader, ambient identity).
        var settings = new AzureProviderSettings { TenantId = "t", ClientCertificatePath = "cert.pfx" };
        var act = () => AzureCredentialChainFactory.Create(settings, _ => SelfSigned());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Create_ClientIdAndTenantIdSetButCertPathBlank_Throws()
    {
        var settings = new AzureProviderSettings { TenantId = "t", ClientId = "c" };
        var act = () => AzureCredentialChainFactory.Create(settings, _ => null);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Create_OnlyClientCertificatePasswordSet_Throws()
    {
        // A lone SP field is still "intent to use certificate auth" and must fail
        // closed rather than silently becoming managed-identity-only.
        var settings = new AzureProviderSettings { ClientCertificatePassword = "pw" };
        var act = () => AzureCredentialChainFactory.Create(settings, _ => SelfSigned());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Create_OnlyClientIdSetButTenantIdBlank_Throws()
    {
        var settings = new AzureProviderSettings { ClientId = "c", ClientCertificatePath = "cert.pfx" };
        var act = () => AzureCredentialChainFactory.Create(settings, _ => SelfSigned());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Create_FullServicePrincipalSet_UsesCertificateCredentialOnly()
    {
        // When the SP set is complete, the chain must not also carry a managed-identity
        // fallback source — that fallback is exactly the broader-identity fall-open path.
        var settings = new AzureProviderSettings { TenantId = "t", ClientId = "c", ClientCertificatePath = "cert.pfx" };
        var cred = AzureCredentialChainFactory.Create(settings, _ => SelfSigned());
        Sources(cred).Should().ContainSingle().Which.Should().BeOfType<ClientCertificateCredential>();
    }

    [Fact]
    public void Create_NoServicePrincipalFields_ManagedIdentityOnlyChain()
    {
        var cred = AzureCredentialChainFactory.Create(new AzureProviderSettings(), _ => null);
        Sources(cred).Should().ContainSingle().Which.Should().BeOfType<ManagedIdentityCredential>();
    }

    // Reads the private _sources array ChainedTokenCredential stores, to assert ordering/contents.
    private static TokenCredential FirstSource(TokenCredential chain) => Sources(chain)[0];

    private static TokenCredential[] Sources(TokenCredential chain)
    {
        var field = typeof(ChainedTokenCredential)
            .GetField("_sources", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (TokenCredential[])field.GetValue(chain)!;
    }
}
