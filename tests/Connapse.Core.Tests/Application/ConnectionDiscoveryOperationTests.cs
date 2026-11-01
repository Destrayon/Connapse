using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Connections;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>Discovering what a connection reaches: administrators only, a missing connection is not found, and drafts only for providers with a picker.</summary>
[Trait("Category", "Unit")]
public sealed class ConnectionDiscoveryOperationTests
{
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);

    private readonly IConnectionStore _connections = Substitute.For<IConnectionStore>();
    private readonly IConnectionDiscovery _discovery = Substitute.For<IConnectionDiscovery>();
    private readonly IOperationAuthorizer _authorizer = Substitute.For<IOperationAuthorizer>();

    public ConnectionDiscoveryOperationTests() =>
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);

    private DiscoverConnection Discover() => new(_connections, _discovery, _authorizer);

    private Connection Stored(ConnectionProvider provider)
    {
        Connection connection = new(Guid.NewGuid(), "c", provider, "{}", null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);
        _connections.GetAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);
        return connection;
    }

    [Fact]
    public async Task Discover_NotAnAdministrator_IsForbiddenAndAsksNothing()
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.SourcesAdmin).Returns(false);
        Connection s3 = Stored(ConnectionProvider.S3);

        var stored = await Discover().RunAsync(Caller, new ConnectionRef(s3.Id));
        var draft = await Discover().RunDraftAsync(Caller, new DraftDiscovery(ConnectionProvider.S3));

        stored.Status.Should().Be(ConnectionStatus.Forbidden);
        draft.Status.Should().Be(ConnectionStatus.Forbidden);
        _discovery.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Discover_UnknownConnection_IsNotFound()
    {
        var found = await Discover().RunAsync(Caller, new ConnectionRef(Guid.NewGuid()));

        found.Status.Should().Be(ConnectionStatus.NotFound);
        _discovery.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Discover_StoredConnection_AsksAboutTheStoredConnection()
    {
        Connection s3 = Stored(ConnectionProvider.S3);
        var listed = new ConnectionDiscovery(DiscoveryOutcome.Listed) { Buckets = ["a"] };
        _discovery.DiscoverAsync(s3, Arg.Any<ConnectionDiscoveryRequest>(), Arg.Any<CancellationToken>())
            .Returns(Attempt<ConnectionDiscovery, ConnectionStatus>.Succeed(listed));

        var found = await Discover().RunAsync(Caller, new ConnectionRef(s3.Id));

        found.Result.Should().Be(listed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ConnectionProvider.Sftp)]
    [InlineData(ConnectionProvider.Atlassian)]
    [InlineData(ConnectionProvider.Filesystem)]
    public async Task DiscoverDraft_ProviderWithNoPicker_IsInvalid(ConnectionProvider? provider)
    {
        var found = await Discover().RunDraftAsync(Caller, new DraftDiscovery(provider));

        found.Status.Should().Be(ConnectionStatus.Invalid);
        found.Field.Should().Be("provider");
        _discovery.ReceivedCalls().Should().BeEmpty();
    }
}
