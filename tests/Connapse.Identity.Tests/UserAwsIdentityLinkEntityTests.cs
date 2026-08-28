using Connapse.Identity.Data.Entities;
using FluentAssertions;
using Xunit;

namespace Connapse.Identity.Tests;

/// <summary>
/// The shape of a stored AWS identity link.
/// </summary>
[Trait("Category", "Unit")]
public class UserAwsIdentityLinkEntityTests
{
    [Fact]
    public void ProtectedRefreshToken_IsNamedForWhatItHolds()
    {
        // The name is the guard rail. A property called RefreshToken invites someone to assign a
        // plaintext one; "Protected" says the value has already been through Data Protection and
        // that assigning a raw token here is the bug.
        typeof(UserAwsIdentityLinkEntity).GetProperty("ProtectedRefreshToken")
            .Should().NotBeNull();
        typeof(UserAwsIdentityLinkEntity).GetProperty("RefreshToken")
            .Should().BeNull("a plaintext token must have nowhere to go");
    }

    [Fact]
    public void NewLink_DefaultsToEmptyRatherThanNull()
    {
        var link = new UserAwsIdentityLinkEntity();

        link.Email.Should().BeEmpty();
        link.ProtectedRefreshToken.Should().BeEmpty();
    }
}
