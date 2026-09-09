using Azure;
using Azure.Identity;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AzureBlobDiscoveryTests
{
    private static AzureBlobDiscovery Build(AzureProviderSettings settings)
    {
        var monitor = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
        monitor.CurrentValue.Returns(settings);
        return new AzureBlobDiscovery(new ConnapseAzureCredentials(monitor), monitor, NullLogger<AzureBlobDiscovery>.Instance);
    }

    private static AzureProviderSettings Configured() => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        ClientId = "22222222-2222-2222-2222-222222222222",
        ClientCertificatePath = "/nonexistent.pem",
        SubscriptionId = "33333333-3333-3333-3333-333333333333",
    };

    [Fact]
    public async Task ListStorageAccounts_NoIdentity_IsNotConfigured_WithoutCallingAzure()
    {
        var probe = await Build(new AzureProviderSettings()).ListStorageAccountsAsync();

        probe.Outcome.Should().Be(AzureProbeOutcome.NotConfigured);
        probe.Detail.Should().Contain("provider page");
    }

    [Fact]
    public async Task ListStorageAccounts_NoSubscription_IsNotConfigured()
    {
        var probe = await Build(Configured() with { SubscriptionId = null }).ListStorageAccountsAsync();

        probe.Outcome.Should().Be(AzureProbeOutcome.NotConfigured);
        probe.Detail.Should().Contain("subscription");
    }

    [Fact]
    public async Task ListContainers_NoIdentity_IsNotConfigured()
    {
        var probe = await Build(new AzureProviderSettings()).ListContainersAsync("https://acct.blob.core.windows.net");
        probe.Outcome.Should().Be(AzureProbeOutcome.NotConfigured);
    }

    [Fact]
    public async Task ListContainers_NoEndpoint_Fails_WithoutCallingAzure()
    {
        var probe = await Build(Configured()).ListContainersAsync("");
        probe.Outcome.Should().Be(AzureProbeOutcome.Failed);
        probe.Detail.Should().Contain("No storage account");
    }

    [Fact]
    public async Task CheckAccess_NoIdentity_IsNotConfigured_WithoutCallingAzure()
    {
        var probe = await Build(new AzureProviderSettings()).CheckAccessAsync();

        probe.Outcome.Should().Be(AzureProbeOutcome.NotConfigured);
        probe.Detail.Should().Contain("provider page");
    }

    [Fact]
    public async Task CheckAccess_CertificateFileMissing_IsUnusable_WithTheReason()
    {
        // The credential chain refuses to build without a readable certificate, and refuses to
        // fall through to a managed identity. Azure was never asked: this host is what needs
        // fixing, which is neither Entra saying no nor "could not confirm".
        var probe = await Build(Configured()).CheckAccessAsync();

        probe.Outcome.Should().Be(AzureProbeOutcome.Unusable);
        probe.Detail.Should().Contain("certificate");
    }

    [Fact]
    public async Task CheckAccess_Candidate_IsCheckedInsteadOfTheStoredSettings()
    {
        // Stored settings are blank (NotConfigured); the candidate is complete but its certificate
        // file is missing, so it is the candidate that produced the answer.
        var probe = await Build(new AzureProviderSettings()).CheckAccessAsync(Configured());

        probe.Outcome.Should().Be(AzureProbeOutcome.Unusable);
        probe.Detail.Should().Contain("certificate");
    }

    [Fact]
    public async Task CheckAccess_Candidate_NotConfigured_NeverCallsAzure()
    {
        var probe = await Build(Configured()).CheckAccessAsync(new AzureProviderSettings { TenantId = "t" });

        probe.Outcome.Should().Be(AzureProbeOutcome.NotConfigured);
    }

    [Theory]
    [InlineData("AADSTS700027: Client assertion contains an invalid signature", true)]
    [InlineData("AADSTS7000215: Invalid client secret provided", true)]
    [InlineData("AADSTS7000222: The provided client secret keys are expired", true)]
    [InlineData("AADSTS7000229: The client application is missing service principal in the tenant", true)]
    [InlineData("AADSTS7000112: Application 'x' is disabled", true)]
    [InlineData("AADSTS700016: Application with identifier 'x' was not found in the directory", true)]
    [InlineData("AADSTS90002: Tenant 'x' not found", true)]
    // Entra answered, but about itself, not the credential: transient, throttled, or unavailable.
    [InlineData("AADSTS90024: The request body must contain the following parameter", false)]
    [InlineData("AADSTS90033: A transient error has occurred. Please try again.", false)]
    [InlineData("AADSTS50196: The server terminated an operation because it encountered a client request loop", false)]
    [InlineData("No such host is known (login.microsoftonline.com:443)", false)]
    public void IsCredentialRefusal_OnlyForCodesThatMeanTheCredentialIsWrong(string message, bool expected) =>
        AzureBlobDiscovery.IsCredentialRefusal(new AuthenticationFailedException(message)).Should().Be(expected);

    [Fact]
    public void IsCredentialRefusal_ManagedIdentityUnavailable_IsNotARefusal() =>
        AzureBlobDiscovery.IsCredentialRefusal(new CredentialUnavailableException("AADSTS-looking text from the IMDS probe"))
            .Should().BeFalse();

    [Theory]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(404, false)]
    [InlineData(500, false)]
    public void IsDenial_OnlyForAuthFailures(int status, bool expected) =>
        AzureBlobDiscovery.IsDenial(new RequestFailedException(status, "x")).Should().Be(expected);

    [Fact]
    public void IsConfigured_MatchesTheProvidersPageRule()
    {
        AzureBlobDiscovery.IsConfigured(Configured()).Should().BeTrue();
        AzureBlobDiscovery.IsConfigured(Configured() with { ClientCertificatePath = null }).Should().BeFalse();
        AzureBlobDiscovery.IsConfigured(new AzureProviderSettings
        {
            TenantId = "t", UserAssignedManagedIdentityClientId = "mi",
        }).Should().BeTrue();
        AzureBlobDiscovery.IsConfigured(Configured() with { TenantId = null }).Should().BeFalse();
    }
}
