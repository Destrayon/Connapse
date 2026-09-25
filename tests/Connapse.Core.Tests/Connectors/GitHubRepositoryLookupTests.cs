using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.GitHub;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>The New source dialog's check that a repository exists and is public, asked as an installation.</summary>
[Trait("Category", "Unit")]
public sealed class GitHubRepositoryLookupTests
{
    private readonly Stub _github = new();

    private GitHubRepositoryLookup Lookup()
    {
        using var rsa = RSA.Create(2048);
        var store = Substitute.For<IProviderCredentialStore>();
        store.GetGitHubAppMaterialAsync(Arg.Any<CancellationToken>()).Returns(new GitHubAppCredentialMaterial(
            new GitHubAppRegistration(42, "connapse-test", null, "octo-org", "https://github.com/apps/connapse-test"),
            rsa.ExportRSAPrivateKeyPem(), null));

        var services = new ServiceCollection();
        services.AddSingleton(store);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_github, disposeHandler: false));

        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var app = new ConnapseGitHubApp(scopes, http, NullLogger<ConnapseGitHubApp>.Instance) { ApiBaseUrl = "https://api.github.test" };

        return new GitHubRepositoryLookup(new GitHubCredentialPool(app, scopes), http) { ApiBaseUrl = "https://api.github.test" };
    }

    [Theory]
    [InlineData("public", true)]
    [InlineData("private", false)]
    [InlineData("internal", false)]
    public async Task FindAsync_ReportsVisibility(string visibility, bool isPublic)
    {
        _github.Visibility = visibility;

        var found = await Lookup().FindAsync("octocat", "hello", installationId: 7);

        found!.FullName.Should().Be("octocat/hello");
        found.IsPublic.Should().Be(isPublic);
        _github.LastRepoAuthorization.Should().Be("Bearer tok-7", "the check reads as the chosen installation");
    }

    [Fact]
    public async Task FindAsync_Missing_IsNull()
    {
        _github.Visibility = null;

        (await Lookup().FindAsync("octocat", "nope", installationId: 7)).Should().BeNull();
    }

    private sealed class Stub : HttpMessageHandler
    {
        public string? Visibility { get; set; } = "public";
        public string? LastRepoAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;

            if (path.StartsWith("/app/installations/", StringComparison.Ordinal))
                return Json(new { token = "tok-" + path.Split('/')[3], expires_at = DateTimeOffset.UtcNow.AddHours(1) });

            LastRepoAuthorization = request.Headers.Authorization?.ToString();
            return Visibility is null
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                : Json(new { id = 1, full_name = "octocat/hello", @private = Visibility != "public", visibility = Visibility });
        }

        private static Task<HttpResponseMessage> Json(object body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) });
    }
}
