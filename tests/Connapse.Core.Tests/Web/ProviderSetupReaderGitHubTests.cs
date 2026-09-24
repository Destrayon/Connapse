using System.Net;
using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Connectors.GitHub;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Web;

/// <summary>The GitHub entry on the Providers page: whether the App is set up, installed, and still accepted.</summary>
[Trait("Category", "Unit")]
public sealed class ProviderSetupReaderGitHubTests
{
    private static readonly GitHubAppRegistration App =
        new(42, "connapse-test", "Iv1.client", "octo-org", "https://github.com/apps/connapse-test");

    private readonly IProviderCredentialStore _credentials = Substitute.For<IProviderCredentialStore>();

    private ProviderSetupReader Reader(HttpStatusCode installationsStatus, int installations, string? clientSecret = "secret")
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        _credentials.GetGitHubAppMaterialAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubAppCredentialMaterial(App, rsa.ExportRSAPrivateKeyPem(), clientSecret));

        var services = new ServiceCollection();
        services.AddSingleton(_credentials);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new Stub(installationsStatus, installations)));

        var gitHubApp = new ConnapseGitHubApp(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), http,
            NullLogger<ConnapseGitHubApp>.Instance) { ApiBaseUrl = "https://api.github.test" };

        var connections = Substitute.For<IConnectionStore>();
        connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        return new ProviderSetupReader(
            Monitor(new SamlSignInSettings()), Monitor(new IdentityCenterSettings()),
            Monitor(new AzureProviderSettings()), Monitor(new AzureAdSignInSettings()),
            Substitute.For<IS3Discovery>(), Substitute.For<IAzureBlobDiscovery>(),
            Monitor(new PermissionEnforcementSettings()), connections, _credentials,
            TimeProvider.System, NullLogger<ProviderSetupReader>.Instance, gitHubApp);
    }

    private static IOptionsMonitor<T> Monitor<T>(T value) where T : class
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    private async Task<ProviderSetup> GitHubAsync(ProviderSetupReader reader) =>
        (await reader.ReadAsync()).Single(p => p.Key == "github");

    [Fact]
    public async Task ReadAsync_NoAppStored_IsNotConfiguredAndNotInUse()
    {
        _credentials.GetGitHubAppAsync(Arg.Any<CancellationToken>()).Returns((GitHubAppRegistration?)null);

        var github = await GitHubAsync(Reader(HttpStatusCode.OK, 1));

        github.InUse.Should().BeFalse();
        github.Requirements.Single().Status.Should().Be(RequirementStatus.NotConfigured);
    }

    [Fact]
    public async Task ReadAsync_AppInstalled_IsSatisfiedAndNamesTheInstallations()
    {
        _credentials.GetGitHubAppAsync(Arg.Any<CancellationToken>()).Returns(App);

        var github = await GitHubAsync(Reader(HttpStatusCode.OK, 1));

        github.InUse.Should().BeTrue();
        var requirement = github.Requirements.Single();
        requirement.Status.Should().Be(RequirementStatus.Satisfied);
        requirement.Detail.Should().Contain("octo-org");
        await _credentials.Received().MarkVerifiedAsync("github", Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadAsync_AppInstalledNowhere_IsSetUpAndPointsAtConnections()
    {
        _credentials.GetGitHubAppAsync(Arg.Any<CancellationToken>()).Returns(App);

        var requirement = (await GitHubAsync(Reader(HttpStatusCode.OK, 0))).Requirements.Single();

        requirement.Status.Should().Be(RequirementStatus.Satisfied, "installing is a connection's step, not the provider's");
        requirement.ActionHref.Should().Be("/connections?new=github");
    }

    [Fact]
    public async Task ReadAsync_AppWithoutClientSecret_WarnsThatNobodyCanLinkAnAccount()
    {
        _credentials.GetGitHubAppAsync(Arg.Any<CancellationToken>()).Returns(App);

        var requirement = (await GitHubAsync(Reader(HttpStatusCode.OK, 1, clientSecret: null))).Requirements.Single();

        requirement.Status.Should().Be(RequirementStatus.Warning,
            "an App that cannot sign people in hides every private repository from everyone");
        requirement.Detail.Should().Contain("cannot link their GitHub accounts");
    }

    [Fact]
    public async Task ReadAsync_KeyRefused_Fails()
    {
        _credentials.GetGitHubAppAsync(Arg.Any<CancellationToken>()).Returns(App);

        var requirement = (await GitHubAsync(Reader(HttpStatusCode.Unauthorized, 0))).Requirements.Single();

        requirement.Status.Should().Be(RequirementStatus.Failed);
    }

    private sealed class Stub(HttpStatusCode status, int installations) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var items = Enumerable.Range(1, installations).Select(i => new
            {
                id = i, account = new { login = "octo-org", type = "Organization" },
                repository_selection = "all", html_url = "https://github.com/x",
            });

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK
                    ? JsonSerializer.Serialize(items)
                    : """{"message":"Bad credentials"}"""),
            });
        }
    }
}
