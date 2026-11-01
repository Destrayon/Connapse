using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>The GitHub App stored as the <c>github</c> provider credential, against real PostgreSQL.</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class GitHubAppCredentialStoreIntegrationTests(SharedWebAppFixture fixture)
{
    private static readonly GitHubAppRegistration App =
        new(4242, "connapse-it", "Iv1.it", "octo-org", "https://github.com/apps/connapse-it");

    [Fact]
    public async Task SaveGitHubApp_ThenRead_RoundTripsAndKeepsSecretsEncryptedAtRest()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();

        try
        {
            await store.SaveGitHubAppAsync(App, "APP-PRIVATE-KEY", "CLIENT-SECRET", createdByUserId: null);

            (await store.GetGitHubAppAsync()).Should().Be(App);

            var material = await store.GetGitHubAppMaterialAsync();
            material.Should().Be(new GitHubAppCredentialMaterial(App, "APP-PRIVATE-KEY", "CLIENT-SECRET"));

            await using var db = await scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
            var row = await db.ProviderCredentials.AsNoTracking().SingleAsync(c => c.Provider == "github");
            row.PrivateKeyProtected.Should().NotContain("APP-PRIVATE-KEY");
            row.SecretProtected.Should().NotContain("CLIENT-SECRET");
            row.ConfigJson.Should().Contain("connapse-it");

            (await store.GetStatusAsync("github"))!.VerifiedAt.Should().BeNull();
        }
        finally
        {
            await store.DeleteAsync("github");
        }

        (await store.GetGitHubAppAsync()).Should().BeNull();
        (await store.GetGitHubAppMaterialAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReplaceGitHubApp_TwoSavesOfTheSameReadRace_OnlyOneWinsAndTheOtherWritesNothing(bool appStored)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        var first = App with { AppId = 5001, Slug = "first-admin" };
        var second = App with { AppId = 5002, Slug = "second-admin" };

        try
        {
            if (appStored)
                await store.SaveGitHubAppAsync(App, "STORED-KEY", "STORED-SECRET", createdByUserId: null);
            GitHubAppVersion? read = (await store.GetGitHubAppSnapshotAsync())?.Version;

            // Both admins read the same stored App, then save at once.
            ProviderCredentialInfo?[] saves = await Task.WhenAll(
                Task.Run(() => store.ReplaceGitHubAppAsync(read, first, "FIRST-KEY", "FIRST-SECRET", null)),
                Task.Run(() => store.ReplaceGitHubAppAsync(read, second, "SECOND-KEY", null, null)));

            saves.Count(s => s is not null).Should().Be(1, "the save that lost the race must not overwrite the winner");
            bool firstWon = saves[0] is not null;
            (await store.GetGitHubAppMaterialAsync()).Should().Be(firstWon
                ? new GitHubAppCredentialMaterial(first, "FIRST-KEY", "FIRST-SECRET")
                : new GitHubAppCredentialMaterial(second, "SECOND-KEY", null));

            // The loser reloads and saves again over what the winner stored.
            GitHubAppSnapshot? reloaded = await store.GetGitHubAppSnapshotAsync();
            (await store.ReplaceGitHubAppAsync(reloaded!.Version, App, "RETRY-KEY", null, null)).Should().NotBeNull();
            (await store.ReplaceGitHubAppAsync(reloaded.Version, first, "STALE-KEY", null, null)).Should().BeNull("that read is now stale");
            (await store.GetGitHubAppMaterialAsync()).Should().Be(new GitHubAppCredentialMaterial(App, "RETRY-KEY", null));
        }
        finally
        {
            await store.DeleteAsync("github");
        }
    }

    [Fact]
    public async Task GetGitHubAppSnapshot_KeyUnreadable_StillGivesAVersionARepairCanSaveOver()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();

        try
        {
            await store.SaveGitHubAppAsync(App, "STORED-KEY", null, createdByUserId: null);
            await using (var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync())
                await db.Database.ExecuteSqlRawAsync("UPDATE provider_credentials SET private_key_protected = 'not-ciphertext' WHERE provider = 'github'");

            GitHubAppSnapshot? snapshot = await store.GetGitHubAppSnapshotAsync();

            snapshot!.App.Should().Be(App);
            snapshot.Material.Should().BeNull();
            (await store.ReplaceGitHubAppAsync(snapshot.Version, App, "REPAIRED-KEY", null, null)).Should().NotBeNull();
            (await store.GetGitHubAppMaterialAsync()).Should().Be(new GitHubAppCredentialMaterial(App, "REPAIRED-KEY", null));
        }
        finally
        {
            await store.DeleteAsync("github");
        }
    }

    [Fact]
    public async Task ReplaceGitHubApp_AppRemovedSinceRead_WritesNothing()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();

        try
        {
            await store.SaveGitHubAppAsync(App, "STORED-KEY", null, createdByUserId: null);
            GitHubAppVersion read = (await store.GetGitHubAppSnapshotAsync())!.Version;
            await store.DeleteAsync("github");

            (await store.ReplaceGitHubAppAsync(read, App, "NEW-KEY", null, null)).Should().BeNull();
            (await store.GetGitHubAppSnapshotAsync()).Should().BeNull();
        }
        finally
        {
            await store.DeleteAsync("github");
        }
    }

    [Theory]
    [InlineData("github", """{"appId":1}""", null, "a GitHub App with no key")]
    [InlineData("aws", """{"appId":1}""", "cipher", "App settings on a non-GitHub row")]
    public async Task Database_RefusesAPartialOrMisplacedGitHubAppRow(string provider, string config, string? key, string because)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();

        // A unique key for the misplaced case, so it cannot collide with a real row.
        string key0 = provider == "github" ? "github" : $"{provider}-{Guid.NewGuid():N}"[..12];

        Func<Task> act = () => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO provider_credentials (provider, created_at, config_json, private_key_protected) VALUES ({0}, now(), {1}, {2})",
            key0, config, key!);

        await act.Should().ThrowAsync<Npgsql.PostgresException>(because).Where(e => e.SqlState == "23514");
    }
}
