using Connapse.Identity.Data;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Storing a per-user refresh token so that only this deployment can read it back.
/// </summary>
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
    public async Task SaveAsync_ThenGetRefreshTokenAsync_RoundTripsThePlaintext()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = Build(scope.ServiceProvider);

        await store.SaveAsync(userId, "person@example.com", "the-refresh-token");

        (await store.GetRefreshTokenAsync(userId)).Should().Be("the-refresh-token");
    }

    [Fact]
    public async Task SaveAsync_DoesNotStoreThePlaintext()
    {
        // The point of the whole class. Asserting the round trip alone would pass just as happily
        // against an implementation that wrote the token straight to the column.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = Build(scope.ServiceProvider);

        await store.SaveAsync(userId, "person@example.com", "the-refresh-token");

        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.UserAwsIdentityLinks.SingleAsync(x => x.UserId == userId);

        row.ProtectedRefreshToken.Should().NotBe("the-refresh-token");
        row.ProtectedRefreshToken.Should().NotContain("the-refresh-token");
        row.Email.Should().Be("person@example.com");
    }

    [Fact]
    public async Task SaveAsync_CalledTwice_ReplacesRatherThanAdds()
    {
        // Connecting again must not leave two rows, or something later has to decide which token
        // is live and there is no correct way to choose.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = Build(scope.ServiceProvider);

        await store.SaveAsync(userId, "first@example.com", "first-token");
        await store.SaveAsync(userId, "second@example.com", "second-token");

        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        (await db.UserAwsIdentityLinks.CountAsync(x => x.UserId == userId)).Should().Be(1);
        (await store.GetRefreshTokenAsync(userId)).Should().Be("second-token");
        (await store.GetAsync(userId))!.Email.Should().Be("second@example.com");
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
                await store.SaveAsync(userId, $"user{i}@example.com", $"token-{i}");
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
    public async Task GetRefreshTokenAsync_ForAnUnconnectedUser_IsNull()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);

        (await Build(scope.ServiceProvider).GetRefreshTokenAsync(userId)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheRow_AndIsSafeToRepeat()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = Build(scope.ServiceProvider);
        await store.SaveAsync(userId, "person@example.com", "the-refresh-token");

        (await store.DeleteAsync(userId)).Should().BeTrue();
        (await store.DeleteAsync(userId)).Should().BeFalse("nothing was left to delete");
        (await store.GetRefreshTokenAsync(userId)).Should().BeNull();
    }
}
