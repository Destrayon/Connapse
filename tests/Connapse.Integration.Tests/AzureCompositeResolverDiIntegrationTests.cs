using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AzureCompositeResolverDiIntegrationTests(SharedWebAppFixture fixture)
{
    [Fact]
    public void Di_Resolves_CompositeAsTheScopeResolver()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        ISearchScopeResolver resolver = scope.ServiceProvider.GetRequiredService<ISearchScopeResolver>();
        resolver.Should().BeOfType<CompositeSearchScopeResolver>();
        scope.ServiceProvider.GetRequiredService<AzureSearchScopeResolver>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<AwsSearchScopeResolver>().Should().NotBeNull();
    }
}
