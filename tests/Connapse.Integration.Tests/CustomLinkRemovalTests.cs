using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Storage.Data;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The outbox of unlink calls owed to custom providers when a user is deleted: queued per provider that
/// offers linking, drained until the middle man acknowledges, and reported once when it never does.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class CustomLinkRemovalTests(SharedWebAppFixture fixture) : IAsyncLifetime
{
    private FakeMiddleMan Remote => fixture.CustomSource;

    public Task InitializeAsync()
    {
        Remote.SupportsLinking = true;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Remote.SupportsLinking = false;
        Remote.Links.Clear();
        return Task.CompletedTask;
    }

    private async Task<CustomProvider> ProviderAsync(string? linkUrl = FakeMiddleMan.LinkUrl, string token = FakeMiddleMan.Token)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ICustomProviderStore>().CreateAsync(new SaveCustomProviderRequest(
            $"unlink-{Guid.NewGuid():N}"[..20], FakeMiddleMan.BaseUrl, token, 100, true, linkUrl));
    }

    private async Task<List<Storage.Data.Entities.CustomLinkRemovalEntity>> RowsAsync(Guid userId)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        await using KnowledgeDbContext context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        return await context.CustomLinkRemovals.AsNoTracking().Where(r => r.UserId == userId).ToListAsync();
    }

    private async Task RunJobAsync(IAuditLogger? audit = null)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        CustomLinkRemovalJob job = audit is null
            ? services.GetRequiredService<CustomLinkRemovalJob>()
            : new CustomLinkRemovalJob(services.GetRequiredService<ICustomLinkRemovalStore>(),
                services.GetRequiredService<CustomLinkService>(),
                services.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Connapse.Identity.Data.Entities.ConnapseUser>>(),
                audit, TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CustomLinkRemovalJob>.Instance);
        await job.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Enqueue_QueuesEveryProviderOfferingLinkingOnce_AndCancelDropsThem()
    {
        CustomProvider linking = await ProviderAsync();
        CustomProvider notLinking = await ProviderAsync(linkUrl: null);
        Guid user = Guid.NewGuid();
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ICustomLinkRemovalStore store = scope.ServiceProvider.GetRequiredService<ICustomLinkRemovalStore>();

        await store.EnqueueForUserAsync(user);
        (await store.EnqueueForUserAsync(user)).Should().Be(0, "a second deletion attempt queues nothing new");

        List<Guid> providers = (await RowsAsync(user)).Select(r => r.ProviderId).ToList();
        providers.Should().Contain(linking.Id).And.NotContain(notLinking.Id).And.OnlyHaveUniqueItems();

        (await RowsAsync(user)).Should().OnlyContain(r => r.NextAttemptAt == null, "nothing is sent before the deletion succeeds");
        await store.CancelForUserAsync(user);
        (await RowsAsync(user)).Should().BeEmpty();
    }

    private async Task QueueDeletedAsync(Guid user)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ICustomLinkRemovalStore store = scope.ServiceProvider.GetRequiredService<ICustomLinkRemovalStore>();
        await store.EnqueueForUserAsync(user);
        await store.ActivateForUserAsync(user);
    }

    [Fact]
    public async Task Job_EntryWaitingForItsDeletion_IsNotSent()
    {
        await ProviderAsync();
        Guid user = Guid.NewGuid();
        Remote.Links[user.ToString()] = "still-here@acme";
        using (IServiceScope scope = fixture.Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ICustomLinkRemovalStore>().EnqueueForUserAsync(user);

        await RunJobAsync();

        Remote.Unlinked.Should().NotContain(user.ToString());
        (await RowsAsync(user)).Should().NotBeEmpty().And.OnlyContain(r => r.NextAttemptAt == null);
    }

    [Fact]
    public async Task Job_EntryLeftWaiting_IsSentWhenTheUserIsGone_AndKeptForADayWhileTheUserRemains()
    {
        await ProviderAsync();
        Guid gone = Guid.NewGuid();
        Guid present = Guid.Parse(await AdminIdAsync());
        using (IServiceScope scope = fixture.Factory.Services.CreateScope())
        {
            ICustomLinkRemovalStore store = scope.ServiceProvider.GetRequiredService<ICustomLinkRemovalStore>();
            await store.EnqueueForUserAsync(gone);
            await store.EnqueueForUserAsync(present);
        }
        await BackdateAsync(CustomLinkRemovalJob.SettleWaitingAfter, gone, present);

        await RunJobAsync();

        Remote.Unlinked.Should().Contain(gone.ToString()).And.NotContain(present.ToString());
        (await RowsAsync(present)).Should().NotBeEmpty("a slow deletion may still succeed and activate them");

        await BackdateAsync(CustomLinkRemovalJob.DropWaitingAfter, present);
        await RunJobAsync();

        (await RowsAsync(present)).Should().BeEmpty("a day on, the deletion failed and nothing is owed");
        Remote.Unlinked.Should().NotContain(present.ToString());
    }

    [Fact]
    public async Task Enqueue_ProviderThatStoppedOfferingLinking_IsStillOwedAnUnlink()
    {
        CustomProvider provider = await ProviderAsync();
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ICustomProviderStore providers = scope.ServiceProvider.GetRequiredService<ICustomProviderStore>();
        await providers.GenerateLinkSecretAsync(provider.Id);
        await providers.UpdateAsync(provider.Id, new SaveCustomProviderRequest(
            provider.Name, provider.BaseUrl, null, 100, true, LinkUrl: null));
        Guid user = Guid.NewGuid();

        await scope.ServiceProvider.GetRequiredService<ICustomLinkRemovalStore>().EnqueueForUserAsync(user);

        (await RowsAsync(user)).Should().Contain(r => r.ProviderId == provider.Id);
    }

    [Fact]
    public async Task Job_ProviderDeletedOrMovedAfterQueueing_StillSendsToTheMiddleManItWasQueuedFor()
    {
        CustomProvider provider = await ProviderAsync();
        Guid user = Guid.NewGuid();
        await QueueDeletedAsync(user);
        using (IServiceScope scope = fixture.Factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<ICustomProviderStore>().DeleteAsync(provider.Id)).Should().BeTrue();

        (await RowsAsync(user)).Should().Contain(r => r.ProviderId == provider.Id && r.BaseUrl == FakeMiddleMan.BaseUrl);
        await RunJobAsync();

        Remote.Unlinked.Should().Contain(user.ToString());
        (await RowsAsync(user)).Should().NotContain(r => r.ProviderId == provider.Id);
    }

    private async Task<string> AdminIdAsync()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Connapse.Identity.Data.Entities.ConnapseUser>>();
        return (await users.FindByEmailAsync(SharedWebAppFixture.AdminEmail))!.Id.ToString();
    }

    private async Task BackdateAsync(TimeSpan by, params Guid[] userIds)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        await using KnowledgeDbContext context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        DateTime past = DateTime.UtcNow - by - TimeSpan.FromMinutes(1);
        await context.CustomLinkRemovals.Where(r => userIds.Contains(r.UserId))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedAt, past));
    }

    [Fact]
    public async Task Job_MiddleManAcknowledges_RemovesTheLinkAndTheEntry()
    {
        CustomProvider provider = await ProviderAsync();
        Guid user = Guid.NewGuid();
        Remote.Links[user.ToString()] = "gone@acme";
        await QueueDeletedAsync(user);
        var audit = Substitute.For<IAuditLogger>();

        await RunJobAsync(audit);

        await audit.Received().LogAsync(Connapse.Web.Endpoints.CustomLinkEndpoints.UnlinkedEvent, "user", user.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());

        Remote.Links.Should().NotContainKey(user.ToString());
        Remote.Unlinked.Should().Contain(user.ToString());
        (await RowsAsync(user)).Should().NotContain(r => r.ProviderId == provider.Id);
    }

    [Fact]
    public async Task Job_MiddleManRefusing_KeepsRetryingAndReportsOnceAfterADay()
    {
        CustomProvider refusing = await ProviderAsync(token: "not-the-token");
        Guid user = Guid.NewGuid();
        await QueueDeletedAsync(user);
        var audit = Substitute.For<IAuditLogger>();

        await RunJobAsync(audit);

        var row = (await RowsAsync(user)).Single(r => r.ProviderId == refusing.Id);
        row.Attempts.Should().BeGreaterThanOrEqualTo(1);
        row.NextAttemptAt.Should().BeAfter(DateTime.UtcNow);
        row.LastError.Should().NotBeNullOrEmpty();
        row.AlertedAt.Should().BeNull();
        await audit.DidNotReceive().LogAsync(CustomLinkRemovalJob.StuckEvent, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());

        // A day later and due again: reported once, and still queued.
        await AgeAsync(row.Id);
        await RunJobAsync(audit);
        await AgeAsync(row.Id);
        await RunJobAsync(audit);

        await audit.Received(1).LogAsync(CustomLinkRemovalJob.StuckEvent, "custom_provider", refusing.Id.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
        (await RowsAsync(user)).Single(r => r.ProviderId == refusing.Id).AlertedAt.Should().NotBeNull();
    }

    private async Task AgeAsync(Guid id)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        await using KnowledgeDbContext context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        DateTime past = DateTime.UtcNow - CustomLinkRemovalJob.AlertAfter - TimeSpan.FromMinutes(1);
        await context.CustomLinkRemovals.Where(r => r.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.CreatedAt, past)
            .SetProperty(r => r.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
    }

    [Fact]
    public void WaitAfter_DoublesUpToAnHour()
    {
        CustomLinkRemovalJob.WaitAfter(1).Should().Be(TimeSpan.FromMinutes(2));
        CustomLinkRemovalJob.WaitAfter(3).Should().Be(TimeSpan.FromMinutes(8));
        CustomLinkRemovalJob.WaitAfter(50).Should().Be(TimeSpan.FromHours(1));
    }
}
