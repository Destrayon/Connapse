using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AzureRbacReaderDiIntegrationTests(SharedWebAppFixture fixture)
{
    [Fact]
    public void Di_Resolves_AzureRbacReader()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAzureRbacReader>().Should().NotBeNull();
    }
}
