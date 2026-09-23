using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using Connapse.Storage.Connectors.GitHub;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>The GitHub App Connapse acts as, against a stubbed GitHub.</summary>
[Trait("Category", "Unit")]
public sealed class ConnapseGitHubAppTests : IDisposable
{
    private readonly RSA _key = RSA.Create(2048);
    private readonly StubGitHub _github = new();
    private readonly ManualClock _clock = new();
    private readonly IProviderCredentialStore _store = Substitute.For<IProviderCredentialStore>();

    public void Dispose() => _key.Dispose();

    private string Pem => _key.ExportRSAPrivateKeyPem();

    private ConnapseGitHubApp App(bool stored = true)
    {
        _store.GetGitHubAppMaterialAsync(Arg.Any<CancellationToken>()).Returns(stored
            ? new GitHubAppCredentialMaterial(
                new GitHubAppRegistration(42, "connapse-test", "Iv1.abc", "octo-org", "https://github.com/apps/connapse-test"),
                Pem, "secret")
            : null);

        var services = new ServiceCollection();
        services.AddSingleton(_store);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_github, disposeHandler: false));

        return new ConnapseGitHubApp(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), http,
            NullLogger<ConnapseGitHubApp>.Instance, _clock)
        { ApiBaseUrl = "https://api.github.test" };
    }

    [Fact]
    public void CreateJwt_IsAnRs256TokenTheKeysPublicHalfVerifies()
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

        string jwt = ConnapseGitHubApp.CreateJwt(42, Pem, now);

        string[] parts = jwt.Split('.');
        parts.Should().HaveCount(3);
        _key.VerifyData(
                Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromBase64Url(parts[2]),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .Should().BeTrue();

        using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
        header.RootElement.GetProperty("alg").GetString().Should().Be("RS256");

        using var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
        payload.RootElement.GetProperty("iss").GetString().Should().Be("42");
        long iat = payload.RootElement.GetProperty("iat").GetInt64();
        long exp = payload.RootElement.GetProperty("exp").GetInt64();
        iat.Should().Be(now.AddSeconds(-60).ToUnixTimeSeconds(), "issued in the past to absorb clock drift");
        (exp - iat).Should().BeLessThanOrEqualTo(600, "GitHub refuses App JWTs valid for more than ten minutes");
    }

    [Fact]
    public void CreateJwt_UnreadableKey_SaysSo()
    {
        Action act = () => ConnapseGitHubApp.CreateJwt(42, "not a key", DateTimeOffset.UtcNow);

        act.Should().Throw<GitHubAppException>().WithMessage("*not a readable RSA key*");
    }

    [Fact]
    public async Task GetInstallationTokenAsync_ReusesATokenUntilNearItsExpiry()
    {
        var app = App();
        _github.TokenExpiresAt = _clock.Now.AddHours(1);

        var first = await app.GetInstallationTokenAsync(7);
        var second = await app.GetInstallationTokenAsync(7);

        second.Should().Be(first);
        _github.TokenRequests.Should().Be(1);
        _github.LastAuthorization.Should().StartWith("Bearer ey", "tokens are minted with the App JWT");

        _clock.Advance(TimeSpan.FromMinutes(56));
        await app.GetInstallationTokenAsync(7);
        _github.TokenRequests.Should().Be(2, "a token within five minutes of expiry is replaced");
    }

    [Fact]
    public async Task ClearCache_DropsCachedTokens()
    {
        var app = App();
        _github.TokenExpiresAt = _clock.Now.AddHours(1);
        await app.GetInstallationTokenAsync(7);

        app.ClearCache();
        await app.GetInstallationTokenAsync(7);

        _github.TokenRequests.Should().Be(2);
    }

    [Fact]
    public async Task ListInstallationsAsync_ReadsEachAccount()
    {
        var installations = await App().ListInstallationsAsync();

        installations.Should().ContainSingle();
        installations[0].Should().Be(new GitHubAppInstallation(7, "octo-org", "Organization", "all", "https://github.com/organizations/octo-org/settings/installations/7"));
    }

    [Fact]
    public async Task ConvertManifestAsync_ReturnsTheAppItsKeyAndSecret_WithoutAuthenticating()
    {
        var result = await App(stored: false).ConvertManifestAsync("temp-code");

        result.App.AppId.Should().Be(99);
        result.App.Slug.Should().Be("connapse-new");
        result.App.OwnerLogin.Should().Be("octo-org");
        result.PrivateKeyPem.Should().Contain("PRIVATE KEY");
        result.ClientSecret.Should().Be("s3cret");
        _github.LastAuthorization.Should().BeNull("the one-time code is the credential for this call");
        _github.LastPath.Should().Be("/app-manifests/temp-code/conversions");
    }

    [Fact]
    public async Task GetInstallationTokenAsync_NoAppStored_SaysSetOneUp()
    {
        Func<Task> act = () => App(stored: false).GetInstallationTokenAsync(7);

        await act.Should().ThrowAsync<GitHubAppException>().WithMessage("*Providers page*");
    }

    [Fact]
    public async Task VerifyAsync_RefusedByGitHub_CarriesTheStatusAndMessage()
    {
        _github.RefuseApp = true;

        Func<Task> act = () => App().VerifyAsync(42, Pem);

        (await act.Should().ThrowAsync<GitHubAppException>()).Which.StatusCode.Should().Be(401);
    }

    private static byte[] FromBase64Url(string s)
    {
        string padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    [Theory]
    [InlineData("octo-org", GitHubAccountKind.Organization)]
    [InlineData("octocat", GitHubAccountKind.User)]
    [InlineData("nobody-here", GitHubAccountKind.None)]
    public async Task GetAccountKindAsync_TellsOrganisationsFromPeopleAndNobody_WithoutAuthenticating(
        string login, GitHubAccountKind expected)
    {
        var app = App(stored: false);

        (await app.GetAccountKindAsync(login)).Should().Be(expected);
        _github.LastAuthorization.Should().BeNull("the check runs before any App exists");
    }

    [Fact]
    public async Task ClearCache_DuringAnInFlightMint_DoesNotLetThatTokenBeCached()
    {
        var app = App();
        _github.HoldTokens = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var inFlight = app.GetInstallationTokenAsync(7);
        await _github.TokenRequested.Task;
        app.ClearCache(); // the App is removed or replaced while GitHub is answering
        _github.HoldTokens.SetResult();
        await inFlight;

        _github.HoldTokens = null;
        await app.GetInstallationTokenAsync(7);

        _github.TokenRequests.Should().Be(2, "a token minted before the clear must not be reused after it");
    }

    private sealed class StubGitHub : HttpMessageHandler
    {
        private static readonly object NotFound = new();

        public int TokenRequests { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastPath { get; private set; }
        public DateTimeOffset TokenExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(1);
        public bool RefuseApp { get; set; }

        /// <summary>When set, a token request waits for this before GitHub "answers".</summary>
        public TaskCompletionSource? HoldTokens { get; set; }
        public TaskCompletionSource TokenRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal) && HoldTokens is { } hold)
            {
                TokenRequested.TrySetResult();
                await hold.Task;
            }

            return Answer(request);
        }

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastPath = request.RequestUri!.AbsolutePath;

            object? body = (request.Method.Method, LastPath) switch
            {
                ("GET", "/app") when RefuseApp => null,
                ("GET", "/users/nobody-here") => NotFound,
                ("GET", "/users/octo-org") => new { login = "octo-org", type = "Organization" },
                ("GET", "/users/octocat") => new { login = "octocat", type = "User" },
                ("GET", "/app") => new { id = 42, slug = "connapse-test", client_id = "Iv1.abc", owner = new { login = "octo-org" }, html_url = "https://github.com/apps/connapse-test" },
                ("GET", "/app/installations") => new[]
                {
                    new { id = 7, account = new { login = "octo-org", type = "Organization" }, repository_selection = "all", html_url = "https://github.com/organizations/octo-org/settings/installations/7" },
                },
                ("POST", "/app/installations/7/access_tokens") => Token(),
                ("POST", "/app-manifests/temp-code/conversions") => new
                {
                    id = 99, slug = "connapse-new", client_id = "Iv1.new", client_secret = "s3cret",
                    pem = "-----BEGIN RSA PRIVATE KEY-----\nabc\n-----END RSA PRIVATE KEY-----", owner = new { login = "octo-org" },
                    html_url = "https://github.com/apps/connapse-new",
                },
                _ => throw new InvalidOperationException("unexpected " + request.RequestUri),
            };

            if (ReferenceEquals(body, NotFound))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"message":"Not Found"}"""),
                };
            }

            var response = body is null
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"message":"A JSON web token could not be decoded"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };

            return response;
        }

        private object Token()
        {
            TokenRequests++;
            return new { token = "ghs_" + TokenRequests, expires_at = TokenExpiresAt };
        }
    }
}
