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
    public void Create_NothingConfigured_Throws_RatherThanUsingWhateverIdentityTheHostHas()
    {
        // An empty record is a reset or a lost row, not a request to use the host's identity;
        // that request is the explicit flag. Continuing as the host would be a broader identity
        // nobody chose.
        var act = () => AzureCredentialChainFactory.Create(new AzureProviderSettings(), _ => null);
        act.Should().Throw<InvalidOperationException>().WithMessage("*No Azure identity is configured*");
    }

    [Fact]
    public void Create_CertificateAppBesideUserAssignedIdentity_Throws_RatherThanPickingTheCertificate()
    {
        var settings = new AzureProviderSettings
        {
            TenantId = "t", ClientId = "c", ClientCertificatePath = "x.pem", UserAssignedManagedIdentityClientId = "mi",
        };
        var act = () => AzureCredentialChainFactory.Create(settings, _ => SelfSigned());
        act.Should().Throw<InvalidOperationException>().WithMessage("*both*");
    }

    [Fact]
    public void Create_NoCert_UserAssignedManagedIdentity()
    {
        var settings = new AzureProviderSettings { UserAssignedManagedIdentityClientId = "mi-client" };
        var cred = AzureCredentialChainFactory.Create(settings, _ => null);
        FirstSource(cred).Should().BeOfType<ManagedIdentityCredential>();
    }

    [Fact]
    public void Create_TenantAndUserAssignedManagedIdentity_UsesManagedIdentity()
    {
        // The provider page records the tenant for every identity. A tenant beside a managed
        // identity id is not certificate intent, and must not be refused as a half-filled one.
        var settings = new AzureProviderSettings { TenantId = "t", UserAssignedManagedIdentityClientId = "mi-client" };
        var cred = AzureCredentialChainFactory.Create(settings, _ => null);
        Sources(cred).Should().ContainSingle().Which.Should().BeOfType<ManagedIdentityCredential>();
    }

    [Fact]
    public void Create_HostManagedIdentityFlag_UsesTheSystemAssignedIdentity_TenantOrNot()
    {
        // The guided setup on an Azure host records only the flag (and the tenant, for display);
        // that must be the host's own identity, never a refusal for a "half-filled" certificate.
        var settings = new AzureProviderSettings { TenantId = "t", UseHostManagedIdentity = true, ManagedIdentityPrincipalId = "oid" };
        var cred = AzureCredentialChainFactory.Create(settings, _ => null);
        Sources(cred).Should().ContainSingle().Which.Should().BeOfType<ManagedIdentityCredential>();
        AzureCredentialChainFactory.IsHostManagedIdentity(settings).Should().BeTrue();
        AzureCredentialChainFactory.IsManagedIdentity(settings).Should().BeTrue();
    }

    [Fact]
    public void Create_HostFlagAndUserAssignedIdTogether_Throws_RatherThanChoosing()
    {
        // Neither side is picked: the flag and the id name different identities.
        var settings = new AzureProviderSettings { UseHostManagedIdentity = true, UserAssignedManagedIdentityClientId = "mi" };
        var act = () => AzureCredentialChainFactory.Create(settings, _ => null);
        act.Should().Throw<InvalidOperationException>().WithMessage("*both*");
        AzureCredentialChainFactory.IsManagedIdentity(settings).Should().BeFalse();

        // The same for the flag beside a certificate app: the certificate must not win silently.
        var withApp = new AzureProviderSettings { TenantId = "t", UseHostManagedIdentity = true, ClientId = "c", ClientCertificatePath = "x.pem" };
        var actApp = () => AzureCredentialChainFactory.Create(withApp, _ => SelfSigned());
        actApp.Should().Throw<InvalidOperationException>().WithMessage("*both*");
    }

    [Fact]
    public void IsHostManagedIdentity_FalseWhenACertificateOrUserAssignedFieldIsSet()
    {
        // The flag beside a certificate field is a mix the form can never produce; if it arrives
        // from configuration it is certificate intent and must fail closed like any other mix.
        AzureCredentialChainFactory.IsHostManagedIdentity(
            new AzureProviderSettings { UseHostManagedIdentity = true, ClientId = "c" }).Should().BeFalse();
        AzureCredentialChainFactory.IsHostManagedIdentity(
            new AzureProviderSettings { UseHostManagedIdentity = true, UserAssignedManagedIdentityClientId = "mi" }).Should().BeFalse();
        AzureCredentialChainFactory.IsHostManagedIdentity(new AzureProviderSettings { TenantId = "t" }).Should().BeFalse();
    }

    [Fact]
    public void IsUserAssignedManagedIdentity_FalseWhenAnyCertificateFieldIsSet()
    {
        AzureCredentialChainFactory.IsUserAssignedManagedIdentity(
            new AzureProviderSettings { TenantId = "t", UserAssignedManagedIdentityClientId = "mi" }).Should().BeTrue();
        AzureCredentialChainFactory.IsUserAssignedManagedIdentity(
            new AzureProviderSettings { UserAssignedManagedIdentityClientId = "mi", ClientId = "c" }).Should().BeFalse();
        AzureCredentialChainFactory.IsUserAssignedManagedIdentity(
            new AzureProviderSettings { UserAssignedManagedIdentityClientId = "mi", ClientCertificatePath = "x.pem" }).Should().BeFalse();
        AzureCredentialChainFactory.IsUserAssignedManagedIdentity(new AzureProviderSettings { TenantId = "t" }).Should().BeFalse();
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
    public void Create_HostIdentityFlag_ManagedIdentityOnlyChain()
    {
        // The only way to the host's identity is asking for it.
        var cred = AzureCredentialChainFactory.Create(new AzureProviderSettings { UseHostManagedIdentity = true }, _ => null);
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
