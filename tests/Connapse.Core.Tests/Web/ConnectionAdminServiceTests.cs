using System.Text.Json;
using Connapse.Application.Connections;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Web;

/// <summary>
/// Saving a connection's settings and secret from the Connections page's forms: the name checked as
/// connections.update checks it, and every change audited without recording a secret. Renaming, deleting,
/// pausing and resuming are covered by ConnectionOperationTests.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConnectionAdminServiceTests
{
    private readonly IConnectionStore _connections = Substitute.For<IConnectionStore>();
    private readonly List<(string Action, string? Id, string Details)> _events = [];
    private readonly ConnectionAdminService _admin;

    public ConnectionAdminServiceTests()
    {
        var audit = Substitute.For<IAuditLogger>();
        audit.LogAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(c => _events.Add((c.ArgAt<string>(0), c.ArgAt<string?>(2), JsonSerializer.Serialize(c.ArgAt<object?>(3)))));
        var sources = Substitute.For<ISourceStore>();
        var authorizer = Substitute.For<IOperationAuthorizer>();
        var operations = new ConnectionOperations(
            new ListConnections(_connections, sources, authorizer), new GetConnection(_connections, sources, authorizer),
            new UpdateConnection(_connections, sources, audit, authorizer), new PauseConnection(_connections, sources, audit, authorizer),
            new ResumeConnection(_connections, sources, audit, authorizer), new DeleteConnection(_connections, sources, audit, authorizer));
        _admin = new ConnectionAdminService(_connections, audit, operations, Substitute.For<AuthenticationStateProvider>());
    }

    private static Connection Site(string name = "acme.atlassian.net", int sources = 0) => new(
        Guid.NewGuid(), name, ConnectionProvider.Atlassian, "{}", null, DateTime.UtcNow, DateTime.UtcNow,
        HasSecret: true, SourceCount: sources);

    [Fact]
    public async Task SaveAsync_NameAnotherConnectionHas_IsRefusedWithoutWriting_ForAnyProvider()
    {
        Connection bucket = Site("docs-bucket") with { Provider = ConnectionProvider.S3 }, other = Site("Docs wiki");
        _connections.GetAsync(bucket.Id, Arg.Any<CancellationToken>()).Returns(bucket);
        _connections.ListAsync(0, int.MaxValue, Arg.Any<CancellationToken>()).Returns([bucket, other]);

        var result = await _admin.SaveAsync(bucket.Id, new UpdateConnectionRequest(" Docs wiki ", """{"bucketName":"b"}""", null));

        result.Outcome.Should().Be(ConnectionRenameOutcome.NameTaken);
        await _connections.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
        ConnectionAdminService.NameTakenMessage(" Docs wiki ").Should().Be("A connection named 'Docs wiki' already exists. Choose another name.");
    }

    [Fact]
    public async Task SaveAsync_NameTakenBetweenTheCheckAndTheSave_IsReportedAsTakenNotAsTheDatabaseError()
    {
        Connection site = Site(), other = Site("Docs wiki");
        _connections.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _connections.ListAsync(0, int.MaxValue, Arg.Any<CancellationToken>()).Returns([site], [site, other]);
        _connections.UpdateAsync(site.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns<Connection?>(_ => throw new Microsoft.EntityFrameworkCore.DbUpdateException("duplicate key value violates unique constraint"));

        (await _admin.SaveAsync(site.Id, new UpdateConnectionRequest("Docs wiki"))).Outcome.Should().Be(ConnectionRenameOutcome.NameTaken);
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_KeepsTheConfigAndSecretAndTrimsTheName()
    {
        Connection site = Site();
        _connections.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _connections.ListAsync(0, int.MaxValue, Arg.Any<CancellationToken>()).Returns([site]);
        _connections.UpdateAsync(site.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>()).Returns(site);

        (await _admin.SaveAsync(site.Id, new UpdateConnectionRequest(" acme.atlassian.net ", "{}", "s"))).Outcome
            .Should().Be(ConnectionRenameOutcome.Renamed);
        await _connections.Received(1).UpdateAsync(site.Id,
            Arg.Is<UpdateConnectionRequest>(r => r.Name == "acme.atlassian.net" && r.ConfigJson == "{}" && r.Secret == "s"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_RecordsThatTheSecretChangedButNeverItsValue()
    {
        Connection site = Site();
        _connections.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _connections.UpdateAsync(site.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>()).Returns(site);

        await _admin.UpdateAsync(site.Id, new UpdateConnectionRequest(site.Name, "{}", "the-secret-value"));

        _events.Should().ContainSingle().Which.Details.Should().Contain("\"SecretReplaced\":true").And.NotContain("the-secret-value");
    }
}
