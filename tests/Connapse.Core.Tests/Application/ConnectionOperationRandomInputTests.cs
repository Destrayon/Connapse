using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Connections;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using CsCheck;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// The connection operations and their Atlassian site aliases on generated inputs: who may call them,
/// which connection a request finds, the name and confirmation rules, and that only a success writes
/// or audits anything. An alias finds only an Atlassian connection.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConnectionOperationRandomInputTests
{
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);

    private static readonly Gen<string> Name = Gen.String[Gen.Char[" aAbB-"], 0, 5];

    private static readonly Gen<ConnectionProvider> AnyProvider = Gen.OneOfConst(ConnectionProvider.Atlassian, ConnectionProvider.S3);

    private static readonly Gen<Connection> AnyConnection =
        Gen.Select(Gen.String[Gen.Char["abAB"], 1, 4], AnyProvider, Gen.Int[0, 2], (name, provider, sources) => new Connection(
            Guid.NewGuid(), name, provider, "{}", null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true, SourceCount: sources));

    private sealed class World
    {
        private IConnectionStore? connections;
        private ISourceStore? sources;
        private IAuditLogger? audit;
        private IConnectionSettingsRules? rules;

        // Each sample builds a world, so a fake is only made when the sample uses it.
        public IConnectionStore Connections => connections ??= Substitute.For<IConnectionStore>();
        public ISourceStore Sources => sources ??= Substitute.For<ISourceStore>();
        public IAuditLogger Audit => audit ??= Substitute.For<IAuditLogger>();
        public PolicyAuthorizer Authorizer { get; }
        public IConnectionSettingsRules Rules => rules ??= Substitute.For<IConnectionSettingsRules>();

        public World(bool admin) => Authorizer = new PolicyAuthorizer(_ => admin);

        public int AuditCalls => audit?.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAuditLogger.LogAsync)) ?? 0;

        public bool WroteSources => sources?.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ISourceStore.UpdateAsync)) ?? false;

        public bool WroteConnections => connections?.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name is nameof(IConnectionStore.UpdateAsync) or nameof(IConnectionStore.DeleteAsync)) ?? false;

        public List<Source> Store(Connection connection, bool exists, IEnumerable<bool> enabled)
        {
            Connections.GetAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(exists ? connection : null);
            var onConnection = enabled.Select((on, i) => new Source(Guid.NewGuid(), $"src-{i}", null, connection.Id, "{}",
                DateTime.UtcNow, DateTime.UtcNow, Enabled: on)).ToList();
            Sources.ListByConnectionAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(onConnection);
            return onConnection;
        }
    }

    private static bool Found(Connection connection, bool exists, bool alias) =>
        exists && (!alias || connection.Provider == ConnectionProvider.Atlassian);

    [Fact]
    [RandomInputTestOf(typeof(ListConnections))]
    [RandomInputTestOf(typeof(ListAtlassianSites))]
    public Task List_AnyPage_ReturnsThatPageByName_AndTheAliasOnlySites() =>
        Gen.Select(Gen.Bool, AnyConnection.Array[0, 5], Gen.Int[-1, 3], Gen.OneOfConst<int?>(null, -1, 0, 1, 2, 200, 201),
                Gen.OneOfConst<ConnectionProvider?>(null, ConnectionProvider.Atlassian, ConnectionProvider.S3), Gen.Bool, Gen.Bool.Array[5])
            .SampleAsync(async t =>
            {
                var (admin, all, skip, take, provider, alias, paused) = t;
                var world = new World(admin);
                world.Connections.ListAsync(0, int.MaxValue, Arg.Any<CancellationToken>()).Returns(all);
                // A connection whose flag is set has one source, turned off.
                world.Sources.ListAsync(take: int.MaxValue, ct: Arg.Any<CancellationToken>()).Returns(all
                    .Select((c, i) => (c, i)).Where(x => paused[x.i])
                    .Select(x => new Source(Guid.NewGuid(), "s", null, x.c.Id, "{}", DateTime.UtcNow, DateTime.UtcNow, Enabled: false))
                    .ToList());
                var list = new ListConnections(world.Connections, world.Sources, world.Authorizer);

                var listed = alias
                    ? await new ListAtlassianSites(list).RunAsync(Caller)
                    : await list.RunAsync(Caller, new ListConnectionsQuery(skip, take, provider));

                var query = alias ? new ListConnectionsQuery(Provider: ConnectionProvider.Atlassian) : new ListConnectionsQuery(skip, take, provider);
                ConnectionStatus expected =
                    !admin ? ConnectionStatus.Forbidden
                    : query.Skip < 0 || query.Take is < 1 or > ListConnections.MaxTake ? ConnectionStatus.InvalidPage
                    : ConnectionStatus.Success;
                listed.Status.Should().Be(expected);
                if (!admin)
                    world.Connections.ReceivedCalls().Should().BeEmpty();
                if (expected != ConnectionStatus.Success)
                    return;

                var matching = all.Where(c => query.Provider is null || c.Provider == query.Provider)
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Skip(query.Skip).ToList();
                var page = query.Take is int size ? matching.Take(size).ToList() : matching;
                listed.Result!.Items.Select(v => v.Connection).Should().Equal(page);
                listed.Result.HasMore.Should().Be(page.Count < matching.Count);
                listed.Result.Items.Should().OnlyContain(v => v.SyncPaused == paused[Array.IndexOf(all, v.Connection)]);
            }, iter: 1000);

    [Fact]
    [RandomInputTestOf(typeof(GetConnection))]
    [RandomInputTestOf(typeof(GetAtlassianSite))]
    public Task Get_AnyConnection_FindsItUnlessTheAliasNamesAnotherProvider() =>
        Gen.Select(Gen.Bool, AnyConnection, Gen.Bool, Gen.Bool, Gen.Bool.Array[0, 3])
            .SampleAsync(async t =>
            {
                var (admin, connection, exists, alias, enabled) = t;
                var world = new World(admin);
                world.Store(connection, exists, enabled);
                var get = new GetConnection(world.Connections, world.Sources, world.Authorizer);

                var read = alias
                    ? await new GetAtlassianSite(get).RunAsync(Caller, connection.Id)
                    : await get.RunAsync(Caller, new ConnectionRef(connection.Id));

                read.Status.Should().Be(!admin ? ConnectionStatus.Forbidden
                    : !Found(connection, exists, alias) ? ConnectionStatus.NotFound
                    : ConnectionStatus.Success);
                if (read.Succeeded)
                {
                    read.Result!.Connection.Should().Be(connection);
                    read.Result.SyncPaused.Should().Be(enabled.Length > 0 && enabled.All(on => !on));
                }
            }, iter: 1000);

    [Fact]
    [RandomInputTestOf(typeof(UpdateConnection))]
    [RandomInputTestOf(typeof(RenameAtlassianSite))]
    public Task Update_AnyName_RenamesOnlyToAFreeNameOfTheRightLength() =>
        Gen.Select(Gen.Bool, AnyConnection, Gen.Bool, Gen.Bool,
                Gen.OneOf(Name.Select(n => (string?)n), Gen.Const((string?)null), Gen.String[Gen.Char["ab"], 127, 130].Select(n => (string?)n)), Gen.Bool)
            .SampleAsync(async t =>
            {
                var (admin, connection, exists, alias, name, taken) = t;
                var world = new World(admin);
                world.Store(connection, exists, []);
                string trimmed = name?.Trim() ?? "";
                var other = connection with { Id = Guid.NewGuid(), Name = taken ? trimmed : "zz-other" };
                world.Connections.ListAsync(0, int.MaxValue, Arg.Any<CancellationToken>()).Returns([connection, other]);
                world.Connections.UpdateAsync(connection.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>())
                    .Returns(connection with { Name = trimmed });
                var update = new UpdateConnection(world.Connections, world.Sources, world.Rules, world.Audit, world.Authorizer);

                var updated = alias
                    ? await new RenameAtlassianSite(update).RunAsync(Caller, connection.Id, name)
                    : await update.RunAsync(Caller, new ConnectionRef(connection.Id), name);

                bool renames = trimmed != connection.Name;
                ConnectionStatus expected =
                    !admin ? ConnectionStatus.Forbidden
                    : trimmed.Length is 0 or > ConnectionMessages.MaxNameLength ? ConnectionStatus.Invalid
                    : !Found(connection, exists, alias) ? ConnectionStatus.NotFound
                    : renames && taken ? ConnectionStatus.Conflict
                    : ConnectionStatus.Success;
                updated.Status.Should().Be(expected);
                if (expected is ConnectionStatus.Invalid or ConnectionStatus.Conflict)
                    updated.Field.Should().Be("name");

                bool wrote = expected == ConnectionStatus.Success && renames;
                world.WroteConnections.Should().Be(wrote);
                world.AuditCalls.Should().Be(wrote ? 1 : 0);
                if (wrote)
                    await world.Connections.Received(1).UpdateAsync(connection.Id,
                        Arg.Is<UpdateConnectionRequest>(r => r.Name == trimmed && r.ConfigJson == null && r.Secret == null), Arg.Any<CancellationToken>());
            }, iter: 2000);

    [Fact]
    [RandomInputTestOf(typeof(CreateConnection))]
    public Task Create_AnyRequest_StoresOnlyAFreeNameWithSettingsTheRulesAccept() =>
        Gen.Select(Gen.Bool,
                Gen.OneOf(Name.Select(n => (string?)n), Gen.Const((string?)null), Gen.String[Gen.Char["ab"], 127, 130].Select(n => (string?)n)),
                Gen.OneOf(Gen.Enum<ConnectionProvider>().Select(p => (ConnectionProvider?)p), Gen.Const((ConnectionProvider?)null),
                    Gen.Const((ConnectionProvider?)(ConnectionProvider)99)),
                Gen.Bool, Gen.Int[0, 2], Gen.Bool)
            .SampleAsync(async t =>
            {
                var (admin, name, provider, taken, rulesAnswer, verified) = t;
                var world = new World(admin);
                string trimmed = name?.Trim() ?? "";
                var other = new Connection(Guid.NewGuid(), taken ? trimmed : "zz-other", ConnectionProvider.S3, "{}", null, DateTime.UtcNow, DateTime.UtcNow);
                world.Connections.ListAsync(0, int.MaxValue, Arg.Any<CancellationToken>()).Returns([other]);
                SettingsCheck check = rulesAnswer switch
                {
                    0 => new SettingsCheck("""{"ok":true}""", "secret") { Verified = verified },
                    1 => SettingsCheck.Refuse("bad", "config"),
                    _ => SettingsCheck.Refuse("down", "installationId", SettingsRefusal.Unavailable),
                };
                world.Rules.CheckAsync(default!, default, default!, default, default).ReturnsForAnyArgs(check);
                var created = new Connection(Guid.NewGuid(), trimmed, provider ?? ConnectionProvider.S3, """{"ok":true}""", null, DateTime.UtcNow, DateTime.UtcNow);
                world.Connections.CreateAsync(Arg.Any<CreateConnectionRequest>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(created);

                var result = await new CreateConnection(world.Connections, world.Rules, world.Audit, world.Authorizer)
                    .RunAsync(Caller, new NewConnection(name, provider, new ConnectionSettings("{}")));

                bool knownProvider = provider is { } p && Enum.IsDefined(p);
                ConnectionStatus expected =
                    !admin ? ConnectionStatus.Forbidden
                    : trimmed.Length is 0 or > ConnectionMessages.MaxNameLength ? ConnectionStatus.Invalid
                    : !knownProvider || provider == ConnectionProvider.Atlassian ? ConnectionStatus.Invalid
                    : taken ? ConnectionStatus.Conflict
                    : rulesAnswer == 1 ? ConnectionStatus.Invalid
                    : rulesAnswer == 2 ? ConnectionStatus.Unavailable
                    : ConnectionStatus.Success;
                result.Status.Should().Be(expected);

                bool stored = expected == ConnectionStatus.Success;
                world.AuditCalls.Should().Be(stored ? 1 : 0);
                if (stored)
                {
                    await world.Connections.Received(1).CreateAsync(
                        Arg.Is<CreateConnectionRequest>(r => r.Name == trimmed && r.Provider == provider && r.ConfigJson == """{"ok":true}""" && r.Secret == "secret"),
                        Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
                    await world.Connections.Received(verified ? 1 : 0).RecordHealthAsync(created.Id, Arg.Any<ConnectionHealth>(), Arg.Any<CancellationToken>());
                }
                else
                {
                    await world.Connections.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default);
                }
            }, iter: 2000);

    /// <summary>Each group of these tests is its own class, so xUnit runs the groups side by side instead of one after another (CON-145).</summary>
    [Trait("Category", "Unit")]
    public class Checking
    {
        [Fact]
        [RandomInputTestOf(typeof(TestConnection))]
        public Task Test_AnyConnection_RunsOnlyForAnAdministrator_AndRecordsOnlyTheHealthTheRunnerReports() =>
            Gen.Select(Gen.Bool, AnyProvider, Gen.Bool, Gen.Bool, Gen.Int[0, 2])
                .SampleAsync(async t =>
                {
                    var (admin, provider, exists, withSettings, runnerAnswer) = t;
                    var world = new World(admin);
                    var connection = new Connection(Guid.NewGuid(), "c", provider, "{}", null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);
                    world.Store(connection, exists, []);
                    var health = new ConnectionHealth(DateTime.UtcNow, true, null, "ok", "test");
                    var runner = Substitute.For<IConnectionTestRunner>();
                    // The runner refuses, tests without touching the stored settings, or tests exactly them.
                    runner.TestAsync(default!, default).ReturnsForAnyArgs(runnerAnswer switch
                    {
                        0 => ConnectionTestRun.Refuse("no", "privateKey", ConnectionStatus.Unavailable),
                        1 => new ConnectionTestRun(ConnectionTestResult.CreateSuccess("ok")),
                        _ => new ConnectionTestRun(ConnectionTestResult.CreateSuccess("ok"), health),
                    });
                    world.Connections.RecordHealthAtAsync(connection.Id, health, connection.UpdatedAt, Arg.Any<CancellationToken>()).Returns(health);

                    var tested = await new TestConnection(world.Connections, runner, world.Authorizer).RunAsync(Caller,
                        new ConnectionRef(connection.Id), new ConnectionTestRequest(withSettings ? new ConnectionSettings("{}") : null));

                    ConnectionStatus expected =
                        !admin ? ConnectionStatus.Forbidden
                        : !exists ? ConnectionStatus.NotFound
                        : provider == ConnectionProvider.Atlassian && withSettings ? ConnectionStatus.Invalid
                        : runnerAnswer == 0 ? ConnectionStatus.Unavailable
                        : ConnectionStatus.Success;
                    tested.Status.Should().Be(expected);
                    bool ran = admin && exists && !(provider == ConnectionProvider.Atlassian && withSettings);
                    await runner.Received(ran ? 1 : 0).TestAsync(Arg.Any<ConnectionTestSubject>(), Arg.Any<CancellationToken>());
                    await world.Connections.Received(ran && runnerAnswer == 2 ? 1 : 0)
                        .RecordHealthAtAsync(connection.Id, health, connection.UpdatedAt, Arg.Any<CancellationToken>());
                    world.WroteConnections.Should().BeFalse();
                    world.AuditCalls.Should().Be(0);
                }, iter: 500);

        [Fact]
        [RandomInputTestOf(typeof(TestAtlassianSite))]
        public Task TestSite_AnyConnection_FindsOnlyAnAtlassianSite() =>
            Gen.Select(Gen.Bool, AnyProvider, Gen.Bool)
                .SampleAsync(async t =>
                {
                    var (admin, provider, exists) = t;
                    var world = new World(admin);
                    var connection = new Connection(Guid.NewGuid(), "c", provider, "{}", null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);
                    world.Store(connection, exists, []);
                    var runner = Substitute.For<IConnectionTestRunner>();
                    runner.TestAsync(default!, default).ReturnsForAnyArgs(new ConnectionTestRun(ConnectionTestResult.CreateSuccess("ok")));

                    var tested = await new TestAtlassianSite(new TestConnection(world.Connections, runner, world.Authorizer))
                        .RunAsync(Caller, connection.Id);

                    tested.Status.Should().Be(!admin ? ConnectionStatus.Forbidden
                        : !Found(connection, exists, alias: true) ? ConnectionStatus.NotFound
                        : ConnectionStatus.Success);
                    await runner.Received(tested.Succeeded ? 1 : 0).TestAsync(Arg.Any<ConnectionTestSubject>(), Arg.Any<CancellationToken>());
                }, iter: 300);

        [Fact]
        [RandomInputTestOf(typeof(ForgetConnectionHostKey))]
        public Task ForgetHostKey_AnyConnection_RemovesOnlyAPinnedSftpKey_AuditingOnlyThat() =>
            Gen.Select(Gen.Bool, Gen.OneOfConst(ConnectionProvider.Sftp, ConnectionProvider.S3, ConnectionProvider.Atlassian), Gen.Bool, Gen.Bool, Gen.Bool)
                .SampleAsync(async t =>
                {
                    var (admin, provider, exists, pinned, changed) = t;
                    var world = new World(admin);
                    string config = pinned ? """{"host":"h","hostKeyFingerprint":"SHA256:x"}""" : """{"host":"h"}""";
                    var connection = new Connection(Guid.NewGuid(), "c", provider, config, null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);
                    world.Store(connection, exists, []);
                    if (changed)
                        world.Connections.UpdateAsync(connection.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>())
                            .Returns<Connection?>(_ => throw new ConnectionChangedException(connection.Id));
                    else
                        world.Connections.UpdateAsync(connection.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>()).Returns(connection);

                    var forgotten = await new ForgetConnectionHostKey(world.Connections, world.Sources, world.Audit, world.Authorizer)
                        .RunAsync(Caller, new ConnectionRef(connection.Id));

                    ConnectionStatus expected =
                        !admin ? ConnectionStatus.Forbidden
                        : !exists ? ConnectionStatus.NotFound
                        : provider != ConnectionProvider.Sftp ? ConnectionStatus.Invalid
                        : pinned && changed ? ConnectionStatus.Conflict
                        : ConnectionStatus.Success;
                    forgotten.Status.Should().Be(expected);
                    world.AuditCalls.Should().Be(expected == ConnectionStatus.Success && pinned ? 1 : 0);
                    if (expected != ConnectionStatus.Success && expected != ConnectionStatus.Conflict || !pinned)
                        world.WroteConnections.Should().BeFalse();
                }, iter: 500);

        [Fact]
        [RandomInputTestOf(typeof(DiscoverConnection))]
        public Task Discover_AnyConnectionOrDraft_AsksOnlyForAnAdministrator_AndNeverWrites() =>
            Gen.Select(Gen.Bool, Gen.OneOfConst<ConnectionProvider?>(null, ConnectionProvider.S3, ConnectionProvider.AzureBlob,
                    ConnectionProvider.GitHub, ConnectionProvider.Custom, ConnectionProvider.Atlassian, ConnectionProvider.Sftp), Gen.Bool, Gen.Bool)
                .SampleAsync(async t =>
                {
                    var (admin, provider, exists, draft) = t;
                    var world = new World(admin);
                    var connection = new Connection(Guid.NewGuid(), "c", provider ?? ConnectionProvider.S3, "{}", null,
                        DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);
                    world.Store(connection, exists, []);
                    var discovery = Substitute.For<IConnectionDiscovery>();
                    var listed = Attempt<ConnectionDiscovery, ConnectionStatus>.Succeed(new ConnectionDiscovery(DiscoveryOutcome.Listed));
                    discovery.DiscoverAsync(default!, default!, default).ReturnsForAnyArgs(listed);
                    discovery.DiscoverDraftAsync(default!, default).ReturnsForAnyArgs(listed);
                    var discover = new DiscoverConnection(world.Connections, discovery, world.Authorizer);

                    var found = draft
                        ? await discover.RunDraftAsync(Caller, new DraftDiscovery(provider))
                        : await discover.RunAsync(Caller, new ConnectionRef(connection.Id));

                    bool hasPicker = provider is ConnectionProvider.S3 or ConnectionProvider.AzureBlob or ConnectionProvider.GitHub or ConnectionProvider.Custom;
                    ConnectionStatus expected =
                        !admin ? ConnectionStatus.Forbidden
                        : draft ? hasPicker ? ConnectionStatus.Success : ConnectionStatus.Invalid
                        : !exists ? ConnectionStatus.NotFound
                        : ConnectionStatus.Success;
                    found.Status.Should().Be(expected);
                    discovery.ReceivedCalls().Count().Should().Be(expected == ConnectionStatus.Success ? 1 : 0);
                    world.WroteConnections.Should().BeFalse();
                    world.AuditCalls.Should().Be(0);
                }, iter: 500);

        [Fact]
        [RandomInputTestOf(typeof(ResolveAtlassianSite))]
        public Task ResolveSite_AnyAddress_SendsNothingUnlessAnAdministratorGaveAnAtlassianAddress() =>
            Gen.Select(Gen.Bool, Gen.OneOfConst<string?>(null, "", " ", "not a url", "https://example.com", "http://x.atlassian.net/../"))
                .SampleAsync(async t =>
                {
                    var (admin, siteUrl) = t;
                    var world = new World(admin);
                    var http = Substitute.For<IHttpClientFactory>();

                    var resolved = await new ResolveAtlassianSite(http, world.Authorizer).RunAsync(Caller, siteUrl);

                    resolved.Status.Should().Be(admin ? ConnectionStatus.Invalid : ConnectionStatus.Forbidden);
                    http.DidNotReceiveWithAnyArgs().CreateClient(default!);
                }, iter: 100);

        [Fact]
        [RandomInputTestOf(typeof(ReplaceConnectionSecret))]
        public Task ReplaceSecret_AnySecret_StoresOnlyAnAdministratorsCheckedKeyOrToken() =>
            Gen.Select(Gen.Bool, Gen.OneOfConst(ConnectionProvider.Sftp, ConnectionProvider.Custom, ConnectionProvider.S3, ConnectionProvider.Atlassian),
                    Gen.Bool, Gen.OneOfConst<string?>(null, " ", "k"), Gen.OneOfConst<string?>(null, " ", "t"), Gen.Int[0, 2], Gen.Bool)
                .SampleAsync(async t =>
                {
                    var (admin, provider, exists, key, token, rulesAnswer, changed) = t;
                    var world = new World(admin);
                    var connection = new Connection(Guid.NewGuid(), "c", provider, "{}", null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);
                    world.Store(connection, exists, []);
                    // The rules accept with a secret, refuse, or accept but leave the secret to a custom provider.
                    world.Rules.CheckAsync(default!, default, default!, default, default).ReturnsForAnyArgs(rulesAnswer switch
                    {
                        0 => new SettingsCheck("{}", "secret"),
                        1 => SettingsCheck.Refuse("bad", "secret"),
                        _ => new SettingsCheck("{}", null),
                    });
                    if (changed)
                        world.Connections.UpdateAsync(connection.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>())
                            .Returns<Connection?>(_ => throw new ConnectionChangedException(connection.Id));
                    else
                        world.Connections.UpdateAsync(connection.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>()).Returns(connection);

                    var replaced = await new ReplaceConnectionSecret(world.Connections, world.Sources, world.Rules, world.Audit, world.Authorizer)
                        .RunAsync(Caller, new ConnectionRef(connection.Id), new ConnectionSecret(key, null, token));

                    bool given = provider == ConnectionProvider.Sftp ? !string.IsNullOrWhiteSpace(key) : !string.IsNullOrWhiteSpace(token);
                    ConnectionStatus expected =
                        !admin ? ConnectionStatus.Forbidden
                        : !exists ? ConnectionStatus.NotFound
                        : provider is not (ConnectionProvider.Sftp or ConnectionProvider.Custom) || !given ? ConnectionStatus.Invalid
                        : rulesAnswer != 0 ? ConnectionStatus.Invalid
                        : changed ? ConnectionStatus.Conflict
                        : ConnectionStatus.Success;
                    replaced.Status.Should().Be(expected);
                    world.AuditCalls.Should().Be(expected == ConnectionStatus.Success ? 1 : 0);
                    if (expected is ConnectionStatus.Forbidden or ConnectionStatus.NotFound || rulesAnswer != 0)
                        world.WroteConnections.Should().BeFalse();
                    if (expected == ConnectionStatus.Success)
                        await world.Connections.Received(1).UpdateAsync(connection.Id,
                            Arg.Is<UpdateConnectionRequest>(r => r.Name == null && r.Secret == "secret" && r.ExpectedUpdatedAt == connection.UpdatedAt),
                            Arg.Any<CancellationToken>());
                }, iter: 2000);
    }

    [Trait("Category", "Unit")]
    public class Lifecycle
    {
        [Fact]
        [RandomInputTestOf(typeof(PauseConnection))]
        [RandomInputTestOf(typeof(PauseAtlassianSite))]
        public Task Pause_AnyConnection_TurnsOffOnlyTheSourcesStillOn() =>
            Gen.Select(Gen.Bool, AnyConnection, Gen.Bool, Gen.Bool, Gen.Bool.Array[0, 3])
                .SampleAsync(async t =>
                {
                    var (admin, connection, exists, alias, enabled) = t;
                    var world = new World(admin);
                    var onConnection = world.Store(connection, exists, enabled);
                    var pause = new PauseConnection(world.Connections, world.Sources, world.Audit, world.Authorizer);

                    var paused = alias
                        ? await new PauseAtlassianSite(pause).RunAsync(Caller, connection.Id)
                        : await pause.RunAsync(Caller, new ConnectionRef(connection.Id));

                    ConnectionStatus expected =
                        !admin ? ConnectionStatus.Forbidden
                        : !Found(connection, exists, alias) ? ConnectionStatus.NotFound
                        : onConnection.Count == 0 ? ConnectionStatus.NoSources
                        : ConnectionStatus.Success;
                    paused.Status.Should().Be(expected);
                    world.AuditCalls.Should().Be(expected == ConnectionStatus.Success ? 1 : 0);
                    if (expected != ConnectionStatus.Success)
                    {
                        world.WroteSources.Should().BeFalse();
                        return;
                    }

                    paused.Result.Should().Be(new ConnectionSyncState(true, onConnection.Count));
                    foreach (var source in onConnection)
                        await world.Sources.Received(source.Enabled ? 1 : 0).UpdateAsync(source.Id,
                            Arg.Is<UpdateSourceRequest>(r => r.Enabled == false), Arg.Any<CancellationToken>());
                }, iter: 1000);

        [Fact]
        [RandomInputTestOf(typeof(ResumeConnection))]
        [RandomInputTestOf(typeof(ResumeAtlassianSite))]
        public Task Resume_AnySourceIds_TurnsOnOnlyTheChosenSourcesOfThisConnection() =>
            Gen.Select(Gen.Bool, AnyConnection, Gen.Bool, Gen.Bool, Gen.Bool.Array[0, 3], Gen.Int[0, 2], Gen.Bool.Array[3])
                .SampleAsync(async t =>
                {
                    var (admin, connection, exists, alias, enabled, mode, pick) = t;
                    var world = new World(admin);
                    var onConnection = world.Store(connection, exists, enabled);
                    // Mode 0 leaves sourceIds out, 1 picks some of the connection's sources, 2 adds one from elsewhere.
                    List<Guid>? sourceIds = mode == 0 ? null : onConnection.Where((_, i) => pick[i]).Select(s => s.Id).ToList();
                    if (mode == 2)
                        sourceIds!.Add(Guid.NewGuid());
                    var resume = new ResumeConnection(world.Connections, world.Sources, world.Audit, world.Authorizer);

                    var resumed = alias
                        ? await new ResumeAtlassianSite(resume).RunAsync(Caller, connection.Id, sourceIds)
                        : await resume.RunAsync(Caller, new ConnectionRef(connection.Id), sourceIds);

                    ConnectionStatus expected =
                        !admin ? ConnectionStatus.Forbidden
                        : !Found(connection, exists, alias) ? ConnectionStatus.NotFound
                        : onConnection.Count == 0 ? ConnectionStatus.NoSources
                        : sourceIds is not null && (sourceIds.Count == 0 || mode == 2) ? ConnectionStatus.Invalid
                        : ConnectionStatus.Success;
                    resumed.Status.Should().Be(expected);
                    if (expected == ConnectionStatus.Invalid)
                        resumed.Field.Should().Be("sourceIds");
                    world.AuditCalls.Should().Be(expected == ConnectionStatus.Success ? 1 : 0);
                    if (expected != ConnectionStatus.Success)
                    {
                        world.WroteSources.Should().BeFalse();
                        return;
                    }

                    var chosen = sourceIds is null ? onConnection : onConnection.Where(s => sourceIds.Contains(s.Id)).ToList();
                    resumed.Result!.Sources.Should().Be(chosen.Count);
                    foreach (var source in onConnection)
                        await world.Sources.Received(chosen.Contains(source) && !source.Enabled ? 1 : 0).UpdateAsync(source.Id,
                            Arg.Is<UpdateSourceRequest>(r => r.Enabled == true), Arg.Any<CancellationToken>());
                }, iter: 1000);

        [Fact]
        [RandomInputTestOf(typeof(DeleteConnection))]
        [RandomInputTestOf(typeof(DeleteAtlassianSite))]
        public Task Delete_AnyConfirmation_DeletesOnlyAnUnusedConnectionNamedExactly() =>
            Gen.Select(Gen.Bool, AnyConnection, Gen.Bool, Gen.Bool, Gen.Bool, Gen.OneOf(Name.Select(n => (string?)n), Gen.Const((string?)null)),
                    Gen.Int[0, 2])
                .SampleAsync(async t =>
                {
                    var (admin, connection, exists, alias, typeTheName, other, store) = t;
                    var world = new World(admin);
                    world.Store(connection, exists, Enumerable.Repeat(true, connection.SourceCount));
                    // The store deletes it, finds it already gone, or refuses because a source was just added.
                    if (store == 2)
                        world.Connections.DeleteAsync(connection.Id, Arg.Any<CancellationToken>()).Returns<bool>(_ => throw new InvalidOperationException("in use"));
                    else
                        world.Connections.DeleteAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(store == 0);
                    string? confirm = typeTheName ? $" {connection.Name}  " : other;
                    var delete = new DeleteConnection(world.Connections, world.Sources, world.Audit, world.Authorizer);

                    var deleted = alias
                        ? await new DeleteAtlassianSite(delete).RunAsync(Caller, connection.Id, confirm)
                        : await delete.RunAsync(Caller, new ConnectionRef(connection.Id), confirm);

                    bool confirmed = string.Equals(confirm?.Trim(), connection.Name, StringComparison.Ordinal);
                    ConnectionStatus expected =
                        !admin ? ConnectionStatus.Forbidden
                        : !Found(connection, exists, alias) ? ConnectionStatus.NotFound
                        : !confirmed ? ConnectionStatus.Invalid
                        : connection.SourceCount > 0 || store == 2 ? ConnectionStatus.InUse
                        : store == 1 ? ConnectionStatus.NotFound
                        : ConnectionStatus.Success;
                    deleted.Status.Should().Be(expected);
                    if (expected == ConnectionStatus.Invalid)
                        deleted.Field.Should().Be("confirmName");
                    if (!confirmed || connection.SourceCount > 0 || !Found(connection, exists, alias))
                        world.WroteConnections.Should().BeFalse();
                    world.AuditCalls.Should().Be(expected == ConnectionStatus.Success ? 1 : 0);
                }, iter: 2000);
    }
}
