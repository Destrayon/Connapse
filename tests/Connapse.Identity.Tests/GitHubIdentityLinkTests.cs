using Connapse.Identity.Data;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Connapse.Identity.Tests;

/// <summary>Linking a Connapse user to a GitHub account: the store, and the one-time state of a sign-in.</summary>
[Trait("Category", "Unit")]
public class GitHubIdentityLinkTests
{
    private static GitHubIdentityLinkStore Store(string db)
    {
        var factory = Substitute.For<IDbContextFactory<ConnapseIdentityDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new ConnapseIdentityDbContext(
            new DbContextOptionsBuilder<ConnapseIdentityDbContext>().UseInMemoryDatabase(db).Options)));
        return new GitHubIdentityLinkStore(factory, TimeProvider.System);
    }

    private static GitHubLinkFlow Flow() => new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task Store_SaveReplaceReadDelete_RoundTrips()
    {
        var store = Store(Guid.NewGuid().ToString());
        var user = Guid.NewGuid();

        await store.SaveAsync(user, 583231, "octocat");
        await store.SaveAsync(user, 583231, "octocat-renamed");

        (await store.GetLinkAsync(user)).Should().Be(new Core.Interfaces.GitHubIdentityRef(583231, "octocat-renamed"),
            "linking again replaces the one row rather than adding a second");
        (await store.DeleteAsync(user)).Should().BeTrue();
        (await store.GetLinkAsync(user)).Should().BeNull();
        (await store.DeleteAsync(user)).Should().BeFalse();
    }

    [Fact]
    public void SignIn_IsTakenOnce()
    {
        var flow = Flow();
        flow.AddSignIn(new GitHubPendingSignIn("s1", "verifier", Guid.NewGuid(), DateTime.UtcNow.AddMinutes(10), DateTime.UtcNow));

        flow.TakeSignIn("s1").Should().NotBeNull();
        flow.TakeSignIn("s1").Should().BeNull("a state is honoured once");
        flow.TakeSignIn("never-issued").Should().BeNull();
    }

    [Fact]
    public async Task ParkedLink_IsClaimedByExactlyOneOfManyConcurrentConfirms()
    {
        var flow = Flow();
        string code = flow.Park(new PendingGitHubLink(Guid.NewGuid(), 1, "octocat", DateTime.UtcNow));

        var claims = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => flow.Claim(code))));

        claims.Count(c => c is not null).Should().Be(1);
    }

    [Fact]
    public void Unlinking_RefusesSignInsAndParkedLinksStartedBeforeIt()
    {
        var flow = Flow();
        var user = Guid.NewGuid();
        flow.AddSignIn(new GitHubPendingSignIn("s1", "verifier", user, DateTime.UtcNow.AddMinutes(10), DateTime.UtcNow.AddSeconds(-1)));
        string code = flow.Park(new PendingGitHubLink(user, 1, "octocat", DateTime.UtcNow.AddSeconds(-1)));

        flow.RevokeFor(user);

        flow.TakeSignIn("s1").Should().BeNull("a sign-in begun before the unlink must not re-create the link");
        flow.Claim(code).Should().BeNull();
    }

    [Fact]
    public void ParkedAfterAnUnlink_ButSignedInBeforeIt_IsStillRefused()
    {
        var flow = Flow();
        var user = Guid.NewGuid();
        DateTime signInStarted = DateTime.UtcNow.AddSeconds(-5);

        flow.RevokeFor(user); // the unlink lands while the callback is still talking to GitHub
        string code = flow.Park(new PendingGitHubLink(user, 1, "octocat", signInStarted));

        flow.Claim(code).Should().BeNull("the sign-in began before the unlink, whenever it was parked");
    }
}
