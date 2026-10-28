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
/// Replacing a connection's secret: the Owner only, checked against the stored settings as the Connections page
/// checks a save, audited without the secret, and refused when the connection changed while it was checked.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConnectionSecretOperationTests
{
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);

    private readonly IConnectionStore _connections = Substitute.For<IConnectionStore>();
    private readonly ISourceStore _sources = Substitute.For<ISourceStore>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly IOperationAuthorizer _authorizer = Substitute.For<IOperationAuthorizer>();
    private readonly IConnectionSettingsRules _rules = Substitute.For<IConnectionSettingsRules>();
    private readonly List<(string Action, string Details)> _events = [];

    public ConnectionSecretOperationTests()
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        _audit.LogAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(c => _events.Add((c.ArgAt<string>(0), JsonSerializer.Serialize(c.ArgAt<object?>(3)))));
    }

    private static Connection Of(ConnectionProvider provider, string name = "files") => new(
        Guid.NewGuid(), name, provider, """{"host":"files.example"}""", null, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(-1),
        HasSecret: true);

    private Connection Stored(ConnectionProvider provider)
    {
        Connection connection = Of(provider);
        _connections.GetAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);
        _connections.UpdateAsync(connection.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>()).Returns(connection);
        return connection;
    }

    // An administrator who isn't the Owner: every connection policy passes but the Owner's.
    private void NotTheOwner() => _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.SourcesOwner).Returns(false);

    private ReplaceConnectionSecret Replace() => new(_connections, _sources, _rules, _audit, _authorizer);

    private UpdateConnection Update() => new(_connections, _sources, _rules, _audit, _authorizer);

    [Fact]
    public async Task ReplaceSecret_NotTheOwner_IsForbiddenAndChecksNothing()
    {
        NotTheOwner();
        Connection sftp = Stored(ConnectionProvider.Sftp);

        var replaced = await Replace().RunAsync(Caller, new ConnectionRef(sftp.Id), new ConnectionSecret("KEY"));
        var stored = await Replace().RunCheckedAsync(Caller, ConnectionRef.AtlassianSite(sftp.Id),
            new CheckedSecret("{}", "s", sftp.UpdatedAt, new { }));

        replaced.Status.Should().Be(ConnectionStatus.Forbidden);
        replaced.Detail.Should().Be(ConnectionMessages.OwnerReplacesSecrets);
        stored.Status.Should().Be(ConnectionStatus.Forbidden);
        await _rules.DidNotReceiveWithAnyArgs().CheckAsync(default!, default, default!, default, default);
        await _connections.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task ReplaceSecret_SftpKey_IsCheckedWithTheStoredSettingsAndAuditedWithoutTheSecret()
    {
        Connection sftp = Stored(ConnectionProvider.Sftp);
        _rules.CheckAsync(default!, default, default!, default, default).ReturnsForAnyArgs(
            new SettingsCheck(sftp.ConfigJson, """{"privateKey":"KEY"}"""));

        var replaced = await Replace().RunAsync(Caller, new ConnectionRef(sftp.Id), new ConnectionSecret("KEY", "pass", Token: "ignored"));

        replaced.Succeeded.Should().BeTrue();
        await _rules.Received(1).CheckAsync(sftp.Name, ConnectionProvider.Sftp,
            new ConnectionSettings(null, "KEY", "pass"), sftp, Arg.Any<CancellationToken>());
        await _connections.Received(1).UpdateAsync(sftp.Id,
            Arg.Is<UpdateConnectionRequest>(r => r.Name == null && r.Secret == """{"privateKey":"KEY"}""" && r.ExpectedUpdatedAt == sftp.UpdatedAt),
            Arg.Any<CancellationToken>());
        _events.Should().ContainSingle().Which.Should().Be(("connection.credentials_replaced", """{"Name":"files","Provider":"Sftp"}"""));
    }

    [Fact]
    public async Task ReplaceSecret_VerifiedCustomToken_RecordsTheCheckAsTheConnectionsHealth()
    {
        Connection custom = Stored(ConnectionProvider.Custom);
        var health = new ConnectionHealth(DateTime.UtcNow, true, null, "Reachable, token accepted.", "test");
        _connections.RecordHealthAsync(custom.Id, Arg.Any<ConnectionHealth>(), Arg.Any<CancellationToken>()).Returns(health);
        _rules.CheckAsync(default!, default, default!, default, default).ReturnsForAnyArgs(
            new SettingsCheck(custom.ConfigJson, "tok") { Verified = true });

        var replaced = await Replace().RunAsync(Caller, new ConnectionRef(custom.Id), new ConnectionSecret(Token: "tok"));

        replaced.Result!.Connection.Health.Should().Be(health);
        await _rules.Received(1).CheckAsync(custom.Name, ConnectionProvider.Custom,
            new ConnectionSettings(null, Token: "tok"), custom, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ConnectionProvider.Sftp, "privateKey")]
    [InlineData(ConnectionProvider.Custom, "token")]
    public async Task ReplaceSecret_NoSecretGiven_IsInvalidOnItsField(ConnectionProvider provider, string field)
    {
        Connection connection = Stored(provider);

        var replaced = await Replace().RunAsync(Caller, new ConnectionRef(connection.Id), new ConnectionSecret(" ", "pass", " "));

        replaced.Status.Should().Be(ConnectionStatus.Invalid);
        replaced.Field.Should().Be(field);
        await _connections.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
    }

    [Theory]
    [InlineData(ConnectionProvider.S3)]
    [InlineData(ConnectionProvider.GitHub)]
    [InlineData(ConnectionProvider.Atlassian)]
    public async Task ReplaceSecret_ProviderWithoutASecretHere_IsInvalidWithoutAskingTheRules(ConnectionProvider provider)
    {
        Connection connection = Stored(provider);

        var replaced = await Replace().RunAsync(Caller, new ConnectionRef(connection.Id), new ConnectionSecret("KEY", Token: "tok"));

        replaced.Status.Should().Be(ConnectionStatus.Invalid);
        await _rules.DidNotReceiveWithAnyArgs().CheckAsync(default!, default, default!, default, default);
        await _connections.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
    }

    [Fact]
    public async Task ReplaceSecret_RulesRefuse_StoresNothing()
    {
        Connection custom = Stored(ConnectionProvider.Custom);
        _rules.CheckAsync(default!, default, default!, default, default).ReturnsForAnyArgs(
            SettingsCheck.Refuse("The source server refused the token.", "secret"));

        var replaced = await Replace().RunAsync(Caller, new ConnectionRef(custom.Id), new ConnectionSecret(Token: "bad"));

        replaced.Status.Should().Be(ConnectionStatus.Invalid);
        replaced.Field.Should().Be("secret");
        await _connections.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task RunChecked_SiteChangedSinceTheCheck_IsAConflictAndAuditsNothing()
    {
        Connection site = Stored(ConnectionProvider.Atlassian);
        _connections.UpdateAsync(site.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns<Connection?>(_ => throw new ConnectionChangedException(site.Id));

        var stored = await Replace().RunCheckedAsync(Caller, ConnectionRef.AtlassianSite(site.Id),
            new CheckedSecret("{}", "secret", site.UpdatedAt, new { }));

        stored.Status.Should().Be(ConnectionStatus.Conflict);
        stored.Detail.Should().Contain("site changed");
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task RunChecked_Stored_AuditsTheDetailsTheSiteCheckGave()
    {
        Connection site = Stored(ConnectionProvider.Atlassian);

        var stored = await Replace().RunCheckedAsync(Caller, ConnectionRef.AtlassianSite(site.Id),
            new CheckedSecret("""{"clientId":"new"}""", "secret", site.UpdatedAt, new { ClientId = "new" }));

        stored.Succeeded.Should().BeTrue();
        await _connections.Received(1).UpdateAsync(site.Id,
            new UpdateConnectionRequest(ConfigJson: """{"clientId":"new"}""", Secret: "secret", ExpectedUpdatedAt: site.UpdatedAt),
            Arg.Any<CancellationToken>());
        _events.Should().ContainSingle().Which.Should().Be(("connection.credentials_replaced", """{"ClientId":"new"}"""));
    }

    [Fact]
    public async Task RunChecked_AnotherProvidersConnection_ReadsAsNoSuchSite()
    {
        Connection sftp = Stored(ConnectionProvider.Sftp);

        var stored = await Replace().RunCheckedAsync(Caller, ConnectionRef.AtlassianSite(sftp.Id),
            new CheckedSecret("{}", "secret", sftp.UpdatedAt, new { }));

        stored.Status.Should().Be(ConnectionStatus.NotFound);
        await _connections.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
    }

    [Fact]
    public async Task Update_CarryingASecret_NotTheOwner_IsForbidden_ButSettingsAloneAreNot()
    {
        NotTheOwner();
        Connection sftp = Stored(ConnectionProvider.Sftp);
        _rules.CheckAsync(default!, default, default!, default, default).ReturnsForAnyArgs(new SettingsCheck(sftp.ConfigJson, null));
        var key = new ConnectionRef(sftp.Id);

        var withKey = await Update().RunAsync(Caller, key, new ConnectionChange(null, new ConnectionSettings(null, "KEY")));
        var withToken = await Update().RunAsync(Caller, key, new ConnectionChange(null, new ConnectionSettings("{}", Token: "tok")));
        var settingsOnly = await Update().RunAsync(Caller, key, new ConnectionChange(null, new ConnectionSettings("{}")));

        withKey.Status.Should().Be(ConnectionStatus.Forbidden);
        withKey.Detail.Should().Be(ConnectionMessages.OwnerReplacesSecrets);
        withToken.Status.Should().Be(ConnectionStatus.Forbidden);
        settingsOnly.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Secrets_NeverPrintTheirCredential()
    {
        new ConnectionSecret("KEY", "pass", "tok").ToString().Should().NotContain("KEY").And.NotContain("pass").And.NotContain("tok");
        new CheckedSecret("{}", "s3cr3t", DateTime.UtcNow, new { }).ToString().Should().NotContain("s3cr3t");
    }
}
