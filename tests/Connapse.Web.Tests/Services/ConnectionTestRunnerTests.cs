using Connapse.Application.Connections;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.GitHub;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Web.Tests.Services;

/// <summary>
/// connections.test's live checks, where they refuse before reaching a provider: a stored SFTP key or custom token
/// is only ever used with the stored settings it was saved for.
/// </summary>
[Trait("Category", "Unit")]
public class ConnectionTestRunnerTests
{
    private const string SftpConfig = """{"host":"files.example","port":22,"username":"me","allowedRoot":"/data"}""";

    private readonly IConnectionStore _connections = Substitute.For<IConnectionStore>();
    private readonly IContainerHostResolver _hosts = Substitute.For<IContainerHostResolver>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();

    public ConnectionTestRunnerTests() =>
        _hosts.Resolve(Arg.Any<string?>()).Returns(c => new HostResolution(c.Arg<string?>() ?? "", false, null));

    // The GitHub App, Azure, S3 and Atlassian testers are never reached by these refusals.
    private ConnectionTestRunner Runner()
    {
        var settings = Substitute.For<IOptionsMonitor<CustomSourceSettings>>();
        settings.CurrentValue.Returns(new CustomSourceSettings());
        var customProviders = Substitute.For<ICustomProviderStore>();
        var setup = new CustomSourceSetup(Substitute.For<IHttpClientFactory>(), settings, customProviders, _connections);
        var rules = new ConnectionSettingsRules(_connections, customProviders, setup, Substitute.For<IGitHubAppInstallations>(),
            Substitute.For<IS3Discovery>(), _hosts, _audit);
        return new ConnectionTestRunner(_connections, rules, setup, null!, new SftpConnectionTester(), null!, null!, _hosts, null!, _audit);
    }

    private static Connection Stored(ConnectionProvider provider, string configJson) =>
        new(Guid.NewGuid(), "stored", provider, configJson, null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);

    [Theory]
    [InlineData("""{"host":"attacker.example","port":22,"username":"me","allowedRoot":"/data"}""")]
    [InlineData("""{"host":"files.example","port":2222,"username":"me","allowedRoot":"/data"}""")]
    [InlineData("""{"host":"files.example","port":22,"username":"root","allowedRoot":"/data"}""")]
    public async Task Test_SftpOtherServerWithoutAKey_IsRefusedBeforeTheStoredKeyIsRead(string configJson)
    {
        Connection stored = Stored(ConnectionProvider.Sftp, SftpConfig);

        ConnectionTestRun run = await Runner().TestAsync(
            new ConnectionTestSubject(stored.Name, ConnectionProvider.Sftp, new ConnectionSettings(configJson), stored));

        run.Result.Should().BeNull();
        run.Field.Should().Be("privateKey");
        run.Problem.Should().Be("Paste the private key for this server. The saved key is only used with me@files.example:22.");
        await _connections.DidNotReceiveWithAnyArgs().GetSecretAtAsync(default, default, default);
    }

    [Fact]
    public async Task Test_SftpStoredSettingsWithoutAKey_UsesTheStoredKey()
    {
        Connection stored = Stored(ConnectionProvider.Sftp, SftpConfig);
        _connections.GetSecretAtAsync(stored.Id, stored.UpdatedAt, Arg.Any<CancellationToken>()).Returns((string?)null);

        ConnectionTestRun run = await Runner().TestAsync(
            new ConnectionTestSubject(stored.Name, ConnectionProvider.Sftp, null, stored));

        await _connections.Received(1).GetSecretAtAsync(stored.Id, stored.UpdatedAt, Arg.Any<CancellationToken>());
        run.Problem.Should().Be("This connection has no stored key. Paste one to test.");
    }

    [Fact]
    public async Task Test_SftpSavedAfterItsServerWasRead_IsAConflictAndUsesNoKey()
    {
        Connection stored = Stored(ConnectionProvider.Sftp, SftpConfig);
        _connections.GetSecretAtAsync(stored.Id, stored.UpdatedAt, Arg.Any<CancellationToken>())
            .Returns<string?>(_ => throw new ConnectionChangedException(stored.Id));

        ConnectionTestRun run = await Runner().TestAsync(
            new ConnectionTestSubject(stored.Name, ConnectionProvider.Sftp, null, stored));

        run.Result.Should().BeNull();
        run.Refusal.Should().Be(ConnectionStatus.Conflict);
        run.Problem.Should().Be(ConnectionMessages.ChangedWhileChecked);
        await _connections.DidNotReceiveWithAnyArgs().GetSecretAsync(default, default);
    }

    [Fact]
    public async Task Test_CustomSavedAfterItsAddressWasRead_IsAConflictAndSendsNoToken()
    {
        Connection stored = Stored(ConnectionProvider.Custom, CustomConnectionConfig.ForMiddleMan("https://source.example"));
        _connections.GetSecretAtAsync(stored.Id, stored.UpdatedAt, Arg.Any<CancellationToken>())
            .Returns<string?>(_ => throw new ConnectionChangedException(stored.Id));

        // The setup's HTTP client factory is a bare substitute, so reaching the server would throw.
        ConnectionTestRun run = await Runner().TestAsync(new ConnectionTestSubject(stored.Name, ConnectionProvider.Custom, null, stored));

        run.Result.Should().BeNull();
        run.Health.Should().BeNull();
        run.Refusal.Should().Be(ConnectionStatus.Conflict);
        await _connections.DidNotReceiveWithAnyArgs().GetSecretAsync(default, default);
    }

    [Fact]
    public async Task Test_SftpDraftWithoutAKey_IsRefused()
    {
        ConnectionTestRun run = await Runner().TestAsync(
            new ConnectionTestSubject("new", ConnectionProvider.Sftp, new ConnectionSettings(SftpConfig), null));

        run.Field.Should().Be("privateKey");
        run.Refusal.Should().Be(ConnectionStatus.Invalid);
    }

    [Fact]
    public async Task Test_CustomAddressChangedWithoutAToken_IsRefusedWithoutReadingTheTokenOrRecordingHealth()
    {
        Connection stored = Stored(ConnectionProvider.Custom, CustomConnectionConfig.ForMiddleMan("https://source.example"));

        ConnectionTestRun run = await Runner().TestAsync(new ConnectionTestSubject(stored.Name, ConnectionProvider.Custom,
            new ConnectionSettings(CustomConnectionConfig.ForMiddleMan("https://attacker.example")), stored));

        run.Result.Should().BeNull();
        run.Health.Should().BeNull();
        run.Field.Should().Be("secret");
        await _connections.DidNotReceiveWithAnyArgs().GetSecretAtAsync(default, default, default);
    }

    [Fact]
    public async Task Test_S3WithNoBucketToProbe_AsksForOne()
    {
        ConnectionTestRun run = await Runner().TestAsync(
            new ConnectionTestSubject("bucket", ConnectionProvider.S3, new ConnectionSettings("""{"region":"us-east-1"}"""), null));

        run.Field.Should().Be("probeTarget");
    }

    [Theory]
    [InlineData(ConnectionProvider.Filesystem, "provider")]
    [InlineData(ConnectionProvider.Atlassian, "provider")]
    public async Task Test_ProviderWithNothingToTestHere_IsRefused(ConnectionProvider provider, string field)
    {
        ConnectionTestRun run = await Runner().TestAsync(new ConnectionTestSubject("x", provider, new ConnectionSettings("{}"), null));

        run.Result.Should().BeNull();
        run.Field.Should().Be(field);
        run.Refusal.Should().Be(ConnectionStatus.Invalid);
    }
}
