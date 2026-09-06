using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// Proves the Entra user-identity-link DI graph actually resolves from a real host — a clean
/// container start does not catch a missing registration, only constructing the services does.
/// Tasks 2-6 of the Azure Phase 3 (Entra user identity link) feature each registered one piece of
/// this graph (the link store/reader/service, <see cref="AzureAdSignInSettings"/> options, the
/// sign-in request cache, the id-token validator + token exchanger HttpClient, and the link
/// confirmation cache); this test is the guard that all of them landed together and stayed
/// resolvable as later tasks touched the same registration block.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AzureEntraLinkDiIntegrationTests(SharedWebAppFixture fixture)
{
    [Fact]
    public void Di_Resolves_EntraUserIdentityLinkGraph()
    {
        using var scope = fixture.Factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAzureIdentityLinkService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IAzureIdentityLinkReader>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IOidcTokenExchanger>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<AzureIdTokenValidator>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<AzureSignInRequests>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<AzureLinkConfirmations>().Should().NotBeNull();
    }
}
