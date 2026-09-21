using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Identity.Tests;

/// <summary>
/// Disconnecting an Entra identity must also end every flow the person started before it.
/// Otherwise a second tab that already reached the callback — its confirmation parked, or its
/// sign-in still pending — could finish afterwards and put the link back.
/// </summary>
[Trait("Category", "Unit")]
public class AzureLinkRevocationTests
{
    private static readonly Guid User = Guid.NewGuid();

    [Fact]
    public void Confirmation_ParkedBeforeDisconnect_IsRefusedAfterIt()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var confirmations = new AzureLinkConfirmations(cache);
        string code = confirmations.Start(new PendingAzureLink(User, "oid-1", "tid-1", "Ada"));

        confirmations.RevokeFor(User);

        confirmations.Consume(code).Should().BeNull("the disconnect came after the flow started");
    }

    [Fact]
    public void Confirmation_ParkedAfterDisconnect_StillWorks()
    {
        // A fresh link after a disconnect is the ordinary re-connect; only older flows are ended.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var confirmations = new AzureLinkConfirmations(cache);

        confirmations.RevokeFor(User);
        Thread.Sleep(5);
        string code = confirmations.Start(new PendingAzureLink(User, "oid-1", "tid-1", "Ada"));

        confirmations.Consume(code).Should().NotBeNull();
    }

    [Fact]
    public void Disconnect_OfOneUser_LeavesAnotherUsersFlowAlone()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var confirmations = new AzureLinkConfirmations(cache);
        var other = Guid.NewGuid();
        string code = confirmations.Start(new PendingAzureLink(other, "oid-2", "tid-1", "Bob"));

        confirmations.RevokeFor(User);

        confirmations.Consume(code).Should().NotBeNull();
    }

    [Fact]
    public void SignIn_StartedBeforeDisconnect_IsRefusedAfterIt()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var signIns = new AzureSignInRequests(cache);
        DateTime started = DateTime.UtcNow.AddSeconds(-1);
        signIns.Add(new AzurePendingSignIn("state-1", "verifier", "nonce", User, DateTime.UtcNow.AddMinutes(10), started));

        signIns.RevokeFor(User);

        signIns.TakeByState("state-1").Should().BeNull();
    }

    [Fact]
    public void SignIn_StartedAfterDisconnect_StillWorks()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var signIns = new AzureSignInRequests(cache);

        signIns.RevokeFor(User);
        Thread.Sleep(5);
        signIns.Add(new AzurePendingSignIn("state-2", "verifier", "nonce", User, DateTime.UtcNow.AddMinutes(10), DateTime.UtcNow));

        signIns.TakeByState("state-2").Should().NotBeNull();
    }

    [Fact]
    public void BothStores_ShareTheRevocation_ThroughTheCache()
    {
        // The web app hands the service both stores on one cache; a disconnect recorded through
        // either must be seen by the other, which is what lets DisconnectAsync revoke once each.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var signIns = new AzureSignInRequests(cache);
        var confirmations = new AzureLinkConfirmations(cache);
        string code = confirmations.Start(new PendingAzureLink(User, "oid-1", "tid-1", "Ada"));

        signIns.RevokeFor(User);

        confirmations.Consume(code).Should().BeNull();
    }
}
