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

        // Registered concrete-only, matching S3ConnectionTester/SftpConnectionTester — this is
        // the exact path Connections.razor's `@inject AzureBlobConnectionTester` resolves
        // through. A prior interface-only registration (AddScoped<IConnectionTester,
        // AzureBlobConnectionTester>) satisfied GetServices<IConnectionTester>() but left the
        // concrete type unresolvable, which threw InvalidOperationException at component init.
        scope.ServiceProvider.GetRequiredService<AzureBlobConnectionTester>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IConnectorFactory>().Should().NotBeNull();
    }
}
