using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>Atlassian account links and the Atlassian link app credential, against real PostgreSQL.</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AtlassianLinkStorageTests(SharedWebAppFixture fixture)
{
    private static async Task<Guid> SeedUserAsync(IServiceProvider sp)
    {
        var factory = sp.GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var user = new Connapse.Identity.Data.Entities.ConnapseUser
        {
            Id = Guid.NewGuid(),
            UserName = $"u-{Guid.NewGuid():N}@example.com",
            Email = $"u-{Guid.NewGuid():N}@example.com",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task SaveAsync_ThenGetLinkAsync_ReturnsAccountId()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = scope.ServiceProvider.GetRequiredService<AtlassianIdentityLinkStore>();

        await store.SaveAsync(userId, "5b10ac8d82e05b22cc7d4ef5", "Ada Lovelace", "ada@example.com");

        var reader = scope.ServiceProvider.GetRequiredService<IAtlassianIdentityLinkReader>();
        (await reader.GetLinkAsync(userId)).Should().Be(new AtlassianIdentityRef("5b10ac8d82e05b22cc7d4ef5", "Ada Lovelace"));
        (await store.GetAsync(userId))!.Email.Should().Be("ada@example.com");
    }

    [Fact]
    public async Task SaveAsync_Twice_ReplacesRow()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = scope.ServiceProvider.GetRequiredService<AtlassianIdentityLinkStore>();

        await store.SaveAsync(userId, "account-one", "Ada", null);
        await store.SaveAsync(userId, "account-two", "Ada Renamed", null);

        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>().CreateDbContextAsync();
        var rows = await db.UserAtlassianIdentityLinks.AsNoTracking().Where(x => x.UserId == userId).ToListAsync();
        rows.Should().ContainSingle().Which.AccountId.Should().Be("account-two");
        (await store.GetLinkAsync(userId))!.DisplayName.Should().Be("Ada Renamed");
    }

    [Fact]
    public async Task DeleteAsync_RemovesRow()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = scope.ServiceProvider.GetRequiredService<AtlassianIdentityLinkStore>();
        await store.SaveAsync(userId, "account-one", "Ada", null);

        (await store.DeleteAsync(userId)).Should().BeTrue();

        (await store.GetLinkAsync(userId)).Should().BeNull();
        (await store.DeleteAsync(userId)).Should().BeFalse();
    }

    [Fact]
    public async Task TrySaveAsync_UnlinkAfterTheGenerationWasRead_SavesNothing()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = scope.ServiceProvider.GetRequiredService<AtlassianIdentityLinkStore>();
        await store.SaveAsync(userId, "account-one", "Ada", null);
        long generation = await store.GetRevocationGenerationAsync(userId);

        // The confirmation has claimed its link and is paused; the unlink completes, then it resumes.
        (await store.DeleteAsync(userId)).Should().BeTrue();

        (await store.TrySaveAsync(userId, generation, "account-revived", "Ada", null)).Should().BeFalse();
        (await store.GetLinkAsync(userId)).Should().BeNull();
        await store.Invoking(s => s.TrySaveAsync(userId, generation, "account-revived", "Ada", null, new CancellationToken(canceled: true)))
            .Should().ThrowAsync<OperationCanceledException>();
        (await store.GetLinkAsync(userId)).Should().BeNull("a resumed confirmation that is then cancelled leaves nothing to clean up");
    }

    [Fact]
    public async Task TrySaveAsync_GenerationUnchanged_Saves_AndAnUnlinkWithNoLinkStillRefusesOlderSignIns()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = scope.ServiceProvider.GetRequiredService<AtlassianIdentityLinkStore>();
        long generation = await store.GetRevocationGenerationAsync(userId);

        (await store.TrySaveAsync(userId, generation, "account-one", "Ada", null)).Should().BeTrue();
        (await store.GetLinkAsync(userId))!.AccountId.Should().Be("account-one");

        (await store.DeleteAsync(userId)).Should().BeTrue();
        (await store.DeleteAsync(userId)).Should().BeFalse();
        (await store.TrySaveAsync(userId, generation + 1, "account-two", "Ada", null)).Should().BeFalse(
            "the second unlink found no link but still refuses a sign-in started before it");
        (await store.TrySaveAsync(userId, generation + 2, "account-two", "Ada", null)).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAtlassianLinkAppAsync_SecretIsEncryptedAtRest()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();

        try
        {
            await store.SaveAtlassianLinkAppAsync("atl-client-id", "ATL-CLIENT-SECRET", createdByUserId: null);

            (await store.GetAtlassianLinkAppAsync()).Should().Be(new AtlassianLinkAppRegistration("atl-client-id"));
            (await store.GetAtlassianLinkAppSecretAsync()).Should().Be("ATL-CLIENT-SECRET");

            await using var db = await scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
            var row = await db.ProviderCredentials.AsNoTracking().SingleAsync(c => c.Provider == "atlassian");
            row.SecretProtected.Should().NotBeNullOrEmpty().And.NotContain("ATL-CLIENT-SECRET");
            row.ConfigJson.Should().Contain("atl-client-id");
        }
        finally
        {
            await store.DeleteAsync("atlassian");
        }

        (await store.GetAtlassianLinkAppAsync()).Should().BeNull();
        (await store.GetAtlassianLinkAppSecretAsync()).Should().BeNull();
    }

    [Fact]
    public async Task MarkAtlassianLinkAppVerifiedAsync_SetsVerifiedAt_AndSavingAgainClearsIt()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();

        try
        {
            (await store.MarkAtlassianLinkAppVerifiedAsync(DateTime.UtcNow)).Should().BeFalse("nothing is stored yet");

            await store.SaveAtlassianLinkAppAsync("atl-client-id", "secret-1", createdByUserId: null);
            (await store.GetAtlassianLinkAppAsync())!.VerifiedAt.Should().BeNull();

            (await store.MarkAtlassianLinkAppVerifiedAsync(DateTime.UtcNow)).Should().BeTrue();
            (await store.GetAtlassianLinkAppAsync())!.VerifiedAt.Should().NotBeNull();

            await store.SaveAtlassianLinkAppAsync("atl-client-id", "secret-2", createdByUserId: null);
            (await store.GetAtlassianLinkAppAsync())!.VerifiedAt.Should().BeNull("new credentials are unverified");
        }
        finally
        {
            await store.DeleteAsync("atlassian");
        }
    }

    [Fact]
    public async Task SaveAtlassianLinkAppAsync_OverlappingSaves_NeverMixClientIdAndSecret()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();

        try
        {
            for (int round = 0; round < 40; round++)
            {
                await store.SaveAtlassianLinkAppAsync("client-a", "secret-for-client-a", createdByUserId: null);

                // One save swaps to another app; the other keeps the client id and rotates only the secret.
                await Task.WhenAll(
                    Task.Run(() => store.SaveAtlassianLinkAppAsync("client-b", "secret-for-client-b", null)),
                    Task.Run(() => store.SaveAtlassianLinkAppAsync("client-a", "secret-for-client-a", null)));

                var registration = await store.GetAtlassianLinkAppAsync();
                string? secret = await store.GetAtlassianLinkAppSecretAsync();

                secret.Should().Be($"secret-for-{registration!.ClientId}",
                    "the stored pair must be exactly one call's pair (round {0})", round);
            }
        }
        finally
        {
            await store.DeleteAsync("atlassian");
        }
    }
}
