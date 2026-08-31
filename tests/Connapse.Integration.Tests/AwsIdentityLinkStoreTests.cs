using Connapse.Identity.Data;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Storing which IAM Identity Center user a Connapse user signed in as, against real PostgreSQL.
/// </summary>
/// <remarks>
/// The encryption round-trip these tests used to cover is gone with the stored refresh token. What
/// still needs a real database is the concurrency behaviour: the unique index on <c>user_id</c> and
/// the upsert that races against it cannot be exercised on the InMemory provider.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AwsIdentityLinkStoreTests(SharedWebAppFixture fixture)
{
    private static AwsIdentityLinkStore Build(IServiceProvider sp) =>
        sp.GetRequiredService<AwsIdentityLinkStore>();

    private async Task<Guid> SeedUserAsync(IServiceProvider sp)
    {
        // A real user row, because the link table has a cascading foreign key to it. Create the
        // user the way the neighbouring identity integration tests do.
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
    public async Task SaveAsync_ThenGetAsync_RoundTripsTheDirectoryIdentity()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = Build(scope.ServiceProvider);

        await store.SaveAsync(userId, "a1b2c3d4-5678-90ab-cdef-EXAMPLE11111", "person", "person@example.com");

        var link = await store.GetAsync(userId);
        link.Should().NotBeNull();
        link!.DirectoryUserId.Should().Be("a1b2c3d4-5678-90ab-cdef-EXAMPLE11111");
        link.DirectoryUserName.Should().Be("person");
        link.Email.Should().Be("person@example.com");
    }

    [Fact]
    public async Task SaveAsync_CalledTwice_ReplacesRatherThanAdds()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = Build(scope.ServiceProvider);

        await store.SaveAsync(userId, "id-one", "person", "person@example.com");
        await store.SaveAsync(userId, "id-two", "person-renamed", "person@example.com");

        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        (await db.UserAwsIdentityLinks.CountAsync(x => x.UserId == userId)).Should().Be(1);
        (await store.GetAsync(userId))!.DirectoryUserId.Should().Be("id-two");
    }

    [Fact]
    public async Task SaveAsync_ManyConcurrentCallsForOneUser_LeaveExactlyOneRow_AndNoneThrow()
    {
        // Every call reads "no row for this user" before deciding whether to insert or update, so
        // without an atomic upsert, two calls landing close enough together both take the insert
        // path and the unique index on user_id rejects whichever commits second with an unhandled
        // exception instead of one link simply winning. A shared gate releases every call at (as
        // close to) the same instant, and enough callers race at once, so that overlap is all but
        // guaranteed rather than left to incidental timing between two calls.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = Build(scope.ServiceProvider);

        const int concurrency = 16;
        using var gate = new ManualResetEventSlim(initialState: false);

        var tasks = Enumerable.Range(0, concurrency)
            .Select(i => Task.Run(async () =>
            {
                gate.Wait();
                await store.SaveAsync(userId, $"id-{i}", $"user{i}", $"user{i}@example.com");
            }))
            .ToArray();

        gate.Set();
        await Task.WhenAll(tasks); // Throws if any SaveAsync call threw.

        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        (await db.UserAwsIdentityLinks.CountAsync(x => x.UserId == userId)).Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_ForAnUnconnectedUser_IsNull()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);

        (await Build(scope.ServiceProvider).GetAsync(userId)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheRow_AndIsSafeToRepeat()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = Build(scope.ServiceProvider);
        await store.SaveAsync(userId, "a-directory-id", "person", "person@example.com");

        (await store.DeleteAsync(userId)).Should().BeTrue();
        (await store.DeleteAsync(userId)).Should().BeFalse("nothing was left to delete");
        (await store.GetAsync(userId)).Should().BeNull();
    }
}
