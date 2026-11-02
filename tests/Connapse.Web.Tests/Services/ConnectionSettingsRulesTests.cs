using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.GitHub;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Web.Tests.Services;

/// <summary>The Connections page's save checks, as connections.create and connections.update run them.</summary>
[Trait("Category", "Unit")]
public class ConnectionSettingsRulesTests
{
    private readonly IConnectionStore _connections = Substitute.For<IConnectionStore>();
    private readonly ICustomProviderStore _customProviders = Substitute.For<ICustomProviderStore>();
    private readonly IGitHubAppInstallations _gitHubApp = Substitute.For<IGitHubAppInstallations>();
    private readonly IS3Discovery _s3 = Substitute.For<IS3Discovery>();
    private readonly IContainerHostResolver _hosts = Substitute.For<IContainerHostResolver>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();

    public ConnectionSettingsRulesTests() =>
        _hosts.Resolve(Arg.Any<string?>()).Returns(c => new HostResolution(c.Arg<string?>() ?? "", false, null));

    private ConnectionSettingsRules Rules()
    {
        var settings = Substitute.For<IOptionsMonitor<CustomSourceSettings>>();
        settings.CurrentValue.Returns(new CustomSourceSettings());
        var setup = new CustomSourceSetup(Substitute.For<IHttpClientFactory>(), settings, _customProviders, _connections);
        return new ConnectionSettingsRules(_connections, _customProviders, setup, _gitHubApp, _s3, _hosts, _audit);
    }

    private static Connection Stored(ConnectionProvider provider, string configJson) =>
        new(Guid.NewGuid(), "stored", provider, configJson, null, DateTime.UtcNow, DateTime.UtcNow);

    [Theory]
    [InlineData("[1]")]
    [InlineData("not json")]
    public async Task Check_ConfigNotAnObject_IsRefused(string configJson)
    {
        SettingsCheck check = await Rules().CheckAsync("box", ConnectionProvider.Filesystem, new ConnectionSettings(configJson), null);

        check.Error.Should().Be("config must be a JSON object of the provider's settings.");
        check.Field.Should().Be("config");
    }

    [Fact]
    public async Task Check_FormRuleBroken_SaysWhichRule()
    {
        SettingsCheck check = await Rules().CheckAsync("box", ConnectionProvider.Filesystem, new ConnectionSettings("{}"), null);

        check.Error.Should().Be("Choose an allowed root.");
        check.Field.Should().Be("config");
    }

    [Fact]
    public async Task Check_S3WithoutRegion_TakesTheBucketsRegionFromAws()
    {
        _s3.GetBucketRegionAsync("docs", null, Arg.Any<CancellationToken>()).Returns(AwsProbe<string>.Ok("eu-west-1"));

        SettingsCheck check = await Rules().CheckAsync("bucket", ConnectionProvider.S3,
            new ConnectionSettings("""{"allowedLocations":["docs/team"]}"""), null);

        check.Error.Should().BeNull();
        check.ConfigJson.Should().Contain("\"region\":\"eu-west-1\"");
    }

    [Fact]
    public async Task Check_S3RegionNobodyCouldLookUp_IsRefusedRatherThanGuessed()
    {
        _s3.GetBucketRegionAsync(default!, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("no credentials"));

        SettingsCheck check = await Rules().CheckAsync("bucket", ConnectionProvider.S3,
            new ConnectionSettings("""{"allowedLocations":["docs"]}"""), null);

        check.Field.Should().Be("region");
        check.Refusal.Should().Be(SettingsRefusal.Invalid);
    }

    [Fact]
    public async Task Check_SftpLoopbackInsideAContainer_StoresTheReachableHostWithANote()
    {
        _hosts.Resolve("localhost").Returns(new HostResolution("host.docker.internal", true, "Rewrote localhost."));

        SettingsCheck check = await Rules().CheckAsync("box", ConnectionProvider.Sftp,
            new ConnectionSettings("""{"host":"localhost","username":"me","allowedRoot":"/data"}""", PrivateKey: "key"), null);

        check.Error.Should().BeNull();
        check.ConfigJson.Should().Contain("\"host\":\"host.docker.internal\"");
        check.Note.Should().Be("Rewrote localhost.");
    }

