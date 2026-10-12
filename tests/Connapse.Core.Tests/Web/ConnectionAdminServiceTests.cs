using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Services;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Web;

/// <summary>
/// Renaming, deleting, pausing and resuming a connection, the rules the Connections page and the
/// REST API share: every change is audited, a delete must name the connection and is refused while
/// sources use it, and pausing disables sources without deleting anything.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConnectionAdminServiceTests
{
    private readonly IConnectionStore _connections = Substitute.For<IConnectionStore>();
    private readonly ISourceStore _sources = Substitute.For<ISourceStore>();
    private readonly List<(string Action, string? Id, string Details)> _events = [];
    private readonly ConnectionAdminService _admin;

    public ConnectionAdminServiceTests()
    {
        var audit = Substitute.For<IAuditLogger>();
        audit.LogAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(c => _events.Add((c.ArgAt<string>(0), c.ArgAt<string?>(2), JsonSerializer.Serialize(c.ArgAt<object?>(3)))));
        _admin = new ConnectionAdminService(_connections, _sources, audit);
    }

    private static Connection Site(string name = "acme.atlassian.net", int sources = 0) => new(
        Guid.NewGuid(), name, ConnectionProvider.Atlassian, "{}", null, DateTime.UtcNow, DateTime.UtcNow,
        HasSecret: true, SourceCount: sources);

    private static Source SourceOn(Guid connectionId, string name, bool enabled = true) =>
        new(Guid.NewGuid(), name, null, connectionId, "{}", DateTime.UtcNow, DateTime.UtcNow, Enabled: enabled);

    [Fact]
    public async Task RenameAsync_RenamesAndAuditsTheOldAndNewName()
    {
        Connection site = Site();
        _connections.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _connections.ListAsync(0, int.MaxValue, Arg.Any<CancellationToken>()).Returns([site]);
        _connections.UpdateAsync(site.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(site with { Name = "Docs wiki" });

        var result = await _admin.RenameAsync(site.Id, "  Docs wiki ");

        result.Outcome.Should().Be(ConnectionRenameOutcome.Renamed);
        await _connections.Received(1).UpdateAsync(site.Id,
            Arg.Is<UpdateConnectionRequest>(r => r.Name == "Docs wiki" && r.ConfigJson == null && r.Secret == null),
            Arg.Any<CancellationToken>());
        _events.Should().ContainSingle(e => e.Action == "connection.updated" && e.Id == site.Id.ToString())
            .Which.Details.Should().Contain("\"OldName\":\"acme.atlassian.net\"").And.Contain("\"NewName\":\"Docs wiki\"")
            .And.Contain("\"SecretReplaced\":false");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task RenameAsync_BlankName_IsRefusedWithoutWriting(string? name)
    {
        (await _admin.RenameAsync(Guid.NewGuid(), name)).Outcome.Should().Be(ConnectionRenameOutcome.InvalidName);
        await _connections.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task RenameAsync_NameAnotherConnectionHas_IsRefused()
    {
        Connection site = Site(), other = Site("Docs wiki");
        _connections.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _connections.ListAsync(0, int.MaxValue, Arg.Any<CancellationToken>()).Returns([site, other]);

        (await _admin.RenameAsync(site.Id, "Docs wiki")).Outcome.Should().Be(ConnectionRenameOutcome.NameTaken);
        await _connections.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
    }

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

    [Fact]
    public async Task DeleteAsync_WrongConfirmation_DeletesNothing()
    {
        Connection site = Site();
        _connections.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);

        (await _admin.DeleteAsync(site.Id, "acme")).Outcome.Should().Be(ConnectionDeleteOutcome.ConfirmMismatch);
        (await _admin.DeleteAsync(site.Id, null)).Outcome.Should().Be(ConnectionDeleteOutcome.ConfirmMismatch);
        await _connections.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
    }

    [Fact]
    public async Task DeleteAsync_WithSources_IsRefusedAndNamesThem()
    {
        Connection site = Site(sources: 1);
        _connections.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _sources.ListByConnectionAsync(site.Id, Arg.Any<CancellationToken>()).Returns([SourceOn(site.Id, "acme / Engineering")]);

        var result = await _admin.DeleteAsync(site.Id, site.Name);

        result.Outcome.Should().Be(ConnectionDeleteOutcome.HasSources);
        result.Sources!.Select(s => s.Name).Should().Equal("acme / Engineering");
        await _connections.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
    }

    [Fact]
    public async Task DeleteAsync_SourceAddedSinceTheCount_IsRefusedByTheStoreAndReported()
    {
        Connection site = Site();
        _connections.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _connections.DeleteAsync(site.Id, Arg.Any<CancellationToken>()).Returns<bool>(_ => throw new InvalidOperationException("in use"));
        _sources.ListByConnectionAsync(site.Id, Arg.Any<CancellationToken>()).Returns([SourceOn(site.Id, "late")]);

        (await _admin.DeleteAsync(site.Id, site.Name)).Outcome.Should().Be(ConnectionDeleteOutcome.HasSources);
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_Confirmed_DeletesAndAudits()
    {
        Connection site = Site();
        _connections.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _connections.DeleteAsync(site.Id, Arg.Any<CancellationToken>()).Returns(true);

        (await _admin.DeleteAsync(site.Id, " acme.atlassian.net ")).Outcome.Should().Be(ConnectionDeleteOutcome.Deleted);
        _events.Should().ContainSingle(e => e.Action == "connection.deleted" && e.Id == site.Id.ToString());
    }

    [Fact]
    public async Task SetSyncingAsync_PausesEverySourceAndResumesOnlyTheChosenOnes()
    {
        Connection site = Site(sources: 2);
        Source a = SourceOn(site.Id, "a"), b = SourceOn(site.Id, "b");
        _sources.ListByConnectionAsync(site.Id, Arg.Any<CancellationToken>()).Returns([a, b]);

        (await _admin.SetSyncingAsync(site, enabled: false)).Should().Be(2);
        await _sources.Received(1).UpdateAsync(a.Id, Arg.Is<UpdateSourceRequest>(r => r.Enabled == false), Arg.Any<CancellationToken>());
        await _sources.Received(1).UpdateAsync(b.Id, Arg.Is<UpdateSourceRequest>(r => r.Enabled == false), Arg.Any<CancellationToken>());

        _sources.ListByConnectionAsync(site.Id, Arg.Any<CancellationToken>())
            .Returns([a with { Enabled = false }, b with { Enabled = false }]);
        (await _admin.SetSyncingAsync(site, enabled: true, new HashSet<Guid> { a.Id })).Should().Be(1);
        await _sources.Received(1).UpdateAsync(a.Id, Arg.Is<UpdateSourceRequest>(r => r.Enabled == true), Arg.Any<CancellationToken>());
        await _sources.DidNotReceive().UpdateAsync(b.Id, Arg.Is<UpdateSourceRequest>(r => r.Enabled == true), Arg.Any<CancellationToken>());

        _events.Select(e => e.Action).Should().Equal("connection.paused", "connection.resumed");
    }

    [Fact]
    public async Task IsPausedAsync_OnlyWhenThereAreSourcesAndAllAreOff()
    {
        Guid id = Guid.NewGuid();
        _sources.ListByConnectionAsync(id, Arg.Any<CancellationToken>()).Returns(
            [], [SourceOn(id, "a", enabled: false), SourceOn(id, "b")], [SourceOn(id, "a", enabled: false)]);

        (await _admin.IsPausedAsync(id)).Should().BeFalse("no sources is not paused");
        (await _admin.IsPausedAsync(id)).Should().BeFalse("one is still on");
        (await _admin.IsPausedAsync(id)).Should().BeTrue();
    }
}
