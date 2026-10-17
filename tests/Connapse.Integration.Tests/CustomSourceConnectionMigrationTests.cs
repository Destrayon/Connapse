using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Custom sources made before they needed a connection move onto generated custom connections, one per middle
/// man address and token, and lose the token they held; running it again changes nothing.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CustomSourceConnectionMigrationTests(SharedWebAppFixture fixture)
{
    private static async Task<Source> LegacySourceAsync(ISourceStore store, string baseUrl, string token) =>
        await store.CreateAsync(new CreateSourceRequest(
            $"legacy-{Guid.NewGuid():N}"[..20], null, new CustomSourceScope(baseUrl, "eng").ToJson(),
            Provider: ConnectionProvider.Custom, Secret: token));

    [Fact]
    public async Task Run_MovesEachSourceOntoAConnectionPerMiddleManAndToken_AndRunsAgainHarmlessly()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ISourceStore sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        IConnectionStore connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        string host = $"m{Guid.NewGuid():N}"[..12] + ".acme.test";
        string baseUrl = $"https://{host}/api";
        string tokenA = $"a-{Guid.NewGuid():N}";
        string tokenB = $"b-{Guid.NewGuid():N}";

        Source first = await LegacySourceAsync(sources, baseUrl, tokenA);
        Source second = await LegacySourceAsync(sources, baseUrl, tokenA);
        Source third = await LegacySourceAsync(sources, baseUrl, tokenB);
        CustomSourceConnectionMigration migration = scope.ServiceProvider.GetRequiredService<CustomSourceConnectionMigration>();

        (await migration.RunAsync()).Should().BeGreaterThanOrEqualTo(3);

        Source[] moved = [(await sources.GetAsync(first.Id))!, (await sources.GetAsync(second.Id))!, (await sources.GetAsync(third.Id))!];
        moved.Should().OnlyContain(s => s.ConnectionId != null && s.Provider == null && !s.HasSecret);
        moved[0].ConnectionId.Should().Be(moved[1].ConnectionId!.Value, "the same source server and token share a connection");
        moved[2].ConnectionId!.Value.Should().NotBe(moved[0].ConnectionId!.Value, "a different token is a different connection");

        Connection shared = (await connections.GetAsync(moved[0].ConnectionId!.Value))!;
        shared.Provider.Should().Be(ConnectionProvider.Custom);
        shared.Name.Should().StartWith(host);
        CustomConnectionConfig.BaseUrlOf(shared.ConfigJson).Should().Be(baseUrl);
        (await connections.GetSecretAsync(shared.Id)).Should().Be(tokenA);
        (await connections.GetSecretAsync(moved[2].ConnectionId!.Value)).Should().Be(tokenB);
        CustomSourceScope.TryParse(moved[0].ScopeJson).Should().Be(new CustomSourceScope(baseUrl, "eng"), "nothing about what it reads changes");

        int connectionCount = (await connections.ListAsync(take: int.MaxValue)).Count;
        (await migration.RunAsync()).Should().Be(0);
        (await connections.ListAsync(take: int.MaxValue)).Should().HaveCount(connectionCount);
    }

    [Fact]
    public async Task Run_ReusesAConnectionThatAlreadyHoldsTheSameMiddleManAndToken()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ISourceStore sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        IConnectionStore connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        string baseUrl = $"https://r{Guid.NewGuid():N}"[..20] + ".acme.test";
        string token = $"t-{Guid.NewGuid():N}";
        Connection existing = await connections.CreateAsync(new CreateConnectionRequest(
            $"pre-{Guid.NewGuid():N}"[..20], ConnectionProvider.Custom, CustomConnectionConfig.ForMiddleMan(baseUrl), token), null);
        Source source = await LegacySourceAsync(sources, baseUrl, token);

        await scope.ServiceProvider.GetRequiredService<CustomSourceConnectionMigration>().RunAsync();

        (await sources.GetAsync(source.Id))!.ConnectionId.Should().Be(existing.Id);
    }
}
