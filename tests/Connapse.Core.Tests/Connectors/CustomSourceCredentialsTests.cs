using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Custom;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>A connection's token, or its provider's, only goes to the server it was stored for.</summary>
[Trait("Category", "Unit")]
public class CustomSourceCredentialsTests
{
    private readonly ISourceStore sources = Substitute.For<ISourceStore>();
    private readonly IConnectionStore connections = Substitute.For<IConnectionStore>();
    private readonly ICustomProviderStore providers = Substitute.For<ICustomProviderStore>();

    private static Source SourceReading(string baseUrl, Guid connectionId) =>
        new(Guid.NewGuid(), "eng", null, connectionId, new CustomSourceScope(baseUrl, "eng").ToJson(), DateTime.UtcNow, DateTime.UtcNow);

    private Connection ProviderConnection(string providerUrl)
    {
        CustomProvider provider = new(Guid.NewGuid(), "p", providerUrl, true, 100, true, null, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);
        providers.GetAsync(provider.Id, Arg.Any<CancellationToken>()).Returns(provider);
        providers.GetTokenAsync(provider.Id, Arg.Any<CancellationToken>()).Returns("provider-token");
        return new Connection(Guid.NewGuid(), "c", ConnectionProvider.Custom, CustomConnectionConfig.ToJson(provider.Id), null, DateTime.UtcNow, DateTime.UtcNow);
    }

    [Fact]
    public async Task ProviderConnection_SourceReadingTheProvidersServer_GetsTheProvidersToken()
    {
        Connection connection = ProviderConnection("https://bridge.acme.test");

        string? token = await CustomSourceCredentials.TokenAsync(SourceReading("https://bridge.acme.test/", connection.Id), connection,
            sources, connections, providers, CancellationToken.None);

        token.Should().Be("provider-token");
    }

    [Fact]
    public async Task ProviderConnection_SourceReadingAnotherServer_GetsNoToken()
    {
        Connection connection = ProviderConnection("https://bridge.acme.test");

        Func<Task> act = () => CustomSourceCredentials.TokenAsync(SourceReading("https://attacker.example", connection.Id), connection,
            sources, connections, providers, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*attacker.example*");
        await providers.DidNotReceive().GetTokenAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContentOnlyConnection_SourceReadingAnotherServer_GetsNoToken()
    {
        Connection connection = new(Guid.NewGuid(), "c", ConnectionProvider.Custom,
            CustomConnectionConfig.ForMiddleMan("https://bridge.acme.test"), null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);

        Func<Task> act = () => CustomSourceCredentials.TokenAsync(SourceReading("https://attacker.example", connection.Id), connection,
            sources, connections, providers, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await connections.DidNotReceive().GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
