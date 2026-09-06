using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using Connapse.Storage.ConnectionTesters;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// Proves the Azure provider's DI graph actually resolves from a real host — a clean container
/// start does not catch a missing registration, only constructing the services does. Covers the
/// gap left when <see cref="Connapse.Storage.Connectors.ConnectorFactory"/> gained a
/// <see cref="ConnapseAzureCredentials"/> constructor parameter before that type (and the Azure
/// connection tester) were registered.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AzureProviderDiIntegrationTests(SharedWebAppFixture fixture)
{
    [Fact]
    public void Di_Resolves_AzureCredentialsAndTester()
    {
        using var scope = fixture.Factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ConnapseAzureCredentials>().Should().NotBeNull();
        scope.ServiceProvider.GetServices<IConnectionTester>()
            .Should().Contain(t => t is AzureBlobConnectionTester);
        scope.ServiceProvider.GetRequiredService<IConnectorFactory>().Should().NotBeNull();
    }
}
