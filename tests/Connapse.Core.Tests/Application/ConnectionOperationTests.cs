using System.Security.Claims;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Connections;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// Listing, reading, renaming, pausing, resuming and deleting a connection, the rules the Connections
/// page, REST and the Atlassian site aliases share: administrators only, every change audited, a
/// delete must name the connection and is refused while sources use it, and pausing disables
/// sources without deleting anything.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConnectionOperationTests
{
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);

    private readonly IConnectionStore _connections = Substitute.For<IConnectionStore>();
    private readonly ISourceStore _sources = Substitute.For<ISourceStore>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly IOperationAuthorizer _authorizer = Substitute.For<IOperationAuthorizer>();
    private readonly List<(string Action, string? Id, string Details)> _events = [];

    public ConnectionOperationTests()
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        _audit.LogAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(c => _events.Add((c.ArgAt<string>(0), c.ArgAt<string?>(2), JsonSerializer.Serialize(c.ArgAt<object?>(3)))));
    }

    private static Connection Site(string name = "acme.atlassian.net", int sources = 0) => new(
        Guid.NewGuid(), name, ConnectionProvider.Atlassian, "{}", null, DateTime.UtcNow, DateTime.UtcNow,
        HasSecret: true, SourceCount: sources);

    private static Connection Bucket(string name = "docs-bucket", int sources = 0) =>
        Site(name, sources) with { Provider = ConnectionProvider.S3 };

    private static Source SourceOn(Guid connectionId, string name, bool enabled = true) =>
        new(Guid.NewGuid(), name, null, connectionId, "{}", DateTime.UtcNow, DateTime.UtcNow, Enabled: enabled);

    private void Stored(params Connection[] all)
    {
        foreach (var connection in all)
            _connections.GetAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);
        _connections.ListAsync(0, int.MaxValue, Arg.Any<CancellationToken>()).Returns(all);
    }

    private void DenyAdmin() => _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Admin).Returns(false);

    private ListConnections List() => new(_connections, _sources, _authorizer);

    private GetConnection Get() => new(_connections, _sources, _authorizer);

    private UpdateConnection Update() => new(_connections, _sources, _audit, _authorizer);

    private PauseConnection Pause() => new(_connections, _sources, _audit, _authorizer);

    private ResumeConnection Resume() => new(_connections, _sources, _audit, _authorizer);

    private DeleteConnection Delete() => new(_connections, _sources, _audit, _authorizer);

    [Fact]
    public async Task EveryOperation_NotAnAdministrator_IsForbiddenAndTouchesNothing()
    {
        DenyAdmin();
        Connection site = Site(sources: 1);
        var key = new ConnectionRef(site.Id);

        (await List().RunAsync(Caller, new ListConnectionsQuery())).Status.Should().Be(ConnectionStatus.Forbidden);
        (await Get().RunAsync(Caller, key)).Status.Should().Be(ConnectionStatus.Forbidden);
        (await Update().RunAsync(Caller, key, "new")).Status.Should().Be(ConnectionStatus.Forbidden);
        (await Pause().RunAsync(Caller, key)).Status.Should().Be(ConnectionStatus.Forbidden);
        (await Resume().RunAsync(Caller, key)).Status.Should().Be(ConnectionStatus.Forbidden);
        (await Delete().RunAsync(Caller, key, site.Name)).Status.Should().Be(ConnectionStatus.Forbidden);

        _connections.ReceivedCalls().Should().BeEmpty();
        _sources.ReceivedCalls().Should().BeEmpty();
        _events.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1, 10, "skip must be >= 0")]
    [InlineData(0, 0, "take must be >= 1")]
    [InlineData(0, 201, "take must be <= 200")]
    public async Task List_PageOutOfRange_IsInvalidPage(int skip, int take, string detail)
    {
        var listed = await List().RunAsync(Caller, new ListConnectionsQuery(skip, take));

        listed.Status.Should().Be(ConnectionStatus.InvalidPage);
        listed.Detail.Should().Be(detail);
    }

    [Fact]
    public async Task List_ByProvider_PagesTheProvidersConnectionsByNameAndMarksThePausedOnes()
    {
        Connection a = Site("a.atlassian.net", 1), b = Site("B.atlassian.net"), c = Site("c.atlassian.net"), bucket = Bucket("0-bucket");
        Stored(c, bucket, b, a);
        _sources.ListAsync(0, int.MaxValue, Arg.Any<CancellationToken>()).Returns([SourceOn(a.Id, "off", enabled: false)]);

        var page = (await List().RunAsync(Caller, new ListConnectionsQuery(0, 2, ConnectionProvider.Atlassian))).Result!;

        page.Items.Select(v => v.Connection.Name).Should().Equal("a.atlassian.net", "B.atlassian.net");
        page.Items.Select(v => v.SyncPaused).Should().Equal(true, false);
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task Get_AtlassianSiteRef_AnotherProvidersConnection_ReadsAsNoSuchSite()
    {
        Connection bucket = Bucket();
        Stored(bucket);

        var read = await Get().RunAsync(Caller, ConnectionRef.AtlassianSite(bucket.Id));

        read.Status.Should().Be(ConnectionStatus.NotFound);
        read.Detail.Should().Be("No such Atlassian site.");
        (await Get().RunAsync(Caller, new ConnectionRef(bucket.Id))).Result!.Connection.Should().Be(bucket);
    }

    [Fact]
    public async Task Get_PausedOnlyWhenThereAreSourcesAndAllAreOff()
    {
        Connection site = Site();
        Stored(site);
        _sources.ListByConnectionAsync(site.Id, Arg.Any<CancellationToken>()).Returns(
            [], [SourceOn(site.Id, "a", enabled: false), SourceOn(site.Id, "b")], [SourceOn(site.Id, "a", enabled: false)]);

        (await Get().RunAsync(Caller, new ConnectionRef(site.Id))).Result!.SyncPaused.Should().BeFalse("no sources is not paused");
        (await Get().RunAsync(Caller, new ConnectionRef(site.Id))).Result!.SyncPaused.Should().BeFalse("one is still on");
        (await Get().RunAsync(Caller, new ConnectionRef(site.Id))).Result!.SyncPaused.Should().BeTrue();
    }

    [Fact]
    public async Task Update_RenamesAndAuditsTheOldAndNewName()
    {
        Connection site = Site();
        Stored(site);
        _connections.UpdateAsync(site.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(site with { Name = "Docs wiki" });

        var renamed = await Update().RunAsync(Caller, new ConnectionRef(site.Id), "  Docs wiki ");

        renamed.Result!.Connection.Name.Should().Be("Docs wiki");
        await _connections.Received(1).UpdateAsync(site.Id,
            Arg.Is<UpdateConnectionRequest>(r => r.Name == "Docs wiki" && r.ConfigJson == null && r.Secret == null),
            Arg.Any<CancellationToken>());
        _events.Should().ContainSingle(e => e.Action == "connection.updated" && e.Id == site.Id.ToString())
            .Which.Details.Should().Contain("\"OldName\":\"acme.atlassian.net\"").And.Contain("\"NewName\":\"Docs wiki\"")
            .And.Contain("\"SecretReplaced\":false");
    }

    [Fact]
    public async Task Update_SameName_WritesAndAuditsNothing()
    {
        Connection site = Site();
        Stored(site);

        (await Update().RunAsync(Caller, new ConnectionRef(site.Id), " acme.atlassian.net ")).Succeeded.Should().BeTrue();

        await _connections.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
        _events.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Update_BlankOrLongName_IsInvalidOnTheNameFieldWithoutWriting(string? name)
    {
        foreach (string? attempt in new[] { name, new string('n', 129) })
        {
            var renamed = await Update().RunAsync(Caller, new ConnectionRef(Guid.NewGuid()), attempt);

            renamed.Status.Should().Be(ConnectionStatus.Invalid);
            renamed.Field.Should().Be("name");
            renamed.Detail.Should().Be("Enter a name of 1 to 128 characters.");
        }
        await _connections.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_NameAnotherConnectionHas_IsAConflictForAnyProvider()
    {
        Connection bucket = Bucket(), other = Site("Docs wiki");
        Stored(bucket, other);

        var renamed = await Update().RunAsync(Caller, new ConnectionRef(bucket.Id), " Docs wiki ");

        renamed.Status.Should().Be(ConnectionStatus.Conflict);
        renamed.Field.Should().Be("name");
        renamed.Detail.Should().Be("A connection named 'Docs wiki' already exists. Choose another name.");
        await _connections.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
    }

    [Fact]
    public async Task Update_NameTakenBetweenTheCheckAndTheSave_IsAConflictNotTheDatabaseError()
    {
        Connection site = Site(), other = Site("Docs wiki");
        _connections.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _connections.ListAsync(0, int.MaxValue, Arg.Any<CancellationToken>()).Returns([site], [site, other]);
        _connections.UpdateAsync(site.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns<Connection?>(_ => throw new Microsoft.EntityFrameworkCore.DbUpdateException("duplicate key value violates unique constraint"));

        (await Update().RunAsync(Caller, new ConnectionRef(site.Id), "Docs wiki")).Status.Should().Be(ConnectionStatus.Conflict);
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task Pause_NoSources_IsNoSourcesInTheRoutesOwnWords()
    {
        Connection site = Site();
        Stored(site);

        (await Pause().RunAsync(Caller, ConnectionRef.AtlassianSite(site.Id))).Detail
            .Should().Be("This site has no sources, so there is no syncing to pause.");
        var paused = await Pause().RunAsync(Caller, new ConnectionRef(site.Id));
        paused.Status.Should().Be(ConnectionStatus.NoSources);
        paused.Detail.Should().Be("This connection has no sources, so there is no syncing to pause.");
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task PauseThenResume_DisablesEverySourceAndResumesOnlyTheChosenOnes()
    {
        Connection site = Site(sources: 2);
        Stored(site);
        Source a = SourceOn(site.Id, "a"), b = SourceOn(site.Id, "b");
        _sources.ListByConnectionAsync(site.Id, Arg.Any<CancellationToken>()).Returns([a, b]);

        (await Pause().RunAsync(Caller, new ConnectionRef(site.Id))).Result.Should().Be(new ConnectionSyncState(true, 2));
        await _sources.Received(1).UpdateAsync(a.Id, Arg.Is<UpdateSourceRequest>(r => r.Enabled == false), Arg.Any<CancellationToken>());
        await _sources.Received(1).UpdateAsync(b.Id, Arg.Is<UpdateSourceRequest>(r => r.Enabled == false), Arg.Any<CancellationToken>());

        _sources.ListByConnectionAsync(site.Id, Arg.Any<CancellationToken>())
            .Returns([a with { Enabled = false }, b with { Enabled = false }]);
        (await Resume().RunAsync(Caller, new ConnectionRef(site.Id), [a.Id])).Result!.Sources.Should().Be(1);
        await _sources.Received(1).UpdateAsync(a.Id, Arg.Is<UpdateSourceRequest>(r => r.Enabled == true), Arg.Any<CancellationToken>());
        await _sources.DidNotReceive().UpdateAsync(b.Id, Arg.Is<UpdateSourceRequest>(r => r.Enabled == true), Arg.Any<CancellationToken>());

        _events.Select(e => e.Action).Should().Equal("connection.paused", "connection.resumed");
    }

    [Fact]
    public async Task Resume_SourceIdsEmptyOrOffTheConnection_IsInvalidAndTurnsNothingOn()
    {
        Connection site = Site(sources: 1);
        Stored(site);
        _sources.ListByConnectionAsync(site.Id, Arg.Any<CancellationToken>()).Returns([SourceOn(site.Id, "a", enabled: false)]);

        foreach (Guid[] ids in new[] { Array.Empty<Guid>(), [Guid.NewGuid()] })
        {
            var resumed = await Resume().RunAsync(Caller, ConnectionRef.AtlassianSite(site.Id), ids);

            resumed.Status.Should().Be(ConnectionStatus.Invalid);
            resumed.Field.Should().Be("sourceIds");
            resumed.Detail.Should().Be("sourceIds must name at least one source, each of them on this site.");
        }
        await _sources.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_WrongConfirmation_IsInvalidAndDeletesNothing()
    {
        Connection site = Site();
        Stored(site);

        foreach (string? confirm in new[] { "acme", null })
        {
            var deleted = await Delete().RunAsync(Caller, ConnectionRef.AtlassianSite(site.Id), confirm);

            deleted.Status.Should().Be(ConnectionStatus.Invalid);
            deleted.Field.Should().Be("confirmName");
            deleted.Detail.Should().Be("confirmName must be the site's name, 'acme.atlassian.net'.");
        }
        await _connections.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
    }

    [Fact]
    public async Task Delete_WithSources_IsInUseAndNamesThem()
    {
        Connection site = Site(sources: 1);
        Stored(site);
        _sources.ListByConnectionAsync(site.Id, Arg.Any<CancellationToken>()).Returns([SourceOn(site.Id, "acme / Engineering")]);

        var deleted = await Delete().RunAsync(Caller, new ConnectionRef(site.Id), site.Name);

        deleted.Status.Should().Be(ConnectionStatus.InUse);
        deleted.Detail.Should().Be("'acme.atlassian.net' can't be deleted while 1 source uses it: acme / Engineering. Delete that source first.");
        await _connections.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
    }

    [Fact]
    public async Task Delete_SourceAddedSinceTheCount_IsRefusedByTheStoreAndReportedAsInUse()
    {
        Connection site = Site();
        Stored(site);
        _connections.DeleteAsync(site.Id, Arg.Any<CancellationToken>()).Returns<bool>(_ => throw new InvalidOperationException("in use"));
        _sources.ListByConnectionAsync(site.Id, Arg.Any<CancellationToken>()).Returns([SourceOn(site.Id, "late")]);

        (await Delete().RunAsync(Caller, new ConnectionRef(site.Id), site.Name)).Status.Should().Be(ConnectionStatus.InUse);
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_Confirmed_DeletesAndAudits()
    {
        Connection site = Site();
        Stored(site);
        _connections.DeleteAsync(site.Id, Arg.Any<CancellationToken>()).Returns(true);

        (await Delete().RunAsync(Caller, new ConnectionRef(site.Id), " acme.atlassian.net ")).Succeeded.Should().BeTrue();
        _events.Should().ContainSingle(e => e.Action == "connection.deleted" && e.Id == site.Id.ToString());
    }

    [Fact]
    public async Task AtlassianSiteAliases_RunTheConnectionOperations_WithTheSiteProviderRequired()
    {
        Connection bucket = Bucket(sources: 1);
        Stored(bucket);
        _sources.ListByConnectionAsync(bucket.Id, Arg.Any<CancellationToken>()).Returns([SourceOn(bucket.Id, "a")]);
        _sources.ListAsync(0, int.MaxValue, Arg.Any<CancellationToken>()).Returns([]);

        (await new ListAtlassianSites(List()).RunAsync(Caller)).Result!.Items.Should().BeEmpty();
        (await new GetAtlassianSite(Get()).RunAsync(Caller, bucket.Id)).Status.Should().Be(ConnectionStatus.NotFound);
        (await new RenameAtlassianSite(Update()).RunAsync(Caller, bucket.Id, "x")).Status.Should().Be(ConnectionStatus.NotFound);
        (await new PauseAtlassianSite(Pause()).RunAsync(Caller, bucket.Id)).Status.Should().Be(ConnectionStatus.NotFound);
        (await new ResumeAtlassianSite(Resume()).RunAsync(Caller, bucket.Id)).Status.Should().Be(ConnectionStatus.NotFound);
        (await new DeleteAtlassianSite(Delete()).RunAsync(Caller, bucket.Id, bucket.Name)).Status.Should().Be(ConnectionStatus.NotFound);

        await _connections.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
        await _connections.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
        await _sources.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
    }
}
