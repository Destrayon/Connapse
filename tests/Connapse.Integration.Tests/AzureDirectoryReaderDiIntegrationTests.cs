using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// Proves the Phase 4a Graph identity-reader DI graph resolves from a real host — a clean container
/// start does not catch a missing registration, only constructing the service does. Guards the
/// TokenCredential mapping + typed HttpClient landing together.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AzureDirectoryReaderDiIntegrationTests(SharedWebAppFixture fixture)
{
    [Fact]
    public void Di_Resolves_AzureDirectoryReader()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAzureDirectoryReader>().Should().NotBeNull();
    }
}
