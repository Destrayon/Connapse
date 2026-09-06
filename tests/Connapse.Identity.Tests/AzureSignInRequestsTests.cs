using Connapse.Identity.Services;
using FluentAssertions;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class AzureSignInRequestsTests
{
    [Fact]
    public void Pending_TakeByState_RemovesAndDropsExpired()
    {
        var store = new AzureSignInRequests();
        store.Add(new AzurePendingSignIn("s1", "v", "n", Guid.NewGuid(), DateTime.UtcNow.AddMinutes(5)));
        store.TakeByState("s1").Should().NotBeNull();
        store.TakeByState("s1").Should().BeNull(); // removed
        store.Add(new AzurePendingSignIn("s2", "v", "n", Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-1)));
        store.TakeByState("s2").Should().BeNull(); // expired
    }
}
