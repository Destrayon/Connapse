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

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; private set; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
