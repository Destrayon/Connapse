using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// <see cref="ISettingsStore.UpdateAsync{T}"/> against PostgreSQL: a read-modify-write under a row
/// lock, so concurrent updaters merge rather than overwrite. The enforcement latches depend on it —
/// two administrators saving the AWS and Azure sign-in applications at once must end with both
/// flags on, never one switched back off by the other's stale copy.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SettingsStoreUpdateIntegrationTests(SharedWebAppFixture fixture)
{
    private const string Category = "integrationtest-update";

    private ISettingsStore Store(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<ISettingsStore>();

    [Fact]
    public async Task UpdateAsync_ConcurrentMergesFromSeparateScopes_LoseNothing()
    {
        await using var setup = fixture.Factory.Services.CreateAsyncScope();
        await Store(setup).ResetAsync(Category);

        try
        {
            // Each task owns a scope (its own DbContext) and switches one flag on, ORing the rest.
            var tasks = Enumerable.Range(0, 12).Select(async i =>
            {
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                await Store(scope).UpdateAsync<PermissionEnforcementSettings>(Category, stored => new PermissionEnforcementSettings
                {
                    IsEnforcing = (stored?.IsEnforcing ?? false) || i % 2 == 0,
                    AzureEnforcing = (stored?.AzureEnforcing ?? false) || i % 2 == 1,
                });
            });
            await Task.WhenAll(tasks);

            var final = await Store(setup).GetAsync<PermissionEnforcementSettings>(Category);
            final.Should().NotBeNull();
            final!.IsEnforcing.Should().BeTrue();
            final.AzureEnforcing.Should().BeTrue();
        }
        finally
        {
            await Store(setup).ResetAsync(Category);
        }
    }

    [Fact]
    public async Task UpdateAsync_WhenNothingIsStored_HandsTheUpdaterNull_AndStoresItsResult()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await Store(scope).ResetAsync(Category);

        try
        {
            PermissionEnforcementSettings? seen = new();
            var stored = await Store(scope).UpdateAsync<PermissionEnforcementSettings>(Category, current =>
            {
                seen = current;
                return new PermissionEnforcementSettings { AzureEnforcing = true };
            });

            seen.Should().BeNull();
            stored.AzureEnforcing.Should().BeTrue();
            (await Store(scope).GetAsync<PermissionEnforcementSettings>(Category))!.AzureEnforcing.Should().BeTrue();
        }
        finally
        {
            await Store(scope).ResetAsync(Category);
        }
    }

    [Fact]
    public async Task Revision_ChangesOnEveryWrite_AndIsWhatTheNextReadSees()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = Store(scope);
        await store.ResetAsync(Category);

        try
        {
            (await store.GetWithRevisionAsync<PermissionEnforcementSettings>(Category)).Should().Be(((PermissionEnforcementSettings?)null, (string?)null));

            string? handed = "unset";
            var (_, first) = await store.UpdateWithRevisionAsync<PermissionEnforcementSettings>(Category, (stored, revision) =>
            {
                handed = revision;
                return new PermissionEnforcementSettings { IsEnforcing = true };
            });
            handed.Should().BeNull("nothing was stored, though the update inserts a row to lock");
            (await store.GetWithRevisionAsync<PermissionEnforcementSettings>(Category)).Revision.Should().Be(first);

            // The same values written again still get a new revision, as a secret-only change would.
            var (_, second) = await store.UpdateWithRevisionAsync<PermissionEnforcementSettings>(Category, (stored, revision) =>
            {
                handed = revision;
                return stored!;
            });
            handed.Should().Be(first);
            second.Should().NotBe(first);
            await store.SaveAsync(Category, new PermissionEnforcementSettings { IsEnforcing = true });
            (await store.GetWithRevisionAsync<PermissionEnforcementSettings>(Category)).Revision.Should().NotBe(second);
        }
        finally
        {
            await store.ResetAsync(Category);
        }
    }
}