    [Fact]
    public async Task Check_GitHubWithoutTheApp_IsRefused()
    {
        _gitHubApp.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(false);

        SettingsCheck check = await Rules().CheckAsync("repo", ConnectionProvider.GitHub,
            new ConnectionSettings("""{"installationId":7}"""), null);

        check.Error.Should().Be("Set up the GitHub App on the Providers page first.");
        check.Field.Should().Be("installationId");
    }

    [Fact]
    public async Task Check_GitHubInstallationGone_IsRefused()
    {
        _gitHubApp.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitHubApp.ListInstallationsAsync(Arg.Any<CancellationToken>()).Returns([new GitHubAppInstallation(8, "other", "User", "all", "")]);

        SettingsCheck check = await Rules().CheckAsync("repo", ConnectionProvider.GitHub,
            new ConnectionSettings("""{"installationId":7}"""), null);

        check.Error.Should().StartWith("The GitHub App is no longer installed there.");
    }

    [Fact]
    public async Task Check_GitHubInstallation_TakesTheAccountFromGitHubNotTheCaller()
    {
        _gitHubApp.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitHubApp.ListInstallationsAsync(Arg.Any<CancellationToken>()).Returns([new GitHubAppInstallation(7, "acme", "Organization", "all", "")]);

        SettingsCheck check = await Rules().CheckAsync("repo", ConnectionProvider.GitHub,
            new ConnectionSettings("""{"installationId":7,"account":"spoofed"}"""), null);

        check.Error.Should().BeNull();
        check.ConfigJson.Should().Contain("\"account\":\"acme\"");
    }

    [Fact]
    public async Task Check_GitHubUnreachable_IsUnavailableNotInvalid()
    {
        _gitHubApp.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitHubApp.ListInstallationsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("down"));

        SettingsCheck check = await Rules().CheckAsync("repo", ConnectionProvider.GitHub,
            new ConnectionSettings("""{"installationId":7}"""), null);

        check.Refusal.Should().Be(SettingsRefusal.Unavailable);
        check.Field.Should().Be("installationId");
    }

    [Fact]
    public async Task Check_GitHubInstallationUnchanged_IsNotAskedAgain()
    {
        Connection current = Stored(ConnectionProvider.GitHub, """{"installationId":7,"account":"acme"}""");

        SettingsCheck check = await Rules().CheckAsync("repo", ConnectionProvider.GitHub,
            new ConnectionSettings("""{"installationId":7,"account":"acme","allowedLocations":["acme/docs"]}"""), current);

        check.Error.Should().BeNull();
        await _gitHubApp.DidNotReceiveWithAnyArgs().ListInstallationsAsync(default);
    }

    [Theory]
    [InlineData("https://old.example", "https://evil.example")]
    [InlineData("https://old.example/TenantA", "https://old.example/tenanta")]
    public async Task Check_CustomAddressChangedWithoutAToken_IsRefusedBeforeTheStoredTokenIsSent(string storedUrl, string newUrl)
    {
        Connection stored = Stored(ConnectionProvider.Custom, CustomConnectionConfig.ForMiddleMan(storedUrl));
        Connection current = stored with { HasSecret = true };

        SettingsCheck check = await Rules().CheckAsync("src", ConnectionProvider.Custom,
            new ConnectionSettings(CustomConnectionConfig.ForMiddleMan(newUrl)), current);

        check.Field.Should().Be("secret");
        await _connections.DidNotReceiveWithAnyArgs().GetSecretAsync(default, default);
        await _audit.Received(1).LogAsync("connection.test_step_failed", "connection", current.Id.ToString(),
            Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Check_CustomProviderThatDoesNotExist_IsRefused()
    {
        Guid providerId = Guid.NewGuid();

        SettingsCheck check = await Rules().CheckAsync("src", ConnectionProvider.Custom,
            new ConnectionSettings(CustomConnectionConfig.ToJson(providerId)), null);

        check.Error.Should().Be("No custom provider has that id.");
    }
}
