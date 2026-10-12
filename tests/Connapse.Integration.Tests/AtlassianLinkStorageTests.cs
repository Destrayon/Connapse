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

        await store.SaveAsync(userId, "5b10ac8d82e05b22cc7d4ef5");

        var reader = scope.ServiceProvider.GetRequiredService<IAtlassianIdentityLinkReader>();
        (await reader.GetLinkAsync(userId)).Should().Be(new AtlassianIdentityRef("5b10ac8d82e05b22cc7d4ef5"));
    }

    [Fact]
    public async Task Table_HoldsNoNameOrEmail()
    {
        // Atlassian treats a name or email as personal data an app must report weekly (#695).
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>().CreateDbContextAsync();

        var columns = await db.Database.SqlQueryRaw<string>(
            "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'user_atlassian_identity_links'")
            .ToListAsync();

        columns.Should().BeEquivalentTo("id", "user_id", "account_id", "connected_at");
    }

    [Fact]
    public async Task SaveAsync_Twice_ReplacesRow()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = scope.ServiceProvider.GetRequiredService<AtlassianIdentityLinkStore>();

        await store.SaveAsync(userId, "account-one");
        await store.SaveAsync(userId, "account-two");

        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>().CreateDbContextAsync();
        var rows = await db.UserAtlassianIdentityLinks.AsNoTracking().Where(x => x.UserId == userId).ToListAsync();
        rows.Should().ContainSingle().Which.AccountId.Should().Be("account-two");
    }

    [Fact]
    public async Task DeleteAsync_RemovesRow()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = scope.ServiceProvider.GetRequiredService<AtlassianIdentityLinkStore>();
        await store.SaveAsync(userId, "account-one");

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
        await store.SaveAsync(userId, "account-one");
        long generation = await store.GetRevocationGenerationAsync(userId);

        // The confirmation has claimed its link and is paused; the unlink completes, then it resumes.
        (await store.DeleteAsync(userId)).Should().BeTrue();

        (await store.TrySaveAsync(userId, generation, "account-revived")).Should().BeFalse();
        (await store.GetLinkAsync(userId)).Should().BeNull();
        await store.Invoking(s => s.TrySaveAsync(userId, generation, "account-revived", new CancellationToken(canceled: true)))
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

        (await store.TrySaveAsync(userId, generation, "account-one")).Should().BeTrue();
        (await store.GetLinkAsync(userId))!.AccountId.Should().Be("account-one");

        (await store.DeleteAsync(userId)).Should().BeTrue();
        (await store.DeleteAsync(userId)).Should().BeFalse();
        (await store.TrySaveAsync(userId, generation + 1, "account-two")).Should().BeFalse(
            "the second unlink found no link but still refuses a sign-in started before it");
        (await store.TrySaveAsync(userId, generation + 2, "account-two")).Should().BeTrue();
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
    public async Task CountLinksAsync_CountsLinkedUsers()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<AtlassianIdentityLinkStore>();
        int before = await store.CountLinksAsync();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);

        await store.SaveAsync(userId, "account-counted");
        try
        {
            (await store.CountLinksAsync()).Should().Be(before + 1);
        }
        finally
        {
            await store.DeleteAsync(userId);
        }
    }

    [Fact]
    public async Task SaveAtlassianLinkAppAsync_WithACheck_RecordsItAccepted_AndRecordCheckFollowsTheStoredClientOnly()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        var checkedAt = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

        try
        {
            await store.SaveAtlassianLinkAppAsync("atl-client-id", "secret-1", createdByUserId: null, checkedAt);
            var saved = (await store.GetAtlassianLinkAppAsync())!;
            saved.CheckedAt.Should().Be(checkedAt);
            saved.CheckAccepted.Should().BeTrue();
            saved.SignInEnabled.Should().BeTrue();

            (await store.RecordAtlassianLinkAppCheckAsync("another-client", "secret-1", accepted: false, checkedAt.AddHours(1)))
                .Should().BeFalse("a check of an app that has since been replaced must not mark the new one");
            (await store.GetAtlassianLinkAppAsync())!.CheckAccepted.Should().BeTrue();

            (await store.RecordAtlassianLinkAppCheckAsync("atl-client-id", "secret-1", accepted: false, checkedAt.AddHours(2))).Should().BeTrue();
            var rejected = (await store.GetAtlassianLinkAppAsync())!;
            rejected.CheckAccepted.Should().BeFalse();
            rejected.CheckedAt.Should().Be(checkedAt.AddHours(2));
            (await store.GetAtlassianLinkAppSecretAsync()).Should().Be("secret-1");
        }
        finally
        {
            await store.DeleteAsync("atlassian");
        }
    }

    /// <summary>
    /// A check that started with secret A must not record its answer on secret B, saved for the
    /// same client ID while the check was waiting on Atlassian.
    /// </summary>
    [Fact]
    public async Task CheckStoredApp_SecretRotatedWhileChecking_LeavesTheNewSecretsResultAlone()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        var savedAt = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

        try
        {
            await store.SaveAtlassianLinkAppAsync("atl-client-id", "secret-a", createdByUserId: null, savedAt);

            // Atlassian refuses secret A, but only after an administrator has saved secret B.
            var atlassian = new RotateThenRefuse(() => store.SaveAtlassianLinkAppAsync("atl-client-id", "secret-b", null, savedAt));
            var http = new SingleClientFactory(atlassian);
            var signIn = new Connapse.Storage.Connectors.Atlassian.AtlassianUserSignIn(http, store);

            var result = await signIn.CheckStoredAppAsync("https://connapse.example/api/v1/auth/cloud/atlassian/callback");

            result.Should().Be(Connapse.Storage.Connectors.Atlassian.AtlassianLinkAppCheck.Rejected);
            atlassian.Rotated.Should().BeTrue();
            (await store.GetAtlassianLinkAppSecretAsync()).Should().Be("secret-b");
            var app = (await store.GetAtlassianLinkAppAsync())!;
            app.CheckAccepted.Should().BeTrue("secret A's refusal says nothing about secret B");
            app.CheckedAt.Should().Be(savedAt);
        }
        finally
        {
            await store.DeleteAsync("atlassian");
        }
    }

    [Fact]
    public async Task RecordAtlassianLinkAppCheckAsync_WritesOnlyForTheSecretThatWasChecked()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        var checkedAt = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

        try
        {
            await store.SaveAtlassianLinkAppAsync("atl-client-id", "secret-b", createdByUserId: null, checkedAt);

            (await store.RecordAtlassianLinkAppCheckAsync("atl-client-id", "secret-a", accepted: false, checkedAt.AddHours(1)))
                .Should().BeFalse("secret A is no longer the stored secret");
            (await store.GetAtlassianLinkAppAsync())!.CheckAccepted.Should().BeTrue();

            (await store.RecordAtlassianLinkAppCheckAsync("atl-client-id", "secret-b", accepted: false, checkedAt.AddHours(2)))
                .Should().BeTrue();
            (await store.GetAtlassianLinkAppAsync())!.CheckAccepted.Should().BeFalse();
        }
        finally
        {
            await store.DeleteAsync("atlassian");
        }
    }

    private sealed class RotateThenRefuse(Func<Task> rotate) : HttpMessageHandler
    {
        public bool Rotated { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await rotate();
            Rotated = true;
            return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"access_denied"}""", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task RememberAtlassianCallbackUrlAsync_KeepsTheCallbackForTheStoredClientOnly_AndASaveClearsIt()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        const string callback = "https://connapse.example/api/v1/auth/cloud/atlassian/callback";

        try
        {
            (await store.RememberAtlassianCallbackUrlAsync("atl-client-id", callback)).Should().BeFalse("nothing is stored yet");

            await store.SaveAtlassianLinkAppAsync("atl-client-id", "secret-1", createdByUserId: null);
            (await store.GetAtlassianLinkAppAsync())!.CallbackUrl.Should().BeNull();

            (await store.RememberAtlassianCallbackUrlAsync("another-client", callback)).Should().BeFalse();
            (await store.RememberAtlassianCallbackUrlAsync("atl-client-id", callback)).Should().BeTrue();
            (await store.RememberAtlassianCallbackUrlAsync("atl-client-id", callback)).Should().BeFalse("an unchanged URL is not written again");

            var app = (await store.GetAtlassianLinkAppAsync())!;
            app.CallbackUrl.Should().Be(callback);
            (await store.SetAtlassianSignInEnabledAsync(false)).Should().BeTrue();
            (await store.GetAtlassianLinkAppAsync())!.CallbackUrl.Should().Be(callback, "other settings keep it");

            await store.SaveAtlassianLinkAppAsync("new-client", "secret-2", createdByUserId: null);
            (await store.GetAtlassianLinkAppAsync())!.CallbackUrl.Should().BeNull("a new app has not been checked from anywhere yet");
        }
        finally
        {
            await store.DeleteAsync("atlassian");
        }
    }

    [Fact]
    public async Task CheckJob_IsRegisteredDaily_AndResolvesAndRuns()
    {
        // The recurring job is registered at startup; Hangfire resolves its type from DI when it fires.
        using (var connection = Hangfire.JobStorage.Current.GetConnection())
        {
            var job = Hangfire.Storage.StorageConnectionExtensions.GetRecurringJobs(connection)
                .SingleOrDefault(j => j.Id == Connapse.Web.Services.AtlassianLinkAppCheckJob.RecurringJobId);
            job.Should().NotBeNull();
            job!.Cron.Should().Be(Hangfire.Cron.Daily());
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        var checkJob = scope.ServiceProvider.GetRequiredService<Connapse.Web.Services.AtlassianLinkAppCheckJob>();

        try
        {
            // Stored, but no callback known yet: skipped, nothing recorded.
            await store.SaveAtlassianLinkAppAsync("atl-client-id", "secret-1", createdByUserId: null);
            await checkJob.RunAsync(CancellationToken.None);
            (await store.GetAtlassianLinkAppAsync())!.CheckedAt.Should().BeNull();
        }
        finally
        {
            await store.DeleteAsync("atlassian");
        }
    }

    [Fact]
    public async Task SetAtlassianSignInEnabledAsync_TurnsSignInOffAndOn_KeepingTheApp_AndSavingTurnsItOn()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();

        try
        {
            (await store.SetAtlassianSignInEnabledAsync(false)).Should().BeFalse("nothing is stored yet");

            await store.SaveAtlassianLinkAppAsync("atl-client-id", "secret-1", createdByUserId: null);
            (await store.SetAtlassianSignInEnabledAsync(false)).Should().BeTrue();
            var off = (await store.GetAtlassianLinkAppAsync())!;
            off.SignInEnabled.Should().BeFalse();
            off.ClientId.Should().Be("atl-client-id");
            (await store.GetAtlassianLinkAppSecretAsync()).Should().Be("secret-1");

            (await store.SetAtlassianSignInEnabledAsync(true)).Should().BeTrue();
            (await store.GetAtlassianLinkAppAsync())!.SignInEnabled.Should().BeTrue();

            await store.SetAtlassianSignInEnabledAsync(false);
            await store.SaveAtlassianLinkAppAsync("atl-client-id", "secret-2", createdByUserId: null);
            (await store.GetAtlassianLinkAppAsync())!.SignInEnabled.Should().BeTrue("saving a new app turns sign-in back on");
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
