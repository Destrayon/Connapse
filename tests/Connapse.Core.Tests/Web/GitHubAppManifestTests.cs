using System.Text.Json;
using Connapse.Web.Components.Settings;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Connapse.Core.Tests.Web;

/// <summary>The manifest that registers Connapse's GitHub App, and the one-time state guarding its return.</summary>
[Trait("Category", "Unit")]
public class GitHubAppManifestTests
{
    [Fact]
    public void Build_AsksForReadOnlyAccessAndReturnsToThisConnapse()
    {
        using var manifest = JsonDocument.Parse(GitHubAppManifest.Build("https://connapse.example.test/", isPublic: false));
        var root = manifest.RootElement;

        root.GetProperty("redirect_url").GetString()
            .Should().Be("https://connapse.example.test/api/v1/providers/github/manifest/callback");
        root.GetProperty("setup_url").GetString()
            .Should().Be("https://connapse.example.test/api/v1/providers/github/installed", "installing returns to Connections");
        root.GetProperty("public").GetBoolean().Should().BeFalse();
        root.GetProperty("hook_attributes").GetProperty("active").GetBoolean().Should().BeFalse();

        var permissions = root.GetProperty("default_permissions").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString());
        permissions.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["metadata"] = "read", ["contents"] = "read", ["issues"] = "read", ["pull_requests"] = "read",
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_CarriesTheRequestedVisibility(bool isPublic)
    {
        using var manifest = JsonDocument.Parse(GitHubAppManifest.Build("https://connapse.example.test/", isPublic));

        manifest.RootElement.GetProperty("public").GetBoolean().Should().Be(isPublic);
    }

    [Fact]
    public void Name_FitsGitHubsLimit()
    {
        GitHubAppManifest.Name("a-very-long-hostname.trycloudflare.com").Length.Should().BeLessThanOrEqualTo(34);
        GitHubAppManifest.Name("localhost").Should().Be("Connapse (localhost)");
    }

    [Theory]
    [InlineData(null, "https://github.com/settings/apps/new?state=abc")]
    [InlineData("  ", "https://github.com/settings/apps/new?state=abc")]
    [InlineData("octo-org", "https://github.com/organizations/octo-org/settings/apps/new?state=abc")]
    [InlineData("../evil", null)]
    [InlineData("octo org", null)]
    public void FormAction_PersonalOrValidOrganisationOnly(string? organisation, string? expected)
    {
        GitHubAppManifest.FormAction(organisation, "abc").Should().Be(expected);
    }

    [Fact]
    public async Task ManifestRequests_ConcurrentCallbacks_OnlyOneClaimsTheState()
    {
        var requests = new GitHubManifestRequests(new MemoryCache(new MemoryCacheOptions()));
        var admin = Guid.NewGuid();
        string state = requests.Start(admin);

        bool[] claims = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => requests.TryComplete(state, admin))));

        claims.Count(c => c).Should().Be(1);
    }

    [Fact]
    public void ManifestRequests_CompleteOnceAndOnlyForWhoeverStartedThem()
    {
        var requests = new GitHubManifestRequests(new MemoryCache(new MemoryCacheOptions()));
        var admin = Guid.NewGuid();

        string mine = requests.Start(admin);
        requests.TryComplete(mine, admin).Should().BeTrue();
        requests.TryComplete(mine, admin).Should().BeFalse("a state is honoured once");

        string planted = requests.Start(Guid.NewGuid());
        requests.TryComplete(planted, admin).Should().BeFalse("another user's redirect must not complete for this admin");

        requests.TryComplete("never-issued", admin).Should().BeFalse();
        requests.TryComplete(null, admin).Should().BeFalse();
    }

    [Fact]
    public void ConnectionForm_GitHubInstallation_RoundTripsAndIsRequired()
    {
        var form = new ConnectionForm { Name = "github-octo", Provider = ConnectionProvider.GitHub };
        form.Validate().Should().Contain("installation");

        form.GitHubInstallationId = 77;
        form.GitHubAccount = "octo-org";
        form.Validate().Should().BeNull();

        string json = form.ToConfigJson();
        json.Should().Be("""{"installationId":77,"account":"octo-org"}""");

        var read = ConnectionForm.FromConnection(new Connection(
            Guid.NewGuid(), "github-octo", ConnectionProvider.GitHub, json, null, DateTime.UtcNow, DateTime.UtcNow));
        read.GitHubInstallationId.Should().Be(77);
        read.GitHubAccount.Should().Be("octo-org");
    }
}
