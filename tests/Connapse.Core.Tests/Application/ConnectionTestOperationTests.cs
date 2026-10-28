using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Connections;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connections;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// Testing a connection and forgetting an SFTP host key: administrators only, the health recorded only when the
/// runner says the test was of the stored settings, and a forgotten key audited once.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConnectionTestOperationTests
{
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);

    private readonly IConnectionStore _connections = Substitute.For<IConnectionStore>();
    private readonly ISourceStore _sources = Substitute.For<ISourceStore>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly IOperationAuthorizer _authorizer = Substitute.For<IOperationAuthorizer>();
    private readonly IConnectionTestRunner _runner = Substitute.For<IConnectionTestRunner>();

    public ConnectionTestOperationTests() =>
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);

    private Connection Stored(ConnectionProvider provider, string configJson = """{"host":"files.example"}""")
    {
        Connection connection = new(Guid.NewGuid(), "files", provider, configJson, null,
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(-1), HasSecret: true);
        _connections.GetAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);
        return connection;
    }

    private TestConnection Test() => new(_connections, _runner, _authorizer);

    private ForgetConnectionHostKey Forget() => new(_connections, _sources, _audit, _authorizer);

    [Fact]
    public async Task Test_NotAnAdministrator_IsForbiddenAndRunsNothing()
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.SourcesAdmin).Returns(false);
        Connection sftp = Stored(ConnectionProvider.Sftp);

        var tested = await Test().RunAsync(Caller, new ConnectionRef(sftp.Id));
        var draft = await Test().RunDraftAsync(Caller, new DraftConnectionTest("x", ConnectionProvider.S3, new ConnectionSettings("{}")));

        tested.Status.Should().Be(ConnectionStatus.Forbidden);
        draft.Status.Should().Be(ConnectionStatus.Forbidden);
        await _runner.DidNotReceiveWithAnyArgs().TestAsync(default!, default);
    }

    [Fact]
    public async Task Test_StoredConnection_PassesTheStoredConnectionAndTheSettingsGiven()
    {
        Connection sftp = Stored(ConnectionProvider.Sftp);
        var settings = new ConnectionSettings("""{"host":"files.example"}""", "KEY");
        _runner.TestAsync(default!, default).ReturnsForAnyArgs(new ConnectionTestRun(ConnectionTestResult.CreateSuccess("ok")));

        var tested = await Test().RunAsync(Caller, new ConnectionRef(sftp.Id), new ConnectionTestRequest(settings, "bucket"));

        tested.Succeeded.Should().BeTrue();
        tested.Result!.Health.Should().BeNull();
        await _runner.Received(1).TestAsync(new ConnectionTestSubject("files", ConnectionProvider.Sftp, settings, sftp, "bucket"),
            Arg.Any<CancellationToken>());
        await _connections.DidNotReceiveWithAnyArgs().RecordHealthAtAsync(default, default!, default, default);
    }

    [Fact]
    public async Task Test_RunnerReportsHealth_RecordsItOnTheConnection()
    {
        Connection custom = Stored(ConnectionProvider.Custom);
        var health = new ConnectionHealth(DateTime.UtcNow, true, null, "Reachable, token accepted.", "test");
        _runner.TestAsync(default!, default).ReturnsForAnyArgs(new ConnectionTestRun(ConnectionTestResult.CreateSuccess("ok"), health));
        _connections.RecordHealthAtAsync(custom.Id, health, custom.UpdatedAt, Arg.Any<CancellationToken>()).Returns(health);

        var tested = await Test().RunAsync(Caller, new ConnectionRef(custom.Id));

        tested.Result!.Health.Should().Be(health);
    }

    [Fact]
    public async Task Test_ConnectionSavedDuringTheTest_LeavesItsHealthAlone()
    {
        Connection custom = Stored(ConnectionProvider.Custom);
        var health = new ConnectionHealth(DateTime.UtcNow, false, "token", "Rejected.", "test");
        _runner.TestAsync(default!, default).ReturnsForAnyArgs(new ConnectionTestRun(ConnectionTestResult.CreateFailure("no"), health));
        // The store writes nothing once the connection is past the version tested.
        _connections.RecordHealthAtAsync(custom.Id, health, custom.UpdatedAt, Arg.Any<CancellationToken>()).Returns((ConnectionHealth?)null);

        var tested = await Test().RunAsync(Caller, new ConnectionRef(custom.Id));

        tested.Succeeded.Should().BeTrue();
        tested.Result!.Health.Should().BeNull();
        await _connections.DidNotReceiveWithAnyArgs().RecordHealthAsync(default, default!, default);
    }

    [Fact]
    public async Task Test_RunnerRefuses_FailsWithItsStatusAndField()
    {
        Connection sftp = Stored(ConnectionProvider.Sftp);
        _runner.TestAsync(default!, default).ReturnsForAnyArgs(ConnectionTestRun.Refuse("Paste the key.", "privateKey"));

        var tested = await Test().RunAsync(Caller, new ConnectionRef(sftp.Id));

        tested.Status.Should().Be(ConnectionStatus.Invalid);
        tested.Detail.Should().Be("Paste the key.");
        tested.Field.Should().Be("privateKey");
    }

    [Fact]
    public async Task Test_AtlassianSiteWithSettings_IsRefusedWithoutTesting()
    {
        Connection site = Stored(ConnectionProvider.Atlassian);

        var tested = await Test().RunAsync(Caller, new ConnectionRef(site.Id), new ConnectionTestRequest(new ConnectionSettings("{}")));

        tested.Status.Should().Be(ConnectionStatus.Invalid);
        tested.Field.Should().Be("settings");
        await _runner.DidNotReceiveWithAnyArgs().TestAsync(default!, default);
    }

    [Fact]
    public async Task TestAtlassianSite_AnotherProvidersConnection_IsNotFound()
    {
        Connection sftp = Stored(ConnectionProvider.Sftp);

        var tested = await new TestAtlassianSite(Test()).RunAsync(Caller, sftp.Id);

        tested.Status.Should().Be(ConnectionStatus.NotFound);
        await _runner.DidNotReceiveWithAnyArgs().TestAsync(default!, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ConnectionProvider.Atlassian)]
    public async Task TestDraft_NoProviderOrAnAtlassianSite_IsInvalid(ConnectionProvider? provider)
    {
        var tested = await Test().RunDraftAsync(Caller, new DraftConnectionTest("x", provider, new ConnectionSettings("{}")));

        tested.Status.Should().Be(ConnectionStatus.Invalid);
        tested.Field.Should().Be("provider");
    }

    [Fact]
    public async Task ForgetHostKey_PinnedKey_RemovesOnlyTheFingerprintAndAuditsOnce()
    {
        Connection sftp = Stored(ConnectionProvider.Sftp,
            $$"""{"host":"files.example","{{ConnectionSshHostKeyStore.FingerprintProperty}}":"SHA256:abc"}""");
        _connections.UpdateAsync(sftp.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>()).Returns(sftp);

        var forgotten = await Forget().RunAsync(Caller, new ConnectionRef(sftp.Id));

        forgotten.Succeeded.Should().BeTrue();
        await _connections.Received(1).UpdateAsync(sftp.Id,
            Arg.Is<UpdateConnectionRequest>(r => r.ConfigJson == """{"host":"files.example"}""" && r.ExpectedUpdatedAt == sftp.UpdatedAt
                && r.Secret == null && r.Name == null),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("connection.host_key_forgotten", "connection", sftp.Id.ToString(), Arg.Any<object?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForgetHostKey_NothingPinned_ChangesNothing()
    {
        Connection sftp = Stored(ConnectionProvider.Sftp);

        var forgotten = await Forget().RunAsync(Caller, new ConnectionRef(sftp.Id));

        forgotten.Succeeded.Should().BeTrue();
        await _connections.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task ForgetHostKey_ConnectionChangedMeanwhile_IsAConflict()
    {
        Connection sftp = Stored(ConnectionProvider.Sftp,
            $$"""{"host":"files.example","{{ConnectionSshHostKeyStore.FingerprintProperty}}":"SHA256:abc"}""");
        _connections.UpdateAsync(sftp.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns<Connection?>(_ => throw new ConnectionChangedException(sftp.Id));

        var forgotten = await Forget().RunAsync(Caller, new ConnectionRef(sftp.Id));

        forgotten.Status.Should().Be(ConnectionStatus.Conflict);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default, default, default, default);
    }

    [Theory]
    [InlineData(ConnectionProvider.S3)]
    [InlineData(ConnectionProvider.Custom)]
    public async Task ForgetHostKey_NotSftp_IsInvalid(ConnectionProvider provider)
    {
        Connection connection = Stored(provider);

        var forgotten = await Forget().RunAsync(Caller, new ConnectionRef(connection.Id));

        forgotten.Status.Should().Be(ConnectionStatus.Invalid);
    }

    [Fact]
    public async Task ForgetHostKey_NotAnAdministrator_IsForbidden()
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.SourcesAdmin).Returns(false);
        Connection sftp = Stored(ConnectionProvider.Sftp);

        var forgotten = await Forget().RunAsync(Caller, new ConnectionRef(sftp.Id));

        forgotten.Status.Should().Be(ConnectionStatus.Forbidden);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("not a url")]
    public async Task ResolveSite_NotAnAtlassianAddress_IsInvalidWithoutAnyRequest(string siteUrl)
    {
        var http = Substitute.For<IHttpClientFactory>();

        var resolved = await new ResolveAtlassianSite(http, _authorizer).RunAsync(Caller, siteUrl);

        resolved.Status.Should().Be(ConnectionStatus.Invalid);
        resolved.Field.Should().Be("siteUrl");
        http.DidNotReceiveWithAnyArgs().CreateClient(default!);
    }
}
