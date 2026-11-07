using Connapse.Identity.Services;
using FluentAssertions;

namespace Connapse.Identity.Tests;

/// <summary>
/// A started sign-in can be taken once, however many callbacks carry its state at the same moment.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class SignInConcurrencyTests
{
    private const string State = "state-1";

    private static DateTime Expiry => DateTime.UtcNow.AddMinutes(10);

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task SamlConsume_WhenSimultaneousRequestsAllReadTheNonce_YieldsSignInToExactlyOneCaller(int requests)
    {
        using var cache = new RendezvousCache(requests);
        var signIns = new SamlSignInRequests(cache);
        Guid userId = Guid.NewGuid();
        string nonce = signIns.Start(userId, "request-1");

        StartedSignIn?[] taken = await cache.SendSimultaneously(() => signIns.Consume(nonce));

        taken.Where(result => result is not null).Should().ContainSingle()
            .Which.Should().Be(new StartedSignIn(userId, "request-1"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task AzureTakeByState_WhenSimultaneousRequestsAllReadTheState_YieldsSignInToExactlyOneCaller(int requests)
    {
        using var cache = new RendezvousCache(requests);
        var signIns = new AzureSignInRequests(cache);
        signIns.Add(new AzurePendingSignIn(State, "verifier", "nonce", Guid.NewGuid(), Expiry));

        AzurePendingSignIn?[] taken = await cache.SendSimultaneously(() => signIns.TakeByState(State));

        taken.Where(result => result is not null).Should().ContainSingle()
            .Which!.State.Should().Be(State);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task GitHubTakeSignIn_WhenSimultaneousRequestsAllReadTheState_YieldsSignInToExactlyOneCaller(int requests)
    {
        using var cache = new RendezvousCache(requests);
        var flow = new GitHubLinkFlow(cache);
        flow.AddSignIn(new GitHubPendingSignIn(State, "verifier", Guid.NewGuid(), Expiry, DateTime.UtcNow));

        GitHubPendingSignIn?[] taken = await cache.SendSimultaneously(() => flow.TakeSignIn(State));

        taken.Where(result => result is not null).Should().ContainSingle()
            .Which!.State.Should().Be(State);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task AtlassianTakeSignIn_WhenSimultaneousRequestsAllReadTheState_YieldsSignInToExactlyOneCaller(int requests)
    {
        using var cache = new RendezvousCache(requests);
        var flow = new AtlassianLinkFlow(cache);
        flow.AddSignIn(new AtlassianPendingSignIn(State, "verifier", Guid.NewGuid(), Expiry, DateTime.UtcNow, RevocationGeneration: 0));

        AtlassianPendingSignIn?[] taken = await cache.SendSimultaneously(() => flow.TakeSignIn(State));

        taken.Where(result => result is not null).Should().ContainSingle()
            .Which!.State.Should().Be(State);
    }
}
