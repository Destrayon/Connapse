using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SourceStoreIntegrationTests(SharedWebAppFixture fixture)
{
    private async Task<Guid> NewConnectionAsync(IConnectionStore connections)
    {
        var created = await connections.CreateAsync(
            new CreateConnectionRequest($"c-{Guid.NewGuid():N}"[..24], ConnectionProvider.S3, """{"region":"us-east-1"}"""),
            createdByUserId: null);
        return created.Id;
    }

    private async Task<Source> NewSourceAsync(ISourceStore sources, IConnectionStore connections)
    {
        Guid connectionId = await NewConnectionAsync(connections);
        return await sources.CreateAsync(new CreateSourceRequest(
            Name: $"s-{Guid.NewGuid():N}"[..24],
            ConnectionId: connectionId,
            ScopeJson: """{"prefix":"docs/"}"""));
    }

    [Fact]
    public async Task CreateAsync_NewSource_StartsNeverSyncedAndEnabled()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var source = await NewSourceAsync(sources, connections);

        source.LastSyncStatus.Should().Be(SyncStatus.Never);
        source.SyncCursor.Should().BeNull();
        source.LastSyncedAt.Should().BeNull();
        source.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateSyncStateAsync_AfterSuccessfulSync_PersistsCursor()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var source = await NewSourceAsync(sources, connections);
        var syncedAt = DateTime.UtcNow;

        await sources.UpdateSyncStateAsync(source.Id, "cursor-abc", SyncStatus.Succeeded, error: null, syncedAt);

        var reloaded = await sources.GetAsync(source.Id);
        reloaded!.SyncCursor.Should().Be("cursor-abc");
        reloaded.LastSyncStatus.Should().Be(SyncStatus.Succeeded);
        reloaded.LastSyncError.Should().BeNull();
        reloaded.LastSyncedAt.Should().BeCloseTo(syncedAt, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UpdateSyncStateAsync_WithNullCursor_ClearsItForFullResync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var source = await NewSourceAsync(sources, connections);
        await sources.UpdateSyncStateAsync(source.Id, "cursor-abc", SyncStatus.Succeeded, null, DateTime.UtcNow);

        await sources.UpdateSyncStateAsync(source.Id, null, SyncStatus.Failed, "410 Gone", DateTime.UtcNow);

        var reloaded = await sources.GetAsync(source.Id);
        reloaded!.SyncCursor.Should().BeNull();
        reloaded.LastSyncStatus.Should().Be(SyncStatus.Failed);
        reloaded.LastSyncError.Should().Be("410 Gone");
    }

    [Fact]
    public async Task TryAdvanceSyncStateAsync_FirstAdvanceFromNullCursor_Succeeds()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var source = await NewSourceAsync(sources, connections);

        bool advanced = await sources.TryAdvanceSyncStateAsync(
            source.Id, expectedCursor: null, newCursor: "cursor-1", SyncStatus.Succeeded, null, DateTime.UtcNow);

        advanced.Should().BeTrue();
        (await sources.GetAsync(source.Id))!.SyncCursor.Should().Be("cursor-1");
    }

    [Fact]
    public async Task TryAdvanceSyncStateAsync_ReverseCompletionOrder_CannotRegressTheCursor()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var source = await NewSourceAsync(sources, connections);

        // Both syncs read the same starting cursor (null), then B finishes first.
        await sources.TryAdvanceSyncStateAsync(
            source.Id, expectedCursor: null, newCursor: "cursor-B", SyncStatus.Succeeded, null, DateTime.UtcNow);

        // A now completes late, still believing the cursor is null. It must not win.
        bool aWon = await sources.TryAdvanceSyncStateAsync(
            source.Id, expectedCursor: null, newCursor: "cursor-A", SyncStatus.Succeeded, null, DateTime.UtcNow);

        aWon.Should().BeFalse();
        (await sources.GetAsync(source.Id))!.SyncCursor.Should().Be("cursor-B");
    }

    [Fact]
    public async Task TryAdvanceSyncStateAsync_ChainedAdvances_EachMatchesPriorCursor()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var source = await NewSourceAsync(sources, connections);

        await sources.TryAdvanceSyncStateAsync(source.Id, null, "c1", SyncStatus.Succeeded, null, DateTime.UtcNow);
        bool second = await sources.TryAdvanceSyncStateAsync(source.Id, "c1", "c2", SyncStatus.Succeeded, null, DateTime.UtcNow);

        second.Should().BeTrue();
        (await sources.GetAsync(source.Id))!.SyncCursor.Should().Be("c2");
    }

    [Fact]
    public async Task UpdateSyncStateAsync_StillResetsUnconditionally_ForFullResync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var source = await NewSourceAsync(sources, connections);
        await sources.TryAdvanceSyncStateAsync(source.Id, null, "stale-token", SyncStatus.Succeeded, null, DateTime.UtcNow);

        // A RequiresFullResync response must be able to clear the cursor regardless of its
        // current value — the compare-and-swap path deliberately does not gate this.
        await sources.UpdateSyncStateAsync(source.Id, null, SyncStatus.Failed, "410 Gone", DateTime.UtcNow);

        var reloaded = await sources.GetAsync(source.Id);
        reloaded!.SyncCursor.Should().BeNull();
        reloaded.LastSyncError.Should().Be("410 Gone");
    }

    [Fact]
    public async Task CreateAsync_UnknownConnection_Throws()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();

        Func<Task> act = async () => await sources.CreateAsync(new CreateSourceRequest(
            Name: $"s-{Guid.NewGuid():N}"[..24],
            ConnectionId: Guid.NewGuid(),
            ScopeJson: "{}"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ListByConnectionAsync_ReturnsOnlyThatConnectionsSources()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        Guid connectionId = await NewConnectionAsync(connections);
        await sources.CreateAsync(new CreateSourceRequest($"a-{Guid.NewGuid():N}"[..24], connectionId, "{}"));
        await sources.CreateAsync(new CreateSourceRequest($"b-{Guid.NewGuid():N}"[..24], connectionId, "{}"));
        await NewSourceAsync(sources, connections); // belongs to a different connection

        var results = await sources.ListByConnectionAsync(connectionId);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.ConnectionId == connectionId);
    }

    [Fact]
    public async Task UpdateAsync_SetEnabledFalse_Persists()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var source = await NewSourceAsync(sources, connections);

        await sources.UpdateAsync(source.Id, new UpdateSourceRequest(Enabled: false));

        var reloaded = await sources.GetAsync(source.Id);
        reloaded!.Enabled.Should().BeFalse();
    }
}
