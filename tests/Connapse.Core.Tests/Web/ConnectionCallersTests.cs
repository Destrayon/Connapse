using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Connections;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Web;

/// <summary>
/// The Connections page's edit Save: a new secret alone goes through connections.replaceSecret, anything else
/// through connections.update, and each refusal reaches the place the page shows it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConnectionCallersTests
{
    private static readonly AuthenticationState User = new(new ClaimsPrincipal(new ClaimsIdentity("test")));

    private readonly IConnectionStore _connections = Substitute.For<IConnectionStore>();
    private readonly IConnectionSettingsRules _rules = Substitute.For<IConnectionSettingsRules>();
    private readonly IOperationAuthorizer _authorizer = Substitute.For<IOperationAuthorizer>();
    private readonly ConnectionOperations _operations;
    private readonly List<string> _failed = [];
    private readonly List<string> _nameRefused = [];

    public ConnectionCallersTests()
    {
        var audit = Substitute.For<IAuditLogger>();
        var sources = Substitute.For<ISourceStore>();
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        _operations = new ConnectionOperations(
            new ListConnections(_connections, sources, _authorizer), new GetConnection(_connections, sources, _authorizer),
            new CreateConnection(_connections, _rules, audit, _authorizer), new UpdateConnection(_connections, sources, _rules, audit, _authorizer),
            new ReplaceConnectionSecret(_connections, sources, _rules, audit, _authorizer), new PauseConnection(_connections, sources, audit, _authorizer),
            new ResumeConnection(_connections, sources, audit, _authorizer), new DeleteConnection(_connections, sources, audit, _authorizer),
            new TestConnection(_connections, Substitute.For<IConnectionTestRunner>(), _authorizer),
            new ForgetConnectionHostKey(_connections, sources, audit, _authorizer));
    }

    private Connection Stored()
    {
        Connection sftp = new(Guid.NewGuid(), "files", ConnectionProvider.Sftp, """{"host":"files.example"}""", null,
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(-1), HasSecret: true);
        _connections.GetAsync(sftp.Id, Arg.Any<CancellationToken>()).Returns(sftp);
        _connections.UpdateAsync(sftp.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>()).Returns(sftp);
        _connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Connection> { sftp });
        _rules.CheckAsync(default!, default, default!, default, default).ReturnsForAnyArgs(new SettingsCheck(sftp.ConfigJson, "SECRET"));
        return sftp;
    }

    private Task<Attempt<ConnectionView, ConnectionStatus>> SaveAsync(Connection editing, string name, ConnectionSettings? settings) =>
        _operations.SaveAsync(User, editing, name, settings, _failed.Add, n => { _nameRefused.Add(n); return Task.CompletedTask; },
            (_, _) => Task.CompletedTask);

    [Fact]
    public async Task SaveAsync_OnlyANewKey_ReplacesTheSecretKeepingTheSettings()
    {
        Connection sftp = Stored();

        var saved = await SaveAsync(sftp, " files ", new ConnectionSettings(sftp.ConfigJson, "KEY", "pass"));

        saved.Succeeded.Should().BeTrue();
        await _rules.Received(1).CheckAsync(sftp.Name, ConnectionProvider.Sftp, new ConnectionSettings(null, "KEY", "pass"), sftp,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_NewSettingsWithTheKey_SavesThemTogetherThroughUpdate()
    {
        Connection sftp = Stored();
        var settings = new ConnectionSettings("""{"host":"other.example"}""", "KEY");

        var saved = await SaveAsync(sftp, "files", settings);

        saved.Succeeded.Should().BeTrue();
        await _rules.Received(1).CheckAsync("files", ConnectionProvider.Sftp, settings, sftp, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_NewKeyFromAnAdministratorWhoIsNotTheOwner_ReplacesIt()
    {
        Connection sftp = Stored();
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(false);
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.SourcesAdmin).Returns(true);

        var saved = await SaveAsync(sftp, "files", new ConnectionSettings(sftp.ConfigJson, "KEY"));

        saved.Succeeded.Should().BeTrue();
        _failed.Should().BeEmpty();
        await _connections.Received(1).UpdateAsync(sftp.Id, Arg.Is<UpdateConnectionRequest>(r => r.Secret == "SECRET"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_NewKeyFromSomeoneWhoIsNotAnAdministrator_IsRefusedAboveTheForm()
    {
        Connection sftp = Stored();
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.SourcesAdmin).Returns(false);

        var saved = await SaveAsync(sftp, "files", new ConnectionSettings(sftp.ConfigJson, "KEY"));

        saved.Status.Should().Be(ConnectionStatus.Forbidden);
        _failed.Should().Equal(ConnectionMessages.CannotReplaceSecrets);
        await _connections.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
    }

    [Fact]
    public async Task SaveAsync_NameAnotherConnectionHas_IsRefusedUnderTheNameField()
    {
        Connection sftp = Stored();
        Connection other = sftp with { Id = Guid.NewGuid(), Name = "taken" };
        _connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Connection> { sftp, other });

        var saved = await SaveAsync(sftp, "taken", null);

        saved.Status.Should().Be(ConnectionStatus.Conflict);
        _nameRefused.Should().Equal(ConnectionMessages.NameTaken("taken"));
        _failed.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_ConnectionDeletedElsewhere_LeavesTheMessageToThePage()
    {
        Connection sftp = Stored();
        _connections.GetAsync(sftp.Id, Arg.Any<CancellationToken>()).Returns((Connection?)null);

        var saved = await SaveAsync(sftp, "renamed", null);

        saved.Status.Should().Be(ConnectionStatus.NotFound);
        _failed.Should().BeEmpty();
        _nameRefused.Should().BeEmpty();
    }
}
