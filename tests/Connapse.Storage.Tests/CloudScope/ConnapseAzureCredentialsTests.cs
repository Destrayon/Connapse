using Azure.Core;
using Azure.Identity;
using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class ConnapseAzureCredentialsTests
{
    [Fact]
    public void ProviderKey_IsAzure() => ConnapseAzureCredentials.ProviderKey.Should().Be("azure");

    [Fact]
    public void GetToken_WithNoAzureEnvironment_FailsClosed()
    {
        // No cert, no managed-identity endpoint reachable in a unit-test host → the chain
        // cannot produce a token and throws, rather than returning a bogus token.
        var monitor = new TestOptionsMonitor<AzureProviderSettings>(new AzureProviderSettings());
        var creds = new ConnapseAzureCredentials(monitor);
        var act = () => creds.GetToken(
            new TokenRequestContext(new[] { "https://storage.azure.com/.default" }), default);
        act.Should().Throw<CredentialUnavailableException>();
    }

    [Fact]
    public void Constructor_WithInvalidPartialServicePrincipalSettings_DoesNotThrow()
    {
        // Host-safety: a broken/partial Azure config must not take down DI construction
        // for installs that don't even use Azure (filesystem/S3/SFTP-only hosts share
        // this singleton via ConnectorFactory -> SourceSyncService).
        var settings = new AzureProviderSettings { TenantId = "t", ClientCertificatePath = "missing.pfx" };
        var monitor = new TestOptionsMonitor<AzureProviderSettings>(settings);
        var act = () => new ConnapseAzureCredentials(monitor);
        act.Should().NotThrow();
    }

    [Fact]
    public void GetToken_WithInvalidPartialServicePrincipalSettings_Throws()
    {
        // The failure surfaces lazily, on first actual token request, and fails closed
        // rather than falling through to managed identity.
        var settings = new AzureProviderSettings { TenantId = "t", ClientCertificatePath = "missing.pfx" };
        var monitor = new TestOptionsMonitor<AzureProviderSettings>(settings);
        var creds = new ConnapseAzureCredentials(monitor);
        var act = () => creds.GetToken(
            new TokenRequestContext(new[] { "https://storage.azure.com/.default" }), default);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void OnChange_AfterInvalidatingSettings_DoesNotThrowFromCallback()
    {
        // Settings reload must invalidate the cache without building/throwing inline.
        var settings = new AzureProviderSettings();
        var monitor = new TestOptionsMonitor<AzureProviderSettings>(settings);
        var creds = new ConnapseAzureCredentials(monitor);

        // Prime the cache with a valid (managed-identity-only) build.
        var act1 = () => creds.GetToken(
            new TokenRequestContext(new[] { "https://storage.azure.com/.default" }), default);
        act1.Should().Throw<CredentialUnavailableException>();

        // Reload into a broken partial config; the callback itself must not throw.
        var broken = new AzureProviderSettings { TenantId = "t", ClientCertificatePath = "missing.pfx" };
        var raiseChange = () => monitor.Set(broken);
        raiseChange.Should().NotThrow();

        // The next token request rebuilds and now fails closed on the bad config.
        var act2 = () => creds.GetToken(
            new TokenRequestContext(new[] { "https://storage.azure.com/.default" }), default);
        act2.Should().Throw<InvalidOperationException>();
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        private Action<T, string?>? _listener;

        public T CurrentValue { get; private set; } = value;
        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _listener += listener;
            return null;
        }

        public void Set(T newValue)
        {
            CurrentValue = newValue;
            _listener?.Invoke(newValue, null);
        }
    }
}
