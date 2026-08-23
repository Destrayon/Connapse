using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class DeleteGuardIntegrationTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<Source> SeedSourceAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();

        var connection = await connections.CreateAsync(
            new CreateConnectionRequest(ShortName("conn"), ConnectionProvider.S3, """{"region":"us-east-1"}"""),
            createdByUserId: null);

        return await sources.CreateAsync(
            new CreateSourceRequest(ShortName("src"), connection.Id, """{"bucketName":"b"}"""));
    }

    [Fact]
    public async Task UpdateWithheldDeletionsAsync_RoundTripsTheCount()
    {
        var source = await SeedSourceAsync();

        using var scope = fixture.Factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();

        (await sources.GetAsync(source.Id))!.WithheldDeletions
            .Should().BeNull("a source with nothing pending must not claim a count of zero");

        await sources.UpdateWithheldDeletionsAsync(source.Id, 42);
        (await sources.GetAsync(source.Id))!.WithheldDeletions.Should().Be(42);

        // Clearing must return to null, not zero: the UI distinguishes "nothing pending"
        // from "a decision was made", and zero would leave the button showing forever.
        await sources.UpdateWithheldDeletionsAsync(source.Id, null);
        (await sources.GetAsync(source.Id))!.WithheldDeletions.Should().BeNull();
    }
}
