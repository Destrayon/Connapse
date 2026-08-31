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
    public void Link_HoldsNoCredential()
    {
        // The point of the whole design, pinned so it cannot drift back. IAM Identity Center
        // attests an identity once and Connapse records who it named; permissions are read later
        // with Connapse's own IAM identity. A property here that holds a token — protected or not —
        // means someone reintroduced a per-user credential that expires and is worth stealing.
        var properties = typeof(UserAwsIdentityLinkEntity).GetProperties().Select(p => p.Name);

        properties.Should().NotContain(
            name => name.Contains("Token", StringComparison.OrdinalIgnoreCase),
            "the link records an attested identity, never a credential");
    }

    [Fact]
    public void NewLink_DefaultsToEmptyRatherThanNull()
    {
        var link = new UserAwsIdentityLinkEntity();

        link.DirectoryUserId.Should().BeEmpty();
        link.DirectoryUserName.Should().BeEmpty();
        link.Email.Should().BeEmpty();
    }
}
